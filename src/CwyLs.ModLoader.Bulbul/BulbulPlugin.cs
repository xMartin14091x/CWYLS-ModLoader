using BepInEx;
using UnityEngine;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// Game adapter entry point: loads after the core, proves at runtime that the game members
    /// the adapter is built on are still present in this build of the game, and starts the features.
    /// Note the global:: qualifiers - our namespace ends in "Bulbul" and the game's is "Bulbul",
    /// so unqualified references would bind to this assembly instead.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CwyLsModLoader.PluginGuid)]
    public class BulbulPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "cwyls.modloader.bulbul";
        public const string PluginName = "CWYLS Game Adapter";
        public const string PluginVersion = "0.1.0";

        private void Awake()
        {
            // Demo lock: full game only (ModLoaderPlugin has already logged why).
            if (DemoLock.IsLocked)
            {
                enabled = false;
                return;
            }

            // Signature check: if a game update renames or moves either of these, the log says so
            // at startup instead of a Harmony patch failing silently later.
            var heroine = typeof(global::Bulbul.HeroineService);
            var load = typeof(global::Bulbul.MasterDataLoader).GetMethod("Load");

            ConsoleEvents.Setup(
                "bulbul",
                $"{PluginName} v{PluginVersion}",
                ("game", Application.version),
                ("heroine", heroine.Assembly.GetName().Name),
                ("masterDataLoad", load?.Name ?? "MISSING"));

            // The full seam table, verified once at startup. The version gate only explains
            // a mismatch - it never aborts, because the target report is the real evidence.
            PatchTargetRegistry.Register(PluginName, BulbulPatchTargets.All);
            PatchTargetVerifier.CheckGameVersion(Application.version);
            PatchTargetVerifier.VerifyAll();

            // The typed game event bus. Verifies its own seams, applies its static patches
            // and arms its DontDestroyOnLoad host - nothing about it depends on this component
            // still being alive (the game destroys plugin components shortly after boot).
            Events.EventBus.Initialize(Config);
            global::CwyLs.ModLoader.Bulbul.Textures.TextureFeature.Initialize(Config);

            // Lightroom-style grading volume over the room camera (scenery starts inside TextureFeature).
            global::CwyLs.ModLoader.Bulbul.Grading.GradingFeature.Initialize(Config);
            global::CwyLs.ModLoader.Bulbul.VoiceFeature.Initialize(Config);
            global::CwyLs.ModLoader.Bulbul.Satone.Motion.SatoneMotionFeature.Initialize(Config);
            global::CwyLs.ModLoader.Bulbul.MasterData.MasterDataFeature.Initialize(Config);

            // Audio: pack clips for ambient/SE/timer/story-music rows, pack music, A/B switch (after MasterData).
            global::CwyLs.ModLoader.Bulbul.Audio.AudioFeature.Initialize(Config);

            // Pack languages before subtitles: the text seam and the reading-time hold read the
            // language registry, and the save guard / LoadSetting re-apply must be patched before
            // EntryBehavior loads the settings. Idempotent.
            global::CwyLs.ModLoader.Bulbul.Language.LanguageFeature.Initialize(Config);
            global::CwyLs.ModLoader.Bulbul.Subtitles.SubtitleFeature.Initialize(Config);

            // Pack lines offered only in their language (needs MasterData + Language above).
            global::CwyLs.ModLoader.Bulbul.PackLines.PackLinesFeature.Initialize(Config);

            // Script commands 5000-5999 (cwy.do/defer/wait/flag), save-log guard, pack-scene lint, `scene` command (after MasterData).
            global::CwyLs.ModLoader.Bulbul.Scenario.ScenarioFeature.Initialize(Config);
            global::CwyLs.ModLoader.Bulbul.Satone.Motion.SatoneSayFeature.Initialize(Config);
            global::CwyLs.ModLoader.Bulbul.Console.BulbulConsoleCommands.Register();

            // Game services + UI-click deny list for action families (seam check, smoke e.actions.services/uiclick).
            global::CwyLs.ModLoader.Bulbul.Actions.BulbulActionsCore.Initialize();

            // The game-specific facets of the code-mod helper (events, Satone, language, windows).
            // Mods load on the first frame, after this Awake.
            global::CwyLs.ModLoader.Bulbul.Mods.BulbulModBackends.Register();

            // Mod windows (helper.Ui.CreateWindow(spec)): backend, Escape routing, Hide UI, remembered places, 'windows' command.
            global::CwyLs.ModLoader.Bulbul.UI.ModWindows.Initialize(Config);
        }
    }
}
