using System;
using CwyLs.ModLoader.Core.Scenario;
using CwyLs.ModLoader.Core.Smoke;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// Script-runtime smoke checks. Four are quiet and read-only; <see cref="DemoId"/> is loud (it plays the
    /// demo scene) and runs only on an explicit loud trigger.
    /// </summary>
    internal static class ScenarioSmokeChecks
    {
        /// <summary>Phase label these checks are reported under.</summary>
        internal const string Phase = "ES";

        /// <summary>Script rows run through the <c>ReadSentence</c> prefix (not the fallback).</summary>
        internal const string SeamId = "es.script.seam";

        /// <summary>The four built-ins and <c>scene.play</c> are registered and the registry reports the runtime.</summary>
        internal const string BuiltInsId = "es.script.builtins";

        /// <summary>The save-log guard is in and no pack scene is in the in-memory played list.</summary>
        internal const string GuardId = "es.script.guard";

        /// <summary>The installed packs' scenes lint without errors.</summary>
        internal const string LintId = "es.script.lint";

        /// <summary>Loud: plays the demo scene.</summary>
        internal const string DemoId = "es.script.demo";

        /// <summary>The demo pack's first scene.</summary>
        internal const string DemoGroup = "cwy_demo_script_01";

        /// <summary>Registers the checks. Idempotent: the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(SeamId, Phase, Seam));
            SmokeRegistry.Register(new SmokeCheck(BuiltInsId, Phase, BuiltIns));
            SmokeRegistry.Register(new SmokeCheck(GuardId, Phase, Guard));
            SmokeRegistry.Register(new SmokeCheck(LintId, Phase, LintCheck));
            SmokeRegistry.Register(new SmokeCheck(DemoId, Phase, Demo), loud: true);
        }

        private static SmokeCheckResult Seam()
        {
            if (!ScenarioFeature.Enabled)
            {
                return SmokeCheckResult.Skipped(SeamId, ScenarioFeature.InactiveReason);
            }

            return ScenarioRuntime.Mode == ScenarioRuntimeMode.Seam
                ? SmokeCheckResult.Pass(SeamId, ScenarioFeature.ModeDetail + "; " + ScenarioFeature.TargetSummary + "; rows run " + ScenarioRuntime.RowsRun)
                : SmokeCheckResult.Fail(SeamId, (ScenarioFeature.IsActive ? ScenarioFeature.ModeDetail : "off: " + ScenarioFeature.InactiveReason) + "; " + ScenarioFeature.TargetSummary);
        }

        private static SmokeCheckResult BuiltIns()
        {
            if (!ScenarioFeature.IsActive)
            {
                return SmokeCheckResult.Skipped(BuiltInsId, "script commands are off: " + ScenarioFeature.InactiveReason);
            }

            var registered = string.Join(",", ScenarioBuiltIns.Registered);
            if (ScenarioBuiltIns.Registered.Count != 5 || !ScenarioCommandRegistry.Shared.RuntimeAvailable)
            {
                return SmokeCheckResult.Fail(BuiltInsId, "registered: " + registered + "; runtime: " + ScenarioCommandRegistry.Shared.RuntimeDetail);
            }

            return SmokeCheckResult.Pass(BuiltInsId, registered + "; " + ScenarioCommandRegistry.Shared.Bindings.Count + " command(s) bound");
        }

        private static SmokeCheckResult Guard()
        {
            if (!ScenarioFeature.Enabled)
            {
                return SmokeCheckResult.Skipped(GuardId, ScenarioFeature.InactiveReason);
            }

            if (!ScenarioPatches.GuardApplied)
            {
                return SmokeCheckResult.Fail(GuardId, "the SaveScenarioPlayedLog prefix is not in place");
            }

            var progress = Game.SaveDataManager.Instance?.ScenarioProgressData;
            if (progress?.PlayedScenarioGroupIDs is null)
            {
                return SmokeCheckResult.NotYetObserved(GuardId, "the save is not loaded yet");
            }

            var lint = ScenarioFeature.Lint;
            foreach (var id in lint.PackScenes)
            {
                var group = ScenarioPlayer.FindPackGroup(id);
                if (group != null && group.Languages is null && progress.PlayedScenarioGroupIDs.Contains(id))
                {
                    return SmokeCheckResult.Fail(GuardId, "the played list holds the pack scene " + id);
                }
            }

            return SmokeCheckResult.Pass(GuardId,
                "guard on; " + ScenarioSaveGuard.Skipped + " pack scene(s) kept out this session" +
                (ScenarioSaveGuard.LastSkipped.Length > 0 ? " (last " + ScenarioSaveGuard.LastSkipped + ")" : string.Empty) +
                "; none in the played list (" + progress.PlayedScenarioGroupIDs.Count + " ids)");
        }

        private static SmokeCheckResult LintCheck()
        {
            var lint = ScenarioFeature.Lint;
            if (ReferenceEquals(lint, ScenarioLintReport.Empty))
            {
                return SmokeCheckResult.NotYetObserved(LintId, "the packs have not been linted yet (master data not injected)");
            }

            if (lint.ErrorCount > 0)
            {
                foreach (var finding in lint.Findings)
                {
                    if (finding.Severity == ScenarioLintSeverity.Error)
                    {
                        return SmokeCheckResult.Fail(LintId, lint.Summarize() + "; first: " + finding);
                    }
                }
            }

            return SmokeCheckResult.Pass(LintId, lint.Summarize());
        }

        private static SmokeCheckResult Demo()
        {
            if (ScenarioPlayer.FindPackGroup(DemoGroup) is null)
            {
                return SmokeCheckResult.Skipped(DemoId, "the demo pack cwy.demo.script is not installed");
            }

            ScenarioResult result;
            try
            {
                result = ScenarioPlayer.Play(DemoGroup, "smoke");
            }
            catch (Exception ex)
            {
                return SmokeCheckResult.Fail(DemoId, ex.GetType().Name + ": " + ex.Message);
            }

            return result.IsOk
                ? SmokeCheckResult.Pass(DemoId, result.Message + "; watch: a 3 s pause, then a choice; 'play it again' brings scene 02 after the room is idle")
                : SmokeCheckResult.Fail(DemoId, result.Message);
        }
    }
}
