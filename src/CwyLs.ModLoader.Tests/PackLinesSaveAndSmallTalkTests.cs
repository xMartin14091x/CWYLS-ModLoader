using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Pack lines, requirement 4 (pack ids never reach the save) and the small-talk half of
    /// requirement 2 (the lottery cache holds pack lines only while their language is shown).
    /// </summary>
    public class PackLinesSaveAndSmallTalkTests
    {
        private static readonly HashSet<string> PackIds = new HashSet<string> { "cwy_th_small_001", "cwy_th_small_002" };

        [Fact]
        public void The_save_filter_takes_out_only_pack_ids_and_keeps_the_games_order()
        {
            var ids = new List<string> { "main_01", "cwy_th_small_001", "smalltalk_03", "cwy_th_small_002", "main_02" };

            var held = PackLineSaveFilter.Strip(ids, PackIds.Contains);

            Assert.Equal(new[] { "main_01", "smalltalk_03", "main_02" }, ids);
            Assert.Equal(new[] { "cwy_th_small_001", "cwy_th_small_002" }, held.Ids);
        }

        [Fact]
        public void Restoring_after_the_save_gives_back_exactly_the_list_the_session_had()
        {
            var before = new List<string> { "cwy_th_small_001", "main_01", "cwy_th_small_001", "smalltalk_03", "cwy_th_small_002" };
            var ids = new List<string>(before);

            var held = PackLineSaveFilter.Strip(ids, PackIds.Contains);
            Assert.Equal(new[] { "main_01", "smalltalk_03" }, ids);
            PackLineSaveFilter.Restore(ids, held);

            Assert.Equal(before, ids);
        }

        [Fact]
        public void Nothing_to_hold_back_changes_nothing()
        {
            var ids = new List<string> { "main_01", "smalltalk_03" };

            var held = PackLineSaveFilter.Strip(ids, PackIds.Contains);
            PackLineSaveFilter.Restore(ids, held);

            Assert.Same(PackLineHeldIds.None, held);
            Assert.Equal(new[] { "main_01", "smalltalk_03" }, ids);
            Assert.Same(PackLineHeldIds.None, PackLineSaveFilter.Strip(null, PackIds.Contains));
            PackLineSaveFilter.Restore(null, held);
        }

        [Fact]
        public void A_list_shortened_behind_the_filters_back_still_gets_every_id_back()
        {
            var ids = new List<string> { "main_01", "main_02", "cwy_th_small_001" };
            var held = PackLineSaveFilter.Strip(ids, PackIds.Contains);
            ids.Clear();

            PackLineSaveFilter.Restore(ids, held);

            Assert.Equal(new[] { "cwy_th_small_001" }, ids);
        }

        [Fact]
        public void Withdrawing_pack_small_talks_leaves_the_games_entries_in_their_order()
        {
            var cache = SmallTalkCache();

            var withdrawn = PackLineSmallTalkGate.Withdraw(cache, IsPack);

            Assert.Equal(new[] { 1f, 2f, 3f }, cache.Keys.ToArray());
            Assert.Equal(new[] { 1001f, 1002f }, withdrawn.Select(e => e.Key).ToArray());
        }

        [Fact]
        public void A_closed_gate_keeps_the_lottery_exactly_the_games_own()
        {
            var vanilla = new Dictionary<float, Group> { [1f] = new Group("smalltalk_01"), [2f] = new Group("smalltalk_02"), [3f] = new Group("smalltalk_03") };
            var cache = SmallTalkCache();
            var withdrawn = PackLineSmallTalkGate.Withdraw(cache, IsPack);

            var result = PackLineSmallTalkGate.Reconcile(cache, withdrawn, _ => false);

            Assert.False(result.Changed);
            Assert.Equal(vanilla.Keys.ToArray(), cache.Keys.ToArray());
            Assert.Equal(vanilla.Values.Select(g => g.Id), cache.Values.Select(g => g.Id));
        }

        [Fact]
        public void An_open_gate_puts_the_shown_languages_lines_into_the_lottery_and_takes_them_out_again()
        {
            var cache = SmallTalkCache();
            var withdrawn = PackLineSmallTalkGate.Withdraw(cache, IsPack);

            var on = PackLineSmallTalkGate.Reconcile(cache, withdrawn, episode => episode == 1001f);
            Assert.Equal(1, on.Placed);
            Assert.True(cache.ContainsKey(1001f));
            Assert.False(cache.ContainsKey(1002f));

            var off = PackLineSmallTalkGate.Reconcile(cache, withdrawn, _ => false);
            Assert.Equal(1, off.Withdrawn);
            Assert.Equal(new[] { 1f, 2f, 3f }, cache.Keys.OrderBy(k => k).ToArray());
        }

        [Fact]
        public void An_entry_that_is_not_ours_is_never_touched()
        {
            var cache = new Dictionary<float, Group> { [1001f] = new Group("someone_elses") };
            var ours = new List<KeyValuePair<float, Group>> { new KeyValuePair<float, Group>(1001f, new Group("cwy_th_small_001")) };

            PackLineSmallTalkGate.Reconcile(cache, ours, _ => false);
            PackLineSmallTalkGate.Reconcile(cache, ours, _ => true);

            Assert.Equal("someone_elses", cache[1001f].Id);
        }

        private static bool IsPack(Group group) => PackIds.Contains(group.Id);

        private static Dictionary<float, Group> SmallTalkCache() =>
            new Dictionary<float, Group>
            {
                [1f] = new Group("smalltalk_01"),
                [2f] = new Group("smalltalk_02"),
                [1001f] = new Group("cwy_th_small_001"),
                [3f] = new Group("smalltalk_03"),
                [1002f] = new Group("cwy_th_small_002"),
            };

        /// <summary>Stands in for the game's <c>ScenarioGroupData</c>.</summary>
        private sealed class Group
        {
            internal Group(string id)
            {
                Id = id;
            }

            internal string Id { get; }
        }
    }
}
