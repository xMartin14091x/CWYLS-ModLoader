using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CwyLs.ModLoader.Core.Backups
{
    /// <summary>What one launch backup run should do.</summary>
    public sealed class SaveBackupRequest
    {
        /// <summary>Creates a request.</summary>
        /// <param name="saveFolder">The game's save folder (<c>Application.persistentDataPath/SaveData</c>).</param>
        /// <param name="backupsRoot">The backups folder (<c>&lt;Application.persistentDataPath&gt;/CWYLS Backups</c>, next to the save folder).</param>
        /// <param name="now">The local time now; names the backup and goes into its marker.</param>
        /// <param name="loaderVersion">For the marker.</param>
        /// <param name="gameVersion">For the marker.</param>
        /// <param name="rules">The retention rules; null means <see cref="SaveBackupRules.Default"/>.</param>
        public SaveBackupRequest(string saveFolder, string backupsRoot, DateTimeOffset now, string? loaderVersion, string? gameVersion, SaveBackupRules? rules)
        {
            SaveFolder = saveFolder ?? throw new ArgumentNullException(nameof(saveFolder));
            BackupsRoot = backupsRoot ?? throw new ArgumentNullException(nameof(backupsRoot));
            Now = now;
            LoaderVersion = loaderVersion;
            GameVersion = gameVersion;
            Rules = rules ?? SaveBackupRules.Default;
        }

        /// <summary>The game's save folder.</summary>
        public string SaveFolder { get; }

        /// <summary>The backups folder.</summary>
        public string BackupsRoot { get; }

        /// <summary>The local time now.</summary>
        public DateTimeOffset Now { get; }

        /// <summary>The loader version for the marker.</summary>
        public string? LoaderVersion { get; }

        /// <summary>The game version for the marker.</summary>
        public string? GameVersion { get; }

        /// <summary>The retention rules.</summary>
        public SaveBackupRules Rules { get; }

        /// <summary>
        /// Test seam, null in the game: called with the <c>.partial-</c> folder once its copy and marker are written,
        /// right before the rename. Throwing from it is how the tests stand in for a failure at the last moment.
        /// </summary>
        public Action<string>? BeforeRename { get; set; }
    }

    /// <summary>How a launch backup run ended.</summary>
    public enum SaveBackupOutcome
    {
        /// <summary>A new backup was made (and old ones pruned).</summary>
        BackedUp,

        /// <summary>The save is identical to the newest backup; nothing new was made (old ones were still pruned).</summary>
        Unchanged,

        /// <summary>There is no save folder yet (first launch ever); nothing was done.</summary>
        NoSaveFolder,

        /// <summary>The save folder holds no files; nothing was done.</summary>
        EmptySaveFolder,

        /// <summary>Something failed; see <see cref="SaveBackupResult.Failure"/>. No half backup is left behind and nothing was pruned.</summary>
        Failed,
    }

    /// <summary>The result of one run: what the SETUP line and the WARN line are made from.</summary>
    public sealed class SaveBackupResult
    {
        /// <summary>How it ended.</summary>
        public SaveBackupOutcome Outcome { get; internal set; }

        /// <summary>The new backup's folder name (<see cref="SaveBackupOutcome.BackedUp"/>).</summary>
        public string? Name { get; internal set; }

        /// <summary>The new backup's full path (<see cref="SaveBackupOutcome.BackedUp"/>).</summary>
        public string? Folder { get; internal set; }

        /// <summary>The newest backup the save still matches (<see cref="SaveBackupOutcome.Unchanged"/>).</summary>
        public string? UnchangedSince { get; internal set; }

        /// <summary>Files in the save (the copy's count when a backup was made).</summary>
        public int Files { get; internal set; }

        /// <summary>Bytes in the save.</summary>
        public long Bytes { get; internal set; }

        /// <summary>The save's content hash.</summary>
        public string? ContentSha256 { get; internal set; }

        /// <summary>Backups kept by the rules after pruning (the new one included).</summary>
        public int Kept { get; internal set; }

        /// <summary>Old backups deleted.</summary>
        public int Removed { get; internal set; }

        /// <summary>Old backups the rules dropped but that could not be deleted, with why. Not a failure of the run.</summary>
        public IReadOnlyList<string> PruneProblems { get; internal set; } = Array.Empty<string>();

        /// <summary>Folders in the backups folder that are not ours (left alone).</summary>
        public int LeftAlone { get; internal set; }

        /// <summary>Links in the save folder that were not followed (normally none).</summary>
        public IReadOnlyList<string> SkippedLinks { get; internal set; } = Array.Empty<string>();

        /// <summary>Why the run failed (<see cref="SaveBackupOutcome.Failed"/>).</summary>
        public string? Failure { get; internal set; }

        /// <summary>How long the whole run took.</summary>
        public long ElapsedMs { get; internal set; }
    }

    /// <summary>The result of one prune pass.</summary>
    public sealed class SaveBackupPruneResult
    {
        internal SaveBackupPruneResult(IReadOnlyList<SaveBackupDecision> decisions, int removed, IReadOnlyList<string> problems, int leftAlone)
        {
            Decisions = decisions;
            Removed = removed;
            Problems = problems;
            LeftAlone = leftAlone;
        }

        /// <summary>The policy's decision for every backup, newest first.</summary>
        public IReadOnlyList<SaveBackupDecision> Decisions { get; }

        /// <summary>How many the rules keep.</summary>
        public int Kept
        {
            get
            {
                var kept = 0;
                foreach (var decision in Decisions)
                {
                    if (decision.Keep)
                    {
                        kept++;
                    }
                }

                return kept;
            }
        }

        /// <summary>How many were deleted.</summary>
        public int Removed { get; }

        /// <summary>Dropped backups that could not be deleted: <c>name: why</c>.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>Folders that are not ours (never touched).</summary>
        public int LeftAlone { get; }
    }

    /// <summary>
    /// One launch save backup, Unity-free: hash the save, compare with the newest backup, copy it to a
    /// <c>.partial-</c> folder, verify, write the marker, rename, then prune by <see cref="SaveBackupPolicy"/>.
    /// Never throws: every failure is a <see cref="SaveBackupOutcome.Failed"/> result.
    /// </summary>
    /// <remarks>
    /// Safety rules, all enforced here:
    /// <list type="bullet">
    ///   <item><description>The backups folder may not be the save folder or inside it (Steam Cloud syncs the save folder), nor the other way round.</description></item>
    ///   <item><description>A backup only gets its final name once its files, their verification and its marker are all written, so a crash never leaves a half backup that looks valid; a leftover <c>.partial-</c> folder is never mistaken for a backup.</description></item>
    ///   <item><description>Pruning deletes only folders named <c>save-YYYYMMDD-HHMMSS</c> holding a valid <c>backup.json</c> (checked again right before the delete) and containing no links. Nothing else is ever deleted, except this run's own <c>.partial-</c> folder after a failure.</description></item>
    ///   <item><description>Nothing is pruned when the new backup failed.</description></item>
    /// </list>
    /// </remarks>
    public static class SaveBackupRunner
    {
        /// <summary>Runs one launch backup. Never throws.</summary>
        /// <param name="request">What to do.</param>
        public static SaveBackupResult Run(SaveBackupRequest request)
        {
            var result = new SaveBackupResult();
            var clock = Stopwatch.StartNew();
            try
            {
                RunCore(request, result);
            }
            catch (Exception ex)
            {
                result.Outcome = SaveBackupOutcome.Failed;
                result.Failure = ex.GetType().Name + ": " + ex.Message;
            }

            result.ElapsedMs = clock.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// Applies the rules to the backups folder and deletes what no rule keeps. Folders that are not ours are
        /// never touched. Never throws for a single folder; a delete that fails is reported in
        /// <see cref="SaveBackupPruneResult.Problems"/>.
        /// </summary>
        /// <param name="backupsRoot">The backups folder.</param>
        /// <param name="rules">The rules.</param>
        public static SaveBackupPruneResult Prune(string backupsRoot, SaveBackupRules rules)
        {
            var inventory = SaveBackupInventory.Scan(backupsRoot);
            var decisions = SaveBackupPolicy.Apply(inventory.Stamps, rules);
            var removed = 0;
            var problems = new List<string>();

            foreach (var decision in decisions)
            {
                if (decision.Keep)
                {
                    continue;
                }

                var folder = Path.Combine(backupsRoot, decision.Name);
                if (!SaveBackupInventory.TryRead(folder, out _, out _, out var reason))
                {
                    problems.Add(decision.Name + ": " + reason + "; left alone");
                    continue;
                }

                if (SaveBackupContent.TryDeleteBackupFolder(folder, out var problem))
                {
                    removed++;
                }
                else
                {
                    problems.Add(decision.Name + ": " + problem);
                }
            }

            return new SaveBackupPruneResult(decisions, removed, problems, inventory.LeftAlone.Count);
        }

        /// <summary>
        /// True when <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it. Compared on full
        /// paths, ignoring case (the cautious answer on case-insensitive macOS and Windows volumes).
        /// </summary>
        public static bool IsSameOrInside(string path, string folder)
        {
            var p = Trim(Path.GetFullPath(path));
            var f = Trim(Path.GetFullPath(folder));
            if (string.Equals(p, f, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return p.Length > f.Length
                && p.StartsWith(f, StringComparison.OrdinalIgnoreCase)
                && (p[f.Length] == Path.DirectorySeparatorChar || p[f.Length] == Path.AltDirectorySeparatorChar);
        }

        private static string Trim(string path)
        {
            var root = Path.GetPathRoot(path) ?? string.Empty;
            while (path.Length > root.Length
                && (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)))
            {
                path = path.Substring(0, path.Length - 1);
            }

            return path;
        }

        private static void RunCore(SaveBackupRequest request, SaveBackupResult result)
        {
            var source = Path.GetFullPath(request.SaveFolder);
            var root = Path.GetFullPath(request.BackupsRoot);

            if (IsSameOrInside(root, source))
            {
                Fail(result, "the backups folder " + root + " is inside the save folder " + source
                    + "; refusing, so Steam Cloud never syncs backups");
                return;
            }

            if (IsSameOrInside(source, root))
            {
                Fail(result, "the save folder " + source + " is inside the backups folder " + root + "; refusing");
                return;
            }

            if (!Directory.Exists(source))
            {
                result.Outcome = SaveBackupOutcome.NoSaveFolder;
                return;
            }

            var live = SaveBackupContent.Measure(source);
            result.Files = live.Files;
            result.Bytes = live.Bytes;
            result.ContentSha256 = live.Sha256;
            result.SkippedLinks = live.SkippedLinks;
            if (live.Files == 0)
            {
                result.Outcome = SaveBackupOutcome.EmptySaveFolder;
                return;
            }

            Directory.CreateDirectory(root);
            var inventory = SaveBackupInventory.Scan(root);
            var newest = inventory.Backups.Count > 0 ? inventory.Backups[0] : null;

            if (newest != null && string.Equals(newest.Manifest.ContentSha256, live.Sha256, StringComparison.Ordinal))
            {
                result.Outcome = SaveBackupOutcome.Unchanged;
                result.UnchangedSince = newest.Name;
            }
            else if (!MakeBackup(request, source, root, result))
            {
                return;
            }

            var prune = Prune(root, request.Rules);
            result.Kept = prune.Kept;
            result.Removed = prune.Removed;
            result.PruneProblems = prune.Problems;
            result.LeftAlone = prune.LeftAlone;
        }

        /// <summary>Copies the save to a <c>.partial-</c> folder, verifies it, writes the marker and renames it. False after a failure (already recorded).</summary>
        private static bool MakeBackup(SaveBackupRequest request, string source, string root, SaveBackupResult result)
        {
            var name = SaveBackupNames.FolderName(request.Now.DateTime);
            var final = Path.Combine(root, name);
            if (Directory.Exists(final) || File.Exists(final))
            {
                Fail(result, "a folder named " + name + " is already in " + root + "; it was left alone and no backup was made");
                return false;
            }

            var partial = Path.Combine(root, SaveBackupNames.PartialName(name, Guid.NewGuid().ToString("N").Substring(0, 8)));
            var created = false;
            try
            {
                Directory.CreateDirectory(partial);
                created = true;

                var copy = SaveBackupContent.CopyTree(source, Path.Combine(partial, SaveBackupNames.SaveFolderName));
                if (copy.Files == 0)
                {
                    throw new IOException("the save folder was empty when it was copied");
                }

                // Read the copy back from disk: the marker vouches for what is there, not for what we meant to write.
                var check = SaveBackupContent.Measure(Path.Combine(partial, SaveBackupNames.SaveFolderName));
                if (!string.Equals(check.Sha256, copy.Sha256, StringComparison.Ordinal) || check.Files != copy.Files)
                {
                    throw new IOException("the copy does not read back the same as what was written");
                }

                var manifest = new SaveBackupManifest(
                    SaveBackupManifest.CurrentSchema,
                    request.Now,
                    request.LoaderVersion,
                    request.GameVersion,
                    copy.Files,
                    copy.Bytes,
                    copy.Sha256);
                WriteMarker(Path.Combine(partial, SaveBackupNames.MarkerFileName), manifest.ToJson());

                request.BeforeRename?.Invoke(partial);
                Directory.Move(partial, final);

                result.Outcome = SaveBackupOutcome.BackedUp;
                result.Name = name;
                result.Folder = final;
                result.Files = copy.Files;
                result.Bytes = copy.Bytes;
                result.ContentSha256 = copy.Sha256;
                return true;
            }
            catch (Exception ex)
            {
                // Only this run's own .partial- folder is ever removed here: it was created a moment ago and holds
                // nothing but this run's copy. If even that fails, the name keeps it from looking like a backup.
                var cleanup = string.Empty;
                if (created && Directory.Exists(partial) && !SaveBackupContent.TryDeleteBackupFolder(partial, out var problem))
                {
                    cleanup = "; the unfinished copy " + Path.GetFileName(partial) + " could not be removed (" + problem + ")";
                }

                Fail(result, ex.GetType().Name + ": " + ex.Message + cleanup);
                return false;
            }
        }

        private static void WriteMarker(string path, string json)
        {
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(json);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(bytes, 0, bytes.Length);
        }

        private static void Fail(SaveBackupResult result, string why)
        {
            result.Outcome = SaveBackupOutcome.Failed;
            result.Failure = why;
        }
    }
}
