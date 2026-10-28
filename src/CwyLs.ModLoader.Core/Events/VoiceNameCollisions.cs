using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>One voice clip name claimed by more than one localization row.</summary>
    public readonly struct VoiceNameCollision
    {
        /// <summary>Creates a collision.</summary>
        /// <param name="voice">The clip name two or more rows claim.</param>
        /// <param name="ids">The row ids that claim it, in table order.</param>
        public VoiceNameCollision(string voice, IReadOnlyList<string> ids)
        {
            Voice = voice;
            Ids = ids;
        }

        /// <summary>The clip name.</summary>
        public string Voice { get; }

        /// <summary>The row ids that claim it, in table order. Always two or more.</summary>
        public IReadOnlyList<string> Ids { get; }

        /// <summary><c>Voice_X (a, b)</c>.</summary>
        public string Describe() => Voice + " (" + string.Join(", ", Ids) + ")";
    }

    /// <summary>
    /// Finds the voice clip names that more than one localization row claims.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reverse voice-name index can only keep one row per clip name, so on a collision it
    /// silently picks a winner and every line that plays that clip is attributed to it. That is the
    /// bug that made a Say line report a content pack's row id. Attribution from the scenario row
    /// removes the dependency for scripted lines; this makes the remaining ambiguity visible
    /// instead of silent.
    /// </para>
    /// <para>
    /// Collisions are legitimate: the shipped v1.17.2 table has two of them
    /// (<c>Voice_MainScenario_6_009</c> and <c>Voice_ClickHeroine_Word_Work_013</c>, each claimed by
    /// a scenario row and a <c>special_scenario_statisticsDev_3_*</c> row), out of 1354 distinct
    /// clip names across 1356 voiced rows. So this is a one-line notice, not an error.
    /// </para>
    /// </remarks>
    public static class VoiceNameCollisions
    {
        /// <summary>How many collisions a log line names before it says "and N more".</summary>
        public const int DefaultMax = 10;

        /// <summary>
        /// Every clip name claimed by two or more rows, most-claimed first and then by name, so the
        /// log line is stable across runs.
        /// </summary>
        /// <param name="rows">The localization table; null yields an empty list. Rows without a voice are ignored.</param>
        public static IReadOnlyList<VoiceNameCollision> Find(IEnumerable<VoiceIndexEntry>? rows)
        {
            var byVoice = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>();

            if (rows != null)
            {
                foreach (var row in rows)
                {
                    if (!row.HasVoice)
                    {
                        continue;
                    }

                    var voice = row.Voice!;
                    if (!byVoice.TryGetValue(voice, out var ids))
                    {
                        ids = new List<string>(2);
                        byVoice.Add(voice, ids);
                        order.Add(voice);
                    }

                    ids.Add(row.Id ?? string.Empty);
                }
            }

            var found = new List<VoiceNameCollision>();
            foreach (var voice in order)
            {
                var ids = byVoice[voice];
                if (ids.Count > 1)
                {
                    found.Add(new VoiceNameCollision(voice, ids));
                }
            }

            found.Sort(Compare);
            return found;
        }

        /// <summary>
        /// The one log line for a set of collisions: how many there are, then up to
        /// <paramref name="max"/> of them.
        /// </summary>
        /// <param name="collisions">The collisions, from <see cref="Find"/>.</param>
        /// <param name="max">How many to name; the rest are counted. Values below 1 name none.</param>
        /// <returns>An empty string when there are no collisions, so the caller can skip the log entirely.</returns>
        public static string Summarize(IReadOnlyList<VoiceNameCollision>? collisions, int max = DefaultMax)
        {
            if (collisions is null || collisions.Count == 0)
            {
                return string.Empty;
            }

            var shown = Math.Max(0, Math.Min(max, collisions.Count));
            var sb = new StringBuilder();
            sb.Append(collisions.Count.ToString(CultureInfo.InvariantCulture))
                .Append(collisions.Count == 1 ? " voice name is" : " voice names are")
                .Append(" claimed by more than one localization row, so the voice index can only "
                        + "name one of them");

            for (var i = 0; i < shown; i++)
            {
                sb.Append(i == 0 ? ": " : "; ").Append(collisions[i].Describe());
            }

            if (shown < collisions.Count)
            {
                sb.Append("; and ")
                    .Append((collisions.Count - shown).ToString(CultureInfo.InvariantCulture))
                    .Append(" more");
            }

            return sb.ToString();
        }

        private static int Compare(VoiceNameCollision a, VoiceNameCollision b)
        {
            var byCount = b.Ids.Count.CompareTo(a.Ids.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Voice, b.Voice);
        }
    }
}
