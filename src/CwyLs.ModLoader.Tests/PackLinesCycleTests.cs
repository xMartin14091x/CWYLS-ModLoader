using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// One deck for game and pack lines across sessions: all of them in one pool, one line drawn at a
    /// time. The game keeps its RandomList cycle in the save; the pack lines were owed again at every
    /// launch, so a pack line came once per session and a game line once per cycle.
    /// </summary>
    public class PackLinesCycleTests
    {
        private static readonly int[] Pack = { 1001, 1002, 1003, 1004, 1005, 1006 };

        [Fact]
        public void The_file_round_trips()
        {
            var pools = new Dictionary<string, PackLineCycleRecord>
            {
                ["click.normal"] = new PackLineCycleRecord(12, 20, new[] { 1003, 1001 }),
                ["click.break"] = new PackLineCycleRecord(PackLineCycles.Unknown, 3, null),
            };

            var back = PackLineCycles.Parse(PackLineCycles.ToJson(pools), out var problem);
            Assert.Equal(string.Empty, problem);
            Assert.Equal(new[] { 1001, 1003 }, back["click.normal"].Played);
            Assert.Equal(12, back["click.normal"].BeforeLast);
            Assert.Equal(20, back["click.normal"].GameLeft);
            Assert.Equal(PackLineCycles.Unknown, back["click.break"].BeforeLast);
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("{\"schema\":1}")]
        public void A_broken_file_starts_empty_with_a_reason(string text)
        {
            Assert.Empty(PackLineCycles.Parse(text, out var problem));
            Assert.NotEqual(string.Empty, problem);
        }

        [Theory]
        [InlineData(12, 20, true)]   // same cycle, nothing dealt since
        [InlineData(12, 15, true)]   // same cycle, game lines dealt since
        [InlineData(12, 31, false)]  // refilled: a new cycle
        [InlineData(7, 15, false)]   // a different last element: a new cycle
        public void The_same_cycle_is_recognised_by_its_last_element_and_a_shrinking_count(int beforeLast, int gameLeft, bool same) =>
            Assert.Equal(same, PackLineCycles.IsSameCycle(new PackLineCycleRecord(12, 20, null), beforeLast, gameLeft));

        [Fact]
        public void No_record_is_never_the_same_cycle() =>
            Assert.False(PackLineCycles.IsSameCycle(null, 12, 3));

        [Fact]
        public void Lines_dealt_in_this_cycle_wait_for_the_next_one_after_a_restart()
        {
            var list = Enumerable.Range(1, 30).ToList();
            var first = new PackLinePoolState(Pack);
            first.Reconcile(list, _ => true, _ => 0); // dealt first, so the ten picks below reach them
            Deal(list, 10);
            first.Reconcile(list, _ => true, n => n - 1);
            var played = first.PlayedThisCycle;
            Assert.NotEmpty(played);

            // Saving strips our episodes; the next launch binds a fresh state to the saved list.
            list.RemoveAll(e => e >= 1000);
            var second = new PackLinePoolState(Pack);
            Assert.Equal(played.Count, second.RestorePlayed(played));
            second.Reconcile(list, _ => true, n => 0);
            foreach (var episode in played)
            {
                Assert.DoesNotContain(episode, list);
            }

            second.StartCycle();
            Assert.Empty(second.PlayedThisCycle);
        }

        [Fact]
        public void Over_many_short_sessions_every_line_plays_as_often_as_a_game_line()
        {
            // 30 game lines + 6 pack lines, one pick per session, 360 sessions = 10 cycles.
            var random = new Random(20261003);
            var gameList = new List<int>();
            int beforeLast = -1;
            var counts = new Dictionary<int, int>();
            PackLineCycleRecord? stored = null;

            for (var session = 0; session < 360; session++)
            {
                if (gameList.Count == 0)
                {
                    gameList = Enumerable.Range(1, 30).OrderBy(_ => random.Next()).ToList();
                    beforeLast = gameList[gameList.Count - 1];
                }

                var state = new PackLinePoolState(Pack);
                var gameLeft = gameList.Count(e => e < 1000);
                if (PackLineCycles.IsSameCycle(stored, beforeLast, gameLeft))
                {
                    state.RestorePlayed(stored!.Played);
                }
                else
                {
                    state.StartCycle();
                }

                state.Reconcile(gameList, _ => true, n => random.Next(n));
                var pick = gameList[0];
                gameList.RemoveAt(0);
                counts[pick] = counts.TryGetValue(pick, out var c) ? c + 1 : 1;
                state.Reconcile(gameList, _ => true, n => random.Next(n));

                stored = new PackLineCycleRecord(beforeLast, gameList.Count(e => e < 1000), state.PlayedThisCycle);
                gameList.RemoveAll(e => e >= 1000); // the save strips our episodes
            }

            // Every line, game or pack, came up about 10 times (once per cycle).
            foreach (var episode in Enumerable.Range(1, 30).Concat(Pack))
            {
                Assert.InRange(counts.TryGetValue(episode, out var n) ? n : 0, 8, 12);
            }
        }

        private static void Deal(List<int> list, int count)
        {
            for (var i = 0; i < count && list.Count > 0; i++)
            {
                list.RemoveAt(0);
            }
        }
    }
}
