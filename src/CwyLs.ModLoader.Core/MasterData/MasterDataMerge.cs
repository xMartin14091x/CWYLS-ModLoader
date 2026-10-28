using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// Merges every installed pack's master-data files into one set, in pack order.
    /// <para>The rules, in full:</para>
    /// <list type="bullet">
    ///   <item><description>
    ///     Packs are applied in the order they are given, which is <c>PackManager</c>'s dependency
    ///     order. A later pack's row replaces an earlier pack's row with the same id, and that is
    ///     counted as a <see cref="MasterDataFileReport.PackOverrides"/> - not an error.
    ///   </description></item>
    ///   <item><description>
    ///     A row whose id the game already has <em>overrides</em> it; a row with a new id is
    ///     <em>added</em>. A row replaces the game's row whole: a localization override that only
    ///     sets <c>En</c> leaves the other languages empty, it does not merge column by column.
    ///   </description></item>
    ///   <item><description>
    ///     A novel row whose id names more than one row in the game's own data is refused, because
    ///     there is no way to tell which one was meant (see <see cref="MasterDataBaseline"/>).
    ///   </description></item>
    ///   <item><description>
    ///     Parse rejections are carried through untouched, so one bad row never costs a pack its
    ///     good ones.
    ///   </description></item>
    /// </list>
    /// </summary>
    public static class MasterDataMerge
    {
        /// <summary>Kind label of the localization file, as it appears in the log line.</summary>
        public const string LocalizationKind = "localization";

        /// <summary>Kind label of the novel file.</summary>
        public const string NovelKind = "novel";

        /// <summary>Kind label of the scenario-group file.</summary>
        public const string ScenarioGroupsKind = "scenario_groups";

        /// <summary>Kind label of the subtitle tables.</summary>
        public const string SubtitlesKind = "subtitles";

        /// <summary>Merges the packs' files against what the game already has.</summary>
        /// <param name="packs">
        /// The packs' files, in load order; null entries are skipped and null itself is an empty
        /// merge.
        /// </param>
        /// <param name="baseline">
        /// The game's own ids, so a row can be classified as added or overridden. Null means
        /// <see cref="MasterDataBaseline.Empty"/>, i.e. everything counts as added - which is what
        /// a unit test wants and what the adapter uses before the game has loaded.
        /// </param>
        public static MasterDataSet Merge(
            IEnumerable<MasterDataPackInput?>? packs, MasterDataBaseline? baseline = null)
        {
            var known = baseline ?? MasterDataBaseline.Empty;

            var localization = new Merged<LocalizationRow>(LocalizationKind);
            var novel = new Merged<NovelRow>(NovelKind);
            var groups = new Merged<ScenarioGroupRow>(ScenarioGroupsKind);

            var subtitleEntries = new List<SubtitleEntry>();
            var subtitleRejections = new List<MasterDataRejection>();
            var subtitleReplacements = 0;
            var subtitleSeen = new HashSet<string>(StringComparer.Ordinal);

            if (packs != null)
            {
                foreach (var pack in packs)
                {
                    if (pack is null || pack.IsEmpty)
                    {
                        continue;
                    }

                    if (pack.LocalizationJson != null)
                    {
                        var parsed = MasterDataPackParser.ParseLocalization(
                            pack.LocalizationJson, Where(pack, MasterDataPackParser.LocalizationFile));

                        localization.Absorb(parsed, row => row.Id, id => known.HasLocalization(id) ? 1 : 0);
                    }

                    if (pack.NovelJson != null)
                    {
                        var parsed = MasterDataPackParser.ParseNovel(
                            pack.NovelJson, Where(pack, MasterDataPackParser.NovelFile));

                        novel.Absorb(parsed, row => row.Id, known.NovelRowsWithId);
                    }

                    if (pack.ScenarioGroupsJson != null)
                    {
                        var parsed = MasterDataPackParser.ParseScenarioGroups(
                            pack.ScenarioGroupsJson, Where(pack, MasterDataPackParser.ScenarioGroupsFile));

                        groups.Absorb(parsed, row => row.Id, id => known.HasScenarioGroup(id) ? 1 : 0);
                    }

                    foreach (var source in pack.Subtitles)
                    {
                        var file = Where(pack, source.RelativePath);
                        var language = SubtitleTables.LanguageOf(source.RelativePath);
                        var parsed = SubtitleTables.Parse(source.Json, file, language);

                        subtitleRejections.AddRange(parsed.Rejected);

                        foreach (var entry in parsed.Rows)
                        {
                            if (!subtitleSeen.Add(entry.Language + " " + entry.LocalizationId))
                            {
                                subtitleReplacements++;
                            }

                            subtitleEntries.Add(entry);
                        }
                    }
                }
            }

            var subtitles = SubtitleTables.Build(subtitleEntries);
            var subtitleReport = new MasterDataFileReport(
                SubtitlesKind, subtitles.Count, 0, subtitleReplacements, subtitleRejections);

            var byLanguage = new KeyValuePair<string, int>[subtitles.Languages.Count];
            for (var i = 0; i < subtitles.Languages.Count; i++)
            {
                var code = subtitles.Languages[i];
                byLanguage[i] = new KeyValuePair<string, int>(code, subtitles.CountFor(code));
            }

            var report = new MasterDataMergeReport(
                localization.ToReport(),
                novel.ToReport(),
                groups.ToReport(),
                subtitleReport,
                byLanguage);

            return new MasterDataSet(localization.Rows, novel.Rows, groups.Rows, subtitles, report);
        }

        private static string Where(MasterDataPackInput pack, string relativePath) =>
            pack.PackId + "/" + relativePath;

        /// <summary>
        /// One kind of row, accumulated across packs: first-seen order, last-write-wins by id, and
        /// the counters the report needs.
        /// </summary>
        /// <typeparam name="T">The row type.</typeparam>
        private sealed class Merged<T>
            where T : class
        {
            private readonly string _kind;
            private readonly List<T> _rows = new List<T>();
            private readonly Dictionary<string, int> _positionById =
                new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<MasterDataRejection> _rejected = new List<MasterDataRejection>();

            private int _added;
            private int _overridden;
            private int _packOverrides;

            internal Merged(string kind)
            {
                _kind = kind;
            }

            internal IReadOnlyList<T> Rows => _rows;

            /// <summary>Folds one parsed file in.</summary>
            /// <param name="parsed">The file's rows and rejections.</param>
            /// <param name="idOf">Reads a row's id.</param>
            /// <param name="baselineRows">
            /// How many of the game's own rows carry that id: 0 = addition, 1 = override, more than
            /// 1 = ambiguous and refused.
            /// </param>
            internal void Absorb(MasterDataParse<T> parsed, Func<T, string> idOf, Func<string, int> baselineRows)
            {
                _rejected.AddRange(parsed.Rejected);

                for (var i = 0; i < parsed.Rows.Count; i++)
                {
                    var row = parsed.Rows[i];
                    var id = idOf(row);
                    var existing = baselineRows(id);

                    if (existing > 1)
                    {
                        _rejected.Add(new MasterDataRejection(
                            parsed.File,
                            i,
                            "ID",
                            "\"" + id + "\" names " + existing + " rows in the game's own data, so an "
                            + "override would be ambiguous; give the row a new id instead"));
                        continue;
                    }

                    if (_positionById.TryGetValue(id, out var position))
                    {
                        // An earlier pack already claimed this id; the later pack wins, in place, so
                        // row order does not depend on which pack happened to load first.
                        _rows[position] = row;
                        _packOverrides++;
                        continue;
                    }

                    _positionById.Add(id, _rows.Count);
                    _rows.Add(row);

                    if (existing == 1)
                    {
                        _overridden++;
                    }
                    else
                    {
                        _added++;
                    }
                }
            }

            internal MasterDataFileReport ToReport() =>
                new MasterDataFileReport(_kind, _added, _overridden, _packOverrides, _rejected);
        }
    }
}
