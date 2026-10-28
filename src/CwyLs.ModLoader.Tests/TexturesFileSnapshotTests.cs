using System.Collections.Generic;
using CwyLs.ModLoader.Core.Textures;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The pure half of the texture live-reload: what a reading of the pack texture files says
    /// changed since the last one. This is what the reload's log line and its smoke check report,
    /// so the counts and the wording are both pinned here.
    /// </summary>
    public class TexturesFileSnapshotTests
    {
        private static TextureFileSnapshot Snapshot(params (string Path, long Stamp)[] files)
        {
            var pairs = new List<KeyValuePair<string, long>>(files.Length);
            foreach (var file in files)
            {
                pairs.Add(new KeyValuePair<string, long>(file.Path, file.Stamp));
            }

            return TextureFileSnapshot.Of(pairs);
        }

        [Fact]
        public void A_reading_of_nothing_is_the_empty_one()
        {
            Assert.Equal(0, TextureFileSnapshot.Of(null).Count);
            Assert.Equal(0, TextureFileSnapshot.Of(new List<KeyValuePair<string, long>>()).Count);
            Assert.Same(TextureFileSnapshot.Empty, TextureFileSnapshot.Of(null));
        }

        [Fact]
        public void A_blank_path_is_not_a_file()
        {
            var reading = Snapshot(("", 5L), ("a/b.png", 7L));

            Assert.Equal(1, reading.Count);
            Assert.Equal(7L, reading.StampOf("a/b.png"));
        }

        [Fact]
        public void An_unknown_path_has_the_missing_stamp()
        {
            var reading = Snapshot(("a/b.png", 7L));

            Assert.Equal(TextureFileSnapshot.MissingStamp, reading.StampOf("a/c.png"));
            Assert.Equal(TextureFileSnapshot.MissingStamp, reading.StampOf(null));
        }

        [Fact]
        public void Two_identical_readings_report_nothing_moved()
        {
            var before = Snapshot(("a/b.png", 7L), ("a/_rules.json", 9L));
            var after = Snapshot(("a/b.png", 7L), ("a/_rules.json", 9L));

            var diff = after.DiffFrom(before);

            Assert.True(diff.IsEmpty);
            Assert.Equal(2, diff.Total);
            Assert.Equal("2 file(s) (0 changed on disk, 0 new, 0 removed)", diff.Summarize());
        }

        [Fact]
        public void A_rewritten_file_is_changed_not_new()
        {
            // The editing case the whole feature exists for: same path, later timestamp.
            var diff = Snapshot(("a/b.png", 8L)).DiffFrom(Snapshot(("a/b.png", 7L)));

            Assert.Equal(new[] { "a/b.png" }, diff.Changed);
            Assert.Empty(diff.Added);
            Assert.Empty(diff.Removed);
            Assert.False(diff.IsEmpty);
        }

        [Fact]
        public void An_added_and_a_deleted_file_are_each_counted_once()
        {
            var before = Snapshot(("a/b.png", 7L), ("a/gone.png", 7L));
            var after = Snapshot(("a/b.png", 7L), ("a/new.png", 11L));

            var diff = after.DiffFrom(before);

            Assert.Equal(new[] { "a/new.png" }, diff.Added);
            Assert.Equal(new[] { "a/gone.png" }, diff.Removed);
            Assert.Empty(diff.Changed);
            Assert.Equal("2 file(s) (0 changed on disk, 1 new, 1 removed): +a/new.png, -a/gone.png", diff.Summarize());
        }

        [Fact]
        public void The_first_reading_of_a_session_reports_every_file_as_new()
        {
            var diff = Snapshot(("a/b.png", 7L)).DiffFrom(null);

            Assert.Equal(new[] { "a/b.png" }, diff.Added);
            Assert.Equal(1, diff.Total);
        }

        [Fact]
        public void Paths_are_sorted_so_the_same_change_reads_the_same_way_twice()
        {
            var after = Snapshot(("z.png", 2L), ("a.png", 2L), ("m.png", 2L));

            var diff = after.DiffFrom(TextureFileSnapshot.Empty);

            Assert.Equal(new[] { "a.png", "m.png", "z.png" }, diff.Added);
        }

        [Fact]
        public void A_long_list_names_the_first_few_and_counts_the_rest()
        {
            var files = new List<KeyValuePair<string, long>>();
            for (var i = 0; i < TextureFileDiff.MaxNamedPaths + 3; i++)
            {
                files.Add(new KeyValuePair<string, long>("f" + i + ".png", 1L));
            }

            var diff = TextureFileSnapshot.Of(files).DiffFrom(TextureFileSnapshot.Empty);

            Assert.Contains("and 3 more", diff.Summarize());
            Assert.Equal(TextureFileDiff.MaxNamedPaths + 3, diff.Added.Count);
        }

        [Fact]
        public void A_file_that_could_not_be_stated_still_counts_as_a_file()
        {
            // StampOf returns MissingStamp for an unreadable file; it must not vanish from the
            // reading, or every poll would report it as removed and reload for ever.
            var before = Snapshot(("a/b.png", TextureFileSnapshot.MissingStamp));
            var after = Snapshot(("a/b.png", TextureFileSnapshot.MissingStamp));

            Assert.True(after.DiffFrom(before).IsEmpty);
            Assert.Equal(1, after.Count);
        }

        [Fact]
        public void The_counts_line_is_the_one_the_reload_logs()
        {
            var diff = new TextureFileDiff(3, new[] { "a" }, new[] { "b" }, new[] { "c", "d" });

            Assert.Equal("3 file(s) (1 changed on disk, 1 new, 2 removed)", diff.Counts());
            Assert.Equal(diff.Summarize(), diff.ToString());
        }

        [Fact]
        public void A_difference_built_from_nulls_is_empty_rather_than_broken()
        {
            var diff = new TextureFileDiff(-4, null, null, null);

            Assert.True(diff.IsEmpty);
            Assert.Equal(0, diff.Total);
            Assert.True(TextureFileDiff.None.IsEmpty);
        }
    }
}
