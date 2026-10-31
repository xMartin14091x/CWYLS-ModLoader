using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// The accepted pack lines, indexed the ways the game-side patches ask: by pool, by
    /// (pool, episode), by group id. Immutable; a new pack state builds a new registry, so a reader on
    /// the game's thread never sees a half-built one.
    /// </summary>
    public sealed class PackLineRegistry
    {
        private static readonly PackLine[] NoLines = new PackLine[0];
        private static readonly int[] NoEpisodes = new int[0];

        private readonly Dictionary<string, PackLine[]> _byPool;
        private readonly Dictionary<string, int[]> _episodesByPool;
        private readonly Dictionary<string, PackLine> _byGroup;
        private readonly Dictionary<string, Dictionary<int, PackLine>> _byPoolEpisode;

        private PackLineRegistry(IReadOnlyList<PackLine> lines)
        {
            var accepted = new List<PackLine>(lines.Count);
            _byPool = new Dictionary<string, PackLine[]>(StringComparer.OrdinalIgnoreCase);
            _episodesByPool = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            _byGroup = new Dictionary<string, PackLine>(StringComparer.Ordinal);
            _byPoolEpisode = new Dictionary<string, Dictionary<int, PackLine>>(StringComparer.OrdinalIgnoreCase);

            var grouped = new Dictionary<string, List<PackLine>>(StringComparer.OrdinalIgnoreCase);
            var poolOrder = new List<string>();
            var languages = new List<string>();

            foreach (var line in lines)
            {
                if (_byGroup.ContainsKey(line.GroupId))
                {
                    continue;
                }

                if (!_byPoolEpisode.TryGetValue(line.Pool.Id, out var episodes))
                {
                    episodes = new Dictionary<int, PackLine>();
                    _byPoolEpisode.Add(line.Pool.Id, episodes);
                }

                if (episodes.ContainsKey(line.Episode))
                {
                    continue;
                }

                episodes.Add(line.Episode, line);
                _byGroup.Add(line.GroupId, line);
                accepted.Add(line);

                if (!grouped.TryGetValue(line.Pool.Id, out var list))
                {
                    list = new List<PackLine>();
                    grouped.Add(line.Pool.Id, list);
                    poolOrder.Add(line.Pool.Id);
                }

                list.Add(line);

                foreach (var code in line.Languages)
                {
                    if (!languages.Contains(code))
                    {
                        languages.Add(code);
                    }
                }
            }

            // Pools are reported in catalogue order, so the SETUP line and 'lines list' read the same
            // whatever order the packs listed their rows in.
            var pools = new List<PackLinePool>();
            foreach (var pool in PackLinePools.All)
            {
                if (!grouped.TryGetValue(pool.Id, out var list))
                {
                    continue;
                }

                pools.Add(pool);
                _byPool.Add(pool.Id, list.ToArray());
                var numbers = new int[list.Count];
                for (var i = 0; i < list.Count; i++)
                {
                    numbers[i] = list[i].Episode;
                }

                _episodesByPool.Add(pool.Id, numbers);
            }

            Lines = accepted;
            Pools = pools;
            Languages = languages;
            GroupIds = new HashSet<string>(_byGroup.Keys, StringComparer.Ordinal);
            Fingerprint = BuildFingerprint(accepted);
        }

        /// <summary>A registry holding no lines.</summary>
        public static PackLineRegistry Empty { get; } = new PackLineRegistry(NoLines);

        /// <summary>
        /// Builds a registry. A second line for an already-registered group id, or for a (pool,
        /// episode) another line holds, is ignored: <see cref="PackLineScreen"/> refuses both first,
        /// so this only guards a hand-built list.
        /// </summary>
        /// <param name="lines">The accepted lines; null means none.</param>
        public static PackLineRegistry Build(IEnumerable<PackLine>? lines)
        {
            if (lines is null)
            {
                return Empty;
            }

            var copy = new List<PackLine>();
            foreach (var line in lines)
            {
                if (line != null)
                {
                    copy.Add(line);
                }
            }

            return copy.Count == 0 ? Empty : new PackLineRegistry(copy);
        }

        /// <summary>Every accepted line, in the order given (duplicates dropped).</summary>
        public IReadOnlyList<PackLine> Lines { get; }

        /// <summary>How many lines.</summary>
        public int Count => _byGroup.Count;

        /// <summary>The pools that hold at least one line, in catalogue order.</summary>
        public IReadOnlyList<PackLinePool> Pools { get; }

        /// <summary>Every language code some line is offered in, in first-seen order.</summary>
        public IReadOnlyList<string> Languages { get; }

        /// <summary>
        /// Every line's group id - every group injected for a pack line, spread copies
        /// (<c>&lt;id&gt;@&lt;pool&gt;</c>) included: what the save filter holds back.
        /// </summary>
        public IReadOnlyCollection<string> GroupIds { get; }

        /// <summary>
        /// A stable text over every line's group, pool, episode, languages, weight and spread; equal
        /// fingerprints mean an identical registry, so a re-injection with unchanged packs logs nothing new.
        /// </summary>
        public string Fingerprint { get; }

        /// <summary>The lines of one pool, in the order given; empty when none.</summary>
        /// <param name="poolId">A pool id.</param>
        public IReadOnlyList<PackLine> LinesOf(string? poolId) =>
            poolId != null && _byPool.TryGetValue(poolId, out var lines) ? lines : (IReadOnlyList<PackLine>)NoLines;

        /// <summary>The episode numbers of one pool's lines, in the order given; empty when none.</summary>
        /// <param name="poolId">A pool id.</param>
        public IReadOnlyList<int> EpisodesOf(string? poolId) =>
            poolId != null && _episodesByPool.TryGetValue(poolId, out var episodes) ? episodes : (IReadOnlyList<int>)NoEpisodes;

        /// <summary>Finds the line a pool holds under an episode number.</summary>
        /// <param name="poolId">A pool id.</param>
        /// <param name="episode">An episode number.</param>
        /// <param name="line">The line, or null.</param>
        /// <returns>True when the episode is one of this pool's pack lines.</returns>
        public bool TryGet(string? poolId, int episode, out PackLine? line)
        {
            line = null;
            return poolId != null
                   && _byPoolEpisode.TryGetValue(poolId, out var episodes)
                   && episodes.TryGetValue(episode, out line);
        }

        /// <summary>Finds a line by its scenario group id.</summary>
        /// <param name="groupId">A group id.</param>
        /// <param name="line">The line, or null.</param>
        /// <returns>True when the id is a pack line's group.</returns>
        public bool TryGetByGroup(string? groupId, out PackLine? line)
        {
            line = null;
            return groupId != null && _byGroup.TryGetValue(groupId, out line);
        }

        /// <summary>True when the id is a pack line's group (the save filter's test).</summary>
        /// <param name="groupId">A scenario group id.</param>
        public bool IsPackGroup(string? groupId) => groupId != null && _byGroup.ContainsKey(groupId);

        /// <summary>
        /// The pools the game shows when it displays <paramref name="languageCode"/>, for the SETUP line:
        /// <c>click.work:3,pomodoro.start:2</c> (pool:line count, catalogue order). Empty when none.
        /// </summary>
        /// <param name="languageCode">A language code, or null for every line regardless of language.</param>
        public string DescribePools(string? languageCode = null)
        {
            var sb = new StringBuilder();
            foreach (var pool in Pools)
            {
                var count = 0;
                foreach (var line in LinesOf(pool.Id))
                {
                    if (languageCode is null || line.IsOfferedIn(languageCode))
                    {
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(pool.Id).Append(':').Append(count.ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        /// <summary><c>N line(s) in M pool(s)</c>.</summary>
        public override string ToString() => Count + " line(s) in " + Pools.Count + " pool(s)";

        private static string BuildFingerprint(IReadOnlyList<PackLine> lines)
        {
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                sb.Append(line.GroupId).Append('|').Append(line.Pool.Id).Append('|')
                  .Append(line.Episode.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(string.Join("/", line.Languages)).Append('|')
                  .Append(line.Weight.HasValue ? line.Weight.Value.ToString("R", CultureInfo.InvariantCulture) : "-").Append('|')
                  .Append(line.Spread).Append(';');
            }

            return sb.ToString();
        }
    }
}
