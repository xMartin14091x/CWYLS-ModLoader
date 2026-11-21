using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    public sealed class MakerSubtitleProjectWrite
    {
        public MakerStudioSaved Saved { get; set; } = new MakerStudioSaved();
        public MakerSubtitleWrite FileWrite { get; set; } = new MakerSubtitleWrite();
        public string? HistoryRevisionId { get; set; }
        public bool Replayed { get; set; }
    }

    /// <summary>Bounded subtitle revisions, guarded saves and revert through recoverable multi-file commits.</summary>
    public sealed class MakerHistoryStore
    {
        private readonly MakerProjectStore _store;
        private readonly MakerOperationLedger _operations;

        public MakerHistoryStore(MakerProjectStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _operations = new MakerOperationLedger(store);
        }

        public MakerResult<MakerStudioHistoryPage> Read(string lang, string id, int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > MakerStudioContract.SubtitleRevisions)
                return MakerResult<MakerStudioHistoryPage>.Fail(MakerErrorCodes.BadRequest, "history offset must be 0 or more and limit 1-20");
            var line = _store.ReadLine(lang, id);
            if (!line.Ok) return MakerResult<MakerStudioHistoryPage>.Fail(line.Error!, line.Message!, line.Details);
            var items = line.Value!.History.Skip(offset).Take(limit).Select(h => new MakerStudioHistoryItem
            {
                RevisionId = h.RevisionId, CreatedAt = h.CreatedAt, Text = h.Text, Source = h.Source,
            }).ToArray();
            return MakerResult<MakerStudioHistoryPage>.Success(new MakerStudioHistoryPage
            {
                Revision = line.Value.Revision,
                Total = line.Value.History.Count,
                Offset = offset,
                Items = items,
                HistoryGap = line.Value.HistoryGap,
            });
        }

        public MakerResult<MakerSubtitleProjectWrite> Save(MakerStudioSubtitleRequest request) =>
            Mutate(request, request.Text, "studio", "subtitle", null);

        public MakerResult<MakerSubtitleProjectWrite> Revert(MakerStudioRevertRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            lock (_store.SyncRoot)
            {
                var recovery = _store.RecoverPending();
                if (!recovery.Ok) return MakerResult<MakerSubtitleProjectWrite>.Fail(recovery.Error!, recovery.Message!, recovery.Details);
                var fingerprint = MakerOperationLedger.Fingerprint("revert", request.PackId, request.Lang,
                    request.Id, request.ExpectedRevision, request.RevisionId);
                var replay = _operations.Check(request.OperationId, fingerprint);
                if (replay.Kind == MakerOperationCheckKind.Replay) return Replay(replay.Receipt!, _operations.RecoveryDetails(request.OperationId));
                if (replay.Kind != MakerOperationCheckKind.New)
                    return MakerResult<MakerSubtitleProjectWrite>.Fail(
                        replay.Kind == MakerOperationCheckKind.Conflict ? MakerStudioErrorCodes.Conflict : MakerStudioErrorCodes.RecoveryRequired,
                        replay.Message!, replay.Details);
                var line = _store.ReadLine(request.Lang, request.Id);
                if (!line.Ok) return MakerResult<MakerSubtitleProjectWrite>.Fail(line.Error!, line.Message!, line.Details);
                var revision = line.Value!.History.FirstOrDefault(h => h.RevisionId == request.RevisionId);
                if (revision is null)
                    return MakerResult<MakerSubtitleProjectWrite>.Fail(MakerErrorCodes.NotFound, "subtitle revision is no longer retained");
                var save = new MakerStudioSubtitleRequest
                {
                    PackId = request.PackId, Lang = request.Lang, Id = request.Id,
                    ExpectedRevision = request.ExpectedRevision, OperationId = request.OperationId, Text = revision.Text,
                };
                return Mutate(save, revision.Text, "revert", "revert", fingerprint);
            }
        }

        /// <summary>Best-effort history for the compatible v1 route. Never throws or changes the v1 result.</summary>
        public void CaptureV1(string lang, string id, string? previous, string? current)
        {
            try
            {
                if (CwyLs.ModLoader.Core.Packs.SafeFileNames.ProblemWith(lang) != null
                    || CwyLs.ModLoader.Core.Packs.SafeFileNames.ProblemWith(id) != null) return;
                lock (_store.SyncRoot)
                {
                    // Optional v1 capture never rolls a v2 intent over a just-completed v1 write.
                    if (_store.Files.ListFiles(MakerStudioPaths.Transactions).Any(p => p.EndsWith(".pending.json", StringComparison.Ordinal))) return;
                    var header = _store.ReadHeader();
                    var linePath = MakerStudioPaths.Line(lang, id);
                    var hadMetadata = _store.Files.Exists(linePath);
                    var lineResult = _store.ReadBeforeV1(lang, id, previous);
                    if (!header.Ok || !lineResult.Ok) return;
                    var line = lineResult.Value!;
                    var previousFingerprint = _store.SubtitleFingerprint(previous);
                    line.HistoryGap = hadMetadata
                        && (line.HistoryGap || !string.Equals(line.SubtitleFingerprint, previousFingerprint, StringComparison.Ordinal));
                    if (!hadMetadata) line.SubtitleFingerprint = previousFingerprint;
                    if (line.History.Count == 0) AddHistory(line, previous, "baseline");
                    AddHistory(line, current, "v1");
                    line.SubtitleFingerprint = _store.SubtitleFingerprint(current);
                    line.Revision = MakerProjectRevision.New();
                    Trim(line.History);
                    var targets = new List<MakerCommitFile>();
                    var headerTarget = _store.HeaderTarget(header.Value!);
                    if (headerTarget != null) targets.Add(headerTarget);
                    targets.Add(_store.Target(linePath, MakerProjectStore.Utf8(MakerProjectJson.WriteLine(line))));
                    // Best effort only: no pending marker can survive a failed v1 capture.
                    if (_store.Files.BytesUnder(MakerStudioPaths.Root) + targets.Sum(t => (long)t.Bytes.Length * 2) > _store.Quota) return;
                    if (targets.Any(t => _store.FingerprintOf(t.Path) != t.BeforeFingerprint)) return;
                    var capture = MakerProjectRevision.New();
                    for (var i = 0; i < targets.Count; i++)
                    {
                        var target = targets[i];
                        var swap = MakerStudioPaths.Transactions + "/" + capture + "." + i + ".swap";
                        if (!_store.Files.WriteChecked(target.Path, target.Bytes, target.BeforeFingerprint, swap)) return;
                        _store.RememberCommitted(target);
                        if (_store.Files.Exists(swap)) _store.Files.Delete(swap);
                    }
                }
            }
            catch
            {
                // Compatibility rule: creator metadata can never turn a successful v1 subtitle save into failure.
            }
        }

        private MakerResult<MakerSubtitleProjectWrite> Mutate(
            MakerStudioSubtitleRequest request, string? text, string source, string operationKind, string? requestFingerprint)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            lock (_store.SyncRoot)
            {
                if (text != null && !MakerEditRules.IsSubtitleText(text, out var textProblem))
                    return MakerResult<MakerSubtitleProjectWrite>.Fail(MakerErrorCodes.BadRequest, textProblem!);
                var recovered = _store.RecoverPending();
                if (!recovered.Ok) return MakerResult<MakerSubtitleProjectWrite>.Fail(recovered.Error!, recovered.Message!, recovered.Details);
                var fingerprint = requestFingerprint ?? MakerOperationLedger.Fingerprint(
                    operationKind, request.PackId, request.Lang, request.Id, request.ExpectedRevision, text);
                var checkedOperation = _operations.Check(request.OperationId, fingerprint);
                if (checkedOperation.Kind == MakerOperationCheckKind.Replay) return Replay(checkedOperation.Receipt!, _operations.RecoveryDetails(request.OperationId));
                if (checkedOperation.Kind != MakerOperationCheckKind.New)
                {
                    return MakerResult<MakerSubtitleProjectWrite>.Fail(
                        checkedOperation.Kind == MakerOperationCheckKind.Conflict ? MakerStudioErrorCodes.Conflict : MakerStudioErrorCodes.RecoveryRequired,
                        checkedOperation.Message ?? "the operation cannot continue", checkedOperation.Details);
                }

                var header = _store.ReadHeader();
                if (!header.Ok) return MakerResult<MakerSubtitleProjectWrite>.Fail(header.Error!, header.Message!, header.Details);
                var lineResult = _store.ReadLine(request.Lang, request.Id);
                if (!lineResult.Ok) return MakerResult<MakerSubtitleProjectWrite>.Fail(lineResult.Error!, lineResult.Message!, lineResult.Details);
                var line = lineResult.Value!;
                if (!string.Equals(request.ExpectedRevision, line.Revision, StringComparison.Ordinal))
                    return MakerResult<MakerSubtitleProjectWrite>.Fail(MakerStudioErrorCodes.Conflict, "the line changed; reload it before saving the subtitle");

                var tablePath = MakerEditRules.SubtitlePath(request.Lang);
                var existing = _store.ReadTextSnapshot(tablePath, out var existingBytes, out var bom);
                var edit = MakerSubtitleFile.Apply(existing, request.Id, text);
                if (!edit.Ok)
                    return MakerResult<MakerSubtitleProjectWrite>.Fail(MakerErrorCodes.NotReady, edit.Error ?? "the subtitle table cannot be edited");
                if (!string.Equals(line.SubtitleFingerprint, _store.SubtitleFingerprint(edit.Previous), StringComparison.Ordinal))
                    return MakerResult<MakerSubtitleProjectWrite>.Fail(MakerStudioErrorCodes.Conflict, "the subtitle changed; reload it before saving");
                var changed = edit.Changed || source == "revert" || line.ExternalDrift;
                var write = new MakerSubtitleWrite
                {
                    Path = tablePath, Ok = true, Written = edit.Changed, Previous = edit.Previous,
                    Current = changed ? text : edit.Previous,
                };

                if (changed)
                {
                    if (line.History.Count == 0) AddHistory(line, edit.Previous, "baseline");
                    if (edit.Changed || source == "revert") AddHistory(line, text, source);
                    Trim(line.History);
                    line.SubtitleFingerprint = _store.SubtitleFingerprint(text);
                    line.Revision = MakerProjectRevision.New();
                }

                var saved = new MakerStudioSaved
                {
                    Revision = line.Revision, OperationId = request.OperationId, Saved = changed,
                    Live = false, Path = tablePath, Note = changed ? "Saved to the pack; live update is pending." : "Unchanged.",
                };
                var response = Response(saved);
                var receipt = MakerOperationLedger.Receipt(request.OperationId, fingerprint, operationKind, line.Revision, changed, _store.Clock.Now, response);
                var targets = new List<MakerCommitFile>();
                var headerTarget = _store.HeaderTarget(header.Value!);
                if (headerTarget != null) targets.Add(headerTarget);
                if (changed)
                {
                    if (existingBytes != null)
                    {
                        var backup = MakerEditRules.BackupPathOf(tablePath);
                        if (!_store.Files.Exists(backup))
                        {
                            write.SafetyCopy = backup;
                            targets.Add(_store.Target(backup, existingBytes, null));
                        }
                    }

                    if (edit.Changed) targets.Add(_store.Target(tablePath, Encode(edit.Text!, bom),
                        existingBytes is null ? null : MakerPackFiles.Fingerprint(existingBytes)));
                    targets.Add(_store.Target(MakerStudioPaths.Line(request.Lang, request.Id), MakerProjectStore.Utf8(MakerProjectJson.WriteLine(line))));
                }

                targets.Add(_operations.ReceiptTarget(receipt));
                var committed = _store.Commit(request.OperationId, targets, kind: operationKind);
                if (!committed.Ok) return MakerResult<MakerSubtitleProjectWrite>.Fail(committed.Error!, committed.Message!, committed.Details);
                _operations.Prune();
                return MakerResult<MakerSubtitleProjectWrite>.Success(new MakerSubtitleProjectWrite
                {
                    Saved = saved,
                    FileWrite = write,
                    HistoryRevisionId = changed ? line.History.Last().RevisionId : null,
                });
            }
        }

        private void AddHistory(MakerLineDocument line, string? text, string source) => line.History.Add(new MakerHistoryRevision
        {
            RevisionId = MakerProjectRevision.New(), CreatedAt = MakerProjectRevision.At(_store.Clock.Now), Text = text, Source = source,
        });

        private static void Trim(List<MakerHistoryRevision> history)
        {
            while (history.Count > MakerStudioContract.SubtitleRevisions) history.RemoveAt(0);
        }

        private static byte[] Encode(string text, bool bom)
        {
            var body = new UTF8Encoding(false).GetBytes(text);
            if (!bom) return body;
            var all = new byte[body.Length + 3]; all[0] = 0xEF; all[1] = 0xBB; all[2] = 0xBF;
            Buffer.BlockCopy(body, 0, all, 3, body.Length); return all;
        }

        private static JsonObject Response(MakerStudioSaved value) => new JsonObject
        {
            ["revision"] = value.Revision, ["operationId"] = value.OperationId, ["saved"] = value.Saved,
            ["live"] = value.Live, ["path"] = value.Path, ["durationSec"] = value.DurationSec,
            ["note"] = value.Note, ["publishedTakeId"] = value.PublishedTakeId,
            ["effectivePackId"] = value.EffectivePackId, ["restartRequired"] = value.RestartRequired,
        };

        private static MakerResult<MakerSubtitleProjectWrite> Replay(MakerOperationReceipt receipt, MakerRecoveryDetails details)
        {
            var o = receipt.Response;
            if (!(o["revision"] is string revision) || !(o["operationId"] is string operationId)
                || !(o["saved"] is bool saved) || !(o["live"] is bool live) || !(o["note"] is string note)
                || !(o["restartRequired"] is bool restart))
                return MakerResult<MakerSubtitleProjectWrite>.Fail(MakerStudioErrorCodes.RecoveryRequired, "the subtitle receipt is incomplete", details);
            return MakerResult<MakerSubtitleProjectWrite>.Success(new MakerSubtitleProjectWrite
            {
                Replayed = true,
                Saved = new MakerStudioSaved
                {
                    Revision = revision, OperationId = operationId, Saved = saved, Live = live,
                    Path = o["path"] as string, DurationSec = o["durationSec"] as double?, Note = note,
                    PublishedTakeId = o["publishedTakeId"] as string, EffectivePackId = o["effectivePackId"] as string,
                    RestartRequired = restart,
                },
            });
        }
    }
}
