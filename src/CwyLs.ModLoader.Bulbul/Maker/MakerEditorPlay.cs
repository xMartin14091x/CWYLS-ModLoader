using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Bulbul.Satone.Motion;
using CwyLs.ModLoader.Bulbul.Subtitles;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Subtitles;
using CwyLs.ModLoader.Packs;
using CwyLs.ModLoader.Loaders;
using CwyLs.ModLoader.Threading;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// The Play half of the editor: Satone says one line in the game, through the game's own scenario
    /// reader and story text box (<c>Satone.Say</c>), never an overlay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line is spoken under its real voice name, and for the length of that one line the language
    /// voice lookup is pinned (<see cref="LanguageVoices.Pin"/>): <c>original</c> pins "no language
    /// clip", so the game's own clip plays whatever language is selected; Studio <c>sub</c> uses
    /// that same original voice pin. <c>dub</c> pins the pack's
    /// clip for <c>lang</c> (the live swap, the set's loaded clip, or the file decoded now), so the take
    /// can be heard in any language. Because the clip comes through the game's own lookup, the voice
    /// channel, its slider, the mouth animation and the subtitle hold all treat it as a game line.
    /// </para>
    /// <para>
    /// Text: <c>original</c> shows the game's own row (Japanese in Japanese, English otherwise);
    /// <c>dub</c> and Studio <c>sub</c> show that language's subtitle in every column, so the creator sees the line as a
    /// player of that language would, falling back to the game's text when it has none yet.
    /// </para>
    /// </remarks>
    public sealed partial class MakerEditor
    {
        /// <summary>The <c>original</c> variant.</summary>
        public const string OriginalVariant = "original";

        /// <summary>The <c>dub</c> variant.</summary>
        public const string DubVariant = "dub";

        private const string SubtitleVariant = "sub";

        /// <summary>Extra seconds a Play pin outlives its clip (the queue tick, the typewriter, the hold).</summary>
        private const double PinMarginSeconds = 30d;

        /// <inheritdoc />
        public async Task<MakerResult<MakerSaved>> PlayAsync(string id, string variant, string lang, CancellationToken cancel)
        {
            // v1 Play reads the game's table directly, not the catalog: a paid DLC line the player is not known to own is not
            // played from Pack Maker (Studio Play is checked by the Studio editor's line scope).
            if (MakerEditRules.IsLineId(id))
            {
                var paid = await MakerCatalog.Instance.DlcRefusalAsync(id, null, cancel).ConfigureAwait(false);
                if (paid != null)
                {
                    return MakerResult<MakerSaved>.Fail(paid.Error!, paid.Message ?? "paid DLC content is not playable here", paid.Details);
                }
            }

            var result = await PlayCoreAsync(id, variant, lang, null, cancel).ConfigureAwait(false);
            return result.Ok
                ? MakerResult<MakerSaved>.Success(result.Value!.Saved)
                : MakerResult<MakerSaved>.Fail(result.Error!, result.Message!, result.Details);
        }

        /// <summary>
        /// Studio v2 playback: reuses the v1 play path; <c>sub</c> previews the selected pack subtitle with original audio.
        /// <c>dub</c> previews the selected pack's own take when
        /// <see cref="MakerStudioPlayRequest.PackId"/> is set, and reports the winning loaded pack in
        /// <see cref="MakerVoiceLiveResult.EffectivePackId"/>.
        /// </summary>
        internal Task<MakerResult<MakerVoiceLiveResult>> PlayStudioAsync(MakerStudioPlayRequest request, CancellationToken cancel) =>
            PlayCoreAsync(request.Id, request.Variant, request.Lang, request.PackId, cancel, studio: true);

        private async Task<MakerResult<MakerVoiceLiveResult>> PlayCoreAsync(
            string id, string variant, string lang, string? packId, CancellationToken cancel, bool studio = false)
        {
            var ownership = new MakerPreviewOwnership<AudioClip>(DispatchPreviewCleanup, clip => UnityEngine.Object.Destroy(clip));
            try
            {
                var dub = string.Equals(variant, DubVariant, StringComparison.Ordinal);
                var sub = studio && string.Equals(variant, SubtitleVariant, StringComparison.Ordinal);
                if (!dub && !sub && !string.Equals(variant, OriginalVariant, StringComparison.Ordinal))
                {
                    return FailLive(MakerErrorCodes.BadRequest, "variant must be " + (studio ? "'original', 'dub' or 'sub'" : "'original' or 'dub'") + ", not '" + variant + "'");
                }

                if (sub && string.IsNullOrEmpty(packId))
                {
                    return FailLive(MakerErrorCodes.BadRequest, "variant 'sub' requires packId");
                }

                if (!MakerEditRules.IsLineId(id))
                {
                    return FailLive(MakerErrorCodes.BadRequest, "'" + id + "' is not a line id");
                }

                var code = string.Empty;
                string? problem = null;
                if ((dub || sub || !string.IsNullOrEmpty(lang)) && !MakerEditRules.TryLanguage(lang, out code, out problem))
                {
                    return FailLive(MakerErrorCodes.BadRequest, problem!);
                }

                // 1. On the main thread: is she free, and what is the line?
                var plan = await OnMain("maker.play.look", () => Look(id, dub, code), cancel).ConfigureAwait(false);
                if (plan.Error != null)
                {
                    return FailLive(plan.Error, plan.Message);
                }

                var effective = !string.IsNullOrEmpty(code) ? EffectiveVoice(code, plan.Voice) : default;
                var effectivePackId = effective.HasValue ? effective.PackId : null;

                // 2. Resolve the dub clip: either the selected pack's own take (Studio preview) or the winning pack's take.
                if (dub)
                {
                    if (!string.IsNullOrEmpty(packId))
                    {
                        var selected = PackManager.Find(packId!);
                        if (selected is null)
                        {
                            return FailLive(MakerErrorCodes.NotFound, "no pack '" + packId + "' is loaded");
                        }

                        var ownPath = MakerLayers.LastPath(new MakerPackFiles(selected.RootPath).VoiceFiles(code, plan.Voice));
                        if (ownPath is null)
                        {
                            return FailLive(MakerErrorCodes.NotFound, selected.Id + " has no " + code + " dub for " + plan.Voice + " yet");
                        }

                        var selectedWins = effective.HasValue
                            && string.Equals(effective.PackId, selected.Id, StringComparison.OrdinalIgnoreCase);
                        if (!selectedWins || plan.Hidden || plan.Clip == null)
                        {
                            plan.Clip = await LoadPreviewFile(selected.FullPath(ownPath), ownership, cancel).ConfigureAwait(false);
                        }

                        plan.Hidden = false;
                        plan.DubPack = selected.Id;
                        if (plan.Clip == null)
                        {
                            return FailLive(MakerErrorCodes.Internal, selected.Id + "/" + ownPath + " did not load; see the log");
                        }
                    }
                    else if (plan.Clip == null)
                    {
                        var pack = effective.HasValue ? PackManager.Find(effective.PackId) : null;
                        if (plan.Hidden || pack is null)
                        {
                            return FailLive(MakerErrorCodes.NotFound, "no pack has a " + code + " dub for " + plan.Voice + " yet");
                        }

                        plan.Clip = await LoadPreviewFile(pack.FullPath(effective.Value!), ownership, cancel).ConfigureAwait(false);
                        plan.DubPack = effective.PackId;
                        if (plan.Clip == null)
                        {
                            return FailLive(MakerErrorCodes.Internal, effective.PackId + "/" + effective.Value + " did not load; see the log");
                        }
                    }
                    else if (plan.DubPack == "live" && effective.HasValue)
                    {
                        plan.DubPack = effective.PackId;
                    }
                }

                // 3. The text, then the line itself, back on the main thread.
                string? subtitle = null;
                if (dub || sub)
                {
                    if (!string.IsNullOrEmpty(packId) && PackManager.Find(packId!) is LoadedPack subPack)
                    {
                        try
                        {
                            subtitle = new MakerProjectStore(subPack.RootPath, recoveryWritable: false).CurrentSubtitle(code, id);
                        }
                        catch
                        {
                            subtitle = null;
                        }
                    }

                    if (subtitle is null)
                    {
                        MasterDataSubtitleSource.TryGetCurrent(id, code, out subtitle);
                    }
                }

                var request = BuildRequest(plan, dub || sub, code, subtitle);
                if (!request.Validate(out problem))
                {
                    return FailLive(MakerErrorCodes.BadRequest, "Satone cannot say this line: " + problem);
                }

                var started = await OnMain("maker.play.start", () => Start(plan, request, dub, code, ownership, cancel), cancel).ConfigureAwait(false);
                if (started.Error != null)
                {
                    return FailLive(started.Error, started.Message);
                }

                MakerBackends.Publish("maker.play", new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["variant"] = variant,
                    ["lang"] = code,
                    ["voice"] = plan.Voice,
                });
                ConsoleEvents.Emit(
                    TraceSys,
                    "play",
                    ConsoleTags.Say,
                    LogPrefix + " play " + variant + (dub ? " " + code : string.Empty) + " " + id,
                    "voice,seconds",
                    ("id", id),
                    ("variant", variant),
                    ("lang", code),
                    ("voice", plan.Voice),
                    ("pack", plan.DubPack),
                    ("seconds", plan.Seconds),
                    ("subtitle", subtitle != null));

                string note;
                if (sub)
                {
                    note = "Satone is saying the line with the original voice"
                        + (subtitle is null ? " (no subtitle yet: the box shows the game's text)." : " and the pack's subtitle.");
                }
                else if (!dub)
                {
                    note = "Satone is saying the game's own line.";
                }
                else if (!string.IsNullOrEmpty(packId)
                    && effectivePackId != null
                    && !string.Equals(effectivePackId, plan.DubPack, StringComparison.OrdinalIgnoreCase))
                {
                    note = "Satone is previewing " + plan.DubPack + "'s " + code + " dub in the game; "
                        + effectivePackId + " loads later and wins in normal play.";
                }
                else
                {
                    note = "Satone is saying the " + code + " dub in the game"
                        + (subtitle is null ? " (no " + code + " subtitle yet: the box shows the game's text)." : ".");
                }

                MakerGameFocus.Bring();
                return MakerResult<MakerVoiceLiveResult>.Success(new MakerVoiceLiveResult
                {
                    Saved = new MakerSaved
                    {
                        Live = true,
                        Path = null,
                        DurationSec = plan.Seconds > 0 ? Math.Round(plan.Seconds, 3) : (double?)null,
                        Note = note,
                    },
                    EffectivePackId = effectivePackId,
                });
            }
            catch (OperationCanceledException)
            {
                return FailLive(MakerErrorCodes.Unavailable, "the request was cancelled");
            }
            catch (Exception ex)
            {
                var failed = Failed("playing a line", ex);
                return FailLive(failed.Error!, failed.Message!);
            }
            finally
            {
                ownership.EndRequest();
            }
        }

        // Own in the decoder callback, before any cancellable await can lose its result. Late results
        // after the load timeout are also reclaimed by the same request owner.
        private static async Task<AudioClip?> LoadPreviewFile(string path, MakerPreviewOwnership<AudioClip> ownership, CancellationToken cancel)
        {
            var done = new TaskCompletionSource<AudioClip?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await OnMain("maker.play.load", () =>
            {
                AudioLoader.LoadAsync(path, clip =>
                {
                    if (clip != null) ownership.Decoded(clip);
                    done.TrySetResult(clip);
                });
                return true;
            }, cancel).ConfigureAwait(false);
            var finished = await Task.WhenAny(done.Task, Task.Delay(LoadTimeout, cancel)).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            return finished == done.Task ? await done.Task.ConfigureAwait(false) : null;
        }

        private static void DispatchPreviewCleanup(Action cleanup)
        {
            // No request token or invoke timeout: a stalled frame loop must not discard destruction.
            if (MainThread.RunOrPost("maker.play.cleanup", cleanup, Owner)) return;
            // A full queue can still accept a ticker. Dispose even if the first tick races registration.
            IDisposable? retry = null;
            var ran = 0;
            retry = MainThread.AddTicker("maker.play.cleanup", () =>
            {
                if (Interlocked.Exchange(ref ran, 1) == 0) cleanup();
                retry?.Dispose();
            }, Owner);
            if (Volatile.Read(ref ran) != 0) retry.Dispose();
        }

        private static MakerResult<MakerVoiceLiveResult> FailLive(string code, string message) =>
            MakerResult<MakerVoiceLiveResult>.Fail(code, message);

        /// <summary>Main thread: the readiness checks, the game's row, and a dub clip already in memory.</summary>
        private static PlayPlan Look(string id, bool dub, string code)
        {
            var plan = new PlayPlan();
            var busy = NotReadyReason();

            // The request itself first (404/400), then the game's state (409): a wrong id stays wrong while Satone
            // talks (found by DS-07b against the real game). Without the line table the game is simply not ready.
            var rows = MasterDataFeature.ResolveLoader()?.LocalizationList;
            if (rows is null)
            {
                return plan.Refuse(MakerErrorCodes.NotReady, busy ?? "the game's lines are not loaded yet");
            }

            if (!rows.TryGetValue(id, out var row) || row is null)
            {
                return plan.Refuse(MakerErrorCodes.NotFound, "the game has no line '" + id + "'");
            }

            if (string.IsNullOrEmpty(row.Voice))
            {
                return plan.Refuse(MakerErrorCodes.BadRequest, "line '" + id + "' has no voice clip");
            }

            if (busy != null)
            {
                return plan.Refuse(MakerErrorCodes.NotReady, busy);
            }

            if (dub && (!VoiceFeature.Enabled || !LanguageVoices.Enabled))
            {
                return plan.Refuse(MakerErrorCodes.NotReady, "language voices are off, so a dub cannot play (" + LanguageVoices.DisabledReason + ")");
            }

            plan.Id = id;
            plan.Voice = row.Voice;
            plan.Ja = row.Ja ?? string.Empty;
            plan.En = row.En ?? string.Empty;

            if (!dub)
            {
                var game = VoiceSeam.TryGetGameClip(VoiceSeam.TryGetInstance(out _), plan.Voice) as AudioClip;
                plan.Seconds = game != null ? game.length : 0d;
                return plan;
            }

            if (LanguageVoices.TryLive(code, plan.Voice, out var live))
            {
                plan.Hidden = live == null;
                plan.Clip = live;
                plan.DubPack = "live";
            }
            else
            {
                var table = LanguageVoices.PeekTable(code, plan.Voice);
                plan.Clip = table.HasClip ? table.Clip as AudioClip : null;
                plan.DubPack = table.Step?.PackId;
            }

            return plan;
        }

        /// <summary>Main thread: checks again, pins the clip and queues the line through <c>Satone.Say</c>.</summary>
        private static PlayPlan Start(PlayPlan plan, SayRequest request, bool dub, string code, MakerPreviewOwnership<AudioClip> ownership, CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested)
            {
                return plan.Refuse(MakerErrorCodes.Unavailable, "the request was cancelled");
            }

            var busy = NotReadyReason();
            if (busy != null)
            {
                return plan.Refuse(MakerErrorCodes.NotReady, busy);
            }

            if (dub)
            {
                if (plan.Clip == null)
                {
                    return plan.Refuse(MakerErrorCodes.NotFound, "the " + code + " dub for " + plan.Voice + " is not loaded any more");
                }

                plan.Seconds = plan.Clip.length;
            }

            var accepted = ownership.TryStart(() =>
            {
                // Recheck after acquiring ownership: cancellation may have won the dispatch race.
                if (cancel.IsCancellationRequested) return false;
                var token = LanguageVoices.Pin(plan.Voice, dub ? plan.Clip : null, plan.DubPack ?? string.Empty, code, Math.Max(plan.Seconds, 1d) + PinMarginSeconds);
                try
                {
                    var handle = SatoneSayFeature.Say(request);
                    if (handle.IsCompleted && handle.Outcome == SayOutcome.Rejected)
                    {
                        LanguageVoices.Unpin(token);
                        plan.Refuse(MakerErrorCodes.NotReady, "Satone.Say refused the line: " + handle.Detail);
                        return false;
                    }

                    // The game reports the runtime row (cwy_say_*); line.spoken names this line instead (TD-34).
                    MakerCatalog.Instance.Played.Note(handle.Line.LocalizationId, plan.Id);
                    handle.Completion.ContinueWith(_ =>
                    {
                        LanguageVoices.Unpin(token);
                        ownership.PlaybackCompleted();
                    }, TaskScheduler.Default);
                    return true;
                }
                catch
                {
                    LanguageVoices.Unpin(token);
                    throw;
                }
            });
            if (!accepted && plan.Error is null) plan.Refuse(MakerErrorCodes.Unavailable, "the request was cancelled");
            return plan;
        }

        /// <summary>The <c>Satone.Say</c> request for the line: its real voice name, and the text to show.</summary>
        private static SayRequest BuildRequest(PlayPlan plan, bool packSubtitle, string code, string? subtitle)
        {
            if (packSubtitle && !string.IsNullOrEmpty(subtitle))
            {
                return new SayRequest
                {
                    Text = subtitle!,
                    TextJa = subtitle,
                    Texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [code] = subtitle! },
                    VoiceName = plan.Voice,
                    Language = code,
                    MoveMouth = true,
                };
            }

            return new SayRequest
            {
                Text = plan.En.Length > 0 ? plan.En : plan.Ja,
                TextJa = plan.Ja.Length > 0 ? plan.Ja : null,
                VoiceName = plan.Voice,
                Language = SubtitleLanguages.English,
                MoveMouth = true,
            };
        }

        /// <summary>Why Satone cannot take a line now, or null. Main thread only.</summary>
        private static string? NotReadyReason()
        {
            if (!SatoneSayFeature.Initialized || !SatoneSayFeature.Enabled)
            {
                return "Satone.Say is off (Say.Enabled=false), so Play cannot use the game's text box";
            }

            if (!GameScopes.IsRoomLoaded || !EventHost.RoomReadyRaised)
            {
                return "the room is not ready yet";
            }

            if (SatoneSayFeature.IsSpeaking || SatoneSayFeature.WaitingCount > 0)
            {
                return "Satone is already speaking";
            }

            if (!GameScopes.TryRoom<Game.ScenarioReader>(out var reader) || reader is null
                || !SatoneSeam.TryHeroine(out var heroine) || heroine is null)
            {
                return "the room is not ready yet";
            }

            if (reader.IsPlayingScenario() || !heroine.IsFinishedVoice)
            {
                return "Satone is already speaking";
            }

            if (!heroine.IsPossibleTalk())
            {
                return "Satone cannot be interrupted right now";
            }

            return null;
        }

        /// <summary>Main-thread playback fact for the Studio activity cache. Null means readiness is not known yet.</summary>
        internal static bool? PlaybackBusy(out string? reason)
        {
            if (!GameScopes.IsRoomLoaded || !EventHost.RoomReadyRaised)
            {
                reason = "room_not_ready";
                return null;
            }

            if (SatoneSayFeature.IsSpeaking || SatoneSayFeature.WaitingCount > 0)
            {
                reason = "satone_speaking";
                return true;
            }

            if (!GameScopes.TryRoom<Game.ScenarioReader>(out var reader) || reader is null
                || !SatoneSeam.TryHeroine(out var heroine) || heroine is null)
            {
                reason = "room_not_ready";
                return null;
            }

            if (reader.IsPlayingScenario() || !heroine.IsFinishedVoice)
            {
                reason = "satone_speaking";
                return true;
            }

            if (!heroine.IsPossibleTalk())
            {
                reason = "satone_unavailable";
                return true;
            }

            reason = null;
            return false;
        }

        /// <summary>What Play found and decided, carried between its main-thread steps.</summary>
        private sealed class PlayPlan
        {
            internal string? Error { get; private set; }

            internal string Message { get; private set; } = string.Empty;

            internal string Id { get; set; } = string.Empty;

            internal string Voice { get; set; } = string.Empty;

            internal string Ja { get; set; } = string.Empty;

            internal string En { get; set; } = string.Empty;

            internal AudioClip? Clip { get; set; }

            internal bool Hidden { get; set; }

            internal string? DubPack { get; set; }

            internal double Seconds { get; set; }

            internal PlayPlan Refuse(string error, string message)
            {
                Error = error;
                Message = message;
                return this;
            }
        }
    }
}
