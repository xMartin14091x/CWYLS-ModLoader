using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Backups
{
    /// <summary>
    /// Names of the launch save backups: <c>CWYLS Backups/save-YYYYMMDD-HHMMSS/</c>, next to the game's save folder.
    /// Pure. A folder is only ever treated as one of ours when its name matches
    /// <see cref="TryParse"/> exactly <b>and</b> it holds a <see cref="MarkerFileName"/> that
    /// <see cref="SaveBackupManifest.TryParse"/> accepts; anything else in the folder is left alone.
    /// </summary>
    public static class SaveBackupNames
    {
        /// <summary>The backups folder's name, next to the game's <c>SaveData</c> folder (outside the game folder).</summary>
        public const string RootFolderName = "CWYLS Backups";

        /// <summary>The game's save folder name under <c>Application.persistentDataPath</c>; also its name inside a backup.</summary>
        public const string SaveFolderName = "SaveData";

        /// <summary>The marker every backup carries. A folder without it is never deleted.</summary>
        public const string MarkerFileName = "backup.json";

        /// <summary>Every backup folder name starts with this.</summary>
        public const string Prefix = "save-";

        /// <summary>
        /// A backup being written is named <c>&lt;final name&gt;.partial-&lt;token&gt;</c> and renamed to its final
        /// name only once it is complete. A leftover from a crash never matches <see cref="TryParse"/>.
        /// </summary>
        public const string PartialInfix = ".partial-";

        private const string StampFormat = "yyyyMMdd-HHmmss";

        /// <summary>The folder name for a backup made at <paramref name="localTime"/>, e.g. <c>save-20261101-140312</c>.</summary>
        /// <param name="localTime">The local clock time (its kind is ignored).</param>
        public static string FolderName(DateTime localTime) =>
            Prefix + localTime.ToString(StampFormat, CultureInfo.InvariantCulture);

        /// <summary>The temporary name a backup is written under before it is renamed to <paramref name="finalName"/>.</summary>
        /// <param name="finalName">The name it will get, from <see cref="FolderName"/>.</param>
        /// <param name="token">Something unique to this run, e.g. 8 hex digits of a new GUID.</param>
        public static string PartialName(string finalName, string token) => finalName + PartialInfix + token;

        /// <summary>
        /// True when <paramref name="name"/> is exactly <c>save-</c>, 8 ASCII digits, <c>-</c>, 6 ASCII digits,
        /// and those digits are a real date and time. <c>save-scene-…</c> (the scene-preview backups), a
        /// <c>.partial-</c> leftover, other casing, spaces or suffixes all fail.
        /// </summary>
        /// <param name="name">A folder name (no path).</param>
        /// <param name="localTime">The time in the name, <see cref="DateTimeKind.Unspecified"/>.</param>
        public static bool TryParse(string? name, out DateTime localTime)
        {
            localTime = default;
            if (name is null || name.Length != Prefix.Length + StampFormat.Length
                || !name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            for (var i = Prefix.Length; i < name.Length; i++)
            {
                var c = name[i];
                var dash = i == Prefix.Length + 8;
                if (dash ? c != '-' : c < '0' || c > '9')
                {
                    return false;
                }
            }

            return DateTime.TryParseExact(
                name.Substring(Prefix.Length),
                StampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out localTime);
        }

        /// <summary>A byte count the way the log and the console print it: <c>512 B</c>, <c>184 KB</c>, <c>3.4 MB</c> (1 KB = 1024 B).</summary>
        /// <param name="bytes">The size.</param>
        public static string Size(long bytes)
        {
            if (bytes < 0)
            {
                bytes = 0;
            }

            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            }

            if (bytes < 1024L * 1024)
            {
                return Math.Round(bytes / 1024d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " KB";
            }

            return (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
