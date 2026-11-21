using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>Versioned creator glossary reads and complete replacements.</summary>
    public sealed class MakerGlossaryStore
    {
        private const int MaxEntries = 200;
        private readonly MakerProjectStore _store;
        private readonly MakerOperationLedger _operations;

        public MakerGlossaryStore(MakerProjectStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _operations = new MakerOperationLedger(store);
        }

        public MakerResult<MakerStudioGlossary> Read(string lang)
        {
            var result = _store.ReadGlossary(lang);
            return result.Ok
                ? MakerResult<MakerStudioGlossary>.Success(ToWire(result.Value!))
                : MakerResult<MakerStudioGlossary>.Fail(result.Error!, result.Message!, result.Details);
        }

        public MakerResult<MakerStudioGlossary> Save(MakerStudioGlossaryRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            var entries = request.Entries ?? new MakerStudioGlossaryEntry[0];
            var validation = Validate(entries);
            if (validation != null) return MakerResult<MakerStudioGlossary>.Fail(MakerErrorCodes.BadRequest, validation);
            lock (_store.SyncRoot)
            {
                var recovered = _store.RecoverPending();
                if (!recovered.Ok) return MakerResult<MakerStudioGlossary>.Fail(recovered.Error!, recovered.Message!, recovered.Details);

                var entryFingerprint = string.Join("\n", entries.Select(e => e.Id + "\u001f" + e.SourceJa + "\u001f" + e.SourceEn + "\u001f" + e.Target + "\u001f" + e.Note));
                var fingerprint = MakerOperationLedger.Fingerprint("glossary", request.PackId, request.Lang, request.ExpectedRevision, entryFingerprint);
                var checkedOperation = _operations.Check(request.OperationId, fingerprint);
                if (checkedOperation.Kind == MakerOperationCheckKind.Replay) return Replay(checkedOperation.Receipt!, _operations.RecoveryDetails(request.OperationId));
                if (checkedOperation.Kind != MakerOperationCheckKind.New)
                {
                    return MakerResult<MakerStudioGlossary>.Fail(
                        checkedOperation.Kind == MakerOperationCheckKind.Conflict ? MakerStudioErrorCodes.Conflict : MakerStudioErrorCodes.RecoveryRequired,
                        checkedOperation.Message ?? "the operation cannot continue", checkedOperation.Details);
                }

                var headerResult = _store.ReadHeader();
                if (!headerResult.Ok) return MakerResult<MakerStudioGlossary>.Fail(headerResult.Error!, headerResult.Message!, headerResult.Details);
                var currentResult = _store.ReadGlossary(request.Lang);
                if (!currentResult.Ok) return MakerResult<MakerStudioGlossary>.Fail(currentResult.Error!, currentResult.Message!, currentResult.Details);
                var current = currentResult.Value!;
                if (!string.Equals(current.Revision, request.ExpectedRevision, StringComparison.Ordinal))
                {
                    return MakerResult<MakerStudioGlossary>.Fail(MakerStudioErrorCodes.Conflict, "the glossary changed; reload it before saving");
                }

                var changed = !Same(current.Entries, entries);
                if (changed)
                {
                    current.Revision = MakerProjectRevision.New();
                    current.Entries.Clear();
                    current.Entries.AddRange(entries.Select(Copy));
                }

                var wire = ToWire(current);
                var response = Response(wire);
                var receipt = MakerOperationLedger.Receipt(request.OperationId, fingerprint, "glossary", current.Revision, changed, _store.Clock.Now, response);
                var targets = new List<MakerCommitFile>();
                var header = _store.HeaderTarget(headerResult.Value!);
                if (header != null) targets.Add(header);
                if (changed)
                {
                    var bytes = MakerProjectStore.Utf8(MakerProjectJson.WriteGlossary(current));
                    if (bytes.Length > MakerDefaults.MaxJsonBytes)
                    {
                        return MakerResult<MakerStudioGlossary>.Fail(MakerErrorCodes.TooLarge, "the encoded glossary may be at most 64 KiB");
                    }

                    targets.Add(_store.Target(MakerStudioPaths.Glossary(request.Lang), bytes));
                }

                targets.Add(_operations.ReceiptTarget(receipt));
                var committed = _store.Commit(request.OperationId, targets, kind: "glossary");
                if (!committed.Ok) return MakerResult<MakerStudioGlossary>.Fail(committed.Error!, committed.Message!, committed.Details);
                _operations.Prune();
                return MakerResult<MakerStudioGlossary>.Success(wire);
            }
        }

        internal static string? Validate(IReadOnlyList<MakerStudioGlossaryEntry>? entries)
        {
            if (entries is null) return null; // no list is an empty list, as Save treats it
            if (entries.Count > MaxEntries) return "a glossary may have at most 200 entries";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 64
                    || entry.Id.Any(c => !(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')))
                {
                    return "each glossary id must be 1-64 letters, numbers, dot, underscore or hyphen";
                }

                if (!ids.Add(entry.Id)) return "glossary id '" + entry.Id + "' is repeated";
                if (string.IsNullOrWhiteSpace(entry.Target)
                    || !Editor.MakerEditRules.TryCodePointCount(entry.Target, out var targetPoints)
                    || targetPoints > 2000) return "each glossary target must be 1-2000 characters";
                if ((entry.SourceJa != null && (!Editor.MakerEditRules.TryCodePointCount(entry.SourceJa, out var jaPoints) || jaPoints > 2000))
                    || (entry.SourceEn != null && (!Editor.MakerEditRules.TryCodePointCount(entry.SourceEn, out var enPoints) || enPoints > 2000))
                    || (entry.Note != null && (!Editor.MakerEditRules.TryCodePointCount(entry.Note, out var notePoints) || notePoints > 2000)))
                    return "glossary source text and notes may be at most 2000 characters";
            }

            return null;
        }

        private static bool Same(IReadOnlyList<MakerStudioGlossaryEntry> a, IReadOnlyList<MakerStudioGlossaryEntry> b) =>
            a.Count == b.Count && a.Zip(b, (x, y) => x.Id == y.Id && x.SourceJa == y.SourceJa && x.SourceEn == y.SourceEn && x.Target == y.Target && x.Note == y.Note).All(x => x);

        private static MakerStudioGlossaryEntry Copy(MakerStudioGlossaryEntry e) => new MakerStudioGlossaryEntry
        {
            Id = e.Id, SourceJa = e.SourceJa, SourceEn = e.SourceEn, Target = e.Target, Note = e.Note,
        };

        private static MakerStudioGlossary ToWire(MakerGlossaryDocument value) => new MakerStudioGlossary
        {
            Revision = value.Revision,
            Entries = value.Entries.Select(Copy).ToArray(),
        };

        private static JsonObject Response(MakerStudioGlossary value)
        {
            var entries = new List<object?>();
            foreach (var e in value.Entries)
            {
                entries.Add(new JsonObject { ["id"] = e.Id, ["sourceJa"] = e.SourceJa, ["sourceEn"] = e.SourceEn, ["target"] = e.Target, ["note"] = e.Note });
            }

            return new JsonObject { ["revision"] = value.Revision, ["entries"] = entries };
        }

        private static MakerResult<MakerStudioGlossary> Replay(MakerOperationReceipt receipt, MakerRecoveryDetails details)
        {
            if (!(receipt.Response.TryGetValue("revision", out var revision) && revision is string r)
                || !(receipt.Response.TryGetValue("entries", out var node) && node is IList<object?> entries))
                return MakerResult<MakerStudioGlossary>.Fail(MakerStudioErrorCodes.RecoveryRequired, "the glossary receipt is incomplete", details);
            var found = new List<MakerStudioGlossaryEntry>();
            foreach (var raw in entries)
            {
                if (!(raw is JsonObject e) || !(e["id"] is string id) || !(e["target"] is string target))
                    return MakerResult<MakerStudioGlossary>.Fail(MakerStudioErrorCodes.RecoveryRequired, "the glossary receipt is incomplete", details);
                found.Add(new MakerStudioGlossaryEntry { Id = id, SourceJa = e["sourceJa"] as string, SourceEn = e["sourceEn"] as string, Target = target, Note = e["note"] as string });
            }

            return MakerResult<MakerStudioGlossary>.Success(new MakerStudioGlossary { Revision = r, Entries = found });
        }
    }
}
