using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core
{
    /// <summary>
    /// The result of checking a whole patch-target table: a one-line summary for the startup log
    /// plus one line per missing target.
    /// </summary>
    public sealed class PatchTargetReport
    {
        /// <summary>Prefix of a missing-target log line; grep-able from LogOutput.log.</summary>
        public const string MissingPrefix = "PATCH TARGET MISSING";

        /// <summary>Creates a report over already-resolved results.</summary>
        /// <param name="results">The per-target results, in table order.</param>
        /// <exception cref="ArgumentNullException"><paramref name="results"/> is null.</exception>
        public PatchTargetReport(IEnumerable<PatchTargetResult> results)
        {
            if (results == null)
            {
                throw new ArgumentNullException(nameof(results));
            }

            var all = new List<PatchTargetResult>();
            var missing = new List<PatchTargetResult>();
            foreach (var result in results)
            {
                all.Add(result);
                if (!result.Found)
                {
                    missing.Add(result);
                }
            }

            Results = all;
            MissingResults = missing;
        }

        /// <summary>Every result, in the order the targets were checked.</summary>
        public IReadOnlyList<PatchTargetResult> Results { get; }

        /// <summary>Only the results that did not resolve.</summary>
        public IReadOnlyList<PatchTargetResult> MissingResults { get; }

        /// <summary>How many targets resolved.</summary>
        public int OkCount => Results.Count - MissingResults.Count;

        /// <summary>How many targets did not resolve.</summary>
        public int MissingCount => MissingResults.Count;

        /// <summary>True when every target in the table resolved.</summary>
        public bool AllFound => MissingResults.Count == 0;

        /// <summary>The startup summary line, e.g. <c>targets: 26 ok, 0 missing</c>.</summary>
        public string Summarize() => "targets: " + OkCount + " ok, " + MissingCount + " missing";

        /// <summary>One <c>PATCH TARGET MISSING ...</c> line per missing target, in table order.</summary>
        public IReadOnlyList<string> MissingLines()
        {
            var lines = new List<string>(MissingResults.Count);
            foreach (var result in MissingResults)
            {
                lines.Add(FormatMissing(result));
            }

            return lines;
        }

        /// <summary>Formats a single missing target as <c>PATCH TARGET MISSING &lt;id&gt;: &lt;detail&gt;</c>.</summary>
        /// <param name="result">A result; expected to be a missing one.</param>
        /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
        public static string FormatMissing(PatchTargetResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return MissingPrefix + " " + result.Target.Id + ": " + result.Detail;
        }
    }
}
