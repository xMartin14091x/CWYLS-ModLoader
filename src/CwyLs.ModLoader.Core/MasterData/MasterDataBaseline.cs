using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// What the game already has, reduced to the only thing the merge needs to know: which ids
    /// exist, and - for novel rows - how many times.
    /// <para>
    /// The count matters because the game's own <c>NovelMaster</c> is <em>not</em> unique by id:
    /// 26 of its 2289 rows share an id with another row (observed in
    /// the game's NovelMaster data). Overriding such an id would have to
    /// guess which of the rows was meant, so the merge refuses it and says so.
    /// </para>
    /// </summary>
    public sealed class MasterDataBaseline
    {
        private readonly HashSet<string> _localizationIds;
        private readonly Dictionary<string, int> _novelIdCounts;
        private readonly HashSet<string> _scenarioGroupIds;

        private MasterDataBaseline(
            HashSet<string> localizationIds,
            Dictionary<string, int> novelIdCounts,
            HashSet<string> scenarioGroupIds)
        {
            _localizationIds = localizationIds;
            _novelIdCounts = novelIdCounts;
            _scenarioGroupIds = scenarioGroupIds;
        }

        /// <summary>A baseline holding nothing: every pack row then counts as an addition.</summary>
        public static MasterDataBaseline Empty { get; } = new MasterDataBaseline(
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        /// <summary>Number of distinct localization ids the game has.</summary>
        public int LocalizationCount => _localizationIds.Count;

        /// <summary>Number of distinct novel ids the game has.</summary>
        public int NovelCount => _novelIdCounts.Count;

        /// <summary>Number of scenario-group ids the game has.</summary>
        public int ScenarioGroupCount => _scenarioGroupIds.Count;

        /// <summary>Builds a baseline from the game's ids. Null entries and blanks are ignored.</summary>
        /// <param name="localizationIds">Every <c>LocalizationData.ID</c>.</param>
        /// <param name="novelIds">Every <c>NovelData.ID</c>, including repeats.</param>
        /// <param name="scenarioGroupIds">Every <c>ScenarioGroupData.ID</c>.</param>
        public static MasterDataBaseline Build(
            IEnumerable<string?>? localizationIds,
            IEnumerable<string?>? novelIds,
            IEnumerable<string?>? scenarioGroupIds)
        {
            var localization = new HashSet<string>(StringComparer.Ordinal);
            var novel = new Dictionary<string, int>(StringComparer.Ordinal);
            var groups = new HashSet<string>(StringComparer.Ordinal);

            if (localizationIds != null)
            {
                foreach (var id in localizationIds)
                {
                    if (!string.IsNullOrEmpty(id))
                    {
                        localization.Add(id!);
                    }
                }
            }

            if (novelIds != null)
            {
                foreach (var id in novelIds)
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    novel[id!] = novel.TryGetValue(id!, out var seen) ? seen + 1 : 1;
                }
            }

            if (scenarioGroupIds != null)
            {
                foreach (var id in scenarioGroupIds)
                {
                    if (!string.IsNullOrEmpty(id))
                    {
                        groups.Add(id!);
                    }
                }
            }

            return new MasterDataBaseline(localization, novel, groups);
        }

        /// <summary>True when the game already has this localization id.</summary>
        /// <param name="id">A localization id.</param>
        public bool HasLocalization(string? id) => id != null && _localizationIds.Contains(id);

        /// <summary>True when the game already has this scenario-group id.</summary>
        /// <param name="id">A scenario-group id.</param>
        public bool HasScenarioGroup(string? id) => id != null && _scenarioGroupIds.Contains(id);

        /// <summary>How many of the game's novel rows carry this id: 0, 1, or more.</summary>
        /// <param name="id">A novel row id.</param>
        public int NovelRowsWithId(string? id) =>
            id != null && _novelIdCounts.TryGetValue(id, out var count) ? count : 0;

        /// <summary><c>n localization, m novel, k group ids</c>.</summary>
        public override string ToString() =>
            LocalizationCount + " localization, " + NovelCount + " novel, " + ScenarioGroupCount + " group ids";
    }
}
