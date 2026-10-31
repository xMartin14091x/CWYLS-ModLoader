using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Startup greetings: what the game-side pick (<c>Bulbul/PackLines/PackLinesGameStart.cs</c>, not
    /// testable in-process) relies on from Core - which pool a <c>GetGameStartTalk(ScenarioType)</c>
    /// call reaches, the shared no-repeat key, and the roll composed the way the pick composes it
    /// (a line's <c>Weight</c>, else an equal share with the game's lines).
    /// </summary>
    public class PackLinesGameStartTests
    {
        /// <summary>The pool the game-side pick takes for a scenario: the one GameStart pool of that value, or none.</summary>
        private static PackLinePool? StartupPoolOf(string scenarioName)
        {
            Assert.True(MasterDataEnums.Scenario.TryParseName(scenarioName, out var value), scenarioName);
            PackLinePool? found = null;
            foreach (var pool in PackLinePools.ForScenario(value))
            {
                if (pool.Family == PackLineFamily.GameStart)
                {
                    Assert.Null(found);
                    found = pool;
                }
            }

            return found;
        }

        /// <summary>The candidates as the game-side pick builds them.</summary>
        private static List<PackLineCandidate> Candidates(PackLinePool pool, params PackLine[] lines)
        {
            var unweighted = 0;
            foreach (var line in lines)
            {
                if (!line.IsWeighted)
                {
                    unweighted++;
                }
            }

            var share = PackLineWeights.EqualShare(pool.VanillaCount, unweighted);
            var candidates = new List<PackLineCandidate>();
            foreach (var line in lines)
            {
                candidates.Add(new PackLineCandidate(line, line.Weight ?? share));
            }

            return candidates;
        }

        private static PackLine Line(PackLinePool pool, string source, int episode, double? weight) =>
            new PackLine(source + "@" + pool.Id, pool, episode, new[] { "th" }, new[] { "Voice_" + source }, weight, source, PackLinePools.AnyGameStart);

        [Theory]
        [InlineData("GameStart_LessTowDays", "gamestart.sameday")]
        [InlineData("GameStart_LessTowDays_CameraTouch", "gamestart.newday")]
        [InlineData("GameStart_LessTowDays_CameraTouch_Morning", "gamestart.newday.morning")]
        [InlineData("GameStart_LessTowDays_CameraTouch_Noon", "gamestart.newday.noon")]
        [InlineData("GameStart_LessTowDays_CameraTouch_Evening", "gamestart.newday.evening")]
        [InlineData("GameStart_LessTowDays_CameraTouch_Night", "gamestart.newday.night")]
        [InlineData("GameStart_LessHarfMonth_CameraTouch", "gamestart.days3to14")]
        [InlineData("GameStart_GreaterHarfMonth_CameraTouch", "gamestart.days15to30")]
        [InlineData("GameStart_GreaterMonth_CameraTouch", "gamestart.days31plus")]
        [InlineData("GameStart_GreaterYear_AND_PlayOneHundred_CameraTouch", "gamestart.login100")]
        public void Every_greeting_kind_the_game_asks_for_reaches_exactly_one_startup_pool(string scenarioName, string poolId)
        {
            var pool = StartupPoolOf(scenarioName);

            Assert.NotNull(pool);
            Assert.Equal(poolId, pool!.Id);
            Assert.False(pool.HasDeck);
            Assert.Equal(PackLineLastPlayed.GameStartKey, PackLineLastPlayed.KeyOf(pool));
        }

        [Theory]
        [InlineData("GameStart_First_CameraTouch")]
        [InlineData("Photograph_GameStart_CameraTouch")]
        [InlineData("Event_2025_Christmas_GameStart")]
        public void The_first_launch_photo_mode_and_the_event_opening_are_no_startup_pool(string scenarioName)
        {
            Assert.Null(StartupPoolOf(scenarioName));
        }

        [Fact]
        public void A_weight_one_line_alone_in_its_pool_takes_every_launch_even_right_after_itself()
        {
            // The launch-video hook: "Pool": "gamestart.any", "Weight": 1, the only startup line offered.
            foreach (var pool in PackLinePools.All)
            {
                if (pool.Family != PackLineFamily.GameStart)
                {
                    continue;
                }

                var hook = Line(pool, "hook", 1001, 1d);
                var candidates = Candidates(pool, hook);
                foreach (var last in new[] { null, "hook", "other" })
                {
                    for (var i = 0; i <= 20; i++)
                    {
                        var roll = Math.Min(i / 20d, 0.999999d);
                        Assert.Same(hook, PackLineWeights.Roll(candidates, last, roll, roll));
                    }
                }
            }
        }

        [Fact]
        public void A_weight_one_line_is_not_certain_when_another_startup_line_is_offered_in_its_pool()
        {
            // Pack authors' caveat: an unweighted line in the same pool still rolls its equal share
            // (1 / (12 game lines + 1)), so the Weight 1 line takes 1 / (1 + 1/13) of the launches.
            Assert.True(PackLinePools.TryGet("gamestart.newday", out var pool));
            var hook = Line(pool!, "hook", 1001, 1d);
            var other = Line(pool!, "other", 1002, null);
            var candidates = Candidates(pool!, hook, other);

            Assert.Equal(1d / 13d, candidates[1].Weight, 12);
            Assert.Same(hook, PackLineWeights.Roll(candidates, null, 0.999d, 0.5d));
            Assert.Same(other, PackLineWeights.Roll(candidates, null, 0.5d, 0.99d));
        }

        [Fact]
        public void An_unweighted_startup_line_rolls_an_equal_share_with_the_game_lines_of_its_kind()
        {
            Assert.True(PackLinePools.TryGet("gamestart.sameday", out var pool));
            var line = Line(pool!, "hello", 1001, null);
            var candidates = Candidates(pool!, line);

            // 17 game greetings + 1: the pack line takes the launch when the roll is under 1/18.
            Assert.Equal(1d / 18d, candidates[0].Weight, 12);
            Assert.Same(line, PackLineWeights.Roll(candidates, null, 0.05d, 0d));
            Assert.Null(PackLineWeights.Roll(candidates, null, 0.06d, 0d));
        }
    }
}
