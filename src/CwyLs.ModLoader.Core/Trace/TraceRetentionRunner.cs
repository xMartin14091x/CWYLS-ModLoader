using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>What happened to one file during a retention pass. Values are the <c>action</c> field of the trace line.</summary>
    public static class TraceRetentionAction
    {
        /// <summary>A part was archived and verified; its original was removed.</summary>
        public const string Compressed = "compressed";

        /// <summary>A part could not be archived; its original is untouched.</summary>
        public const string CompressFailed = "compressFailed";

        /// <summary>An interrupted archive (<c>.gz.tmp</c>) of a part that still exists was removed.</summary>
        public const string TempRemoved = "tempRemoved";

        /// <summary>A file of a session over the cap was deleted.</summary>
        public const string Deleted = "deleted";

        /// <summary>A file could not be removed.</summary>
        public const string DeleteFailed = "deleteFailed";
    }

    /// <summary>One per-file result, emitted as it happens (the plugin writes it to the trace file only).</summary>
    /// <param name="Action">One of the <see cref="TraceRetentionAction"/> values.</param>
    /// <param name="File">Bare file name.</param>
    /// <param name="BytesBefore">Size before the action.</param>
    /// <param name="BytesAfter">Size after it: the archive's size, or 0 for a removal.</param>
    /// <param name="Detail">The error or note, or null.</param>
    public sealed record TraceRetentionStep(string Action, string File, long BytesBefore, long BytesAfter, string? Detail);

    /// <summary>Totals of one retention pass, and the one-line summary the console shows.</summary>
    public sealed class TraceRetentionReport
    {
        /// <summary>Parts archived and removed.</summary>
        public int PartsCompressed { get; internal set; }

        /// <summary>Combined size of those parts before archiving.</summary>
        public long CompressedFromBytes { get; internal set; }

        /// <summary>Combined size of their archives.</summary>
        public long CompressedToBytes { get; internal set; }

        /// <summary>Space archiving freed.</summary>
        public long SavedBytes => CompressedFromBytes - CompressedToBytes;

        /// <summary>Interrupted archives cleared.</summary>
        public int TempsRemoved { get; internal set; }

        /// <summary>Whole sessions deleted to get under the cap.</summary>
        public int SessionsDeleted { get; internal set; }

        /// <summary>Files those deletions removed.</summary>
        public int FilesDeleted { get; internal set; }

        /// <summary>Space those deletions freed.</summary>
        public long DeletedBytes { get; internal set; }

        /// <summary>Files that could not be archived or removed. Each has its own trace line.</summary>
        public int Failures { get; internal set; }

        /// <summary>Recognised trace files on disk after the pass, live session included.</summary>
        public long FolderBytes { get; internal set; }

        /// <summary>The cap; 0 = off.</summary>
        public long CapBytes { get; internal set; }

        /// <summary>Whether the folder ended at or under the cap (always true with no cap).</summary>
        public bool CapReached => CapBytes <= 0 || FolderBytes <= CapBytes;

        /// <summary>True when the pass stopped early because the game was quitting.</summary>
        public bool Cancelled { get; internal set; }

        /// <summary>True when the console line should be a warning: something failed, or the cap could not be met.</summary>
        public bool NeedsAttention => Failures > 0 || !CapReached;

        /// <summary>The console line's primary column, e.g. <c>retention: compressed 4 parts, saved 651 MB, deleted 0 sessions</c>.</summary>
        public string Summary() =>
            "retention: compressed " + PartsCompressed.ToString(CultureInfo.InvariantCulture)
            + (PartsCompressed == 1 ? " part" : " parts")
            + ", saved " + WholeMegabytes(SavedBytes) + " MB"
            + ", deleted " + SessionsDeleted.ToString(CultureInfo.InvariantCulture)
            + (SessionsDeleted == 1 ? " session" : " sessions")
            + (Cancelled ? " (stopped early: quitting)" : string.Empty);

        /// <summary>Bytes as a whole number of megabytes, e.g. <c>651</c>.</summary>
        /// <param name="bytes">A size in bytes.</param>
        public static string WholeMegabytes(long bytes) =>
            (bytes / (double)TraceRetentionPolicy.BytesPerMB).ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The thin IO half of <see cref="TraceRetentionPolicy"/>: lists the folder, runs step 1 one file
    /// at a time, lists again, runs step 2. Blocking and slow on purpose - run it on a background
    /// thread, never on Unity's main thread.
    /// </summary>
    /// <remarks>
    /// Never throws for a single file: every failure becomes a <see cref="TraceRetentionAction.CompressFailed"/>
    /// or <see cref="TraceRetentionAction.DeleteFailed"/> step, counted in
    /// <see cref="TraceRetentionReport.Failures"/>, and the pass moves on.
    /// </remarks>
    public sealed class TraceRetentionRunner
    {
        private readonly string _directory;
        private readonly string _currentStamp;
        private readonly TraceRetentionPolicy _policy;
        private readonly Action<TraceRetentionStep> _onStep;

        /// <summary>Creates a runner for one folder and one live session.</summary>
        /// <param name="directory">The trace folder.</param>
        /// <param name="currentStamp">The live session's stamp; its files are never touched.</param>
        /// <param name="policy">What to keep.</param>
        /// <param name="onStep">Receives every per-file result as it happens. Required: it is where failures are logged.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="currentStamp"/> is not a valid stamp.</exception>
        public TraceRetentionRunner(
            string directory, string currentStamp, TraceRetentionPolicy policy, Action<TraceRetentionStep> onStep)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _onStep = onStep ?? throw new ArgumentNullException(nameof(onStep));

            if (!TraceFileName.IsStamp(currentStamp))
            {
                throw new ArgumentException(
                    "the current session stamp must be " + TraceFileName.StampFormat + ", got '" + currentStamp + "'",
                    nameof(currentStamp));
            }

            _currentStamp = currentStamp;
        }

        /// <summary>Lists the recognised trace files in a folder (top level only), with their sizes.</summary>
        /// <param name="directory">The trace folder.</param>
        /// <returns>The files; empty when the folder does not exist.</returns>
        public static IReadOnlyList<TraceFile> List(string directory)
        {
            var files = new List<TraceFile>();
            if (!Directory.Exists(directory))
            {
                return files;
            }

            foreach (var info in new DirectoryInfo(directory).GetFiles())
            {
                if (!TraceFileName.TryParse(info.Name, out _))
                {
                    continue;
                }

                try
                {
                    files.Add(new TraceFile(info.Name, info.Length));
                }
                catch (IOException ex)
                {
                    // Removed between the listing and the size read: not ours to count any more.
                    TraceDiagnostics.Report("TRACE: retention skipped " + info.Name + " while listing", ex);
                }
            }

            return files;
        }

        /// <summary>Runs one pass: archive past parts, then delete oldest sessions while over the cap.</summary>
        /// <param name="cancel">Checked between files and between buffers; set it when the game quits.</param>
        /// <returns>What was done.</returns>
        public TraceRetentionReport Run(CancellationToken cancel)
        {
            var report = new TraceRetentionReport { CapBytes = _policy.MaxTotalBytes };

            var compression = _policy.PlanCompression(List(_directory), _currentStamp);

            foreach (var temp in compression.StaleTemps)
            {
                if (cancel.IsCancellationRequested)
                {
                    return Finish(report, cancelled: true);
                }

                if (Remove(temp, TraceRetentionAction.TempRemoved, report))
                {
                    report.TempsRemoved++;
                }
            }

            // One file at a time, oldest session first.
            foreach (var part in compression.ToCompress)
            {
                if (cancel.IsCancellationRequested)
                {
                    break;
                }

                Compress(part, report, cancel);
            }

            if (cancel.IsCancellationRequested)
            {
                return Finish(report, cancelled: true);
            }

            var deletion = _policy.PlanDeletion(List(_directory), _currentStamp);
            foreach (var session in deletion.ToDelete)
            {
                // Checked between sessions only, so a session is never left half deleted.
                if (cancel.IsCancellationRequested)
                {
                    return Finish(report, cancelled: true);
                }

                var removed = 0;
                foreach (var file in session.Files)
                {
                    if (Remove(file, TraceRetentionAction.Deleted, report))
                    {
                        removed++;
                        report.FilesDeleted++;
                        report.DeletedBytes += file.Bytes;
                    }
                }

                if (removed == session.Files.Count)
                {
                    report.SessionsDeleted++;
                }
            }

            return Finish(report, cancelled: false);
        }

        private void Compress(TraceFile part, TraceRetentionReport report, CancellationToken cancel)
        {
            // The policy only plans names it parsed, so the fallback is never expected to be used.
            TraceFileName.TryParse(part.Name, out var name);
            var archiveName = name?.ArchiveName ?? part.Name + TraceFileName.GzipExtension;

            TraceArchiveResult result;
            try
            {
                result = TraceArchive.CompressAndReplace(
                    Path.Combine(_directory, part.Name), Path.Combine(_directory, archiveName), cancel);
            }
            catch (OperationCanceledException)
            {
                // Not a failure: the game is quitting. Recorded so the trace shows where it stopped.
                Emit(new TraceRetentionStep(
                    TraceRetentionAction.CompressFailed, part.Name, part.Bytes, 0L, "cancelled: the game is quitting; original kept"));
                return;
            }
            catch (Exception ex)
            {
                report.Failures++;
                Emit(new TraceRetentionStep(
                    TraceRetentionAction.CompressFailed, part.Name, part.Bytes, 0L,
                    ex.GetType().Name + ": " + ex.Message + "; original kept"));
                return;
            }

            report.PartsCompressed++;
            report.CompressedFromBytes += result.OriginalBytes;
            report.CompressedToBytes += result.ArchiveBytes;
            Emit(new TraceRetentionStep(
                TraceRetentionAction.Compressed, part.Name, result.OriginalBytes, result.ArchiveBytes, result.Note));
        }

        private bool Remove(TraceFile file, string action, TraceRetentionReport report)
        {
            try
            {
                File.Delete(Path.Combine(_directory, file.Name));
            }
            catch (Exception ex)
            {
                report.Failures++;
                Emit(new TraceRetentionStep(
                    TraceRetentionAction.DeleteFailed, file.Name, file.Bytes, file.Bytes, ex.GetType().Name + ": " + ex.Message));
                return false;
            }

            Emit(new TraceRetentionStep(action, file.Name, file.Bytes, 0L, null));
            return true;
        }

        private TraceRetentionReport Finish(TraceRetentionReport report, bool cancelled)
        {
            report.Cancelled = cancelled;
            report.FolderBytes = List(_directory).Sum(f => f.Bytes);
            return report;
        }

        private void Emit(TraceRetentionStep step)
        {
            try
            {
                _onStep(step);
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("TRACE: retention could not report " + step.Action + " " + step.File, ex);
            }
        }
    }
}
