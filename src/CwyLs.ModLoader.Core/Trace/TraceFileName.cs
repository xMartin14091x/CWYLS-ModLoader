using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>What a recognised trace file is.</summary>
    public enum TraceFileKind
    {
        /// <summary>A plain part as the writer produces it: <c>trace-&lt;stamp&gt;[-partN].jsonl</c>.</summary>
        Jsonl,

        /// <summary>A verified archive of a part: <c>….jsonl.gz</c>.</summary>
        Gzip,

        /// <summary>An archive still being written or verified: <c>….jsonl.gz.tmp</c>. Never the only copy.</summary>
        GzipTemp
    }

    /// <summary>
    /// The trace folder's naming scheme, parsed. <see cref="BufferedFileTraceWriter"/> names a
    /// session's first part <c>trace-yyyyMMdd-HHmmss.jsonl</c> and each rollover
    /// <c>trace-yyyyMMdd-HHmmss-partN.jsonl</c> (N from 2); retention adds <c>.gz</c> and, while it
    /// works, <c>.gz.tmp</c>. Anything else in the folder - <c>latest.txt</c>, a file the user put
    /// there - does not parse, and retention never counts or touches it.
    /// </summary>
    public sealed class TraceFileName
    {
        /// <summary>Every trace file name starts with this.</summary>
        public const string Prefix = "trace-";

        /// <summary>Extension of a plain part.</summary>
        public const string JsonlExtension = ".jsonl";

        /// <summary>Appended to a part's name to name its archive.</summary>
        public const string GzipExtension = ".gz";

        /// <summary>Appended to an archive's name while it is being written and verified.</summary>
        public const string TempExtension = ".tmp";

        /// <summary>Separates the session stamp from the part number of a rolled-over part.</summary>
        public const string PartMarker = "-part";

        /// <summary>The session stamp's format; ordinal order of stamps is chronological order.</summary>
        public const string StampFormat = "yyyyMMdd-HHmmss";

        private TraceFileName(string fileName, string stamp, int part, TraceFileKind kind)
        {
            FileName = fileName;
            Stamp = stamp;
            Part = part;
            Kind = kind;
        }

        /// <summary>The file name as found, e.g. <c>trace-20260924-122149-part2.jsonl.gz</c>.</summary>
        public string FileName { get; }

        /// <summary>The session stamp, e.g. <c>20260924-122149</c>.</summary>
        public string Stamp { get; }

        /// <summary>Part number: 1 for the unsuffixed first part, N for <c>-partN</c>.</summary>
        public int Part { get; }

        /// <summary>Plain, archive, or archive in progress.</summary>
        public TraceFileKind Kind { get; }

        /// <summary>Name of the plain part this file is or came from, e.g. <c>trace-20260924-122149-part2.jsonl</c>.</summary>
        public string JsonlName => Prefix + Stamp + (Part == 1 ? string.Empty : PartMarker + Part.ToString(CultureInfo.InvariantCulture)) + JsonlExtension;

        /// <summary>Name of this part's archive.</summary>
        public string ArchiveName => JsonlName + GzipExtension;

        /// <summary>Name of this part's archive while it is being written.</summary>
        public string TempName => ArchiveName + TempExtension;

        /// <summary>The first part's file name for a session, e.g. <c>trace-20260928-223500.jsonl</c>.</summary>
        /// <param name="stamp">A stamp in <see cref="StampFormat"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="stamp"/> is not a valid stamp.</exception>
        public static string SessionFileName(string stamp)
        {
            if (!IsStamp(stamp))
            {
                throw new ArgumentException("not a " + StampFormat + " stamp: '" + stamp + "'", nameof(stamp));
            }

            return Prefix + stamp + JsonlExtension;
        }

        /// <summary>The stamp for a session starting at <paramref name="time"/>.</summary>
        /// <param name="time">Local start time.</param>
        public static string StampFor(DateTime time) => time.ToString(StampFormat, CultureInfo.InvariantCulture);

        /// <summary>Whether <paramref name="stamp"/> is eight digits, a dash and six digits.</summary>
        /// <param name="stamp">The candidate stamp.</param>
        public static bool IsStamp(string? stamp)
        {
            if (stamp is null || stamp.Length != StampFormat.Length)
            {
                return false;
            }

            for (var i = 0; i < stamp.Length; i++)
            {
                var ok = i == 8 ? stamp[i] == '-' : stamp[i] >= '0' && stamp[i] <= '9';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Parses a bare file name (no directory). Returns false for anything that is not ours.</summary>
        /// <param name="fileName">The file name, e.g. <c>trace-20260924-122149-part3.jsonl</c>.</param>
        /// <param name="parsed">The parsed name, or null.</param>
        public static bool TryParse(string? fileName, out TraceFileName? parsed)
        {
            parsed = null;
            if (fileName is null || !fileName.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            TraceFileKind kind;
            string suffix;
            if (fileName.EndsWith(JsonlExtension + GzipExtension + TempExtension, StringComparison.Ordinal))
            {
                kind = TraceFileKind.GzipTemp;
                suffix = JsonlExtension + GzipExtension + TempExtension;
            }
            else if (fileName.EndsWith(JsonlExtension + GzipExtension, StringComparison.Ordinal))
            {
                kind = TraceFileKind.Gzip;
                suffix = JsonlExtension + GzipExtension;
            }
            else if (fileName.EndsWith(JsonlExtension, StringComparison.Ordinal))
            {
                kind = TraceFileKind.Jsonl;
                suffix = JsonlExtension;
            }
            else
            {
                return false;
            }

            var core = fileName.Substring(Prefix.Length, fileName.Length - Prefix.Length - suffix.Length);
            if (core.Length < StampFormat.Length)
            {
                return false;
            }

            var stamp = core.Substring(0, StampFormat.Length);
            if (!IsStamp(stamp))
            {
                return false;
            }

            var rest = core.Substring(StampFormat.Length);
            var part = 1;
            if (rest.Length > 0 && !TryParsePart(rest, out part))
            {
                return false;
            }

            parsed = new TraceFileName(fileName, stamp, part, kind);
            return true;
        }

        /// <summary>
        /// <c>-partN</c> with N a plain number from 2: the writer never names part 1 that way, so a
        /// <c>-part1</c> or <c>-part02</c> is somebody else's file and is left alone.
        /// </summary>
        private static bool TryParsePart(string rest, out int part)
        {
            part = 0;
            if (!rest.StartsWith(PartMarker, StringComparison.Ordinal))
            {
                return false;
            }

            var digits = rest.Substring(PartMarker.Length);
            if (digits.Length == 0 || digits.Length > 6 || digits[0] == '0')
            {
                return false;
            }

            foreach (var c in digits)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            part = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
            return part >= 2;
        }
    }
}
