using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Pack lines, requirements 2 and 3: the gate on the game's <c>RandomList</c> pools. The game's
    /// list is modelled line for line on its <c>RandomList</c> (game 1.17.3), with the two
    /// hooks the adapter adds (a reconcile before each pick, a new cycle after <c>InitList</c>).
    /// </summary>
    public class PackLinesGateTests
    {
        private static readonly Func<int, bool> Closed = _ => false;
        private static readonly Func<int, bool> Open = _ => true;

        [Fact]
        public void A_closed_gate_leaves_the_list_alone_and_draws_no_random_numbers()
        {
            var list = new List<int> { 4, 2, 5, 1, 3 };
            var state = new PackLinePoolState(new[] { 1001, 1002 });
            var draws = 0;

            var result = state.Reconcile(list, Closed, n => { draws++; return 0; });

            Assert.Equal(new[] { 4, 2, 5, 1, 3 }, list);
            Assert.Equal(0, draws);
            Assert.False(result.Changed);
            Assert.Equal(2, state.OwedCount);
        }

        [Fact]
        public void An_open_gate_inserts_each_owed_line_once_and_never_after_the_last_element()
        {
            var list = new List<int> { 4, 2, 5, 1, 3 };
            var state = new PackLinePoolState(new[] { 1001, 1002 });

            var result = state.Reconcile(list, Open, n => n - 1);

            Assert.Equal(2, result.Placed);
            Assert.Equal(7, list.Count);
            Assert.Equal(3, list[list.Count - 1]);
            Assert.Equal(1, list.Count(v => v == 1001));
            Assert.Equal(1, list.Count(v => v == 1002));
        }

        [Fact]
        public void An_out_of_range_index_is_clamped_in_front_of_the_last_element()
        {
            var list = new List<int> { 7 };
            var state = new PackLinePoolState(new[] { 1001 });

            state.Reconcile(list, Open, n => 99);

            Assert.Equal(new[] { 1001, 7 }, list);
        }

        [Fact]
        public void An_empty_list_is_left_for_the_game_to_start_a_new_cycle()
        {
            var list = new List<int>();
            var state = new PackLinePoolState(new[] { 1001 });

            state.Reconcile(list, Open, n => 0);

            Assert.Empty(list);
            Assert.Equal(1, state.OwedCount);
        }

        [Fact]
        public void A_line_the_game_used_is_not_offered_again_in_the_same_cycle()
        {
            var list = new List<int> { 1, 2 };
            var state = new PackLinePoolState(new[] { 1001 });
            state.Reconcile(list, Open, n => 0);
            Assert.Equal(1001, list[0]);

            list.RemoveAt(0); // RandomList.UseNext
            var result = state.Reconcile(list, Open, n => 0);

            Assert.Equal(1, result.Consumed);
            Assert.Equal(new[] { 1, 2 }, list);
            Assert.Equal(0, state.OwedCount);
        }

        [Fact]
        public void Switching_language_mid_cycle_takes_lines_out_and_brings_back_only_the_unplayed_ones()
        {
            var list = new List<int> { 1, 2, 3 };
            var state = new PackLinePoolState(new[] { 1001, 1002 });
            state.Reconcile(list, Open, n => 0);
            list.Remove(1002); // played (RemoveFromList)

            var off = state.Reconcile(list, Closed, n => 0);
            Assert.Equal(1, off.Consumed);
            Assert.Equal(1, off.Withdrawn);
            Assert.Equal(new[] { 1, 2, 3 }, list);

            var on = state.Reconcile(list, Open, n => 0);
            Assert.Equal(1, on.Placed);
            Assert.Equal(new[] { 1001, 1, 2, 3 }, list);
        }

        [Fact]
        public void Lines_of_different_languages_in_one_pool_are_gated_one_by_one()
        {
            var list = new List<int> { 1, 2 };
            var state = new PackLinePoolState(new[] { 1001, 1002 });
            var thai = new HashSet<int> { 1001 };

            state.Reconcile(list, thai.Contains, n => 0);

            Assert.Contains(1001, list);
            Assert.DoesNotContain(1002, list);
        }

        [Fact]
        public void A_new_cycle_owes_every_line_again()
        {
            var list = new List<int> { 1 };
            var state = new PackLinePoolState(new[] { 1001 });
            state.Reconcile(list, Open, n => 0);
            list.Clear(); // both played

            state.Reconcile(list, Open, n => 0);
            Assert.Equal(0, state.OwedCount);

            list.Add(1); // RandomList.InitList
            state.StartCycle();
            state.Reconcile(list, Open, n => 0);
            Assert.Equal(new[] { 1001, 1 }, list);
        }

        [Fact]
        public void A_pack_episode_found_in_the_list_is_adopted_and_follows_the_gate()
        {
            var list = new List<int> { 1, 1001, 2 };
            var state = new PackLinePoolState(new[] { 1001 });

            var result = state.Reconcile(list, Closed, n => 0);

            Assert.Equal(1, result.Withdrawn);
            Assert.Equal(new[] { 1, 2 }, list);
            Assert.True(state.IsOwed(1001));
        }

        [Fact]
        public void Rebinding_takes_the_old_lines_out_and_owes_the_new_ones()
        {
            var list = new List<int> { 1, 2 };
            var state = new PackLinePoolState(new[] { 1001 });
            state.Reconcile(list, Open, n => 0);

            state.Rebind(new[] { 1005 }, list);

            Assert.Equal(new[] { 1, 2 }, list);
            Assert.False(state.IsPackEpisode(1001));
            Assert.True(state.IsOwed(1005));
            Assert.Equal(0, state.PlacedCount);
        }

        [Fact]
        public void Rebinding_to_its_own_episodes_keeps_them_and_clears_the_list()
        {
            var list = new List<int> { 1, 2 };
            var state = new PackLinePoolState(new[] { 1001, 1002 });
            state.Reconcile(list, Open, n => 0);

            state.Rebind(state.Episodes, list);

            Assert.Equal(new[] { 1001, 1002 }, state.Episodes);
            Assert.Equal(new[] { 1, 2 }, list);
            Assert.Equal(2, state.OwedCount);
        }

        [Fact]
        public void A_clone_runs_the_gate_without_touching_the_live_bookkeeping()
        {
            var list = new List<int> { 1, 2 };
            var state = new PackLinePoolState(new[] { 1001 });

            var copy = new List<int>(list);
            state.Clone().Reconcile(copy, Open, n => 0);

            Assert.Contains(1001, copy);
            Assert.Equal(0, state.PlacedCount);
            Assert.Equal(new[] { 1, 2 }, list);
        }

        [Fact]
        public void With_no_line_offered_the_game_draws_exactly_its_own_sequence()
        {
            var vanilla = Draw(new GameList(1, 23, new Random(7)), null, Closed, 200);
            var hooked = Draw(new GameList(1, 23, new Random(7)), new PackLinePoolState(new[] { 1001, 1002, 1003 }), Closed, 200);

            Assert.Equal(vanilla, hooked);
        }

        [Fact]
        public void With_lines_offered_every_line_plays_once_per_cycle_and_never_twice_in_a_row()
        {
            var game = new GameList(1, 5, new Random(11));
            var state = new PackLinePoolState(new[] { 1001, 1002 });
            var picks = Draw(game, state, Open, 7 * 40, seed: 3);

            for (var cycle = 0; cycle < 40; cycle++)
            {
                var slice = picks.Skip(cycle * 7).Take(7).OrderBy(v => v).ToArray();
                Assert.Equal(new[] { 1, 2, 3, 4, 5, 1001, 1002 }, slice);
            }

            for (var i = 1; i < picks.Count; i++)
            {
                Assert.NotEqual(picks[i - 1], picks[i]);
            }
        }

        [Fact]
        public void A_cycle_always_ends_on_a_game_line()
        {
            var game = new GameList(1, 3, new Random(5));
            var state = new PackLinePoolState(new[] { 1001, 1002, 1003 });
            var picks = Draw(game, state, Open, 6 * 50, seed: 9);

            for (var cycle = 0; cycle < 50; cycle++)
            {
                Assert.True(picks[cycle * 6 + 5] < 1000, "cycle " + cycle + " ended on a pack line");
            }
        }

        [Fact]
        public void Switching_away_mid_cycle_gives_back_the_games_own_remaining_order()
        {
            var vanillaGame = new GameList(1, 10, new Random(21));
            var vanilla = new List<int>();
            for (var i = 0; i < 10; i++)
            {
                vanilla.Add(vanillaGame.Take(null, Closed, null));
            }

            var game = new GameList(1, 10, new Random(21));
            var state = new PackLinePoolState(new[] { 1001, 1002 });
            var rng = new Random(1);
            var picks = new List<int>();
            for (var i = 0; i < 4; i++)
            {
                picks.Add(game.Take(state, Open, rng));
            }

            while (picks.Count(v => v < 1000) < 10)
            {
                picks.Add(game.Take(state, Closed, rng));
            }

            Assert.Equal(vanilla, picks.Where(v => v < 1000).ToList());
        }

        private static List<int> Draw(GameList game, PackLinePoolState? state, Func<int, bool> gate, int count, int seed = 1)
        {
            var rng = new Random(seed);
            var picks = new List<int>(count);
            for (var i = 0; i < count; i++)
            {
                picks.Add(game.Take(state, gate, rng));
            }

            return picks;
        }

        /// <summary>RandomList as the game ships it (RandomList.cs), plus the adapter's two hooks.</summary>
        private sealed class GameList
        {
            private readonly List<int> _list = new List<int>();
            private readonly int _min;
            private readonly int _max;
            private readonly Random _shuffle;
            private int _beforeLast = -1;

            internal GameList(int min, int max, Random shuffle)
            {
                _min = min;
                _max = max;
                _shuffle = shuffle;
                InitList(null, null, null);
            }

            /// <summary>One pick: the gate (FacilityClickHeroine.OneWord prefix), GetNext, UseNext.</summary>
            internal int Take(PackLinePoolState? state, Func<int, bool>? gate, Random? rng)
            {
                state?.Reconcile(_list, gate!, n => rng!.Next(n));
                if (_list.Count == 0)
                {
                    InitList(state, gate, rng);
                }

                var next = _list[0];
                _list.RemoveAt(0);
                return next;
            }

            private void InitList(PackLinePoolState? state, Func<int, bool>? gate, Random? rng)
            {
                for (var i = _min; i <= _max; i++)
                {
                    _list.Add(i);
                }

                // ShuffleUtils.Shuffle: Fisher-Yates.
                for (var i = _list.Count - 1; i > 0; i--)
                {
                    var j = _shuffle.Next(i + 1);
                    (_list[i], _list[j]) = (_list[j], _list[i]);
                }

                if (_list[0] == _beforeLast)
                {
                    _list.RemoveAt(0);
                    _list.Add(_beforeLast);
                }

                _beforeLast = _list[_list.Count - 1];

                // The InitList postfix.
                if (state != null)
                {
                    state.StartCycle();
                    state.Reconcile(_list, gate!, n => rng!.Next(n));
                }
            }
        }
    }
}
