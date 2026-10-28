using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// Remembers which mod has already been told about which deprecated member, so each pair
    /// produces exactly one line per session. Thread-safe; pure logic, unit-tested.
    /// </summary>
    public sealed class DeprecationLedger
    {
        /// <summary>What a caller that is not a loaded mod is recorded as.</summary>
        public const string UnknownCaller = "(unknown)";

        private readonly object _gate = new object();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>How many distinct (mod, member) pairs have been recorded.</summary>
        public int Count
        {
            get { lock (_gate) { return _seen.Count; } }
        }

        /// <summary>
        /// Records a call and says whether it is the first from this mod to this member.
        /// </summary>
        /// <param name="modId">The calling mod's id; null or blank is <see cref="UnknownCaller"/>.</param>
        /// <param name="member">The deprecated member, e.g. <c>ICwyModSatone.SayLegacy</c>.</param>
        /// <returns>True the first time the pair is seen: the caller should log one line.</returns>
        public bool ShouldReport(string? modId, string member)
        {
            var key = (string.IsNullOrWhiteSpace(modId) ? UnknownCaller : modId!.Trim()) + "\n" + (member ?? string.Empty);
            lock (_gate)
            {
                return _seen.Add(key);
            }
        }

        /// <summary>
        /// The one line for a deprecated call, e.g.
        /// <c>cwy.sample.hello uses SayLegacy, deprecated since 0.9.0 and due for removal; use helper.Satone.Say instead</c>.
        /// </summary>
        /// <param name="modId">The calling mod's id, or null.</param>
        /// <param name="member">The deprecated member.</param>
        /// <param name="since">The API version that deprecated it.</param>
        /// <param name="stage">Its stage.</param>
        /// <param name="replacement">What to use instead, or null.</param>
        public static string Describe(string? modId, string member, string since, DeprecationStage stage, string? replacement)
        {
            var who = string.IsNullOrWhiteSpace(modId) ? UnknownCaller : modId!.Trim();
            var line = who + " uses " + member + ", deprecated since " + (string.IsNullOrWhiteSpace(since) ? "?" : since);
            if (stage == DeprecationStage.PendingRemoval)
            {
                line += " and due for removal";
            }

            return string.IsNullOrWhiteSpace(replacement) ? line : line + "; use " + replacement + " instead";
        }
    }
}
