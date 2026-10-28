using System.Collections.Generic;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Actions
{
    /// <summary>
    /// Quiet smoke checks of the action runner: it is installed and the families
    /// registered, and the self-test action ran through the real dispatcher on the main thread.
    /// Nothing here changes the game.
    /// </summary>
    internal static class ActionSmokeChecks
    {
        /// <summary>Smoke phase label of the checks.</summary>
        internal const string Phase = "E";

        /// <summary>Runner installed, families discovered, none failed.</summary>
        internal const string RunnerId = "e.actions.runner";

        /// <summary>The private self-test action answered Ok on the main thread.</summary>
        internal const string SelfTestId = "e.actions.selftest";

        /// <summary>Registers both checks; idempotent (the registry replaces by id).</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(RunnerId, Phase, Runner));
            SmokeRegistry.Register(new SmokeCheck(SelfTestId, Phase, SelfTest));
        }

        private static SmokeCheckResult Runner()
        {
            if (!ActionsFeature.Enabled)
            {
                return SmokeCheckResult.Skipped(RunnerId, "[Actions] Enabled=false");
            }

            if (!ActionServices.Shared.HasRunner)
            {
                return SmokeCheckResult.Fail(RunnerId, "no action runner is installed on ActionServices.Shared");
            }

            var catalog = ActionsFeature.Families;
            if (catalog is null || !ActionsFeature.HasDiscovered)
            {
                return SmokeCheckResult.NotYetObserved(RunnerId, "families are discovered on the first frame");
            }

            var failed = new List<string>();
            foreach (var outcome in catalog.Outcomes)
            {
                if (outcome.State == ActionFamilyState.Failed)
                {
                    failed.Add(outcome.ToString());
                }
            }

            var summary = catalog.Summary() + "; " + ActionRegistry.Shared.Count + " registered";
            return failed.Count == 0
                ? SmokeCheckResult.Pass(RunnerId, summary)
                : SmokeCheckResult.Fail(RunnerId, "failed: " + string.Join("; ", failed) + " | " + summary);
        }

        private static SmokeCheckResult SelfTest()
        {
            if (!ActionsFeature.Enabled)
            {
                return SmokeCheckResult.Skipped(SelfTestId, "[Actions] Enabled=false");
            }

            var result = ActionsFeature.SelfTestResult;
            if (result is null)
            {
                return SmokeCheckResult.NotYetObserved(SelfTestId, "the self-test runs right after the first-frame discovery");
            }

            return result.IsOk && result.Path == ActionPath.Semantic
                ? SmokeCheckResult.Pass(SelfTestId, result.ToString())
                : SmokeCheckResult.Fail(SelfTestId, result.ToString());
        }
    }
}
