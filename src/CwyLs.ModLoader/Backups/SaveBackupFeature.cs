using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Console;
using CwyLs.ModLoader.Core.Backups;
using CwyLs.ModLoader.Core.Logging;
using UnityEngine;

namespace CwyLs.ModLoader.Backups
{
    /// <summary>
    /// Launch save backups: copies the game's whole save folder (<c>Application.persistentDataPath/SaveData</c>)
    /// to <c>&lt;Application.persistentDataPath&gt;/CWYLS Backups/save-YYYYMMDD-HHMMSS/</c> (next to the save folder, outside
    /// the game folder) once per launch, before the game can
    /// write to it, and prunes old copies with the <c>[Backups] Keep*</c> rules. Adds the <c>saves</c> command.
    /// </summary>
    /// <remarks>
    /// Runs synchronously from <see cref="ModLoaderPlugin"/>'s Awake: the first plugin the chainloader starts,
    /// inside <c>UnityEngine.Application</c>'s static constructor, at frame 0 - long before the game's DI scope
    /// builds <c>SaveDataIO</c> and Steam is initialised, both of which every save write needs. The copy is about
    /// 200 KB. All file work is Unity-free and lives in <c>CwyLs.ModLoader.Core/Backups</c>; this class only reads the
    /// config and the two paths, and logs. A failure is one WARN and the game starts as usual. Reads no game member.
    /// See <c>README.md</c> next to this file.
    /// </remarks>
    internal static class SaveBackupFeature
    {
        /// <summary>Config section.</summary>
        internal const string ConfigSection = "Backups";

        /// <summary>The <c>src=</c> of the SETUP line and the bus event name.</summary>
        internal const string Feature = "backups";

        /// <summary>The console command.</summary>
        internal const string CommandName = "saves";

        /// <summary>Prefix of this feature's WARN lines.</summary>
        internal const string LogPrefix = "BACKUPS";

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<int>? _keepLast;
        private static ConfigEntry<int>? _keepDaily;
        private static ConfigEntry<int>? _keepWeekly;
        private static ConfigEntry<int>? _keepMonthly;
        private static bool _initialized;

        /// <summary>True while backups are on (<c>[Backups] Enabled</c>, default true).</summary>
        internal static bool Enabled => _enabled?.Value ?? true;

        /// <summary>The configured rules, clamped (KeepLast at least 1).</summary>
        internal static SaveBackupRules Rules => SaveBackupRules.Clamp(
            _keepLast?.Value ?? SaveBackupRules.DefaultKeepLast,
            _keepDaily?.Value ?? SaveBackupRules.DefaultKeepDaily,
            _keepWeekly?.Value ?? SaveBackupRules.DefaultKeepWeekly,
            _keepMonthly?.Value ?? SaveBackupRules.DefaultKeepMonthly);

        /// <summary>The game's live save folder: <c>Application.persistentDataPath/SaveData</c>.</summary>
        internal static string SaveFolder => Path.Combine(Application.persistentDataPath, SaveBackupNames.SaveFolderName);

        /// <summary>
        /// Binds <c>[Backups]</c>, registers <c>saves</c>, makes this launch's backup and prunes. Called once from
        /// <see cref="ModLoaderPlugin"/>'s Awake; a second call does nothing. Never throws.
        /// </summary>
        /// <param name="config">The loader's config file.</param>
        internal static void Initialize(ConfigFile config)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            try
            {
                BindConfig(config);
                ConsoleFeature.Register(CommandName, SaveBackupText.Description, SaveBackupText.Usage, ConsoleFeature.FrameworkGroup, Run);

                if (!Enabled)
                {
                    ConsoleEvents.Setup(Feature, "disabled", ("enabled", false));
                    return;
                }

                BackUpNow();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{LogPrefix} save backup failed: {ex.GetType().Name}: {ex.Message}; the game starts as usual");
            }
        }

        private static void BackUpNow()
        {
            var persistent = Application.persistentDataPath;
            if (string.IsNullOrEmpty(persistent))
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{LogPrefix} save backup failed: the game's save location (Application.persistentDataPath) is not known yet; the game starts as usual");
                return;
            }

