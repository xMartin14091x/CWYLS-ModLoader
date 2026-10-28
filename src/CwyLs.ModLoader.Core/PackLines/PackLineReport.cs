using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// The text of the console's <c>lines list</c>: pure, so its wording is unit-tested.
    /// </summary>
    public static class PackLineReport
    {
        /// <summary>
        /// Lists every pack line by pool, whether the game would offer it right now, the live state of
        /// each pool, what was refused and what is armed.
        /// </summary>
        /// <param name="registry">The accepted lines. Null is treated as empty.</param>
        /// <param name="rejected">The refused groups; null means none.</param>
        /// <param name="languageCode">The language the game shows, e.g. <c>th</c>; empty before the save has loaded.</param>
        /// <param name="inactiveReason">Why the feature is off, or empty when it is active.</param>
        /// <param name="live">Per pool, a short live-state phrase (e.g. <c>3 in the game's list</c>), or null when unknown.</param>
        /// <param name="armed">The armed timer test line, or null.</param>
        /// <returns>The console text, lines separated by <c>\n</c>.</returns>
        public static string List(
            PackLineRegistry? registry,
            IReadOnlyList<PackLineRejection>? rejected,
            string? languageCode,
            string? inactiveReason,
            Func<PackLinePool, string?>? live,
            PackLine? armed)
        {
            var lines = registry ?? PackLineRegistry.Empty;
            var code = languageCode ?? string.Empty;
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(inactiveReason))
            {
                sb.Append("pack lines are off: ").Append(inactiveReason).Append('\n');
            }

            var offered = 0;
            foreach (var line in lines.Lines)
            {
                if (line.IsOfferedIn(code))
                {
                    offered++;
                }
            }

            sb.Append("pack lines: ").Append(lines.ToString());
            if (lines.Languages.Count > 0)
            {
                sb.Append(", languages ").Append(string.Join("/", lines.Languages));
            }

            sb.Append("; the game shows ").Append(code.Length == 0 ? "(not loaded yet)" : code)
              .Append(": ").Append(offered.ToString(CultureInfo.InvariantCulture)).Append(" offered");

            foreach (var pool in lines.Pools)
            {
                var poolLines = lines.LinesOf(pool.Id);
                sb.Append('\n').Append(pool.Id).Append("  ").Append(pool.ScenarioName).Append("  ")
                  .Append(Weight(pool, poolLines.Count)).Append("  [").Append(pool.Selector).Append(']');

                var state = live?.Invoke(pool);
                if (!string.IsNullOrEmpty(state))
                {
                    sb.Append("  live: ").Append(state);
                }

                foreach (var line in poolLines)
                {
                    sb.Append("\n  #").Append(line.Episode.ToString(CultureInfo.InvariantCulture))
                      .Append("  ").Append(line.GroupId)
                      .Append("  ").Append(string.Join("/", line.Languages))
                      .Append("  ").Append(line.IsOfferedIn(code) ? "offered" : "not offered");
                }
            }

            if (rejected != null && rejected.Count > 0)
            {
                sb.Append("\nrejected ").Append(rejected.Count.ToString(CultureInfo.InvariantCulture)).Append(':');
                foreach (var rejection in rejected)
                {
                    sb.Append("\n  ").Append(rejection);
                }
            }

            if (armed != null)
            {
                sb.Append("\narmed: ").Append(armed).Append(" - ").Append(WhenArmedPlays(armed.Pool));
            }

            return sb.ToString();
        }

        /// <summary>
        /// How a pool's pack line weighs against a game line, for <c>lines list</c> and the README:
        /// <c>game 1-23 + 2 pack: each line once per 25-pick cycle</c>.
        /// </summary>
        /// <param name="pool">The pool. Required.</param>
        /// <param name="packLines">How many pack lines it holds (all languages).</param>
        /// <exception cref="ArgumentNullException"><paramref name="pool"/> is null.</exception>
        public static string Weight(PackLinePool pool, int packLines)
        {
            if (pool is null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            if (pool.IsDataDriven)
            {
                return "data-driven + " + packLines.ToString(CultureInfo.InvariantCulture)
                       + " pack: same lottery rules as the game's small talks";
            }

            var total = pool.VanillaCount + packLines;
            return "game " + pool.VanillaFirst.ToString(CultureInfo.InvariantCulture) + "-"
                   + pool.VanillaLast.ToString(CultureInfo.InvariantCulture) + " + "
                   + packLines.ToString(CultureInfo.InvariantCulture) + " pack: each line once per "
                   + total.ToString(CultureInfo.InvariantCulture) + "-pick cycle";
        }

        /// <summary>What has to happen in the game for an armed timer line to play.</summary>
        /// <param name="pool">The line's pool. Required.</param>
        /// <exception cref="ArgumentNullException"><paramref name="pool"/> is null.</exception>
        public static string WhenArmedPlays(PackLinePool pool)
        {
            if (pool is null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            string moment;
            switch (pool.Phase)
            {
                case PackLineTimerPhase.WorkStart:
                    moment = "work start";
                    break;
                case PackLineTimerPhase.BreakStart:
                    moment = "break start";
                    break;
                case PackLineTimerPhase.Finish:
                    moment = "timer finish";
                    break;
                default:
                    return "plays at once as a click reaction";
            }

            var mode = pool.TimerMode == PackLineTimerMode.Countup ? "count-up" : "pomodoro";
            return "plays at the next " + moment + " in " + mode + " mode, in place of the " + moment
                   + " line the game would have picked";
        }
    }
}
