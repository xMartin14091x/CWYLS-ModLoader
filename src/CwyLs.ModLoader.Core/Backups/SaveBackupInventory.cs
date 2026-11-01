using System;
using System.Collections.Generic;
using System.IO;

namespace CwyLs.ModLoader.Core.Backups
{
    /// <summary>One launch save backup found in the backups folder.</summary>
    /// <param name="Name">The folder name, e.g. <c>save-20261101-140312</c>.</param>
    /// <param name="Time">The local time in the name.</param>
    /// <param name="FullPath">The folder's full path.</param>
    /// <param name="Manifest">Its <c>backup.json</c>.</param>
    public sealed record SaveBackupInfo(string Name, DateTime Time, string FullPath, SaveBackupManifest Manifest);

    /// <summary>A folder in the backups folder that is not one of ours, and why. Never touched.</summary>
    /// <param name="Name">The folder name.</param>
    /// <param name="Reason">Why it is not a backup, e.g. <c>no backup.json</c>.</param>
    public sealed record SaveBackupStray(string Name, string Reason);

    /// <summary>What is in the backups folder: our backups (newest first) and everything we leave alone.</summary>
    public sealed class SaveBackupInventory
    {
        private SaveBackupInventory(string root, bool exists, IReadOnlyList<SaveBackupInfo> backups, IReadOnlyList<SaveBackupStray> leftAlone)
        {
            Root = root;
            Exists = exists;
            Backups = backups;
            LeftAlone = leftAlone;
        }

        /// <summary>The backups folder.</summary>
        public string Root { get; }

        /// <summary>False when the backups folder does not exist (yet).</summary>
        public bool Exists { get; }

        /// <summary>Our backups, newest first: name matches <see cref="SaveBackupNames.TryParse"/> and the marker parses.</summary>
        public IReadOnlyList<SaveBackupInfo> Backups { get; }

        /// <summary>Every other folder, sorted by name. Files in the backups folder are ignored.</summary>
        public IReadOnlyList<SaveBackupStray> LeftAlone { get; }

        /// <summary>Total bytes of our backups, as their markers record it.</summary>
        public long TotalBytes
        {
            get
            {
                long total = 0;
                foreach (var backup in Backups)
                {
                    total += backup.Manifest.Bytes;
                }

                return total;
            }
        }

        /// <summary>The policy's input for these backups.</summary>
        public IReadOnlyList<SaveBackupStamp> Stamps
        {
            get
            {
                var stamps = new List<SaveBackupStamp>(Backups.Count);
                foreach (var backup in Backups)
                {
                    stamps.Add(new SaveBackupStamp(backup.Name, backup.Time));
                }

                return stamps;
            }
        }

        /// <summary>Lists the backups folder. Reads only the markers. A missing folder is an empty inventory.</summary>
        /// <param name="root">The backups folder.</param>
        public static SaveBackupInventory Scan(string root)
        {
            var backups = new List<SaveBackupInfo>();
            var leftAlone = new List<SaveBackupStray>();
            if (!Directory.Exists(root))
            {
                return new SaveBackupInventory(root, false, backups, leftAlone);
            }

            foreach (var folder in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(folder);
                if (TryRead(folder, out var time, out var manifest, out var reason))
                {
                    backups.Add(new SaveBackupInfo(name, time, folder, manifest!));
                }
                else
                {
                    leftAlone.Add(new SaveBackupStray(name, reason!));
                }
            }

            backups.Sort((a, b) =>
            {
                var byTime = b.Time.CompareTo(a.Time);
                return byTime != 0 ? byTime : string.CompareOrdinal(b.Name, a.Name);
            });
            leftAlone.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new SaveBackupInventory(root, true, backups, leftAlone);
        }

        /// <summary>
        /// True only for one of our backups: the folder name matches <see cref="SaveBackupNames.TryParse"/>, the folder
        /// is not a link, and it holds a <c>backup.json</c> that <see cref="SaveBackupManifest.TryParse"/> accepts.
        /// Checked again right before any delete.
        /// </summary>
        /// <param name="folder">The folder's full path.</param>
        /// <param name="time">The local time in its name.</param>
        /// <param name="manifest">Its marker.</param>
        /// <param name="reason">Why it is not one of ours.</param>
        public static bool TryRead(string folder, out DateTime time, out SaveBackupManifest? manifest, out string? reason)
        {
            manifest = null;
            var name = Path.GetFileName(folder);
            if (!SaveBackupNames.TryParse(name, out time))
            {
                reason = name.IndexOf(SaveBackupNames.PartialInfix, StringComparison.Ordinal) >= 0
                    ? "an unfinished backup (the game closed while it was written)"
                    : "not named save-YYYYMMDD-HHMMSS";
                return false;
            }

            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "a link, not a folder";
                    return false;
                }

                var marker = Path.Combine(folder, SaveBackupNames.MarkerFileName);
                if (!File.Exists(marker))
                {
                    reason = "no " + SaveBackupNames.MarkerFileName;
                    return false;
                }

                if (!SaveBackupManifest.TryParse(File.ReadAllText(marker), out manifest, out var problem))
                {
                    reason = problem;
                    return false;
                }

                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                reason = SaveBackupNames.MarkerFileName + " could not be read (" + ex.GetType().Name + ": " + ex.Message + ")";
                return false;
            }
        }
    }
}
