using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>
    /// Names and retention for the save backups <c>scene play</c> makes before it previews a game story
    /// the player has not seen.
    /// Pure: the copying is the game adapter's.
    /// </summary>
    public static class ScenePreviewBackups
    {
        /// <summary>Every backup folder this feature makes starts with this.</summary>
        public const string Prefix = "save-scene-";

        /// <summary>How many backups are kept; older ones are removed after a new one is made.</summary>
        public const int Keep = 10;

        /// <summary>The folder name for a backup made now before previewing a group, e.g. <c>save-scene-20261003-143512-main_05</c>.</summary>
        /// <param name="now">The local time.</param>
        /// <param name="groupId">The group about to play; anything but letters, digits, <c>_</c> and <c>-</c> becomes <c>_</c>.</param>
        public static string FolderName(DateTime now, string? groupId)
        {
            var safe = new StringBuilder();
            foreach (var c in groupId ?? string.Empty)
            {
                safe.Append(char.IsLetterOrDigit(c) && c < 128 || c == '_' || c == '-' ? c : '_');
            }

            return Prefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (safe.Length > 0 ? "-" + safe : string.Empty);
        }

        /// <summary>The backup folders to remove so only the newest <see cref="Keep"/> stay. Other folders are never named.</summary>
        /// <param name="folderNames">The names in the backups folder.</param>
        public static IReadOnlyList<string> ToPrune(IEnumerable<string>? folderNames)
        {
            var ours = new List<string>();
            foreach (var name in folderNames ?? Array.Empty<string>())
            {
                if (name != null && name.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    ours.Add(name);
                }
            }

            ours.Sort(StringComparer.Ordinal); // the timestamp sorts oldest first
            return ours.Count <= Keep ? Array.Empty<string>() : ours.GetRange(0, ours.Count - Keep);
        }
    }
}
