using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>
    /// D-148: one click labels every shipped dub that has no valid voice label (a hand-copied file, a v1 voice upload, an entry
    /// with an unknown origin, an old statement or other bytes) with the pack's voice default, in one recoverable commit of
    /// <c>voice-provenance.json</c>. Studio publications are never relabelled here: their label follows their take. The
    /// confirmation is the creator's click: the request must name the default's revision and origin as the page showed
    /// them and carry a confirmed <c>consent-1</c>; with no default set nothing is labelled (<c>consent_required</c>).
    /// Writes follow <see cref="MakerGlossaryStore.Save"/>: store lock, pending recovery, operation ledger, receipt, commit.
    /// </summary>
    public sealed class MakerVoiceLabelStore
    {
        internal const string Kind = "voice.label";
        private const int MaxPaths = 20;
        private readonly MakerProjectStore _store;
        private readonly MakerOperationLedger _operations;

        public MakerVoiceLabelStore(MakerProjectStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _operations = new MakerOperationLedger(store);
        }

        /// <summary>Null when the request may label: a known origin and a confirmed <c>consent-1</c>.</summary>
        internal static string? Validate(string? origin, MakerStudioConsent? consent)
        {
            if (!MakerTakeRules.ValidateOrigin(origin, out var originProblem)) return originProblem;
            if (!MakerTakeRules.ValidateConsent(consent, out var consentProblem)) return consentProblem;
            if (!consent!.Confirmed) return "consent.confirmed must be true: labelling is the creator's confirmation";
            return null;
        }

        public MakerResult<MakerStudioVoiceLabelled> Label(MakerStudioVoiceLabelRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            var validation = Validate(request.Origin, request.Consent);
            if (validation != null) return Fail(MakerErrorCodes.BadRequest, validation);
            lock (_store.SyncRoot)
            {
                try
                {
                    var recovered = _store.RecoverPending();
                    if (!recovered.Ok) return Fail(recovered.Error!, recovered.Message!, recovered.Details);
                    var fingerprint = MakerOperationLedger.Fingerprint(Kind, request.PackId, request.ExpectedRevision, request.Origin,
                        request.Consent.Confirmed ? "1" : "0", request.Consent.StatementVersion);
                    var checkedOperation = _operations.Check(request.OperationId, fingerprint);
                    if (checkedOperation.Kind == MakerOperationCheckKind.Replay) return Replay(checkedOperation.Receipt!, _operations.RecoveryDetails(request.OperationId));
                    if (checkedOperation.Kind != MakerOperationCheckKind.New)
                        return Fail(checkedOperation.Kind == MakerOperationCheckKind.Conflict ? MakerStudioErrorCodes.Conflict : MakerStudioErrorCodes.RecoveryRequired,
                            checkedOperation.Message ?? "the operation cannot continue", checkedOperation.Details);

                    var headerResult = _store.ReadHeader();
                    if (!headerResult.Ok) return Fail(headerResult.Error!, headerResult.Message!, headerResult.Details);
                    var defaults = new MakerVoiceDefaultStore(_store).ReadDocument();
                    if (!defaults.Ok) return Fail(defaults.Error!, defaults.Message!, defaults.Details);
                    var current = defaults.Value!;
                    // Never silent: no default, no label. The page sends the creator to set one first.
                    if (current.Origin is null || current.Consent is null || !current.Consent.Confirmed)
                        return Fail(MakerStudioErrorCodes.ConsentRequired, "set the pack's voice origin and consent first; nothing was labelled");
                    if (!string.Equals(current.Revision, request.ExpectedRevision, StringComparison.Ordinal)
                        || !string.Equals(current.Origin, request.Origin, StringComparison.Ordinal))
                        return Fail(MakerStudioErrorCodes.Conflict, "the pack's voice default changed; reload it before labelling");

                    var text = _store.ReadTextSnapshot(MakerStudioPaths.Provenance, out _, out _);
                    var entries = new List<MakerProvenanceEntry>();
                    if (text != null && !MakerVoiceProvenance.TryRead(text, out entries, out var problem))
                        return Fail(MakerStudioErrorCodes.MetadataUnavailable, MakerStudioPaths.Provenance + " is unreadable (" + problem + "); fix or remove it before labelling");

                    var scan = MakerVoiceLabels.Scan(_store.Files, MakerPackFileSet.Walk(_store.Files.Root), entries);
                    var targets = scan.In(MakerDubLabelState.Unlabelled).ToList();
                    var now = MakerProjectRevision.At(_store.Clock.Now);
                    foreach (var dub in targets)
                    {
                        entries.RemoveAll(e => e.Lang == dub.Lang && e.Clip == dub.Clip);
                        entries.Add(new MakerProvenanceEntry
                        {
                            Lang = dub.Lang, Clip = dub.Clip, Origin = current.Origin, StatementVersion = MakerStudioContract.ConsentVersion,
                            ConfirmedAt = now, Sha256 = dub.Sha256, PublishedAt = now,
                        });
                    }

                    var labelled = new MakerStudioVoiceLabelled
                    {
                        OperationId = request.OperationId, Revision = current.Revision, Origin = current.Origin,
                        Labelled = targets.Count, Paths = targets.Select(d => d.Path).Take(MaxPaths).ToList(),
                    };
                    var changed = targets.Count > 0;
                    var receipt = MakerOperationLedger.Receipt(request.OperationId, fingerprint, Kind, current.Revision, changed, _store.Clock.Now, MakerStudioJson.VoiceLabelled(labelled));
                    var files = new List<MakerCommitFile>();
                    var header = _store.HeaderTarget(headerResult.Value!);
                    if (header != null) files.Add(header);
                    if (changed) files.Add(_store.Target(MakerStudioPaths.Provenance, MakerVoiceProvenance.Write(entries)));
                    files.Add(_operations.ReceiptTarget(receipt));
                    var committed = _store.Commit(request.OperationId, files, kind: Kind);
                    if (!committed.Ok) return Fail(committed.Error!, committed.Message!, committed.Details);
                    _operations.Prune();
                    return MakerResult<MakerStudioVoiceLabelled>.Success(labelled);
                }
                catch (UnauthorizedAccessException ex) { return Fail(MakerStudioErrorCodes.MetadataUnavailable, ex.Message); }
                catch (IOException ex) { return Fail(MakerErrorCodes.Unavailable, "the pack's voice files could not be read: " + ex.Message); }
            }
        }

        private static MakerResult<MakerStudioVoiceLabelled> Replay(MakerOperationReceipt receipt, MakerRecoveryDetails details)
        {
            var r = receipt.Response;
            if (!(r.TryGetValue("operationId", out var op) && op is string operationId)
                || !(r.TryGetValue("revision", out var rev) && rev is string revision)
                || !(r.TryGetValue("origin", out var o) && o is string origin)
                || !(r.TryGetValue("labelled", out var n) && n is long count)
                || !(r.TryGetValue("paths", out var p) && p is IList<object?> paths) || paths.Any(x => !(x is string)))
                return Fail(MakerStudioErrorCodes.RecoveryRequired, "the voice label receipt is incomplete", details);
            return MakerResult<MakerStudioVoiceLabelled>.Success(new MakerStudioVoiceLabelled
            {
                OperationId = operationId, Revision = revision, Origin = origin, Labelled = (int)count, Paths = paths.Cast<string>().ToList(),
            });
        }

        private static MakerResult<MakerStudioVoiceLabelled> Fail(string code, string message, MakerRecoveryDetails? details = null) =>
            MakerResult<MakerStudioVoiceLabelled>.Fail(code, message, details);
    }
}
