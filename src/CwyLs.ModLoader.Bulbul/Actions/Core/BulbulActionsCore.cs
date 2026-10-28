using System;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Bulbul.Actions
{
    /// <summary>
    /// The game half of the action core: checks the seams of <see cref="GameServices"/>, and registers
    /// two quiet smoke checks (services resolve in the room; the UI-click deny list holds). The
    /// action families themselves need no init: they are found by <c>[ActionFamily]</c> on the
    /// first frame (<c>CwyLs.ModLoader.Actions.ActionsFeature</c>).
    /// </summary>
    public static class BulbulActionsCore
    {
        /// <summary>Phase label the checks are reported under.</summary>
        public const string Phase = "E";

        /// <summary>Smoke: <c>HeroineService</c> resolves from the room scope and <c>MusicService</c> from a scope.</summary>
        public const string ServicesCheckId = "e.actions.services";

        /// <summary>Smoke: a probe of the SNS button is refused by the deny list, and a probe of the room canvas resolves.</summary>
        public const string UiClickCheckId = "e.actions.uiclick";

        /// <summary>A control the deny list must refuse (the X button of the settings' SNS row, RoomScene).</summary>
        public const string DeniedProbePath = "Paremt/PCPlatform/Canvas/UI/ChangeOrderObjects/UI_FacilitySetting/SNS/X";

        /// <summary>An object that exists while the room is loaded (the room UI canvas, RoomScene).</summary>
        public const string ResolvableProbePath = "Paremt/PCPlatform/Canvas";

        private static bool _initialized;

        /// <summary>Verifies the seams and registers the smoke checks. Idempotent; call from the adapter plugin's Awake.</summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (actions)", ActionsCoreTargets.All);
            var report = PatchTargetVerifier.VerifyAll(ActionsCoreTargets.All, new ReflectionMemberResolver());
            var room = true;
            var project = true;
            foreach (var missing in report.MissingResults)
            {
                switch (missing.Target.Id)
                {
                    case ActionsCoreTargets.RoomScopeId:
                        room = false;
                        break;
                    case ActionsCoreTargets.ProjectScopeId:
                        project = false;
                        break;
                    default:
                        room = false;
                        project = false;
                        break;
                }
            }

            GameServices.UseSeams(room, project);
            if (!room || !project)
            {
                CwyLsModLoader.Log.LogWarning(
                    "ACTION game services: " + (room ? string.Empty : "room scope lookup off; ") +
                    (project ? string.Empty : "project scope lookup off; ") + "scene objects are still found (" + report.Summarize() + ")");
            }

            SmokeRegistry.Register(new SmokeCheck(ServicesCheckId, Phase, Services));
            SmokeRegistry.Register(new SmokeCheck(UiClickCheckId, Phase, UiClickCheck));

            ConsoleEvents.Setup(
                "actions.game",
                "game services and UI click ready",
                ("room", room ? "on" : "off"),
                ("project", project ? "on" : "off"),
                ("denyWords", UiClickPolicy.DeniedWords.Count),
                ("denyTypes", UiClick.DeniedTypes.Count));
        }

        private static SmokeCheckResult Services()
        {
            if (!GameServices.IsRoomReady)
            {
                return SmokeCheckResult.NotYetObserved(ServicesCheckId, "the room is not loaded");
            }

            var heroine = GameServices.Lookup<global::Bulbul.HeroineService>();
            var music = GameServices.Lookup<global::MusicService>();
            var detail = heroine + "; " + music;
            return heroine.Found && heroine.Via == GameServices.ViaRoom && music.Found
                ? SmokeCheckResult.Pass(ServicesCheckId, detail)
                : SmokeCheckResult.Fail(ServicesCheckId, detail);
        }

        private static SmokeCheckResult UiClickCheck()
        {
            if (!GameServices.IsRoomReady)
            {
                return SmokeCheckResult.NotYetObserved(UiClickCheckId, "the room is not loaded");
            }

            var denied = UiClick.Probe(DeniedProbePath);
            var resolvable = UiClick.Probe(ResolvableProbePath);
            var detail = "deny probe: " + denied + "; resolve probe: " + resolvable;
            var resolved = resolvable.Status == UiClickStatus.Probed
                           || resolvable.Status == UiClickStatus.NoHandler
                           || resolvable.Status == UiClickStatus.NotInteractable;
            return denied.Status == UiClickStatus.Denied && resolved
                ? SmokeCheckResult.Pass(UiClickCheckId, detail)
                : SmokeCheckResult.Fail(UiClickCheckId, detail);
        }
    }
}
