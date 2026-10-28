using System.Collections.Generic;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>P1-4: which pack files a preload run will load, and through which loader.</summary>
    public class VoiceLoadPlanTests
    {
        private static KeyValuePair<string, PackContentIndex> PackOf(string id, params string[] paths) =>
            new KeyValuePair<string, PackContentIndex>(id, PackContentIndex.Build(paths));

        [Theory]
        [InlineData(".wav", VoiceLoaderKind.WavSync)]
        [InlineData(".WAV", VoiceLoaderKind.WavSync)]
        [InlineData(".ogg", VoiceLoaderKind.EngineAsync)]
        public void Picks_the_loader_from_the_extension(string extension, VoiceLoaderKind expected)
        {
            Assert.True(VoiceLoadPlan.TryLoaderFor(extension, out var loader));
            Assert.Equal(expected, loader);
        }

        [Theory]
        [InlineData(".mp3")]
        [InlineData("")]
        [InlineData(null)]
        public void Refuses_an_extension_it_does_not_load(string? extension) =>
            Assert.False(VoiceLoadPlan.TryLoaderFor(extension, out _));

        [Fact]
        public void Plans_one_step_per_voice_file_and_counts_the_formats()
        {
            var plan = VoiceLoadPlan.Build(new[]
            {
                PackOf(
                    "cwy.example",
                    "manifest.json",
                    "audio/voice/Voice_CWY_Test.wav",
                    "audio/voice/Voice_CWY_Test_Ogg.ogg",
                    "audio/music/track.ogg",
                    "textures/Body/_MainTex.png",
                    "README.md"),
            });

            Assert.Equal(2, plan.Count);
            Assert.Equal(1, plan.WavCount);
            Assert.Equal(1, plan.OggCount);
            Assert.Empty(plan.Skipped);
            Assert.Equal("2 clip(s) to load: 1 ogg, 1 wav; 0 skipped", plan.Summarize());

            // The index is ordinal by path, and '.' sorts before '_', so the WAV comes first.
            var wav = plan.Steps[0];
            Assert.Equal("Voice_CWY_Test", wav.Name);
            Assert.Equal(VoiceLoaderKind.WavSync, wav.Loader);
            Assert.Equal("cwy.example", wav.PackId);
            Assert.Equal("audio/voice/Voice_CWY_Test.wav", wav.RelativePath);

            Assert.Equal("Voice_CWY_Test_Ogg", plan.Steps[1].Name);
            Assert.Equal(VoiceLoaderKind.EngineAsync, plan.Steps[1].Loader);
        }

        [Fact]
        public void Only_the_last_pack_to_claim_a_name_is_loaded()
        {
            var plan = VoiceLoadPlan.Build(new[]
            {
                PackOf("cwy.first", "audio/voice/Voice_A.ogg", "audio/voice/Voice_B.ogg"),
                PackOf("cwy.second", "audio/voice/Voice_A.ogg"),
            });

            Assert.Equal(2, plan.Count);
            Assert.Equal(new[] { "Voice_B", "Voice_A" }, new[] { plan.Steps[0].Name, plan.Steps[1].Name });
            Assert.Equal("cwy.second", plan.Steps[1].PackId);

            Assert.Single(plan.Skipped);
            Assert.Equal("cwy.first", plan.Skipped[0].PackId);
            Assert.Equal("overridden by cwy.second/audio/voice/Voice_A.ogg", plan.Skipped[0].Reason);
        }

        [Fact]
        public void A_wav_and_an_ogg_of_one_name_resolve_to_the_wav_whatever_the_decoders_do()
        {
            var plan = VoiceLoadPlan.Build(new[]
            {
                PackOf("cwy.example", "audio/voice/Voice_A.ogg", "audio/voice/Voice_A.wav"),
            });

            Assert.Single(plan.Steps);
            Assert.Equal(VoiceLoaderKind.WavSync, plan.Steps[0].Loader);
            Assert.Single(plan.Skipped);
            Assert.Equal("audio/voice/Voice_A.ogg", plan.Skipped[0].RelativePath);
        }

        [Fact]
        public void Skips_a_file_whose_name_is_not_a_voice_name_and_keeps_the_rest()
        {
            var plan = VoiceLoadPlan.Build(new[]
            {
                PackOf("cwy.example", "audio/voice/my voice.wav", "audio/voice/Voice_Ok.wav"),
            });

            Assert.Single(plan.Steps);
            Assert.Equal("Voice_Ok", plan.Steps[0].Name);
            Assert.Single(plan.Skipped);
            Assert.Equal("audio/voice/my voice.wav", plan.Skipped[0].RelativePath);
            Assert.Contains(VoiceNameRules.Pattern, plan.Skipped[0].Reason);
            Assert.Equal("1 clip(s) to load: 0 ogg, 1 wav; 1 skipped", plan.Summarize());
        }

        [Fact]
        public void An_empty_or_missing_pack_list_plans_nothing()
        {
            Assert.Same(VoiceLoadPlan.Empty, VoiceLoadPlan.Build(null));
            Assert.Same(VoiceLoadPlan.Empty, VoiceLoadPlan.Build(new KeyValuePair<string, PackContentIndex>[0]));
            Assert.Same(VoiceLoadPlan.Empty, VoiceLoadPlan.Build(new[] { PackOf("cwy.example", "manifest.json") }));
            Assert.Equal(VoiceLoadPlan.NothingToLoadSummary, VoiceLoadPlan.Empty.Summarize());
            Assert.Equal(0, VoiceLoadPlan.Empty.Count);
        }
    }
}
