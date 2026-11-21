using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>Persistent per-line workflow and star mutations.</summary>
    public sealed class MakerWorkflowStore
    {
        private readonly MakerProjectStore _store;
        private readonly MakerOperationLedger _operations;

        public MakerWorkflowStore(MakerProjectStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _operations = new MakerOperationLedger(store);
        }

        public MakerResult<MakerStudioWorkflowSaved> Update(MakerStudioWorkflowRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            lock (_store.SyncRoot)
            {
                var recovered = _store.RecoverPending();
                if (!recovered.Ok)
                {
                    return MakerResult<MakerStudioWorkflowSaved>.Fail(recovered.Error!, recovered.Message!, recovered.Details);
                }

                if (request.Status is null && !request.Starred.HasValue)
                {
                    return MakerResult<MakerStudioWorkflowSaved>.Fail(MakerErrorCodes.BadRequest, "status or starred is required");
                }

                if (request.Status != null && !IsStatus(request.Status))
                {
                    return MakerResult<MakerStudioWorkflowSaved>.Fail(MakerErrorCodes.BadRequest, "status must be todo, draft, done or needs-retake");
                }

                var fingerprint = MakerOperationLedger.Fingerprint(
                    "workflow", request.PackId, request.Lang, request.Id, request.ExpectedRevision,
                    request.Status ?? "<omitted>", request.Starred.HasValue ? request.Starred.Value.ToString() : "<omitted>");
                var checkedOperation = _operations.Check(request.OperationId, fingerprint);
                if (checkedOperation.Kind == MakerOperationCheckKind.Replay)
                {
                    return Replay(checkedOperation.Receipt!, _operations.RecoveryDetails(request.OperationId));
                }

                if (checkedOperation.Kind != MakerOperationCheckKind.New)
                {
                    return MakerResult<MakerStudioWorkflowSaved>.Fail(
                        checkedOperation.Kind == MakerOperationCheckKind.Conflict ? MakerStudioErrorCodes.Conflict : MakerStudioErrorCodes.RecoveryRequired,
                        checkedOperation.Message ?? "the operation cannot continue", checkedOperation.Details);
                }

                var headerResult = _store.ReadHeader();
                if (!headerResult.Ok) return MakerResult<MakerStudioWorkflowSaved>.Fail(headerResult.Error!, headerResult.Message!, headerResult.Details);
                var lineResult = _store.ReadLine(request.Lang, request.Id);
                if (!lineResult.Ok) return MakerResult<MakerStudioWorkflowSaved>.Fail(lineResult.Error!, lineResult.Message!, lineResult.Details);
                var line = lineResult.Value!;
                if (!string.Equals(request.ExpectedRevision, line.Revision, StringComparison.Ordinal))
                {
                    return MakerResult<MakerStudioWorkflowSaved>.Fail(MakerStudioErrorCodes.Conflict, "the line changed; reload it before saving workflow");
                }

                var status = request.Status ?? line.Workflow.Status;
                var starred = request.Starred ?? line.Workflow.Starred;
                var changed = line.ExternalDrift || !string.Equals(status, line.Workflow.Status, StringComparison.Ordinal) || starred != line.Workflow.Starred;
                if (changed)
                {
                    if (!_store.Files.Exists(MakerStudioPaths.Line(request.Lang, request.Id)))
                        line.History.Add(new MakerHistoryRevision { RevisionId = MakerProjectRevision.New(),
                            CreatedAt = MakerProjectRevision.At(_store.Clock.Now), Text = _store.CurrentSubtitle(request.Lang, request.Id), Source = "baseline" });
                    line.Workflow = new MakerStudioWorkflow { Status = status, Starred = starred };
                    line.Revision = MakerProjectRevision.New();
                }

                var saved = new MakerStudioWorkflowSaved
                {
                    Revision = line.Revision,
                    Workflow = new MakerStudioWorkflow { Status = line.Workflow.Status, Starred = line.Workflow.Starred },
                };
                var response = Response(saved);
                var receipt = MakerOperationLedger.Receipt(
                    request.OperationId, fingerprint, "workflow", line.Revision, changed, _store.Clock.Now, response);
                var targets = new List<MakerCommitFile>();
                var header = _store.HeaderTarget(headerResult.Value!);
                if (header != null) targets.Add(header);
                if (changed)
                {
                    targets.Add(_store.Target(MakerStudioPaths.Line(request.Lang, request.Id), MakerProjectStore.Utf8(MakerProjectJson.WriteLine(line))));
                }

                targets.Add(_operations.ReceiptTarget(receipt));
                var committed = _store.Commit(request.OperationId, targets, kind: "workflow");
                if (!committed.Ok)
                {
                    return MakerResult<MakerStudioWorkflowSaved>.Fail(committed.Error!, committed.Message!, committed.Details);
                }

                _operations.Prune();
                return MakerResult<MakerStudioWorkflowSaved>.Success(saved);
            }
        }

        private static bool IsStatus(string status) =>
            status == "todo" || status == "draft" || status == "done" || status == "needs-retake";

        private static JsonObject Response(MakerStudioWorkflowSaved saved) => new JsonObject
        {
            ["revision"] = saved.Revision,
            ["workflow"] = new JsonObject
            {
                ["status"] = saved.Workflow.Status,
                ["starred"] = saved.Workflow.Starred,
            },
        };

        private static MakerResult<MakerStudioWorkflowSaved> Replay(MakerOperationReceipt receipt, MakerRecoveryDetails details)
        {
            if (!(receipt.Response.TryGetValue("revision", out var revision) && revision is string r)
                || !(receipt.Response.TryGetValue("workflow", out var workflow) && workflow is JsonObject w)
                || !(w.TryGetValue("status", out var status) && status is string s)
                || !(w.TryGetValue("starred", out var starred) && starred is bool b))
            {
                return MakerResult<MakerStudioWorkflowSaved>.Fail(MakerStudioErrorCodes.RecoveryRequired, "the workflow operation receipt is incomplete", details);
            }

            return MakerResult<MakerStudioWorkflowSaved>.Success(new MakerStudioWorkflowSaved
            {
                Revision = r,
                Workflow = new MakerStudioWorkflow { Status = s, Starred = b },
            });
        }
    }
}
