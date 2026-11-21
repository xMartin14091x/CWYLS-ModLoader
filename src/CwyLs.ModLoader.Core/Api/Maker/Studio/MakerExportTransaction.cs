using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    public sealed partial class MakerPackExporter
    {
        private const string ExportPending = ReceiptFolder + "/pending.json";
        private const long ExportStorageBytes = 4L * 1024 * 1024 * 1024;
        private const long BackupStorageBytes = 32L * 1024 * 1024;
        private const long FreeSpaceReserve = 64L * 1024 * 1024;

        private static string ExportIntent(string operation, MakerExportResult result, string fingerprint, DateTimeOffset now,
            byte[]? manifestBefore, byte[]? manifestAfter, byte[]? changelogBefore, byte[]? changelogAfter) =>
            ApiJson.Write(new JsonObject
            {
                ["schema"] = 1L, ["archiveFolder"] = result.PackId, ["operation"] = operation, ["receipt"] = MakerStudioExportJson.Receipt(result, fingerprint),
                ["last"] = ApiJson.Write(MakerStudioExportJson.Last(new MakerLastExport
                {
                    FileName = result.FileName, Version = result.Version, SizeBytes = result.SizeBytes,
                    CreatedAt = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                })),
                ["manifestBefore"] = Base64(manifestBefore), ["manifestAfter"] = Base64(manifestAfter),
                ["changelogBefore"] = Base64(changelogBefore), ["changelogAfter"] = Base64(changelogAfter),
            });

        private MakerResult<MakerExportResult> CompleteExport(MakerPackFiles files, string intent, string packId)
        {
            // A blocked intent holds back exports and export checks only (milestone-1 P3 B3). The message names the one
            // manual step that gives the export up: the intent never changes pack files on its own.
            MakerResult<MakerExportResult> Blocked(string message, bool fileState = true) =>
                MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.RecoveryRequired, "export recovery required: " + message + (fileState
                    ? ". Exports and export checks of this pack wait for this export. To give it up, move '" + files.Resolve(ExportPending)
                        + "' out of the pack folder, then check again; nothing else in the pack needs to change."
                    : "."));
            try
            {
                if (!JsonReader.TryParse(intent, out var parsed, out _) || !(parsed is JsonObject o)
                    || !o.TryGetValue("schema", out var schema) || !(schema is long n) || n != 1
                    || !o.TryGetValue("operation", out var operation) || !(operation is string op) || !MakerStudioPaths.IsOperationId(op)
                    || !o.TryGetValue("receipt", out var receipt) || !(receipt is string receiptText)
                    || !MakerStudioExportJson.TryReceipt(receiptText, out var result, out _)
                    || result == null || result.PackId != packId || !MakerSegments.IsPackId(result.PackId) || !FileNameRule.IsMatch(result.FileName)
                    || !result.FileName.StartsWith(result.PackId + "-", StringComparison.Ordinal)
                    || !o.TryGetValue("last", out var last) || !(last is string lastText))
                    return Blocked("the retained intent is unreadable; preserve it and the staged archive");
                var beforeManifest = IntentBytes(o, "manifestBefore");
                var afterManifest = IntentBytes(o, "manifestAfter");
                var beforeChangelog = IntentBytes(o, "changelogBefore");
                var afterChangelog = IntentBytes(o, "changelogAfter");
                var root = _exportsRoot();
                if (root == null) return Blocked("the exports folder is unknown", fileState: false);
                // Intents from the shared-folder layout have no archiveFolder. Recover in place;
                // new intents are tied to exactly one pack's export directory.
                if (o.TryGetValue("archiveFolder", out var archiveFolder))
                {
                    if (!(archiveFolder is string folder) || folder != packId) return Blocked("the archive folder does not match the pack");
                    root = PackExportsRoot(root, packId);
                }
                var final = new MakerPackFiles(root).Resolve(result.FileName);
                var stage = new MakerPackFiles(root).Resolve("." + result.FileName + "." + op + ".tmp");
                var receiptPath = ReceiptFolder + "/" + op + ".json";
                var currentReceipt = MakerPackChecks.ReadMetadata(files, receiptPath, out _);
                if (currentReceipt != null && currentReceipt != receiptText) return Blocked("the operation receipt changed outside Studio");
                // The receipt is the last durable step. Once it matches, the archive, CHANGELOG and manifest were all
                // installed; a later outside move or edit of them must not block the housekeeping below.
                if (currentReceipt == null)
                {
                    var archive = File.Exists(final) ? final : stage;
                    if (!File.Exists(archive) || new FileInfo(archive).Length != result.SizeBytes || MakerPackChecks.HashFile(archive) != result.Sha256)
                        return Blocked("the original archive is missing or changed; no pack files were overwritten");

                    if (!InstallMetadata(files, MakerPackChecks.ChangelogFile, beforeChangelog, afterChangelog, op + ".changelog.swap"))
                        return Blocked("CHANGELOG.md changed outside Studio; the intent and original archive are preserved");
                    _exportBoundary?.Invoke("after-changelog");
                    if (!InstallMetadata(files, PackContentIndex.ManifestFileName, beforeManifest, afterManifest, op + ".manifest.swap"))
                        return Blocked("manifest.json changed outside Studio; the intent and original archive are preserved");
                    _exportBoundary?.Invoke("after-manifest");
                    if (!File.Exists(final)) File.Move(stage, final); // no replacement: retained artifacts are immutable
                    _exportBoundary?.Invoke("after-zip");
                    _exportBoundary?.Invoke("before-receipt");
                    files.WriteTextAtomic(receiptPath, receiptText, false);
                }

                // The archive, metadata and receipt are durable. Later housekeeping cannot turn a
                // completed operation into a failure or make it bump a second time on retry.
                try
                {
                    _exportBoundary?.Invoke("after-receipt");
                    files.WriteTextAtomic(LastFile, lastText, false);
                    RetireSwap(files, op + ".manifest.swap", beforeManifest);
                    RetireSwap(files, op + ".changelog.swap", beforeChangelog);
                    File.Delete(files.Resolve(ExportPending));
                    if (File.Exists(stage) && MakerPackChecks.HashFile(stage) == result.Sha256) File.Delete(stage);
                    TrimReceipts(files, op);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                if (currentReceipt == null)
                    _publish("maker.pack.changed", new Dictionary<string, object?> { ["packId"] = result.PackId, ["reason"] = "export" });
                return MakerResult<MakerExportResult>.Success(result);
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidDataException || ex is OverflowException)
            {
                return Blocked("the retained metadata is unreadable: " + ex.Message);
            }
        }

        private static bool InstallMetadata(MakerPackFiles files, string path, byte[]? before, byte[]? after, string swap)
        {
            if (after == null) return true;
            MakerPackChecks.ReadMetadata(files, path, out _); // bound the following checked replacement reads
            var current = files.ReadBytes(path);
            if (current != null && current.SequenceEqual(after)) return true;
            var expected = before == null ? null : MakerPackFiles.Fingerprint(before);
            if ((current == null ? null : MakerPackFiles.Fingerprint(current)) != expected) return false;
            return files.WriteChecked(path, after, expected, ReceiptFolder + "/" + swap);
        }

        private static void RetireSwap(MakerPackFiles files, string name, byte[]? before)
        {
            var path = ReceiptFolder + "/" + name;
            var old = files.ReadBytes(path);
            if (old != null && before != null && old.SequenceEqual(before)) File.Delete(files.Resolve(path));
        }

        private static string? Base64(byte[]? bytes) => bytes == null ? null : Convert.ToBase64String(bytes);
        private static byte[]? IntentBytes(JsonObject o, string key)
        {
            if (!o.TryGetValue(key, out var value)) throw new InvalidDataException("missing " + key);
            if (value == null) return null;
            if (!(value is string text)) throw new InvalidDataException("invalid " + key);
            var bytes = Convert.FromBase64String(text);
            if (bytes.LongLength > MakerPackChecks.MaxMetadataBytes) throw new InvalidDataException("oversized " + key);
            return bytes;
        }

        private static string PackExportsRoot(string root, string packId)
        {
            if (!MakerSegments.IsPackId(packId)) throw new UnauthorizedAccessException("Invalid export pack id.");
            if (MakerPackRules.IsLink(root)) throw new UnauthorizedAccessException("Export storage at '" + root + "' is a symbolic link or junction; use a real folder.");
            if (!new MakerPackFiles(root).TryResolve(packId, out var folder, out var problem))
                throw new UnauthorizedAccessException("Export storage at '" + Path.Combine(root, packId) + "' cannot be used: " + problem + "; move linked entries elsewhere, then retry. Existing files were preserved.");
            return folder;
        }

        private static string? ExportStorageProblem(string root, long growth)
        {
            if (growth > MakerPackChecks.MaxExportBytes + 64L * 1024 * 1024)
                return "ZIP metadata would exceed the non-ZIP64 archive limit; split the pack";
            if (File.Exists(root)) return "Export storage at '" + root + "' is a file; move it elsewhere so a folder can be created, then retry. Existing files were preserved.";
            var measured = MeasureStorage(root, ExportStorageBytes, MaxReceipts);
            if (measured.Blocked && measured.Files <= MaxReceipts && measured.Bytes <= ExportStorageBytes)
                return "Export storage at '" + root + "' contains a link or too many nested folders to check safely; move the linked or nested entries elsewhere, then retry. Existing files were preserved.";
            if (measured.Blocked || measured.Bytes + growth > ExportStorageBytes || measured.Files + 1 > MaxReceipts)
                return "Export storage at '" + root + "' is limited to 100 files and 4 GiB per pack; move older exports from this folder elsewhere, then retry. Existing files were preserved.";
            return HasFreeSpace(root, growth) ? null : "Not enough free disk space for export storage at '" + root + "' and a 64 MiB reserve; free disk space, then retry. Existing files were preserved.";
        }

        private static string? IconStorageProblem(MakerPackFiles files, long oldSize, long newSize)
        {
            var measured = MeasureStorage(files.Resolve(MakerEditRules.BackupFolder), BackupStorageBytes, 128);
            if (measured.Blocked || measured.Bytes + oldSize > BackupStorageBytes || measured.Files + (oldSize > 0 ? 1 : 0) > 128)
                return "icon backups are limited to 128 files and 32 MiB; move older _maker_backup files elsewhere, then retry";
            return HasFreeSpace(files.Root, newSize) ? null : "not enough free disk space for the icon and a 64 MiB reserve; free space, then retry";
        }

        private static (long Bytes, int Files, bool Blocked) MeasureStorage(string root, long maxBytes, int maxFiles)
        {
            if (!Directory.Exists(root)) return (0, 0, false);
            long bytes = 0;
            var count = 0;
            var pending = new Stack<string>();
            pending.Push(root);
            var visited = 0;
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                if (++visited > maxFiles + 1 || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return (bytes, count, true);
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return (bytes, count, true);
                    bytes = checked(bytes + new FileInfo(file).Length);
                    if (++count > maxFiles || bytes > maxBytes) return (bytes, count, true);
                }
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    if (pending.Count >= maxFiles + 1) return (bytes, count, true);
                    pending.Push(child);
                }
            }
            return (bytes, count, false);
        }

        private static bool HasFreeSpace(string path, long growth)
        {
            var full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives().Where(d => full.StartsWith(d.Name, StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length).FirstOrDefault();
            return drive != null && drive.AvailableFreeSpace >= growth + FreeSpaceReserve;
        }
    }
}
