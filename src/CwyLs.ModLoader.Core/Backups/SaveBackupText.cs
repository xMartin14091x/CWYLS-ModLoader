using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Backups
{
    /// <summary>What the <c>saves</c> console command prints. Pure: the adapter passes in the paths and the scan.</summary>
    public static class SaveBackupText
    {
        /// <summary>The command's usage line.</summary>
        public const string Usage = "saves list|folder";

        /// <summary>The one-line description <c>help</c> shows.</summary>
        public const string Description =
            "Save backups made at launch: list them or show the folders. No restore command: "
            + "restore by hand with the game closed ('saves' shows how).";

        /// <summary>
        /// <c>saves list</c>: every backup with its time, size and the rules that keep it, then the folders.
        /// </summary>
        /// <param name="inventory">The scan of the backups folder.</param>
        /// <param name="rules">The configured rules.</param>
        /// <param name="enabled">Whether backups are on (off: nothing is pruned, so a dropped one is not "removed at the next launch").</param>
        /// <param name="saveFolder">The live save folder.</param>
        public static string List(SaveBackupInventory inventory, SaveBackupRules rules, bool enabled, string saveFolder)
        {
            if (inventory is null)
            {
                throw new ArgumentNullException(nameof(inventory));
            }

            var sb = new StringBuilder();
            var count = inventory.Backups.Count;
            sb.Append(count.ToString(CultureInfo.InvariantCulture)).Append(" backup(s)");
            if (count > 0)
            {
                sb.Append(", ").Append(SaveBackupNames.Size(inventory.TotalBytes));
            }

            sb.Append("; keep ").Append(rules).Append(enabled ? string.Empty : " (backups are off: [Backups] Enabled = false)");

            var byName = new Dictionary<string, SaveBackupInfo>(StringComparer.Ordinal);
            foreach (var backup in inventory.Backups)
            {
                byName[backup.Name] = backup;
            }

            foreach (var decision in SaveBackupPolicy.Apply(inventory.Stamps, rules))
            {
                var backup = byName[decision.Name];
                sb.Append("\n  ").Append(decision.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append("  ").Append(decision.Name)
                    .Append("  ").Append(SaveBackupNames.Size(backup.Manifest.Bytes).PadLeft(7))
                    .Append("  ").Append(backup.Manifest.Files.ToString(CultureInfo.InvariantCulture)).Append(" files")
                    .Append("  ").Append(decision.Keep
                        ? "kept: " + string.Join(", ", ToArray(decision.Reasons))
                        : enabled
                            ? "no rule keeps it: removed at the next launch"
                            : "no rule keeps it (not removed while backups are off)");
            }

            if (inventory.LeftAlone.Count > 0)
            {
                sb.Append("\nleft alone (not backups made by the loader):");
                foreach (var stray in inventory.LeftAlone)
                {
                    sb.Append("\n  ").Append(stray.Name).Append(" - ").Append(stray.Reason);
                }
            }

            return sb.Append('\n').Append(Folders(saveFolder, inventory.Root)).ToString();
        }

        /// <summary><c>saves folder</c>: the live save folder and the backups folder.</summary>
        public static string Folders(string saveFolder, string backupsRoot) =>
            "live save: " + saveFolder + "\nbackups:   " + backupsRoot;

        /// <summary><c>saves</c> on its own: what the feature does and how to restore by hand.</summary>
        /// <param name="saveFolder">The live save folder.</param>
        /// <param name="backupsRoot">The backups folder.</param>
        /// <param name="enabled">Whether backups are on.</param>
        public static string Help(string saveFolder, string backupsRoot, bool enabled) =>
            (enabled
                ? "Each time the game starts with the loader, the save folder is copied to the backups folder "
                  + "(no copy when nothing changed since the newest one)."
                : "Save backups are off ([Backups] Enabled = false); existing ones are kept.")
            + "\n" + Folders(saveFolder, backupsRoot)
            + "\nTo restore a backup, by hand and with the game CLOSED (restoring while it runs is unsafe, so there is no command):"
            + "\n  1. Quit the game."
            + "\n  2. Rename the live save folder (SaveData) to SaveData-before-restore. Keep it until you are sure."
            + "\n  3. Copy the SaveData folder from inside the backup you want (save-YYYYMMDD-HHMMSS/SaveData) to where the live one was."
            + "\n  4. Start the game. If Steam reports a Cloud conflict, choose the files on this computer."
            + "\n" + Usage;

        private static string[] ToArray(IReadOnlyList<string> items)
        {
            var array = new string[items.Count];
            for (var i = 0; i < array.Length; i++)
            {
                array[i] = items[i];
            }

            return array;
        }
    }
}
