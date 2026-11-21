using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>Preview and keep-current resolution. All preservation and retirement is one recoverable commit.</summary>
    public sealed class MakerRecoveryStore
    {
        private const long MaxReadBytes = 64L * 1024 * 1024;
        private readonly MakerProjectStore _store;
        private readonly Action<string>? _boundary;
        private MakerPackFiles Files => _store.Files;

        public MakerRecoveryStore(MakerProjectStore store, Action<string>? boundary = null)
        { _store = store; _boundary = boundary; }

        public MakerResult<MakerRecoveryPreview> Preview(string blockedOperationId) =>
            Guard(blockedOperationId, () =>
            {
                SettlePendingResolutions(blockedOperationId, null);
                var snapshot = Capture(blockedOperationId);
                return new MakerRecoveryPreview
                {
                    BlockedOperationId = blockedOperationId, RecoveryRevision = snapshot.Revision,
                    Resources = snapshot.Rows.Select(r => new MakerRecoveryResource
                    {
                        Resource = r.Path, CurrentExists = r.Current != null, IntendedExists = r.Intended != null,
                        CurrentFingerprint = Hash(r.Current), IntendedFingerprint = Hash(r.Intended),
                    }).ToArray(),
                };
            });

        public MakerResult<MakerRecoveryResolved> Resolve(MakerRecoveryRequest request) =>
            Guard(request.BlockedOperationId, () =>
            {
                MakerStudioPaths.RequireOperation(request.OperationId);
                if (request.OperationId == request.BlockedOperationId || request.Strategy != "keep-current"
                    || !MakerStudioPaths.IsOperationId(request.ExpectedRecoveryRevision))
                    throw Failure(MakerErrorCodes.BadRequest, "Expected a preview token, a new operationId and strategy keep-current.");
                var fingerprint = MakerOperationLedger.Fingerprint("recovery.resolve", request.PackId,
                    request.BlockedOperationId, request.ExpectedRecoveryRevision, request.Strategy);
                // Check this operation before consulting the now-retired original intent.
                if (Files.Exists(MakerStudioPaths.Pending(request.OperationId)))
                {
                    if (!JsonReader.TryParse(Files.ReadText(MakerStudioPaths.Pending(request.OperationId), out _)!, out var parsedOwn, out _)
                        || !(parsedOwn is JsonObject own))
                        throw Failure(MakerStudioErrorCodes.RecoveryRequired, "Unreadable resolution journal.");
                    if (Text(own, "resolves") != request.BlockedOperationId || Text(own, "requestFingerprint") != fingerprint)
                        throw Failure(MakerStudioErrorCodes.Conflict, "operationId belongs to another request.");
                    Require(new MakerCommit(Files, _store.Quota, _boundary).Recover(request.OperationId));
                }
                var replay = Receipt(request.OperationId, fingerprint);
                if (replay != null) return replay;
                SettlePendingResolutions(request.BlockedOperationId, request.OperationId);
                var snapshot = Capture(request.BlockedOperationId);
                if (snapshot.Revision != request.ExpectedRecoveryRevision)
                    throw Failure(MakerStudioErrorCodes.Conflict, "The recovery preview changed; refetch it.");
                var archive = Archive(request.BlockedOperationId, request.OperationId);
                if (Directory.Exists(Files.Resolve(archive)))
                    throw Failure(MakerStudioErrorCodes.Conflict, "This operationId already has an archive.");

                var result = new MakerRecoveryResolved
                {
                    Resolved = true, OperationId = request.OperationId, BlockedOperationId = request.BlockedOperationId,
                    Revision = MakerProjectRevision.New(), ArchivePath = archive, Live = false,
                };
                var targets = new List<MakerCommitFile>();
                Add(targets, archive + "/intent.json", snapshot.Intent);
                var entries = new List<object?>();
                for (var i = 0; i < snapshot.Rows.Count; i++)
                {
                    var row = snapshot.Rows[i];
                    if (row.Current != null) Add(targets, archive + "/" + i + ".current.bin", row.Current);
                    if (row.Intended != null) Add(targets, archive + "/" + i + ".intended.bin", row.Intended);
                    if (row.Swap != null) Add(targets, archive + "/" + i + ".before.bin", row.Swap);
                    entries.Add(new JsonObject
                    {
                        ["resource"] = row.Path, ["beforeFingerprint"] = row.Before,
                        ["currentExists"] = row.Current != null, ["currentFingerprint"] = Hash(row.Current),
                        ["intendedExists"] = row.Intended != null, ["intendedFingerprint"] = Hash(row.Intended),
                        ["displacedExists"] = row.Swap != null, ["displacedFingerprint"] = Hash(row.Swap),
                    });
                }
                Add(targets, archive + "/manifest.json", Bytes(new JsonObject
                {
                    ["schema"] = 1, ["blockedOperationId"] = request.BlockedOperationId,
                    ["operationId"] = request.OperationId, ["recoveryRevision"] = snapshot.Revision,
                    ["strategy"] = request.Strategy, ["resources"] = entries,
                }));
                // Archive targets precede every metadata edit and intent retirement.
                foreach (var row in snapshot.Rows)
                {
                    var reconciled = Reconcile(row, result.Revision);
                    targets.Add(new MakerCommitFile { Path = row.Path, BeforeFingerprint = Hash(row.Current),
                        Bytes = reconciled ?? new byte[0], CheckOnly = reconciled is null });
                }
                var receipt = MakerOperationLedger.Receipt(request.OperationId, fingerprint, "recovery.resolve",
                    result.Revision, true, _store.Clock.Now, MakerStudioJson.RecoveryResolved(result));
                var receiptTarget = new MakerOperationLedger(_store).ReceiptTarget(receipt);
                receiptTarget.BeforeFingerprint = null; // Another process must not claim this retry key mid-preview.
                targets.Add(receiptTarget);
                foreach (var row in snapshot.Rows.Where(r => r.Index >= 0))
                {
                    targets.Add(new MakerCommitFile { Path = MakerStudioPaths.Stage(request.BlockedOperationId, row.Index),
                        BeforeFingerprint = Hash(row.Stage), Delete = true });
                    targets.Add(new MakerCommitFile { Path = Swap(request.BlockedOperationId, row.Index),
                        BeforeFingerprint = Hash(row.Swap), Delete = true });
                }
                targets.Add(new MakerCommitFile { Path = MakerStudioPaths.Pending(request.BlockedOperationId),
                    BeforeFingerprint = Hash(snapshot.Intent), Delete = true });
                Require(new MakerCommit(Files, "recovery.resolve", _store.Quota, _boundary, request.BlockedOperationId, fingerprint)
                    .Execute(request.OperationId, targets));
                new MakerOperationLedger(_store).Prune();
                return result;
            });

        /// <summary>
        /// Finishes or undoes any other interrupted resolution of <paramref name="blocked"/> (recover on access, as reads
        /// do) before this request captures it. When that finishes the blocked operation's own retirement, the request
        /// answers 409 <c>conflict</c> (refetch) instead of a 404 for an operation the client was just told is blocked.
        /// </summary>
        private void SettlePendingResolutions(string blocked, string? currentOperationId)
        {
            if (!_store.RecoveryWritable) return;
            var blockedWasPending = Files.Exists(MakerStudioPaths.Pending(blocked));
            foreach (var path in Files.ListFiles(MakerStudioPaths.Transactions))
            {
                if (!path.EndsWith(".pending.json", StringComparison.Ordinal)) continue;
                var name = Path.GetFileName(path);
                var other = name.Substring(0, name.Length - ".pending.json".Length);
                if (other == blocked || other == currentOperationId || !MakerStudioPaths.IsOperationId(other)) continue;
                var text = Files.ReadText(path, out _);
                if (text is null || !JsonReader.TryParse(text, out var obj, out _) || !(obj is JsonObject root)
                    || Text(root, "resolves") != blocked) continue;
                var settled = new MakerCommit(Files, _store.Quota).Recover(other);
                if (!settled.Ok || Files.Exists(MakerStudioPaths.Pending(other)))
                    throw Failure(MakerStudioErrorCodes.Conflict, "Another resolution for this operation is still pending.");
            }

            if (blockedWasPending && !Files.Exists(MakerStudioPaths.Pending(blocked)))
                throw Failure(MakerStudioErrorCodes.Conflict, "An earlier resolution of this operation has just finished; refetch the line and status.");
        }

        private MakerResult<T> Guard<T>(string blocked, Func<T> work)
        {
            lock (_store.SyncRoot)
            {
                try { MakerStudioPaths.RequireOperation(blocked); return MakerResult<T>.Success(work()); }
                catch (RecoveryException ex)
                {
                    return MakerResult<T>.Fail(ex.Code, ex.Message, ex.Details ?? (ex.Code == MakerStudioErrorCodes.RecoveryRequired ? Details(blocked) : null));
                }
                catch (ArgumentException ex) { return MakerResult<T>.Fail(MakerErrorCodes.BadRequest, ex.Message); }
                catch (InvalidDataException ex) { return MakerResult<T>.Fail(MakerStudioErrorCodes.MetadataUnavailable, ex.Message, Details(blocked)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
                { return MakerResult<T>.Fail(MakerStudioErrorCodes.RecoveryRequired, "Cannot preserve recovery state: " + ex.Message, Details(blocked)); }
            }
        }

        private Snapshot Capture(string blocked)
        {
            long budget = 0;
            var pending = Read(MakerStudioPaths.Pending(blocked), ref budget);
            if (pending is null) throw Failure(MakerErrorCodes.NotFound, "No pending operation " + blocked);
            if (!JsonReader.TryParse(Encoding.UTF8.GetString(pending), out var parsed, out _) || !(parsed is JsonObject root)
                || !(root.TryGetValue("schema", out var schema) && schema is long s && s == 1)
                || Text(root, "operationId") != blocked || Text(root, "resolves") != null
                || !(root.TryGetValue("files", out var nodes) && nodes is IList<object?> files) || files.Count == 0 || files.Count > 20)
                throw Failure(MakerStudioErrorCodes.RecoveryRequired, "Unreadable or unsupported intent.");
            var snapshot = new Snapshot { Intent = pending };
            var kind = MakerCommit.IntentKind(Files, root, files, blocked);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Count; i++)
            {
                if (!(files[i] is JsonObject f) || Text(f, "path") is not string path || !MakerCommit.AllowedTarget(path, kind, blocked)
                    || !names.Add(path) || Text(f, "stage") != MakerStudioPaths.Stage(blocked, i)
                    || !f.ContainsKey("after") || (f["after"] != null && !(f["after"] is string))
                    || !f.ContainsKey("before")
                    || (f["before"] != null && !(f["before"] is string))
                    || (f.TryGetValue("checkOnly", out var check) && check is true))
                    throw Failure(MakerStudioErrorCodes.RecoveryRequired, "Unconfined or malformed intent target.");
                var isDelete = f.TryGetValue("delete", out var del) && del is true;
                var after = f["after"] as string;
                if (isDelete ? (!MakerCommit.AllowedNormalDelete(path, kind) || after != null) : after is null)
                    throw Failure(MakerStudioErrorCodes.RecoveryRequired, "Unconfined or malformed intent target.");
                var current = Read(path, ref budget);
                var stage = Read(MakerStudioPaths.Stage(blocked, i), ref budget);
                byte[]? intended = null;
                if (!isDelete)
                {
                    intended = stage ?? (Hash(current) == after ? current : null);
                    if (intended is null || Hash(intended) != after)
                        throw Failure(MakerStudioErrorCodes.RecoveryRequired, "Intended bytes are unavailable for " + path);
                }
                snapshot.Rows.Add(new Row { Path = path, Before = Text(f, "before"), Current = current, Intended = intended,
                    Stage = stage, Swap = Read(Swap(blocked, i), ref budget), Index = i });
            }
            // Metadata-only writes still depend on the current published subtitle table.
            foreach (var row in snapshot.Rows.ToArray())
            {
                if (!row.Path.StartsWith(".maker/lines/", StringComparison.Ordinal)) continue;
                var table = MakerEditRules.SubtitlePath(row.Path.Split('/')[2]);
                if (names.Add(table))
                {
                    var current = Read(table, ref budget);
                    snapshot.Rows.Add(new Row { Path = table, Current = current, Intended = current, Index = -1 });
                }
            }
            var facts = new List<object?>();
            foreach (var row in snapshot.Rows) facts.Add(new JsonObject
            {
                ["path"] = row.Path, ["current"] = Hash(row.Current), ["intended"] = Hash(row.Intended),
                ["stage"] = Hash(row.Stage), ["swap"] = Hash(row.Swap),
            });
            snapshot.Revision = MakerProjectRevision.StableId(ApiJson.Write(new JsonObject
            { ["intent"] = Hash(pending), ["files"] = facts }));
            return snapshot;
        }

        private byte[]? Reconcile(Row row, string revision)
        {
            if (row.Path.StartsWith(".maker/lines/", StringComparison.Ordinal))
            {
                var parts = row.Path.Split('/');
                var lang = parts[2]; var id = Path.GetFileNameWithoutExtension(parts[3]);
                var line = new MakerLineDocument();
                if (row.Current != null && !MakerProjectJson.TryLine(Encoding.UTF8.GetString(row.Current), out line, out var problem))
                    throw Failure(MakerStudioErrorCodes.RecoveryRequired, "Cannot reconcile " + row.Path + ": " + problem);
                var current = _store.CurrentSubtitle(lang, id);
                var subtitleFingerprint = _store.SubtitleFingerprint(current);
                line.Revision = revision;
                // History records a subtitle only when it really changed, as line reads do (REVIEW-FIXES-M5c item 6):
                // resolving a blocked take must not invent a subtitle entry or a history gap.
                if (row.Current is null || !string.Equals(line.SubtitleFingerprint, subtitleFingerprint, StringComparison.Ordinal))
                {
                    line.SubtitleFingerprint = subtitleFingerprint; line.HistoryGap = true;
                    line.History.Add(new MakerHistoryRevision { RevisionId = MakerProjectRevision.New(),
                        CreatedAt = MakerProjectRevision.At(_store.Clock.Now), Text = current, Source = "external" });
                    while (line.History.Count > 20) line.History.RemoveAt(0);
                }
                _store.ReconcileLineTakes(lang, id, line, out _, fullHash: true);
                return MakerProjectStore.Utf8(MakerProjectJson.WriteLine(line));
            }
            if (row.Path.StartsWith(".maker/glossary/", StringComparison.Ordinal))
            {
                var glossary = new MakerGlossaryDocument();
                if (row.Current != null && !MakerProjectJson.TryGlossary(Encoding.UTF8.GetString(row.Current), out glossary, out var problem))
                    throw Failure(MakerStudioErrorCodes.RecoveryRequired, "Cannot reconcile " + row.Path + ": " + problem);
                glossary.Revision = revision;
                return MakerProjectStore.Utf8(MakerProjectJson.WriteGlossary(glossary));
            }
            return null; // Published files, creator backups, project header and old receipts stay as they are.
        }

        private MakerRecoveryResolved? Receipt(string op, string fingerprint)
        {
            var text = Files.ReadText(MakerStudioPaths.Receipt(op), out _);
            if (text is null) return null;
            if (!MakerProjectJson.TryReceipt(text, out var r, out _) || r.Kind != "recovery.resolve")
                throw Failure(MakerStudioErrorCodes.Conflict, "operationId is already in use.");
            if (r.Fingerprint != fingerprint) throw Failure(MakerStudioErrorCodes.Conflict, "operationId belongs to another resolution request.");
            if (!DateTimeOffset.TryParse(r.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var created))
                throw Failure(MakerStudioErrorCodes.RecoveryRequired, "Unreadable resolution receipt time.");
            if (_store.Clock.Now - created >= TimeSpan.FromHours(24))
                throw Failure(MakerErrorCodes.NotFound, "Resolution receipt expired.");
            var body = r.Response;
            if (Text(body, "operationId") != op || Text(body, "blockedOperationId") is not string blocked
                || Text(body, "revision") is not string revision || Text(body, "archivePath") is not string archive
                || archive != Archive(blocked, op) || !(body.TryGetValue("resolved", out var resolved) && resolved is true))
                throw Failure(MakerStudioErrorCodes.RecoveryRequired, "Unreadable resolution receipt.");
            return new MakerRecoveryResolved { Resolved = true, OperationId = op, BlockedOperationId = blocked,
                Revision = revision, ArchivePath = archive, Live = false };
        }

        private byte[]? Read(string path, ref long budget)
        {
            var full = Files.Resolve(path);
            if (!File.Exists(full)) return null;
            var size = new FileInfo(full).Length;
            if (size > 16 * 1024 * 1024 || (budget += size) > MaxReadBytes)
                throw Failure(MakerErrorCodes.TooLarge, "Recovery preview exceeds the bounded 64 MiB snapshot budget.");
            return Files.ReadBytes(path);
        }
        private static byte[] Bytes(JsonObject obj) => MakerProjectStore.Utf8(ApiJson.Write(obj));
        private static string? Hash(byte[]? bytes) => bytes is null ? null : MakerPackFiles.Fingerprint(bytes);
        private static string? Text(JsonObject obj, string key) => obj.TryGetValue(key, out var value) ? value as string : null;
        private static string Archive(string blocked, string op) => ".maker/recovery/" + blocked + "/" + op;
        private static string Swap(string op, int i) => MakerStudioPaths.Transactions + "/" + op + "." + i + ".swap";
        private static void Add(List<MakerCommitFile> targets, string path, byte[] bytes) =>
            targets.Add(new MakerCommitFile { Path = path, Bytes = bytes });
        private MakerRecoveryDetails Details(string blocked) => new MakerRecoveryDetails
        { PackId = Path.GetFileName(Files.Root), Resource = MakerStudioPaths.Pending(blocked), BlockedOperationId = blocked };
        private static RecoveryException Failure(string code, string message) => new RecoveryException(code, message);
        private static void Require(MakerCommitResult r)
        { if (!r.Ok) throw new RecoveryException(r.Error!, r.Message!, r.Details); }

        internal static bool AllowedOriginal(string path, string blocked)
        {
            if (path == MakerStudioPaths.Project || path == MakerStudioPaths.Receipt(blocked) || MakerStudioPaths.IsTakeAudioPath(path)) return true;
            // v2.4 pack voice default; AllowedTarget limits it to voice.default intents, and Reconcile keeps it as it is.
            if (path == MakerStudioPaths.VoiceDefault) return true;
            // M5d publication targets; AllowedTarget still limits them to publish/unpublish intents.
            if (MakerStudioPaths.IsVoicePublicationPath(path) || path == MakerStudioPaths.Provenance || MakerStudioPaths.IsClipRecordPath(path)) return true;
            var parts = path.Split('/');
            try
            {
                if (parts.Length == 4 && parts[0] == ".maker" && parts[1] == "lines" && parts[3].EndsWith(".json", StringComparison.Ordinal))
                    return MakerStudioPaths.Line(parts[2], parts[3].Substring(0, parts[3].Length - 5)) == path;
                if (parts.Length == 3 && parts[0] == ".maker" && parts[1] == "glossary" && parts[2].EndsWith(".json", StringComparison.Ordinal))
                    return MakerStudioPaths.Glossary(parts[2].Substring(0, parts[2].Length - 5)) == path;
                var table = path.StartsWith("_maker_backup/", StringComparison.Ordinal) ? path.Substring(14) : path;
                var t = table.Split('/');
                if (t.Length == 3 && t[0] == "data" && t[1] == "subtitles" && t[2].EndsWith(".json", StringComparison.Ordinal))
                {
                    var lang = t[2].Substring(0, t[2].Length - 5);
                    MakerStudioPaths.Glossary(lang);
                    return true;
                }
            }
            catch (ArgumentException) { }
            return false;
        }

        internal static bool AllowedResolutionTarget(string path, string op, string blocked, bool delete, bool checkOnly)
        {
            if (delete && checkOnly) return false;
            if (delete)
            {
                if (path == MakerStudioPaths.Pending(blocked)) return true;
                for (var i = 0; i < 20; i++)
                    if (path == MakerStudioPaths.Stage(blocked, i) || path == Swap(blocked, i)) return true;
                return false;
            }
            if (checkOnly) return AllowedOriginal(path, blocked);
            if (path == MakerStudioPaths.Receipt(op)) return true;
            if (AllowedOriginal(path, blocked) && (path.StartsWith(".maker/lines/", StringComparison.Ordinal)
                || path.StartsWith(".maker/glossary/", StringComparison.Ordinal))) return true;
            var prefix = Archive(blocked, op) + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var leaf = path.Substring(prefix.Length);
            if (leaf == "intent.json" || leaf == "manifest.json") return true;
            for (var i = 0; i < 40; i++)
                if (leaf == i + ".current.bin" || leaf == i + ".intended.bin" || leaf == i + ".before.bin") return true;
            return false;
        }

        internal static bool IsResolutionIntent(MakerPackFiles files, string path)
        {
            var text = files.ReadText(path, out _);
            return text != null && JsonReader.TryParse(text, out var obj, out _) && obj is JsonObject root
                && MakerStudioPaths.IsOperationId(Text(root, "resolves"));
        }
        private sealed class Snapshot
        {
            internal byte[] Intent = new byte[0];
            internal List<Row> Rows = new List<Row>();
            internal string Revision = "";
        }
        private sealed class Row
        {
            internal string Path = "";
            internal string? Before;
            internal byte[]? Current, Intended, Stage, Swap;
            internal int Index;
        }
        private sealed class RecoveryException : Exception
        {
            internal readonly string Code;
            internal readonly MakerRecoveryDetails? Details;
            internal RecoveryException(string code, string message, MakerRecoveryDetails? details = null) : base(message)
            { Code = code; Details = details; }
        }
    }
}
