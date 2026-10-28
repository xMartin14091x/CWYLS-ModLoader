using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Audio;
using CwyLs.ModLoader.Core.Loaders;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-AUDIO's demo pack, checked as files: a valid manifest, two rain loops that replace the game's
    /// LightRain/HeavyRain rows with the original's exact rate and length and a seamless loop point, and
    /// one music track that the paid-content rule lets through against a music list.
    /// </summary>
    public class AudioDemoPackTests
    {
        /// <summary>
        /// A stand-in for the game's music list: two original-soundtrack titles and one guest title
        /// written with TMP rich text, as some of the game's titles are.
        /// </summary>
        internal static readonly SoundtrackCatalog GameMusic = new SoundtrackCatalog(new[]
        {
            ("Placeholder Morning", true), ("Placeholder Evening", true),
            ("Guest Tune <size=14.5>-Placeholder Remix-</size>", false),
        }.Select(t => new SoundtrackTrack(t.Item1, string.Empty, t.Item1, string.Empty, t.Item2)));

        private static string PackRoot =>
            Path.Combine(TestRepo.Root ?? throw new InvalidOperationException("CwyLs.ModLoader.sln not found"), "packs", "cwy.demo.audio");

        private static PackContentIndex Index()
        {
            var root = PackRoot;
            return PackContentIndex.Build(
                Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)));
        }

        [Fact]
        public void The_stand_in_music_list_counts_its_originals_and_finds_a_rich_text_title()
        {
            Assert.Equal(3, GameMusic.Tracks.Count);
            Assert.Equal(2, GameMusic.OriginalCount);
            Assert.NotNull(GameMusic.FindExact("guest tune -Placeholder Remix-"));
        }

        [Fact]
        public void The_manifest_is_valid()
        {
            var manifest = PackManifestParser.Parse(File.ReadAllText(Path.Combine(PackRoot, "manifest.json")));

            Assert.Equal("cwy.demo.audio", manifest.Id);
        }

        [Fact]
        public void The_rain_loops_replace_the_two_rain_rows()
        {
            var plan = AudioReplacementPlan.Build(
                AudioCandidate.FromPacks(new[] { new KeyValuePair<string, PackContentIndex>("cwy.demo.audio", Index()) }),
                new[] { AudioReplacementPlanTests.LightRain, AudioReplacementPlanTests.HeavyRain, AudioReplacementPlanTests.ThunderRain },
                GameMusic);

            Assert.Equal(new[] { "ambient/LightRain", "ambient/HeavyRain" }, plan.Assignments.Select(a => a.Row.Key));
            Assert.Empty(plan.Skipped);
        }

        [Theory]
        [InlineData("LightRain.wav", 1536000)]
        [InlineData("HeavyRain.wav", 1179648)]
        public void Each_loop_has_the_originals_rate_and_length_and_a_seamless_loop_point(string file, int originalFrames)
        {
            var bytes = File.ReadAllBytes(Path.Combine(PackRoot, "audio", "ambient", file));

            Assert.True(WavParser.TryParse(bytes, out var wav, out var error), error);
            Assert.Equal(48000, wav!.SampleRate);
            Assert.Equal(2, wav.Channels);
            Assert.Equal(originalFrames, wav.Frames);

            // The jump from the last frame back to the first must look like any step inside the loop.
            var steps = new List<float>(wav.Frames);
            for (var i = 1; i < wav.Frames; i++)
            {
                steps.Add(Math.Abs(wav.Samples[i * 2] - wav.Samples[(i - 1) * 2]));
            }

            steps.Sort();
            var typical = steps[(int)(steps.Count * 0.95)];
            var wrap = Math.Abs(wav.Samples[0] - wav.Samples[(wav.Frames - 1) * 2]);
            Assert.True(wrap <= 2 * typical, $"loop seam {wrap / typical:0.00} x a typical step");
        }

        [Fact]
        public void The_music_track_is_listed_and_not_a_relisted_game_track()
        {
            var index = Index();
            var plan = PackMusicPlan.Build(
                new[] { new PackMusicInput("cwy.demo.audio", index, File.ReadAllText(Path.Combine(PackRoot, "audio", "music", "_tracks.json"))) },
                GameMusic);

            var track = Assert.Single(plan.Tracks);
            Assert.Equal("Rainy Desk (CWY demo)", track.Title);
            Assert.Equal(PackMusicTag.Other, track.Tag);
            Assert.StartsWith("cwy-", track.Uuid);
            Assert.Empty(plan.Skipped);
        }
    }
}
