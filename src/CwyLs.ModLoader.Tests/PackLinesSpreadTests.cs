using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Startup-greeting pools and spread lines: a <c>GameStart_*</c> line joins its
    /// pool, <c>"Pool": "gamestart.any"</c> copies a line into every startup pool and <c>"Pool": "*"</c>
    /// (a global line) into every pool a single line can fill, each copy screened for its pool.
    /// </summary>
    public class PackLinesSpreadTests
    {
        private const string Hello = "cwy_th_hello";

        private static readonly MasterDataBaseline Game =
            MasterDataBaseline.Build(new[] { "clickheroine_work_001", "main_01" }, new[] { "clickheroine_work_001" }, new[] { "clickheroine_work_001", "main_01" });

        private static PackLinePool Pool(string id)
        {
            Assert.True(PackLinePools.TryGet(id, out var pool), id);
            return pool!;
        }

        private static IReadOnlyList<PackLinePool> Targets(string spread)
        {
            Assert.True(PackLinePools.TryGetSpread(spread, out var targets), spread);
            return targets;
        }

        /// <summary>A spread group as a pack writes it: <c>"Scenario": "None"</c>, a spread <c>Pool</c>.</summary>
        private static ScenarioGroupRow Spread(string id, string pool, float episode = 5001, double? weight = null, int scenario = 0) =>
            new ScenarioGroupRow { Id = id, Scenario = scenario, EpisodeNumber = episode, Languages = new[] { "th" }, Pool = pool, Weight = weight };

        /// <summary>A spoken Text row with a face, a look and a body motion of its own.</summary>
        private static NovelRow Text(string id, string group, string localization, int body = -1) =>
            new NovelRow
            {
                Id = id, ScenarioGroupId = group, Command = PackLineRows.Text, Arg1 = "0", Arg2 = 1f, Arg3 = "0", Talker = 1,
                LocalizationId = localization, BodyMotion = body, FacialMotion = 7, LookScale = 0.5f, LookSpeedSeconds = 1.25f, LookEaseType = 3
            };

        private static PackLineScreenResult Screen(ScenarioGroupRow group, params NovelRow[] rows) =>
            Screen(new[] { group }, rows);

        /// <summary>Screens the groups with a localization row (and a voice) for every LocalizationID the rows name.</summary>
        private static PackLineScreenResult Screen(ScenarioGroupRow[] groups, NovelRow[] rows)
        {
            var localization = rows.Where(r => r.LocalizationId != "0").Select(r => r.LocalizationId).Distinct()
                                   .Select(id => PackLineRows.Line(id, "Voice_" + id));
            return PackLineScreen.Screen(PackLineRows.Set(groups, rows, localization), Game, admit: true);
        }

        // ---- Startup pools ----

        [Theory]
        [InlineData(13, "gamestart.sameday")]
        [InlineData(12, "gamestart.newday")]
        [InlineData(1400, "gamestart.newday.morning")]
        [InlineData(1402, "gamestart.newday.noon")]
        [InlineData(1404, "gamestart.newday.evening")]
        [InlineData(1406, "gamestart.newday.night")]
        [InlineData(14, "gamestart.days3to14")]
        [InlineData(15, "gamestart.days15to30")]
        [InlineData(16, "gamestart.days31plus")]
        [InlineData(17, "gamestart.login100")]
        public void A_startup_greeting_joins_the_pool_its_scenario_names(int scenario, string pool)
        {
            var result = Screen(PackLineRows.Group(Hello, scenario, 1001, new[] { "th" }), Text("r1", Hello, "l1"));

            Assert.Empty(result.Rejected);
            var line = Assert.Single(result.Lines);
            Assert.Equal(pool, line.Pool.Id);
            Assert.Equal(PackLineFamily.GameStart, line.Pool.Family);
            Assert.False(line.Pool.HasDeck);
            Assert.False(line.Pool.IsClickStyle);
        }

        [Fact]
        public void The_first_launchs_greeting_is_not_a_pool()
        {
            var rejection = Assert.Single(Screen(PackLineRows.Group(Hello, 11, 1001, new[] { "th" }), Text("r1", Hello, "l1")).Rejected);

            Assert.Contains("GameStart_First_CameraTouch is not a pool", rejection.Reason);
        }

        [Fact]
        public void A_startup_greeting_with_a_choice_is_refused_but_the_answer_choice_talk_keeps_its_choices()
        {
            NovelRow[] Rows(string group) => new[]
            {
                Text(group + "_q", group, "l_q"),
                new NovelRow { Id = group + "_pick", ScenarioGroupId = group, Command = PackLineRows.Selection, Arg1 = group + "_a", LocalizationId = "l_button" },
                Text(group + "_a", group, "l_a"),
            };

            var greeting = Screen(new[] { PackLineRows.Group(Hello, 13, 1001, new[] { "th" }) }, Rows(Hello));
            var talk = Screen(new[] { PackLineRows.Group("cwy_th_end", 7, 1001, new[] { "th" }) }, Rows("cwy_th_end"));

            var rejection = Assert.Single(greeting.Rejected);
            Assert.Equal("novel row 'cwy_th_hello_pick' is a Selection: a startup greeting reads in auto mode, so a choice would wait for the player; "
                         + "write the greeting as spoken rows only", rejection.Reason);
            Assert.Empty(talk.Rejected);
            Assert.Equal("pomodoro.end", Assert.Single(talk.Lines).Pool.Id);
        }

        [Fact]
        public void A_startup_greeting_with_a_text_loop_is_refused()
        {
            var loop = new NovelRow { Id = "loop", ScenarioGroupId = Hello, Command = 81, Arg1 = "0", LocalizationId = "0" };

            var rejection = Assert.Single(Screen(PackLineRows.Group(Hello, 13, 1001, new[] { "th" }), Text("r1", Hello, "l1"), loop).Rejected);

            Assert.StartsWith("novel row 'loop' starts a text loop: it shows answer buttons", rejection.Reason);
        }

        [Fact]
        public void A_spread_pool_without_languages_is_an_ordinary_group_and_gets_a_note()
        {
            var plain = new ScenarioGroupRow { Id = "cwy_plain", Scenario = 0, EpisodeNumber = 5002, Pool = "*" };

            var result = Screen(
                new[] { PackLineRows.Group(Hello, 13, 1001, new[] { "th" }), plain },
                new[] { Text("r1", Hello, "l1"), Text("p1", "cwy_plain", "l2") });

            Assert.Empty(result.Rejected);
            Assert.Equal(Hello, Assert.Single(result.Lines).GroupId);
            Assert.Contains(result.Set.ScenarioGroups, g => g.Id == "cwy_plain");
            Assert.Equal(
                "'cwy_plain' has \"Pool\": \"*\" but no \"Languages\", so it is an ordinary group and is never offered as a pack line; add \"Languages\" (e.g. [\"th\"])",
                Assert.Single(result.Notes));
        }

        [Fact]
        public void A_startup_greetings_pomodoro_voice_row_needs_a_body_motion_or_Arg2_minus_1_like_any_line_outside_the_timer()
        {
            NovelRow Voice(float arg2, int body) => new NovelRow
            {
                Id = "r1", ScenarioGroupId = Hello, Command = PackLineRows.PomodoroVoiceText, Arg1 = "1", Arg2 = arg2, Arg3 = "0",
                LocalizationId = "l1", BodyMotion = body
            };

            var group = PackLineRows.Group(Hello, 13, 1001, new[] { "th" });

            Assert.Contains("outside the timer", Assert.Single(Screen(group, Voice(0f, -1)).Rejected).Reason);
            Assert.Single(Screen(group, Voice(-1f, -1)).Lines);
            Assert.Single(Screen(group, Voice(0f, 55)).Lines);
        }

        // ---- gamestart.any ----

        [Fact]
        public void Gamestart_any_copies_the_line_into_every_startup_pool_and_leaves_the_written_group_out()
        {
            var written = Spread(Hello, "gamestart.any", weight: 0.5);
            var row = Text("cwy_th_hello_001", Hello, "l_hello");
            var set = PackLineRows.Set(new[] { written }, new[] { row }, new[] { PackLineRows.Line("l_hello", "Voice_Hello") });

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Empty(result.Rejected);
            Assert.Empty(result.Notes);
            var startup = PackLinePools.All.Where(p => p.Family == PackLineFamily.GameStart).ToArray();
            Assert.Equal(startup.Select(p => p.Id), Targets("gamestart.any").Select(p => p.Id));
            Assert.Equal(startup.Select(p => p.Id), result.Lines.Select(l => l.Pool.Id));

            foreach (var pool in startup)
            {
                var copyId = Hello + "@" + pool.Id;
                Assert.Equal(copyId, PackLineScreen.CopyId(Hello, pool.Id));

                var line = Assert.Single(result.Lines, l => l.Pool == pool);
                Assert.Equal(copyId, line.GroupId);
                Assert.Equal(5001, line.Episode);
                Assert.Equal(new[] { "th" }, line.Languages);
                Assert.Equal(0.5, line.Weight);
                Assert.Equal(new[] { "Voice_Hello" }, line.Voices);
                Assert.Equal(Hello, line.SourceGroupId);
                Assert.Equal(PackLinePools.AnyGameStart, line.Spread);
                Assert.True(line.IsCopy);
                Assert.False(line.IsGlobal);

                var group = Assert.Single(result.Set.ScenarioGroups, g => g.Id == copyId);
                Assert.Equal(pool.Scenario, group.Scenario);
                Assert.Equal(pool.Id, group.Pool);
                Assert.Equal(5001f, group.EpisodeNumber);
                Assert.Equal(0.5, group.Weight);
                Assert.Same(written.Languages, group.Languages);

                var copy = Assert.Single(result.Set.Novel, r => r.ScenarioGroupId == copyId);
                Assert.Equal("cwy_th_hello_001@" + pool.Id, copy.Id);
                Assert.Equal(row.Command, copy.Command);
                Assert.Equal(row.Arg1, copy.Arg1);
                Assert.Equal(row.Arg2, copy.Arg2);
                Assert.Equal(row.Arg3, copy.Arg3);
                Assert.Equal("l_hello", copy.LocalizationId);
                Assert.Equal(row.Talker, copy.Talker);
                Assert.Equal(row.FacialMotion, copy.FacialMotion);
                Assert.Equal(row.BodyMotion, copy.BodyMotion);
                Assert.Equal(row.LookScale, copy.LookScale);
                Assert.Equal(row.LookSpeedSeconds, copy.LookSpeedSeconds);
                Assert.Equal(row.LookEaseType, copy.LookEaseType);
            }

            Assert.DoesNotContain(result.Set.ScenarioGroups, g => g.Id == Hello);
            Assert.DoesNotContain(result.Set.Novel, r => r.ScenarioGroupId == Hello);
            Assert.Same(set.Localization, result.Set.Localization);
            Assert.Equal(10, result.Set.ScenarioGroups.Count);
            Assert.Equal(10, result.Set.Novel.Count);
        }

        [Fact]
        public void A_spread_group_written_as_the_pack_files_say_loads_through_the_merge_and_the_screen()
        {
            var input = new MasterDataPackInput("my.lines")
            {
                ScenarioGroupsJson = """
                    [{"ID": "my_hello", "Scenario": "None", "EpisodeNumber": 5001, "Languages": ["th"], "Pool": "gamestart.any", "Weight": 0.5}]
                    """,
                NovelJson = """
                    [{"ID": "my_hello_001", "ScenarioGroupID": "my_hello", "Command": "Text", "Arg1": "0", "Arg2": 1.0, "Arg3": "0",
                      "Talker": "Heroine", "LocalizationID": "my_hello_001", "BodyMotion": -1, "FacialMotion": 1}]
                    """,
                LocalizationJson = """[{"ID": "my_hello_001", "Ja": "", "En": "Welcome back!", "Voice": "Voice_My_Hello_001"}]""",
            };

            var merged = MasterDataMerge.Merge(new[] { input });
            var result = PackLineScreen.Screen(merged, Game, admit: true);

            Assert.Equal(0, merged.Report.RejectedCount);
            Assert.Empty(result.Rejected);
            Assert.Equal(10, result.Lines.Count);
            Assert.All(result.Lines, l => Assert.Equal(0.5, l.Weight));
            Assert.All(result.Lines, l => Assert.Equal(new[] { "Voice_My_Hello_001" }, l.Voices));
            Assert.Equal(10, result.Set.Novel.Count);
        }

        [Fact]
        public void A_spread_ignores_the_groups_own_scenario()
        {
            var result = Screen(Spread(Hello, "GameStart.Any", scenario: 9), Text("r1", Hello, "l1"));

            Assert.Equal(10, result.Lines.Count);
            Assert.All(result.Lines, l => Assert.Equal(PackLineFamily.GameStart, l.Pool.Family));
            Assert.All(result.Lines, l => Assert.Equal(PackLinePools.AnyGameStart, l.Spread));
        }

        [Fact]
        public void A_copy_keeps_its_rows_order_and_its_jumps_inside_the_copy()
        {
            var result = Screen(
                Spread(Hello, "gamestart.any"),
                Text("r1", Hello, "l1"),
                new NovelRow { Id = "r2", ScenarioGroupId = Hello, Command = PackLineRows.Jump, Arg1 = "r4" },
                Text("r3", Hello, "l3"),
                Text("r4", Hello, "l4"));

            Assert.Empty(result.Rejected);
            var rows = result.Set.Novel.Where(r => r.ScenarioGroupId == Hello + "@gamestart.sameday").ToArray();
            Assert.Equal(new[] { "r1", "r2", "r3", "r4" }.Select(id => id + "@gamestart.sameday"), rows.Select(r => r.Id));
            Assert.Equal("r4@gamestart.sameday", rows[1].Arg1);
        }

        [Fact]
        public void If_one_copy_fails_the_written_group_is_refused_once_naming_the_pool()
        {
            var noon = PackLineRows.Group("cwy_th_noon", 1402, 5001, new[] { "th" });
            var result = Screen(
                new[] { noon, Spread(Hello, "gamestart.any") },
                new[] { Text("noon_001", "cwy_th_noon", "l_noon"), Text("hello_001", Hello, "l_hello") });

            var rejection = Assert.Single(result.Rejected);
            Assert.Equal(Hello, rejection.GroupId);
            Assert.Equal(
                "as its copy for gamestart.newday.noon (\"Pool\": \"gamestart.any\"): EpisodeNumber 5001 of GameStart_LessTowDays_CameraTouch_Noon "
                + "is already used by group 'cwy_th_noon'; every (Scenario, EpisodeNumber) must be unique",
                rejection.Reason);

            Assert.Equal("cwy_th_noon", Assert.Single(result.Lines).GroupId);
            Assert.Equal(new[] { "cwy_th_noon" }, result.Set.ScenarioGroups.Select(g => g.Id));
            Assert.DoesNotContain(result.Set.Novel, r => r.Id.Contains('@'));
            Assert.Contains(result.Set.Novel, r => r.Id == "hello_001");
        }

        [Fact]
        public void A_copy_claims_its_episode_so_a_later_line_on_it_is_refused()
        {
            var noon = PackLineRows.Group("cwy_th_noon", 1402, 5001, new[] { "th" });
            var result = Screen(
                new[] { Spread(Hello, "gamestart.any"), noon },
                new[] { Text("hello_001", Hello, "l_hello"), Text("noon_001", "cwy_th_noon", "l_noon") });

            Assert.Equal(10, result.Lines.Count);
            var rejection = Assert.Single(result.Rejected);
            Assert.Equal("cwy_th_noon", rejection.GroupId);
            Assert.Contains("already used by group 'cwy_th_hello@gamestart.newday.noon'", rejection.Reason);
        }

        [Fact]
        public void A_copy_may_not_take_an_id_a_group_or_a_row_already_has()
        {
            var taken = PackLineRows.Group(Hello + "@gamestart.newday", 22, 900);
            var groupClash = Screen(new[] { taken, Spread(Hello, "gamestart.any") }, new[] { Text("r1", Hello, "l1") });
            var rowClash = Screen(
                new[] { Spread(Hello, "gamestart.any") },
                new[] { Text("r1", Hello, "l1"), new NovelRow { Id = "r1@gamestart.days3to14", ScenarioGroupId = "someone_else", Command = 12 } });

            Assert.Contains("the copy's id 'cwy_th_hello@gamestart.newday' is already a scenario group's id", Assert.Single(groupClash.Rejected).Reason);
            Assert.Contains("the copy's novel row id 'r1@gamestart.days3to14' is already a novel row's id", Assert.Single(rowClash.Rejected).Reason);
        }

        [Fact]
        public void A_spread_group_is_held_back_like_any_line_while_pack_lines_are_not_active()
        {
            var set = PackLineRows.Set(new[] { Spread(Hello, "gamestart.any") }, new[] { Text("r1", Hello, "l1") }, new[] { PackLineRows.Line("l1") });

            var result = PackLineScreen.Screen(set, Game, admit: false);

            Assert.Equal(1, result.HeldBack);
            Assert.Empty(result.Lines);
            Assert.Empty(result.Set.ScenarioGroups);
            Assert.Same(set.Novel, result.Set.Novel);
            Assert.Empty(result.Notes);
        }

        [Fact]
        public void The_novel_rows_are_the_same_instance_while_no_spread_group_is_accepted()
        {
            var set = PackLineRows.Set(PackLineRows.Click("cwy_th_click"));

            var result = PackLineScreen.Screen(set, Game, admit: true);

            Assert.Single(result.Lines);
            Assert.Same(set.Novel, result.Set.Novel);
            Assert.Empty(result.Notes);
            Assert.Empty(PackLineScreenResult.Empty.Notes);
        }

        // ---- Global lines ----

        [Fact]
        public void A_global_line_fills_every_pool_but_the_answer_choice_talk()
        {
            var targets = Targets("*");

            Assert.Equal(52, targets.Count);
            Assert.Equal(PackLinePools.All.Where(p => p.Id != "pomodoro.end").Select(p => p.Id), targets.Select(p => p.Id));
            Assert.Contains(targets, p => p.Family == PackLineFamily.GameStart);
            Assert.Contains(targets, p => p.Id == "smalltalk");
            Assert.Contains(targets, p => p.Id == "selftalk.time.24");
            Assert.Contains(targets, p => p.Id == "selftalk.worked.10h");
            Assert.False(PackLinePools.TryGetSpread("click.work", out var none));
            Assert.Empty(none);

            var result = Screen(Spread(Hello, "*"), Text("r1", Hello, "l1"));

            Assert.Empty(result.Rejected);
            Assert.Empty(result.Notes);
            Assert.Equal(targets.Select(p => p.Id), result.Lines.Select(l => l.Pool.Id));
            Assert.All(result.Lines, l => Assert.True(l.IsGlobal));
            Assert.All(result.Lines, l => Assert.Equal(Hello, l.SourceGroupId));
        }

        // ---- Global lines: one episode per clock and worked-time slot ----

        private static readonly string[] ClockSlots = { "selftalk.time.7", "selftalk.time.12", "selftalk.time.17", "selftalk.time.24" };

        private static readonly string[] WorkedSlots = { "selftalk.worked.2h", "selftalk.worked.5h", "selftalk.worked.10h" };

        private static int[] EpisodesOf(PackLineScreenResult result, string source, string[] slots) =>
            slots.Select(slot => Assert.Single(result.Lines, l => l.GroupId == source + "@" + slot).Episode).ToArray();

        [Fact]
        public void A_global_line_gets_its_own_episode_in_each_clock_and_worked_time_slot()
        {
            var result = Screen(Spread(Hello, "*", episode: 5001), Text("r1", Hello, "l1"));

            Assert.Empty(result.Rejected);
            Assert.Equal(new[] { 5001, 5002, 5003, 5004 }, EpisodesOf(result, Hello, ClockSlots));
            Assert.Equal(new[] { 5001, 5002, 5003 }, EpisodesOf(result, Hello, WorkedSlots));

            // The copy's group carries the slot's episode, and every other pool keeps the written one.
            foreach (var slot in ClockSlots.Concat(WorkedSlots))
            {
                var line = Assert.Single(result.Lines, l => l.Pool.Id == slot);
                var group = Assert.Single(result.Set.ScenarioGroups, g => g.Id == line.GroupId);
                Assert.Equal(line.Pool.Scenario, group.Scenario);
                Assert.Equal((float)line.Episode, group.EpisodeNumber);
            }

            Assert.All(result.Lines.Where(l => l.Pool.Scenario != 2050 && l.Pool.Scenario != 2100), l => Assert.Equal(5001, l.Episode));
            Assert.Equal(
                result.Set.ScenarioGroups.Count,
                result.Set.ScenarioGroups.Select(g => (g.Scenario, g.EpisodeNumber)).Distinct().Count());
        }

        [Fact]
        public void The_registry_finds_each_slot_by_its_own_episode()
        {
            var registry = PackLineRegistry.Build(Screen(Spread(Hello, "*", episode: 5001), Text("r1", Hello, "l1")).Lines);

            foreach (var slot in ClockSlots.Concat(WorkedSlots))
            {
                var episodes = registry.EpisodesOf(slot);
                var episode = Assert.Single(episodes);
                Assert.True(registry.TryGet(slot, episode, out var line), slot);
                Assert.Equal(Hello + "@" + slot, line!.GroupId);
                Assert.Equal(slot, line.Pool.Id);
            }

            // Each episode answers in its own slot only.
            Assert.True(registry.TryGet("selftalk.time.12", 5002, out _));
            Assert.False(registry.TryGet("selftalk.time.7", 5002, out _));
            Assert.False(registry.TryGet("selftalk.time.12", 5001, out _));
        }

        [Fact]
        public void The_automatic_episodes_are_the_same_for_the_same_input()
        {
            ScenarioGroupRow[] Groups() => new[]
            {
                Spread(Hello, "*", episode: 5001),
                Spread("cwy_th_bye", "*", episode: 5002),
                PackLineRows.Group("cwy_th_noon", 2050, 5003, new[] { "th" }, "selftalk.time.12"),
            };

            NovelRow[] Rows() => new[] { Text("r1", Hello, "l1"), Text("r2", "cwy_th_bye", "l2"), Text("r3", "cwy_th_noon", "l3") };

            var first = Screen(Groups(), Rows());
            var second = Screen(Groups(), Rows());

            Assert.Empty(first.Rejected);
            Assert.Equal(first.Lines.Select(l => l.GroupId + "#" + l.Episode), second.Lines.Select(l => l.GroupId + "#" + l.Episode));
            Assert.Equal(PackLineRegistry.Build(first.Lines).Fingerprint, PackLineRegistry.Build(second.Lines).Fingerprint);
        }

        [Fact]
        public void An_automatic_episode_skips_every_episode_another_group_writes_or_claims()
        {
            // In the clock scenario 5002 is an ordinary pack group's, 5003 a pack line's later in the
            // input and 5005 a later global line's first slot: the first line's automatic episodes skip
            // all three, and the later global line's skip the first line's.
            var groups = new[]
            {
                PackLineRows.Group("cwy_ordinary_clock", 2050, 5002),
                Spread(Hello, "*", episode: 5001),
                PackLineRows.Group("cwy_th_noon", 2050, 5003, new[] { "th" }, "selftalk.time.12"),
                Spread("cwy_th_bye", "*", episode: 5005),
            };
            var rows = new[] { Text("r1", Hello, "l1"), Text("r3", "cwy_th_noon", "l3"), Text("r4", "cwy_th_bye", "l4") };

            var result = Screen(groups, rows);

            Assert.Empty(result.Rejected);
            Assert.Equal(new[] { 5001, 5004, 5006, 5007 }, EpisodesOf(result, Hello, ClockSlots));
            Assert.Equal(5003, Assert.Single(result.Lines, l => l.GroupId == "cwy_th_noon").Episode);
            Assert.Equal(new[] { 5005, 5008, 5009, 5010 }, EpisodesOf(result, "cwy_th_bye", ClockSlots));

            // The worked-time scenario has none of those claims: each line counts up from its own episode.
            Assert.Equal(new[] { 5001, 5002, 5003 }, EpisodesOf(result, Hello, WorkedSlots));
            Assert.Equal(new[] { 5005, 5006, 5007 }, EpisodesOf(result, "cwy_th_bye", WorkedSlots));

            var clock = result.Set.ScenarioGroups.Where(g => g.Scenario == 2050).Select(g => g.EpisodeNumber).ToArray();
            Assert.Equal(clock.Length, clock.Distinct().Count());
        }

        [Fact]
        public void An_automatic_episode_stays_in_the_pack_range_and_wraps_to_its_bottom()
        {
            var result = Screen(Spread(Hello, "*", episode: PackLinePools.MaximumEpisode - 1), Text("r1", Hello, "l1"));

            Assert.Empty(result.Rejected);
            Assert.Equal(
                new[] { PackLinePools.MaximumEpisode - 1, PackLinePools.MaximumEpisode, PackLinePools.MinimumEpisode, PackLinePools.MinimumEpisode + 1 },
                EpisodesOf(result, Hello, ClockSlots));
        }

        [Fact]
        public void The_first_slot_keeps_the_written_episode_so_a_clash_there_refuses_the_line()
        {
            var clock = PackLineRows.Group("cwy_th_seven", 2050, 5001, new[] { "th" }, "selftalk.time.7");
            var result = Screen(new[] { clock, Spread(Hello, "*", episode: 5001) }, new[] { Text("r0", "cwy_th_seven", "l0"), Text("r1", Hello, "l1") });

            var rejection = Assert.Single(result.Rejected);
            Assert.Equal(Hello, rejection.GroupId);
            Assert.StartsWith("as its copy for selftalk.time.7 (\"Pool\": \"*\"): EpisodeNumber 5001 of HeroineSelfShortTalk_CurrentTime is already used by group 'cwy_th_seven'", rejection.Reason);
        }

        [Fact]
        public void A_global_lines_timer_copy_speaks_its_one_row_at_once_and_other_copies_keep_it_as_written()
        {
            var row = Text("r1", Hello, "l1", body: 1000);
            var result = Screen(Spread(Hello, "*"), row);

            foreach (var pool in Targets("*"))
            {
                var copy = Assert.Single(result.Set.Novel, r => r.ScenarioGroupId == Hello + "@" + pool.Id);
                Assert.Equal("r1@" + pool.Id, copy.Id);
                Assert.Equal("l1", copy.LocalizationId);
                Assert.Equal(row.Talker, copy.Talker);
                Assert.Equal(row.FacialMotion, copy.FacialMotion);
                Assert.Equal(row.LookScale, copy.LookScale);
                Assert.Equal(row.LookSpeedSeconds, copy.LookSpeedSeconds);
                Assert.Equal(row.LookEaseType, copy.LookEaseType);

                if (pool.Shape == PackLineShape.TimerVoice)
                {
                    Assert.Equal(PackLineRows.PomodoroVoiceText, copy.Command);
                    Assert.Equal("0", copy.Arg1);
                    Assert.Equal(0f, copy.Arg2);
                    Assert.Equal("0", copy.Arg3);
                    Assert.Equal(-1, copy.BodyMotion);
                }
                else
                {
                    Assert.Equal(PackLineRows.Text, copy.Command);
                    Assert.Equal(row.Arg2, copy.Arg2);
                    Assert.Equal(1000, copy.BodyMotion);
                }
            }
        }

        [Fact]
        public void A_global_line_with_several_spoken_rows_skips_the_timer_pools_with_one_note()
        {
            var result = Screen(Spread(Hello, "*"), Text("r1", Hello, "l1"), Text("r2", Hello, "l2"));

            Assert.Empty(result.Rejected);
            Assert.Equal(Targets("*").Where(p => p.Shape != PackLineShape.TimerVoice).Select(p => p.Id), result.Lines.Select(l => l.Pool.Id));
            Assert.Equal(
                "'cwy_th_hello' is global but speaks 2 rows: a timer line speaks one PomodoroVoiceText row, so it skips the timer pools (pomodoro.*, countup.*)",
                Assert.Single(result.Notes));
        }

        [Fact]
        public void A_global_line_at_weight_1_is_accepted_with_one_note()
        {
            var global = Screen(Spread(Hello, "*", weight: 1d), Text("r1", Hello, "l1"));
            var startup = Screen(Spread(Hello, "gamestart.any", weight: 1d), Text("r1", Hello, "l1"));

            Assert.Empty(global.Rejected);
            Assert.Equal(
                "'cwy_th_hello' is global with Weight 1: Satone says it at every pick of every pool it joins (clicks, self-talk, timer lines, greetings, small talk)",
                Assert.Single(global.Notes));
            Assert.All(global.Lines, l => Assert.Equal(1d, l.Weight));
            Assert.Empty(startup.Notes);
        }

        [Fact]
        public void A_global_lines_small_talk_copy_gets_the_lottery_columns_unless_it_carries_valid_ones()
        {
            ScenarioGroupRow SmallTalkCopy(PackLineScreenResult result) =>
                Assert.Single(result.Set.ScenarioGroups, g => g.Id == Hello + "@smalltalk");

            var plain = Screen(Spread(Hello, "*"), Text("r1", Hello, "l1"));
            var copy = SmallTalkCopy(plain);
            Assert.Equal(("Break", "None", "None", "25", "5", "0"), (copy.PlayTiming, copy.ContentType, copy.UnlockStoryEpisode, copy.Arg1, copy.Arg2, copy.Arg3));
            Assert.Equal(23, copy.Scenario);
            Assert.Empty(plain.Notes);

            var valid = new ScenarioGroupRow
            {
                Id = Hello, Scenario = 0, EpisodeNumber = 5001, Languages = new[] { "th" }, Pool = "*",
                PlayTiming = "GameStartAndBreak", ContentType = "None", UnlockStoryEpisode = "main_01", Arg1 = "50", Arg2 = "10", Arg3 = "0"
            };
            var kept = Screen(valid, Text("r1", Hello, "l1"));
            copy = SmallTalkCopy(kept);
            Assert.Equal(("GameStartAndBreak", "None", "main_01", "50", "10"), (copy.PlayTiming, copy.ContentType, copy.UnlockStoryEpisode, copy.Arg1, copy.Arg2));
            Assert.Empty(kept.Notes);

            var typo = new ScenarioGroupRow
            {
                Id = Hello, Scenario = 0, EpisodeNumber = 5001, Languages = new[] { "th" }, Pool = "*", PlayTiming = "Brake", Arg1 = "50"
            };
            var fixedUp = Screen(typo, Text("r1", Hello, "l1"));
            copy = SmallTalkCopy(fixedUp);
            Assert.Equal(("Break", "25"), (copy.PlayTiming, copy.Arg1));
            var note = Assert.Single(fixedUp.Notes);
            Assert.StartsWith("'cwy_th_hello' is global, but its small-talk columns do not play (PlayTiming \"Brake\" never plays a small talk", note);
            Assert.EndsWith("so its small-talk copy uses PlayTiming Break, ContentType None, UnlockStoryEpisode None, Arg1 25, Arg2 5", note);
        }

        [Fact]
        public void A_global_line_a_startup_pool_cannot_take_is_refused_whole()
        {
            var result = Screen(
                new[] { Spread(Hello, "*") },
                new[]
                {
                    Text("q", Hello, "l_q"),
                    new NovelRow { Id = "pick", ScenarioGroupId = Hello, Command = PackLineRows.Selection, Arg1 = "a", LocalizationId = "l_button" },
                    Text("a", Hello, "l_a"),
                });

            Assert.Empty(result.Lines);
            Assert.Empty(result.Notes);
            var rejection = Assert.Single(result.Rejected);
            Assert.StartsWith("as its copy for gamestart.sameday (\"Pool\": \"*\"): novel row 'pick@gamestart.sameday' is a Selection", rejection.Reason);
            Assert.Empty(result.Set.ScenarioGroups);
        }

        // ---- Registry ----

        [Fact]
        public void Every_copy_is_a_registered_group_and_the_fingerprint_follows_weight_and_spread()
        {
            var lines = Screen(Spread(Hello, "*", weight: 0.25), Text("r1", Hello, "l1")).Lines;
            var registry = PackLineRegistry.Build(lines);

            Assert.Equal(lines.Count, registry.Count);
            Assert.Equal(lines.Select(l => l.GroupId).OrderBy(id => id, StringComparer.Ordinal), registry.GroupIds.OrderBy(id => id, StringComparer.Ordinal));
            Assert.False(registry.IsPackGroup(Hello));
            Assert.True(registry.IsPackGroup(Hello + "@click.work"));
            Assert.True(registry.TryGetByGroup(Hello + "@pomodoro.start", out var timer));
            Assert.Equal(5001, timer!.Episode);

            var pool = Pool("click.work");
            PackLineRegistry One(double? weight, string? spread) =>
                PackLineRegistry.Build(new[] { new PackLine("x@click.work", pool, 5001, new[] { "th" }, null, weight, "x", spread) });

            Assert.Equal(One(0.25, "*").Fingerprint, One(0.25, "*").Fingerprint);
            Assert.NotEqual(One(0.25, "*").Fingerprint, One(0.5, "*").Fingerprint);
            Assert.NotEqual(One(0.25, "*").Fingerprint, One(null, "*").Fingerprint);
            Assert.NotEqual(One(0.25, "*").Fingerprint, One(0.25, "gamestart.any").Fingerprint);
        }

        [Fact]
        public void The_list_report_shows_weights_copies_and_how_a_startup_pool_picks()
        {
            var lines = Screen(Spread(Hello, "gamestart.any", weight: 0.25), Text("r1", Hello, "l1")).Lines;
            var text = PackLineReport.List(PackLineRegistry.Build(lines), null, "th", string.Empty, null, null);

            Assert.Contains(
                "gamestart.sameday  GameStart_LessTowDays  game 1-17 + 1 pack: picked at launch by weight (no Weight = an equal share with the game's lines); "
                + "the game's deck is never widened",
                text);
            Assert.Contains("  #5001  cwy_th_hello@gamestart.sameday  th  offered  weight 0.25  copy of cwy_th_hello (\"Pool\": \"gamestart.any\")", text);
            Assert.Equal(
                "plays only at launch, in place of the game's greeting: restart the game (Weight 1 makes it certain)",
                PackLineReport.WhenArmedPlays(Pool("gamestart.newday.night")));
            Assert.Equal("game 1-5 + 2 pack: each line once per 7-pick cycle", PackLineReport.Weight(Pool("click.work.morning"), 2));
        }
    }
}
