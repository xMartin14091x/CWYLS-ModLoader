using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// The smoke check <c>p2.masterdata.save_filter</c>: whether master data's save filter
    /// (<see cref="PackSaveHoldBack"/>) is in place, and a dry run of it on a copy of the game's played list.
    /// Before it, the filter's state showed only in the log. Read-only: the played list is never changed.
    /// </summary>
    public static class PackSaveFilterSmoke
    {
        /// <summary>The check id.</summary>
        public const string Id = "p2.masterdata.save_filter";

        /// <summary>The verdict.</summary>
        /// <param name="masterDataEnabled">False when master data is off: nothing is injected, so nothing needs holding back.</param>
        /// <param name="disabledReason">Why master data is off, for the report.</param>
        /// <param name="holdBack">The filter. Required.</param>
        /// <param name="played">The game's <c>PlayedScenarioGroupIDs</c>, or null when the save is not loaded.</param>
        /// <param name="rule">The hold-back rule to dry-run; null uses <see cref="PackSaveHoldBack.IsHeld"/> (tests pass another).</param>
        /// <exception cref="ArgumentNullException"><paramref name="holdBack"/> is null.</exception>
        public static SmokeCheckResult Evaluate(bool masterDataEnabled, string disabledReason, PackSaveHoldBack holdBack, IList<string>? played,
            Func<string, bool>? rule = null)
        {
            if (holdBack is null)
            {
                throw new ArgumentNullException(nameof(holdBack));
            }

            if (!masterDataEnabled)
            {
                return SmokeCheckResult.Skipped(Id, "master data is off (" + disabledReason + "): no pack id is injected, so none needs holding back");
            }

            if (!holdBack.Installed)
            {
                return SmokeCheckResult.Fail(Id, "the save filter is OFF (" + holdBack.Problem + "): pack small talk is not injected and pack lines stay off");
            }

            var state = "installed; " + holdBack.AddedGroups.Count + " pack-added group(s) this session; pack lines "
                + (holdBack.PackLinesJoined ? "joined" : "not joined");
            if (played is null)
            {
                return SmokeCheckResult.NotYetObserved(Id, state + "; the save is not loaded yet, so there is no played list to dry-run");
            }

            var copy = new List<string>(played);
            var held = PackLineSaveFilter.Strip(copy, rule ?? holdBack.IsHeld);
            var added = 0;
            foreach (var id in played)
            {
                if (holdBack.AddedGroups.Contains(id)) added++;
            }

            // Independent of the rule: a recorded pack-added group still in the list after the dry run would be written.
            var leaked = 0;
            foreach (var id in copy)
            {
                if (holdBack.AddedGroups.Contains(id)) leaked++;
            }

            var dryRun = "a save would hold back " + held.Count + " of " + played.Count + " played id(s)";
            return leaked > 0
                ? SmokeCheckResult.Fail(Id, state + "; " + added + " pack-added group(s) in memory, " + leaked + " would be written: " + dryRun)
                : SmokeCheckResult.Pass(Id, state + "; " + dryRun);
        }
    }
}
