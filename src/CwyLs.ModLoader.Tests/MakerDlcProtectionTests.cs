using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.MasterData;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Hotfix 0.9.3 review items (Codex review of f57740f): pack rows cannot override or use paid DLC content (#1, #2), lines the
    /// game picks at playback are protected (#3), a missing verdict protects everything (#4), hidden groups' titles (#12), free
    /// interface labels on a newer patch (#13) and the exact 1.18.1 baseline (#14). Invented ids, except the shipped baseline.
    /// </summary>
    public sealed class MakerDlcProtectionTests
    {
        private static readonly IReadOnlyCollection<string> PreDlc = new HashSet<string>(StringComparer.Ordinal) { "free_story", "free_scene" };

        private static MakerDlcGate Gate(string? version = "2.0.0") =>
            new MakerDlcGate(MakerDlcMarkers.All, null, () => version, null, null, PreDlc);

        /// <summary>The untouched game tables: a free story, a flagged small-talk group with its own title and subtitle rows.</summary>
        private static MakerDlcHidden Untouched(MakerDlcGate? gate = null)
        {
            var rows = new[]
            {
                new MakerGameRow("free_story_001", "Voice_Free_001", "free_story", null, null),
                new MakerGameRow("paid_talk_001", "Voice_Paid_001", "paid_talk", null, null),
                new MakerGameRow("paid_talk_title", null, null, null, null),
                new MakerGameRow("paid_talk_subtitle", null, null, null, null),
                new MakerGameRow("free_title", null, null, null, null),
            };
            var novel = new[]
            {
                new MakerGameNovelRow("n_free", "free_story", "free_story_001", "Heroine", null, null),
                new MakerGameNovelRow("n_paid", "paid_talk", "paid_talk_001", "Heroine", null, null),
            };
            var groups = new[]
            {
                new MakerGameGroup("free_story", null, 1, false, "free_title", null),
                new MakerGameGroup("paid_talk", null, 23, true, "paid_talk_title", "paid_talk_subtitle"),
            };
            return (gate ?? Gate()).Decide(rows, novel, groups);
        }

        /// <summary>Decides over a free story (so lines can be tied to groups) plus the given ungrouped rows.</summary>
        private static MakerDlcHidden Decide(MakerGameRow a, MakerGameRow b, MakerDlcGate? gate = null) =>
            (gate ?? Gate()).Decide(new[] { new MakerGameRow("free_story_001", null, "free_story", null, null), a, b },
                new[] { new MakerGameNovelRow("n_free", "free_story", "free_story_001", null, null, null) },
                new[] { new MakerGameGroup("free_story", null, 1, false) });

        private static readonly IReadOnlyDictionary<string, string?> BaseNovel =
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["n_free"] = "free_story", ["n_paid"] = "paid_talk" };

        private static MasterDataSet Set(LocalizationRow[]? loc = null, NovelRow[]? novel = null, ScenarioGroupRow[]? groups = null) =>
            new MasterDataSet(loc ?? new LocalizationRow[0], novel ?? new NovelRow[0], groups ?? new ScenarioGroupRow[0], null);

        [Fact]
        public void P0_a_same_id_override_of_a_flagged_group_is_refused_so_the_game_flag_and_the_refusal_stay()
        {
            var paid = Untouched();
            var result = MasterDataPaidAdmission.Filter(
                Set(groups: new[] { new ScenarioGroupRow { Id = "paid_talk", Scenario = 23 }, new ScenarioGroupRow { Id = "free_story", Scenario = 1 } }),
                paid, BaseNovel);

            Assert.Equal(new[] { "free_story" }, result.ScenarioGroups.Select(g => g.Id));
            Assert.Contains("scenario group paid_talk", result.Refused);
            Assert.True(paid.IsGroup("paid_talk"));
            Assert.NotNull(paid.Refusal("paid_talk_001", null));
        }

        [Fact]
        public void P1_a_localization_override_of_a_paid_line_or_clip_is_refused_and_a_free_override_is_admitted()
        {
            var result = MasterDataPaidAdmission.Filter(Set(loc: new[]
            {
                new LocalizationRow { Id = "paid_talk_001", Voice = "Voice_Paid_001" },
                new LocalizationRow { Id = "my_new_line", Voice = "Voice_Paid_001" },
                new LocalizationRow { Id = "free_story_001", Voice = "Voice_Free_001" },
            }), Untouched(), BaseNovel);

            Assert.Equal(new[] { "free_story_001" }, result.Localization.Select(r => r.Id));
            Assert.Contains("localization paid_talk_001", result.Refused);
            Assert.Contains("localization my_new_line", result.Refused);
        }

        [Fact]
        public void P1_a_free_scene_that_speaks_a_paid_line_or_joins_or_replaces_a_paid_group_is_refused()
        {
            var result = MasterDataPaidAdmission.Filter(Set(novel: new[]
            {
                new NovelRow { Id = "my_scene_1", ScenarioGroupId = "my_scene", LocalizationId = "paid_talk_001" },
                new NovelRow { Id = "my_scene_2", ScenarioGroupId = "paid_talk", LocalizationId = "free_story_001" },
                new NovelRow { Id = "n_paid", ScenarioGroupId = "my_scene", LocalizationId = "free_story_001" },
                new NovelRow { Id = "my_scene_3", ScenarioGroupId = "my_scene", LocalizationId = "free_story_001" },
            }, groups: new[]
            {
                new ScenarioGroupRow { Id = "my_scene", Scenario = 23, TitleLocalizationId = "free_title" },
                new ScenarioGroupRow { Id = "my_titled", Scenario = 23, TitleLocalizationId = "paid_talk_title" },
            }), Untouched(), BaseNovel);

            Assert.Equal(new[] { "my_scene_3" }, result.Novel.Select(r => r.Id));
            Assert.Equal(new[] { "my_scene" }, result.ScenarioGroups.Select(g => g.Id));
            Assert.Equal(4, result.Refused.Count);
        }

        [Fact]
        public void Guards_Q1_the_verdict_names_the_games_novel_rows_of_hidden_content()
        {
            var paid = Untouched();

            Assert.True(paid.IsNovelRow("n_paid"));
            Assert.False(paid.IsNovelRow("n_free"));
            Assert.Equal(1, paid.NovelRowCount);
            Assert.True(MakerDlcHidden.Unknown("x").IsNovelRow("n_free"));

            // A pack row replacing the game's paid novel row by id is refused even without the base-row map.
            var result = MasterDataPaidAdmission.Filter(Set(novel: new[] { new NovelRow { Id = "n_paid", ScenarioGroupId = "my_scene", LocalizationId = "free_story_001" } }), paid, null);
            Assert.Empty(result.Novel);
        }

        [Theory]
        [InlineData("Voice_Paid_001")]
        [InlineData("Sounds/Voice/Voice_Paid_001.wav")]
        [InlineData("Voice/Voice_Paid_001")]
        public void Guards_Q2_admission_refuses_a_path_variant_of_a_paid_clip(string voice)
        {
            var result = MasterDataPaidAdmission.Filter(Set(loc: new[] { new LocalizationRow { Id = "my_line", Voice = voice } }), Untouched(), BaseNovel);

            Assert.Empty(result.Localization);
        }

        [Fact]
        public void P1_no_verdict_or_an_undecided_one_refuses_every_pack_row_and_protects_every_id()
        {
            var set = Set(loc: new[] { new LocalizationRow { Id = "free_story_001" } }, novel: new[] { new NovelRow { Id = "x", ScenarioGroupId = "g", LocalizationId = "free_story_001" } });

            Assert.Empty(MasterDataPaidAdmission.Filter(set, null, BaseNovel).Localization);
            Assert.Empty(MasterDataPaidAdmission.Filter(set, MakerDlcHidden.Unknown("not ready"), BaseNovel).Novel);
            Assert.True(MakerDlcHidden.Protects(null, "free_story_001"));
            Assert.True(MakerDlcHidden.ProtectsVoice(null, "Voice_Free_001"));
            Assert.True(MakerDlcHidden.ProtectsGroup(MakerDlcHidden.Unknown("failed"), "free_story"));
            Assert.False(MakerDlcHidden.Protects(Untouched(), "free_story_001"));
            Assert.True(MakerDlcHidden.Protects(Untouched(), "paid_talk_001"));
        }

        [Theory]
        [InlineData("goto_bed_003")]
        [InlineData("goto_stairs_001")]
        [InlineData("goto_desk_002")]
        [InlineData("location_arrival_004")]
        public void P1_movement_lines_picked_at_playback_are_protected_without_any_novel_row(string id)
        {
            var hidden = Decide(new MakerGameRow(id, "Voice_" + id, null, null, null), new MakerGameRow("gotoxbed_1", null, null, null, null));

            Assert.True(hidden.IsKnown);
            Assert.True(hidden.IsLine(id));
            Assert.True(hidden.IsVoice("Voice_" + id));
            Assert.False(hidden.IsLine("gotoxbed_1"));
        }

        [Fact]
        public void P1_the_catalog_decides_over_gate_only_rows_refuses_them_and_never_lists_them()
        {
            var game = new MakerGameData(
                new[] { new MakerGameRow("free_story_001", "Voice_Free_001", "free_story", "", "Free") },
                null, null,
                new[] { new MakerGameNovelRow("n_free", "free_story", "free_story_001", "Heroine", null, null) },
                new[] { new MakerGameGroup("free_story", null, 1, false) },
                null, Gate(),
                new[] { new MakerGameRow("goto_bed_001", "Voice_Goto_Bed_001", null, null, null), new MakerGameRow("menu_label", null, null, null, null) });

            Assert.Equal(new[] { "free_story_001" }, game.Rows.Select(r => r.Id));
            Assert.Equal(MakerErrorCodes.NotFound, game.Hidden.Refusal("goto_bed_001", null)!.Error);
            Assert.NotNull(game.Hidden.Refusal(null, "Voice_Goto_Bed_001"));
            Assert.Null(game.Hidden.Refusal("menu_label", null));
        }

        [Fact]
        public void P2_a_hidden_groups_title_and_subtitle_rows_are_protected_and_a_free_groups_title_is_not()
        {
            var hidden = Untouched();

            Assert.True(hidden.IsLine("paid_talk_title"));
            Assert.True(hidden.IsLine("paid_talk_subtitle"));
            Assert.False(hidden.IsLine("free_title"));
            Assert.False(new MakerGameGroup("g", null, 1, false, "0", " ").TitleId != null);
        }

        [Fact]
        public void P2_on_a_newer_patch_a_free_unvoiced_label_stays_and_an_unresolved_voiced_line_is_hidden()
        {
            var gate = Gate("2.0.1");
            Assert.False(gate.GameVersionConfirmed(out _));

            var hidden = Decide(new MakerGameRow("menu_settings_label", null, null, null, null),
                new MakerGameRow("brand_new_line", "Voice_Brand_New", null, null, null), gate);

            Assert.False(hidden.IsLine("menu_settings_label"));
            Assert.True(hidden.IsLine("brand_new_line"));
            Assert.True(hidden.IsVoice("Voice_Brand_New"));
        }

        [Fact]
        public void P2_the_shipped_known_free_set_is_exactly_the_recorded_capture()
        {
            // Recorded independently by the maintainers (read from game 2.0.0's ScenarioGroupMaster:
            // 616 free groups + 3 dropped 1.18.x groups), ids sorted ordinal and joined with "\n".
            var ids = MakerDlcBaseline.KnownFreeGroups.OrderBy(id => id, StringComparer.Ordinal).ToList();
            Assert.Equal(619, ids.Count);
            using var sha = SHA256.Create();
            var digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", ids)))).Replace("-", "").ToLowerInvariant();
            Assert.Equal("91c851a296516ee5c04b8559a741fe6fd4bb9f96a2ec2d999ebbb1ec50b05b2f", digest);
        }

        [Theory]
        [InlineData("2.0.0")]
        [InlineData(null)]
        [InlineData("garbled")]
        public void P2_the_shipped_fallback_keeps_a_baseline_member_and_hides_a_new_group_when_the_flag_is_unreadable(string? version)
        {
            var gate = new MakerDlcGate(MakerDlcMarkers.All, null, () => version, null);
            var hidden = gate.Decide(
                new[] { new MakerGameRow("main_01_001", null, "main_01", null, null), new MakerGameRow("synthetic_new_001", null, "synthetic_new_group", null, null) },
                new[] { new MakerGameNovelRow("a", "main_01", "main_01_001", null, null, null), new MakerGameNovelRow("b", "synthetic_new_group", "synthetic_new_001", null, null, null) },
                new[] { new MakerGameGroup("main_01", null, 1, null), new MakerGameGroup("synthetic_new_group", null, 1, null) });

            Assert.False(hidden.IsGroup("main_01"));
            Assert.True(hidden.IsGroup("synthetic_new_group"));
            Assert.True(hidden.IsLine("synthetic_new_001"));
        }
    }
}
