using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Weighted pack lines: the <c>Weight</c> member of
    /// <c>data/scenario_groups.json</c>, the screen's range check, the weighted pick
    /// (<see cref="PackLineWeights"/>) and the last-played file (<see cref="PackLineLastPlayed"/>).
    /// </summary>
    public class PackLinesWeightTests
    {
        private const string GroupFile = "p/data/scenario_groups.json";

        private const string WeightRule = "Weight must be a number from 0 to 1 (0.25 = a quarter of this pool's picks, 1 = every pick), not ";

        private static readonly MasterDataBaseline Game =
            MasterDataBaseline.Build(new[] { "main_01" }, null, new[] { "main_01" });

        private static PackLinePool Pool(string id)
        {
            Assert.True(PackLinePools.TryGet(id, out var pool), id);
            return pool!;
        }

        private static PackLine Line(string id, string? source = null, string? spread = null) =>
            new PackLine(id, Pool("gamestart.sameday"), 1001, new[] { "th" }, null, null, source, spread);

        private static PackLineCandidate[] Offer(params (PackLine Line, double Weight)[] lines) =>
            lines.Select(l => new PackLineCandidate(l.Line, l.Weight)).ToArray();

        private static MasterDataSet Greeting(string id, double? weight, int scenario = 13) =>
            PackLineRows.Set(
                new[] { PackLineRows.Group(id, scenario, 1001, new[] { "th" }, weight: weight) },
                new[] { PackLineRows.Spoken(id + "_001", id) },
                new[] { PackLineRows.Line(id + "_001", "Voice_" + id) });

        // ---- The member ----

        [Fact]
        public void The_parser_reads_Weight_as_an_optional_number_and_leaves_rows_without_it_unchanged()
        {
            var parsed = MasterDataPackParser.ParseScenarioGroups(
                """
                [
                  {"ID": "plain", "Scenario": "MotionScenario", "EpisodeNumber": 900.0},
                  {"ID": "quarter", "Scenario": 13, "EpisodeNumber": 1001, "Languages": ["th"], "Weight": 0.25},
                  {"ID": "whole", "Scenario": 13, "EpisodeNumber": 1002, "Languages": ["th"], "weight": 1},
                  {"ID": "zero", "Scenario": 13, "EpisodeNumber": 1003, "Languages": ["th"], "WEIGHT": 0},
                  {"ID": "nulled", "Scenario": 13, "EpisodeNumber": 1004, "Languages": ["th"], "Weight": null},
                  {"ID": "big", "Scenario": 13, "EpisodeNumber": 1005, "Languages": ["th"], "Weight": 1.5}
                ]
                """,
                GroupFile);

            Assert.True(parsed.Clean, string.Join(" | ", parsed.Rejected));
            Assert.Equal(new double?[] { null, 0.25, 1d, 0d, null, 1.5 }, parsed.Rows.Select(r => r.Weight));
            Assert.Null(parsed.Rows[0].Languages);
            Assert.Equal(string.Empty, parsed.Rows[0].Pool);
        }

        [Theory]
        [InlineData("\"0.25\"", "not a string (write the number without quotes)")]
        [InlineData("true", "not a boolean")]
        [InlineData("[0.25]", "not an array")]
        [InlineData("{\"chance\": 0.25}", "not an object")]
        public void A_Weight_that_is_not_a_number_rejects_the_row_naming_the_member(string weight, string expected)
        {
            var parsed = MasterDataPackParser.ParseScenarioGroups(
                "[{\"ID\": \"g\", \"Scenario\": 13, \"EpisodeNumber\": 1001, \"Languages\": [\"th\"], \"Weight\": " + weight + "}]",
                GroupFile);

            Assert.Empty(parsed.Rows);
            var rejection = Assert.Single(parsed.Rejected);
            Assert.Equal("Weight", rejection.Member);
            Assert.Contains("must be a number such as 0.25", rejection.Reason);
            Assert.Contains(expected, rejection.Reason);
        }

        // ---- The screen ----

        [Theory]
        [InlineData(1.5, "1.5")]
        [InlineData(-0.25, "-0.25")]
        [InlineData(2d, "2")]
        [InlineData(double.NaN, "NaN")]
        public void A_Weight_outside_0_to_1_is_refused_saying_how_to_write_it(double weight, string written)
        {
            var result = PackLineScreen.Screen(Greeting("cwy_th_hello", weight), Game, admit: true);

            Assert.Empty(result.Lines);
            var rejection = Assert.Single(result.Rejected);
            Assert.Equal("cwy_th_hello", rejection.GroupId);
            Assert.Equal(WeightRule + written, rejection.Reason);
        }

        [Theory]
        [InlineData(0d)]
        [InlineData(0.25)]
        [InlineData(1d)]
        public void A_Weight_from_0_to_1_is_accepted_and_carried_into_the_line(double weight)
        {
            var line = Assert.Single(PackLineScreen.Screen(Greeting("cwy_th_hello", weight), Game, admit: true).Lines);

            Assert.Equal(weight, line.Weight);
            Assert.True(line.IsWeighted);
            Assert.False(line.IsCopy);
            Assert.Equal("cwy_th_hello", line.SourceGroupId);
            Assert.Equal("gamestart.sameday", line.Pool.Id);
        }

        [Fact]
        public void Without_a_Weight_a_line_is_unweighted_as_before()
        {
            var line = Assert.Single(PackLineScreen.Screen(PackLineRows.Set(PackLineRows.Click("cwy_th_click")), Game, admit: true).Lines);

            Assert.Null(line.Weight);
            Assert.False(line.IsWeighted);
        }

        [Fact]
        public void A_line_refuses_a_weight_outside_0_to_1_however_it_is_built()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PackLine("x", Pool("click.work"), 1001, new[] { "th" }, weight: 1.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PackLine("x", Pool("click.work"), 1001, new[] { "th" }, weight: double.NaN));
        }

        // ---- The pick ----

        [Fact]
        public void With_nothing_offered_the_game_picks()
        {
            Assert.Null(PackLineWeights.Roll(null, null, 0d, 0d));
            Assert.Null(PackLineWeights.Roll(Array.Empty<PackLineCandidate>(), null, 0d, 0d));
        }

        [Fact]
        public void A_candidate_clamps_its_weight_to_0_to_1()
        {
            var line = Line("a");

            Assert.Equal(1d, new PackLineCandidate(line, 1.5).Weight);
            Assert.Equal(0d, new PackLineCandidate(line, -1d).Weight);
            Assert.Equal(0d, new PackLineCandidate(line, double.NaN).Weight);
            Assert.Throws<ArgumentNullException>(() => new PackLineCandidate(null!, 0.5));
        }

        [Fact]
        public void A_line_at_weight_0_never_plays()
        {
            var zero = Line("zero");
            var other = Line("other");

            Assert.Null(PackLineWeights.Roll(Offer((zero, 0d)), null, 0d, 0d));
            for (var pick = 0d; pick < 1d; pick += 0.01)
            {
                Assert.Same(other, PackLineWeights.Roll(Offer((zero, 0d), (other, 0.5)), null, 0d, pick));
            }
        }

        [Fact]
        public void The_chance_that_a_pack_line_takes_the_pick_is_the_sum_of_the_weights_capped_at_1()
        {
            var a = Line("a");
            var b = Line("b");

            Assert.NotNull(PackLineWeights.Roll(Offer((a, 0.25), (b, 0.25)), null, 0.4999, 0d));
            Assert.Null(PackLineWeights.Roll(Offer((a, 0.25), (b, 0.25)), null, 0.5, 0d));
            Assert.NotNull(PackLineWeights.Roll(Offer((a, 0.75), (b, 0.75)), null, 0.9999, 0d));
        }

        [Fact]
        public void Two_lines_at_weight_1_split_every_pick_50_50()
        {
            var a = Line("a");
            var b = Line("b");
            var both = Offer((a, 1d), (b, 1d));

            Assert.Same(a, PackLineWeights.Roll(both, null, 0.9999, 0.4999));
            Assert.Same(b, PackLineWeights.Roll(both, null, 0.9999, 0.5));
        }

        [Fact]
        public void Which_line_follows_the_weights()
        {
            var a = Line("a");
            var b = Line("b");
            var offer = Offer((a, 0.1), (b, 0.3));

            // a holds the first quarter of the 0.4 total, b the rest.
            Assert.Same(a, PackLineWeights.Roll(offer, null, 0d, 0d));
            Assert.Same(a, PackLineWeights.Roll(offer, null, 0d, 0.24));
            Assert.Same(b, PackLineWeights.Roll(offer, null, 0d, 0.26));
            Assert.Same(b, PackLineWeights.Roll(offer, null, 0d, 0.9999));
        }

        [Fact]
        public void The_line_that_played_last_sits_the_next_pick_out()
        {
            var a = Line("a");
            var b = Line("b");
            var offer = Offer((a, 0.5), (b, 0.5));

            Assert.Same(b, PackLineWeights.Roll(offer, "a", 0d, 0d));
            Assert.Same(b, PackLineWeights.Roll(offer, "a", 0.4999, 0.9999));
            Assert.Null(PackLineWeights.Roll(offer, "a", 0.5, 0d));
            Assert.Same(a, PackLineWeights.Roll(offer, "b", 0d, 0.9999));
        }

        [Fact]
        public void The_last_line_still_plays_when_it_is_the_only_candidate_or_its_weight_is_1()
        {
            var a = Line("a");
            var b = Line("b");

            Assert.Same(a, PackLineWeights.Roll(Offer((a, 0.5)), "a", 0d, 0d));
            Assert.Same(a, PackLineWeights.Roll(Offer((a, 1d), (b, 0.5)), "a", 0d, 0d));
            Assert.Same(b, PackLineWeights.Roll(Offer((a, 1d), (b, 0.5)), "a", 0d, 0.9999));
        }

        [Fact]
        public void No_repeat_compares_the_group_the_pack_wrote_so_a_copy_counts_as_its_source()
        {
            var copy = Line("cwy_hello@gamestart.sameday", "cwy_hello", PackLinePools.AnyGameStart);
            var other = Line("cwy_other");

            Assert.Same(other, PackLineWeights.Roll(Offer((copy, 0.5), (other, 0.5)), "cwy_hello", 0d, 0d));
            Assert.Same(copy, PackLineWeights.Roll(Offer((copy, 0.5), (other, 0.5)), "cwy_hello@gamestart.sameday", 0d, 0d));
        }

        [Fact]
        public void Over_many_seeded_draws_each_line_plays_at_its_weight()
        {
            var a = Line("a");
            var b = Line("b");
            var c = Line("c");
            var offer = Offer((a, 0.25), (b, 0.1), (c, 0.05));
            var random = new Random(20261031);
            var counts = new Dictionary<string, int> { ["a"] = 0, ["b"] = 0, ["c"] = 0, ["game"] = 0 };
            const int Draws = 200000;

            for (var i = 0; i < Draws; i++)
            {
                var line = PackLineWeights.Roll(offer, null, random.NextDouble(), random.NextDouble());
                counts[line?.GroupId ?? "game"]++;
            }

            Assert.InRange(counts["a"] / (double)Draws, 0.24, 0.26);
            Assert.InRange(counts["b"] / (double)Draws, 0.09, 0.11);
            Assert.InRange(counts["c"] / (double)Draws, 0.04, 0.06);
            Assert.InRange(counts["game"] / (double)Draws, 0.59, 0.61);
        }

        [Fact]
        public void Over_many_seeded_draws_no_line_plays_twice_in_a_row_and_each_keeps_close_to_its_weight()
        {
            // "Last" here is the previous pick of the pool: a game pick in between clears it. With
            // that, a line of weight w that sits out the pick after itself plays at w / (1 + w).
            var a = Line("a");
            var b = Line("b");
            var c = Line("c");
            var offer = Offer((a, 0.25), (b, 0.1), (c, 0.05));
            var random = new Random(31102026);
            var counts = new Dictionary<string, int> { ["a"] = 0, ["b"] = 0, ["c"] = 0, ["game"] = 0 };
            string? last = null;
            const int Draws = 200000;

            for (var i = 0; i < Draws; i++)
            {
                var line = PackLineWeights.Roll(offer, last, random.NextDouble(), random.NextDouble());
                if (line != null)
                {
                    Assert.NotEqual(last, line.SourceGroupId);
                }

                last = line?.SourceGroupId;
                counts[line?.GroupId ?? "game"]++;
            }

            Assert.InRange(counts["a"] / (double)Draws, 0.25 / 1.25 - 0.01, 0.25 / 1.25 + 0.01);
            Assert.InRange(counts["b"] / (double)Draws, 0.1 / 1.1 - 0.01, 0.1 / 1.1 + 0.01);
            Assert.InRange(counts["c"] / (double)Draws, 0.05 / 1.05 - 0.01, 0.05 / 1.05 + 0.01);
        }

        [Fact]
        public void An_unweighted_line_without_a_deck_gets_an_equal_share_with_the_games_lines()
        {
            Assert.Equal(1d / 18d, PackLineWeights.EqualShare(17, 1));
            Assert.Equal(1d / 20d, PackLineWeights.EqualShare(17, 3));
            Assert.Equal(0.5, PackLineWeights.EqualShare(1, 1));
            Assert.Equal(0.5, PackLineWeights.EqualShare(-5, 2));
            Assert.Equal(1d, PackLineWeights.EqualShare(0, 1));
            Assert.Equal(0d, PackLineWeights.EqualShare(0, 0));
        }

        [Fact]
        public void Two_unweighted_greetings_in_the_same_day_pool_take_two_picks_in_nineteen()
        {
            var a = Line("a");
            var b = Line("b");
            var share = PackLineWeights.EqualShare(Pool("gamestart.sameday").VanillaCount, 2);
            var offer = Offer((a, share), (b, share));

            Assert.NotNull(PackLineWeights.Roll(offer, null, 2d / 19d - 1e-9, 0d));
            Assert.Null(PackLineWeights.Roll(offer, null, 2d / 19d, 0d));
        }

        // ---- The last-played file ----

        [Fact]
        public void Every_startup_pool_shares_one_last_line_and_other_pools_keep_their_own()
        {
            foreach (var pool in PackLinePools.All)
            {
                Assert.Equal(pool.Family == PackLineFamily.GameStart ? "gamestart" : pool.Id, PackLineLastPlayed.KeyOf(pool));
            }

            Assert.Throws<ArgumentNullException>(() => PackLineLastPlayed.KeyOf(null!));
        }

        [Fact]
        public void The_last_played_file_round_trips_with_sorted_keys()
        {
            var last = new Dictionary<string, string>
            {
                ["gamestart"] = "cwy_th_hello",
                ["click.work"] = "cwy_th_\"quoted\"\\slash",
            };

            var json = PackLineLastPlayed.ToJson(last);
            var back = PackLineLastPlayed.Parse(json, out var problem);

            Assert.Equal(string.Empty, problem);
            Assert.Equal(last.OrderBy(p => p.Key, StringComparer.Ordinal), back.OrderBy(p => p.Key, StringComparer.Ordinal));
            Assert.True(json.IndexOf("\"click.work\"", StringComparison.Ordinal) < json.IndexOf("\"gamestart\"", StringComparison.Ordinal));
            Assert.Equal(json, PackLineLastPlayed.ToJson(back));
            Assert.Equal("{\"schema\":1,\"last\":{}}\n", PackLineLastPlayed.ToJson(null));
            Assert.Empty(PackLineLastPlayed.Parse(PackLineLastPlayed.ToJson(new Dictionary<string, string>()), out problem));
            Assert.Equal(string.Empty, problem);
        }

        [Theory]
        [InlineData("not json", "not a JSON object")]
        [InlineData("[\"gamestart\"]", "not a JSON object")]
        [InlineData("{\"schema\":1}", "no \"last\" object")]
        [InlineData("{\"schema\":1,\"last\":[\"a\"]}", "no \"last\" object")]
        public void A_malformed_last_played_file_reads_as_empty_with_a_reason(string json, string expected)
        {
            var last = PackLineLastPlayed.Parse(json, out var problem);

            Assert.Empty(last);
            Assert.StartsWith(expected, problem);
        }

        [Fact]
        public void A_missing_file_reads_as_empty_and_entries_that_are_not_ids_are_skipped()
        {
            Assert.Empty(PackLineLastPlayed.Parse(null, out var problem));
            Assert.Equal(string.Empty, problem);
            Assert.Empty(PackLineLastPlayed.Parse("  ", out problem));
            Assert.Equal(string.Empty, problem);

            var last = PackLineLastPlayed.Parse("{\"last\":{\"gamestart\":\"a\",\"click.work\":5,\"click.break\":\"\",\"smalltalk\":null}}", out problem);

            Assert.Equal(string.Empty, problem);
            Assert.Equal("a", Assert.Single(last).Value);
        }
            [Theory]
        [InlineData("None", null)]
        [InlineData("PurchaseEnvironment", null)]
        [InlineData("SmallTalk_Cicada", "a weighted small talk needs ContentType \"None\" or \"PurchaseEnvironment\"")]
        [InlineData("SmallTalk_CatHeadphone", "a weighted small talk needs ContentType \"None\" or \"PurchaseEnvironment\"")]
        public void A_weighted_small_talk_takes_only_the_content_types_whose_conditions_the_roll_applies(string content, string? refusal)
        {
            var group = new ScenarioGroupRow
            {
                Id = "cwy_th_small", Scenario = 23, EpisodeNumber = 1001, Languages = new[] { "th" }, Weight = 0.5,
                PlayTiming = "Break", ContentType = content, Arg1 = "25", Arg2 = "5", Arg3 = "3", UnlockStoryEpisode = "None"
            };
            var set = PackLineRows.Set(new[] { group }, new[] { PackLineRows.Spoken("s1", "cwy_th_small") }, new[] { PackLineRows.Line("s1", "Voice_S1") });

            var result = PackLineScreen.Screen(set, Game, admit: true);

            if (refusal is null)
            {
                Assert.Empty(result.Rejected);
                Assert.Equal(0.5, Assert.Single(result.Lines).Weight);
            }
            else
            {
                Assert.StartsWith(refusal, Assert.Single(result.Rejected).Reason);
            }
        }

        [Fact]
        public void The_pool_header_counts_only_unweighted_lines_as_deck_cards()
        {
            Assert.Equal("game 1-5 + 1 pack: each line once per 6-pick cycle; 2 weighted: roll at each pick, outside the deck",
                PackLineReport.Weight(Pool("click.work.morning"), 1, 2));
            Assert.Equal("game 1-5 + 2 pack: each line once per 7-pick cycle", PackLineReport.Weight(Pool("click.work.morning"), 2));
        }
    }
}
