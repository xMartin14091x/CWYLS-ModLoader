using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The small-talk save gap: a plain pack scene group (no <c>Languages</c>) is injected like a game
    /// group, the game's own lottery can draw it and <c>SmallTalkSelector.OnEndTalk</c> logs its id. The
    /// save filter must hold back exactly the groups packs added (recorded at injection) - never an id
    /// merely unknown to this build, which can be real progress synced from a newer game version.
    /// </summary>
    public class PackLinesSaveGapTests
    {
        private const int SmallTalk = 23;
        private const string PlainPack = "mymod_smalltalk_scene_1";
        private const string PackLine = "cwy_th_small_001";
        private const string GameOwn = "smalltalk_03";
        private const string GameOverridden = "smalltalk_04";
        private const string NewerGame = "smalltalk_99";

        private static readonly MasterDataBaseline Game =
            MasterDataBaseline.Build(new[] { "x" }, new[] { "x" }, new[] { GameOwn, GameOverridden, "main_01" });

        private static readonly HashSet<string> PackLineIds = new HashSet<string>(StringComparer.Ordinal) { PackLine };

        /// <summary>What one injection puts in: a plain pack small talk and a pack override of a game small talk.</summary>
        private static IReadOnlyList<ScenarioGroupRow> Injected() =>
            PackLineRows.Set(new[]
            {
                PackLineRows.Group(PlainPack, SmallTalk, 9001),
                PackLineRows.Group(GameOverridden, SmallTalk, 4),
            }).ScenarioGroups;

        private static Func<string, bool> RuleAfterInjection()
        {
            var added = new PackAddedGroups();
            added.Record(Injected(), Game);
            return PackLineSaveFilter.HoldBackRule(PackLineIds, added);
        }

        [Fact]
        public void A_plain_small_talk_group_a_pack_added_is_held_out_of_the_save()
        {
            var played = new List<string> { GameOwn, PlainPack };

            var held = PackLineSaveFilter.Strip(played, RuleAfterInjection());

            Assert.Equal(new[] { GameOwn }, played);
            Assert.Equal(new[] { PlainPack }, held.Ids);
        }

        [Fact]
        public void An_id_neither_this_game_build_nor_a_pack_knows_stays_in_the_save()
        {
            // A save synced from a newer game version: NewerGame is real progress this build has never seen.
            var played = new List<string> { NewerGame, PlainPack, "main_02" };

            PackLineSaveFilter.Strip(played, RuleAfterInjection());

            Assert.Equal(new[] { NewerGame, "main_02" }, played);
        }

        [Fact]
        public void The_games_own_ids_stay_in_the_save_including_one_a_pack_overrides()
        {
            var played = new List<string> { GameOwn, GameOverridden, "main_01" };

            var held = PackLineSaveFilter.Strip(played, RuleAfterInjection());

            Assert.Equal(new[] { GameOwn, GameOverridden, "main_01" }, played);
            Assert.Same(PackLineHeldIds.None, held);
        }

        [Fact]
        public void Pack_line_ids_are_still_held_out_of_the_save()
        {
            var played = new List<string> { PackLine, GameOwn };

            var held = PackLineSaveFilter.Strip(played, RuleAfterInjection());

            Assert.Equal(new[] { GameOwn }, played);
            Assert.Equal(new[] { PackLine }, held.Ids);
        }

        [Fact]
        public void Held_back_ids_still_count_as_played_for_the_session_and_in_later_ones()
        {
            var rule = RuleAfterInjection();
            var before = new List<string> { GameOwn, PlainPack, NewerGame, PackLine };
            var played = new List<string>(before);

            var held = PackLineSaveFilter.Strip(played, rule);
            Assert.Equal(new[] { GameOwn, NewerGame }, played);
            PackLineSaveFilter.Restore(played, held);
            Assert.Equal(before, played);

            // Next launch: the save held neither; the mod's seen file puts both back as seen, as for pack lines.
            var nextSession = new List<string> { GameOwn, NewerGame };
            var marked = PackLineSeen.MarkSeen(nextSession, held.Ids, rule);
            Assert.Equal(2, marked);
            Assert.Equal(new[] { GameOwn, NewerGame, PlainPack, PackLine }, nextSession);
        }

        [Fact]
        public void An_injection_without_the_games_ids_records_nothing()
        {
            var added = new PackAddedGroups();

            Assert.Equal(0, added.Record(Injected(), MasterDataBaseline.Empty));
            Assert.Equal(0, added.Record(Injected(), null));
            Assert.Equal(0, added.Count);
            Assert.False(added.Contains(GameOverridden));
        }

        [Fact]
        public void Recording_takes_exactly_the_added_groups_and_keeps_them_across_injections()
        {
            var added = new PackAddedGroups();

            Assert.Equal(1, added.Record(Injected(), Game));
            Assert.Equal(new[] { PlainPack }, added.Ids);

            // A rescan without the pack: the in-memory played list may still hold its id, so it stays recorded.
            Assert.Equal(0, added.Record(Array.Empty<ScenarioGroupRow>(), Game));
            Assert.True(added.Contains(PlainPack));
            Assert.False(added.Contains(NewerGame));
            Assert.False(added.Contains(null));
        }

        [Fact]
        public void A_group_the_games_own_data_turns_out_to_have_is_the_games_and_is_dropped()
        {
            var added = new PackAddedGroups();
            added.Record(Injected(), Game);
            var later = MasterDataBaseline.Build(new[] { "x" }, new[] { "x" }, new[] { GameOwn, GameOverridden, PlainPack });

            added.Record(Injected(), later);

            Assert.False(added.Contains(PlainPack));
            Assert.Equal(0, added.Count);
        }

        [Fact]
        public void The_rule_refuses_missing_inputs()
        {
            Assert.Throws<ArgumentNullException>(() => PackLineSaveFilter.HoldBackRule(null!, new PackAddedGroups()));
            Assert.Throws<ArgumentNullException>(() => PackLineSaveFilter.HoldBackRule(PackLineIds, null!));
        }
    }
}
