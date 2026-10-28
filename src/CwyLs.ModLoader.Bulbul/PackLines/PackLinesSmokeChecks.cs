using System.Collections.Generic;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Bulbul.PackLines
{
    /// <summary>
    /// Pack-lines smoke checks. All four are quiet and read-only: nothing is played, no language is
    /// switched, and the pool checks run the gate on copies of the game's lists.
    /// </summary>
    internal static class PackLinesSmokeChecks
    {
        /// <summary>Phase label these checks are reported under.</summary>
        internal const string Phase = "PL";

        /// <summary>The seams verified and every patch is in place.</summary>
        internal const string PatchesId = "pl.lines.patches";

        /// <summary>The packs' language-exclusive lines were accepted, none refused.</summary>
        internal const string RegistryId = "pl.lines.registry";

        /// <summary>Each pool holds its pack lines only while their language is shown.</summary>
        internal const string PoolsId = "pl.lines.pools";

        /// <summary>A save holds back every pack-line id the session recorded.</summary>
        internal const string SaveFilterId = "pl.lines.savefilter";

        /// <summary>A language code no line is offered in: the dry run for "any other language".</summary>
        internal const string ClosedProbeCode = "x-none";

        /// <summary>Registers the four checks. Idempotent: the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(PatchesId, Phase, Patches));
            SmokeRegistry.Register(new SmokeCheck(RegistryId, Phase, RegistryCheck));
            SmokeRegistry.Register(new SmokeCheck(PoolsId, Phase, Pools));
            SmokeRegistry.Register(new SmokeCheck(SaveFilterId, Phase, SaveFilter));
        }

        private static SmokeCheckResult Patches()
        {
            if (!PackLinesFeature.Enabled)
            {
                return SmokeCheckResult.Skipped(PatchesId, PackLinesFeature.InactiveReason);
            }

            return PackLinesFeature.IsActive
                ? SmokeCheckResult.Pass(PatchesId, PackLinesFeature.TargetSummary + "; " + PackLinesPatches.Detail + "; " + PackLinesVoiceCheck.Detail)
                : SmokeCheckResult.Fail(PatchesId, "off: " + PackLinesFeature.InactiveReason);
        }

        private static SmokeCheckResult RegistryCheck()
        {
            if (!PackLinesFeature.IsActive)
            {
                return SmokeCheckResult.Skipped(RegistryId, "pack lines are off: " + PackLinesFeature.InactiveReason);
            }

            if (!MasterDataFeature.IsInjected)
            {
                return SmokeCheckResult.NotYetObserved(RegistryId, "the game's master data is not injected yet");
            }

            var registry = PackLinesFeature.Registry;
            var rejected = PackLinesFeature.Rejected;
            if (rejected.Count > 0)
            {
                var reasons = new List<string>();
                foreach (var rejection in rejected)
                {
                    reasons.Add(rejection.ToString());
                }

                return SmokeCheckResult.Fail(
                    RegistryId, rejected.Count + " pack line(s) refused (accepted: " + registry + "): " + string.Join(" | ", reasons));
            }

            if (registry.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(
                    RegistryId, "no loaded pack declares a language-exclusive line (\"Languages\" on a scenario group)");
            }

            return SmokeCheckResult.Pass(
                RegistryId, registry + ": " + registry.DescribePools() + "; languages " + string.Join("/", registry.Languages));
        }

        private static SmokeCheckResult Pools()
        {
            if (!PackLinesFeature.IsActive)
            {
                return SmokeCheckResult.Skipped(PoolsId, "pack lines are off: " + PackLinesFeature.InactiveReason);
            }

            var registry = PackLinesFeature.Registry;
            if (registry.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(PoolsId, "no pack lines to check");
            }

            if (PackLinesRuntime.BoundListCount == 0 && !PackLinesRuntime.SmallTalkBound)
            {
                return SmokeCheckResult.NotYetObserved(PoolsId, "the room's selectors are not set up yet (room not loaded)");
            }

            var shown = LanguageFeature.CurrentCode;
            var problems = new List<string>();

            // 1. Any language no line is offered in: every pool is the game's own, untouched.
            var closed = PackLinesRuntime.DryRun(ClosedProbeCode);
            var closedPools = 0;
            foreach (var run in closed)
            {
                closedPools++;
                if (run.PackAfter != 0 || run.GameAfter != run.GameBefore)
                {
                    problems.Add("other language: " + run);
                }
            }

            // 2. Each language some line is offered in (th): the pool gains exactly its owed lines.
            var open = new List<string>();
            foreach (var code in registry.Languages)
            {
                foreach (var run in PackLinesRuntime.DryRun(code))
                {
                    if (!run.Ok)
                    {
                        problems.Add(code + ": " + run);
                    }
                    else if (run.PackAfter > 0 && code == shown)
                    {
                        open.Add(run.ToString());
                    }
                }
            }

            if (problems.Count > 0)
            {
                return SmokeCheckResult.Fail(PoolsId, string.Join(" | ", problems));
            }

            return SmokeCheckResult.Pass(
                PoolsId,
                "any other language: pack 0 in " + closedPools + " pool(s), game episodes untouched; shown now: "
                + (shown.Length == 0 ? "(not loaded)" : shown)
                + (open.Count == 0 ? " (no pack line offered now)" : " - " + string.Join(", ", open)));
        }

        private static SmokeCheckResult SaveFilter()
        {
            if (!PackLinesFeature.IsActive)
            {
                return SmokeCheckResult.Skipped(SaveFilterId, "pack lines are off: " + PackLinesFeature.InactiveReason);
            }

            var (inMemory, heldBack, readable) = PackLinesRuntime.DryRunSaveFilter();
            if (!readable)
            {
                return SmokeCheckResult.NotYetObserved(SaveFilterId, "the save is not loaded yet");
            }

            if (heldBack < inMemory)
            {
                return SmokeCheckResult.Fail(
                    SaveFilterId, inMemory + " pack-line id(s) in memory but a save would hold back only " + heldBack);
            }

            return SmokeCheckResult.Pass(
                SaveFilterId,
                inMemory + " pack-line id(s) recorded this session; a save writes none of them (" + heldBack + " held back)");
        }
    }
}
