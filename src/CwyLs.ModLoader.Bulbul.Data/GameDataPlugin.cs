using System;
using BepInEx;
using BepInEx.Configuration;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Bulbul.Events;
using HarmonyLib;
using UnityEngine;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// Entry point of the Game Data Bus. Binds the config, applies the static
    /// Harmony patches, arms the hidden runner and registers the smoke checks - and then gets out
    /// of the way: nothing about the bus depends on this component still being alive.
    /// </summary>
    /// <remarks>
    /// In this game the BepInEx plugin component receives <c>OnDestroy</c> roughly 0.2 s after
    /// startup (observed in an early trace run), so anything with a lifecycle lives on the host
    /// object created by <see cref="DataHost.EnsureRunner"/> and the patches are static. This class
    /// deliberately has no <c>OnDestroy</c>: the bus is only wound down by
    /// <see cref="Application.quitting"/>.
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CwyLsModLoader.PluginGuid)]
    [BepInDependency(BulbulPlugin.PluginGuid)]
    public class GameDataPlugin : BaseUnityPlugin
    {
        /// <summary>BepInEx GUID of this plugin.</summary>
        public const string PluginGuid = "cwyls.modloader.bulbul.data";

        /// <summary>Display name of this plugin.</summary>
        public const string PluginName = "CWYLS Game Data";

        /// <summary>Version of this plugin; also stamped into every snapshot.</summary>
        public const string PluginVersion = "0.1.0";

        private static Harmony? _harmony;

        private void Awake()
        {
            // Demo lock: full game only (ModLoaderPlugin has already logged why).
            if (DemoLock.IsLocked)
            {
                enabled = false;
                return;
            }

            ConsoleEvents.Setup(
                "data",
                $"{PluginName} v{PluginVersion}",
                ("game", Application.version));

            GameData.UseOptions(BindOptions(Config));

            // The action gate reads the one [Data] AllowWrites (this plugin's cfg).
            global::CwyLs.ModLoader.Core.Actions.ActionServices.Shared.UseWritesSwitch(() => GameData.Options.AllowWrites);

            // The load seams, the write API's game methods and the observables this bus
            // subscribes to, checked at startup like every other seam.
            PatchTargetRegistry.Register(PluginName, DataPatchTargets.All);
            PatchTargetVerifier.VerifyAll(DataPatchTargets.All, new ReflectionMemberResolver());

            try
            {
                _harmony = new Harmony(PluginGuid);
                SaveDataPatches.Apply(_harmony);
                CwyLsModLoader.Log.LogDebug(
                    $"DATA listening to {SaveDataPatches.PatchedCount} SaveDataManager save/delete method(s) " +
                    $"through the shared save hook (installed by {SaveDataHook.InstalledBy})" +
                    (SaveDataPatches.Failures.Count == 0
                        ? string.Empty
                        : "; failed: " + string.Join(", ", new System.Collections.Generic.List<string>(
                            SaveDataPatches.Failures).ToArray())));

                // Trace-file evidence that each save is observed once: one CWY postfix per method.
                SaveDataHook.TraceCensus("data");
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"DATA change detection unavailable: {ex.GetType().Name}: {ex.Message}. " +
                    "Reads and snapshots still work; they just will not be triggered by saves.");
            }

            if (DataHost.EnsureRunner() == null)
            {
                CwyLsModLoader.Log.LogError("DATA runner unavailable; no snapshot file will be written");
            }

            DataSmoke.Register();

            // Code mods read this bus through helper.GameData.
            Mods.GameDataModBackend.Register();

            ConsoleEvents.Setup(
                "data",
                "ready",
                ("writes", GameData.Options.AllowWrites ? "enabled" : "disabled"),
                ("noteText", GameData.Options.IncludeNoteText ? "included" : "withheld"),
                ("patched", SaveDataPatches.PatchedCount),
                ("snapshot", DataHost.SnapshotPath));
        }

        /// <summary>
        /// Binds the <c>Data</c> section of the plugin's .cfg. Kept separate from Awake so the
        /// descriptions - which are what a modder actually reads - stay in one place.
        /// </summary>
        private static DataOptions BindOptions(ConfigFile config)
        {
            var options = new DataOptions();

            options.WriteSnapshot = config.Bind(
                DataOptions.Section,
                "WriteSnapshot",
                true,
                "Write BepInEx/data/snapshot.json whenever the player's data changes, so tools outside " +
                "the game can read it. Turning this off leaves the in-process API fully working.").Value;

            options.DebounceMs = config.Bind(
                DataOptions.Section,
                "DebounceMs",
                500,
                "Milliseconds of quiet after the last change before the snapshot is written. The game " +
                "saves in bursts of about 27 calls, so a low value means a lot of file writes.").Value;

            options.IncludeNoteText = config.Bind(
                DataOptions.Section,
                "IncludeNoteText",
                false,
                "Include the text the player wrote - note page bodies and diary entries - in the API and " +
                "the snapshot file. Off by default. PRIVACY: the snapshot is a plain file any process can read, " +
                "and a mod may forward it to a remote service. Off publishes titles, ids and ordering only.").Value;

            options.AllowWrites = config.Bind(
                DataOptions.Section,
                "AllowWrites",
                false,
                "Allow mods to modify the player's save data through GameData.Write (add todo, complete " +
                "todo, add note page). Off by default: the player has no undo.").Value;

            options.RecentDays = config.Bind(
                DataOptions.Section,
                "RecentDays",
                14,
                "How many days of habit and calendar history to publish. The game stores one file per " +
                "month and loads them on demand, so this bounds how much it is asked to read.").Value;

            return options;
        }
    }
}
