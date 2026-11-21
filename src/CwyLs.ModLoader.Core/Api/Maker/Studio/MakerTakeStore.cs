using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>Persistent take slots, creator WAV storage, metadata updates, and favourite selection.</summary>
    public sealed class MakerTakeStore
    {
        /// <summary>v2.5.1: the 400 message when <c>followPackDefault</c> comes with <c>origin</c> or <c>consent</c>.</summary>
        public const string FollowWithOwnConsent = "send followPackDefault without origin and consent";
        /// <summary>v2.5.1: the 403 <c>consent_required</c> message when a take should follow a pack with no default.</summary>
        public const string FollowNoDefault = "set the pack's voice origin and consent before a take can follow it";

        private readonly MakerProjectStore _store;
        private readonly MakerOperationLedger _operations;

        /// <summary>True when the last mutation through this store returned an earlier receipt instead of committing.</summary>
        public bool LastReplayed { get; private set; }

        public MakerTakeStore(MakerProjectStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _operations = new MakerOperationLedger(store);
        }

        /// <summary>The line's takes, favourite and published take.</summary>
        /// <param name="lang">The canonical language code.</param>
        /// <param name="id">The line id.</param>
        /// <param name="clip">The line's voice clip when the catalog named it: the page then carries the clip revision.</param>
        public MakerResult<MakerStudioTakePage> ReadTakes(string lang, string id, string? clip = null) => Locked(() =>
        {
            MakerStudioPaths.Line(lang, id);
            var collision = _store.CaseCollision(MakerStudioPaths.Takes + "/" + lang + "/" + id);
            if (collision != null) return MakerResult<MakerStudioTakePage>.Fail(MakerStudioErrorCodes.Conflict, collision);
            var lineResult = MakerVoiceDefaultStore.ReadLineResolved(_store, lang, id);
            if (!lineResult.Ok) return MakerResult<MakerStudioTakePage>.Fail(lineResult.Error!, lineResult.Message!, lineResult.Details);
            var line = lineResult.Value!;
            return MakerResult<MakerStudioTakePage>.Success(new MakerStudioTakePage
            {
                Revision = line.Revision,
                FavouriteTakeId = line.FavouriteTakeId,
                PublishedTakeId = line.PublishedTakeId,
                ClipRevision = clip is null ? null : new MakerClipStore(_store).Read(lang, clip).Revision,
                Items = line.Takes.Select(t => t.ToContract()).ToArray(),
            });
        });

        /// <summary>
        /// Creator bytes of one take, from this pack's <c>.maker/takes</c> only. Whether a pack may serve them at all
        /// (never a reference pack) is the editor's decision; reading needs no write access to the pack.
        /// </summary>
        public MakerResult<MakerStudioAudio> ReadAudio(string lang, string id, string takeId, string kind) => Locked(() =>
        {
            var path = MakerStudioPaths.TakeAudio(lang, id, takeId, kind);
            var collision = _store.CaseCollision(path);
            if (collision != null) return MakerResult<MakerStudioAudio>.Fail(MakerStudioErrorCodes.Conflict, collision);
            var lineResult = _store.ReadLine(lang, id);
            if (!lineResult.Ok) return MakerResult<MakerStudioAudio>.Fail(lineResult.Error!, lineResult.Message!, lineResult.Details);
            var take = lineResult.Value!.Takes.FirstOrDefault(t => string.Equals(t.Id, takeId, StringComparison.Ordinal));
            if (take is null) return MakerResult<MakerStudioAudio>.Fail(MakerErrorCodes.NotFound, "no such take");
            var present = string.Equals(kind, MakerTakeRules.SourceKind, StringComparison.Ordinal) ? take.SourcePresent : take.RenderReady;
            var bytes = present ? _store.Files.ReadBytes(path) : null;
            if (bytes is null) return MakerResult<MakerStudioAudio>.Fail(MakerErrorCodes.NotFound, "the take has no " + kind + " audio");
            if (!MakerTakeRules.InspectAudio(kind, bytes, out _, out var durationSec, out _, out _))
                return MakerResult<MakerStudioAudio>.Fail(MakerStudioErrorCodes.MetadataUnavailable, "stored take audio is unreadable");
            return MakerResult<MakerStudioAudio>.Success(new MakerStudioAudio { Bytes = bytes, Kind = kind, DurationSec = durationSec });
        });

        public MakerResult<MakerStudioTakeSaved> Create(MakerStudioCreateTakeRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            return Locked(() =>
            {
                // v2.4: origin and consent omitted together = copy the pack's voice default at creation time.
                var inherit = request.Origin is null && request.Consent is null;
                if (!inherit && (!MakerTakeRules.ValidateOrigin(request.Origin, out var problem) || !MakerTakeRules.ValidateConsent(request.Consent, out problem)))
                    return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.BadRequest, problem!);
                MakerStudioPaths.Line(request.Lang, request.Id);
                MakerStudioPaths.RequireOperation(request.OperationId);
                var fingerprint = inherit
                    ? MakerOperationLedger.Fingerprint("take.create", request.PackId, request.Lang, request.Id, request.ExpectedRevision, MakerStudioConsentSources.Pack)
                    : MakerOperationLedger.Fingerprint("take.create", request.PackId, request.Lang, request.Id,
                        request.ExpectedRevision, request.Origin, request.Consent!.Confirmed ? "1" : "0", request.Consent.StatementVersion);
                return Mutate(request, fingerprint, request.Lang, request.Id, "creating a take", ReplayTakeSaved, (header, line) =>
                {
                    if (line.Takes.Count >= MakerStudioContract.TakesPerLine)
                        return MakerResult<MakerStudioTakeSaved>.Fail(MakerStudioErrorCodes.TakeLimit, "a line may have at most 3 takes; delete one before creating another");
                    var nowIso = MakerProjectRevision.At(_store.Clock.Now);
                    string origin;
                    MakerStudioConsent consent;
                    if (inherit)
                    {
                        // Read under the store lock this commit holds. v2.5: the take then follows later default changes when read
                        // (MakerVoiceDefaultStore.ResolveInherited) until it gets its own origin or consent.
                        var defaults = new MakerVoiceDefaultStore(_store).ReadDocument();
                        if (!defaults.Ok) return MakerResult<MakerStudioTakeSaved>.Fail(defaults.Error!, defaults.Message!, defaults.Details);
                        var pack = defaults.Value!;
                        if (pack.Origin is null || pack.Consent is null)
                            return MakerResult<MakerStudioTakeSaved>.Fail(MakerStudioErrorCodes.ConsentRequired, MakerVoiceDefaultStore.NoDefaultMessage);
                        origin = pack.Origin;
                        consent = new MakerStudioConsent
                        {
                            Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion, ConfirmedAt = pack.Consent.ConfirmedAt,
                        };
                    }
                    else
                    {
                        origin = request.Origin!;
                        consent = new MakerStudioConsent
                        {
                            Confirmed = request.Consent!.Confirmed,
                            StatementVersion = MakerStudioContract.ConsentVersion,
                            ConfirmedAt = request.Consent.Confirmed ? nowIso : null,
                        };
                    }

                    var take = new MakerTakeRecord
                    {
                        Id = NewTakeId(line.Takes),
                        Origin = origin,
                        Consent = consent,
                        CreatedAt = nowIso,
                        UpdatedAt = nowIso,
                        ConsentSource = inherit ? MakerStudioConsentSources.Pack : MakerStudioConsentSources.Take,
                    };
                    line.Takes.Add(take);
                    line.Revision = MakerProjectRevision.New();
                    var saved = new MakerStudioTakeSaved { Revision = line.Revision, Take = take.ToContract() };
                    return Commit(request, fingerprint, "take.create", header, line, true, MakerStudioJson.TakeSaved(saved), new List<MakerCommitFile>(), saved);
                });
            });
        }

        public MakerResult<MakerStudioTakeSaved> SaveAudio(MakerStudioTakeAudioRequest request, byte[] wav)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            return Locked(() =>
            {
                var audioPath = MakerStudioPaths.TakeAudio(request.Lang, request.Id, request.TakeId, request.Kind);
                MakerStudioPaths.RequireOperation(request.OperationId);
                if (!MakerTakeRules.InspectAudio(request.Kind, wav, out var canonical, out var durationSec, out var wavError, out var wavProblem))
                    return MakerResult<MakerStudioTakeSaved>.Fail(wavError!, wavProblem!);
                var wavFp = MakerPackFiles.Fingerprint(canonical);
                var fingerprint = MakerOperationLedger.Fingerprint("take.audio", request.PackId, request.Lang, request.Id,
                    request.TakeId, request.Kind, request.ExpectedRevision, wavFp);
                return Mutate(request, fingerprint, request.Lang, request.Id, "saving take audio", ReplayTakeSaved, (header, line) =>
                {
                    var collision = _store.CaseCollision(audioPath);
                    if (collision != null) return MakerResult<MakerStudioTakeSaved>.Fail(MakerStudioErrorCodes.Conflict, collision);
                    var take = line.Takes.FirstOrDefault(t => string.Equals(t.Id, request.TakeId, StringComparison.Ordinal));
                    if (take is null) return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.NotFound, "no such take");
                    var isSource = string.Equals(request.Kind, MakerTakeRules.SourceKind, StringComparison.Ordinal);
                    if (!isSource && !take.SourcePresent)
                        return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.NotFound, "the take has no source audio; upload the source first");
                    var files = new List<MakerCommitFile> { _store.Target(audioPath, canonical) };
                    if (isSource)
                    {
                        take.SourcePresent = true;
                        take.SourceDurationSec = durationSec;
                        take.SourceRevision = MakerProjectRevision.New();
                        take.SourceFingerprint = wavFp;
                        MakerProjectStore.ClearRender(take);
                        if ((take.TrimStartSec.HasValue && take.TrimStartSec.Value >= durationSec)
                            || (take.TrimEndSec.HasValue && take.TrimEndSec.Value > durationSec + 0.001d))
                        {
                            take.TrimStartSec = null;
                            take.TrimEndSec = null;
                        }

                        var renderPath = MakerStudioPaths.TakeAudio(request.Lang, request.Id, take.Id, MakerTakeRules.RenderKind);
                        if (_store.Files.Exists(renderPath)) files.Add(_store.DeleteTarget(renderPath));
                    }
                    else
                    {
                        take.RenderReady = true;
                        take.RenderDurationSec = durationSec;
                        take.RenderFingerprint = wavFp;
                        take.RenderSourceFingerprint = take.SourceFingerprint;
                    }

                    take.UpdatedAt = MakerProjectRevision.At(_store.Clock.Now);
                    line.Revision = MakerProjectRevision.New();
                    var saved = new MakerStudioTakeSaved { Revision = line.Revision, Take = take.ToContract() };
                    var result = Commit(request, fingerprint, "take.audio", header, line, true, MakerStudioJson.TakeSaved(saved), files, saved);
                    if (result.Ok) _store.RememberAudio(audioPath, wavFp);
                    return result;
                });
            });
        }

        public MakerResult<MakerStudioTakeSaved> Update(MakerStudioUpdateTakeRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            return Locked(() =>
            {
                MakerStudioPaths.Line(request.Lang, request.Id);
                MakerStudioPaths.RequireTakeId(request.TakeId);
                MakerStudioPaths.RequireOperation(request.OperationId);
                if (request.Origin is null && request.Consent is null
                    && !request.HasTrimStartSec && !request.HasTrimEndSec && !request.EvenOutVolume.HasValue && !request.FollowPackDefault)
                    return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.BadRequest, "at least one take field is required");
                if (request.FollowPackDefault && (request.Origin != null || request.Consent != null))
                    return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.BadRequest, FollowWithOwnConsent);
                if (request.Origin != null && !MakerTakeRules.ValidateOrigin(request.Origin, out var originProblem))
                    return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.BadRequest, originProblem!);
                if (request.Consent != null && !MakerTakeRules.ValidateConsent(request.Consent, out var consentProblem))
                    return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.BadRequest, consentProblem!);
                if (!MakerTakeRules.ValidateTrim(request.HasTrimStartSec ? request.TrimStartSec : null,
                        request.HasTrimEndSec ? request.TrimEndSec : null, null, out var rawTrimProblem))
                    return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.BadRequest, rawTrimProblem!);
                var fingerprint = MakerOperationLedger.Fingerprint(
                    "take.update", request.PackId, request.Lang, request.Id, request.TakeId, request.ExpectedRevision,
                    request.Origin ?? "<omitted>",
                    request.Consent is null ? "<omitted>" : (request.Consent.Confirmed ? "1:" : "0:") + request.Consent.StatementVersion,
                    request.HasTrimStartSec ? (request.TrimStartSec.HasValue ? request.TrimStartSec.Value.ToString("R", CultureInfo.InvariantCulture) : "<null>") : "<omitted>",
                    request.HasTrimEndSec ? (request.TrimEndSec.HasValue ? request.TrimEndSec.Value.ToString("R", CultureInfo.InvariantCulture) : "<null>") : "<omitted>",
                    request.EvenOutVolume.HasValue ? (request.EvenOutVolume.Value ? "1" : "0") : "<omitted>");
                // v2.5.1: wrapped only when following, so every earlier fingerprint (and receipt) stays the same.
                if (request.FollowPackDefault) fingerprint = MakerOperationLedger.Fingerprint("take.update.follow", fingerprint);
                return Mutate(request, fingerprint, request.Lang, request.Id, "updating the take", ReplayTakeSaved, (header, line) =>
                {
                    var take = line.Takes.FirstOrDefault(t => string.Equals(t.Id, request.TakeId, StringComparison.Ordinal));
                    if (take is null) return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.NotFound, "no such take");
                    MakerVoiceDefaultDocument? pack = null;
                    if (request.FollowPackDefault)
                    {
                        var defaults = new MakerVoiceDefaultStore(_store).ReadDocument();
                        if (!defaults.Ok) return MakerResult<MakerStudioTakeSaved>.Fail(defaults.Error!, defaults.Message!, defaults.Details);
                        pack = defaults.Value!;
                        if (pack.Origin is null || pack.Consent?.ConfirmedAt is null)
                            return MakerResult<MakerStudioTakeSaved>.Fail(MakerStudioErrorCodes.ConsentRequired, FollowNoDefault);
                    }

                    var follows = pack != null && (!string.Equals(take.ConsentSource, MakerStudioConsentSources.Pack, StringComparison.Ordinal)
                        || !string.Equals(take.Origin, pack.Origin, StringComparison.Ordinal) || !take.Consent.Confirmed
                        || take.Consent.StatementVersion != MakerStudioContract.ConsentVersion
                        || !string.Equals(take.Consent.ConfirmedAt, pack.Consent!.ConfirmedAt, StringComparison.Ordinal));
                    var trimStart = request.HasTrimStartSec ? request.TrimStartSec : take.TrimStartSec;
                    var trimEnd = request.HasTrimEndSec ? request.TrimEndSec : take.TrimEndSec;
                    if (!MakerTakeRules.ValidateTrim(trimStart, trimEnd, take.SourceDurationSec, out var trimProblem))
                        return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.BadRequest, trimProblem!);
                    var originChanged = request.Origin != null && !string.Equals(request.Origin, take.Origin, StringComparison.Ordinal);
                    var audioParamsChanged = trimStart != take.TrimStartSec || trimEnd != take.TrimEndSec
                        || (request.EvenOutVolume.HasValue && request.EvenOutVolume.Value != take.EvenOutVolume);
                    var nowIso = MakerProjectRevision.At(_store.Clock.Now);
                    var nextConfirmed = take.Consent.Confirmed;
                    var nextConfirmedAt = take.Consent.ConfirmedAt;
                    var consentChanged = false;
                    if (request.Consent != null)
                    {
                        nextConfirmed = request.Consent.Confirmed;
                        nextConfirmedAt = request.Consent.Confirmed
                            ? (take.Consent.Confirmed && !originChanged && take.Consent.ConfirmedAt != null ? take.Consent.ConfirmedAt : nowIso)
                            : null;
                        consentChanged = nextConfirmed != take.Consent.Confirmed
                            || !string.Equals(nextConfirmedAt, take.Consent.ConfirmedAt, StringComparison.Ordinal);
                    }
                    else if (originChanged)
                    {
                        consentChanged = take.Consent.Confirmed || take.Consent.ConfirmedAt != null;
                        nextConfirmed = false;
                        nextConfirmedAt = null;
                    }

                    // v2.4: an origin or consent sent per take overrides an inherited pack default for good.
                    var ownConsent = (request.Origin != null || request.Consent != null)
                        && !string.Equals(take.ConsentSource, MakerStudioConsentSources.Take, StringComparison.Ordinal);
                    var changed = line.ExternalDrift || originChanged || consentChanged || audioParamsChanged || ownConsent || follows;
                    var files = new List<MakerCommitFile>();
                    if (changed)
                    {
                        if (request.Origin != null) take.Origin = request.Origin;
                        if (request.Origin != null || request.Consent != null) take.ConsentSource = MakerStudioConsentSources.Take;
                        take.Consent = new MakerStudioConsent
                        {
                            Confirmed = nextConfirmed, StatementVersion = MakerStudioContract.ConsentVersion, ConfirmedAt = nextConfirmedAt,
                        };
                        take.TrimStartSec = trimStart;
                        take.TrimEndSec = trimEnd;
                        if (request.EvenOutVolume.HasValue) take.EvenOutVolume = request.EvenOutVolume.Value;
                        if (audioParamsChanged)
                        {
                            MakerProjectStore.ClearRender(take);
                            var renderPath = MakerStudioPaths.TakeAudio(request.Lang, request.Id, take.Id, MakerTakeRules.RenderKind);
                            if (_store.Files.Exists(renderPath)) files.Add(_store.DeleteTarget(renderPath));
                        }

                        if (follows)
                        {
                            // v2.5.1 re-link: the current default now, and its later changes; a published take's disclosure follows too.
                            take.Origin = pack!.Origin!;
                            take.Consent = new MakerStudioConsent
                            {
                                Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion, ConfirmedAt = pack.Consent!.ConfirmedAt,
                            };
                            take.ConsentSource = MakerStudioConsentSources.Pack;
                        }

                        // IG-01: any origin or consent change on the published take (own edit, re-link) rewrites its disclosure in
                        // this commit; without a confirmed consent it is disclosed as unconfirmed and export refuses it.
                        if (string.Equals(line.PublishedTakeId, take.Id, StringComparison.Ordinal) && line.PublishedClip != null)
                        {
                            var disclosure = MakerVoiceDefaultStore.Disclose(_store, request.Lang, request.Id, line.PublishedClip, take);
                            if (!disclosure.Ok) return MakerResult<MakerStudioTakeSaved>.Fail(disclosure.Error!, disclosure.Message!, disclosure.Details);
                            if (disclosure.Value != null) files.Add(disclosure.Value);
                        }

                        take.UpdatedAt = nowIso;
                        line.Revision = MakerProjectRevision.New();
                    }

                    var saved = new MakerStudioTakeSaved { Revision = line.Revision, Take = take.ToContract() };
                    return Commit(request, fingerprint, "take.update", header, changed ? line : null, changed, MakerStudioJson.TakeSaved(saved), files, saved);
                });
            });
        }

        public MakerResult<MakerStudioTakeRemoved> Delete(MakerStudioDeleteTakeRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            return Locked(() =>
            {
                var takeDir = MakerStudioPaths.TakeDir(request.Lang, request.Id, request.TakeId);
                MakerStudioPaths.RequireOperation(request.OperationId);
                var fingerprint = MakerOperationLedger.Fingerprint("take.delete", request.PackId, request.Lang, request.Id,
                    request.TakeId, request.ExpectedRevision);
                return Mutate(request, fingerprint, request.Lang, request.Id, "deleting the take", ReplayTakeRemoved, (header, line) =>
                {
                    var take = line.Takes.FirstOrDefault(t => string.Equals(t.Id, request.TakeId, StringComparison.Ordinal));
                    if (take is null) return MakerResult<MakerStudioTakeRemoved>.Fail(MakerErrorCodes.NotFound, "no such take");
                    if (string.Equals(line.PublishedTakeId, take.Id, StringComparison.Ordinal))
                        return MakerResult<MakerStudioTakeRemoved>.Fail(MakerStudioErrorCodes.TakeInUse, "unpublish or replace this take before deleting it");
                    line.Takes.Remove(take);
                    if (string.Equals(line.FavouriteTakeId, take.Id, StringComparison.Ordinal)) line.FavouriteTakeId = null;
                    line.Revision = MakerProjectRevision.New();
                    var removed = new MakerStudioTakeRemoved { Revision = line.Revision, Removed = true };
                    var files = new List<MakerCommitFile>();
                    foreach (var kind in new[] { MakerTakeRules.SourceKind, MakerTakeRules.RenderKind })
                    {
                        var path = MakerStudioPaths.TakeAudio(request.Lang, request.Id, take.Id, kind);
                        if (_store.Files.Exists(path)) files.Add(_store.DeleteTarget(path));
                    }

                    var result = Commit(request, fingerprint, "take.delete", header, line, true, MakerStudioJson.TakeRemoved(removed), files, removed);
                    if (result.Ok) TryRemoveEmptyTakeDirs(takeDir);
                    return result;
                });
            });
        }

        public MakerResult<MakerStudioTakeSaved> SetFavourite(MakerStudioFavouriteRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            return Locked(() =>
            {
                MakerStudioPaths.Line(request.Lang, request.Id);
                MakerStudioPaths.RequireOperation(request.OperationId);
                if (request.TakeId != null) MakerStudioPaths.RequireTakeId(request.TakeId);
                var fingerprint = MakerOperationLedger.Fingerprint("take.favourite", request.PackId, request.Lang, request.Id,
                    request.ExpectedRevision, request.TakeId);
                return Mutate(request, fingerprint, request.Lang, request.Id, "setting the favourite take", ReplayFavouriteSaved, (header, line) =>
                {
                    if (request.TakeId != null && !line.Takes.Any(t => string.Equals(t.Id, request.TakeId, StringComparison.Ordinal)))
                        return MakerResult<MakerStudioTakeSaved>.Fail(MakerErrorCodes.NotFound, "no such take");
                    var changed = line.ExternalDrift || !string.Equals(line.FavouriteTakeId, request.TakeId, StringComparison.Ordinal);
                    if (changed)
                    {
                        line.FavouriteTakeId = request.TakeId;
                        line.Revision = MakerProjectRevision.New();
                    }

                    var saved = new MakerStudioTakeSaved { Revision = line.Revision, FavouriteTakeId = line.FavouriteTakeId };
                    return Commit(request, fingerprint, "take.favourite", header, changed ? line : null, changed, MakerStudioJson.TakeSaved(saved), new List<MakerCommitFile>(), saved);
                });
            });
        }

        /// <summary>The pack gate and the error mapping every take route shares (REVIEW-FIXES-M5c item 7).</summary>
        private MakerResult<T> Locked<T>(Func<MakerResult<T>> work)
        {
            lock (_store.SyncRoot)
            {
                try { return work(); }
                catch (ArgumentException ex) { return MakerResult<T>.Fail(MakerErrorCodes.BadRequest, ex.Message); }
                catch (UnauthorizedAccessException ex) { return MakerResult<T>.Fail(MakerStudioErrorCodes.MetadataUnavailable, ex.Message); }
                catch (IOException ex)
                {
                    // A full disk or a locked file mid-commit: the journal recovers on the next access.
                    return MakerResult<T>.Fail(MakerErrorCodes.Unavailable, "the take files could not be read or written: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// The one mutation prologue: recover pending commits, replay or refuse a reused operation id, then hand the
        /// current header and line to <paramref name="apply"/> when <c>expectedRevision</c> still matches.
        /// </summary>
        private MakerResult<T> Mutate<T>(MakerStudioMutation request, string fingerprint, string lang, string id, string action,
            Func<MakerOperationReceipt, MakerRecoveryDetails, MakerResult<T>> replay, Func<MakerProjectHeader, MakerLineDocument, MakerResult<T>> apply)
        {
            var recovered = _store.RecoverPending();
            if (!recovered.Ok) return MakerResult<T>.Fail(recovered.Error!, recovered.Message!, recovered.Details);
            var checkedOp = _operations.Check(request.OperationId, fingerprint);
            if (checkedOp.Kind == MakerOperationCheckKind.Replay) return replay(checkedOp.Receipt!, _operations.RecoveryDetails(request.OperationId));
            if (checkedOp.Kind != MakerOperationCheckKind.New)
            {
                return MakerResult<T>.Fail(
                    checkedOp.Kind == MakerOperationCheckKind.Conflict ? MakerStudioErrorCodes.Conflict : MakerStudioErrorCodes.RecoveryRequired,
                    checkedOp.Message ?? "the operation cannot continue", checkedOp.Details);
            }

            var header = _store.ReadHeader();
            if (!header.Ok) return MakerResult<T>.Fail(header.Error!, header.Message!, header.Details);
            var line = MakerVoiceDefaultStore.ReadLineResolved(_store, lang, id);
            if (!line.Ok) return MakerResult<T>.Fail(line.Error!, line.Message!, line.Details);
            if (!string.Equals(request.ExpectedRevision, line.Value!.Revision, StringComparison.Ordinal))
                return MakerResult<T>.Fail(MakerStudioErrorCodes.Conflict, "the line changed; reload it before " + action);
            return apply(header.Value!, line.Value!);
        }

        /// <summary>Commits <paramref name="files"/>, the line (when changed) and the receipt as one recoverable take commit.</summary>
        private MakerResult<T> Commit<T>(MakerStudioMutation request, string fingerprint, string kind, MakerProjectHeader header,
            MakerLineDocument? line, bool saved, JsonObject response, List<MakerCommitFile> files, T value)
        {
            var (lang, id) = Line(request);
            var revision = response.TryGetValue("revision", out var r) ? r as string ?? string.Empty : string.Empty;
            var receipt = MakerOperationLedger.Receipt(request.OperationId, fingerprint, kind, revision, saved, _store.Clock.Now, response);
            var targets = new List<MakerCommitFile>();
            var headerTarget = _store.HeaderTarget(header);
            if (headerTarget != null) targets.Add(headerTarget);
            targets.AddRange(files);
            if (line != null) targets.Add(_store.Target(MakerStudioPaths.Line(lang, id), MakerProjectStore.Utf8(MakerProjectJson.WriteLine(line))));
            targets.Add(_operations.ReceiptTarget(receipt));
            var committed = _store.Commit(request.OperationId, targets, kind: "take");
            if (!committed.Ok) return MakerResult<T>.Fail(committed.Error!, committed.Message!, committed.Details);
            _operations.Prune();
            return MakerResult<T>.Success(value);
        }

        private static (string Lang, string Id) Line(MakerStudioMutation request) => request switch
        {
            MakerStudioCreateTakeRequest r => (r.Lang, r.Id),
            MakerStudioTakeAudioRequest r => (r.Lang, r.Id),
            MakerStudioUpdateTakeRequest r => (r.Lang, r.Id),
            MakerStudioDeleteTakeRequest r => (r.Lang, r.Id),
            MakerStudioFavouriteRequest r => (r.Lang, r.Id),
            _ => throw new ArgumentException("not a take request"),
        };

        private static string NewTakeId(List<MakerTakeRecord> existing)
        {
            while (true)
            {
                var candidate = MakerProjectRevision.New();
                if (!existing.Any(t => string.Equals(t.Id, candidate, StringComparison.Ordinal)))
                {
                    return candidate;
                }
            }
        }

        private void TryRemoveEmptyTakeDirs(string takeDir)
        {
            try
            {
                var full = _store.Files.Resolve(takeDir);
                for (var i = 0; i < 3 && Directory.Exists(full); i++)
                {
                    if (Directory.EnumerateFileSystemEntries(full).Any())
                    {
                        break;
                    }

                    Directory.Delete(full, recursive: false);
                    var parent = Path.GetDirectoryName(full);
                    if (string.IsNullOrEmpty(parent))
                    {
                        break;
                    }

                    full = parent!;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Best-effort empty directory cleanup only.
            }
        }

        private MakerResult<MakerStudioTakeSaved> ReplayTakeSaved(MakerOperationReceipt receipt, MakerRecoveryDetails details)
        {
            if (!(receipt.Response.TryGetValue("revision", out var revNode) && revNode is string revision)
                || !(receipt.Response.TryGetValue("take", out var takeNode) && takeNode is JsonObject t)
                || !TryReadTake(t, out var take))
            {
                return MakerResult<MakerStudioTakeSaved>.Fail(MakerStudioErrorCodes.RecoveryRequired, "the take operation receipt is incomplete", details);
            }

            LastReplayed = true;
            return MakerResult<MakerStudioTakeSaved>.Success(new MakerStudioTakeSaved
            {
                Revision = revision,
                Take = take,
            });
        }

        private MakerResult<MakerStudioTakeSaved> ReplayFavouriteSaved(MakerOperationReceipt receipt, MakerRecoveryDetails details)
        {
            if (!(receipt.Response.TryGetValue("revision", out var revNode) && revNode is string revision)
                || !receipt.Response.TryGetValue("favouriteTakeId", out var favNode)
                || (favNode != null && !(favNode is string)))
            {
                return MakerResult<MakerStudioTakeSaved>.Fail(MakerStudioErrorCodes.RecoveryRequired, "the favourite operation receipt is incomplete", details);
            }

            LastReplayed = true;
            return MakerResult<MakerStudioTakeSaved>.Success(new MakerStudioTakeSaved
            {
                Revision = revision,
                FavouriteTakeId = favNode as string,
            });
        }

        private MakerResult<MakerStudioTakeRemoved> ReplayTakeRemoved(MakerOperationReceipt receipt, MakerRecoveryDetails details)
        {
            if (!(receipt.Response.TryGetValue("revision", out var revNode) && revNode is string revision)
                || !(receipt.Response.TryGetValue("removed", out var remNode) && remNode is bool removed && removed))
            {
                return MakerResult<MakerStudioTakeRemoved>.Fail(MakerStudioErrorCodes.RecoveryRequired, "the delete-take operation receipt is incomplete", details);
            }

            LastReplayed = true;
            return MakerResult<MakerStudioTakeRemoved>.Success(new MakerStudioTakeRemoved
            {
                Revision = revision,
                Removed = true,
            });
        }

        private static bool TryReadTake(JsonObject t, out MakerStudioTake take)
        {
            take = new MakerStudioTake();
            if (!(t.TryGetValue("id", out var idNode) && idNode is string id && MakerStudioPaths.IsTakeId(id))
                || !(t.TryGetValue("origin", out var originNode) && originNode is string origin && MakerTakeRules.IsOrigin(origin))
                || !(t.TryGetValue("consent", out var consentNode) && consentNode is JsonObject c)
                || !(c.TryGetValue("confirmed", out var confirmedNode) && confirmedNode is bool confirmed)
                || !(c.TryGetValue("statementVersion", out var versionNode) && versionNode is string version)
                || !c.TryGetValue("confirmedAt", out var confirmedAtNode) || (confirmedAtNode != null && !(confirmedAtNode is string))
                || !t.TryGetValue("sourceRevision", out var sourceRevNode) || (sourceRevNode != null && !(sourceRevNode is string))
                || !TryOptionalNumber(t, "sourceDurationSec", out var sourceDuration)
                || !TryOptionalNumber(t, "renderDurationSec", out var renderDuration)
                || !TryOptionalNumber(t, "trimStartSec", out var trimStart)
                || !TryOptionalNumber(t, "trimEndSec", out var trimEnd)
                || !(t.TryGetValue("evenOutVolume", out var evenNode) && evenNode is bool evenOut)
                || !(t.TryGetValue("sourcePresent", out var sourcePresentNode) && sourcePresentNode is bool sourcePresent)
                || !(t.TryGetValue("renderReady", out var renderReadyNode) && renderReadyNode is bool renderReady)
                || !(t.TryGetValue("createdAt", out var createdNode) && createdNode is string createdAt)
                || !(t.TryGetValue("updatedAt", out var updatedNode) && updatedNode is string updatedAt))
            {
                return false;
            }

            // v2.4, optional: receipts written before it have no consentSource (the take's own consent).
            var consentSource = t.TryGetValue("consentSource", out var sourceNode)
                && string.Equals(sourceNode as string, MakerStudioConsentSources.Pack, StringComparison.Ordinal)
                ? MakerStudioConsentSources.Pack : MakerStudioConsentSources.Take;

            take = new MakerStudioTake
            {
                Id = id,
                Origin = origin,
                Consent = new MakerStudioConsent
                {
                    Confirmed = confirmed,
                    StatementVersion = version,
                    ConfirmedAt = confirmedAtNode as string,
                },
                SourceRevision = sourceRevNode as string,
                SourceDurationSec = sourceDuration,
                RenderDurationSec = renderDuration,
                TrimStartSec = trimStart,
                TrimEndSec = trimEnd,
                EvenOutVolume = evenOut,
                SourcePresent = sourcePresent,
                RenderReady = renderReady,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                ConsentSource = consentSource,
            };
            return true;
        }

        private static bool TryOptionalNumber(JsonObject obj, string key, out double? value)
        {
            value = null;
            if (!obj.TryGetValue(key, out var node) || node is null)
            {
                return true;
            }

            if (node is double d && !double.IsNaN(d) && !double.IsInfinity(d))
            {
                value = d;
                return true;
            }

            if (node is long l)
            {
                value = l;
                return true;
            }

            return false;
        }
    }
}
