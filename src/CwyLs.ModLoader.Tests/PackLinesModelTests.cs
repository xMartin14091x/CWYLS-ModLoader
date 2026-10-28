using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Pack lines: the pool catalogue, the registry, the <c>Languages</c>/<c>Pool</c> members of
    /// <c>data/scenario_groups.json</c>, the timer test arm and the console report.
    /// </summary>
    public partial class PackLinesModelTests
    {
        private static PackLinePool Pool(string id)
        {
            Assert.True(PackLinePools.TryGet(id, out var pool), id);
            return pool!;
        }

        [Fact]
        public void The_catalogue_has_forty_three_pools_with_unique_ids_and_the_games_scenario_names()
        {
            Assert.Equal(43, PackLinePools.All.Count);
            Assert.Equal(43, PackLinePools.All.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var pool in PackLinePools.All)
            {
                Assert.True(MasterDataEnums.Scenario.TryName(pool.Scenario, out var name), pool.Id);
                Assert.Equal(pool.ScenarioName, name);
                Assert.True(pool.IsDataDriven || pool.VanillaFirst >= 1 && pool.VanillaLast >= pool.VanillaFirst, pool.Id);
                Assert.True(pool.VanillaLast < PackLinePools.MinimumEpisode, pool.Id);
            }
        }

        [Fact]
        public void Only_the_clock_and_worked_time_scenarios_feed_several_pools()
        {
            var shared = PackLinePools.All.GroupBy(p => p.Scenario).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(k => k);

            Assert.Equal(new[] { 2050, 2100 }, shared);
            Assert.Equal(new[] { 1, 2, 3 }, PackLinePools.ForScenario(2050).Select(p => p.StandIns[0]).Take(3));
            Assert.Equal(new[] { 4, 5 }, Pool("selftalk.time.24").StandIns);
            Assert.Empty(Pool("selftalk.worked.10h").StandIns);
        }

        [Fact]
        public void Timer_pools_carry_their_moment_and_mode_and_only_they_are_not_click_style()
        {
            Assert.Equal(PackLineTimerPhase.WorkStart, Pool("countup.start.continuous").Phase);
            Assert.Equal(PackLineTimerMode.Countup, Pool("countup.start.continuous").TimerMode);
            Assert.Equal(PackLineTimerPhase.Finish, Pool("pomodoro.finish.midway").Phase);
            Assert.All(PackLinePools.All, p => Assert.Equal(p.Family != PackLineFamily.TimerLine, p.IsClickStyle));
            Assert.True(Pool("pomodoro.end").IsClickStyle);
        }

        [Fact]
        public void The_registry_indexes_lines_by_pool_episode_and_group()
        {
            var registry = PackLineRegistry.Build(new[]
            {
                new PackLine("cwy_th_click_002", Pool("click.work"), 1002, new[] { "th" }),
                new PackLine("cwy_th_small_001", Pool("smalltalk"), 1001, new[] { "th" }),
                new PackLine("cwy_ja_click_001", Pool("click.work"), 1001, new[] { "ja" }),
            });

            Assert.Equal(3, registry.Count);
            Assert.Equal(new[] { "click.work", "smalltalk" }, registry.Pools.Select(p => p.Id));
            Assert.Equal(new[] { 1002, 1001 }, registry.EpisodesOf("click.work"));
            Assert.True(registry.TryGet("click.work", 1001, out var line));
            Assert.Equal("cwy_ja_click_001", line!.GroupId);
            Assert.False(registry.TryGet("click.break", 1001, out _));
            Assert.True(registry.IsPackGroup("cwy_th_small_001"));
            Assert.False(registry.IsPackGroup("smalltalk_01"));
            Assert.Equal(new[] { "th", "ja" }, registry.Languages);
            Assert.Equal("click.work:1,smalltalk:1", registry.DescribePools("th"));
            Assert.Equal("click.work:2,smalltalk:1", registry.DescribePools());
        }

        [Fact]
        public void The_registry_ignores_a_second_line_for_a_group_or_an_episode_and_fingerprints_its_content()
        {
            var a = new PackLine("cwy_th_a", Pool("click.work"), 1001, new[] { "th" });
            var sameGroup = new PackLine("cwy_th_a", Pool("click.break"), 1001, new[] { "th" });
            var sameEpisode = new PackLine("cwy_th_b", Pool("click.work"), 1001, new[] { "th" });

            var registry = PackLineRegistry.Build(new[] { a, sameGroup, sameEpisode });
            var again = PackLineRegistry.Build(new[] { a });
            var other = PackLineRegistry.Build(new[] { new PackLine("cwy_th_a", Pool("click.work"), 1001, new[] { "th", "en" }) });

            Assert.Same(a, Assert.Single(registry.Lines));
            Assert.Equal(registry.Fingerprint, again.Fingerprint);
            Assert.NotEqual(registry.Fingerprint, other.Fingerprint);
            Assert.Same(PackLineRegistry.Empty, PackLineRegistry.Build(null));
        }

        [Fact]
        public void A_line_is_offered_in_its_languages_only_whatever_the_spelling()
        {
            var line = new PackLine("cwy_th_a", Pool("click.work"), 1001, new[] { "Thai" });

            Assert.True(line.IsOfferedIn("th"));
            Assert.True(line.IsOfferedIn("TH"));
            Assert.False(line.IsOfferedIn("en"));
            Assert.False(line.IsOfferedIn(""));
            Assert.False(line.IsOfferedIn(null));
            Assert.Throws<ArgumentException>(() => new PackLine("x", Pool("click.work"), 1001, new[] { " " }));
        }

        [Fact]
        public void The_parser_reads_Languages_and_Pool_and_leaves_rows_without_them_unchanged()
        {
            var parsed = MasterDataPackParser.ParseScenarioGroups(
                """
                [
                  {"ID": "plain", "Scenario": "MotionScenario", "EpisodeNumber": 900.0},
                  {"id": "line", "scenario": "HeroineSelfShortTalk_CurrentTime", "episodeNumber": 1001,
                   "languages": ["th", " en "], "pool": " selftalk.time.7 "}
                ]
                """,
                "p/data/scenario_groups.json");

            Assert.True(parsed.Clean);
            Assert.Null(parsed.Rows[0].Languages);
            Assert.Equal(string.Empty, parsed.Rows[0].Pool);
            Assert.Equal(new[] { "th", "en" }, parsed.Rows[1].Languages);
            Assert.Equal("selftalk.time.7", parsed.Rows[1].Pool);
        }

        [Theory]
        [InlineData("[]", "is empty")]
        [InlineData("\"th\"", "must be an array")]
        [InlineData("[\"th\", 5]", "item 1 must be a non-blank language code")]
        [InlineData("[\"\"]", "item 0 must be a non-blank language code")]
        public void A_malformed_Languages_member_rejects_the_row(string languages, string expected)
        {
            var parsed = MasterDataPackParser.ParseScenarioGroups(
                "[{\"ID\": \"g\", \"Scenario\": 9, \"Languages\": " + languages + "}]", "p/data/scenario_groups.json");

            Assert.Empty(parsed.Rows);
            var rejection = Assert.Single(parsed.Rejected);
            Assert.Equal("Languages", rejection.Member);
            Assert.Contains(expected, rejection.Reason);
        }

        [Fact]
        public void The_test_arm_hands_a_timer_line_only_to_its_own_moment_mode_and_language()
        {
            var arm = new PackLineTestArm();
            var line = new PackLine("cwy_th_start", Pool("pomodoro.start.long"), 1001, new[] { "th" });
            arm.Arm(line, new DateTime(2026, 10, 1, 23, 0, 0));

            Assert.False(arm.TryTake(PackLineTimerPhase.BreakStart, PackLineTimerMode.Pomodoro, "th", out _));
            Assert.False(arm.TryTake(PackLineTimerPhase.WorkStart, PackLineTimerMode.Countup, "th", out _));
            Assert.False(arm.TryTake(PackLineTimerPhase.WorkStart, PackLineTimerMode.Pomodoro, "en", out _));
            Assert.Same(line, arm.Armed);

            Assert.True(arm.TryTake(PackLineTimerPhase.WorkStart, PackLineTimerMode.Pomodoro, "th", out var taken));
            Assert.Same(line, taken);
            Assert.Null(arm.Armed);
            Assert.Null(arm.Clear());
            Assert.Throws<ArgumentException>(() => arm.Arm(new PackLine("c", Pool("click.work"), 1001, new[] { "th" }), DateTime.Now));
        }

        [Fact]
        public void The_list_report_shows_pools_the_gate_rejections_and_the_armed_line()
        {
            var registry = PackLineRegistry.Build(new[]
            {
                new PackLine("cwy_th_click_001", Pool("click.work.morning"), 1001, new[] { "th" }),
                new PackLine("cwy_th_start_001", Pool("pomodoro.start"), 1001, new[] { "th" }),
            });
            var rejected = new[] { new PackLineRejection("cwy_th_bad", "has no novel rows") };

            var text = PackLineReport.List(registry, rejected, "en", string.Empty, p => p.Id == "pomodoro.start" ? "1 in the game's list" : null, registry.Lines[1]);

            Assert.Contains("pack lines: 2 line(s) in 2 pool(s), languages th; the game shows en: 0 offered", text);
            Assert.Contains("click.work.morning  HeroineClickWork_Morning  game 1-5 + 1 pack: each line once per 6-pick cycle", text);
            Assert.Contains("  #1001  cwy_th_click_001  th  not offered", text);
            Assert.Contains("live: 1 in the game's list", text);
            Assert.Contains("PACKLINES skipped scenario_groups row 'cwy_th_bad': has no novel rows", text);
            Assert.Contains("armed: cwy_th_start_001 (pomodoro.start #1001, th) - plays at the next work start in pomodoro mode", text);
        }

        [Fact]
        public void The_report_says_why_the_feature_is_off_and_how_small_talk_weighs()
        {
            var text = PackLineReport.List(null, null, "", "missing seam(s): packlines.randomlist.initlist", null, null);

            Assert.StartsWith("pack lines are off: missing seam(s): packlines.randomlist.initlist", text);
            Assert.Contains("the game shows (not loaded yet)", text);
            Assert.Equal("data-driven + 2 pack: same lottery rules as the game's small talks", PackLineReport.Weight(Pool("smalltalk"), 2));
            Assert.Equal("plays at once as a click reaction", PackLineReport.WhenArmedPlays(Pool("selftalk.break")));
        }
    }
}
