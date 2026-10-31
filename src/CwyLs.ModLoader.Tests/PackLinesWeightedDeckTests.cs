using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Weighted lines in the deck pools (phase 2): never dealt into the game's <c>RandomList</c>, put
    /// in front for the one pick they won (<see cref="PackLinePoolState.BeginPick"/>) and taken back
    /// when the game read another list (<see cref="PackLinePoolState.EndPick"/>). The game's list is
    /// modelled line for line on its <c>RandomList</c> (game 1.18.0), with the adapter's hooks: the
    /// gate before a pick, the <c>InitList</c> postfix, and the pick's finalizer.
    /// </summary>
    public class PackLinesWeightedDeckTests
    {
        private static readonly Func<int, bool> Closed = _ => false;
        private static readonly Func<int, bool> Open = _ => true;

        [Fact]
        public void A_weighted_line_is_never_dealt_into_the_deck()
        {
            var list = new List<int> { 4, 2, 5, 1, 3 };
            var state = new PackLinePoolState(new[] { 1001 }, new[] { 2001, 2002 });

            state.Reconcile(list, Open, n => 0);

            Assert.Contains(1001, list);
            Assert.DoesNotContain(2001, list);
            Assert.DoesNotContain(2002, list);
            Assert.True(state.IsPackEpisode(2001));
            Assert.True(state.IsWeighted(2001));
            Assert.False(state.IsWeighted(1001));
            Assert.Equal(new[] { 1001 }, state.Episodes);
            Assert.Equal(new[] { 2001, 2002 }, state.WeightedEpisodes);
        }

        [Fact]
        public void An_episode_given_as_both_dealt_and_weighted_counts_as_dealt()
        {
            var state = new PackLinePoolState(new[] { 1001 }, new[] { 1001, 2001 });

            Assert.Equal(new[] { 1001 }, state.Episodes);
            Assert.Equal(new[] { 2001 }, state.WeightedEpisodes);
        }

        [Fact]
        public void With_weighted_lines_none_offered_the_game_draws_exactly_its_own_sequence()
        {
            var vanilla = new GameList(1, 23, new Random(7), null);
            var hooked = new GameList(1, 23, new Random(7), new PackLinePoolState(null, new[] { 2001, 2002 }));

            for (var i = 0; i < 200; i++)
            {
                vanilla.BeginPick(null);
                hooked.BeginPick(null); // no weighted line offered: no winner, nothing in front
                Assert.Equal(vanilla.Take(), hooked.Take());
                var end = hooked.EndPick();
                Assert.True(end.Read);
                Assert.False(end.Weighted);
                Assert.Equal(vanilla.Items, hooked.Items);
                Assert.Equal(vanilla.BeforeLast, hooked.BeforeLast);
            }
        }

        [Fact]
        public void A_won_pick_reads_the_weighted_line_and_the_games_own_order_and_last_element_are_untouched()
        {
            var vanilla = new GameList(1, 5, new Random(11), null);
            var vanillaPicks = new List<int>();
            var vanillaLasts = new List<int>();
            for (var i = 0; i < 200; i++)
            {
                vanillaPicks.Add(vanilla.Take());
                vanillaLasts.Add(vanilla.BeforeLast);
            }

            var hooked = new GameList(1, 5, new Random(11), new PackLinePoolState(new[] { 1001 }, new[] { 2001 }), Open, new Random(3));
            var wins = new Random(5);
            var gamePicks = new List<int>();
            var gameLasts = new List<int>();
            var weightedReads = 0;
            var winsHanded = 0;
            while (gamePicks.Count < 200)
            {
                var won = wins.NextDouble() < 0.3;
                winsHanded += won ? 1 : 0;
                hooked.BeginPick(won ? 2001 : (int?)null);
                var pick = hooked.Take();
                var end = hooked.EndPick();

                Assert.True(end.Read);
                Assert.Equal(pick, end.Episode);
                Assert.Equal(pick == 2001, end.Weighted);
                Assert.DoesNotContain(2001, hooked.Items);
                if (pick == 2001)
                {
                    weightedReads++;
                }
                else if (pick < 1000)
                {
                    gamePicks.Add(pick);
                    gameLasts.Add(hooked.BeforeLast);
                }
            }

            // A one-list pool is always read: every win plays, and the game's own episodes come in
            // exactly the order, and with exactly the cycle ends, they have without us.
            Assert.Equal(winsHanded, weightedReads);
            Assert.Equal(vanillaPicks, gamePicks);
            Assert.Equal(vanillaLasts, gameLasts);
        }

        [Fact]
        public void A_list_the_game_did_not_read_gets_its_weighted_line_back_and_is_left_as_it_was()
        {
            var list = new List<int> { 3, 1, 2 };
            var state = new PackLinePoolState(null, new[] { 2001 });

            state.BeginPick(list, 2001);
            Assert.Equal(new[] { 2001, 3, 1, 2 }, list);
            Assert.Equal(2001, state.Front);

            var end = state.EndPick(list);

            Assert.False(end.Read);
            Assert.False(end.Weighted);
            Assert.Equal(1, end.TakenBack);
            Assert.Equal(new[] { 3, 1, 2 }, list);
            Assert.False(state.InPick);
        }

        [Fact]
        public void A_removal_elsewhere_in_the_list_is_not_a_read()
        {
            // The two-list timer selectors remove the same line from their other mode's list
            // (RemoveDuplicationVoice, RemoveFromList): not a pick of that pool.
            var list = new List<int> { 3, 1, 2 };
            var state = new PackLinePoolState(null, new[] { 2001 });

            state.BeginPick(list, 2001);
            list.Remove(1);
            var end = state.EndPick(list);

            Assert.False(end.Read);
            Assert.Equal(new[] { 3, 2 }, list);
        }

        [Fact]
        public void An_empty_list_stays_empty_and_the_winner_waits_for_the_games_refill()
        {
            var list = new List<int>();
            var state = new PackLinePoolState(null, new[] { 2001 });

            state.BeginPick(list, 2001);
            Assert.Empty(list); // the game's emptiness checks see what they would see without us

            list.AddRange(new[] { 2, 3, 1 }); // RandomList.InitList inside GetNext
            state.StartCycle();
            state.Reconcile(list, Open, n => 0);
            state.OnCycleDuringPick(list);
            Assert.Equal(new[] { 2001, 2, 3, 1 }, list);

            list.RemoveAt(0); // GetNext + UseNext
            var end = state.EndPick(list);

            Assert.True(end.Read);
            Assert.True(end.Weighted);
            Assert.Equal(2001, end.Episode);
            Assert.Equal(new[] { 2, 3, 1 }, list);
        }

        [Fact]
        public void An_empty_list_the_game_did_not_refill_is_not_touched()
        {
            var list = new List<int>();
            var state = new PackLinePoolState(null, new[] { 2001 });

            state.BeginPick(list, 2001);
            var end = state.EndPick(list);

            Assert.False(end.Read);
            Assert.Equal(0, end.TakenBack);
            Assert.Empty(list);
        }

        [Fact]
        public void A_refill_outside_a_pick_puts_nothing_in_front()
        {
            var list = new List<int> { 2, 1 };
            var state = new PackLinePoolState(null, new[] { 2001 });

            state.OnCycleDuringPick(list);

            Assert.Equal(new[] { 2, 1 }, list);
        }

        [Fact]
        public void A_weighted_line_left_in_the_list_outside_a_pick_is_taken_out_by_the_gate()
        {
            var list = new List<int> { 2001, 1, 2 };
            var state = new PackLinePoolState(null, new[] { 2001 });
            var draws = 0;

            var result = state.Reconcile(list, Open, n => { draws++; return 0; });

            Assert.Equal(new[] { 1, 2 }, list);
            Assert.Equal(1, result.Withdrawn);
            Assert.Equal(0, draws);
        }

        [Fact]
        public void A_pick_that_never_ended_is_closed_by_the_next_one()
        {
            var list = new List<int> { 3, 1 };
            var state = new PackLinePoolState(null, new[] { 2001, 2002 });

            state.BeginPick(list, 2001);
            state.BeginPick(list, 2002);

            Assert.Equal(new[] { 2002, 3, 1 }, list);
        }

        [Fact]
        public void Rebinding_takes_weighted_lines_out_and_a_clone_keeps_them()
        {
            var list = new List<int> { 3, 1 };
            var state = new PackLinePoolState(new[] { 1001 }, new[] { 2001 });
            state.Reconcile(list, Open, n => 0);
            state.BeginPick(list, 2001);

            var clone = state.Clone();
            Assert.Equal(new[] { 2001 }, clone.WeightedEpisodes);
            Assert.False(clone.InPick);

            state.Rebind(new[] { 1005 }, new[] { 2005 }, list);

            Assert.Equal(new[] { 3, 1 }, list);
            Assert.False(state.InPick);
            Assert.True(state.IsWeighted(2005));
            Assert.False(state.IsPackEpisode(2001));
        }

        [Fact]
        public void The_two_argument_rebind_keeps_no_weighted_line()
        {
            var list = new List<int> { 2001, 3 };
            var state = new PackLinePoolState(new[] { 1001 }, new[] { 2001 });

            state.Rebind(state.Episodes, list);

            Assert.Equal(new[] { 3 }, list);
            Assert.Empty(state.WeightedEpisodes);
        }

        [Fact]
        public void In_a_two_list_pool_each_list_rolls_on_its_own_so_a_weight_is_the_chance_per_pick_of_that_pool()
        {
            // ReactionTalkSelector.GetNextWorkClickShortTalkSelection: the general list or the
            // time-of-day list, 50/50 (UnityEngine.Random in the game; a model generator here).
            Assert.True(PackLinePools.TryGet("click.work", out var general));
            Assert.True(PackLinePools.TryGet("click.work.morning", out var morning));
            var quarter = new PackLine("w_general", general!, 2001, new[] { "th" }, null, 0.25d);
            var half = new PackLine("w_morning", morning!, 2001, new[] { "th" }, null, 0.5d);

            var vanilla = new TwoLists(new Random(1), new Random(2), new Random(3), null, null);
            var hooked = new TwoLists(
                new Random(1),
                new Random(2),
                new Random(3),
                new PackLinePoolState(null, new[] { 2001 }),
                new PackLinePoolState(null, new[] { 2001 }));
            var rolls = new Random(9);
            string? lastGeneral = null;
            string? lastMorning = null;

            int generalReads = 0, generalWeighted = 0, morningReads = 0, morningWeighted = 0;
            for (var i = 0; i < 40000; i++)
            {
                var generalWin = PackLineWeights.Roll(new[] { new PackLineCandidate(quarter, 0.25d) }, lastGeneral, rolls.NextDouble(), rolls.NextDouble());
                var morningWin = PackLineWeights.Roll(new[] { new PackLineCandidate(half, 0.5d) }, lastMorning, rolls.NextDouble(), rolls.NextDouble());
                var (fromGeneral, episode) = hooked.Pick(generalWin?.Episode, morningWin?.Episode, out var generalEnd, out var morningEnd);

                Assert.DoesNotContain(2001, hooked.General.Items);
                Assert.DoesNotContain(2001, hooked.Morning.Items);
                Assert.True(generalEnd.Read != morningEnd.Read);
                if (fromGeneral)
                {
                    generalReads++;
                    generalWeighted += episode == 2001 ? 1 : 0;
                }
                else
                {
                    morningReads++;
                    morningWeighted += episode == 2001 ? 1 : 0;
                }
            }

            Assert.InRange(generalWeighted / (double)generalReads, 0.23d, 0.27d);
            Assert.InRange(morningWeighted / (double)morningReads, 0.48d, 0.52d);

            // The game's own episodes of each list come in the order they have without us.
            var vanillaGeneral = new List<int>();
            var vanillaMorning = new List<int>();
            for (var i = 0; i < 4000; i++)
            {
                var (fromGeneral, episode) = vanilla.Pick(null, null, out _, out _);
                (fromGeneral ? vanillaGeneral : vanillaMorning).Add(episode);
            }

            var n = Math.Min(vanillaGeneral.Count, hooked.GeneralGamePicks.Count);
            Assert.Equal(vanillaGeneral.Take(n), hooked.GeneralGamePicks.Take(n));
            var m = Math.Min(vanillaMorning.Count, hooked.MorningGamePicks.Count);
            Assert.Equal(vanillaMorning.Take(m), hooked.MorningGamePicks.Take(m));
        }

        /// <summary>
        /// The general and time-of-day lists of one two-list pool and the game's choice between them
        /// (ReactionTalkSelector), with the adapter's hooks on both lists.
        /// </summary>
        private sealed class TwoLists
        {
            private readonly Random _coin;

            internal TwoLists(Random generalShuffle, Random morningShuffle, Random coin, PackLinePoolState? generalState, PackLinePoolState? morningState)
            {
                General = new GameList(1, 23, generalShuffle, generalState);
                Morning = new GameList(1, 5, morningShuffle, morningState);
                _coin = coin;
            }

            internal GameList General { get; }

            internal GameList Morning { get; }

            internal List<int> GeneralGamePicks { get; } = new List<int>();

            internal List<int> MorningGamePicks { get; } = new List<int>();

            internal (bool FromGeneral, int Episode) Pick(int? generalWinner, int? morningWinner, out PackLinePickResult generalEnd, out PackLinePickResult morningEnd)
            {
                General.BeginPick(generalWinner);
                Morning.BeginPick(morningWinner);

                var isEmpty = General.IsEmpty;
                var isEmpty2 = Morning.IsEmpty;
                if (isEmpty & isEmpty2)
                {
                    General.ForceResetIfEmpty();
                    Morning.ForceResetIfEmpty();
                }

                bool fromGeneral;
                if ((isEmpty && !isEmpty2) || (!(!isEmpty & isEmpty2) && _coin.Next(100) < 50))
                {
                    fromGeneral = false;
                }
                else
                {
                    fromGeneral = true;
                }

                var episode = fromGeneral ? General.Take() : Morning.Take();
                generalEnd = General.EndPick();
                morningEnd = Morning.EndPick();
                if (episode < 1000)
                {
                    (fromGeneral ? GeneralGamePicks : MorningGamePicks).Add(episode);
                }

                return (fromGeneral, episode);
            }
        }

        /// <summary>RandomList as the game ships it (RandomList.cs), plus the adapter's hooks.</summary>
        private sealed class GameList
        {
            private readonly List<int> _list = new List<int>();
            private readonly int _min;
            private readonly int _max;
            private readonly Random _shuffle;
            private readonly PackLinePoolState? _state;
            private readonly Func<int, bool> _gate;
            private readonly Random _rng;

            internal GameList(int min, int max, Random shuffle, PackLinePoolState? state, Func<int, bool>? gate = null, Random? rng = null)
            {
                _min = min;
                _max = max;
                _shuffle = shuffle;
                _state = state;
                _gate = gate ?? Closed;
                _rng = rng ?? new Random(0);
                InitList();
            }

            internal List<int> Items => _list;

            internal int BeforeLast { get; private set; } = -1;

            internal bool IsEmpty => _list.Count <= 0;

            /// <summary>The gate before a pick (OneWord / TakeNext*Voice prefix): reconcile, then the weighted winner in front.</summary>
            internal void BeginPick(int? winner)
            {
                if (_state is null)
                {
                    return;
                }

                _state.Reconcile(_list, _gate, n => _rng.Next(n));
                _state.BeginPick(_list, winner);
            }

            /// <summary>The pick's finalizer.</summary>
            internal PackLinePickResult EndPick() => _state?.EndPick(_list) ?? default;

            internal void ForceResetIfEmpty()
            {
                if (_list.Count <= 0)
                {
                    InitList();
                }
            }

            /// <summary>GetNext + UseNext.</summary>
            internal int Take()
            {
                if (_list.Count <= 0)
                {
                    InitList();
                }

                var next = _list[0];
                _list.RemoveAt(0);
                return next;
            }

            private void InitList()
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

                if (_list[0] == BeforeLast)
                {
                    _list.RemoveAt(0);
                    _list.Add(BeforeLast);
                }

                BeforeLast = _list[_list.Count - 1];

                // The InitList postfix (PackLinesRuntime.OnNewCycle).
                if (_state != null)
                {
                    _state.StartCycle();
                    _state.Reconcile(_list, _gate, n => _rng.Next(n));
                    _state.OnCycleDuringPick(_list);
                }
            }
        }
    }
}
