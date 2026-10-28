using BepInEx;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Loaders;
using CwyLs.ModLoader.Smoke;
using UnityEngine;

namespace CwyLs.ModLoader
{
    /// <summary>
    /// Entry point of the Mod Loader core: identity, logging, the directory layout, the pack
    /// scan, and the one smoke runner every plugin's checks are reported through.
    /// </summary>
    /// <remarks>
    /// This is the assembly's only <see cref="BepInPlugin"/>. The loader checks used to arrive on a
    /// second plugin of their own (P1-2 owned <c>Loaders/</c> and could not edit this file while
    /// other tickets were in it); P1-8 folded them back in, so there is one GUID, one Awake, and
    /// one place the <c>Smoke.*</c> settings are bound.
    /// </remarks>
    [BepInPlugin(CwyLsModLoader.PluginGuid, CwyLsModLoader.PluginName, CwyLsModLoader.PluginVersion)]
    public class ModLoaderPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            CwyLsModLoader.UseLog(Logger);

            // RP-2: full game only. On the demo nothing below runs: no patches, no packs, no mods.
            if (DemoLock.Engage())
            {
                enabled = false;
                return;
            }

            // F3: the sink and the mirror come FIRST, before the identity banner and before the
            // pack scan. They used to be installed five lines in, so cwy-console.log started at
            // "console with timestamps -> ..." with no banner and no PACKS: line, and the viewer's
            // session header was unreadable.
            string? consolePath = null;
            try
            {
                consolePath = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "cwy-console.log");
                var listener = new Logging.CwyConsoleListener(consolePath);
                BepInEx.Logging.Logger.Listeners.Add(listener);
                _rolledConsole = listener.RolledTo;
            }
            catch (System.Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"could not start the timestamped console file: {ex.Message}");
            }

            Logging.ConsoleMirror.Install();

            ConsoleEvents.Setup(
                "loader",
                $"{CwyLsModLoader.PluginName} v{CwyLsModLoader.PluginVersion}",
                ("unity", Application.unityVersion),
                ("game", Application.version),
                ("console", consolePath),
                ("rolled", _rolledConsole));

            ModLoaderPaths.EnsureCreated();
            CwyLsModLoader.Log.LogDebug(
                $"packs: {ModLoaderPaths.PacksRoot}; trace: {ModLoaderPaths.TraceRoot}; data: {ModLoaderPaths.DataRoot}");

            // C1-FRONTDOOR: the one main-thread dispatcher and its hidden host, before anything can
            // post to it. Awake runs on the main thread, which is what Arm captures.
            Threading.MainThread.Arm(Config);

            // W0-CONTRACTS: Core condition/action registry failures go to the BepInEx log.
            Mods.CoreRegistrySinks.Install();

            // E-ACT-CORE: action runner on ActionServices.Shared, console 'act', [ActionFamily] discovery on the first frame (before mods load).
            Actions.ActionsFeature.Initialize(Config);

            // P1-1. Runs here, before any game content exists, so every later ticket can assume
            // PackManager.Loaded is final: a pack added while the game runs needs a restart.
            Packs.PackManager.DiscoverAndLoad(Config);

            // C1-FRONTDOOR: code mods (packs with an entryDll) load on the first frame, after every
            // plugin's Awake has registered its mod-helper facets.
            Mods.ModLoader.Initialize(Config);

            LoaderSmokeChecks.RegisterAll();

            Console.ConsoleFeature.Initialize(Config);

            // The single smoke runner for the process, and the smoke.run hotkey with it. Called
            // here and nowhere else in this assembly: every other CWY plugin hard-depends on this
            // one, so this call is always first and the Smoke.* settings plus [Keys] smoke.run.*
            // land in this plugin's .cfg.
            if (SmokeHost.EnsureRunner(Config) == null)
            {
                CwyLsModLoader.Log.LogError("smoke runner unavailable; no report will be written");
                return;
            }

            ConsoleEvents.Setup(
                "smoke",
                $"{SmokeRegistry.Count} check(s) registered",
                ("report", SmokeHost.ReportPath));
        }

        /// <summary>Where the previous session's console file went, for the startup line.</summary>
        private string? _rolledConsole;
    }
}
