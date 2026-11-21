using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker.Editor;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>Pack-serialised Studio sidecars. Editable reads may finish pending commits; read-only reads never write.</summary>
    public sealed class MakerProjectStore
    {
        private static readonly ConcurrentDictionary<string, object> Gates =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private readonly long _quota;
        // Preconditions belong to the bytes parsed by this store, not a later read during target construction.
        private readonly Dictionary<string, string?> _readFingerprints = new Dictionary<string, string?>(StringComparer.Ordinal);
        // Test seam (set by reflection in MakerRecoveryTests and MakerTakeReviewTests): the commit boundary hook for the
        // commits and recovery passes this store runs itself, such as a journal delete that keeps failing.
        private Action<string>? _commitBoundary = null;
        private bool _requestActive;
        private MakerCommitResult? _requestRecovery;
        /// <summary>Whether pending recovery and orphan cleanup may modify this pack.</summary>
        public bool RecoveryWritable { get; }
        /// <summary>Actual recovery scans, excluding cached lookups within one editor request.</summary>
        public int RecoveryPasses { get; private set; }
        /// <summary>Take and runtime voice WAVs read in full to fingerprint them (reconciliation and clip state).</summary>
        public int TakeAudioReads { get; private set; }

        public MakerProjectStore(string packRoot, IApiClock? clock = null, long quota = MakerStudioContract.ProjectBytes, bool recoveryWritable = true)
        {
            Files = new MakerPackFiles(packRoot);
            Clock = clock ?? SystemApiClock.Instance;
            _quota = quota;
            RecoveryWritable = recoveryWritable;
            SyncRoot = Gates.GetOrAdd(Files.Root, _ => new object());
        }

        public MakerPackFiles Files { get; }
        public IApiClock Clock { get; }
        public object SyncRoot { get; }
        internal long Quota => _quota;

        internal IDisposable RequestScope()
        {
            _requestActive = true; _requestRecovery = null;
            return new RequestEnd(this);
        }
        private sealed class RequestEnd : IDisposable
        {
            private readonly MakerProjectStore _store;
            internal RequestEnd(MakerProjectStore store) => _store = store;
            public void Dispose() { _store._requestActive = false; _store._requestRecovery = null; }
        }

        public MakerResult<MakerProjectHeader> ReadHeader()
        {
            lock (SyncRoot)
            {
                try
                {
                    var collision = CaseCollision(MakerStudioPaths.Project);
                    if (collision != null) return MakerResult<MakerProjectHeader>.Fail(MakerStudioErrorCodes.Conflict, collision);
                    var text = ReadTextSnapshot(MakerStudioPaths.Project, out _, out _);
                    if (text is null)
                    {
                        return MakerResult<MakerProjectHeader>.Success(new MakerProjectHeader
                        {
                            ProjectId = MakerProjectRevision.New(),
                        });
                    }

                    return MakerProjectJson.TryHeader(text, out var header, out var problem)
                        ? MakerResult<MakerProjectHeader>.Success(header)
                        : MakerResult<MakerProjectHeader>.Fail(MakerStudioErrorCodes.MetadataUnavailable, "project metadata is unreadable: " + problem);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return MakerResult<MakerProjectHeader>.Fail(MakerStudioErrorCodes.MetadataUnavailable, ex.Message);
                }
            }
        }

        public MakerResult<MakerLineDocument> ReadLine(string lang, string id) => ReadLineCore(lang, id, false, null, out _);

        /// <summary>The editable text and revision from the same read, including any pending recovery.</summary>
        internal MakerResult<MakerLineDocument> ReadLineWithSubtitle(string lang, string id, out string? subtitle) =>
            ReadLineCore(lang, id, false, null, out subtitle);

        internal MakerResult<MakerLineDocument> ReadBeforeV1(string lang, string id, string? previous) =>
            ReadLineCore(lang, id, true, previous, out _);

        private MakerResult<MakerLineDocument> ReadLineCore(string lang, string id, bool usePrevious, string? previous, out string? subtitle)
        {
            subtitle = null;
            lock (SyncRoot)
            {
                try
                {
                    var recovery = usePrevious ? MakerCommitResult.Success() : RecoverPending();
                    if (!recovery.Ok) return MakerResult<MakerLineDocument>.Fail(recovery.Error!, recovery.Message!, recovery.Details);
                    var header = ReadHeader();
                    if (!header.Ok) return MakerResult<MakerLineDocument>.Fail(header.Error!, header.Message!, header.Details);
                    string path;
                    try
                    {
                        path = MakerStudioPaths.Line(lang, id);
                    }
                    catch (ArgumentException ex)
                    {
                        return MakerResult<MakerLineDocument>.Fail(MakerErrorCodes.BadRequest, ex.Message);
                    }

                    var collision = CaseCollision(path);
                    if (collision != null)
                    {
                        return MakerResult<MakerLineDocument>.Fail(MakerStudioErrorCodes.Conflict, collision);
                    }

                    var text = ReadTextSnapshot(path, out _, out _);
                    string? current;
                    try { current = CurrentSubtitle(lang, id); }
                    catch (InvalidDataException ex) { return MakerResult<MakerLineDocument>.Fail(MakerStudioErrorCodes.MetadataUnavailable, ex.Message); }
                    if (usePrevious) current = previous;
                    subtitle = current;
                    var fingerprint = SubtitleFingerprint(current);
                    if (text is null)
                    {
                        return MakerResult<MakerLineDocument>.Success(new MakerLineDocument
                        {
                            Revision = MakerProjectRevision.Initial("line", path, fingerprint),
                            SubtitleFingerprint = fingerprint,
                        });
                    }

                    if (!MakerProjectJson.TryLine(text, out var line, out var problem))
                    {
                        return MakerResult<MakerLineDocument>.Fail(MakerStudioErrorCodes.MetadataUnavailable, path + " is unreadable: " + problem);
                    }

                    if (!string.Equals(line.SubtitleFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        line.ExternalDrift = true;
                        line.Revision = MakerProjectRevision.Initial("external", line.Revision, fingerprint);
                        line.SubtitleFingerprint = fingerprint;
                        line.HistoryGap = true;
                        line.History.Add(new MakerHistoryRevision
                        {
                            RevisionId = MakerProjectRevision.StableId(line.Revision), CreatedAt = MakerProjectRevision.At(Clock.Now),
                            Text = current, Source = "external",
                        });
                        while (line.History.Count > MakerStudioContract.SubtitleRevisions) line.History.RemoveAt(0);
                    }

                    if (line.PublishedTakeId != null && !PublicationIntact(lang, id, line))
                    {
                        // Another line sharing the clip published over it, or the runtime file changed outside Studio.
                        line.PublishedTakeId = null;
                        line.PublishedTakeRevision = null;
                        line.PublishedClip = null;
                        line.ExternalDrift = true;
                        line.Revision = MakerProjectRevision.Initial("external-clip", line.Revision, fingerprint);
                    }

                    if (line.Takes.Count > 0)
                    {
                        ReconcileLineTakes(lang, id, line, out var takeDrift);
                        if (takeDrift)
                        {
                            line.ExternalDrift = true;
                            line.Revision = MakerProjectRevision.Initial("external-takes", line.Revision, TakeDriftFingerprint(line));
                        }
                    }

                    return MakerResult<MakerLineDocument>.Success(line);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return MakerResult<MakerLineDocument>.Fail(MakerStudioErrorCodes.MetadataUnavailable, ex.Message);
                }
                catch (IOException ex)
                {
                    // A sharing violation or an unreadable take file: the creator metadata cannot be read right now.
                    return MakerResult<MakerLineDocument>.Fail(MakerStudioErrorCodes.MetadataUnavailable, "the line's creator files cannot be read: " + ex.Message);
                }
            }
        }

        public MakerResult<MakerGlossaryDocument> ReadGlossary(string lang)
        {
            lock (SyncRoot)
            {
                try
                {
                    var recovery = RecoverPending();
                    if (!recovery.Ok) return MakerResult<MakerGlossaryDocument>.Fail(recovery.Error!, recovery.Message!, recovery.Details);
                    var header = ReadHeader();
                    if (!header.Ok) return MakerResult<MakerGlossaryDocument>.Fail(header.Error!, header.Message!, header.Details);
                    string path;
                    try
                    {
                        path = MakerStudioPaths.Glossary(lang);
                    }
                    catch (ArgumentException ex)
                    {
                        return MakerResult<MakerGlossaryDocument>.Fail(MakerErrorCodes.BadRequest, ex.Message);
                    }

                    var collision = CaseCollision(path);
                    if (collision != null)
                    {
                        return MakerResult<MakerGlossaryDocument>.Fail(MakerStudioErrorCodes.Conflict, collision);
                    }

                    var text = ReadTextSnapshot(path, out _, out _);
                    if (text is null)
                    {
                        return MakerResult<MakerGlossaryDocument>.Success(new MakerGlossaryDocument
                        {
                            Revision = MakerProjectRevision.Initial("glossary", path, "empty"),
                        });
                    }

                    return MakerProjectJson.TryGlossary(text, out var glossary, out var problem)
                        ? MakerResult<MakerGlossaryDocument>.Success(glossary)
                        : MakerResult<MakerGlossaryDocument>.Fail(MakerStudioErrorCodes.MetadataUnavailable, path + " is unreadable: " + problem);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return MakerResult<MakerGlossaryDocument>.Fail(MakerStudioErrorCodes.MetadataUnavailable, ex.Message);
                }
            }
        }

        public string? CurrentSubtitle(string lang, string id)
        {
            // Live-apply observers outside an operation must remain nonblocking. Only reads
            // inside the pack gate supply preconditions to a later Studio commit.
            var table = System.Threading.Monitor.IsEntered(SyncRoot)
                ? ReadTextSnapshot(MakerEditRules.SubtitlePath(lang), out _, out _)
                : Files.ReadText(MakerEditRules.SubtitlePath(lang), out _);
            if (string.IsNullOrWhiteSpace(table)) return null;
            if (!JsonReader.TryParse(table, out var parsed, out _) || !(parsed is JsonObject obj)
                || obj.Values.Any(v => !(v is string)))
                throw new InvalidDataException("data/subtitles/" + lang + ".json is not a valid subtitle table");
            return obj.TryGetValue(id, out var value) ? (string?)value : null;
        }

        public string SubtitleFingerprint(string? text) =>
            MakerPackFiles.Fingerprint(Encoding.UTF8.GetBytes(text is null ? "<null>" : "<text>" + text));

        public MakerCommitResult RecoverPending()
        {
            lock (SyncRoot)
            {
                if (_requestActive && _requestRecovery != null) return _requestRecovery;
                RecoveryPasses++;
                var result = RecoverPendingCore();
                if (_requestActive) _requestRecovery = result;
                return result;
            }
        }

        private MakerCommitResult RecoverPendingCore()
        {
            lock (SyncRoot)
            {
                var collision = CaseCollision(MakerStudioPaths.Transactions);
                if (collision != null) return MakerCommitResult.Fail(MakerStudioErrorCodes.Conflict, collision);
                IReadOnlyList<string> files;
                try
                {
                    files = Files.ListFiles(MakerStudioPaths.Transactions);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return MakerCommitResult.Fail(MakerStudioErrorCodes.RecoveryRequired, ex.Message,
                        new MakerRecoveryDetails { PackId = Path.GetFileName(Files.Root), Resource = MakerStudioPaths.Transactions, BlockedOperationId = "" });
                }

                // Finish preservation/resolution before considering the original blocked intent it retires.
                foreach (var path in files.Where(p => p.EndsWith(".pending.json", StringComparison.Ordinal))
                    .OrderByDescending(p => MakerRecoveryStore.IsResolutionIntent(Files, p)))
                {
                    var name = Path.GetFileName(path);
                    var operation = name.Substring(0, name.Length - ".pending.json".Length);
                    if (!RecoveryWritable)
                        return MakerCommitResult.Fail(MakerStudioErrorCodes.RecoveryRequired, "Pending recovery requires an editable pack.",
                            new MakerRecoveryDetails { PackId = Path.GetFileName(Files.Root), Resource = path, BlockedOperationId = operation });
                    if (!MakerStudioPaths.IsOperationId(operation))
                    {
                        return MakerCommitResult.Fail(MakerStudioErrorCodes.RecoveryRequired, "unknown pending Studio transaction " + path,
                            new MakerRecoveryDetails { PackId = Path.GetFileName(Files.Root), Resource = path, BlockedOperationId = operation });
                    }

                    var recovered = NewCommit().Recover(operation);
                    if (!recovered.Ok)
                    {
                        return MakerCommitResult.Fail(recovered.Error!,
                            "pack=" + Path.GetFileName(Files.Root) + "; resource=" + path
                            + "; operationId=" + operation + "; " + recovered.Message, recovered.Details);
                    }
                }

                if (RecoveryWritable) NewCommit().ScavengeOrphans();
                return MakerCommitResult.Success();
            }
        }

        public MakerCommitResult Commit(string operationId, IReadOnlyList<MakerCommitFile> targets, string kind, Action<int>? afterWrite = null)
        {
            lock (SyncRoot)
            {
                var result = new MakerCommit(Files, kind, _quota, _commitBoundary).Execute(operationId, targets, afterWrite);
                if (result.Ok) foreach (var target in targets) RememberCommitted(target);
                return result;
            }
        }

        internal void RememberCommitted(MakerCommitFile target)
        {
            if (_readFingerprints.ContainsKey(target.Path))
                _readFingerprints[target.Path] = target.Delete ? null : MakerPackFiles.Fingerprint(target.Bytes);
        }

        public MakerCommitFile Target(string path, byte[] bytes)
        {
            lock (SyncRoot)
                return Target(path, bytes, _readFingerprints.TryGetValue(path, out var before) ? before : FingerprintOf(path));
        }

        /// <summary>Builds a replacement guarded by the fingerprint of the input snapshot.</summary>
        public MakerCommitFile Target(string path, byte[] bytes, string? beforeFingerprint) => new MakerCommitFile
        {
            Path = path, BeforeFingerprint = beforeFingerprint, Bytes = bytes,
        };

        /// <summary>Reads once for parsing, backup and the eventual commit precondition, preserving the UTF-8 BOM.</summary>
        internal string? ReadTextSnapshot(string path, out byte[]? bytes, out bool bom)
        {
            lock (SyncRoot)
            {
                bytes = Files.ReadBytes(path);
                _readFingerprints[path] = bytes is null ? null : MakerPackFiles.Fingerprint(bytes);
                bom = bytes != null && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                return bytes is null ? null : bom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
            }
        }

        public MakerCommitFile DeleteTarget(string path) => new MakerCommitFile
        {
            Path = path,
            BeforeFingerprint = _readFingerprints.TryGetValue(path, out var before) ? before : FingerprintOf(path),
            Delete = true,
        };

        public MakerCommitFile? HeaderTarget(MakerProjectHeader header)
        {
            if (_readFingerprints.TryGetValue(MakerStudioPaths.Project, out var before))
                return before != null ? null : Target(MakerStudioPaths.Project, Utf8(MakerProjectJson.WriteHeader(header)), null);
            if (Files.Exists(MakerStudioPaths.Project))
            {
                return null;
            }

            return Target(MakerStudioPaths.Project, Utf8(MakerProjectJson.WriteHeader(header)));
        }

        public string? FingerprintOf(string path)
        {
            var bytes = Files.ReadBytes(path);
            return bytes is null ? null : MakerPackFiles.Fingerprint(bytes);
        }

        public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

        internal void ReconcileLineTakes(string lang, string id, MakerLineDocument line, out bool drifted, bool fullHash = false)
        {
            drifted = false;
            foreach (var take in line.Takes)
            {
                var sourcePath = MakerStudioPaths.TakeAudio(lang, id, take.Id, MakerTakeRules.SourceKind);
                var sourceFp = AudioFingerprint(sourcePath, fullHash, out var sourceBytes);
                if (sourceFp is null)
                {
                    if (take.SourcePresent || take.SourceFingerprint != null || take.RenderReady || take.RenderFingerprint != null)
                    {
                        drifted = true;
                        take.SourcePresent = false;
                        take.SourceDurationSec = null;
                        take.SourceRevision = null;
                        take.SourceFingerprint = null;
                        ClearRender(take);
                    }
                }
                else if (!take.SourcePresent || !string.Equals(take.SourceFingerprint, sourceFp, StringComparison.Ordinal))
                {
                    drifted = true;
                    sourceBytes ??= ReadAudio(sourcePath);
                    if (sourceBytes != null
                        && MakerTakeRules.InspectAudio(MakerTakeRules.SourceKind, sourceBytes, out _, out var sDur, out _, out _))
                    {
                        take.SourcePresent = true;
                        take.SourceDurationSec = sDur;
                        take.SourceRevision = MakerProjectRevision.StableId(sourceFp);
                        take.SourceFingerprint = sourceFp;
                    }
                    else
                    {
                        take.SourcePresent = false;
                        take.SourceDurationSec = null;
                        take.SourceRevision = null;
                        take.SourceFingerprint = null;
                    }
                }

                // A render is ready only as the server validated it, for the source it was made from: never adopt a render
                // file nobody uploaded through Studio, and never keep one whose source changed (REVIEW-FIXES-M5c item 1).
                var renderPath = MakerStudioPaths.TakeAudio(lang, id, take.Id, MakerTakeRules.RenderKind);
                var ready = take.RenderReady && take.SourcePresent && take.RenderFingerprint != null
                    && string.Equals(take.RenderSourceFingerprint, take.SourceFingerprint, StringComparison.Ordinal)
                    && string.Equals(AudioFingerprint(renderPath, fullHash, out _), take.RenderFingerprint, StringComparison.Ordinal);
                if (!ready && (take.RenderReady || take.RenderFingerprint != null || take.RenderDurationSec != null))
                {
                    drifted = true;
                    ClearRender(take);
                }
            }

            if (line.FavouriteTakeId != null && !line.Takes.Any(t => string.Equals(t.Id, line.FavouriteTakeId, StringComparison.Ordinal)))
            {
                drifted = true;
                line.FavouriteTakeId = null;
            }
        }

        private static string TakeDriftFingerprint(MakerLineDocument line) =>
            MakerPackFiles.Fingerprint(Utf8(string.Join("\n", line.Takes.Select(t =>
                t.Id + ":" + (t.SourceFingerprint ?? "<none>") + ":" + (t.RenderFingerprint ?? "<none>")))));

        private MakerCommit NewCommit() => new MakerCommit(Files, _quota, _commitBoundary);

        private bool PublicationIntact(string lang, string id, MakerLineDocument line)
        {
            if (line.PublishedClip is null) return false;
            var publication = new MakerClipStore(this).Read(lang, line.PublishedClip).Publication;
            return publication != null && publication.LineId == id && publication.TakeId == line.PublishedTakeId;
        }

        /// <summary>
        /// A stat-cached identity of any pack file: its fingerprint when it is WAV-sized, otherwise its size and time
        /// (never read); null when absent. Used for the runtime voice files a clip revision is derived from.
        /// </summary>
        internal string? FileStamp(string relativePath)
        {
            var fingerprint = AudioFingerprint(relativePath, false, out _);
            if (fingerprint != null) return fingerprint;
            var info = new FileInfo(Files.Resolve(relativePath));
            return info.Exists ? "large:" + info.Length + ":" + info.LastWriteTimeUtc.Ticks : null;
        }

        internal static void ClearRender(MakerTakeRecord take)
        {
            take.RenderReady = false;
            take.RenderDurationSec = null;
            take.RenderFingerprint = null;
            take.RenderSourceFingerprint = null;
        }

        // Process-wide: path -> (length, last write, fingerprint). A line read stats take WAVs and hashes one only when
        // its size or time changed since it was last hashed or written here (REVIEW-FIXES-M5c item 2).
        private static readonly ConcurrentDictionary<string, (long Length, long Ticks, string Fingerprint)> AudioStamps =
            new ConcurrentDictionary<string, (long, long, string)>(StringComparer.Ordinal);

        /// <summary>
        /// The fingerprint of a take WAV, or null when it is absent or larger than any valid WAV (never read then).
        /// Read in full only when <paramref name="fullHash"/> or when its size or time changed; <paramref name="bytes"/>
        /// holds the bytes when they were read.
        /// </summary>
        internal string? AudioFingerprint(string relativePath, bool fullHash, out byte[]? bytes)
        {
            bytes = null;
            var full = Files.Resolve(relativePath);
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > MakerEditRules.MaxVoiceBytes)
            {
                AudioStamps.TryRemove(full, out _);
                return null;
            }

            var ticks = info.LastWriteTimeUtc.Ticks;
            if (!fullHash && AudioStamps.TryGetValue(full, out var stamp) && stamp.Length == info.Length && stamp.Ticks == ticks)
            {
                return stamp.Fingerprint;
            }

            bytes = ReadAudio(relativePath);
            if (bytes is null) return null;
            var fingerprint = MakerPackFiles.Fingerprint(bytes);
            AudioStamps[full] = (bytes.Length, ticks, fingerprint);
            return fingerprint;
        }

        /// <summary>Records the fingerprint of a take WAV this process has just written, so reads need not hash it.</summary>
        internal void RememberAudio(string relativePath, string fingerprint)
        {
            try
            {
                var full = Files.Resolve(relativePath);
                var info = new FileInfo(full);
                if (info.Exists) AudioStamps[full] = (info.Length, info.LastWriteTimeUtc.Ticks, fingerprint);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Only a cache: the next read hashes the file.
            }
        }

        private byte[]? ReadAudio(string relativePath)
        {
            TakeAudioReads++;
            var bytes = Files.ReadBytes(relativePath);
            return bytes != null && bytes.Length <= MakerEditRules.MaxVoiceBytes ? bytes : null;
        }

        internal string? CaseCollision(string path)
        {
            var parent = Files.Root;
            foreach (var segment in path.Split('/'))
            {
                if (!Directory.Exists(parent)) break;
                foreach (var entry in Directory.GetFileSystemEntries(parent))
                {
                    var actual = Path.GetFileName(entry);
                    if (string.Equals(actual, segment, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(actual, segment, StringComparison.Ordinal))
                        return path + " collides by case with " + actual;
                }
                parent = Path.Combine(parent, segment);
            }
            return null;
        }
    }
}