            var request = new SaveBackupRequest(
                Path.Combine(persistent, SaveBackupNames.SaveFolderName),
                ModLoaderPaths.BackupsRoot,
                DateTimeOffset.Now,
                CwyLsModLoader.PluginVersion,
                Application.version,
                Rules);
            var result = SaveBackupRunner.Run(request);
            Report(request, result);
        }

        private static void Report(SaveBackupRequest request, SaveBackupResult result)
        {
            switch (result.Outcome)
            {
                case SaveBackupOutcome.BackedUp:
                    Setup("save backed up", result, result.Folder);
                    break;
                case SaveBackupOutcome.Unchanged:
                    Setup("save unchanged since " + result.UnchangedSince + "; no new backup", result, request.BackupsRoot);
                    break;
                case SaveBackupOutcome.NoSaveFolder:
                    ConsoleEvents.Setup(Feature, "no save folder yet; nothing to back up", ("saves", request.SaveFolder));
                    break;
                case SaveBackupOutcome.EmptySaveFolder:
                    ConsoleEvents.Setup(Feature, "the save folder is empty; nothing to back up", ("saves", request.SaveFolder));
                    break;
                default:
                    CwyLsModLoader.Log.LogWarning(
                        $"{LogPrefix} save backup failed: {result.Failure}; the game starts as usual (old backups untouched)");
                    return;
            }

            if (result.PruneProblems.Count > 0)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{LogPrefix} {result.PruneProblems.Count} old backup(s) could not be removed: "
                    + string.Join("; ", ToArray(result.PruneProblems)));
            }

            if (result.SkippedLinks.Count > 0)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{LogPrefix} {result.SkippedLinks.Count} link(s) in the save folder were not followed or copied: "
                    + string.Join(", ", ToArray(result.SkippedLinks)));
            }
        }

        /// <summary>The SETUP line: <c>SETUP  save backed up  src=backups files=46 size="184 KB" kept=8 removed=1 ms=14 folder="…"</c>.</summary>
        private static void Setup(string summary, SaveBackupResult result, string? folder)
        {
            ConsoleEvents.Emit(
                ConsoleEvents.SetupSystem,
                Feature,
                ConsoleTags.Setup,
                summary,
                "src,files,size,kept,removed,ms,folder",
                ("src", Feature),
                ("files", result.Files),
                ("size", SaveBackupNames.Size(result.Bytes)),
                ("kept", result.Kept),
                ("removed", result.Removed),
                ("ms", result.ElapsedMs),
                ("folder", folder),
                ("bytes", result.Bytes),
                ("sha256", result.ContentSha256),
                ("leftAlone", result.LeftAlone),
                ("rules", Rules.ToString()));
        }

        /// <summary>The <c>saves</c> command.</summary>
        private static string? Run(IReadOnlyList<string> args)
        {
            var save = SafeSaveFolder();
            var root = ModLoaderPaths.BackupsRoot;
            var what = args.Count > 0 ? args[0].ToLowerInvariant() : string.Empty;
            switch (what)
            {
                case "list":
                    return SaveBackupText.List(SaveBackupInventory.Scan(root), Rules, Enabled, save);
                case "folder":
                case "folders":
                    return SaveBackupText.Folders(save, root);
                case "":
                case "help":
                    return SaveBackupText.Help(save, root, Enabled);
                default:
                    return "usage: " + SaveBackupText.Usage + "  ('saves' alone shows how to restore by hand)";
            }
        }

        private static string SafeSaveFolder()
        {
            try
            {
                return SaveFolder;
            }
            catch (Exception ex)
            {
                return "(unknown: " + ex.GetType().Name + ")";
            }
        }

        private static void BindConfig(ConfigFile file)
        {
            _enabled = file.Bind(
                ConfigSection,
                "Enabled",
                true,
                "Copy the game's save folder into the \"CWYLS Backups\" folder (next to the save folder, outside the game folder) "
                + "each time the game starts with the mod loader, before the game can change it. No copy is made when "
                + "nothing changed since the newest one. Off: no copies are made and no old ones are removed.");

            _keepLast = file.Bind(
                ConfigSection,
                "KeepLast",
                SaveBackupRules.DefaultKeepLast,
                new ConfigDescription(
                    "Keep this many of the newest backups. At least 1. A backup that no Keep setting keeps is deleted "
                    + "at the next game start; folders the mod loader did not make are never touched.",
                    new AcceptableValueRange<int>(1, SaveBackupRules.MaxKeep)));

            _keepDaily = file.Bind(
                ConfigSection,
                "KeepDaily",
                SaveBackupRules.DefaultKeepDaily,
                new ConfigDescription(
                    "Also keep the newest backup of each day, for this many of the most recent days that have a "
                    + "backup. 0 turns this off.",
                    new AcceptableValueRange<int>(0, SaveBackupRules.MaxKeep)));

            _keepWeekly = file.Bind(
                ConfigSection,
                "KeepWeekly",
                SaveBackupRules.DefaultKeepWeekly,
                new ConfigDescription(
                    "Also keep the newest backup of each week (Monday to Sunday), for this many of the most recent "
                    + "weeks that have a backup. 0 turns this off.",
                    new AcceptableValueRange<int>(0, SaveBackupRules.MaxKeep)));

            _keepMonthly = file.Bind(
                ConfigSection,
                "KeepMonthly",
                SaveBackupRules.DefaultKeepMonthly,
                new ConfigDescription(
                    "Also keep the newest backup of each month, for this many of the most recent months that have a "
                    + "backup. 0 turns this off.",
                    new AcceptableValueRange<int>(0, SaveBackupRules.MaxKeep)));
        }

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
