using System;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// File-name rules that keep a name meaning the same file on macOS and Windows.
    /// Pure, so they are unit-tested; used by <see cref="PackContentIndex.Normalise"/> for pack paths
    /// and by the mod Data facet for data keys.
    /// </summary>
    /// <remarks>
    /// Windows opens a device, not a file, for <c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>,
    /// <c>COM1</c>-<c>COM9</c> and <c>LPT1</c>-<c>LPT9</c>, with or without an extension and in any
    /// case, and it silently drops a trailing dot or space (so <c>a.txt.</c> is <c>a.txt</c>). A
    /// colon makes a path drive-relative (<c>C:x</c>) or names an alternate data stream.
    /// </remarks>
    public static class SafeFileNames
    {
        private static readonly string[] Devices = { "CON", "PRN", "AUX", "NUL" };

        /// <summary>
        /// True when <paramref name="segment"/> (one path segment, no slashes) is a Windows device
        /// name: <c>con</c>, <c>nul.txt</c>, <c>COM1.log</c>, <c>lpt9</c>. The part before the first
        /// dot decides, case-insensitively.
        /// </summary>
        /// <param name="segment">One file or folder name.</param>
        public static bool IsReservedDeviceName(string? segment)
        {
            if (string.IsNullOrEmpty(segment))
            {
                return false;
            }

            var dot = segment!.IndexOf('.');
            var stem = (dot < 0 ? segment : segment.Substring(0, dot)).TrimEnd(' ');
            foreach (var device in Devices)
            {
                if (string.Equals(stem, device, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return stem.Length == 4
                   && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                   && stem[3] >= '1' && stem[3] <= '9';
        }

        /// <summary>True when the name ends in a dot or a space (Windows drops them).</summary>
        /// <param name="segment">One file or folder name.</param>
        public static bool HasTrailingDotOrSpace(string? segment) =>
            !string.IsNullOrEmpty(segment) && (segment![segment.Length - 1] == '.' || segment[segment.Length - 1] == ' ');

        /// <summary>
        /// Why one path segment is unsafe, or null when it is fine: a colon, a Windows device name,
        /// or a trailing dot or space. The special segments <c>.</c> and <c>..</c> are the caller's
        /// business.
        /// </summary>
        /// <param name="segment">One file or folder name.</param>
        public static string? ProblemWith(string? segment)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..")
            {
                return null;
            }

            if (segment!.IndexOf(':') >= 0)
            {
                return "\"" + segment + "\" contains ':' (a drive or stream name on Windows)";
            }

            if (IsReservedDeviceName(segment))
            {
                return "\"" + segment + "\" is a Windows device name";
            }

            if (HasTrailingDotOrSpace(segment))
            {
                return "\"" + segment + "\" ends in a dot or a space, which Windows drops";
            }

            return null;
        }
    }
}
