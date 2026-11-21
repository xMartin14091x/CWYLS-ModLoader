using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Master data's save filter, the only one that runs: it holds with pack lines on or off, strips and
    /// restores once per save, tells pack lines once, and - while its hook is missing - keeps pack small
    /// talk out of the game's data instead of letting it leak into the save.
    /// </summary>
    public class PackSaveHoldBackTests
    {
        private const string PlainPack = "mymod_smalltalk_scene_1";
        private const string PlainClick = "mymod_click_1";
        private const string PackLine = "cwy_th_small_001";
        private const string GameOwn = "smalltalk_03";
        private const string GameOverridden = "smalltalk_04";
        private const string NewerGame = "smalltalk_99";

        private static readonly MasterDataBaseline Game =
            MasterDataBaseline.Build(new[] { "x" }, new[] { "x" }, new[] { GameOwn, GameOverridden, "main_01" });

        private static MasterDataSet Injection() =>
            PackLineRows.Set(
                new[]
                {
                    PackLineRows.Group(PlainPack, PackSaveHoldBack.SmallTalkScenario, 9001),
                    PackLineRows.Group(GameOverridden, PackSaveHoldBack.SmallTalkScenario, 4),
                    PackLineRows.Group(PlainClick, 9, 1001),
                },
                new[]
                {
                    PackLineRows.Spoken(PlainPack + "_001", PlainPack),
                    PackLineRows.Spoken(GameOverridden + "_001", GameOverridden),
                    PackLineRows.Spoken(PlainClick + "_001", PlainClick),
                });

        private static PackSaveHoldBack Installed()
        {
            var holdBack = new PackSaveHoldBack(new PackAddedGroups());
            holdBack.MarkInstalled();
            holdBack.AddedGroups.Record(Injection().ScenarioGroups, Game);
            return holdBack;
        }

        private static List<string> Played() => new List<string> { GameOwn, PlainPack, NewerGame, PackLine, GameOverridden };

        [Fact]
        public void Pack_lines_off_the_filter_still_keeps_pack_added_groups_out_of_the_save()
        {
            var holdBack = Installed();
            var played = Played();

            var held = holdBack.HoldBack(played);

            Assert.Equal(new[] { PlainPack }, held.Ids);
            Assert.Equal(new[] { GameOwn, NewerGame, PackLine, GameOverridden }, played);
            holdBack.Restore(played);
            Assert.Equal(Played(), played);
        }

        [Fact]
        public void Pack_lines_on_one_pass_holds_back_both_and_tells_pack_lines_once_per_save()
        {
            var holdBack = Installed();
            var calls = new List<IReadOnlyList<string>>();
            Assert.True(holdBack.JoinPackLines(new HashSet<string> { PackLine }, calls.Add));
            var played = Played();

            holdBack.HoldBack(played);
            Assert.Equal(new[] { GameOwn, NewerGame, GameOverridden }, played);
            holdBack.Restore(played);
            Assert.Equal(Played(), played);
            holdBack.HoldBack(played);
            holdBack.Restore(played);

            Assert.Equal(2, calls.Count);
            Assert.Equal(new[] { PlainPack, PackLine }, calls[0]);
            Assert.True(holdBack.PackLinesJoined);
        }

        [Fact]
        public void Pack_line_ids_are_held_back_even_when_no_pack_added_a_plain_group()
        {
            var holdBack = new PackSaveHoldBack(new PackAddedGroups());
            holdBack.MarkInstalled();
            holdBack.JoinPackLines(new HashSet<string> { PackLine }, null);
            var played = new List<string> { GameOwn, PackLine };

            holdBack.HoldBack(played);

            Assert.Equal(new[] { GameOwn }, played);
        }

        [Fact]
        public void Joining_again_replaces_the_first_join_so_each_save_tells_pack_lines_once()
        {
            var holdBack = Installed();
            var first = 0;
            var second = 0;
            holdBack.JoinPackLines(new HashSet<string> { PackLine }, _ => first++);
            holdBack.JoinPackLines(new HashSet<string> { PackLine }, _ => second++);

            holdBack.HoldBack(Played());

            Assert.Equal(0, first);
            Assert.Equal(1, second);
        }

        [Fact]
        public void A_save_restores_once_and_a_save_holding_nothing_tells_nobody()
        {
            var holdBack = Installed();
            var calls = 0;
            holdBack.JoinPackLines(new HashSet<string> { PackLine }, _ => calls++);
            var played = Played();

            holdBack.HoldBack(played);
            holdBack.Restore(played);
            holdBack.Restore(played);
            Assert.Equal(Played(), played);

            var clean = new List<string> { GameOwn, NewerGame };
            Assert.Same(PackLineHeldIds.None, holdBack.HoldBack(clean));
            holdBack.Restore(clean);
            Assert.Equal(new[] { GameOwn, NewerGame }, clean);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void A_throwing_pack_lines_callback_still_gets_every_id_put_back()
        {
            var holdBack = Installed();
            holdBack.JoinPackLines(new HashSet<string> { PackLine }, _ => throw new InvalidOperationException("seen file"));
            var played = Played();

            Assert.Throws<InvalidOperationException>(() => holdBack.HoldBack(played));
            holdBack.Restore(played);

            Assert.Equal(Played(), played);
        }

        [Fact]
        public void Hook_missing_keeps_pack_small_talk_out_of_the_injection_and_nothing_else()
        {
            var holdBack = new PackSaveHoldBack(new PackAddedGroups());
            holdBack.MarkMissing("PATCH TARGET MISSING: masterdata.save.scenarioprogress");

            var planned = holdBack.PlanInjection(Injection(), Game, out var heldOut);

            Assert.Equal(1, heldOut);
            Assert.Equal(new[] { GameOverridden, PlainClick }, planned.ScenarioGroups.Select(g => g.Id));
            Assert.Equal(new[] { GameOverridden + "_001", PlainClick + "_001" }, planned.Novel.Select(r => r.Id));
            Assert.Equal("PATCH TARGET MISSING: masterdata.save.scenarioprogress", holdBack.Problem);
        }

        [Fact]
        public void Hook_missing_refuses_pack_lines_so_they_stay_off()
        {
            var holdBack = new PackSaveHoldBack(new PackAddedGroups());
            holdBack.MarkMissing("missing");

            Assert.False(holdBack.JoinPackLines(new HashSet<string> { PackLine }, _ => { }));
            Assert.False(holdBack.PackLinesJoined);
            Assert.False(holdBack.IsHeld(PackLine));
        }

        [Fact]
        public void Before_the_hook_is_tried_it_counts_as_missing()
        {
            var holdBack = new PackSaveHoldBack(new PackAddedGroups());

            holdBack.PlanInjection(Injection(), Game, out var heldOut);

            Assert.False(holdBack.Installed);
            Assert.Equal(1, heldOut);
            Assert.False(holdBack.JoinPackLines(new HashSet<string>(), null));
        }

        [Fact]
        public void Without_the_games_ids_a_missing_hook_keeps_every_pack_small_talk_out()
        {
            var holdBack = new PackSaveHoldBack(new PackAddedGroups());

            var planned = holdBack.PlanInjection(Injection(), MasterDataBaseline.Empty, out var heldOut);

            Assert.Equal(2, heldOut);
            Assert.Equal(new[] { PlainClick }, planned.ScenarioGroups.Select(g => g.Id));
        }

        [Fact]
        public void Installed_hook_injects_the_set_unchanged()
        {
            var holdBack = Installed();
            var set = Injection();

            Assert.Same(set, holdBack.PlanInjection(set, Game, out var heldOut));
            Assert.Equal(0, heldOut);
            Assert.True(holdBack.Installed);
            Assert.Equal(string.Empty, holdBack.Problem);
        }
    }
}
