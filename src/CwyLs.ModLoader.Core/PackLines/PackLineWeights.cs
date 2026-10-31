using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>One line in a weighted roll, with the weight it rolls at.</summary>
    public readonly struct PackLineCandidate
    {
        /// <summary>Creates a candidate.</summary>
        /// <param name="line">The line. Required.</param>
        /// <param name="weight">The weight it rolls at: its own <see cref="PackLine.Weight"/>, or an implied share (<see cref="PackLineWeights.EqualShare"/>).</param>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        public PackLineCandidate(PackLine line, double weight)
        {
            Line = line ?? throw new ArgumentNullException(nameof(line));
            Weight = double.IsNaN(weight) || weight < 0d ? 0d : weight > 1d ? 1d : weight;
        }

        /// <summary>The line.</summary>
        public PackLine Line { get; }

        /// <summary>Its weight, clamped to 0..1.</summary>
        public double Weight { get; }
    }

    /// <summary>
    /// The weighted pick (Martin, 2026-10-31): at each pick of a pool, the weighted lines offered there -
    /// written for the pool, or copies of a <c>gamestart.any</c> / global line - share one roll. Pure
    /// logic; the game side supplies the candidates, the last line played and two random numbers.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item><description>Lines with weight 0 never play.</description></item>
    ///   <item><description>No repeat: with two or more candidates, the line that played last in this
    ///   pool (by <see cref="PackLine.SourceGroupId"/>) sits this pick out - unless its weight is 1.</description></item>
    ///   <item><description>The chance that a pack line takes the pick is the sum of the weights, capped
    ///   at 1; the game's own pick runs otherwise.</description></item>
    ///   <item><description>Which line: by weight (two lines at weight 1 split 50/50).</description></item>
    /// </list>
    /// </remarks>
    public static class PackLineWeights
    {
        /// <summary>
        /// The weight an unweighted line rolls at in a pool without a deck (the startup greetings): an
        /// equal share with the game's lines, as if it were one more card - <c>1 / (gameLines + unweighted)</c>.
        /// </summary>
        /// <param name="gameLines">How many lines the game itself draws from for this pick (<see cref="PackLinePool.VanillaCount"/>).</param>
        /// <param name="unweighted">How many unweighted pack lines are offered for this pick.</param>
        public static double EqualShare(int gameLines, int unweighted)
        {
            var total = Math.Max(0, gameLines) + Math.Max(0, unweighted);
            return total <= 0 ? 0d : 1d / total;
        }

        /// <summary>Picks the line that takes this pick, or null for the game's own pick.</summary>
        /// <param name="candidates">The lines offered for this pick (language and clips already checked).</param>
        /// <param name="lastSourceGroupId">The <see cref="PackLine.SourceGroupId"/> of the pack line this pool played last; null for none.</param>
        /// <param name="roll">A uniform number in [0, 1): does a pack line take the pick.</param>
        /// <param name="pick">A uniform number in [0, 1): which one.</param>
        public static PackLine? Roll(IReadOnlyList<PackLineCandidate>? candidates, string? lastSourceGroupId, double roll, double pick)
        {
            if (candidates is null || candidates.Count == 0)
            {
                return null;
            }

            var live = new List<PackLineCandidate>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (candidate.Line != null && candidate.Weight > 0d)
                {
                    live.Add(candidate);
                }
            }

            if (live.Count > 1 && !string.IsNullOrEmpty(lastSourceGroupId))
            {
                live.RemoveAll(c => c.Weight < 1d && string.Equals(c.Line.SourceGroupId, lastSourceGroupId, StringComparison.Ordinal));
            }

            if (live.Count == 0)
            {
                return null;
            }

            var sum = 0d;
            foreach (var candidate in live)
            {
                sum += candidate.Weight;
            }

            if (roll >= Math.Min(1d, sum))
            {
                return null;
            }

            var target = pick * sum;
            var running = 0d;
            foreach (var candidate in live)
            {
                running += candidate.Weight;
                if (target < running)
                {
                    return candidate.Line;
                }
            }

            return live[live.Count - 1].Line;
        }
    }
}
