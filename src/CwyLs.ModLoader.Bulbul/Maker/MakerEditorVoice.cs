using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Loaders;
using CwyLs.ModLoader.Core.Voice;
using CwyLs.ModLoader.Loaders;
using CwyLs.ModLoader.Packs;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>The dub half of the editor: <see cref="SaveVoiceAsync"/> and the live swap.</summary>
    public sealed partial class MakerEditor
    {
        /// <summary>How long a worker waits for the engine to decode a pack file it has to swap in.</summary>
        private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

        /// <inheritdoc />
        public async Task<MakerResult<MakerSaved>> SaveVoiceAsync(
            string packId, string lang, string clip, byte[]? wav, CancellationToken cancel)
        {
            try
            {
                if (!MakerEditRules.TryLanguage(lang, out var code, out var problem))
                {
                    return Fail(MakerErrorCodes.BadRequest, problem!);
                }

                if (!MakerEditRules.IsClip(clip))
                {
                    return Fail(MakerErrorCodes.BadRequest, "'" + clip + "' is not a voice clip name (letters, digits, _ and - only)");
                }

                // Keyed by clip, so the catalog's line lookup is not on this path: a paid DLC line's clip is not editable unless
                // the DLC is known to be owned (no snapshot = refused, fail closed).
                var paid = await MakerCatalog.Instance.DlcRefusalAsync(null, clip, cancel).ConfigureAwait(false);
                if (paid != null)
                {
                    return Fail(paid.Error!, paid.Message ?? "paid DLC content is not editable");
                }

                MakerWavReport? check = null;
                if (wav != null)
                {
                    check = MakerWav.Inspect(wav);
                    if (!check.Ok)
                    {
                        return Fail(check.Error!, check.Message);
                    }
                }

                if (!TryOpen(packId, out var pack, out var files, out var refusal))
                {
                    return refusal!;
                }

                // One runtime voice writer at a time, shared with Studio publish/unpublish (M5d).
                await Core.Api.Maker.Studio.MakerStudioEditor.VoiceGate.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                await WriteGate.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    cancel.ThrowIfCancellationRequested();

                    MakerVoiceWrite write;
                    WavData? samples = null;
                    if (wav != null)
                    {
                        var canonical = MakerWav.Canonical(wav, check!);

                        // Decoded here, off the main thread; the main thread only builds the clip.
                        samples = WavParser.Parse(canonical);
                        write = MakerVoiceStore.Save(files!, code, clip, canonical);
                    }
                    else
                    {
                        write = MakerVoiceStore.Remove(files!, code, clip);
                    }

                    var opened = pack!;
                    // A Studio publication of this clip no longer matches the file: drop its disclosure and record.
                    MakerStudioEditor.Instance.ForgetV1Voice(opened.Id, code, clip);
                    var effective = EffectiveVoice(code, clip);
                    var saved = await LiveVoice(code, clip, opened, write, effective, samples, check, CancellationToken.None).ConfigureAwait(false);
                    var written = wav != null || write.Deleted || write.MovedAside.Count > 0 || write.Restored.Count > 0;
                    Report(MakerEditorChange.VoiceKind, opened.Id, code, clip, wav is null, saved, written);
                    MakerBackends.Publish("maker.voice.saved", new Dictionary<string, object?>
                    {
                        ["packId"] = opened.Id,
                        ["lang"] = code,
                        ["clip"] = clip,
                        ["durationSec"] = saved.DurationSec,
                        ["removed"] = wav is null,
                    });
                    return MakerResult<MakerSaved>.Success(saved);
                }
                finally
                {
                    WriteGate.Release();
                }
                }
                finally
                {
                    Core.Api.Maker.Studio.MakerStudioEditor.VoiceGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return Fail(MakerErrorCodes.Unavailable, "the request was cancelled before anything was written");
            }
            catch (Exception ex)
            {
                return Failed(wav is null ? "removing a dub" : "saving a dub", ex);
            }
        }

        /// <summary>
        /// The live voice apply after a Studio publish or unpublish commit (M5d): the same swap, restore or hide as a v1
        /// save or removal, the same note, report and event. The caller holds <c>MakerStudioEditor.VoiceGate</c>.
        /// </summary>
        internal static async Task<Core.Api.Maker.Studio.MakerVoiceLiveResult> ApplyCommittedVoice(
            string packId, string code, string clip, MakerVoiceWrite write, byte[]? canonical, CancellationToken cancel)
        {
            var pack = PackManager.Find(packId);
            if (pack is null)
            {
                return new Core.Api.Maker.Studio.MakerVoiceLiveResult
                {
                    Saved = new MakerSaved { Path = write.Path, Note = "Saved to " + packId + ", which is not loaded this session; the game uses it after a restart." },
                };
            }

            MakerWavReport? check = null;
            WavData? samples = null;
            if (canonical != null)
            {
                check = MakerWav.Inspect(canonical);
                samples = WavParser.Parse(canonical);
            }

            var effective = EffectiveVoice(code, clip);
            var saved = await LiveVoice(code, clip, pack, write, effective, samples, check, cancel).ConfigureAwait(false);
            Report(MakerEditorChange.VoiceKind, pack.Id, code, clip, canonical is null, saved, true);
            MakerBackends.Publish("maker.voice.saved", new Dictionary<string, object?>
            {
                ["packId"] = pack.Id,
                ["lang"] = code,
                ["clip"] = clip,
                ["durationSec"] = saved.DurationSec,
                ["removed"] = canonical is null,
            });
            return new Core.Api.Maker.Studio.MakerVoiceLiveResult { Saved = saved, EffectivePackId = effective.HasValue ? effective.PackId : null };
        }

        /// <summary>The live update after a dub save or removal, and the answer.</summary>
        private static async Task<MakerSaved> LiveVoice(
            string code,
            string clip,
            LoadedPack pack,
            MakerVoiceWrite write,
            MakerLayer<string> effective,
            WavData? samples,
            MakerWavReport? check,
            CancellationToken cancel)
        {
            var removed = samples is null;
            var saved = new MakerSaved
            {
                Path = write.Path,
                DurationSec = check is null ? (double?)null : Math.Round(check.DurationSec, 3),
            };

            var moved = Describe(write);
            if (removed && !write.Existed && write.Restored.Count == 0)
            {
                saved.Live = true;
                saved.Note = "Nothing to remove: " + pack.Id + " has no " + write.Path + "." + moved;
                return saved;
            }

            if (!VoiceFeature.Enabled || !LanguageVoices.Enabled)
            {
                saved.Note = (removed ? "Removed " : "Saved ") + write.Path + ", but language voices are off ("
                             + (VoiceFeature.Enabled ? LanguageVoices.DisabledReason : "Voice.Enabled=false")
                             + "), so the game does not play it." + moved;
                return saved;
            }

            var ours = effective.HasValue
                       && string.Equals(effective.PackId, pack.Id, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(effective.Value, write.Path, StringComparison.OrdinalIgnoreCase);

            try
            {
                if (!removed && ours)
                {
                    await OnMain(
                        "maker.voice.swap",
                        () =>
                        {
                            var made = AudioClip.Create(clip, samples!.Frames, samples.Channels, samples.SampleRate, false);
                            if (made == null || !made.SetData(samples.Samples, 0))
                            {
                                throw new InvalidOperationException("the engine refused the samples");
                            }

                            LanguageVoices.SwapLive(code, clip, made, pack.Id, write.Path, createdByMaker: true);
                            return true;
                        },
                        cancel).ConfigureAwait(false);

                    saved.Live = true;
                    saved.Note = "Saved; the game plays this take the next time the line is spoken in " + code + "." + moved;
                    return saved;
                }

                var outcome = await Reconcile(code, clip, effective, cancel).ConfigureAwait(false);
                if (!removed)
                {
                    saved.Note = "Saved " + write.Path + ", but " + effective.PackId + " loads later and has its own "
                                 + code + " dub of this line, so the game plays that one." + moved;
                    return saved;
                }

                saved.Live = true;
                saved.Note = "Removed; " + outcome + "." + moved;
                return saved;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is InvalidOperationException)
            {
                CwyLsModLoader.Log.LogWarning(LogPrefix + " " + code + "/" + clip + " was written but not swapped in: " + ex.Message);
                saved.Note = (removed ? "Removed " : "Saved ") + write.Path + ", but the game did not take it live ("
                             + ex.Message + "); it is used from the next restart." + moved;
                return saved;
            }
        }

        /// <summary>
        /// Leaves the language set answering what the packs on disk now give the name: the plan's own
        /// clip when it is that file and loaded or loadable, the file decoded and swapped in otherwise,
        /// or the game's clip when no pack has one.
        /// </summary>
        /// <returns>What now plays, for the note.</returns>
        private static async Task<string> Reconcile(string code, string clip, MakerLayer<string> effective, CancellationToken cancel)
        {
            if (!effective.HasValue)
            {
                await OnMain(
                    "maker.voice.hide",
                    () =>
                    {
                        var table = LanguageVoices.PeekTable(code, clip);
                        if (table.Outcome == LanguageVoiceOutcome.NoSet || table.Outcome == LanguageVoiceOutcome.NotInSet)
                        {
                            LanguageVoices.DropLive(code, clip);
                        }
                        else
                        {
                            LanguageVoices.HideLive(code, clip);
                        }

                        return true;
                    },
                    cancel).ConfigureAwait(false);
                return "the game's own voice plays for this line";
            }

            var path = effective.Value!;
            var usesPlan = await OnMain(
                "maker.voice.plan",
                () =>
                {
                    var table = LanguageVoices.PeekTable(code, clip);
                    var same = table.Step != null
                               && string.Equals(table.Step.PackId, effective.PackId, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(table.Step.RelativePath, path, StringComparison.OrdinalIgnoreCase)
                               && table.Outcome != LanguageVoiceOutcome.Failed;
                    if (same)
                    {
                        LanguageVoices.DropLive(code, clip);
                    }

                    return same;
                },
                cancel).ConfigureAwait(false);

            var what = effective.PackId + "/" + path + " plays for this line";
            if (usesPlan)
            {
                return what;
            }

            var pack = PackManager.Find(effective.PackId);
            var loaded = pack is null ? null : await LoadFile(pack.FullPath(path), cancel).ConfigureAwait(false);
            await OnMain(
                "maker.voice.restore",
                () =>
                {
                    if (loaded != null)
                    {
                        LanguageVoices.SwapLive(code, clip, loaded, effective.PackId, path, createdByMaker: false);
                    }
                    else
                    {
                        LanguageVoices.HideLive(code, clip);
                    }

                    return true;
                },
                cancel).ConfigureAwait(false);

            return loaded != null ? what : "the game's own voice plays for this line until the next restart (" + path + " did not load)";
        }

        /// <summary>
        /// The voice file the loaded packs together give a name in a language: each pack's own last file
        /// by path (as the plan picks), later pack over earlier.
        /// </summary>
        private static MakerLayer<string> EffectiveVoice(string code, string clip)
        {
            var layers = new List<MakerLayer<string>>();
            foreach (var pack in PackManager.Loaded)
            {
                string? path = null;
                try
                {
                    path = MakerLayers.LastPath(new MakerPackFiles(pack.RootPath).VoiceFiles(code, clip));
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    CwyLsModLoader.Log.LogDebug(LogPrefix + " could not list " + pack.Id + "'s " + code + " voices: " + ex.Message);
                }

                layers.Add(new MakerLayer<string>(pack.Id, path));
            }

            return MakerLayers.Effective(layers);
        }

        /// <summary>Decodes a pack voice file through the engine (Ogg or WAV), waiting up to <see cref="LoadTimeout"/>.</summary>
        /// <returns>The clip, or null when it did not load.</returns>
        private static async Task<AudioClip?> LoadFile(string absolutePath, CancellationToken cancel)
        {
            var done = new TaskCompletionSource<AudioClip?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await OnMain(
                "maker.voice.load",
                () =>
                {
                    AudioLoader.LoadAsync(absolutePath, loaded => done.TrySetResult(loaded));
                    return true;
                },
                cancel).ConfigureAwait(false);

            var finished = await Task.WhenAny(done.Task, Task.Delay(LoadTimeout, cancel)).ConfigureAwait(false);
            return finished == done.Task ? done.Task.Result : null;
        }

        /// <summary>The creator files moved, for the note: " Moved aside: …" or empty.</summary>
        private static string Describe(MakerVoiceWrite write)
        {
            var parts = new List<string>();
            foreach (var moved in write.MovedAside)
            {
                parts.Add(moved.Key + " to " + moved.Value);
            }

            var text = parts.Count == 0 ? string.Empty : " Moved aside: " + string.Join(", ", parts) + ".";
            if (write.Restored.Count > 0)
            {
                text += " Restored: " + string.Join(", ", write.Restored) + ".";
            }

            return text;
        }
    }
}
