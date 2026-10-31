using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Builders for pack-line test rows, in the shapes the game's own rows have
    /// (game 1.17.3).
    /// </summary>
    internal static class PackLineRows
    {
        internal const int Text = 0;
        internal const int WaitInput = 2;
        internal const int Selection = 3;
        internal const int Jump = 4;
        internal const int EndScenario = 12;
        internal const int VoiceText = 20;
        internal const int PomodoroVoiceText = 22;

        internal static ScenarioGroupRow Group(
            string id, int scenario, float episode, string[]? languages = null, string pool = "", double? weight = null) =>
            new ScenarioGroupRow { Id = id, Scenario = scenario, EpisodeNumber = episode, Languages = languages, Pool = pool, Weight = weight };

        internal static NovelRow Spoken(string id, string group, int command = Text, string localization = "", int bodyMotion = -1) =>
            new NovelRow
            {
                Id = id,
                ScenarioGroupId = group,
                Command = command,
                Arg1 = command == Text ? "0" : "1",
                Arg2 = command == Text ? 1f : 0f,
                LocalizationId = localization.Length == 0 ? id : localization,
                BodyMotion = bodyMotion,
                Talker = 1
            };

        internal static LocalizationRow Line(string id, string voice = "") =>
            new LocalizationRow { Id = id, En = "text of " + id, Voice = voice };

        /// <summary>A click line: one Text row, one localization row with a voice.</summary>
        internal static (ScenarioGroupRow, NovelRow, LocalizationRow) Click(
            string id, float episode = 1001, int scenario = 9, string[]? languages = null) =>
            (Group(id, scenario, episode, languages ?? new[] { "th" }), Spoken(id + "_001", id), Line(id + "_001", "Voice_" + id));

        internal static MasterDataSet Set(
            IEnumerable<ScenarioGroupRow> groups,
            IEnumerable<NovelRow>? novel = null,
            IEnumerable<LocalizationRow>? localization = null) =>
            new MasterDataSet(localization?.ToList(), novel?.ToList(), groups.ToList());

        internal static MasterDataSet Set(params (ScenarioGroupRow Group, NovelRow Row, LocalizationRow Line)[] lines) =>
            Set(lines.Select(l => l.Group), lines.Select(l => l.Row), lines.Select(l => l.Line));
    }

    /// <summary>
    /// Pack lines, requirements 1 and 5: which language-exclusive groups reach the game, and the one
    /// reason a malformed one is refused.
    /// </summary>
    public class PackLinesScreenTests
    {
        private static readonly MasterDataBaseline Game =
            MasterDataBaseline.Build(new[] { "clickheroine_work_001", "main_01" }, new[] { "clickheroine_work_001" }, new[] { "clickheroine_work_001", "main_01" });

        [Fact]
        public void A_set_without_exclusive_groups_comes_back_as_the_same_instance()
        {
            var set = PackLineRows.Set(new[] { PackLineRows.Group("cwy_example_01", 22, 900) });

            var admitted = PackLineScreen.Screen(set, Game, admit: true);
            var held = PackLineScreen.Screen(set, Game, admit: false);

            Assert.Same(set, admitted.Set);
            Assert.Same(set, held.Set);
            Assert.Empty(admitted.Lines);
            Assert.Empty(admitted.Rejected);
            Assert.Equal(0, held.HeldBack);
        }

        [Fact]
        public void While_pack_lines_are_not_active_every_exclusive_group_is_held_back()
        {
            var ordinary = PackLineRows.Group("cwy_example_01", 22, 900);
            var (group, row, line) = PackLineRows.Click("cwy_th_click_work_001");
            var set = PackLineRows.Set(new[] { ordinary, group }, new[] { row }, new[] { line });

            var result = PackLineScreen.Screen(set, Game, admit: false);

            Assert.Equal(1, result.HeldBack);
            Assert.False(result.Admitted);
            Assert.Empty(result.Lines);
            Assert.Empty(result.Rejected);
            Assert.Same(ordinary, Assert.Single(result.Set.ScenarioGroups));
            Assert.Same(row, Assert.Single(result.Set.Novel));
            Assert.Same(line, Assert.Single(result.Set.Localization));
        }

        [Fact]
        public void A_well_formed_click_line_is_accepted_with_its_pool_episode_languages_and_voice()
        {
            var set = PackLineRows.Set(PackLineRows.Click("cwy_th_click_work_001", 1001, 9, new[] { " TH ", "tha" }));

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Empty(result.Rejected);
            var line = Assert.Single(result.Lines);
            Assert.Equal("cwy_th_click_work_001", line.GroupId);
            Assert.Equal("click.work", line.Pool.Id);
            Assert.Equal(1001, line.Episode);
            Assert.Equal(new[] { "th" }, line.Languages);
            Assert.Equal(new[] { "Voice_cwy_th_click_work_001" }, line.Voices);
            Assert.Single(result.Set.ScenarioGroups);
        }

        [Fact]
        public void A_clock_line_names_its_slot_and_a_missing_slot_is_refused()
        {
            var clock = PackLineRows.Group("cwy_th_time_7", 2050, 1001, new[] { "th" }, "selftalk.time.7");
            var noSlot = PackLineRows.Group("cwy_th_time_x", 2050, 1002, new[] { "th" });
            var set = PackLineRows.Set(
                new[] { clock, noSlot },
                new[] { PackLineRows.Spoken("cwy_th_time_7_001", "cwy_th_time_7"), PackLineRows.Spoken("cwy_th_time_x_001", "cwy_th_time_x") },
                new[] { PackLineRows.Line("cwy_th_time_7_001"), PackLineRows.Line("cwy_th_time_x_001") });

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Equal("selftalk.time.7", Assert.Single(result.Lines).Pool.Id);
            var rejection = Assert.Single(result.Rejected);
            Assert.Equal("cwy_th_time_x", rejection.GroupId);
            Assert.Contains("\"Pool\" is required", rejection.Reason);
            Assert.Contains("selftalk.time.24", rejection.Reason);
            Assert.DoesNotContain(result.Set.ScenarioGroups, g => g.Id == "cwy_th_time_x");
        }

        [Theory]
        [InlineData(22, "", "is not a pool pack lines can join")]
        [InlineData(9, "click.break", "draws HeroineClickBreak groups, not HeroineClickWork")]
        [InlineData(9, "click.nothing", "is not a pack-lines pool")]
        public void A_scenario_or_pool_that_does_not_fit_is_refused(int scenario, string pool, string expected)
        {
            var group = PackLineRows.Group("cwy_th_x", scenario, 1001, new[] { "th" }, pool);
            var set = PackLineRows.Set(new[] { group }, new[] { PackLineRows.Spoken("cwy_th_x_001", "cwy_th_x") }, new[] { PackLineRows.Line("cwy_th_x_001") });

            var rejection = Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected);

            Assert.Contains(expected, rejection.Reason);
        }

        [Theory]
        [InlineData(999f, "between 1000 and")]
        [InlineData(1001.5f, "whole number")]
        [InlineData(20000000f, "between 1000 and")]
        public void An_episode_outside_the_pack_range_is_refused(float episode, string expected)
        {
            var set = PackLineRows.Set(PackLineRows.Click("cwy_th_click_work_001", episode));

            var rejection = Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected);

            Assert.Contains(expected, rejection.Reason);
        }

        [Fact]
        public void A_second_line_on_the_same_episode_is_refused_and_the_first_kept()
        {
            var set = PackLineRows.Set(PackLineRows.Click("cwy_th_a", 1001), PackLineRows.Click("cwy_th_b", 1001));

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Equal("cwy_th_a", Assert.Single(result.Lines).GroupId);
            var rejection = Assert.Single(result.Rejected);
            Assert.Equal("cwy_th_b", rejection.GroupId);
            Assert.Contains("already used by group 'cwy_th_a'", rejection.Reason);
        }

        [Fact]
        public void An_episode_an_ordinary_pack_group_plays_under_is_refused()
        {
            var ordinary = PackLineRows.Group("cwy_other_click", 9, 1001);
            var (group, row, line) = PackLineRows.Click("cwy_th_click", 1001);
            var set = PackLineRows.Set(new[] { ordinary, group }, new[] { row }, new[] { line });

            var rejection = Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected);

            Assert.Contains("cwy_other_click", rejection.Reason);
        }

        [Fact]
        public void An_override_of_a_game_group_is_refused()
        {
            var set = PackLineRows.Set(PackLineRows.Click("clickheroine_work_001", 1001));

            var rejection = Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected);

            Assert.Contains("overrides the game's own group", rejection.Reason);
        }

        [Fact]
        public void A_group_without_rows_is_refused_but_its_other_rows_stay_merged()
        {
            var group = PackLineRows.Group("cwy_th_empty", 9, 1001, new[] { "th" });
            var stray = PackLineRows.Line("cwy_th_empty_001");
            var set = PackLineRows.Set(new[] { group }, null, new[] { stray });

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Contains("has no novel rows", Assert.Single(result.Rejected).Reason);
            Assert.Empty(result.Set.ScenarioGroups);
            Assert.Same(stray, Assert.Single(result.Set.Localization));
        }

        [Fact]
        public void A_row_naming_a_missing_localization_is_refused_unless_the_game_has_it()
        {
            var missing = PackLineRows.Group("cwy_th_a", 9, 1001, new[] { "th" });
            var reused = PackLineRows.Group("cwy_th_b", 9, 1002, new[] { "th" });
            var set = PackLineRows.Set(
                new[] { missing, reused },
                new[]
                {
                    PackLineRows.Spoken("cwy_th_a_001", "cwy_th_a", localization: "nobody_has_this"),
                    PackLineRows.Spoken("cwy_th_b_001", "cwy_th_b", localization: "clickheroine_work_001"),
                });

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Equal("cwy_th_b", Assert.Single(result.Lines).GroupId);
            Assert.Empty(result.Lines[0].Voices);
            Assert.Contains("'nobody_has_this'", Assert.Single(result.Rejected).Reason);
        }

        [Theory]
        [InlineData(PackLineRows.Text, "x", "0", "Text row's Arg1")]
        [InlineData(PackLineRows.Text, "1,5", "0", "Text row's Arg1")]
        [InlineData(PackLineRows.Text, "0", "zero", "Text row's Arg3")]
        [InlineData(PackLineRows.VoiceText, "yes", "0", "VoiceText row's Arg1")]
        [InlineData(PackLineRows.PomodoroVoiceText, "1.0", "0", "PomodoroVoiceText row's Arg1 and Arg3")]
        public void An_argument_the_game_parses_must_parse(int command, string arg1, string arg3, string expected)
        {
            var group = PackLineRows.Group("cwy_th_a", 9, 1001, new[] { "th" });
            var row = new NovelRow
            {
                Id = "cwy_th_a_001", ScenarioGroupId = "cwy_th_a", Command = command, Arg1 = arg1, Arg3 = arg3,
                LocalizationId = "cwy_th_a_001", BodyMotion = 55
            };
            var set = PackLineRows.Set(new[] { group }, new[] { row }, new[] { PackLineRows.Line("cwy_th_a_001") });

            Assert.Contains(expected, Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected).Reason);
        }

        [Fact]
        public void Text_arguments_in_the_games_own_shapes_are_accepted()
        {
            var group = PackLineRows.Group("cwy_th_a", 7, 1001, new[] { "th" }, "pomodoro.end");
            var rows = new[]
            {
                new NovelRow { Id = "r1", ScenarioGroupId = "cwy_th_a", Command = PackLineRows.Text, Arg1 = "1.5", Arg3 = "0", LocalizationId = "l1" },
                new NovelRow { Id = "r2", ScenarioGroupId = "cwy_th_a", Command = PackLineRows.Text, Arg1 = "", Arg3 = " 0 ", LocalizationId = "l1" },
                new NovelRow { Id = "end", ScenarioGroupId = "cwy_th_a", Command = PackLineRows.EndScenario, Arg2 = 1.25f },
            };
            var set = PackLineRows.Set(new[] { group }, rows, new[] { PackLineRows.Line("l1") });

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Empty(result.Rejected);
            Assert.Single(result.Lines);
        }

        [Fact]
        public void A_click_style_timer_voice_row_needs_a_body_motion_but_a_timer_line_does_not()
        {
            var click = PackLineRows.Group("cwy_th_click", 9, 1001, new[] { "th" });
            var timer = PackLineRows.Group("cwy_th_timer", 4, 1001, new[] { "th" });
            var set = PackLineRows.Set(
                new[] { click, timer },
                new[]
                {
                    PackLineRows.Spoken("cwy_th_click_001", "cwy_th_click", PackLineRows.PomodoroVoiceText),
                    PackLineRows.Spoken("cwy_th_timer_001", "cwy_th_timer", PackLineRows.PomodoroVoiceText),
                },
                new[] { PackLineRows.Line("cwy_th_click_001"), PackLineRows.Line("cwy_th_timer_001") });

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Equal("pomodoro.start", Assert.Single(result.Lines).Pool.Id);
            var rejection = Assert.Single(result.Rejected);
            Assert.Equal("cwy_th_click", rejection.GroupId);
            Assert.Contains("needs a BodyMotion", rejection.Reason);
        }

        [Fact]
        public void A_timer_line_needs_a_pomodoro_voice_text_row()
        {
            var set = PackLineRows.Set(PackLineRows.Click("cwy_th_timer", 1001, 4));

            Assert.Contains("needs a PomodoroVoiceText row", Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected).Reason);
        }

        [Fact]
        public void A_command_that_waits_for_input_is_refused()
        {
            var group = PackLineRows.Group("cwy_th_a", 9, 1001, new[] { "th" });
            var rows = new[]
            {
                PackLineRows.Spoken("cwy_th_a_001", "cwy_th_a"),
                new NovelRow { Id = "wait", ScenarioGroupId = "cwy_th_a", Command = PackLineRows.WaitInput },
            };
            var set = PackLineRows.Set(new[] { group }, rows, new[] { PackLineRows.Line("cwy_th_a_001") });

            Assert.Contains("stalls a short talk", Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected).Reason);
        }

        [Fact]
        public void A_line_with_no_spoken_row_is_refused()
        {
            var group = PackLineRows.Group("cwy_th_a", 9, 1001, new[] { "th" });
            var rows = new[] { new NovelRow { Id = "end", ScenarioGroupId = "cwy_th_a", Command = PackLineRows.EndScenario } };
            var set = PackLineRows.Set(new[] { group }, rows);

            Assert.Contains("has no spoken row", Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected).Reason);
        }

        [Fact]
        public void A_selection_must_jump_inside_its_group()
        {
            var group = PackLineRows.Group("cwy_th_end", 7, 1001, new[] { "th" });
            var rows = new[]
            {
                PackLineRows.Spoken("cwy_th_end_001", "cwy_th_end"),
                new NovelRow { Id = "pick", ScenarioGroupId = "cwy_th_end", Command = PackLineRows.Selection, Arg1 = "pomodoroend_01_003_reaction_1", LocalizationId = "cwy_th_end_001" },
            };
            var set = PackLineRows.Set(new[] { group }, rows, new[] { PackLineRows.Line("cwy_th_end_001") });

            Assert.Contains("is not a row of this group", Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected).Reason);
        }

        [Fact]
        public void A_voice_no_clip_can_be_named_is_refused()
        {
            var (group, row, line) = PackLineRows.Click("cwy_th_a");
            var bad = new LocalizationRow { Id = line.Id, En = "x", Voice = "Voice TH 001" };
            var set = PackLineRows.Set(new[] { group }, new[] { row }, new[] { bad });

            Assert.Contains("no clip can be named", Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected).Reason);
        }

        [Theory]
        [InlineData("None", "None", "25", "5", "0", "main_01", "PlayTiming \"None\" never plays")]
        [InlineData("break", "None", "25", "5", "0", "main_01", "PlayTiming \"break\"")]
        [InlineData("Break", "Cicada", "25", "5", "0", "main_01", "ContentType \"Cicada\"")]
        [InlineData("Break", "None", "abc", "5", "0", "main_01", "whole numbers 0-100")]
        [InlineData("Break", "None", "25", "150", "0", "main_01", "whole numbers 0-100")]
        [InlineData("Break", "SmallTalk_Cicada", "25", "5", "ten", "main_01", "hours threshold")]
        [InlineData("Break", "None", "25", "5", "0", "main_99", "names no scenario group")]
        public void A_small_talk_needs_the_columns_the_lottery_parses(
            string timing, string content, string arg1, string arg2, string arg3, string unlock, string expected)
        {
            var group = new ScenarioGroupRow
            {
                Id = "cwy_th_small", Scenario = 23, EpisodeNumber = 1001, Languages = new[] { "th" },
                PlayTiming = timing, ContentType = content, Arg1 = arg1, Arg2 = arg2, Arg3 = arg3, UnlockStoryEpisode = unlock
            };
            var set = PackLineRows.Set(new[] { group }, new[] { PackLineRows.Spoken("s1", "cwy_th_small") }, new[] { PackLineRows.Line("s1") });

            Assert.Contains(expected, Assert.Single(PackLineScreen.Screen(set, Game, admit: true).Rejected).Reason);
        }

        [Fact]
        public void A_small_talk_in_the_games_own_shape_is_accepted()
        {
            var group = new ScenarioGroupRow
            {
                Id = "cwy_th_small", Scenario = 23, EpisodeNumber = 1001, Languages = new[] { "th" },
                PlayTiming = "Break", ContentType = "None", Arg1 = "25", Arg2 = "5", Arg3 = "0", UnlockStoryEpisode = "main_01"
            };
            var set = PackLineRows.Set(new[] { group }, new[] { PackLineRows.Spoken("s1", "cwy_th_small") }, new[] { PackLineRows.Line("s1", "Voice_S1") });

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Empty(result.Rejected);
            Assert.Equal("smalltalk", Assert.Single(result.Lines).Pool.Id);
        }

        [Fact]
        public void Each_rejection_names_the_row_in_one_warn_line()
        {
            var rejection = new PackLineRejection("cwy_th_a", "has no novel rows");

            Assert.Equal("PACKLINES skipped scenario_groups row 'cwy_th_a': has no novel rows", rejection.ToString());
        }
    }
}
