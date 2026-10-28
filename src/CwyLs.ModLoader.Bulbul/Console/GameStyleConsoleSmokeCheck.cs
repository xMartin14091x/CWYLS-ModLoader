using System;
using CwyLs.ModLoader.Bulbul.UI;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Smoke;
using Framework = CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Bulbul.Console
{
    /// <summary>
    /// The game-style console's in-game evidence: the game's UI assets the game-style console is built from can be
    /// found in this build of the game. Quiet - it only looks the assets up, it builds nothing and
    /// shows nothing - so it runs on every smoke pass.
    /// </summary>
    internal static class GameStyleConsoleSmokeCheck
    {
        /// <summary>Phase label the check is reported under.</summary>
        internal const string Phase = "B";

        /// <summary>Check id.</summary>
        internal const string Id = "b.console.gamestyle";

        /// <summary>The scene the game's facility windows (and so their assets) live in.</summary>
        internal const string RoomScene = "RoomScene";

        /// <summary>Registers the check. Idempotent.</summary>
        internal static void Register() => SmokeRegistry.Register(new SmokeCheck(Id, Phase, Run));

        private static SmokeCheckResult Run()
        {
            if (Framework.ConsoleFeature.Style == ConsoleStyle.Imgui)
            {
                return SmokeCheckResult.Skipped(Id, "Console.Style=Imgui: the game-style console is switched off");
            }

            if (Framework.ConsoleFeature.SurfaceBrokenReason.Length > 0)
            {
                return SmokeCheckResult.Fail(Id, "off for this session: " + Framework.ConsoleFeature.SurfaceBrokenReason);
            }

            var assets = GameStyleAssets.TryResolve(out var problem);
            var active = Framework.ConsoleFeature.ActiveSurfaceName;
            var showing = active.Length == 0 ? "console closed" : "console open on " + active;

            if (assets != null)
            {
                return SmokeCheckResult.Pass(Id, assets.Report.Describe() + "; " + showing);
            }

            string scene;
            try
            {
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{Framework.ConsoleFeature.LogPrefix} smoke {Id} could not read the scene: {ex.GetType().Name}: {ex.Message}");
                scene = "?";
            }

            // Before the room is loaded the assets are simply not in memory yet; in the room a
            // missing asset means a game update renamed it.
            return string.Equals(scene, RoomScene, StringComparison.Ordinal)
                ? SmokeCheckResult.Fail(Id, problem + " (the IMGUI console is used instead); " + showing)
                : SmokeCheckResult.NotYetObserved(Id, "scene " + scene + ", assets load with " + RoomScene + ": " + problem);
        }
    }
}
