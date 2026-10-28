using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// Everything the installed packs want to add to or change in the game's master data, merged
    /// and ready to inject: rows in first-seen order, the subtitle lookup, and the report that
    /// explains how they got there.
    /// </summary>
    public sealed class MasterDataSet
    {
        private static readonly LocalizationRow[] NoLocalization = new LocalizationRow[0];
        private static readonly NovelRow[] NoNovel = new NovelRow[0];
        private static readonly ScenarioGroupRow[] NoGroups = new ScenarioGroupRow[0];

        /// <summary>Creates a set.</summary>
        /// <param name="localization">Localization rows, in merged order; null means none.</param>
        /// <param name="novel">Novel rows, in merged order; null means none.</param>
        /// <param name="scenarioGroups">Scenario-group rows, in merged order; null means none.</param>
        /// <param name="subtitles">The subtitle lookup; null means <see cref="SubtitleTables.Empty"/>.</param>
        /// <param name="report">How the merge went; null means <see cref="MasterDataMergeReport.Empty"/>.</param>
        public MasterDataSet(
            IReadOnlyList<LocalizationRow>? localization = null,
            IReadOnlyList<NovelRow>? novel = null,
            IReadOnlyList<ScenarioGroupRow>? scenarioGroups = null,
            SubtitleTables? subtitles = null,
            MasterDataMergeReport? report = null)
        {
            Localization = localization ?? NoLocalization;
            Novel = novel ?? NoNovel;
            ScenarioGroups = scenarioGroups ?? NoGroups;
            Subtitles = subtitles ?? SubtitleTables.Empty;
            Report = report ?? MasterDataMergeReport.Empty;
        }

        /// <summary>A set holding nothing.</summary>
        public static MasterDataSet Empty { get; } = new MasterDataSet();

        /// <summary>Localization rows to add or override, in merged order.</summary>
        public IReadOnlyList<LocalizationRow> Localization { get; }

        /// <summary>Novel rows to add or override, in merged order.</summary>
        public IReadOnlyList<NovelRow> Novel { get; }

        /// <summary>Scenario-group rows to add or override, in merged order.</summary>
        public IReadOnlyList<ScenarioGroupRow> ScenarioGroups { get; }

        /// <summary>Subtitle text for languages the game does not have.</summary>
        public SubtitleTables Subtitles { get; }

        /// <summary>How the merge went, including everything it refused.</summary>
        public MasterDataMergeReport Report { get; }

        /// <summary>True when there is nothing to inject.</summary>
        public bool IsEmpty =>
            Localization.Count == 0 && Novel.Count == 0 && ScenarioGroups.Count == 0 && Subtitles.Count == 0;

        /// <summary>The merge report's summary line.</summary>
        public override string ToString() => Report.Summarize();
    }
}
