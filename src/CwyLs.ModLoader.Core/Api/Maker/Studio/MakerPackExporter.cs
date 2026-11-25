using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>
    /// Contract v2.3 pack details, icon and export (Pack Maker Phase C). File work only, on the caller's thread, under the
    /// pack's store gate so it never interleaves with a Studio save. A durable intent retains the staged ZIP and
    /// metadata snapshots until archive installation, metadata and receipt have all settled. Retained archives
    /// are never overwritten by later exports.
    /// </summary>
    public sealed partial class MakerPackExporter : IMakerStudioExport
    {
        public const string ExportsFolderName = ".maker-exports";
        public const long MaxIconBytes = 512 * 1024;
        private const string ReceiptFolder = MakerStudioPaths.Root + "/exports";
        private const string LastFile = ReceiptFolder + "/last.json";
        private const int MaxReceipts = 100;

        /// <summary>
        /// A receipt older than this is pruned (as is any past the newest 100): replaying an export's operationId is a retry of
        /// a lost answer, which never comes a month later.
        /// </summary>
        public static readonly TimeSpan ReceiptMaxAge = TimeSpan.FromDays(30);
        private static readonly object PendingGate = new object();
        private static readonly Regex FileNameRule = new Regex(@"^[a-z0-9][a-z0-9._-]{2,63}-[0-9A-Za-z.+-]{5,64}(?:-[a-f0-9]{32})?\.zip$", RegexOptions.CultureInvariant);
        private Action<string>? _exportBoundary = null; // deterministic interruption seam, like MakerCommit's boundary

        private readonly Func<string, bool, MakerResult<MakerProjectStore>> _open;
        private readonly Func<string, string?> _pendingRoot;
        private readonly Func<string?> _exportsRoot;
        private readonly Func<string, string, CancellationToken, Task<MakerResult<MakerStudioGroups>>>? _progress;
        private readonly Action<string, IReadOnlyDictionary<string, object?>> _publish;
        private readonly IApiClock _clock;
        private readonly Func<CancellationToken, Task<MakerResult<Catalog.MakerDlcHidden>>>? _paid;

        /// <param name="open">The Studio editor's pack opener (pending packs answer 409 <c>restart_required</c>).</param>
        /// <param name="pendingRoot">The folder of a pack Studio created that the game has not loaded, or null.</param>
        /// <param name="exportsRoot">The folder zips are written to, or null when unknown.</param>
        /// <param name="progress">The catalogue's groups for (pack, language); optional, warnings only.</param>
        /// <param name="publish">The Maker event sink.</param>
        /// <param name="clock">The wall clock (tests).</param>
        /// <param name="paid">
        /// What Pack Maker's DLC gate hides now (the game host wires the catalogue's verdict). Waited for at most 3 s; a failure or
        /// timeout is an unknown verdict and refuses the export (fail closed). Null: no gate (offline hosts and tests), no check.
        /// </param>
        public MakerPackExporter(Func<string, bool, MakerResult<MakerProjectStore>> open, Func<string, string?> pendingRoot, Func<string?> exportsRoot,
            Func<string, string, CancellationToken, Task<MakerResult<MakerStudioGroups>>>? progress = null,
            Action<string, IReadOnlyDictionary<string, object?>>? publish = null, IApiClock? clock = null,
            Func<CancellationToken, Task<MakerResult<Catalog.MakerDlcHidden>>>? paid = null)
        {
            _paid = paid;
            _open = open ?? throw new ArgumentNullException(nameof(open));
            _pendingRoot = pendingRoot ?? throw new ArgumentNullException(nameof(pendingRoot));
            _exportsRoot = exportsRoot ?? throw new ArgumentNullException(nameof(exportsRoot));
            _progress = progress;
            _publish = publish ?? MakerBackends.Publish;
            _clock = clock ?? SystemApiClock.Instance;
        }

        public Task<MakerResult<MakerPackInfo>> InfoAsync(string packId, CancellationToken cancel) =>
            Run<MakerPackInfo>(packId, editing: false, allowPending: true, cancel, (files, pending) =>
            {
                var set = MakerPackFileSet.Walk(files.Root);
                var text = MakerPackChecks.ReadMetadata(files, PackContentIndex.ManifestFileName, out _);
                PackManifestParser.TryParse(text, out var manifest, out _);
                return MakerResult<MakerPackInfo>.Success(new MakerPackInfo
                {
                    Id = manifest?.Id ?? packId, Name = manifest?.Name ?? packId, Version = manifest?.Version.ToString() ?? string.Empty,
                    Author = manifest?.Author, Description = manifest?.Description, Languages = MakerPackChecks.Languages(set),
                    HasIcon = files.Exists(MakerPackChecks.IconFile), SizeBytes = set.IncludedBytes, Pending = pending,
                    Changelog = MakerPackChecks.TopEntry(MakerPackChecks.ReadMetadata(files, MakerPackChecks.ChangelogFile, out _)),
                });
            });

        public Task<MakerResult<MakerBinary>> ReadIconAsync(string packId, CancellationToken cancel)
        {
            if (MakerEditRules.IsReferencePack(packId))
                return Task.FromResult(MakerResult<MakerBinary>.Fail(MakerErrorCodes.ReadOnlyPack, "reference packs ship with the loader and never serve images"));
            return Run<MakerBinary>(packId, editing: false, allowPending: true, cancel, (files, isPending) =>
            {
                var full = files.Resolve(MakerPackChecks.IconFile);
                if (!File.Exists(full)) return MakerResult<MakerBinary>.Fail(MakerErrorCodes.NotFound, "this pack has no icon.png");
                if (new FileInfo(full).Length > MaxIconBytes) return MakerResult<MakerBinary>.Fail(MakerErrorCodes.TooLarge, "icon.png is over 512 KiB");
                var bytes = File.ReadAllBytes(full);
                return MakerPackChecks.TryPngSize(bytes, out _, out _)
                    ? MakerResult<MakerBinary>.Success(new MakerBinary { Bytes = bytes, MediaType = "image/png" })
                    : MakerResult<MakerBinary>.Fail(MakerErrorCodes.UnsupportedMedia, "icon.png is not a PNG");
            });
        }

        public Task<MakerResult<MakerPackIcon>> SaveIconAsync(string packId, byte[] png, CancellationToken cancel) =>
            Run<MakerPackIcon>(packId, editing: true, allowPending: true, cancel, (files, isPending) =>
            {
                if (png is null || png.Length == 0) return MakerResult<MakerPackIcon>.Fail(MakerErrorCodes.BadRequest, "the icon is the request body");
                if (png.Length > MaxIconBytes) return MakerResult<MakerPackIcon>.Fail(MakerErrorCodes.TooLarge, "an icon may be at most 512 KiB");
                if (!MakerPackChecks.TryPngSize(png, out var width, out var height))
                    return MakerResult<MakerPackIcon>.Fail(MakerErrorCodes.UnsupportedMedia, "an icon must be a PNG");
                if (width != height || width < 64 || width > 1024)
                    return MakerResult<MakerPackIcon>.Fail(MakerErrorCodes.UnsupportedMedia, "an icon must be square, 64 to 1024 pixels (this one is " + width + "x" + height + ")");
                var oldPath = files.Resolve(MakerPackChecks.IconFile);
                if (File.Exists(oldPath) && new FileInfo(oldPath).Length > MaxIconBytes)
                    return MakerResult<MakerPackIcon>.Fail(MakerStudioErrorCodes.StorageLimit, "the existing icon exceeds 512 KiB; move it out of the pack before replacing it");
                var old = files.ReadBytes(MakerPackChecks.IconFile);
                if (old == null || !old.SequenceEqual(png))
                {
                    var storageProblem = IconStorageProblem(files, old?.LongLength ?? 0, png.LongLength);
                    if (storageProblem != null) return MakerResult<MakerPackIcon>.Fail(MakerStudioErrorCodes.StorageLimit, storageProblem);
                    if (old != null) files.MoveAside(MakerPackChecks.IconFile);
                    files.WriteAtomic(MakerPackChecks.IconFile, png);
                    _publish("maker.pack.changed", new Dictionary<string, object?> { ["packId"] = packId, ["reason"] = "icon" });
                }

                return MakerResult<MakerPackIcon>.Success(new MakerPackIcon { SizeBytes = png.Length, Width = width, Height = height });
            }, waitForExport: false);

        public Task<MakerResult<MakerExportPreview>> CheckAsync(string packId, CancellationToken cancel)
        {
            var progress = Progress(packId, cancel);
            var paid = Paid(packId, cancel);
            return Run<MakerExportPreview>(packId, editing: true, allowPending: false, cancel, (files, isPending) =>
                MakerResult<MakerExportPreview>.Success(Preview(files, packId, progress, paid, out _, out _)));
        }

        public Task<MakerResult<MakerExportResult>> ExportAsync(MakerExportRequest request, CancellationToken cancel)
        {
            if (request is null) return Task.FromResult(MakerResult<MakerExportResult>.Fail(MakerErrorCodes.BadRequest, "a request is required"));
            var problem = !MakerStudioPaths.IsOperationId(request.OperationId) ? "operationId must be 32 lowercase hexadecimal characters"
                : !MakerPackChecks.TryBump("0.0.0", request.Bump ?? string.Empty, out _) ? "bump must be none, patch, minor or major"
                : MakerPackChecks.NotesProblem(request.Notes);
            if (problem != null) return Task.FromResult(MakerResult<MakerExportResult>.Fail(MakerErrorCodes.BadRequest, problem));
            var progress = Progress(request.PackId, cancel);
            var paid = Paid(request.PackId, cancel);
            return Run<MakerExportResult>(request.PackId, editing: true, allowPending: false, cancel, (files, isPending) =>
            {
                var result = Export(files, request, progress, paid);
                if (result.Ok) result.Value!.Folder = ExportFolder(result.Value.PackId, result.Value.FileName);
                return result;
            });
        }

        public Task<MakerResult<MakerBinary>> ReadExportAsync(string packId, string fileName, CancellationToken cancel)
        {
            if (fileName is null || !FileNameRule.IsMatch(fileName) || !fileName.StartsWith(packId + "-", StringComparison.Ordinal))
                return Task.FromResult(MakerResult<MakerBinary>.Fail(MakerErrorCodes.NotFound, "no such export of this pack"));
            var paid = Paid(packId, cancel);
            return Run<MakerBinary>(packId, editing: true, allowPending: false, cancel, (files, isPending) =>
            {
                var full = RetainedArchive(packId, fileName);
                if (full is null) return MakerResult<MakerBinary>.Fail(MakerErrorCodes.NotFound, "no such export of this pack");

                using var stream = File.OpenRead(full);
                var length = stream.Length;
                if (length > MakerPackChecks.MaxDownloadBytes)
                    return MakerResult<MakerBinary>.Fail(MakerErrorCodes.TooLarge, "this zip is over 256 MiB; take it from '" + Path.GetDirectoryName(full) + "' instead");
                var bytes = new byte[(int)length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) break;
                    offset += read;
                }
                if (offset != bytes.Length || stream.ReadByte() != -1)
                    return MakerResult<MakerBinary>.Fail(MakerErrorCodes.Unavailable, "the retained archive changed while being read");

                using var archive = new MemoryStream(bytes, writable: false);
                var problem = ArchivePaidProblem(archive, packId, paid);
                return problem is null
                    ? MakerResult<MakerBinary>.Success(new MakerBinary { Bytes = bytes, MediaType = "application/zip", FileName = fileName })
                    : MakerResult<MakerBinary>.Fail(MakerStudioErrorCodes.ExportBlocked, problem);
            }, waitForExport: false);
        }

        private string? RetainedArchive(string packId, string fileName)
        {
            if (!FileNameRule.IsMatch(fileName) || !fileName.StartsWith(packId + "-", StringComparison.Ordinal)) return null;
            var root = _exportsRoot();
            if (root is null) return null;
            var files = new MakerPackFiles(root);
            var full = files.Resolve(packId + "/" + fileName);
            // Legacy archives keep their original path.
            if (!File.Exists(full)) full = files.Resolve(fileName);
            return File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0 ? full : null;
        }

        private static string? ArchivePaidProblem(Stream stream, string packId, Catalog.MakerDlcHidden? paid)
        {
            if (paid is null) return null;
            if (!paid.IsKnown) return MakerPackChecks.Paid(Array.Empty<string>(), _ => null, paid)!.Message;

            try
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                var prefix = packId + "/";
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                long size = 0;
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith(prefix, StringComparison.Ordinal))
                        return "the archive contains an entry outside its pack folder";
                    if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                    var relative = name.Substring(prefix.Length);
                    if (PackContentIndex.Normalise(relative) is null)
                        return "the archive contains an invalid pack file path";
                    if (entries.ContainsKey(relative))
                        return "the archive contains duplicate file paths";
                    entries.Add(relative, entry);
                    if (entry.Length > MakerPackChecks.MaxFileBytes || entry.Length > MakerPackChecks.MaxExportBytes - size
                        || entries.Count > MakerZipWriter.MaxEntries)
                        return "the archive exceeds the export size limits";
                    size += entry.Length;
                }

                return MakerPackChecks.Paid(entries.Keys, path =>
                {
                    var entry = entries[path];
                    if (entry.Length > MakerPackChecks.MaxMetadataBytes)
                        throw new InvalidDataException("pack metadata exceeds its size limit");
                    using var metadata = entry.Open();
                    return MakerPackChecks.ReadMetadata(metadata, path, out _);
                }, paid)?.Message;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is NotSupportedException)
            {
                return "Pack Maker could not check the archive for paid DLC content; create a fresh export.";
            }
        }

        /// <summary>
        /// The export zip's file name: <c>&lt;packId&gt;-v&lt;version&gt;.zip</c>. When the manifest has an author, the slug (lowercase,
        /// [a-z0-9] only) is not already in the pack id, and a slug is left after the 64-character version segment limit, the name
        /// gets <c>-by-&lt;authorSlug&gt;</c> too, so a downloader can tell whose pack it is. A name with no usable author slug (none,
        /// or only non-Latin letters) is <c>&lt;packId&gt;-v&lt;version&gt;.zip</c>.
        /// </summary>
        public static string ExportFileName(string packId, string version, string? author)
        {
            var plain = packId + "-v" + version + ".zip";
            var slug = AuthorSlug(author);
            if (slug.Length == 0 || packId.ToLowerInvariant().Contains(slug)) return plain;
            var room = MaxVersionSegment - ("v" + version + "-by-").Length;
            if (room < 1) return plain;
            return packId + "-v" + version + "-by-" + (slug.Length > room ? slug.Substring(0, room) : slug) + ".zip";
        }

        private const int MaxVersionSegment = 64;

        private static string AuthorSlug(string? author)
        {
            if (string.IsNullOrEmpty(author)) return string.Empty;
            var chars = author!.ToLowerInvariant().Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')).ToArray();
            return new string(chars);
        }

        private MakerResult<MakerExportResult> Export(MakerPackFiles files, MakerExportRequest request, IReadOnlyDictionary<string, MakerStudioProgress?>? progress,
            Catalog.MakerDlcHidden? paid)
        {
            var packId = request.PackId;
            var fingerprint = MakerOperationLedger.Fingerprint("export", packId, request.ExpectedVersion, request.Bump, request.Notes);
            var receiptPath = ReceiptFolder + "/" + request.OperationId + ".json";
            var receiptText = MakerPackChecks.ReadMetadata(files, receiptPath, out _);
            if (receiptText != null)
            {
                if (!MakerStudioExportJson.TryReceipt(receiptText, out var stored, out var storedFingerprint) || storedFingerprint != fingerprint)
                    return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.Conflict, "operationId was already used for another request");
                if (paid != null)
                {
                    var retained = RetainedArchive(packId, stored!.FileName);
                    if (retained is null)
                        return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.ExportBlocked, "the retained archive is missing; create a fresh export");
                    using var archive = File.OpenRead(retained);
                    var problem = ArchivePaidProblem(archive, packId, paid);
                    if (problem != null) return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.ExportBlocked, problem);
                }
                return MakerResult<MakerExportResult>.Success(stored!);
            }

            var preview = Preview(files, packId, progress, paid, out var manifest, out var set);
            if (manifest is null || preview.Version != request.ExpectedVersion)
            {
                return manifest is null
                    ? MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.ExportBlocked, "manifest.json is missing or invalid")
                    : MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.Conflict, "the pack is at version " + preview.Version + ", not " + request.ExpectedVersion + "; check it again");
            }

            if (!preview.Ok)
            {
                var first = preview.Checks.First(c => c.Severity == MakerExportSeverity.Error);
                return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.ExportBlocked, preview.ErrorCount + " check(s) failed; first: " + first.Id + ": " + first.Message);
            }

            MakerPackChecks.TryBump(preview.Version, request.Bump, out var version);
            var manifestText = MakerPackChecks.ReadMetadata(files, PackContentIndex.ManifestFileName, out var manifestBom)!;
            var newManifest = manifestText;
            if (version != preview.Version
                && (!MakerPackChecks.TrySetVersion(manifestText, version, out newManifest)
                    || !PackManifestParser.TryParse(newManifest, out var bumped, out _) || bumped is null || bumped.Version.ToString() != version))
                return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.ExportBlocked, "manifest.json's version could not be updated in place");

            var now = _clock.Now;
            var oldChangelog = MakerPackChecks.ReadMetadata(files, MakerPackChecks.ChangelogFile, out var changelogBom);
            var newChangelog = string.IsNullOrWhiteSpace(request.Notes)
                ? oldChangelog
                : MakerPackChecks.WithEntry(oldChangelog, version, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), request.Notes!);
            var changelogUpdated = newChangelog != oldChangelog;
            var manifestUpdated = newManifest != manifestText;
            if (Encoding.UTF8.GetByteCount(newManifest) + (manifestBom ? 3 : 0) > MakerPackChecks.MaxMetadataBytes
                || (newChangelog != null && Encoding.UTF8.GetByteCount(newChangelog) + (changelogBom ? 3 : 0) > MakerPackChecks.MaxMetadataBytes))
                return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.ExportBlocked, "the updated release metadata would exceed 4 MiB; shorten the changelog before exporting");

            var exportsRoot = _exportsRoot();
            if (exportsRoot is null) return MakerResult<MakerExportResult>.Fail(MakerErrorCodes.Unavailable, "the exports folder is unknown");
            var legacyExportsRoot = exportsRoot;
            try { exportsRoot = PackExportsRoot(exportsRoot, packId); }
            catch (UnauthorizedAccessException ex) { return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.StorageLimit, ex.Message); }
            var fileName = ExportFileName(packId, version, manifest.Author);
            if (File.Exists(Path.Combine(exportsRoot, fileName)) || File.Exists(Path.Combine(legacyExportsRoot, fileName)))
                fileName = fileName.Substring(0, fileName.Length - 4) + "-" + request.OperationId + ".zip";
            if (!FileNameRule.IsMatch(fileName))
                return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.ExportBlocked, "the version is too long for a downloadable export filename");
            var final = Path.Combine(exportsRoot, fileName);
            var staged = Path.Combine(exportsRoot, "." + fileName + "." + request.OperationId + ".tmp");
            var entries = set!.Included.Select(p => p.Key).ToList();
            if (newChangelog != null && !entries.Contains(MakerPackChecks.ChangelogFile)) entries.Add(MakerPackChecks.ChangelogFile);
            entries.Sort(StringComparer.Ordinal);
            if (entries.Count > MakerZipWriter.MaxEntries)
                return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.ExportBlocked, "the generated changelog would exceed the non-ZIP64 limit of 65535 files");
            var estimated = checked(set.IncludedBytes + set.IncludedBytes / 100 + entries.Sum(p => 2L * Encoding.UTF8.GetByteCount(packId + "/" + p) + 1100L)
                + (newChangelog?.Length ?? 0) * 4L + newManifest.Length * 4L);
            var storageProblem = ExportStorageProblem(exportsRoot, estimated);
            if (storageProblem != null) return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.StorageLimit, storageProblem);
            Directory.CreateDirectory(exportsRoot);
            var journaled = false;
            try
            {
                using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new MakerZipWriter(stream, now.LocalDateTime))
                {
                    foreach (var path in entries)
                    {
                        if (path == PackContentIndex.ManifestFileName || path == MakerPackChecks.ChangelogFile)
                        {
                            var bytes = path == PackContentIndex.ManifestFileName ? Encode(newManifest, manifestBom) : Encode(newChangelog!, changelogBom);
                            zip.Add(packId + "/" + path, bytes, Deflate(path));
                        }
                        else
                        {
                            using var input = File.OpenRead(files.Resolve(path));
                            zip.AddStream(packId + "/" + path, input, Deflate(path));
                        }
                    }

                    zip.Finish();
                    stream.Flush(true);
                }

                if (paid != null)
                {
                    using var archive = File.OpenRead(staged);
                    var problem = ArchivePaidProblem(archive, packId, paid);
                    if (problem != null) return MakerResult<MakerExportResult>.Fail(MakerStudioErrorCodes.ExportBlocked, problem);
                }

                var result = new MakerExportResult
                {
                    PackId = packId, Version = version, PreviousVersion = preview.Version, FileName = fileName, SizeBytes = new FileInfo(staged).Length,
                    Sha256 = MakerPackChecks.HashFile(staged), Files = entries.Count, Downloadable = new FileInfo(staged).Length <= MakerPackChecks.MaxDownloadBytes,
                    ManifestUpdated = manifestUpdated, ChangelogUpdated = changelogUpdated, WarningCount = preview.WarningCount,
                    Note = manifestUpdated ? "Exported. The game shows version " + version + " after its next start." : "Exported.",
                };
                var intent = ExportIntent(request.OperationId, result, fingerprint, now,
                    manifestUpdated ? Encode(manifestText, manifestBom) : null, manifestUpdated ? Encode(newManifest, manifestBom) : null,
                    changelogUpdated && oldChangelog != null ? Encode(oldChangelog, changelogBom) : null,
                    changelogUpdated ? Encode(newChangelog!, changelogBom) : null);
                files.WriteTextAtomic(ExportPending, intent, false);
                journaled = true;
                _exportBoundary?.Invoke("after-intent");
                return CompleteExport(files, intent, packId);
            }
            finally
            {
                // Once the intent is durable, recovery owns the staged archive. Never discard it on failure.
                try { if (!journaled && File.Exists(staged)) File.Delete(staged); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private MakerExportPreview Preview(MakerPackFiles files, string packId, IReadOnlyDictionary<string, MakerStudioProgress?>? progress,
            Catalog.MakerDlcHidden? paid, out PackManifest? manifest, out MakerPackFileSet? set)
        {
            set = MakerPackFileSet.Walk(files.Root);
            var checks = MakerPackChecks.Run(files, packId, set, progress, paid, out manifest);
            var version = manifest?.Version.ToString() ?? string.Empty;
            var next = new MakerExportNextVersions();
            if (manifest != null)
            {
                MakerPackChecks.TryBump(version, "patch", out var patch);
                MakerPackChecks.TryBump(version, "minor", out var minor);
                MakerPackChecks.TryBump(version, "major", out var major);
                next = new MakerExportNextVersions { Patch = patch, Minor = minor, Major = major };
            }

            var last = MakerPackChecks.ReadMetadata(files, LastFile, out _);
            return new MakerExportPreview
            {
                PackId = packId, Version = version, NextVersions = next, Checks = checks,
                ErrorCount = checks.Count(c => c.Severity == MakerExportSeverity.Error),
                WarningCount = checks.Count(c => c.Severity == MakerExportSeverity.Warning),
                Ok = checks.All(c => c.Severity != MakerExportSeverity.Error),
                Files = new MakerExportFiles { Included = set.Included.Count, IncludedBytes = set.IncludedBytes, Excluded = set.Excluded, ExcludedBytes = set.ExcludedBytes },
                Changelog = checks.Any(c => c.Id == "size" && c.Severity == MakerExportSeverity.Error) ? null
                    : MakerPackChecks.TopEntry(MakerPackChecks.ReadMetadata(files, MakerPackChecks.ChangelogFile, out _)),
                LastExport = last != null && MakerStudioExportJson.TryLast(last, out var stored) ? WithFolder(stored!, packId) : null,
            };
        }

        private MakerLastExport WithFolder(MakerLastExport last, string packId)
        {
            last.Folder = ExportFolder(packId, last.FileName);
            return last;
        }

        /// <summary>
        /// The folder holding an export zip on this computer: the pack's own export folder, or the shared one for an archive
        /// from the older shared layout (as <see cref="ReadExportAsync"/> finds it). Null when the exports folder is unknown.
        /// </summary>
        private string? ExportFolder(string packId, string fileName)
        {
            var root = _exportsRoot();
            if (root is null || !MakerSegments.IsPackId(packId)) return null;
            var own = Path.Combine(root, packId);
            return !File.Exists(Path.Combine(own, fileName)) && File.Exists(Path.Combine(root, fileName)) ? root : own;
        }

        /// <summary>The DLC gate's verdict, waited for at most 3 s (outside the store gate); null when no gate is wired.</summary>
        private Catalog.MakerDlcHidden? Paid(string packId, CancellationToken cancel)
        {
            if (_paid is null) return null;
            if (!MakerSegments.IsPackId(packId)) return Catalog.MakerDlcHidden.Unknown("invalid pack id");
            try
            {
                var task = _paid(cancel);
                if (!task.Wait(TimeSpan.FromSeconds(3))) return Catalog.MakerDlcHidden.Unknown("the game's lines did not answer within 3 s");
                return task.Result.Ok && task.Result.Value != null ? task.Result.Value : Catalog.MakerDlcHidden.Unknown(task.Result.Message ?? "the game's lines are not loaded");
            }
            catch (Exception ex) when (ex is AggregateException || ex is OperationCanceledException || ex is InvalidOperationException)
            {
                return Catalog.MakerDlcHidden.Unknown("the check failed (" + ex.GetType().Name + ")");
            }
        }

        /// <summary>Per language of the pack: the catalogue's overall progress, waited for at most 3 s (outside the store gate).</summary>
        private IReadOnlyDictionary<string, MakerStudioProgress?>? Progress(string packId, CancellationToken cancel)
        {
            lock (PendingGate) return ProgressCore(packId, cancel);
        }

        private IReadOnlyDictionary<string, MakerStudioProgress?>? ProgressCore(string packId, CancellationToken cancel)
        {
            if (_progress is null || !MakerSegments.IsPackId(packId)) return null;
            try
            {
                var open = _open(packId, false);
                if (!open.Ok) return null;
                var set = MakerPackFileSet.Walk(open.Value!.Files.Root);
                // The optional catalog can itself parse subtitle tables. Do not ask it for warnings
                // until the same input-size checks used by export have passed.
                if (!MakerPackChecks.FitsInputLimits(set, packId)) return null;
                MakerPackChecks.ReadMetadata(open.Value.Files, MakerStudioPaths.Project, out _);
                if (!open.Value.ReadHeader().Ok) return null;
                var result = new Dictionary<string, MakerStudioProgress?>(StringComparer.Ordinal);
                foreach (var lang in MakerPackChecks.Languages(set))
                {
                    var task = _progress(packId, lang, cancel);
                    result[lang] = task.Wait(TimeSpan.FromSeconds(3)) && task.Result.Ok ? task.Result.Value!.Overall : null;
                }

                return result;
            }
            catch (Exception ex) when (ex is AggregateException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private Task<MakerResult<T>> Run<T>(string packId, bool editing, bool allowPending, CancellationToken cancel, Func<MakerPackFiles, bool, MakerResult<T>> work,
            bool waitForExport = true)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (!MakerSegments.IsPackId(packId)) return Task.FromResult(MakerResult<T>.Fail(MakerErrorCodes.BadRequest, "invalid pack id"));
                var pending = allowPending ? _pendingRoot(packId) : null;
                if (pending != null)
                {
                    lock (PendingGate) return Task.FromResult(work(new MakerPackFiles(pending), true));
                }

                var open = _open(packId, editing);
                if (!open.Ok) return Task.FromResult(MakerResult<T>.Fail(open.Error!, open.Message!, open.Details));
                var store = open.Value!;
                // Serialise exports/downloads too: memory and per-pack quotas apply to concurrent requests.
                lock (PendingGate)
                lock (store.SyncRoot)
                {
                    if (editing)
                    {
                        MakerPackChecks.ReadMetadata(store.Files, MakerStudioPaths.Project, out _);
                        var header = store.ReadHeader();
                        if (!header.Ok) return Task.FromResult(MakerResult<T>.Fail(header.Error!, header.Message!, header.Details));
                        var recovery = store.RecoverPending();
                        if (!recovery.Ok) return Task.FromResult(MakerResult<T>.Fail(recovery.Error!, recovery.Message!, recovery.Details));
                        header = store.ReadHeader();
                        if (!header.Ok) return Task.FromResult(MakerResult<T>.Fail(header.Error!, header.Message!, header.Details));
                        var pendingExport = MakerPackChecks.ReadMetadata(store.Files, ExportPending, out _, 32L * 1024 * 1024);
                        if (pendingExport != null)
                        {
                            var settled = CompleteExport(store.Files, pendingExport, packId);
                            // A blocked intent (recovery_required) only holds back exports and export checks: it never changes
                            // pack files on its own, so an icon save or a download of an earlier export goes ahead (P3 B3).
                            if (!settled.Ok && (waitForExport || settled.Error != MakerStudioErrorCodes.RecoveryRequired)) return Task.FromResult(MakerResult<T>.Fail(settled.Error!, settled.Message!, settled.Details));
                        }
                    }
                    return Task.FromResult(work(store.Files, false));
                }
            }
            catch (OperationCanceledException) { return Task.FromResult(MakerResult<T>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled.")); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Task.FromResult(MakerResult<T>.Fail(MakerErrorCodes.Unavailable, "the pack folder could not be read or written: " + ex.Message));
            }
        }

        private void TrimReceipts(MakerPackFiles files, string keep)
        {
            var folder = files.Resolve(ReceiptFolder);
            var cutoff = _clock.Now.UtcDateTime - ReceiptMaxAge;
            var receipts = Directory.GetFiles(folder, "*.json").Where(f => MakerStudioPaths.IsOperationId(Path.GetFileNameWithoutExtension(f)))
                .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            var stale = receipts.Skip(MaxReceipts)
                .Concat(receipts.Take(MaxReceipts).Where(f => File.GetLastWriteTimeUtc(f) < cutoff && Path.GetFileNameWithoutExtension(f) != keep));
            foreach (var old in stale)
            {
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static bool Deflate(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext != ".ogg" && ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".wav" && ext != ".zip";
        }

        private static byte[] Encode(string text, bool bom)
        {
            var body = new UTF8Encoding(false).GetBytes(text);
            return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
        }
    }
}
