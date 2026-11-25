using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The DLC gate on game 2.0.0: the game's own paid-DLC flag on a scenario group (<c>MyPaceOnly</c>), the My Pace scenario
    /// types, and the fail-closed fallback when the flag cannot be read. Invented ids only.
    /// </summary>
    public sealed class MakerDlcFlagTests
    {
        private static readonly IReadOnlyCollection<string> PreDlc = new HashSet<string> { "old_story", "old_click" };

        private static MakerDlcGate Gate(string? version, IReadOnlyCollection<string>? preDlc = null,
            System.Func<uint, MakerDlcOwnership>? ownership = null) =>
            new MakerDlcGate(MakerDlcMarkers.All, ownership, () => version, "2.0.0", null, preDlc ?? PreDlc);

        private static MakerDlcHidden Decide(MakerDlcGate gate, params MakerGameGroup[] groups)
        {
            var rows = new List<MakerGameRow>();
            var novel = new List<MakerGameNovelRow>();
            foreach (var group in groups)
            {
                rows.Add(new MakerGameRow(group.Id + "_001", "Voice_" + group.Id, group.Id, null, null));
                novel.Add(new MakerGameNovelRow("n_" + group.Id, group.Id, group.Id + "_001", "Heroine", null, null));
            }

            return gate.Decide(rows, novel, groups);
        }

        [Fact]
        public void A_flagged_group_is_hidden_and_an_unflagged_one_stays_listed()
        {
            var hidden = Decide(Gate("2.0.0"),
                new MakerGameGroup("old_story", null, 1, false),
                new MakerGameGroup("new_paid", null, 23, true),
                new MakerGameGroup("new_free", null, 57, false));

            Assert.True(hidden.IsKnown);
            Assert.True(hidden.IsGroup("new_paid"));
            Assert.True(hidden.IsLine("new_paid_001"));
            Assert.True(hidden.IsVoice("Voice_new_paid"));
            Assert.False(hidden.IsGroup("new_free"));
            Assert.False(hidden.IsGroup("old_story"));
            Assert.False(hidden.IsLine("new_free_001"));
            Assert.Contains("MyPaceOnly", hidden.Summary);
        }

        [Fact]
        public void The_shipped_gate_hides_flagged_groups_for_everyone_because_it_reads_no_ownership()
        {
            var gate = MakerDlcGate.ShippedFor(() => "2.0.0");
            Assert.Equal(MakerDlcOwnership.Unknown, gate.OwnershipOf(MakerDlcMarkers.RoomExpansionAppId));

            var hidden = Decide(gate, new MakerGameGroup("new_paid", null, 23, true), new MakerGameGroup("main_01", null, 1, false));

            Assert.True(hidden.IsGroup("new_paid"));
            Assert.False(hidden.IsGroup("main_01"));
        }

        [Fact]
        public void The_shipped_markers_name_the_flag_and_the_my_pace_types_for_the_room_expansion()
        {
            var markers = MakerDlcMarkers.All;

            Assert.Contains(markers, m => m.Kind == MakerDlcMarkerKind.GroupFlag && m.Pattern == "MyPaceOnly" && m.AppId == 3718890u);
            Assert.Contains(markers, m => m.Kind == MakerDlcMarkerKind.ScenarioType && m.ScenarioType == 3);
            foreach (var type in new[] { 600, 601, 2800, 2801, 2850, 2851, 2852, 2853, 2860, 2861, 3300, 3400, 3500, 3800 })
            {
                Assert.Contains(markers, m => m.Kind == MakerDlcMarkerKind.ScenarioType && m.ScenarioType == type);
            }

            Assert.Equal("2.0.0", MakerDlcMarkers.FlagExpectedFrom);
            Assert.Equal("2.0.0", MakerDlcMarkers.MadeForGameVersion);
        }

        [Fact]
        public void A_my_pace_scenario_type_is_hidden_even_without_the_flag()
        {
            var hidden = Decide(Gate("2.0.0"), new MakerGameGroup("new_mypace_click", null, 600, false), new MakerGameGroup("old_click", null, 8, false));

            Assert.True(hidden.IsGroup("new_mypace_click"));
            Assert.False(hidden.IsGroup("old_click"));
        }

        [Fact]
        public void When_the_flag_cannot_be_read_on_2_0_0_every_group_outside_the_1_18_1_set_is_hidden()
        {
            var hidden = Decide(Gate("2.0.0"),
                new MakerGameGroup("old_story", null, 1, null),
                new MakerGameGroup("old_click", null, 8, null),
                new MakerGameGroup("brand_new", null, 23, null));

            Assert.True(hidden.IsKnown);
            Assert.False(hidden.IsGroup("old_story"));
            Assert.False(hidden.IsGroup("old_click"));
            Assert.True(hidden.IsGroup("brand_new"));
            Assert.True(hidden.IsLine("brand_new_001"));
            Assert.Contains("could not be read", hidden.Summary);
        }

        [Fact]
        public void When_the_flag_is_missing_on_an_unknown_or_unreadable_version_the_fallback_still_hides()
        {
            foreach (var version in new string?[] { null, "", "not-a-version" })
            {
                var hidden = Decide(Gate(version), new MakerGameGroup("old_story", null, 1, null), new MakerGameGroup("brand_new", null, 23, null));

                Assert.True(hidden.IsGroup("brand_new"));
                Assert.False(hidden.IsGroup("old_story"));
            }
        }

        [Fact]
        public void On_1_18_1_which_has_no_flag_nothing_extra_is_hidden_and_the_dlc_story_type_still_is()
        {
            var hidden = Decide(Gate("1.18.1"),
                new MakerGameGroup("old_story", null, 1, null),
                new MakerGameGroup("a_pack_scene", null, 23, null),
                new MakerGameGroup("dlc_story", null, 3, null));

            Assert.False(hidden.IsGroup("old_story"));
            Assert.False(hidden.IsGroup("a_pack_scene"));
            Assert.True(hidden.IsGroup("dlc_story"));
            Assert.DoesNotContain("could not be read", hidden.Summary);
        }

        [Fact]
        public void A_group_a_pack_added_reports_flag_false_and_stays_listed_on_2_0_0()
        {
            var hidden = Decide(Gate("2.0.0"), new MakerGameGroup("my_pack_scene", null, 23, false));

            Assert.False(hidden.IsGroup("my_pack_scene"));
        }

        [Fact]
        public void HidesGroup_follows_the_same_flag_rules_for_scene_play()
        {
            var gate = Gate("2.0.0");

            Assert.True(gate.HidesGroup("new_paid", 23, true));
            Assert.False(gate.HidesGroup("new_free", 23, false));
            Assert.True(gate.HidesGroup("brand_new", 23, null));
            Assert.False(gate.HidesGroup("old_story", 1, null));
            Assert.True(gate.HidesGroup("new_mypace", 3300, false));
            Assert.True(gate.HidesGroup("dlc_story", 3, false));

            var older = Gate("1.18.1");
            Assert.False(older.HidesGroup("brand_new", 23, null));
            Assert.True(older.HidesGroup("dlc_story", 3, null));
        }

        [Fact]
        public void An_owned_app_lifts_the_flag_marker_like_any_other_marker()
        {
            var gate = Gate("2.0.0", ownership: app => app == MakerDlcMarkers.RoomExpansionAppId ? MakerDlcOwnership.Owned : MakerDlcOwnership.Unknown);

            var hidden = Decide(gate, new MakerGameGroup("new_paid", null, 23, true));

            Assert.False(hidden.IsGroup("new_paid"));
        }

        [Fact]
        public void The_flag_expectation_starts_at_2_0_0()
        {
            Assert.False(Gate("1.18.1").FlagExpected());
            Assert.False(Gate("1.17.3").FlagExpected());
            Assert.True(Gate("1.18.2").FlagExpected());
            Assert.True(Gate("1.19.0").FlagExpected());
            Assert.True(Gate("2.0.0-rc.1").FlagExpected());
            Assert.True(Gate("2.0.0").FlagExpected());
            Assert.True(Gate("2.1.0").FlagExpected());
            Assert.True(Gate(null).FlagExpected());
        }

        [Fact]
        public void The_pre_dlc_baseline_holds_game_groups_only()
        {
            var ids = MakerDlcBaseline.KnownFreeGroups;

            Assert.True(ids.Count > 600);
            Assert.DoesNotContain(ids, id => id.StartsWith("cwy_", System.StringComparison.Ordinal));
            Assert.Equal("2.0.0", MakerDlcBaseline.CapturedOn);
            Assert.Equal("1.18.1", MakerDlcBaseline.LastVersionWithoutFlag);
        }

        [Theory]
        [InlineData("1.18.2")]
        [InlineData("1.19.0")]
        [InlineData("2.0.0-rc.1")]
        public void G7_a_build_between_the_last_pre_dlc_build_and_the_made_for_build_is_not_confirmed(string version)
        {
            Assert.False(Gate(version).GameVersionConfirmed(out _));
            Assert.True(Gate("2.0.0").GameVersionConfirmed(out _));
            Assert.True(Gate("1.18.1").GameVersionConfirmed(out _));
        }

        [Fact]
        public void G8_a_flag_marker_never_matches_an_id()
        {
            var marker = new MakerDlcMarker(MakerDlcMarkerKind.GroupFlag, "MyPaceOnly", 1);
            Assert.False(marker.MatchesId("MyPaceOnly"));
            Assert.False(marker.MatchesId("any_group"));
        }

        [Fact]
        public void G2_the_movement_scenario_types_are_paid()
        {
            var hidden = Decide(Gate("2.0.0"), new MakerGameGroup("move_departure_bed", null, 3600, false), new MakerGameGroup("move_arrival", null, 3700, false),
                new MakerGameGroup("old_story", null, 1, false));

            Assert.True(hidden.IsGroup("move_departure_bed"));
            Assert.True(hidden.IsGroup("move_arrival"));
            Assert.False(hidden.IsGroup("old_story"));
        }

        [Fact]
        public void G6_no_novel_rows_and_no_group_on_any_row_cannot_be_decided()
        {
            var gate = Gate("2.0.0");
            var noNovel = gate.Decide(new[] { new MakerGameRow("a_line", "Voice_A", null, null, null) }, new MakerGameNovelRow[0], new[] { new MakerGameGroup("old_story", null, 1, false) });
            Assert.False(noNovel.IsKnown);
            Assert.True(noNovel.IsLine("a_line"));

            var flagOnly = new MakerDlcGate(new[] { new MakerDlcMarker(MakerDlcMarkerKind.GroupFlag, "MyPaceOnly", 1) }, null, () => "2.0.0", null, null, PreDlc);
            var noGroups = flagOnly.Decide(new[] { new MakerGameRow("a_line", null, "some_group", null, null) },
                new[] { new MakerGameNovelRow("n", "some_group", "a_line", null, null, null) }, new MakerGameGroup[0]);
            Assert.False(noGroups.IsKnown);
        }

        [Fact]
        public void G5_a_free_line_shared_by_two_known_free_groups_stays_listed_when_the_flag_is_unreadable()
        {
            // countupstart_word_001 shares its line with pomodorostart_word_001 in the game; both are in the shipped known-free set.
            Assert.Contains("countupstart_word_001", MakerDlcBaseline.KnownFreeGroups);
            Assert.Contains("pomodorostart_word_001", MakerDlcBaseline.KnownFreeGroups);
            Assert.Contains("game_start_02_001", MakerDlcBaseline.KnownFreeGroups);
            var gate = new MakerDlcGate(MakerDlcMarkers.All, null, () => "2.0.0", null);
            var hidden = gate.Decide(new[] { new MakerGameRow("shared_line", "Voice_Shared", "pomodorostart_word_001", null, null) },
                new[] { new MakerGameNovelRow("a", "pomodorostart_word_001", "shared_line", null, null, null), new MakerGameNovelRow("b", "countupstart_word_001", "shared_line", null, null, null) },
                new[] { new MakerGameGroup("pomodorostart_word_001", null, 4, null), new MakerGameGroup("countupstart_word_001", null, 3000, null) });

            Assert.True(hidden.IsKnown);
            Assert.False(hidden.IsLine("shared_line"));
            Assert.False(hidden.IsGroup("countupstart_word_001"));
        }

        [Fact]
        public void G5_the_shipped_set_lists_no_flagged_or_paid_type_group_of_2_0_0()
        {
            foreach (var id in new[] { "smalltalk_bed_1", "smalltalk_stairs_1", "clickheroine_mypace_break_001", "selftalk_break_mypace_001", "mypace_introduction_001" })
            {
                Assert.DoesNotContain(id, MakerDlcBaseline.KnownFreeGroups);
            }
        }
    }
}
