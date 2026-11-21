using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker.Editor;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    public sealed class MakerSubtitleLiveResult
    {
        public MakerSaved Saved { get; set; } = new MakerSaved();
        public string? EffectivePackId { get; set; }
    }

    /// <summary>The live voice apply after a publish or unpublish commit: the v1-style answer and the pack whose dub wins.</summary>
    public sealed class MakerVoiceLiveResult
    {
        public MakerSaved Saved { get; set; } = new MakerSaved();
        public string? EffectivePackId { get; set; }
    }

    /// <summary>Production Studio orchestration. Host-specific pack access and live application are its only seams.</summary>
    public sealed class MakerStudioEditor : IMakerStudioWorkflow, IMakerStudioSubtitles, IMakerStudioDubs, IMakerStudioProjects, IMakerStudioPlayback, IMakerStudioRecovery,
        IMakerStudioVoiceDefaults, IMakerStudioVoiceLabels
    {
        private static readonly object SubtitleGate = new object();
        private readonly Func<string, bool, MakerResult<MakerProjectStore>> _open;
        private readonly Func<string, string, string, bool, MakerSubtitleWrite, MakerSubtitleLiveResult> _apply;
        private readonly Action<string, IReadOnlyDictionary<string, object?>> _publish;
        private readonly Action? _beforeReceiptWrite;
        private readonly Func<string, string, CancellationToken, Task<MakerResult<string?>>>? _lineVoice;
        private readonly Func<string, string, string, MakerVoiceWrite, byte[]?, CancellationToken, Task<MakerVoiceLiveResult>>? _applyVoice;
        private readonly Func<MakerGameActivitySnapshot>? _activity;
        private readonly Func<MakerStudioPlayRequest, CancellationToken, Task<MakerResult<MakerVoiceLiveResult>>>? _play;
        private readonly Func<string, string, CancellationToken, Task<MakerResult<bool>>>? _lineScope;

        /// <summary>
        /// Serialises every runtime voice write of the process: Studio publish/unpublish and the v1 voice route
        /// (the host takes it around its own save). Acquired before any pack gate, never on the Unity main thread.
        /// </summary>
        public static readonly SemaphoreSlim VoiceGate = new SemaphoreSlim(1, 1);

        public MakerStudioEditor(Func<string, bool, MakerResult<MakerProjectStore>> open,
            Func<string, string, string, bool, MakerSubtitleWrite, MakerSubtitleLiveResult> apply,
            Action<string, IReadOnlyDictionary<string, object?>>? publish = null, Action? beforeReceiptWrite = null,
            Func<string, string, CancellationToken, Task<MakerResult<string?>>>? lineVoice = null,
            Func<string, string, string, MakerVoiceWrite, byte[]?, CancellationToken, Task<MakerVoiceLiveResult>>? applyVoice = null,
            Func<MakerGameActivitySnapshot>? activity = null,
            Func<MakerStudioPlayRequest, CancellationToken, Task<MakerResult<MakerVoiceLiveResult>>>? play = null,
            Func<string, string, CancellationToken, Task<MakerResult<bool>>>? lineScope = null)
        {
            _open = open; _apply = apply; _publish = publish ?? MakerBackends.Publish; _beforeReceiptWrite = beforeReceiptWrite;
            _lineVoice = lineVoice; _applyVoice = applyVoice; _activity = activity; _play = play;
            _lineScope = lineScope;
        }

        public Task<MakerResult<MakerStudioWorkflowSaved>> UpdateWorkflowAsync(MakerStudioWorkflowRequest request, CancellationToken cancel) =>
            Run(request.PackId, true, cancel, store =>
            {
                request.Lang = Language(request.Lang);
                MakerStudioPaths.Line(request.Lang, request.Id);
                var result = new MakerWorkflowStore(store).Update(request);
                if (result.Ok && result.Value!.Revision != request.ExpectedRevision)
                    _publish("maker.workflow.changed", new Dictionary<string, object?>
                    {
                        ["packId"] = request.PackId, ["lang"] = request.Lang, ["id"] = request.Id,
                        ["revision"] = result.Value.Revision, ["status"] = result.Value.Workflow.Status, ["starred"] = result.Value.Workflow.Starred,
                    });
                return result;
            }, validate: () =>
            {
                MakerStudioPaths.Line(Language(request.Lang), request.Id); MakerStudioPaths.RequireOperation(request.OperationId);
                if ((request.Status is null && !request.Starred.HasValue) || (request.Status != null
                    && request.Status != "todo" && request.Status != "draft" && request.Status != "done" && request.Status != "needs-retake"))
                    throw new ArgumentException("invalid workflow patch");
            }, lineLang: request.Lang, lineId: request.Id);

        public Task<MakerResult<MakerStudioHistoryPage>> HistoryAsync(string packId, string lang, string id, int offset, int limit, CancellationToken cancel) =>
            Run(packId, false, cancel, store => new MakerHistoryStore(store).Read(Language(lang), id, offset, limit),
                validate: () => { MakerStudioPaths.Line(Language(lang), id); if (offset < 0 || limit < 1 || limit > 20) throw new ArgumentException("invalid history page"); },
                lineLang: lang, lineId: id);

        public Task<MakerResult<MakerStudioSaved>> SaveSubtitleAsync(MakerStudioSubtitleRequest request, CancellationToken cancel) =>
            SaveSubtitle(request, null, cancel);
        public Task<MakerResult<MakerStudioSaved>> RevertSubtitleAsync(MakerStudioRevertRequest request, CancellationToken cancel) =>
            SaveSubtitle(null, request, cancel);

        public async Task<MakerResult<MakerStudioTakePage>> TakesAsync(string packId, string lang, string id, CancellationToken cancel)
        {
            // The clip revision publish needs: best effort, so takes stay listable when the catalog cannot name the clip.
            string? clip = null;
            if (_lineVoice != null && MakerEditRules.TryLanguage(lang, out var code, out _))
            {
                try
                {
                    var voice = await _lineVoice(code, id, cancel).ConfigureAwait(false);
                    clip = voice.Ok && MakerEditRules.IsClip(voice.Value) ? voice.Value : null;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { clip = null; }
            }

            return await RunTakeAsync(packId, lang, id, false, cancel, (store, canonical, _) => new MakerTakeStore(store).ReadTakes(canonical, id, clip),
                voice: false).ConfigureAwait(false);
        }

        /// <summary>Creator bytes are readable on any pack except a reference pack, without write access (REVIEW-FIXES-M5c item 4).</summary>
        public Task<MakerResult<MakerStudioAudio>> ReadAudioAsync(string packId, string lang, string id, string takeId, string kind, CancellationToken cancel) =>
            MakerEditRules.IsReferencePack(packId)
                ? Task.FromResult(MakerResult<MakerStudioAudio>.Fail(MakerErrorCodes.ReadOnlyPack,
                    "reference packs ship with the loader and never serve take audio"))
                : RunTakeAsync(packId, lang, id, false, cancel, (store, code, _) => new MakerTakeStore(store).ReadAudio(code, id, takeId, kind),
                    validate: () =>
                    {
                        MakerStudioPaths.RequireTakeId(takeId);
                        if (!MakerStudioPaths.IsTakeAudioKind(kind)) throw new ArgumentException("invalid audio kind");
                    }, voice: false);

        public Task<MakerResult<MakerStudioTakeSaved>> CreateTakeAsync(MakerStudioCreateTakeRequest request, CancellationToken cancel) =>
            RunTakeAsync(request.PackId, request.Lang, request.Id, true, cancel, (store, code, _) =>
            {
                request.Lang = code;
                var takes = new MakerTakeStore(store);
                var result = takes.Create(request);
                if (result.Ok && !takes.LastReplayed && result.Value!.Take != null)
                    _publish("maker.take.changed", new Dictionary<string, object?>
                    {
                        ["packId"] = request.PackId, ["lang"] = code, ["id"] = request.Id,
                        ["takeId"] = result.Value.Take.Id, ["revision"] = result.Value.Revision, ["change"] = "created",
                    });
                return result;
            }, validate: () =>
            {
                MakerStudioPaths.RequireOperation(request.OperationId);
                if (request.Origin is null && request.Consent is null) return; // v2.4: inherit the pack's voice default
                if (!MakerTakeRules.ValidateOrigin(request.Origin, out var problem)
                    || !MakerTakeRules.ValidateConsent(request.Consent, out problem))
                    throw new ArgumentException(problem);
            });

        public Task<MakerResult<MakerStudioTakeSaved>> SaveTakeAudioAsync(MakerStudioTakeAudioRequest request, byte[] wav, CancellationToken cancel) =>
            RunTakeAsync(request.PackId, request.Lang, request.Id, true, cancel, (store, code, _) =>
            {
                request.Lang = code;
                var takes = new MakerTakeStore(store);
                var result = takes.SaveAudio(request, wav);
                if (result.Ok && !takes.LastReplayed)
                    _publish("maker.take.changed", new Dictionary<string, object?>
                    {
                        ["packId"] = request.PackId, ["lang"] = code, ["id"] = request.Id,
                        ["takeId"] = request.TakeId, ["revision"] = result.Value!.Revision, ["change"] = request.Kind,
                    });
                return result;
            }, validate: () =>
            {
                MakerStudioPaths.RequireTakeId(request.TakeId);
                MakerStudioPaths.RequireOperation(request.OperationId);
                if (!MakerStudioPaths.IsTakeAudioKind(request.Kind)) throw new ArgumentException("invalid audio kind");
            });

        public Task<MakerResult<MakerStudioTakeSaved>> UpdateTakeAsync(MakerStudioUpdateTakeRequest request, CancellationToken cancel) =>
            RunTakeAsync(request.PackId, request.Lang, request.Id, true, cancel, (store, code, _) =>
            {
                request.Lang = code;
                var takes = new MakerTakeStore(store);
                var result = takes.Update(request);
                if (result.Ok && !takes.LastReplayed && result.Value!.Revision != request.ExpectedRevision)
                    _publish("maker.take.changed", new Dictionary<string, object?>
                    {
                        ["packId"] = request.PackId, ["lang"] = code, ["id"] = request.Id,
                        ["takeId"] = request.TakeId, ["revision"] = result.Value.Revision, ["change"] = "updated",
                    });
                return result;
            }, voice: false, validate: () =>
            {
                MakerStudioPaths.RequireTakeId(request.TakeId);
                MakerStudioPaths.RequireOperation(request.OperationId);
                if (request.Origin is null && request.Consent is null && !request.HasTrimStartSec && !request.HasTrimEndSec && !request.EvenOutVolume.HasValue
                    && !request.FollowPackDefault)
                    throw new ArgumentException("at least one take field is required");
                if (request.FollowPackDefault && (request.Origin != null || request.Consent != null))
                    throw new ArgumentException(MakerTakeStore.FollowWithOwnConsent);
                if (request.Origin != null && !MakerTakeRules.ValidateOrigin(request.Origin, out var originProblem))
                    throw new ArgumentException(originProblem);
                if (request.Consent != null && !MakerTakeRules.ValidateConsent(request.Consent, out var consentProblem))
                    throw new ArgumentException(consentProblem);
            });

        public Task<MakerResult<MakerStudioTakeRemoved>> DeleteTakeAsync(MakerStudioDeleteTakeRequest request, CancellationToken cancel) =>
            RunTakeAsync(request.PackId, request.Lang, request.Id, true, cancel, (store, code, _) =>
            {
                request.Lang = code;
                var takes = new MakerTakeStore(store);
                var result = takes.Delete(request);
                if (result.Ok && !takes.LastReplayed)
                    _publish("maker.take.changed", new Dictionary<string, object?>
                    {
                        ["packId"] = request.PackId, ["lang"] = code, ["id"] = request.Id,
                        ["takeId"] = request.TakeId, ["revision"] = result.Value!.Revision, ["change"] = "deleted",
                    });
                return result;
            }, voice: false, validate: () =>
            {
                MakerStudioPaths.RequireTakeId(request.TakeId);
                MakerStudioPaths.RequireOperation(request.OperationId);
            });

        public Task<MakerResult<MakerStudioTakeSaved>> SetFavouriteAsync(MakerStudioFavouriteRequest request, CancellationToken cancel) =>
            RunTakeAsync(request.PackId, request.Lang, request.Id, true, cancel, (store, code, _) =>
            {
                request.Lang = code;
                var takes = new MakerTakeStore(store);
                var result = takes.SetFavourite(request);
                if (result.Ok && !takes.LastReplayed && result.Value!.Revision != request.ExpectedRevision)
                    _publish("maker.take.changed", new Dictionary<string, object?>
                    {
                        ["packId"] = request.PackId, ["lang"] = code, ["id"] = request.Id,
                        ["takeId"] = request.TakeId, ["revision"] = result.Value.Revision, ["change"] = "favourite",
                    });
                return result;
            }, voice: false, validate: () =>
            {
                MakerStudioPaths.RequireOperation(request.OperationId);
                if (request.TakeId != null) MakerStudioPaths.RequireTakeId(request.TakeId);
            });

        public Task<MakerResult<MakerStudioSaved>> PublishAsync(MakerStudioPublishRequest request, CancellationToken cancel) =>
            RunPublishAsync(request.PackId, request.Lang, request.Id, request.OperationId, cancel, (store, code, clip) =>
            {
                request.Lang = code;
                return new MakerPublishStore(store).Publish(request, clip);
            }, () => { MakerStudioPaths.RequireTakeId(request.TakeId); RequireClipRevision(request.ExpectedClipRevision); });

        public Task<MakerResult<MakerStudioSaved>> UnpublishAsync(MakerStudioUnpublishRequest request, CancellationToken cancel) =>
            RunPublishAsync(request.PackId, request.Lang, request.Id, request.OperationId, cancel, (store, code, clip) =>
            {
                request.Lang = code;
                return new MakerPublishStore(store).Unpublish(request, clip);
            }, () => RequireClipRevision(request.ExpectedClipRevision));

        /// <summary>
        /// Transient Studio playback: validates pack/line revision and cached activity (<c>409 game_paused</c> before main-thread
        /// wait), then delegates to the host play seam. <c>sub</c> requires an editable selected pack and uses its
        /// current subtitle with the game's original voice; no take or voice consent is required.
        /// Never writes an operation receipt.
        /// </summary>
        public async Task<MakerResult<MakerStudioSaved>> PlayAsync(MakerStudioPlayRequest request, CancellationToken cancel)
        {
            try
            {
                if (request is null) throw new ArgumentException("play request is required");
                cancel.ThrowIfCancellationRequested();
                var code = ValidateLine(request.Lang, request.Id);
                var dub = string.Equals(request.Variant, "dub", StringComparison.Ordinal);
                var sub = string.Equals(request.Variant, "sub", StringComparison.Ordinal);
                if (!dub && !sub && !string.Equals(request.Variant, "original", StringComparison.Ordinal))
                    throw new ArgumentException("variant must be 'original', 'dub' or 'sub'");
                if (sub && string.IsNullOrEmpty(request.PackId)) throw new ArgumentException("variant 'sub' requires packId");
                if (request.ExpectedRevision != null)
                {
                    if (string.IsNullOrEmpty(request.PackId)) throw new ArgumentException("expectedRevision requires packId");
                    RequireClipRevision(request.ExpectedRevision);
                }

                MakerProjectStore? store = null;
                var revision = string.Empty;
                string? publishedTakeId = null;
                if (request.PackId != null)
                {
                    if (!MakerSegments.IsPackId(request.PackId)) throw new ArgumentException("invalid pack id");
                    var open = _open(request.PackId, sub);
                    if (!open.Ok) return MakerResult<MakerStudioSaved>.Fail(open.Error!, open.Message!, open.Details);
                    store = open.Value!;
                    lock (store.SyncRoot)
                    using (store.RequestScope())
                    {
                        cancel.ThrowIfCancellationRequested();
                        var read = store.ReadLine(code, request.Id);
                        if (!read.Ok)
                        {
                            if (read.Details != null) read.Details.PackId = request.PackId;
                            return MakerResult<MakerStudioSaved>.Fail(read.Error!, read.Message!, read.Details);
                        }

                        if (request.ExpectedRevision != null
                            && !string.Equals(read.Value!.Revision, request.ExpectedRevision, StringComparison.Ordinal))
                            return MakerResult<MakerStudioSaved>.Fail(MakerStudioErrorCodes.Conflict, "the line changed; reload it");

                        revision = read.Value!.Revision;
                        publishedTakeId = read.Value.PublishedTakeId;
                    }
                }

                var snapshot = _activity?.Invoke();
                if (snapshot != null && snapshot.Paused == true)
                    return MakerResult<MakerStudioSaved>.Fail(
                        MakerStudioErrorCodes.GamePaused,
                        "The game is paused in the background. Click the game window to continue.");

                var scope = await CheckLineScope(code, request.Id, cancel).ConfigureAwait(false);
                if (!scope.Ok) return MakerResult<MakerStudioSaved>.Fail(scope.Error!, scope.Message!, scope.Details);
                if (_lineVoice != null)
                {
                    var voice = await _lineVoice(code, request.Id, cancel).ConfigureAwait(false);
                    if (!voice.Ok) return MakerResult<MakerStudioSaved>.Fail(voice.Error!, voice.Message!, voice.Details);
                    if (string.IsNullOrEmpty(voice.Value))
                        return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.BadRequest, "line '" + request.Id + "' is text-only and has no voice clip");
                    var clip = voice.Value!;
                    if (store != null && MakerEditRules.IsClip(clip))
                    {
                        lock (store.SyncRoot)
                        {
                            var clipState = new MakerClipStore(store).Read(code, clip);
                            publishedTakeId = clipState.Publication?.TakeId;
                            if (dub && clipState.VoiceFiles.Count == 0)
                                return MakerResult<MakerStudioSaved>.Fail(
                                    MakerErrorCodes.NotFound,
                                    request.PackId + " has no " + code + " dub for " + clip + " yet");
                        }
                    }
                }

                if (snapshot != null && !snapshot.Stale && snapshot.Busy == true)
                    return MakerResult<MakerStudioSaved>.Fail(
                        MakerErrorCodes.NotReady,
                        snapshot.Reason == "satone_unavailable" ? "Satone cannot be interrupted right now." : "Satone is already speaking.");

                if (_play is null)
                    return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.Unavailable, "Studio playback is not installed");

                cancel.ThrowIfCancellationRequested();
                var live = await _play(new MakerStudioPlayRequest
                {
                    Id = request.Id,
                    Variant = request.Variant,
                    Lang = code,
                    PackId = request.PackId,
                    ExpectedRevision = request.ExpectedRevision,
                }, cancel).ConfigureAwait(false);
                if (!live.Ok) return MakerResult<MakerStudioSaved>.Fail(live.Error!, live.Message!, live.Details);

                var played = live.Value!;
                return MakerResult<MakerStudioSaved>.Success(new MakerStudioSaved
                {
                    Revision = revision,
                    OperationId = string.Empty,
                    Saved = false,
                    Live = played.Saved.Live,
                    Path = null,
                    DurationSec = played.Saved.DurationSec,
                    Note = played.Saved.Note,
                    PublishedTakeId = publishedTakeId,
                    EffectivePackId = played.EffectivePackId,
                    RestartRequired = false,
                });
            }
            catch (OperationCanceledException) { return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled."); }
            catch (ArgumentException ex) { return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.BadRequest, ex.Message); }
        }

        /// <summary>
        /// Best effort after a v1 voice save or removal: a Studio publication of that clip no longer matches the runtime
        /// file, so its <c>voice-provenance.json</c> entry and clip record are dropped (a disclosure never describes a file
        /// the creator replaced by hand). Never fails the v1 save. The host calls it while it holds <see cref="VoiceGate"/>.
        /// </summary>
        public void ForgetV1Voice(string packId, string lang, string clip)
        {
            try
            {
                if (!MakerEditRules.TryLanguage(lang, out var code, out _) || !MakerEditRules.IsClip(clip)) return;
                var open = _open(packId, true);
                if (!open.Ok) return;
                var store = open.Value!;
                lock (store.SyncRoot)
                {
                    var state = new MakerClipStore(store).Read(code, clip);
                    if (state.Publication != null) return;
                    var targets = new List<MakerCommitFile>();
                    var text = store.Files.ReadText(MakerStudioPaths.Provenance, out _);
                    if (text != null && MakerVoiceProvenance.TryRead(text, out var entries, out _)
                        && entries.RemoveAll(e => e.Lang == code && e.Clip == clip) > 0)
                        targets.Add(store.Target(MakerStudioPaths.Provenance, MakerVoiceProvenance.Write(entries)));
                    if (state.RecordPresent) targets.Add(store.DeleteTarget(MakerStudioPaths.Clip(code, clip)));
                    if (targets.Count > 0) store.Commit(MakerProjectRevision.New(), targets, kind: "unpublish");
                }
            }
            catch { /* Optional creator metadata never fails an existing v1 save. */ }
        }

        public Task<MakerResult<MakerStudioGlossary>> GlossaryAsync(string packId, string lang, CancellationToken cancel) =>
            Run(packId, false, cancel, store => new MakerGlossaryStore(store).Read(Language(lang)), validate: () => Language(lang));

        public Task<MakerResult<MakerStudioGlossary>> SaveGlossaryAsync(MakerStudioGlossaryRequest request, CancellationToken cancel) =>
            Run(request.PackId, true, cancel, store =>
            {
                request.Lang = Language(request.Lang);
                var result = new MakerGlossaryStore(store).Save(request);
                if (result.Ok && result.Value!.Revision != request.ExpectedRevision)
                    _publish("maker.glossary.changed", new Dictionary<string, object?>
                    { ["packId"] = request.PackId, ["lang"] = request.Lang, ["revision"] = result.Value.Revision });
                return result;
            }, validate: () =>
            {
                Language(request.Lang); MakerStudioPaths.RequireOperation(request.OperationId);
                var problem = MakerGlossaryStore.Validate(request.Entries ?? Array.Empty<MakerStudioGlossaryEntry>());
                if (problem != null) throw new ArgumentException(problem);
            });

        public Task<MakerResult<MakerStudioVoiceDefault>> VoiceDefaultAsync(string packId, CancellationToken cancel) =>
            Run(packId, false, cancel, store => new MakerVoiceDefaultStore(store).Read());

        public Task<MakerResult<MakerStudioVoiceDefault>> SaveVoiceDefaultAsync(MakerStudioVoiceDefaultRequest request, CancellationToken cancel) =>
            Run(request.PackId, true, cancel, store =>
            {
                var result = new MakerVoiceDefaultStore(store).Save(request);
                // v2.5: inheriting takes (and published disclosures) follow the default; one pack-level invalidation hint.
                if (result.Ok && result.Value!.Revision != request.ExpectedRevision)
                    _publish("maker.pack.changed", new Dictionary<string, object?> { ["packId"] = request.PackId, ["reason"] = "voice-default" });
                return result;
            }, validate: () =>
            {
                MakerStudioPaths.RequireOperation(request.OperationId);
                var problem = MakerVoiceDefaultStore.Validate(request.Origin, request.Consent);
                if (problem != null) throw new ArgumentException(problem);
            });

        /// <summary>D-148: labels every unlabelled dub with the pack's voice default (the creator's click is the confirmation).</summary>
        public Task<MakerResult<MakerStudioVoiceLabelled>> LabelDubsAsync(MakerStudioVoiceLabelRequest request, CancellationToken cancel) =>
            Run(request.PackId, true, cancel, store =>
            {
                var result = new MakerVoiceLabelStore(store).Label(request);
                if (result.Ok && result.Value!.Labelled > 0)
                    _publish("maker.pack.changed", new Dictionary<string, object?> { ["packId"] = request.PackId, ["reason"] = "voice-label" });
                return result;
            }, validate: () =>
            {
                MakerStudioPaths.RequireOperation(request.OperationId);
                var problem = MakerVoiceLabelStore.Validate(request.Origin, request.Consent);
                if (problem != null) throw new ArgumentException(problem);
            });

        public Task<MakerResult<MakerStudioPackCreated>> CreatePackAsync(MakerStudioCreatePackRequest request, CancellationToken cancel) =>
            Task.FromResult(MakerResult<MakerStudioPackCreated>.Fail(MakerErrorCodes.Unavailable, "pack creation is not installed"));

        public Task<MakerResult<MakerStudioOperation>> OperationAsync(string packId, string operationId, CancellationToken cancel) =>
            Run(packId, false, cancel, store => new MakerOperationLedger(store).Read(operationId), validate: () => MakerStudioPaths.RequireOperation(operationId));

        public Task<MakerResult<MakerRecoveryPreview>> PreviewRecoveryAsync(string packId, string blockedOperationId, CancellationToken cancel) =>
            Run(packId, false, cancel, store => new MakerRecoveryStore(store).Preview(blockedOperationId), recover: false,
                validate: () => MakerStudioPaths.RequireOperation(blockedOperationId));

        public Task<MakerResult<MakerRecoveryResolved>> ResolveRecoveryAsync(MakerRecoveryRequest request, CancellationToken cancel) =>
            Run(request.PackId, true, cancel, store => new MakerRecoveryStore(store).Resolve(request), recover: false,
                validate: () =>
                {
                    MakerStudioPaths.RequireOperation(request.BlockedOperationId); MakerStudioPaths.RequireOperation(request.OperationId);
                    MakerStudioPaths.RequireOperation(request.ExpectedRecoveryRevision);
                    if (request.OperationId == request.BlockedOperationId || request.Strategy != "keep-current") throw new ArgumentException("invalid resolution request");
                });

        /// <summary>Compatible v1 write path; shares the complete commit/apply/event gate with Studio writes.</summary>
        public Task<MakerResult<MakerSaved>> SaveV1SubtitleAsync(string packId, string lang, string id, string? text, CancellationToken cancel) =>
            Run(packId, true, cancel, store =>
            {
                var code = Language(lang);
                if (!MakerEditRules.AcceptsSubtitles(code, out var problem)
                    || !MakerEditRules.IsLineId(id) || (text != null && !MakerEditRules.IsSubtitleText(text, out problem)))
                    return MakerResult<MakerSaved>.Fail(MakerErrorCodes.BadRequest, problem ?? "invalid line id");
                var write = MakerSubtitleStore.Save(store.Files, code, id, text);
                if (!write.Ok) return MakerResult<MakerSaved>.Fail(MakerErrorCodes.NotReady, write.Problem!);
                var live = _apply(packId, code, id, text is null, write);
                if (write.Written && CwyLs.ModLoader.Core.Packs.SafeFileNames.ProblemWith(id) == null)
                    new MakerHistoryStore(store).CaptureV1(code, id, write.Previous, write.Current);
                return MakerResult<MakerSaved>.Success(live.Saved);
            }, recover: false, subtitle: true, validate: () => SubtitleInput(lang, id, text, studio: false), lineLang: lang, lineId: id);

        public void CaptureV1Subtitle(string packId, string lang, string id, string? before, string? after)
        {
            try
            {
                var open = _open(packId, true);
                if (open.Ok) new MakerHistoryStore(open.Value!).CaptureV1(lang, id, before, after);
            }
            catch { /* Optional creator metadata never fails an existing v1 save. */ }
        }

        private Task<MakerResult<MakerStudioSaved>> SaveSubtitle(MakerStudioSubtitleRequest? save, MakerStudioRevertRequest? revert, CancellationToken cancel)
        {
            var packId = save?.PackId ?? revert!.PackId;
            return Run(packId, true, cancel, store =>
            {
                var lang = Language(save?.Lang ?? revert!.Lang);
                if (!MakerEditRules.AcceptsSubtitles(lang, out var problem)) return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.BadRequest, problem!);
                var id = save?.Id ?? revert!.Id;
                if (!MakerEditRules.IsLineId(id)) return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.BadRequest, "invalid line id");
                MakerStudioPaths.Line(lang, id);
                if (save != null) save.Lang = lang; else revert!.Lang = lang;
                var history = new MakerHistoryStore(store);
                var result = save != null ? history.Save(save) : history.Revert(revert!);
                if (!result.Ok) return MakerResult<MakerStudioSaved>.Fail(result.Error!, result.Message!, result.Details);
                var project = result.Value!;
                if (!project.Replayed)
                {
                    var durableLive = project.Saved.Live;
                    var durablePath = project.Saved.Path;
                    var durableNote = project.Saved.Note;
                    var durablePack = project.Saved.EffectivePackId;
                    try
                    {
                        var live = _apply(packId, lang, id, project.FileWrite.Current is null, project.FileWrite);
                        project.Saved.Live = live.Saved.Live;
                        project.Saved.Path = live.Saved.Path;
                        project.Saved.Note = live.Saved.Note;
                        project.Saved.EffectivePackId = live.EffectivePackId;
                    }
                    catch (Exception ex)
                    {
                        project.Saved.Live = false;
                        project.Saved.Note = "Saved to the pack; live apply failed (" + ex.GetType().Name + ").";
                    }
                    // This is still inside the same pack gate as commit, apply and publication.
                    // A failed finalisation leaves the earlier durable live:false receipt; it must not fail the save.
                    if (!new MakerOperationLedger(store).UpdateResponse(project.Saved.OperationId,
                        MakerStudioJson.Saved(project.Saved), _beforeReceiptWrite))
                    {
                        project.Saved.Live = durableLive; project.Saved.Path = durablePath;
                        project.Saved.Note = durableNote; project.Saved.EffectivePackId = durablePack;
                    }
                    if (project.HistoryRevisionId != null)
                        _publish("maker.history.changed", new Dictionary<string, object?>
                        {
                            ["packId"] = packId, ["lang"] = lang, ["id"] = id,
                            ["revision"] = project.Saved.Revision, ["revisionId"] = project.HistoryRevisionId,
                        });
                }
                return MakerResult<MakerStudioSaved>.Success(project.Saved);
            }, subtitle: true, validate: () =>
            {
                SubtitleInput(save?.Lang ?? revert!.Lang, save?.Id ?? revert!.Id, save?.Text);
                MakerStudioPaths.RequireOperation(save?.OperationId ?? revert!.OperationId);
                if (revert != null) MakerStudioPaths.RequireOperation(revert.RevisionId);
            }, lineLang: save?.Lang ?? revert!.Lang, lineId: save?.Id ?? revert!.Id);
        }

        private async Task<MakerResult<T>> Run<T>(string packId, bool editing, CancellationToken cancel, Func<MakerProjectStore, MakerResult<T>> work,
            bool recover = false, bool subtitle = false, Action? validate = null, string? lineLang = null, string? lineId = null)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (!MakerSegments.IsPackId(packId)) throw new ArgumentException("invalid pack id");
                validate?.Invoke();
                if (lineLang != null && lineId != null)
                {
                    var scope = await CheckLineScope(Language(lineLang), lineId, cancel).ConfigureAwait(false);
                    if (!scope.Ok) return MakerResult<T>.Fail(scope.Error!, scope.Message!, scope.Details);
                }
                var open = _open(packId, editing);
                if (!open.Ok) return MakerResult<T>.Fail(open.Error!, open.Message!, open.Details);
                var store = open.Value!;
                lock (subtitle ? SubtitleGate : store.SyncRoot)
                lock (store.SyncRoot)
                using (store.RequestScope())
                {
                    cancel.ThrowIfCancellationRequested();
                    var recovery = recover ? store.RecoverPending() : MakerCommitResult.Success();
                    var result = recovery.Ok ? work(store) : MakerResult<T>.Fail(recovery.Error!, recovery.Message!, recovery.Details);
                    if (result.Details != null) result.Details.PackId = packId;
                    return result;
                }
            }
            catch (OperationCanceledException) { return MakerResult<T>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled."); }
            catch (ArgumentException ex) { return MakerResult<T>.Fail(MakerErrorCodes.BadRequest, ex.Message); }
        }

        // Scope is independent of voice availability: silent story lines remain editable. The game host installs
        // this guard on every line route, including existing take records, without changing lower-level store recovery.
        private async Task<MakerResult<bool>> CheckLineScope(string lang, string id, CancellationToken cancel)
        {
            if (_lineScope is null) return MakerResult<bool>.Success(true);
            var scope = await _lineScope(lang, id, cancel).ConfigureAwait(false);
            return scope.Ok && !scope.Value
                ? MakerResult<bool>.Fail(MakerErrorCodes.NotFound, "The game has no story line '" + id + "'.")
                : scope;
        }

        /// <remarks>
        /// <paramref name="voice"/>: the route needs the line's voice clip from the live catalog (create, audio upload,
        /// publish). Routes on existing take records do not, so a line a game update removed can still be cleaned up
        /// (REVIEW-FIXES-M5c item 5).
        /// </remarks>
        private async Task<MakerResult<T>> RunTakeAsync<T>(
            string packId, string lang, string id, bool editing, CancellationToken cancel,
            Func<MakerProjectStore, string, string?, MakerResult<T>> work, Action? validate = null, bool voice = true)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (!MakerSegments.IsPackId(packId)) throw new ArgumentException("invalid pack id");
                var code = ValidateLine(lang, id);
                validate?.Invoke();
                var scope = await CheckLineScope(code, id, cancel).ConfigureAwait(false);
                if (!scope.Ok) return MakerResult<T>.Fail(scope.Error!, scope.Message!, scope.Details);
                var open = _open(packId, editing);
                if (!open.Ok) return MakerResult<T>.Fail(open.Error!, open.Message!, open.Details);
                string? clip = null;
                if (voice && _lineVoice != null)
                {
                    var lineVoice = await _lineVoice(code, id, cancel).ConfigureAwait(false);
                    if (!lineVoice.Ok) return MakerResult<T>.Fail(lineVoice.Error!, lineVoice.Message!, lineVoice.Details);
                    if (string.IsNullOrEmpty(lineVoice.Value))
                        return MakerResult<T>.Fail(MakerErrorCodes.BadRequest, "line '" + id + "' is text-only and has no voice clip");
                    clip = lineVoice.Value;
                }
                var store = open.Value!;
                lock (store.SyncRoot)
                using (store.RequestScope())
                {
                    cancel.ThrowIfCancellationRequested();
                    var result = work(store, code, clip);
                    if (result.Details != null) result.Details.PackId = packId;
                    return result;
                }
            }
            catch (OperationCanceledException) { return MakerResult<T>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled."); }
            catch (ArgumentException ex) { return MakerResult<T>.Fail(MakerErrorCodes.BadRequest, ex.Message); }
        }

        private static void RequireClipRevision(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > 256) throw new ArgumentException("expectedClipRevision is required");
        }

        /// <summary>
        /// Publish and unpublish: name the clip from the live catalog, commit under the pack gate, then apply the runtime
        /// voice live outside it (the main thread may take seconds) and finalise the receipt, all inside <see cref="VoiceGate"/>.
        /// </summary>
        private async Task<MakerResult<MakerStudioSaved>> RunPublishAsync(string packId, string lang, string id, string operationId,
            CancellationToken cancel, Func<MakerProjectStore, string, string, MakerResult<MakerPublishOutcome>> work, Action validate)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (!MakerSegments.IsPackId(packId)) throw new ArgumentException("invalid pack id");
                var code = ValidateLine(lang, id);
                MakerStudioPaths.RequireOperation(operationId);
                validate();
                var scope = await CheckLineScope(code, id, cancel).ConfigureAwait(false);
                if (!scope.Ok) return MakerResult<MakerStudioSaved>.Fail(scope.Error!, scope.Message!, scope.Details);
                if (_lineVoice is null) return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.Unavailable, "the line catalog is not installed, so the clip cannot be named");
                var open = _open(packId, true);
                if (!open.Ok) return MakerResult<MakerStudioSaved>.Fail(open.Error!, open.Message!, open.Details);
                var voice = await _lineVoice(code, id, cancel).ConfigureAwait(false);
                if (!voice.Ok) return MakerResult<MakerStudioSaved>.Fail(voice.Error!, voice.Message!, voice.Details);
                if (string.IsNullOrEmpty(voice.Value))
                    return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.BadRequest, "line '" + id + "' is text-only and has no voice clip");
                if (!MakerEditRules.IsClip(voice.Value))
                    return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.BadRequest, "the voice clip '" + voice.Value + "' cannot be a pack file name");
                var clip = voice.Value!;
                var store = open.Value!;
                await VoiceGate.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    MakerResult<MakerPublishOutcome> outcome;
                    lock (store.SyncRoot)
                    using (store.RequestScope())
                    {
                        outcome = work(store, code, clip);
                    }

                    if (!outcome.Ok)
                    {
                        if (outcome.Details != null) outcome.Details.PackId = packId;
                        return MakerResult<MakerStudioSaved>.Fail(outcome.Error!, outcome.Message!, outcome.Details);
                    }

                    var value = outcome.Value!;
                    var saved = value.Saved;
                    if (value.Replayed || !value.VoiceChanged)
                    {
                        if (!value.Replayed && saved.Saved) PublishedEvent(packId, code, id, clip, saved);
                        return MakerResult<MakerStudioSaved>.Success(saved);
                    }

                    var durable = (saved.Live, saved.Note, saved.EffectivePackId);
                    if (_applyVoice != null)
                    {
                        try
                        {
                            // Not cancellable: the files are committed, so the game must follow them.
                            var live = await _applyVoice(packId, code, clip, value.Write, value.Bytes, CancellationToken.None).ConfigureAwait(false);
                            saved.Live = live.Saved.Live;
                            saved.Note = live.Saved.Note;
                            saved.EffectivePackId = live.EffectivePackId;
                        }
                        catch (Exception ex)
                        {
                            saved.Live = false;
                            saved.Note = "Saved to the pack; live apply failed (" + ex.GetType().Name + ").";
                        }
                    }

                    lock (store.SyncRoot)
                    {
                        if (!new MakerOperationLedger(store).UpdateResponse(operationId, MakerStudioJson.Saved(saved), _beforeReceiptWrite))
                            (saved.Live, saved.Note, saved.EffectivePackId) = durable;
                    }

                    PublishedEvent(packId, code, id, clip, saved);
                    return MakerResult<MakerStudioSaved>.Success(saved);
                }
                finally
                {
                    VoiceGate.Release();
                }
            }
            catch (OperationCanceledException) { return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled."); }
            catch (ArgumentException ex) { return MakerResult<MakerStudioSaved>.Fail(MakerErrorCodes.BadRequest, ex.Message); }
        }

        private void PublishedEvent(string packId, string lang, string id, string clip, MakerStudioSaved saved) =>
            _publish("maker.take.published", new Dictionary<string, object?>
            {
                ["packId"] = packId, ["lang"] = lang, ["id"] = id, ["clip"] = clip,
                ["takeId"] = saved.PublishedTakeId, ["revision"] = saved.Revision, ["live"] = saved.Live,
            });

        private static string ValidateLine(string lang, string id)
        {
            var code = Language(lang);
            if (!MakerEditRules.IsLineId(id)) throw new ArgumentException("invalid line id");
            MakerStudioPaths.Line(code, id);
            return code;
        }

        private static void SubtitleInput(string lang, string id, string? text, bool studio = true)
        {
            var code = Language(lang);
            if (!MakerEditRules.AcceptsSubtitles(code, out var problem) || !MakerEditRules.IsLineId(id)
                || (text != null && !MakerEditRules.IsSubtitleText(text, out problem)))
                throw new ArgumentException(problem ?? "invalid line id");
            if (studio) MakerStudioPaths.Line(code, id);
        }

        private static string Language(string lang)
        {
            if (!MakerEditRules.TryLanguage(lang, out var code, out var problem)) throw new ArgumentException(problem);
            MakerStudioPaths.Glossary(code); // Validates the bare canonical path segment, including device names.
            return code;
        }
    }
}
