using CwyLs.ModLoader.Core.MasterData;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P2-1: reading a pack's <c>data/*.json</c>. The rule under test everywhere here is that a bad
    /// row is named and dropped while the rest of the file still loads.
    /// </summary>
    public class MasterDataParserTests
    {
        private const string File = "cwy.example/data/localization.json";
        private const string NovelFile = "cwy.example/data/novel.json";
        private const string GroupFile = "cwy.example/data/scenario_groups.json";

        [Fact]
        public void Reads_a_localization_row_with_every_column()
        {
            var parsed = MasterDataPackParser.ParseLocalization(
                """
                [
                  {
                    "ID": "cwy_example_01_001",
                    "Ja": "こんにちは",
                    "En": "Hello",
                    "ZhHans": "a", "ZhHant": "b", "Pt": "c", "Ko": "d", "Ru": "e",
                    "Voice": "Voice_CWY_Test"
                  }
                ]
                """,
                File);

            Assert.True(parsed.Clean);
            var row = Assert.Single(parsed.Rows);
            Assert.Equal("cwy_example_01_001", row.Id);
            Assert.Equal("こんにちは", row.Ja);
            Assert.Equal("Hello", row.En);
            Assert.Equal("Voice_CWY_Test", row.Voice);
        }

        [Fact]
        public void Matches_member_names_case_insensitively()
        {
            var parsed = MasterDataPackParser.ParseNovel(
                """[{"id": "r1", "scenarioGroupId": "g1", "command": "VoiceText"}]""",
                NovelFile);

            Assert.True(parsed.Clean);
            var row = Assert.Single(parsed.Rows);
            Assert.Equal("r1", row.Id);
            Assert.Equal("g1", row.ScenarioGroupId);
            Assert.Equal(20, row.Command);
        }

        [Fact]
        public void Applies_the_games_neutral_defaults_to_a_minimal_novel_row()
        {
            var parsed = MasterDataPackParser.ParseNovel(
                """[{"ID": "r1", "ScenarioGroupID": "g1", "Command": 12}]""", NovelFile);

            var row = Assert.Single(parsed.Rows);
            Assert.Equal("0", row.Arg1);
            Assert.Equal("0", row.Arg3);
            Assert.Equal("0", row.Arg4);
            Assert.Equal("0", row.LocalizationId);
            Assert.Equal(0, row.Talker);
            Assert.Equal(-1, row.BodyMotion);
            Assert.Equal(-1, row.FacialMotion);
            Assert.Equal(1f, row.LookSpeedSeconds);
            Assert.Equal(0, row.LookEaseType);
        }

        [Fact]
        public void Rejects_a_row_with_no_id_and_says_which_row()
        {
            var parsed = MasterDataPackParser.ParseLocalization(
                """[{"En": "first"}, {"ID": "ok", "En": "second"}]""", File);

            var rejection = Assert.Single(parsed.Rejected);
            Assert.Equal(0, rejection.Index);
            Assert.Equal("ID", rejection.Member);
            Assert.Equal(File + "[0].ID: is required", rejection.ToString());

            // The good row still loads.
            Assert.Equal("ok", Assert.Single(parsed.Rows).Id);
        }

        [Fact]
        public void Rejects_an_unknown_member_but_ignores_an_x_prefixed_one()
        {
            var parsed = MasterDataPackParser.ParseLocalization(
                """
                [
                  {"ID": "a", "Eng": "typo"},
                  {"ID": "b", "En": "fine", "x-note": "a comment for the translator"}
                ]
                """,
                File);

            var rejection = Assert.Single(parsed.Rejected);
            Assert.Equal("Eng", rejection.Member);
            Assert.Contains("unknown member", rejection.Reason);

            Assert.Equal("b", Assert.Single(parsed.Rows).Id);
        }

        [Fact]
        public void Rejects_a_blank_id_and_a_non_string_id()
        {
            var blank = MasterDataPackParser.ParseLocalization("""[{"ID": "   "}]""", File);
            Assert.Empty(blank.Rows);
            Assert.Contains("cannot be blank", Assert.Single(blank.Rejected).Reason);

            var wrongType = MasterDataPackParser.ParseLocalization("""[{"ID": 7}]""", File);
            Assert.Empty(wrongType.Rows);
            Assert.Contains("must be a string", Assert.Single(wrongType.Rejected).Reason);
        }

        [Fact]
        public void Rejects_a_number_where_the_game_stores_text()
        {
            var parsed = MasterDataPackParser.ParseNovel(
                """[{"ID": "r1", "ScenarioGroupID": "g1", "Command": "VoiceText", "Arg1": 1}]""",
                NovelFile);

            Assert.Empty(parsed.Rows);
            var rejection = Assert.Single(parsed.Rejected);
            Assert.Equal("Arg1", rejection.Member);
            Assert.Contains("quote the value", rejection.Reason);
        }

        [Fact]
        public void Rejects_an_unknown_enum_name_and_an_undeclared_number()
        {
            var byName = MasterDataPackParser.ParseNovel(
                """[{"ID": "r1", "ScenarioGroupID": "g1", "Command": "SingAria"}]""", NovelFile);
            Assert.Empty(byName.Rows);
            Assert.Contains("is not a member of Bulbul.MasterData.CommandType", Assert.Single(byName.Rejected).Reason);

            var byNumber = MasterDataPackParser.ParseNovel(
                """[{"ID": "r1", "ScenarioGroupID": "g1", "Command": 9999}]""", NovelFile);
            Assert.Empty(byNumber.Rows);
            Assert.Contains("is not a value of", Assert.Single(byNumber.Rejected).Reason);
        }

        [Fact]
        public void Rejects_a_document_that_is_not_an_array_of_objects()
        {
            var notArray = MasterDataPackParser.ParseLocalization("""{"ID": "a"}""", File);
            Assert.Empty(notArray.Rows);
            Assert.Equal(MasterDataRejection.WholeFile, Assert.Single(notArray.Rejected).Index);
            Assert.Contains("must be an array of rows", notArray.Rejected[0].Reason);

            var notObject = MasterDataPackParser.ParseLocalization("""["a", {"ID": "b"}]""", File);
            Assert.Single(notObject.Rows);
            Assert.Contains("a row must be an object", Assert.Single(notObject.Rejected).Reason);
        }

        [Fact]
        public void Reports_malformed_json_with_its_position_and_an_empty_file_by_name()
        {
            var malformed = MasterDataPackParser.ParseNovel("""[{"ID": "r1",}]""", NovelFile);
            Assert.Empty(malformed.Rows);
            Assert.StartsWith("1:", Assert.Single(malformed.Rejected).Reason);

            var empty = MasterDataPackParser.ParseNovel("   ", NovelFile);
            Assert.Equal("cwy.example/data/novel.json: the file is empty", Assert.Single(empty.Rejected).ToString());
        }

        [Fact]
        public void Rejects_two_members_that_differ_only_in_case()
        {
            var parsed = MasterDataPackParser.ParseLocalization("""[{"ID": "a", "id": "b"}]""", File);

            Assert.Empty(parsed.Rows);
            Assert.Contains("differ only in case", Assert.Single(parsed.Rejected).Reason);
        }

        [Fact]
        public void Reads_a_scenario_group_by_enum_name_and_by_number()
        {
            var parsed = MasterDataPackParser.ParseScenarioGroups(
                """
                [
                  {"ID": "g1", "Scenario": "MotionScenario", "EpisodeNumber": 1},
                  {"ID": "g2", "Scenario": 22}
                ]
                """,
                GroupFile);

            Assert.True(parsed.Clean);
            Assert.Equal(2, parsed.Rows.Count);
            Assert.Equal(22, parsed.Rows[0].Scenario);
            Assert.Equal(22, parsed.Rows[1].Scenario);
            Assert.Equal(1f, parsed.Rows[0].EpisodeNumber);

            // Defaults the game's own short groups carry.
            Assert.Equal("0", parsed.Rows[1].TitleLocalizationId);
            Assert.Equal("None", parsed.Rows[1].PlayTiming);
            Assert.Equal("None", parsed.Rows[1].ContentType);
        }

        [Fact]
        public void Requires_the_scenario_type_of_a_group()
        {
            var parsed = MasterDataPackParser.ParseScenarioGroups("""[{"ID": "g1"}]""", GroupFile);

            Assert.Empty(parsed.Rows);
            var rejection = Assert.Single(parsed.Rejected);
            Assert.Equal("Scenario", rejection.Member);
            Assert.Contains("is required", rejection.Reason);
        }
    }
}
