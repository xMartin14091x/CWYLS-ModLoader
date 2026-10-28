using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// Keeps a mod that throws on every frame from burying the log: the first failure at a site
    /// is logged, repeats within <see cref="WindowSeconds"/> are counted silently, and the next
    /// logged line carries that count. Thread-safe; pure logic over a caller-supplied clock.
    /// </summary>
    /// <remarks>
    /// A site is a bounded key (<see cref="SiteKey"/>: owner, stage and the first word of the
    /// label), and the table holds at most <see cref="MaxSites"/> of them, so labels that carry data
    /// (<c>chunk 17</c>, a file path) can neither defeat the limit nor grow the table without end.
    /// </remarks>
    public sealed class ModFailureLimiter
    {
        /// <summary>Default most sites tracked at once.</summary>
        public const int DefaultMaxSites = 256;

        /// <summary>The longest label prefix a <see cref="SiteKey"/> keeps.</summary>
        public const int LabelKeyLength = 24;

        /// <summary>The site every failure shares once the table is full of sites still in their window.</summary>
        public const string OverflowSite = "(other sites)";

        private readonly object _gate = new object();
        private readonly Dictionary<string, Site> _sites = new Dictionary<string, Site>(StringComparer.Ordinal);

        /// <summary>Creates a limiter.</summary>
        /// <param name="windowSeconds">How long after a logged failure repeats stay quiet. At least 0.</param>
        /// <param name="maxSites">Most sites tracked at once; at least 1.</param>
        public ModFailureLimiter(double windowSeconds = 10, int maxSites = DefaultMaxSites)
        {
            WindowSeconds = Math.Max(0, windowSeconds);
            MaxSites = Math.Max(1, maxSites);
        }

        /// <summary>Seconds after a logged failure during which repeats are only counted.</summary>
        public double WindowSeconds { get; }

        /// <summary>Most sites tracked at once (the overflow site not counted).</summary>
        public int MaxSites { get; }

        /// <summary>Sites currently tracked, the overflow site included.</summary>
        public int SiteCount
        {
            get { lock (_gate) { return _sites.Count; } }
        }

        /// <summary>
        /// The bounded site key for a failure: <c>owner|stage|labelKey</c>, where the label key is
        /// the label up to its first space, digit, quote, colon, bracket or slash, at most
        /// <see cref="LabelKeyLength"/> characters. <c>chunk 17</c> and <c>chunk 18</c> share a site.
        /// </summary>
        /// <param name="owner">Who: a mod id or a dispatcher owner.</param>
        /// <param name="stage">Where: a stage name, or a short word such as <c>tick</c>.</param>
        /// <param name="label">Which handler or work item; null for none.</param>
        /// <returns>The key. Never null.</returns>
        public static string SiteKey(string? owner, string? stage, string? label) =>
            (owner ?? string.Empty) + "|" + (stage ?? string.Empty) + "|" + LabelKey(label);

        /// <summary>The first word of a label, bounded; see <see cref="SiteKey"/>.</summary>
        /// <param name="label">A label, or null.</param>
        /// <returns>The prefix; empty for null.</returns>
        public static string LabelKey(string? label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return string.Empty;
            }

            var end = 0;
            while (end < label!.Length && end < LabelKeyLength && !EndsKey(label[end]))
            {
                end++;
            }

            return label.Substring(0, end);
        }

        /// <summary>
        /// Records one failure and decides whether to log it.
        /// </summary>
        /// <param name="site">Where it happened, normally a <see cref="SiteKey"/>, e.g. <c>cwy.sample.hello|Event|RoomReady</c>.</param>
        /// <param name="nowSeconds">A monotonic clock.</param>
        /// <param name="suppressed">How many repeats were swallowed since the last logged line.</param>
        /// <returns>True when this failure should be logged.</returns>
        public bool ShouldLog(string site, double nowSeconds, out int suppressed)
        {
            var key = site ?? string.Empty;
            lock (_gate)
            {
                if (!_sites.TryGetValue(key, out var entry))
                {
                    if (TrackedLocked() >= MaxSites)
                    {
                        Prune(nowSeconds);
                    }

                    if (TrackedLocked() >= MaxSites)
                    {
                        key = OverflowSite;
                        _sites.TryGetValue(key, out entry);
                    }

                    if (entry is null)
                    {
                        _sites[key] = new Site { LastLogged = nowSeconds };
                        suppressed = 0;
                        return true;
                    }
                }

                if (nowSeconds - entry.LastLogged >= WindowSeconds)
                {
                    suppressed = entry.Suppressed;
                    entry.Suppressed = 0;
                    entry.LastLogged = nowSeconds;
                    return true;
                }

                entry.Suppressed++;
                suppressed = entry.Suppressed;
                return false;
            }
        }

        /// <summary>Total failures swallowed across every site since the last logged line of each.</summary>
        public int PendingSuppressed
        {
            get
            {
                lock (_gate)
                {
                    var total = 0;
                    foreach (var site in _sites.Values)
                    {
                        total += site.Suppressed;
                    }

                    return total;
                }
            }
        }

        /// <summary>Sites tracked, not counting the overflow site. Caller holds the lock.</summary>
        private int TrackedLocked() => _sites.Count - (_sites.ContainsKey(OverflowSite) ? 1 : 0);

        private static bool EndsKey(char c) =>
            char.IsWhiteSpace(c) || char.IsDigit(c) || c == '\'' || c == '"' || c == ':' || c == '(' || c == '[' || c == '/' || c == '\\';

        /// <summary>
        /// Forgets sites whose window has passed with nothing swallowed: forgetting them changes
        /// nothing, because their next failure would be logged anyway.
        /// </summary>
        private void Prune(double nowSeconds)
        {
            List<string>? idle = null;
            foreach (var pair in _sites)
            {
                if (pair.Value.Suppressed == 0 && nowSeconds - pair.Value.LastLogged >= WindowSeconds)
                {
                    (idle ??= new List<string>()).Add(pair.Key);
                }
            }

            if (idle is null)
            {
                return;
            }

            foreach (var key in idle)
            {
                _sites.Remove(key);
            }
        }

        private sealed class Site
        {
            internal double LastLogged;
            internal int Suppressed;
        }
    }
}
