using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// Scenery smoke checks for the final in-game pass: the property rules came out of the packs,
    /// every applied one is still on its material, the decoration seams are patched, and (loud) a
    /// restore puts the game's colours back.
    /// </summary>
    internal static class ScenerySmokeChecks
    {
        /// <summary>Phase label of the checks.</summary>
        internal const string Phase = "D";

        internal const string RulesId = "d.scenery.rules";
        internal const string AppliedId = "d.scenery.applied";
        internal const string SeamsId = "d.scenery.seams";
        internal const string RestoreId = "d.scenery.restore";

        /// <summary>Registers the checks; the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(RulesId, Phase, Rules));
            SmokeRegistry.Register(new SmokeCheck(AppliedId, Phase, Applied));
            SmokeRegistry.Register(new SmokeCheck(SeamsId, Phase, Seams));

            // Loud: the desk and scenery visibly return to the game's colours while it runs.
            SmokeRegistry.Register(new SmokeCheck(RestoreId, Phase, Restore), loud: true);
        }

        private static SmokeCheckResult Rules()
        {
            if (!SceneryFeature.Enabled)
            {
                return SmokeCheckResult.Pass(RulesId, "Scenery.Enabled=false; nothing to check");
            }

            var rules = TextureFeature.PropertyRules;
            if (rules.Problems.Count > 0)
            {
                return SmokeCheckResult.Fail(RulesId, rules.Summarize() + "; first: " + rules.Problems[0]);
            }

            return SmokeCheckResult.Pass(
                RulesId,
                rules.Count == 0 ? "no pack ships property rules (vacuous pass)" : rules.Summarize());
        }

        private static SmokeCheckResult Applied()
        {
            var rules = TextureFeature.PropertyRules;
            if (!SceneryFeature.Enabled || rules.Count == 0)
            {
                return SmokeCheckResult.Pass(AppliedId, "no property rules to apply (vacuous pass)");
            }

            var applied = TextureFeature.AppliedProperties;
            var mismatches = new List<string>();
            var live = MaterialPropertyRules.VerifyLive(mismatches);

            if (applied.Count < rules.Count || mismatches.Count > 0)
            {
                return SmokeCheckResult.Fail(
                    AppliedId,
                    applied.Count + " of " + rules.Count + " rule(s) applied; " + live + " slot(s) hold our value" +
                    (mismatches.Count == 0 ? string.Empty : "; reverted: " + string.Join(", ", mismatches.ToArray())));
            }

            return SmokeCheckResult.Pass(AppliedId, applied.Count + " rule(s) applied, " + live + " slot(s) verified on the live materials");
        }

        private static SmokeCheckResult Seams() =>
            SceneryPatches.Patched == 3
                ? SmokeCheckResult.Pass(SeamsId, "decoration seams 3/3")
                : SmokeCheckResult.Fail(SeamsId, "decoration seams " + SceneryPatches.Patched + "/3: " + SceneryPatches.Failures);

        private static SmokeCheckResult Restore()
        {
            if (TextureFeature.PropertyRules.Count == 0)
            {
                return SmokeCheckResult.Pass(RestoreId, "no property rules (vacuous pass)");
            }

            try
            {
                var restored = TextureFeature.RestoreAll();
                var leftover = MaterialPropertyRules.BackedUpCount;
                TextureFeature.Apply();

                return leftover == 0 && restored > 0
                    ? SmokeCheckResult.Pass(RestoreId, "restored " + restored + " slot(s), then re-applied: " + TextureFeature.LastSummary)
                    : SmokeCheckResult.Fail(RestoreId, "restored " + restored + " slot(s), " + leftover + " still held");
            }
            catch (Exception ex)
            {
                return SmokeCheckResult.Fail(RestoreId, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
