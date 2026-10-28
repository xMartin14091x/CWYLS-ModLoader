using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// The whole merge, as a report: one <see cref="MasterDataFileReport"/> per kind plus the
    /// single line the plugin logs at startup.
    /// </summary>
    public sealed class MasterDataMergeReport
    {
        /// <summary>The prefix every master-data log line carries.</summary>
        public const string LogPrefix = "MASTERDATA";

        private readonly MasterDataFileReport[] _files;

        /// <summary>Creates a report.</summary>
        /// <param name="localization">The localization file's contribution. Required.</param>
        /// <param name="novel">The novel file's contribution. Required.</param>
        /// <param name="scenarioGroups">The scenario-group file's contribution. Required.</param>
        /// <param name="subtitles">The subtitle tables' contribution. Required.</param>
        /// <param name="subtitleRowsByLanguage">Rows per language code; null means none.</param>
        /// <exception cref="ArgumentNullException">A report is null.</exception>
        public MasterDataMergeReport(
            MasterDataFileReport localization,
            MasterDataFileReport novel,
            MasterDataFileReport scenarioGroups,
            MasterDataFileReport subtitles,
            IReadOnlyList<KeyValuePair<string, int>>? subtitleRowsByLanguage = null)
        {
            Localization = localization ?? throw new ArgumentNullException(nameof(localization));
            Novel = novel ?? throw new ArgumentNullException(nameof(novel));
            ScenarioGroups = scenarioGroups ?? throw new ArgumentNullException(nameof(scenarioGroups));
            Subtitles = subtitles ?? throw new ArgumentNullException(nameof(subtitles));
            SubtitleRowsByLanguage = subtitleRowsByLanguage ?? new KeyValuePair<string, int>[0];
            _files = new[] { Localization, Novel, ScenarioGroups, Subtitles };
        }

        /// <summary>An empty report: nothing added, nothing refused.</summary>
        public static MasterDataMergeReport Empty { get; } = new MasterDataMergeReport(
            new MasterDataFileReport("localization", 0, 0),
            new MasterDataFileReport("novel", 0, 0),
            new MasterDataFileReport("scenario_groups", 0, 0),
            new MasterDataFileReport("subtitles", 0, 0));

        /// <summary>What <c>data/localization.json</c> contributed.</summary>
        public MasterDataFileReport Localization { get; }

        /// <summary>What <c>data/novel.json</c> contributed.</summary>
        public MasterDataFileReport Novel { get; }

        /// <summary>What <c>data/scenario_groups.json</c> contributed.</summary>
        public MasterDataFileReport ScenarioGroups { get; }

        /// <summary>What <c>data/subtitles/*.json</c> contributed.</summary>
        public MasterDataFileReport Subtitles { get; }

        /// <summary>Subtitle rows per language code, ordinally sorted by code.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> SubtitleRowsByLanguage { get; }

        /// <summary>Every file report, in log order.</summary>
        public IReadOnlyList<MasterDataFileReport> Files => _files;

        /// <summary>How many rows were refused across every file.</summary>
        public int RejectedCount =>
            Localization.Rejected.Count + Novel.Rejected.Count
            + ScenarioGroups.Rejected.Count + Subtitles.Rejected.Count;

        /// <summary>Every rejection, in file order: localization, novel, scenario groups, subtitles.</summary>
        public IReadOnlyList<MasterDataRejection> AllRejections
        {
            get
            {
                var all = new List<MasterDataRejection>(RejectedCount);
                all.AddRange(Localization.Rejected);
                all.AddRange(Novel.Rejected);
                all.AddRange(ScenarioGroups.Rejected);
                all.AddRange(Subtitles.Rejected);
                return all;
            }
        }

        /// <summary>True when no pack contributed anything.</summary>
        public bool IsEmpty =>
            Localization.IsEmpty && Novel.IsEmpty && ScenarioGroups.IsEmpty && Subtitles.IsEmpty;

        /// <summary>
        /// The startup line, e.g.
        /// <c>MASTERDATA: localization +1/~0, novel +2/~0, scenario_groups +1/~0, subtitles(th) 1 rows; rejected 0</c>.
        /// </summary>
        public string Summarize()
        {
            var languages = new string[SubtitleRowsByLanguage.Count];
            for (var i = 0; i < SubtitleRowsByLanguage.Count; i++)
            {
                languages[i] = SubtitleRowsByLanguage[i].Key;
            }

            var subtitleLanguages = languages.Length == 0 ? "-" : string.Join(",", languages);

            return LogPrefix + ": " + Localization + ", " + Novel + ", " + ScenarioGroups
                   + ", subtitles(" + subtitleLanguages + ") " + Subtitles.Accepted + " rows"
                   + "; rejected " + RejectedCount;
        }

        /// <summary>The summary line.</summary>
        public override string ToString() => Summarize();
    }
}
