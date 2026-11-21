using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    public sealed class MakerCommitFile
    {
        public string Path { get; set; } = string.Empty;
        public string? BeforeFingerprint { get; set; }
        public byte[] Bytes { get; set; } = new byte[0];
        public bool Delete { get; set; }
        public bool CheckOnly { get; set; }
        /// <summary>A resolution row whose outcome recovery has already decided; it is neither re-imposed nor verified.</summary>
        internal bool Settled { get; set; }
        internal string? After => Delete ? null : CheckOnly ? BeforeFingerprint : MakerPackFiles.Fingerprint(Bytes);
    }

    public sealed class MakerCommitResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Message { get; set; }
        public MakerRecoveryDetails? Details { get; set; }
        public static MakerCommitResult Success() => new MakerCommitResult { Ok = true };
        public static MakerCommitResult Fail(string error, string message, MakerRecoveryDetails? details = null) =>
            new MakerCommitResult { Error = error, Message = message, Details = details };
    }

    /// <summary>Recoverable, preconditioned multi-file replacement. Callers hold the pack gate throughout.</summary>
    public sealed class MakerCommit
    {
        private readonly MakerPackFiles _files;
        private readonly long _quota;
        private readonly Action<string>? _boundary;
        private string? _resolves;
        private readonly string? _requestFingerprint;
        private string? _kind;

        /// <summary>Creates a commit runner for journal recovery or orphan scavenging.</summary>
        /// <param name="files">The confined pack file store.</param>
        /// <param name="quota">The project quota in bytes.</param>
        /// <param name="boundary">Optional test boundary hook.</param>
        public MakerCommit(MakerPackFiles files, long quota = MakerStudioContract.ProjectBytes, Action<string>? boundary = null)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _quota = quota > 0 ? quota : throw new ArgumentOutOfRangeException(nameof(quota));
            _boundary = boundary;
        }

        /// <summary>Creates a commit runner for a durable mutation of the given <paramref name="kind"/>.</summary>
        /// <param name="files">The confined pack file store.</param>
        /// <param name="kind">The explicit commit kind.</param>
        /// <param name="quota">The project quota in bytes.</param>
        /// <param name="boundary">Optional test boundary hook.</param>
        /// <param name="resolves">The blocked operation id when this commit is a recovery resolution.</param>
        /// <param name="requestFingerprint">The resolution request fingerprint.</param>
        public MakerCommit(MakerPackFiles files, string kind, long quota = MakerStudioContract.ProjectBytes, Action<string>? boundary = null,
            string? resolves = null, string? requestFingerprint = null)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
            if (resolves is null && string.IsNullOrEmpty(kind)) throw new ArgumentException("commit kind is required", nameof(kind));
            _quota = quota > 0 ? quota : throw new ArgumentOutOfRangeException(nameof(quota));
            _boundary = boundary;
            _resolves = resolves;
            _requestFingerprint = requestFingerprint;
            _kind = resolves is null ? kind : "recovery.resolve";
        }

        public MakerCommitResult Execute(string operationId, IReadOnlyList<MakerCommitFile> targets, Action<int>? afterWrite = null)
        {
            if (string.IsNullOrEmpty(_kind)) throw new InvalidOperationException("commit kind is required");
            MakerStudioPaths.RequireOperation(operationId);
            if (targets is null || targets.Count == 0 || targets.Count > (_resolves is null ? 100 : 256))
                throw new ArgumentException("Too many commit targets.", nameof(targets));
            if (targets.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new ArgumentException("duplicate commit target", nameof(targets));
            foreach (var target in targets)
                if (_resolves is null && (!AllowedTarget(target.Path, _kind!, operationId)
                    || target.CheckOnly || (target.Delete && !AllowedNormalDelete(target.Path, _kind!))))
                    return RecoveryError(operationId, target.Path, "unconfined commit target");
            if (_files.Exists(MakerStudioPaths.Pending(operationId))) return Recover(operationId, afterWrite);
            ScavengeOrphans();
            var pendingBytes = Encoding.UTF8.GetBytes(WritePending(operationId, targets));
            long peak = pendingBytes.Length;
            long growth = 0; // net change of the .maker bytes once the commit is done
            foreach (var target in targets)
            {
                if (target.CheckOnly) continue;
                var current = _files.ReadBytes(target.Path);
                var old = current?.Length ?? 0;
                if (target.Path.StartsWith(MakerStudioPaths.Root + "/", StringComparison.Ordinal))
                    growth = checked(growth + (target.Delete ? 0 : target.Bytes.Length) - old);
                // Existing .maker bytes already include the old target. A replacement adds a stage and
                // a temp/new target; moving the old target to its swap does not allocate it twice.
                peak = checked(peak + (target.Delete ? 0 : target.Bytes.Length));
                peak = checked(peak + (target.Path.StartsWith(MakerStudioPaths.Root + "/", StringComparison.Ordinal)
                    ? (target.Delete ? 0 : target.Bytes.Length) : old));
            }
            // A commit that frees space is never refused for the quota (REVIEW-FIXES-M5c item 3): near the limit, deleting a
            // take is the way out, and its transient journal and swap bytes are retired with the commit.
            if (growth >= 0 && _files.BytesUnder(MakerStudioPaths.Root) + peak > _quota)
                return MakerCommitResult.Fail(MakerStudioErrorCodes.StorageLimit, "the Pack Maker project would exceed its storage limit");

            for (var i = 0; i < targets.Count; i++)
                if (!targets[i].Delete && !targets[i].CheckOnly)
                    _files.WriteAtomic(MakerStudioPaths.Stage(operationId, i), targets[i].Bytes);
            _boundary?.Invoke("before-intent"); // stages written, no intent yet: the stages are orphans
            _files.WriteAtomic(MakerStudioPaths.Pending(operationId), pendingBytes);
            _boundary?.Invoke("after-intent");
            return Apply(operationId, targets, afterWrite);
        }

        public MakerCommitResult Recover(string operationId, Action<int>? afterWrite = null)
        {
            MakerStudioPaths.RequireOperation(operationId);
            var path = MakerStudioPaths.Pending(operationId);
            try
            {
            var pending = _files.ReadText(path, out _);
            if (pending is null) { ScavengeOrphans(); return MakerCommitResult.Success(); }
            if (TryRecoverResolution(pending, operationId, afterWrite, out var resolutionResult))
                return resolutionResult;
            if (!TryPending(pending, operationId, out var targets, out var problem))
                return RecoveryError(operationId, path, "pending commit unreadable: " + problem);
            return Apply(operationId, targets, afterWrite);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return RecoveryError(operationId, path, "Recovery cannot complete: " + ex.Message);
            }
        }

        /// <summary>
        /// The one commit protocol, for new commits and for recovery alike: validate every row, write the rows not yet
        /// at their after-state, verify, then clean up. <see cref="MakerCommitFile.Settled"/> rows are skipped throughout.
        /// </summary>
        private MakerCommitResult Apply(string op, IReadOnlyList<MakerCommitFile> targets, Action<int>? afterWrite)
        {
            // Validate ALL destinations and stages before changing even the first destination.
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target.Settled) continue;
                var after = target.After;
                var current = Fingerprint(target.Path);
                var swap = Swap(op, i);
                if (_files.Exists(swap) && Fingerprint(swap) != target.BeforeFingerprint)
                {
                    if (current == after)
                    {
                        if (target.Delete) _files.RestoreDeletion(swap, target.Path);
                        else _files.RestoreReplacement(swap, target.Path);
                    }
                    return RecoveryError(op, target.Path, "a replacement displaced an external edit");
                }
                if (current == after) continue;
                if (current != target.BeforeFingerprint)
                    return RecoveryError(op, target.Path, "file changed outside Studio while a commit was pending");
                if (!target.Delete && !target.CheckOnly && Fingerprint(MakerStudioPaths.Stage(op, i)) != after)
                    return RecoveryError(op, target.Path, "staged bytes are missing or changed");
            }
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target.CheckOnly || target.Settled) continue;
                if (Fingerprint(target.Path) != target.After)
                {
                    _boundary?.Invoke("before-write:" + i);
                    var written = target.Delete
                        ? _files.DeleteChecked(target.Path, target.BeforeFingerprint, Swap(op, i))
                        : _files.WriteChecked(target.Path, target.Bytes, target.BeforeFingerprint, Swap(op, i));
                    if (!written)
                        return RecoveryError(op, target.Path, "file changed during replacement; displaced bytes were restored");
                }
                _boundary?.Invoke("after-write:" + i);
                afterWrite?.Invoke(i);
            }
            foreach (var target in targets)
                if (!target.Settled && Fingerprint(target.Path) != target.After)
                    return RecoveryError(op, target.Path, "target changed before commit completion");
            Cleanup(op, targets.Count);
            return MakerCommitResult.Success();
        }

        private string? Fingerprint(string path)
        {
            var bytes = _files.ReadBytes(path);
            return bytes is null ? null : MakerPackFiles.Fingerprint(bytes);
        }

        private bool TryRecoverResolution(string text, string op, Action<int>? afterWrite, out MakerCommitResult result)
        {
            result = MakerCommitResult.Success();
            if (!JsonReader.TryParse(text, out var parsed, out _) || !(parsed is JsonObject root)) return false;
            var hasResolves = root.TryGetValue("resolves", out var r) && r != null;
            var isResolveKind = root.TryGetValue("kind", out var k) && k as string == "recovery.resolve";
            if (!hasResolves && !isResolveKind) return false;
            var pendingPath = MakerStudioPaths.Pending(op);
            if (!(root.TryGetValue("schema", out var schema) && schema is long version && version == 1)
                || !(root.TryGetValue("operationId", out var id) && id is string operation && operation == op)
                || !(r is string blocked && MakerStudioPaths.IsOperationId(blocked) && blocked != op)
                || (root.TryGetValue("kind", out var kindVal) && kindVal as string != "recovery.resolve")
                || !(root.TryGetValue("files", out var f) && f is IList<object?> files) || files.Count == 0 || files.Count > 256)
            {
                _resolves = r as string;
                result = RecoveryError(op, pendingPath, "pending commit unreadable: invalid resolution journal");
                return true;
            }

            _resolves = blocked;
            _kind = "recovery.resolve";
            var archivePrefix = ".maker/recovery/" + blocked + "/" + op + "/";
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? receiptAfter = null;
            var hasIntent = false; var hasManifest = false; var retiresBlocked = false;
            var rows = new List<(string Path, string? Before, string? After, bool Delete, bool CheckOnly)>(files.Count);
            for (var i = 0; i < files.Count; i++)
            {
                if (!(files[i] is JsonObject file)
                    || !(file.TryGetValue("path", out var p) && p is string path) || !names.Add(path)
                    || !(file.TryGetValue("stage", out var s) && s is string stage && stage == MakerStudioPaths.Stage(op, i))
                    || !(file.TryGetValue("before", out var before) && (before is null || before is string))
                    || !(file.TryGetValue("after", out var after) && (after is null || after is string)))
                {
                    result = RecoveryError(op, pendingPath, "pending commit unreadable: invalid resolution file " + i);
                    return true;
                }

                var delete = file.TryGetValue("delete", out var d) && d is true;
                var check = file.TryGetValue("checkOnly", out var c) && c is true;
                if (!MakerRecoveryStore.AllowedResolutionTarget(path, op, blocked, delete, check))
                {
                    result = RecoveryError(op, path, "unconfined resolution target");
                    return true;
                }

                var afterHash = after as string;
                if (path == MakerStudioPaths.Receipt(op))
                {
                    if (string.IsNullOrEmpty(afterHash))
                    {
                        result = RecoveryError(op, path, "invalid resolution receipt fingerprint");
                        return true;
                    }
                    receiptAfter = afterHash;
                }
                else if (path.StartsWith(archivePrefix, StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(afterHash))
                    {
                        result = RecoveryError(op, path, "invalid resolution archive fingerprint");
                        return true;
                    }
                    hasIntent |= path == archivePrefix + "intent.json";
                    hasManifest |= path == archivePrefix + "manifest.json";
                }

                retiresBlocked |= i == files.Count - 1 && delete && path == MakerStudioPaths.Pending(blocked);
                rows.Add((path, before as string, afterHash, delete, check));
            }

            if (receiptAfter is null || !hasIntent || !hasManifest || !retiresBlocked)
            {
                result = RecoveryError(op, pendingPath, "pending commit unreadable: incomplete resolution journal");
                return true;
            }

            // Decide from the journal's recorded progress, never from the receipt alone. Roll back only while that
            // loses nothing: no retirement of the blocked operation has begun and every sidecar write can be undone.
            // Otherwise finish the resolution: its archive keeps what the blocked operation intended and what every
            // sidecar was before, so finishing never discards a version.
            var receiptInstalled = Fingerprint(MakerStudioPaths.Receipt(op)) == receiptAfter;
            if (!receiptInstalled && !RetirementStarted(op, rows) && RollbackRestoresEverySidecar(op, rows))
            {
                RollBack(op, blocked, rows, archivePrefix);
                result = MakerCommitResult.Success();
                return true;
            }

            var targets = new List<MakerCommitFile>(rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                // Settled rows are already decided: check-only tables are never re-imposed (a later v1 save must not
                // wedge recovery), and a sidecar the resolution wrote, or that was edited since, is kept as it is. An
                // installed receipt means every sidecar write had finished.
                var settled = row.CheckOnly
                    || (receiptInstalled && row.Path == MakerStudioPaths.Receipt(op))
                    || (IsResolutionMetadata(row.Path) && (receiptInstalled || Fingerprint(row.Path) != row.Before));
                if (settled && IsResolutionMetadata(row.Path) && _files.Exists(Swap(op, i)) && Fingerprint(Swap(op, i)) != row.Before)
                {
                    // A kept sidecar whose swap holds bytes the resolution did not expect: Cleanup would delete them.
                    result = RecoveryError(op, row.Path, "a replacement displaced an external edit");
                    return true;
                }

                if (settled || row.Delete)
                {
                    targets.Add(new MakerCommitFile
                    {
                        Path = row.Path, BeforeFingerprint = row.Before, Delete = row.Delete, CheckOnly = row.CheckOnly, Settled = settled,
                    });
                    continue;
                }

                // Cleanup may already have removed a stage whose target was durably installed.
                var current = _files.ReadBytes(row.Path);
                var bytes = current != null && MakerPackFiles.Fingerprint(current) == row.After
                    ? current : _files.ReadBytes(MakerStudioPaths.Stage(op, i));
                if (bytes is null || MakerPackFiles.Fingerprint(bytes) != row.After)
                {
                    result = RecoveryError(op, row.Path, "staged bytes are missing or changed");
                    return true;
                }

                targets.Add(new MakerCommitFile { Path = row.Path, BeforeFingerprint = row.Before, Bytes = bytes });
            }

            result = Apply(op, targets, afterWrite);
            return true;
        }

        /// <summary>True once any retirement row took effect: its target moved into this resolution's swap, or is gone.</summary>
        private bool RetirementStarted(string op, List<(string Path, string? Before, string? After, bool Delete, bool CheckOnly)> rows)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Delete && (_files.Exists(Swap(op, i)) || (row.Before != null && !_files.Exists(row.Path)))) return true;
            }

            return false;
        }

        /// <summary>
        /// True when undoing every sidecar write loses nothing: each sidecar is untouched, or still holds this
        /// resolution's bytes (or nothing) while its swap holds what the write displaced.
        /// </summary>
        private bool RollbackRestoresEverySidecar(string op, List<(string Path, string? Before, string? After, bool Delete, bool CheckOnly)> rows)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Delete || row.CheckOnly || !IsResolutionMetadata(row.Path)) continue;
                var current = Fingerprint(row.Path);
                var swap = Swap(op, i);
                if (_files.Exists(swap))
                {
                    // Edited after the resolution wrote it: putting the swap back would destroy the newer edit.
                    if (current != null && current != row.After && current != Fingerprint(swap)) return false;
                }
                else if (current == row.After && row.Before != null && row.After != row.Before)
                {
                    return false; // written, but the version it replaced is no longer in a swap
                }
            }

            return true;
        }

        /// <summary>
        /// Undoes an unfinished resolution so the blocked operation stays resolvable. Called only when that loses
        /// nothing: every displaced sidecar goes back in place (even a displaced edit that is not the expected bytes),
        /// and the archive, all copies of bytes that still exist elsewhere, is deleted last.
        /// </summary>
        private void RollBack(string op, string blocked, List<(string Path, string? Before, string? After, bool Delete, bool CheckOnly)> rows,
            string archivePrefix)
        {
            for (var i = rows.Count - 1; i >= 0; i--)
            {
                var row = rows[i];
                if (row.Delete || row.CheckOnly) continue;
                if (IsResolutionMetadata(row.Path))
                {
                    var swap = Swap(op, i);
                    if (_files.Exists(swap))
                    {
                        // File.Replace refuses a missing destination (a hand deletion, or a crash inside a replace).
                        if (_files.Exists(row.Path)) _files.RestoreReplacement(swap, row.Path);
                        else _files.RestoreDeletion(swap, row.Path);
                    }
                    else if (row.Before is null && Fingerprint(row.Path) == row.After)
                    {
                        // Created by this resolution: its own derived metadata.
                        try { _files.Delete(row.Path); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                else if (row.Path.StartsWith(archivePrefix, StringComparison.Ordinal))
                {
                    try { _files.Delete(row.Path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            try
            {
                var archiveDir = _files.Resolve(".maker/recovery/" + blocked + "/" + op);
                if (Directory.Exists(archiveDir) && !Directory.EnumerateFileSystemEntries(archiveDir).Any())
                    Directory.Delete(archiveDir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            Cleanup(op, rows.Count);
        }

        private static bool IsResolutionMetadata(string path) =>
            path.StartsWith(".maker/lines/", StringComparison.Ordinal)
            || path.StartsWith(".maker/glossary/", StringComparison.Ordinal);

        private static string Swap(string op, int i) => MakerStudioPaths.Transactions + "/" + op + "." + i + ".swap";

        private MakerCommitResult RecoveryError(string op, string resource, string message) =>
            MakerCommitResult.Fail(MakerStudioErrorCodes.RecoveryRequired,
                "pack=" + Path.GetFileName(_files.Root) + "; resource=" + resource + "; operationId=" + op + "; " + message,
                new MakerRecoveryDetails { PackId = Path.GetFileName(_files.Root), Resource = resource, BlockedOperationId = _resolves ?? op });

        private void Cleanup(string op, int count)
        {
            // Verified after-fingerprints are the commit point. Retire the marker before disposable bytes.
            if (!DeleteTemporary(MakerStudioPaths.Pending(op))) return;
            for (var i = 0; i < count; i++)
            {
                DeleteTemporary(MakerStudioPaths.Stage(op, i));
                DeleteTemporary(Swap(op, i));
            }
        }

        private bool DeleteTemporary(string path)
        {
            var deleted = false;
            try
            {
                _boundary?.Invoke("before-delete:" + path);
                _files.Delete(path);
                deleted = !_files.Exists(path) && !Directory.Exists(_files.Resolve(path));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _boundary?.Invoke("after-delete:" + path);
            return deleted;
        }

        public void ScavengeOrphans()
        {
            foreach (var path in _files.ListFiles(MakerStudioPaths.Transactions))
            {
                var name = Path.GetFileName(path);
                if (!(name.EndsWith(".stage", StringComparison.Ordinal) || name.EndsWith(".swap", StringComparison.Ordinal)) || name.Length < 33) continue;
                var op = name.Substring(0, 32);
                var owned = false;
                if (MakerStudioPaths.IsOperationId(op))
                    for (var i = 0; i < 256; i++) owned |= path == MakerStudioPaths.Stage(op, i) || path == Swap(op, i);
                if (owned && !_files.Exists(MakerStudioPaths.Pending(op)))
                    DeleteTemporary(path);
            }
        }

        private string WritePending(string op, IReadOnlyList<MakerCommitFile> targets)
        {
            var files = targets.Select((t, i) => MiniJson.Object(
                Pair("path", MiniJson.String(t.Path)), Pair("before", MiniJson.String(t.BeforeFingerprint)),
                Pair("after", MiniJson.String(t.After)), Pair("delete", MiniJson.Bool(t.Delete)), Pair("checkOnly", MiniJson.Bool(t.CheckOnly)),
                Pair("stage", MiniJson.String(MakerStudioPaths.Stage(op, i)))));
            return MiniJson.Object(Pair("schema", "1"), Pair("operationId", MiniJson.String(op)),
                Pair("kind", MiniJson.String(_kind)),
                Pair("resolves", MiniJson.String(_resolves)), Pair("requestFingerprint", MiniJson.String(_requestFingerprint)),
                Pair("files", "[" + string.Join(",", files) + "]"));
        }

        private bool TryPending(string text, string op, out IReadOnlyList<MakerCommitFile> targets, out string problem)
        {
            var found = new List<MakerCommitFile>(); targets = found;
            if (!JsonReader.TryParse(text, out var parsed, out var error) || !(parsed is JsonObject root)
                || !(root.TryGetValue("schema", out var schema) && schema is long n && n == 1)
                || !(root.TryGetValue("operationId", out var operation) && operation is string id && id == op)
                || !(root.TryGetValue("files", out var nodes) && nodes is IList<object?> list) || list.Count == 0 || list.Count > 256)
            { problem = error ?? "invalid intent shape"; return false; }
            _resolves = root.TryGetValue("resolves", out var resolved) ? resolved as string : null;
            _kind = IntentKind(_files, root, list, op);
            if (_resolves != null && _kind != "recovery.resolve")
            { problem = "invalid resolution kind"; return false; }
            if (_resolves is null && list.Count > 100)
            { problem = "too many normal commit targets"; return false; }
            if (_resolves != null && !MakerStudioPaths.IsOperationId(_resolves))
            { problem = "invalid resolution identity"; return false; }
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i] is JsonObject file)
                    || !(file.TryGetValue("path", out var p) && p is string path)
                    || !(file.TryGetValue("before", out var before) && (before is null || before is string))
                    || !(file.TryGetValue("after", out var a) && (a is null || a is string))
                    || !(file.TryGetValue("stage", out var s) && s is string stage)
                    || stage != MakerStudioPaths.Stage(op, i))
                { problem = "invalid intent file " + i; return false; }

                var after = a as string;
                if (_resolves is null && !AllowedTarget(path, _kind, op))
                { problem = "unconfined " + _kind + " target " + path; return false; }
                var delete = file.TryGetValue("delete", out var d) && d is bool yes && yes;
                var checkOnly = file.TryGetValue("checkOnly", out var c) && c is bool check && check;
                if ((_resolves is null && (checkOnly || (delete && !AllowedNormalDelete(path, _kind))))
                    || (_resolves != null && !MakerRecoveryStore.AllowedResolutionTarget(path, op, _resolves, delete, checkOnly))
                    || (delete && after != null))
                { problem = "unconfined resolution target"; return false; }
                if (delete || checkOnly)
                {
                    found.Add(new MakerCommitFile { Path = path, BeforeFingerprint = before as string, Delete = delete, CheckOnly = checkOnly });
                    continue;
                }

                var current = _files.ReadBytes(path);
                // Cleanup may already have removed this stage after the target was durably installed.
                var bytes = current != null && MakerPackFiles.Fingerprint(current) == after ? current : _files.ReadBytes(stage);
                if (bytes is null || MakerPackFiles.Fingerprint(bytes) != after)
                { problem = "missing or changed stage " + i; return false; }
                found.Add(new MakerCommitFile { Path = path, BeforeFingerprint = before as string, Bytes = bytes });
            }
            problem = ""; return true;
        }

        internal static string IntentKind(MakerPackFiles files, JsonObject root, IList<object?> nodes, string op)
        {
            if (root.TryGetValue("kind", out var kind)) return kind as string ?? "unknown";
            if (root.TryGetValue("resolves", out var resolves) && resolves is string blocked && MakerStudioPaths.IsOperationId(blocked)) return "recovery.resolve";
            // Pre-M5b journals carry their operation kind in the staged receipt. Capture-only journals
            // have no receipt and are restricted to creator line metadata.
            for (var i = 0; i < nodes.Count; i++)
                if (nodes[i] is JsonObject row && row.TryGetValue("path", out var path) && path as string == MakerStudioPaths.Receipt(op))
                {
                    var text = files.ReadText(MakerStudioPaths.Stage(op, i), out _) ?? files.ReadText(MakerStudioPaths.Receipt(op), out _);
                    return text != null && MakerProjectJson.TryReceipt(text, out var receipt, out _) ? receipt.Kind : "unknown";
                }
            return "metadata";
        }

        internal static bool IsTakeKind(string? kind) =>
            string.Equals(kind, "take", StringComparison.Ordinal)
            || (kind != null && kind.StartsWith("take.", StringComparison.Ordinal));

        internal static bool AllowedNormalDelete(string path, string kind) =>
            (IsTakeKind(kind) && MakerStudioPaths.IsTakeAudioPath(path))
            || (IsPublishKind(kind) && (MakerStudioPaths.IsVoicePublicationPath(path) || MakerStudioPaths.IsClipRecordPath(path)));

        /// <summary>Publish and unpublish commits (M5d): runtime voice files, their backups, provenance, clip records, lines.</summary>
        internal static bool IsPublishKind(string? kind) =>
            string.Equals(kind, "publish", StringComparison.Ordinal) || string.Equals(kind, "unpublish", StringComparison.Ordinal);

        internal static bool AllowedTarget(string path, string kind, string op)
        {
            if (!(kind == "workflow" || kind == "subtitle" || kind == "revert" || kind == "glossary" || kind == "metadata" || IsTakeKind(kind)
                || IsPublishKind(kind) || kind == "voice.default" || kind == MakerVoiceLabelStore.Kind)) return false;
            if (!MakerRecoveryStore.AllowedOriginal(path, op)) return false;
            if (path == MakerStudioPaths.Project || path == MakerStudioPaths.Receipt(op)) return true;
            if (kind == "glossary") return path.StartsWith(".maker/glossary/", StringComparison.Ordinal);
            // v2.4 pack voice default; v2.5 adds the disclosures of published takes that inherit it.
            if (kind == "voice.default") return path == MakerStudioPaths.VoiceDefault || path == MakerStudioPaths.Provenance;
            // D-148: one-click labels write only the disclosures.
            if (kind == MakerVoiceLabelStore.Kind) return path == MakerStudioPaths.Provenance;
            if (path.StartsWith(".maker/lines/", StringComparison.Ordinal)) return true;
            if (IsTakeKind(kind)) return MakerStudioPaths.IsTakeAudioPath(path) || path == MakerStudioPaths.Provenance; // v2.5.1 re-link
            if (IsPublishKind(kind))
                return MakerStudioPaths.IsVoicePublicationPath(path) || path == MakerStudioPaths.Provenance || MakerStudioPaths.IsClipRecordPath(path);
            return (kind == "subtitle" || kind == "revert")
                && (path.StartsWith("data/subtitles/", StringComparison.Ordinal) || path.StartsWith("_maker_backup/data/subtitles/", StringComparison.Ordinal));
        }
        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    }
}
