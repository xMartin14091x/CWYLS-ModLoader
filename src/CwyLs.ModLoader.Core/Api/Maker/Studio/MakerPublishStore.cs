using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>What a publish or unpublish committed, for the live voice apply that follows it.</summary>
    public sealed class MakerPublishOutcome
    {
        /// <summary>The durable answer (live:false until the live apply finalises the receipt).</summary>
        public MakerStudioSaved Saved { get; set; } = new MakerStudioSaved();
        /// <summary>The answer is an earlier receipt; nothing was written and nothing must be applied live.</summary>
        public bool Replayed { get; set; }
        /// <summary>The runtime voice files changed, so the live voice set must follow.</summary>
        public bool VoiceChanged { get; set; }
        public string Clip { get; set; } = string.Empty;
        /// <summary>The runtime voice changes in v1's terms (written path, moved aside, restored), for the live apply and its note.</summary>
        public MakerVoiceWrite Write { get; set; } = new MakerVoiceWrite();
        /// <summary>The published canonical WAV (48 kHz mono PCM16); null when the clip was removed.</summary>
        public byte[]? Bytes { get; set; }
    }

    /// <summary>
    /// Publication of a take's prepared render as the pack's runtime voice for one (language, clip), and its removal, each
    /// one recoverable commit: the runtime WAV, every moved-aside or restored file, <c>voice-provenance.json</c>, the
    /// per-clip record and the line. Callers hold the pack gate. Never deletes creator bytes: files in the clip's place are
    /// moved under <c>_maker_backup/</c>; only Studio's own published WAV (whose bytes the take's render still holds) is deleted.
    /// </summary>
    public sealed class MakerPublishStore
    {
        private readonly MakerProjectStore _store;
        private readonly MakerOperationLedger _operations;

        public MakerPublishStore(MakerProjectStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _operations = new MakerOperationLedger(store);
        }

        public MakerResult<MakerPublishOutcome> Publish(MakerStudioPublishRequest request, string clip)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            MakerStudioPaths.RequireTakeId(request.TakeId);
            var fingerprint = MakerOperationLedger.Fingerprint("publish", request.PackId, request.Lang, request.Id, request.TakeId,
                request.ExpectedRevision, request.ExpectedClipRevision);
            return Run(request, request.Lang, request.Id, clip, request.ExpectedClipRevision, fingerprint, (header, line, state) =>
            {
                var take = line.Takes.FirstOrDefault(t => string.Equals(t.Id, request.TakeId, StringComparison.Ordinal));
                if (take is null) return Fail(MakerErrorCodes.NotFound, "no such take");
                if (!take.Consent.Confirmed || take.Consent.StatementVersion != MakerStudioContract.ConsentVersion
                    || take.Consent.ConfirmedAt is null || !MakerTakeRules.IsOrigin(take.Origin))
                    return Fail(MakerStudioErrorCodes.ConsentRequired, "confirm the " + MakerStudioContract.ConsentVersion + " statement for this take's origin before publishing it");
                var renderPath = MakerStudioPaths.TakeAudio(request.Lang, request.Id, take.Id, MakerTakeRules.RenderKind);
                var bytes = take.RenderReady ? _store.Files.ReadBytes(renderPath) : null;
                if (bytes is null || MakerPackFiles.Fingerprint(bytes) != take.RenderFingerprint
                    || !MakerTakeRules.InspectAudio(MakerTakeRules.RenderKind, bytes, out _, out var duration, out _, out _))
                    return Fail(MakerStudioErrorCodes.RenderRequired, "prepare and upload a render of the current source before publishing");
                if (!TryProvenance(out var entries, out var refusal)) return refusal!;

                var target = MakerVoiceStore.VoicePath(_store.Files, request.Lang, clip);
                var write = new MakerVoiceWrite { Path = target, Existed = _store.Files.Exists(target) };
                var files = new List<MakerCommitFile>();
                var backups = KeptBackups(state);
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var existing in state.VoiceFiles)
                {
                    // Studio's own published WAV is replaced or removed outright: the take that made it still holds it.
                    if (state.Publication is null || !string.Equals(existing, state.Publication.Path, StringComparison.Ordinal))
                    {
                        var backup = _store.Files.FreeBackupPath(existing, taken);
                        taken.Add(backup);
                        files.Add(_store.Target(backup, _store.Files.ReadBytes(existing) ?? new byte[0]));
                        backups.Add(new MakerClipBackup { Original = existing, Backup = backup });
                        write.MovedAside.Add(new KeyValuePair<string, string>(existing, backup));
                    }

                    if (!string.Equals(existing, target, StringComparison.OrdinalIgnoreCase)) files.Add(_store.DeleteTarget(existing));
                }

                var now = MakerProjectRevision.At(_store.Clock.Now);
                var wavFingerprint = MakerPackFiles.Fingerprint(bytes);
                var sha = MakerVoiceProvenance.Sha256(bytes);
                files.Add(_store.Target(target, bytes));
                entries.RemoveAll(e => e.Lang == request.Lang && e.Clip == clip);
                entries.Add(new MakerProvenanceEntry
                {
                    Lang = request.Lang, Clip = clip, Origin = take.Origin, StatementVersion = take.Consent.StatementVersion,
                    ConfirmedAt = take.Consent.ConfirmedAt!, Sha256 = sha, PublishedAt = now,
                });
                files.Add(_store.Target(MakerStudioPaths.Provenance, MakerVoiceProvenance.Write(entries)));
                var record = new MakerClipRecord
                {
                    Lang = request.Lang, Clip = clip, Revision = MakerProjectRevision.New(), LineId = request.Id, TakeId = take.Id,
                    TakeRevision = MakerProjectRevision.StableId(take.Id + "|" + wavFingerprint), Path = target,
                    WavFingerprint = wavFingerprint, Sha256 = sha, PublishedAt = now,
                };
                record.Backups.AddRange(backups);
                files.Add(_store.Target(MakerStudioPaths.Clip(request.Lang, clip), MakerClipStore.Write(record)));

                line.PublishedTakeId = take.Id;
                line.PublishedTakeRevision = record.TakeRevision;
                line.PublishedClip = clip;
                line.Revision = MakerProjectRevision.New();
                var saved = new MakerStudioSaved
                {
                    Revision = line.Revision, OperationId = request.OperationId, Saved = true, Live = false, Path = target,
                    DurationSec = Math.Round(duration, 3), PublishedTakeId = take.Id,
                    Note = "Published to " + target + "; the game uses it from the next restart unless it can be applied live.",
                };
                var result = Commit(request, fingerprint, "publish", header, request.Lang, request.Id, line, files, saved);
                if (!result.Ok) return result;
                _store.RememberAudio(target, wavFingerprint);
                result.Value!.VoiceChanged = true;
                result.Value.Clip = clip;
                result.Value.Write = write;
                result.Value.Bytes = bytes;
                return result;
            });
        }

        /// <summary>
        /// Stops the pack providing a dub for the clip. A Studio publication is deleted and whatever it moved aside is put
        /// back; a dub made outside Studio (a v1 upload, a hand-copied file) is moved aside to <c>_maker_backup/</c>, never
        /// deleted. Either way the clip's provenance entry and record go.
        /// </summary>
        public MakerResult<MakerPublishOutcome> Unpublish(MakerStudioUnpublishRequest request, string clip)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            var fingerprint = MakerOperationLedger.Fingerprint("unpublish", request.PackId, request.Lang, request.Id,
                request.ExpectedRevision, request.ExpectedClipRevision);
            return Run(request, request.Lang, request.Id, clip, request.ExpectedClipRevision, fingerprint, (header, line, state) =>
            {
                if (!TryProvenance(out var entries, out var refusal)) return refusal!;
                var publication = state.Publication;
                var write = new MakerVoiceWrite
                {
                    Path = publication?.Path ?? MakerVoiceStore.VoicePath(_store.Files, request.Lang, clip), Existed = state.VoiceFiles.Count > 0,
                };
                var files = new List<MakerCommitFile>();
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (publication != null)
                {
                    var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var backup in publication.Backups)
                    {
                        var bytes = _store.Files.ReadBytes(backup.Backup);
                        var free = string.Equals(backup.Original, publication.Path, StringComparison.OrdinalIgnoreCase) || !_store.Files.Exists(backup.Original);
                        if (bytes is null || !free || !restored.Add(backup.Original)) continue;
                        files.Add(_store.Target(backup.Original, bytes));
                        files.Add(_store.DeleteTarget(backup.Backup));
                        write.Restored.Add(backup.Original);
                    }

                    if (!restored.Contains(publication.Path))
                    {
                        files.Add(_store.DeleteTarget(publication.Path));
                        write.Deleted = true;
                    }
                }
                else
                {
                    // W3 parity gap b: a dub made outside Studio is removed by moving it aside, as v1 removal does.
                    foreach (var existing in state.VoiceFiles)
                    {
                        var backup = _store.Files.FreeBackupPath(existing, taken);
                        taken.Add(backup);
                        files.Add(_store.Target(backup, _store.Files.ReadBytes(existing) ?? new byte[0]));
                        files.Add(_store.DeleteTarget(existing));
                        write.MovedAside.Add(new KeyValuePair<string, string>(existing, backup));
                    }
                }

                var voiceChanged = files.Count > 0;
                if (entries.RemoveAll(e => e.Lang == request.Lang && e.Clip == clip) > 0)
                    files.Add(_store.Target(MakerStudioPaths.Provenance, MakerVoiceProvenance.Write(entries)));
                if (state.RecordPresent) files.Add(_store.DeleteTarget(MakerStudioPaths.Clip(request.Lang, clip)));
                var lineChanged = line.ExternalDrift || line.PublishedTakeId != null;
                line.PublishedTakeId = null;
                line.PublishedTakeRevision = null;
                line.PublishedClip = null;
                if (lineChanged || files.Count > 0) line.Revision = MakerProjectRevision.New();
                var saved = new MakerStudioSaved
                {
                    Revision = line.Revision, OperationId = request.OperationId, Saved = files.Count > 0 || lineChanged, Live = !voiceChanged,
                    Path = write.Path, PublishedTakeId = null,
                    Note = voiceChanged
                        ? "Unpublished; the pack no longer provides this clip's dub from the next restart unless it can be applied live."
                        : "Nothing to unpublish: this pack has no dub for " + clip + ".",
                };
                var result = Commit(request, fingerprint, "unpublish", header, request.Lang, request.Id, lineChanged || files.Count > 0 ? line : null, files, saved);
                if (!result.Ok) return result;
                result.Value!.VoiceChanged = voiceChanged;
                result.Value.Clip = clip;
                result.Value.Write = write;
                return result;
            });
        }

        /// <summary>The backups an earlier publication of this clip recorded, so unpublish can still put the originals back.</summary>
        private List<MakerClipBackup> KeptBackups(MakerClipState state)
        {
            if (state.Publication != null) return state.Publication.Backups.ToList();
            var text = state.RecordPresent ? _store.Files.ReadText(MakerStudioPaths.Clip(state.Lang, state.Clip), out _) : null;
            return text != null && MakerClipStore.TryRecord(text, out var stale) ? stale.Backups.ToList() : new List<MakerClipBackup>();
        }

        private bool TryProvenance(out List<MakerProvenanceEntry> entries, out MakerResult<MakerPublishOutcome>? refusal)
        {
            refusal = null;
            entries = new List<MakerProvenanceEntry>();
            var text = _store.ReadTextSnapshot(MakerStudioPaths.Provenance, out _, out _);
            if (text is null || MakerVoiceProvenance.TryRead(text, out entries, out var problem)) return true;
            refusal = Fail(MakerStudioErrorCodes.MetadataUnavailable, MakerStudioPaths.Provenance + " is unreadable (" + problem + "); fix or remove it before publishing");
            return false;
        }

        private MakerResult<MakerPublishOutcome> Run(MakerStudioMutation request, string lang, string id, string clip, string expectedClip,
            string fingerprint, Func<MakerProjectHeader, MakerLineDocument, MakerClipState, MakerResult<MakerPublishOutcome>> apply)
        {
            lock (_store.SyncRoot)
            {
                try
                {
                    MakerStudioPaths.Line(lang, id);
                    MakerStudioPaths.Clip(lang, clip);
                    MakerStudioPaths.RequireOperation(request.OperationId);
                    var recovered = _store.RecoverPending();
                    if (!recovered.Ok) return Fail(recovered.Error!, recovered.Message!, recovered.Details);
                    var checkedOp = _operations.Check(request.OperationId, fingerprint);
                    if (checkedOp.Kind == MakerOperationCheckKind.Replay) return Replay(checkedOp.Receipt!, request.OperationId);
                    if (checkedOp.Kind != MakerOperationCheckKind.New)
                        return Fail(checkedOp.Kind == MakerOperationCheckKind.Conflict ? MakerStudioErrorCodes.Conflict : MakerStudioErrorCodes.RecoveryRequired,
                            checkedOp.Message ?? "the operation cannot continue", checkedOp.Details);
                    var header = _store.ReadHeader();
                    if (!header.Ok) return Fail(header.Error!, header.Message!, header.Details);
                    var line = MakerVoiceDefaultStore.ReadLineResolved(_store, lang, id);
                    if (!line.Ok) return Fail(line.Error!, line.Message!, line.Details);
                    if (!string.Equals(request.ExpectedRevision, line.Value!.Revision, StringComparison.Ordinal))
                        return Fail(MakerStudioErrorCodes.Conflict, "the line changed; reload it");
                    var state = new MakerClipStore(_store).Read(lang, clip);
                    if (!string.Equals(expectedClip, state.Revision, StringComparison.Ordinal))
                        return Fail(MakerStudioErrorCodes.Conflict, "the clip changed (another line sharing it, or an edit outside Studio); reload it");
                    return apply(header.Value!, line.Value, state);
                }
                catch (ArgumentException ex) { return Fail(MakerErrorCodes.BadRequest, ex.Message); }
                catch (UnauthorizedAccessException ex) { return Fail(MakerStudioErrorCodes.MetadataUnavailable, ex.Message); }
                catch (IOException ex) { return Fail(MakerErrorCodes.Unavailable, "the pack's voice files could not be read or written: " + ex.Message); }
            }
        }

        private MakerResult<MakerPublishOutcome> Commit(MakerStudioMutation request, string fingerprint, string kind, MakerProjectHeader header,
            string lang, string id, MakerLineDocument? line, List<MakerCommitFile> files, MakerStudioSaved saved)
        {
            var receipt = MakerOperationLedger.Receipt(request.OperationId, fingerprint, kind, saved.Revision, saved.Saved, _store.Clock.Now, MakerStudioJson.Saved(saved));
            var targets = new List<MakerCommitFile>();
            var headerTarget = _store.HeaderTarget(header);
            if (headerTarget != null) targets.Add(headerTarget);
            targets.AddRange(files);
            if (line != null) targets.Add(_store.Target(MakerStudioPaths.Line(lang, id), MakerProjectStore.Utf8(MakerProjectJson.WriteLine(line))));
            targets.Add(_operations.ReceiptTarget(receipt));
            var committed = _store.Commit(request.OperationId, targets, kind: kind);
            if (!committed.Ok) return Fail(committed.Error!, committed.Message!, committed.Details);
            _operations.Prune();
            return MakerResult<MakerPublishOutcome>.Success(new MakerPublishOutcome { Saved = saved });
        }

        private MakerResult<MakerPublishOutcome> Replay(MakerOperationReceipt receipt, string operationId)
        {
            var r = receipt.Response;
            string? Text(string key) => r.TryGetValue(key, out var v) ? v as string : null;
            if (!(Text("revision") is string revision) || !(r.TryGetValue("saved", out var s) && s is bool saved)
                || !(r.TryGetValue("live", out var l) && l is bool live))
                return Fail(MakerStudioErrorCodes.RecoveryRequired, "the publication receipt is incomplete", _operations.RecoveryDetails(operationId));
            return MakerResult<MakerPublishOutcome>.Success(new MakerPublishOutcome
            {
                Replayed = true,
                Saved = new MakerStudioSaved
                {
                    Revision = revision, OperationId = operationId, Saved = saved, Live = live, Path = Text("path"),
                    DurationSec = r.TryGetValue("durationSec", out var d) ? d switch { double x => x, long y => y, _ => (double?)null } : null,
                    Note = Text("note") ?? string.Empty, PublishedTakeId = Text("publishedTakeId"), EffectivePackId = Text("effectivePackId"),
                },
            });
        }

        private static MakerResult<MakerPublishOutcome> Fail(string code, string message, MakerRecoveryDetails? details = null) =>
            MakerResult<MakerPublishOutcome>.Fail(code, message, details);
    }
}
