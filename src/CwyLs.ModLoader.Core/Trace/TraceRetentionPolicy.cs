using System;
using System.Collections.Generic;
using System.Linq;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>One file in the trace folder, as the policy sees it: a name and a size. No IO.</summary>
    /// <param name="Name">Bare file name, e.g. <c>trace-20260924-122149-part2.jsonl</c>.</param>
    /// <param name="Bytes">Size on disk.</param>
    public sealed record TraceFile(string Name, long Bytes);

    /// <summary>Every recognised file of one session (one game launch), in part order.</summary>
    public sealed class TraceSession
    {
        /// <summary>Creates a session.</summary>
        /// <param name="stamp">The session stamp.</param>
        /// <param name="files">Its files, in part order.</param>
        public TraceSession(string stamp, IReadOnlyList<TraceFile> files)
        {
            Stamp = stamp;
            Files = files;
            Bytes = files.Sum(f => f.Bytes);
        }

        /// <summary>The session stamp, e.g. <c>20260924-122149</c>.</summary>
        public string Stamp { get; }

        /// <summary>Plain parts, archives and temps of this session, in part order.</summary>
        public IReadOnlyList<TraceFile> Files { get; }

        /// <summary>Total size of <see cref="Files"/>.</summary>
        public long Bytes { get; }
    }

    /// <summary>Step 1 of retention: which parts to archive, and which leftovers of a crashed archive to clear.</summary>
    public sealed class TraceCompressionPlan
    {
        /// <summary>Creates a plan.</summary>
        /// <param name="toCompress">Plain parts of past sessions, oldest session first.</param>
        /// <param name="staleTemps">Unfinished archives whose plain part still exists.</param>
        public TraceCompressionPlan(IReadOnlyList<TraceFile> toCompress, IReadOnlyList<TraceFile> staleTemps)
        {
            ToCompress = toCompress;
            StaleTemps = staleTemps;
        }

        /// <summary>Plain <c>.jsonl</c> parts of past sessions, oldest session first, then by part.</summary>
        public IReadOnlyList<TraceFile> ToCompress { get; }

        /// <summary>
        /// <c>.gz.tmp</c> files of past sessions whose plain part is still on disk: an archive that
        /// was interrupted before it was verified. Safe to remove - the original was never touched.
        /// </summary>
        public IReadOnlyList<TraceFile> StaleTemps { get; }
    }

    /// <summary>Step 2 of retention: which whole sessions to delete to get under the cap.</summary>
    public sealed class TraceDeletionPlan
    {
        /// <summary>Creates a plan.</summary>
        /// <param name="toDelete">Sessions to delete, oldest first.</param>
        /// <param name="bytesBefore">Folder total before deleting.</param>
        /// <param name="bytesAfter">Folder total once <paramref name="toDelete"/> is gone.</param>
        /// <param name="capBytes">The cap; 0 means no cap.</param>
        /// <param name="protectedPastSessions">Past sessions kept by the minimum-sessions guard.</param>
        public TraceDeletionPlan(
            IReadOnlyList<TraceSession> toDelete, long bytesBefore, long bytesAfter, long capBytes, int protectedPastSessions)
        {
            ToDelete = toDelete;
            BytesBefore = bytesBefore;
            BytesAfter = bytesAfter;
            CapBytes = capBytes;
            ProtectedPastSessions = protectedPastSessions;
        }

        /// <summary>Whole sessions to delete (every part), oldest first.</summary>
        public IReadOnlyList<TraceSession> ToDelete { get; }

        /// <summary>Total of every recognised trace file before deleting, live session included.</summary>
        public long BytesBefore { get; }

        /// <summary>The same total once <see cref="ToDelete"/> is gone.</summary>
        public long BytesAfter { get; }

        /// <summary>The cap in bytes; 0 means the cap is off.</summary>
        public long CapBytes { get; }

        /// <summary>How many past sessions the minimum-sessions guard kept out of reach.</summary>
        public int ProtectedPastSessions { get; }

        /// <summary>
        /// True when the folder ends at or under the cap (or there is no cap). False means the
        /// live session plus the protected sessions alone are over it.
        /// </summary>
        public bool CapReached => CapBytes <= 0 || BytesAfter <= CapBytes;
    }

    /// <summary>
    /// A2 trace retention, as pure decisions over a file listing. Archive-first: step 1 gzips every
    /// part of every past session; step 2, only if the folder is still over the cap, deletes whole
    /// past sessions, oldest first, always keeping the newest <see cref="MinSessions"/>.
    /// </summary>
    /// <remarks>
    /// "Past" means a stamp that sorts before the current session's. The current session - and
    /// anything stamped later, which could only be another running instance or a clock that went
    /// backwards - is never compressed and never deleted. Its size still counts toward the cap,
    /// because it is on the disk. <see cref="TraceRetentionRunner"/> does the IO.
    /// </remarks>
    public sealed class TraceRetentionPolicy
    {
        /// <summary>Bytes in one configured megabyte.</summary>
        public const long BytesPerMB = 1024L * 1024L;

        /// <summary>Creates a policy.</summary>
        /// <param name="maxTotalBytes">Folder cap in bytes; 0 or less turns step 2 off.</param>
        /// <param name="minSessions">Newest past sessions step 2 never deletes; negative means 0.</param>
        /// <param name="compressClosedParts">Whether step 1 runs.</param>
        public TraceRetentionPolicy(long maxTotalBytes, int minSessions, bool compressClosedParts)
        {
            MaxTotalBytes = Math.Max(0L, maxTotalBytes);
            MinSessions = Math.Max(0, minSessions);
            CompressClosedParts = compressClosedParts;
        }

        /// <summary>Folder cap in bytes; 0 = no cap.</summary>
        public long MaxTotalBytes { get; }

        /// <summary>Newest past sessions that are never deleted.</summary>
        public int MinSessions { get; }

        /// <summary>Whether past parts are archived.</summary>
        public bool CompressClosedParts { get; }

        /// <summary>The policy the <c>[Trace]</c> config asks for.</summary>
        /// <param name="config">The bound trace configuration.</param>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        public static TraceRetentionPolicy FromConfig(TraceConfig config)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            return new TraceRetentionPolicy(
                Math.Max(0, config.RetainMaxTotalMB) * BytesPerMB,
                config.RetainMinSessions,
                config.CompressClosedParts);
        }

        /// <summary>Groups the recognised files by session stamp, oldest session first, parts in order.</summary>
        /// <param name="files">A folder listing; unrecognised names are ignored.</param>
        /// <exception cref="ArgumentNullException"><paramref name="files"/> is null.</exception>
        public static IReadOnlyList<TraceSession> Sessions(IEnumerable<TraceFile> files)
        {
            if (files is null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            return Parse(files)
                .GroupBy(p => p.Name.Stamp, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new TraceSession(
                    g.Key,
                    g.OrderBy(p => p.Name.Part).ThenBy(p => p.Name.Kind).Select(p => p.File).ToList()))
                .ToList();
        }

        /// <summary>Step 1: every plain part of every past session, plus interrupted archives to clear.</summary>
        /// <param name="files">A folder listing.</param>
        /// <param name="currentStamp">The live session's stamp.</param>
        /// <exception cref="ArgumentNullException"><paramref name="files"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="currentStamp"/> is not a valid stamp.</exception>
        public TraceCompressionPlan PlanCompression(IEnumerable<TraceFile> files, string currentStamp)
        {
            RequireStamp(currentStamp);
            if (files is null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            var past = Parse(files).Where(p => IsPast(p.Name.Stamp, currentStamp)).ToList();
            var plainNames = new HashSet<string>(
                past.Where(p => p.Name.Kind == TraceFileKind.Jsonl).Select(p => p.File.Name),
                StringComparer.Ordinal);

            // A temp is stale only while its original is still there. If the original is gone the
            // temp may be the last copy of that part, and archive-first means it is left alone.
            var staleTemps = past
                .Where(p => p.Name.Kind == TraceFileKind.GzipTemp && plainNames.Contains(p.Name.JsonlName))
                .OrderBy(p => p.Name.Stamp, StringComparer.Ordinal)
                .ThenBy(p => p.Name.Part)
                .Select(p => p.File)
                .ToList();

            var toCompress = !CompressClosedParts
                ? new List<TraceFile>()
                : past
                    .Where(p => p.Name.Kind == TraceFileKind.Jsonl)
                    .OrderBy(p => p.Name.Stamp, StringComparer.Ordinal)
                    .ThenBy(p => p.Name.Part)
                    .Select(p => p.File)
                    .ToList();

            return new TraceCompressionPlan(toCompress, staleTemps);
        }

        /// <summary>Step 2: whole past sessions to delete, oldest first, until the folder fits the cap.</summary>
        /// <param name="files">A folder listing, taken after step 1.</param>
        /// <param name="currentStamp">The live session's stamp.</param>
        /// <exception cref="ArgumentNullException"><paramref name="files"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="currentStamp"/> is not a valid stamp.</exception>
        public TraceDeletionPlan PlanDeletion(IEnumerable<TraceFile> files, string currentStamp)
        {
            RequireStamp(currentStamp);
            var sessions = Sessions(files);
            var total = sessions.Sum(s => s.Bytes);

            var past = sessions.Where(s => IsPast(s.Stamp, currentStamp)).ToList();
            var protectedCount = Math.Min(MinSessions, past.Count);

            if (MaxTotalBytes <= 0)
            {
                return new TraceDeletionPlan(new List<TraceSession>(), total, total, 0L, protectedCount);
            }

            // Oldest first; the newest `protectedCount` past sessions are never candidates.
            var toDelete = new List<TraceSession>();
            var remaining = total;
            for (var i = 0; i < past.Count - protectedCount && remaining > MaxTotalBytes; i++)
            {
                toDelete.Add(past[i]);
                remaining -= past[i].Bytes;
            }

            return new TraceDeletionPlan(toDelete, total, remaining, MaxTotalBytes, protectedCount);
        }

        private static bool IsPast(string stamp, string currentStamp) =>
            string.CompareOrdinal(stamp, currentStamp) < 0;

        private static void RequireStamp(string currentStamp)
        {
            if (!TraceFileName.IsStamp(currentStamp))
            {
                throw new ArgumentException(
                    "the current session stamp must be " + TraceFileName.StampFormat + ", got '" + currentStamp + "'",
                    nameof(currentStamp));
            }
        }

        private static IEnumerable<(TraceFile File, TraceFileName Name)> Parse(IEnumerable<TraceFile> files)
        {
            foreach (var file in files)
            {
                if (file != null && TraceFileName.TryParse(file.Name, out var name) && name != null)
                {
                    yield return (file, name);
                }
            }
        }
    }
}
