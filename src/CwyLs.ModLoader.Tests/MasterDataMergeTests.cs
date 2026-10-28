using CwyLs.ModLoader.Core.MasterData;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P2-1: merging several packs' master data against the rows the game already has.
    /// </summary>
    public class MasterDataMergeTests
    {
        private static MasterDataPackInput Pack(
            string id, string? localization = null, string? novel = null, string? groups = null)
        {
            return new MasterDataPackInput(id)
            {
                LocalizationJson = localization,
                NovelJson = novel,
                ScenarioGroupsJson = groups
            };
        }

        private static MasterDataBaseline Game(
            string[]? localization = null, string[]? novel = null, string[]? groups = null) =>
            MasterDataBaseline.Build(localization, novel, groups);

        [Fact]
        public void Counts_a_new_id_as_added_and_a_known_id_as_overridden()
        {
            var set = MasterDataMerge.Merge(
                new[]
                {
                    Pack(
                        "cwy.example",
                        localization: """
                                      [
                                        {"ID": "clickheroine_normal_001", "En": "mine"},
                                        {"ID": "cwy_new_001", "En": "brand new"}
                                      ]
                                      """)
                },
                Game(localization: new[] { "clickheroine_normal_001" }));

            Assert.Equal(1, set.Report.Localization.Added);
            Assert.Equal(1, set.Report.Localization.Overridden);
            Assert.Equal(0, set.Report.RejectedCount);
            Assert.Equal(2, set.Localization.Count);
        }

        [Fact]
        public void A_later_pack_replaces_an_earlier_packs_row_in_place()
        {
            var set = MasterDataMerge.Merge(
                new[]
                {
                    Pack("a", localization: """[{"ID": "x", "En": "from a"}, {"ID": "y", "En": "y"}]"""),
                    Pack("b", localization: """[{"ID": "x", "En": "from b"}]""")
                },
                MasterDataBaseline.Empty);

            Assert.Equal(2, set.Localization.Count);

            // Position is first-seen, so pack order cannot reshuffle the rows.
            Assert.Equal("x", set.Localization[0].Id);
            Assert.Equal("from b", set.Localization[0].En);
            Assert.Equal(2, set.Report.Localization.Added);
            Assert.Equal(1, set.Report.Localization.PackOverrides);
        }

        [Fact]
        public void Refuses_to_override_a_novel_id_the_game_uses_more_than_once()
        {
            var set = MasterDataMerge.Merge(
                new[]
                {
                    Pack(
                        "cwy.example",
                        novel: """
                               [
                                 {"ID": "tutorial_01_000_1", "ScenarioGroupID": "tutorial_01", "Command": "Text"},
                                 {"ID": "cwy_example_01_001", "ScenarioGroupID": "cwy_example_01", "Command": "VoiceText"}
                               ]
                               """)
                },
                Game(novel: new[] { "tutorial_01_000_1", "tutorial_01_000_1", "tutorial_01_000_1" }));

            Assert.Equal(1, set.Report.Novel.Added);
            Assert.Equal(0, set.Report.Novel.Overridden);

            var rejection = Assert.Single(set.Report.Novel.Rejected);
            Assert.Equal("ID", rejection.Member);
            Assert.Contains("names 3 rows in the game's own data", rejection.Reason);

            Assert.Equal("cwy_example_01_001", Assert.Single(set.Novel).Id);
        }

        [Fact]
        public void Carries_parse_rejections_through_without_losing_the_good_rows()
        {
            var set = MasterDataMerge.Merge(
                new[]
                {
                    Pack(
                        "cwy.example",
                        localization: """[{"En": "no id"}, {"ID": "good", "En": "kept"}]""",
                        groups: """[{"ID": "g1", "Scenario": "NotAThing"}]""")
                },
                MasterDataBaseline.Empty);

            Assert.Equal("good", Assert.Single(set.Localization).Id);
            Assert.Empty(set.ScenarioGroups);
            Assert.Equal(2, set.Report.RejectedCount);
            Assert.Equal(2, set.Report.AllRejections.Count);
        }

        [Fact]
        public void Merges_subtitle_tables_and_lets_a_later_pack_win()
        {
            var first = new MasterDataPackInput("a");
            first.AddSubtitles("data/subtitles/th.json", """{"line1": "หนึ่ง", "line2": "สอง"}""");

            var second = new MasterDataPackInput("b");
            second.AddSubtitles("data/subtitles/th.json", """{"line1": "แก้ไข"}""");
            second.AddSubtitles("data/subtitles/vi.json", """{"line1": "xin chao"}""");

            var set = MasterDataMerge.Merge(new[] { first, second }, MasterDataBaseline.Empty);

            Assert.True(set.Subtitles.TryGet("line1", "th", out var th));
            Assert.Equal("แก้ไข", th);
            Assert.Equal(2, set.Subtitles.CountFor("th"));
            Assert.Equal(1, set.Subtitles.CountFor("vi"));
            Assert.Equal(1, set.Report.Subtitles.PackOverrides);
            Assert.Equal(new[] { "th", "vi" }, set.Subtitles.Languages);
        }

        [Fact]
        public void Summarizes_in_the_form_the_startup_log_uses()
        {
            var pack = new MasterDataPackInput("cwy.example")
            {
                LocalizationJson = """[{"ID": "cwy_example_01_001", "En": "Hello"}]""",
                NovelJson = """
                            [
                              {"ID": "cwy_example_01_001", "ScenarioGroupID": "cwy_example_01", "Command": "VoiceText"},
                              {"ID": "cwy_example_01_end", "ScenarioGroupID": "cwy_example_01", "Command": "EndScenario"}
                            ]
                            """,
                ScenarioGroupsJson = """[{"ID": "cwy_example_01", "Scenario": "MotionScenario"}]"""
            };
            pack.AddSubtitles("data/subtitles/th.json", """{"cwy_example_01_001": "สวัสดี"}""");

            var set = MasterDataMerge.Merge(new[] { pack }, MasterDataBaseline.Empty);

            Assert.Equal(
                "MASTERDATA: localization +1/~0, novel +2/~0, scenario_groups +1/~0, "
                + "subtitles(th) 1 rows; rejected 0",
                set.Report.Summarize());
        }

        [Fact]
        public void An_empty_merge_is_empty_and_says_so()
        {
            var set = MasterDataMerge.Merge(null, null);

            Assert.True(set.IsEmpty);
            Assert.True(set.Report.IsEmpty);
            Assert.Equal(0, set.Report.RejectedCount);
            Assert.Equal(
                "MASTERDATA: localization +0/~0, novel +0/~0, scenario_groups +0/~0, "
                + "subtitles(-) 0 rows; rejected 0",
                set.Report.Summarize());
        }

        [Fact]
        public void Skips_a_pack_that_ships_no_data_files()
        {
            var set = MasterDataMerge.Merge(
                new[] { null, Pack("empty"), Pack("real", localization: """[{"ID": "x"}]""") },
                MasterDataBaseline.Empty);

            Assert.Equal("x", Assert.Single(set.Localization).Id);
        }

        [Fact]
        public void Baseline_counts_repeated_novel_ids()
        {
            var baseline = MasterDataBaseline.Build(
                new[] { "loc_a", "loc_b", null },
                new[] { "n1", "n1", "n2" },
                new[] { "g1" });

            Assert.Equal(2, baseline.LocalizationCount);
            Assert.Equal(2, baseline.NovelCount);
            Assert.Equal(1, baseline.ScenarioGroupCount);
            Assert.Equal(2, baseline.NovelRowsWithId("n1"));
            Assert.Equal(1, baseline.NovelRowsWithId("n2"));
            Assert.Equal(0, baseline.NovelRowsWithId("nope"));
            Assert.True(baseline.HasLocalization("loc_a"));
            Assert.True(baseline.HasScenarioGroup("g1"));
            Assert.False(baseline.HasScenarioGroup("g2"));
        }
    }
}
