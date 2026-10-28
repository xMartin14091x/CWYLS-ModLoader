using System.Linq;
using CwyLs.ModLoader.Core.Audio;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>D-AUDIO pack music: the track list, the plan, the cwy- UUIDs and where tracks land in the list.</summary>
    public class AudioPackMusicTests
    {
        [Fact]
        public void A_full_track_list_parses()
        {
            var result = MusicTrackList.Parse(
                "{\"schema\":1,\"x-note\":\"mine\",\"tracks\":[" +
                "{\"file\":\"rainy-desk.ogg\",\"title\":\" Rainy Desk \",\"credit\":\"CWY demo\",\"tag\":\"Special\",\"x-bpm\":72}]}");

            Assert.Empty(result.Errors);
            var entry = Assert.Single(result.Entries);
            Assert.Equal("rainy-desk.ogg", entry.File);
            Assert.Equal("Rainy Desk", entry.Title);
            Assert.Equal("CWY demo", entry.Credit);
            Assert.Equal(PackMusicTag.Special, entry.Tag);
        }

        [Fact]
        public void A_bare_array_is_accepted_and_the_defaults_apply()
        {
            var result = MusicTrackList.Parse("[{\"file\":\"a.wav\"}]");

            Assert.Empty(result.Errors);
            var entry = Assert.Single(result.Entries);
            Assert.Equal("a", entry.Title);
            Assert.Equal(string.Empty, entry.Credit);
            Assert.Equal(PackMusicTag.Other, entry.Tag);
        }

        [Theory]
        [InlineData("{\"schema\":2,\"tracks\":[]}", "schema: must be 1")]
        [InlineData("{\"schema\":1}", "tracks: missing")]
        [InlineData("{\"schema\":1,\"tracks\":[],\"tittle\":1}", "tittle: unknown member")]
        [InlineData("[{\"file\":\"../x.ogg\"}]", "must be a plain file name")]
        [InlineData("[{\"file\":\"sub/x.ogg\"}]", "must be a plain file name")]
        [InlineData("[{\"file\":\"C:x.ogg\"}]", "must be a plain file name")]
        [InlineData("[{\"title\":\"x\"}]", "file: required")]
        [InlineData("[{\"file\":\"a.ogg\",\"tag\":\"favorite\"}]", "tag: must be")]
        [InlineData("[{\"file\":\"a.ogg\",\"uuid\":\"1a2b3c4d-0000-4000-8000-00000000a002\"}]", "packs do not choose UUIDs")]
        [InlineData("[{\"file\":\"a.ogg\",\"title\":\"  \"}]", "title: must not be blank")]
        [InlineData("[{\"file\":\"a.ogg\"},{\"file\":\"A.ogg\"}]", "is listed twice")]
        [InlineData("not json", "1:2: expected 'null'")]
        public void Track_list_mistakes_are_reported(string json, string expected)
        {
            var result = MusicTrackList.Parse(json);

            Assert.Contains(result.Errors, e => e.Contains(expected));
        }

        [Fact]
        public void Every_supported_music_file_becomes_a_track_titled_by_the_list_or_its_name()
        {
            var plan = PackMusicPlan.Build(
                new[]
                {
                    new PackMusicInput(
                        "cwy.demo.audio",
                        PackContentIndex.Build(new[]
                        {
                            "audio/music/_tracks.json", "audio/music/rainy-desk.ogg", "audio/music/b-side.wav",
                            "audio/music/README.md", "audio/music/.gitkeep", "audio/music/old.mp3",
                        }),
                        "{\"schema\":1,\"tracks\":[{\"file\":\"rainy-desk.ogg\",\"title\":\"Rainy Desk\"},{\"file\":\"gone.ogg\",\"title\":\"Gone\"}]}"),
                },
                AudioReplacementPlanTests.Soundtrack);

            Assert.Equal(new[] { "b-side", "Rainy Desk" }, plan.Tracks.Select(t => t.Title));
            Assert.All(plan.Tracks, t => Assert.Equal(PackMusicTag.Other, t.Tag));

            // The mp3 gets a conversion hint; the list entry without a file is reported; notes are quiet.
            Assert.Contains(plan.Skipped, s => s.Path == "audio/music/old.mp3" && s.Reason.Contains("convert it to .ogg or .wav"));
            Assert.Contains(plan.Skipped, s => s.Reason.Contains("'gone.ogg', which is not in audio/music/"));
            Assert.DoesNotContain(plan.Skipped, s => s.Path.EndsWith("README.md") || s.Path.EndsWith(".gitkeep"));
            Assert.Equal(0, plan.RefusedCount);
        }

        [Fact]
        public void An_unreadable_track_list_is_a_warning_and_the_files_still_load()
        {
            var plan = PackMusicPlan.Build(
                new[] { new PackMusicInput("p", PackContentIndex.Build(new[] { "audio/music/a.ogg" }), null, "access denied") },
                null);

            Assert.Single(plan.Tracks);
            Assert.Contains(plan.Skipped, s => s.Reason.Contains("access denied") && s.IsWarning);
        }

        [Fact]
        public void Pack_uuids_are_stable_prefixed_and_distinct()
        {
            var uuid = PackMusicUuid.For("cwy.demo.audio", "audio/music/rainy-desk.ogg");

            Assert.StartsWith("cwy-", uuid);
            Assert.Equal(36, uuid.Length);
            Assert.Equal(uuid, PackMusicUuid.For("CWY.Demo.Audio", "audio\\music\\Rainy-Desk.ogg"));
            Assert.NotEqual(uuid, PackMusicUuid.For("other.pack", "audio/music/rainy-desk.ogg"));
            Assert.NotEqual(uuid, PackMusicUuid.For("cwy.demo.audio", "audio/music/rainy-desk.wav"));
            Assert.True(PackMusicUuid.IsPackUuid(uuid));
            Assert.True(PackMusicUuid.IsPackUuid("CWY-ABC"));
            Assert.False(PackMusicUuid.IsPackUuid("1a2b3c4d-0000-4000-8000-00000000a002"));
            Assert.False(PackMusicUuid.IsPackUuid(null));
        }

        [Fact]
        public void New_pack_tracks_go_after_the_game_tracks_never_first()
        {
            var placed = PackMusicPlacement.Place(new[] { "g1", "g2", "g3" }, new[] { "g1", "g2", "g3" }, new[] { "cwy-a", "cwy-b" });

            Assert.Equal(new[] { "g1", "g2", "g3", "cwy-a", "cwy-b" }, placed);
        }

        [Fact]
        public void A_remembered_position_is_kept()
        {
            // The game sorts by playlist index; "new" is not in it (-1) and comes first.
            var placed = PackMusicPlacement.Place(
                new[] { "new", "g1", "g2", "g3" },
                new[] { "g1", "cwy-a", "g2", "g3", "cwy-b" },
                new[] { "cwy-b", "cwy-a", "cwy-c" });

            Assert.Equal(new[] { "new", "g1", "cwy-a", "g2", "g3", "cwy-b", "cwy-c" }, placed);
        }

        [Fact]
        public void Placement_never_duplicates()
        {
            var placed = PackMusicPlacement.Place(new[] { "g1" }, null, new[] { "cwy-a", "cwy-a", "g1" });

            Assert.Equal(new[] { "g1", "cwy-a" }, placed);
        }
    }
}
