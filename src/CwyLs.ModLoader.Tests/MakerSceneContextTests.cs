using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>W2b-05: where a line sits in the novel scripts, verified occurrences only; nothing is guessed. Invented rows throughout.</summary>
    public sealed class MakerSceneContextTests
    {
        private static MakerGameNovelRow Row(string row, string? group, string? line, string? talker = "Heroine", string? body = null, string? face = null) =>
            new MakerGameNovelRow(row, group, line, talker, body, face);

        private static MakerSceneContext Build(params MakerGameNovelRow[] rows) =>
            MakerSceneContext.Build(rows, id => id.StartsWith("line_", StringComparison.Ordinal));

        [Fact]
        public void Previous_and_next_follow_the_row_order_of_the_scenario_group()
        {
            var scenes = Build(Row("n1", "g1", "line_1"), Row("n2", "g1", "line_2"), Row("n3", "g1", "line_3"));

            var middle = Assert.Single(scenes.OccurrencesOf("line_2"));
            Assert.Equal("g1", middle.GroupId);
            Assert.Equal(1, middle.Order);
            Assert.Equal("line_1", middle.PreviousId);
            Assert.Equal("line_3", middle.NextId);
            Assert.Null(scenes.OccurrencesOf("line_1").Single().PreviousId);
            Assert.Null(scenes.OccurrencesOf("line_3").Single().NextId);
        }

        [Fact]
        public void Neighbours_are_taken_inside_each_group_even_when_rows_of_groups_interleave()
        {
            var scenes = Build(Row("n1", "g1", "line_1"), Row("n2", "g2", "line_x"), Row("n3", "g1", "line_2"), Row("n4", "g2", "line_y"));

            Assert.Equal("line_2", scenes.OccurrencesOf("line_1").Single().NextId);
            Assert.Equal("line_y", scenes.OccurrencesOf("line_x").Single().NextId);
            Assert.Null(scenes.OccurrencesOf("line_2").Single().NextId);
        }

        [Fact]
        public void A_line_two_scenes_speak_has_two_occurrences_with_different_ids_and_neighbours()
        {
            var scenes = Build(Row("n1", "g1", "line_1"), Row("n2", "g1", "line_shared"), Row("n3", "g2", "line_shared"), Row("n4", "g2", "line_4"));

            var occurrences = scenes.OccurrencesOf("line_shared");

            Assert.Equal(2, occurrences.Count);
            Assert.NotEqual(occurrences[0].OccurrenceId, occurrences[1].OccurrenceId);
            Assert.Equal("line_1", occurrences[0].PreviousId);
            Assert.Null(occurrences[0].NextId);
            Assert.Null(occurrences[1].PreviousId);
            Assert.Equal("line_4", occurrences[1].NextId);
            Assert.All(occurrences, o => Assert.True(MakerStudioPaths.IsOperationId(o.OccurrenceId)));
        }

        [Fact]
        public void A_row_without_a_group_or_a_line_and_a_neighbour_the_game_table_lacks_are_not_verified()
        {
            var scenes = Build(
                Row("n1", null, "line_loose"),
                Row("n2", "g1", "line_1"),
                Row("n3", "g1", null),
                Row("n4", "g1", "0"),
                Row("n5", "g1", "ghost_not_a_line"),
                Row("n6", "g1", "line_2"));

            Assert.Empty(scenes.OccurrencesOf("line_loose"));
            Assert.Empty(scenes.OccurrencesOf("ghost_not_a_line"));
            Assert.Equal("line_2", scenes.OccurrencesOf("line_1").Single().NextId);
            Assert.Equal("line_1", scenes.OccurrencesOf("line_2").Single().PreviousId);
        }

        [Fact]
        public void Raw_motion_ids_pass_through_and_mood_and_pose_are_never_inferred()
        {
            var scenes = Build(Row("n1", "g1", "line_1", "Heroine", "12", "0"), Row("n2", "g1", "line_2", null, null, null));

            Assert.True(scenes.TryContextOf("line_1", null, out var context));

            var occurrence = Assert.Single(context.Occurrences);
            Assert.Equal("Heroine", occurrence.Talker);
            Assert.Equal("12", occurrence.BodyMotion);
            Assert.Equal("0", occurrence.FacialMotion);
            Assert.Null(context.Mood);
            Assert.Null(context.Pose);
            Assert.Equal("novel", context.Source);
            Assert.Null(scenes.OccurrencesOf("line_2").Single().Talker);
            Assert.Null(scenes.OccurrencesOf("line_2").Single().BodyMotion);
        }

        [Fact]
        public void The_first_occurrence_is_selected_unless_one_is_asked_for_and_an_unknown_one_is_refused()
        {
            var scenes = Build(Row("n1", "g1", "line_shared"), Row("n2", "g2", "line_shared"));
            var occurrences = scenes.OccurrencesOf("line_shared");

            Assert.True(scenes.TryContextOf("line_shared", null, out var byDefault));
            Assert.Equal(occurrences[0].OccurrenceId, byDefault.SelectedOccurrenceId);
            Assert.True(scenes.TryContextOf("line_shared", occurrences[1].OccurrenceId, out var chosen));
            Assert.Equal(occurrences[1].OccurrenceId, chosen.SelectedOccurrenceId);
            Assert.Equal(2, chosen.Occurrences.Count);
            Assert.False(scenes.TryContextOf("line_shared", new string('f', 32), out _));
        }

        [Fact]
        public void A_line_no_scene_speaks_has_an_empty_context_with_null_source_and_selection()
        {
            var scenes = Build(Row("n1", "g1", "line_1"));

            Assert.True(scenes.TryContextOf("line_unscripted", null, out var context));
            Assert.Empty(context.Occurrences);
            Assert.Null(context.SelectedOccurrenceId);
            Assert.Null(context.Source);
            Assert.False(scenes.TryContextOf("line_unscripted", new string('a', 32), out _));
        }

        [Fact]
        public void Occurrence_ids_are_stable_for_the_same_rows_and_move_when_the_script_changes()
        {
            var one = Build(Row("n1", "g1", "line_1"), Row("n2", "g1", "line_2"));
            var same = Build(Row("n1", "g1", "line_1"), Row("n2", "g1", "line_2"));
            var changed = Build(Row("n0", "g1", "line_0"), Row("n1", "g1", "line_1"), Row("n2", "g1", "line_2"));

            Assert.Equal(one.OccurrencesOf("line_2").Single().OccurrenceId, same.OccurrencesOf("line_2").Single().OccurrenceId);
            Assert.NotEqual(one.OccurrencesOf("line_2").Single().OccurrenceId, changed.OccurrencesOf("line_2").Single().OccurrenceId);
        }

        [Fact]
        public void The_game_snapshot_stamp_covers_novel_rows_and_groups_and_ignores_nothing_it_lists()
        {
            var rows = new[] { new MakerGameRow("line_1", "Voice_1", null, "ja", "en") };
            var plain = new MakerGameData(rows, null, null);
            var scripted = new MakerGameData(rows, null, null, new[] { Row("n1", "g1", "line_1") }, new[] { new MakerGameGroup("g1", "One") });
            var retitled = new MakerGameData(rows, null, null, new[] { Row("n1", "g1", "line_1") }, new[] { new MakerGameGroup("g1", "Uno") });
            var again = new MakerGameData(rows, null, null, new[] { Row("n1", "g1", "line_1") }, new[] { new MakerGameGroup("g1", "One") });

            Assert.NotEqual(plain.Stamp, scripted.Stamp);
            Assert.NotEqual(scripted.Stamp, retitled.Stamp);
            Assert.Equal(scripted.Stamp, again.Stamp);
            Assert.Single(scripted.NovelRows);
            Assert.Equal("One", scripted.Groups.Single().Title);
        }

        [Fact]
        public void A_novel_row_and_a_group_normalise_their_blanks()
        {
            var row = Row("n1", "", "0");

            Assert.Null(row.GroupId);
            Assert.Null(row.LineId);
            Assert.Throws<ArgumentException>(() => new MakerGameGroup("  ", "x"));
            Assert.Null(new MakerGameGroup("g", " ").Title);
        }

        // ----- through the catalog -------------------------------------------------------------------------------

        [Fact]
        public void The_catalog_selects_an_asked_for_occurrence_and_a_foreign_one_is_404()
        {
            var root = Path.Combine(Path.GetTempPath(), "cwy-scene-" + Guid.NewGuid().ToString("N"));
            try
            {
                var folder = Path.Combine(root, "CWYLS Mods", "lab.mine");
                Directory.CreateDirectory(folder);
                var game = new MakerGameData(
                    new[] { new MakerGameRow("line_1", "Voice_1", "g1", "ja", "en"), new MakerGameRow("line_2", "Voice_2", "g1", "ja", "en") },
                    new Dictionary<string, double> { ["Voice_1"] = 1, ["Voice_2"] = 1 }, null,
                    new[] { Row("n1", "g1", "line_1"), Row("n2", "g1", "line_2"), Row("n3", "g2", "line_2") }, null);
                var sources = new List<MakerPackSource> { new MakerPackSource("lab.mine", "Mine", "1.0.0", folder, false, new[] { "th" }) };
                var view = new MakerCatalogView(game, MakerPackScanner.Scan(sources));
                var catalog = new MakerStudioCatalog(new Fixed(new MakerStudioCatalogSnapshot(
                    view, view.Packs(sources, Path.Combine(root, "CWYLS Mods"), MakerPackRules.IsWritableFolder), sources, Path.Combine(root, "CWYLS Mods"))));

                var plain = catalog.LineNow("lab.mine", "th", "line_2", null).Value!;
                var second = plain.Context.Occurrences[1];
                var chosen = catalog.LineNow("lab.mine", "th", "line_2", second.OccurrenceId).Value!;
                var foreign = catalog.LineNow("lab.mine", "th", "line_1", second.OccurrenceId);

                Assert.Equal(plain.Context.Occurrences[0].OccurrenceId, plain.Context.SelectedOccurrenceId);
                Assert.Equal(second.OccurrenceId, chosen.Context.SelectedOccurrenceId);
                Assert.Equal("line_1", plain.Context.Occurrences[0].PreviousId);
                Assert.Null(plain.Context.Occurrences[1].PreviousId);
                Assert.Equal(MakerErrorCodes.NotFound, foreign.Error);
                Assert.Null(chosen.Context.Mood);
                Assert.Null(chosen.Context.Pose);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { /* harmless */ }
            }
        }

        private sealed class Fixed : IMakerStudioCatalogSource
        {
            private readonly MakerStudioCatalogSnapshot _snapshot;
            internal Fixed(MakerStudioCatalogSnapshot snapshot) => _snapshot = snapshot;

            public Task<MakerResult<MakerStudioCatalogSnapshot>> SnapshotAsync(bool editorVerdicts, CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioCatalogSnapshot>.Success(_snapshot));
        }
    }
}
