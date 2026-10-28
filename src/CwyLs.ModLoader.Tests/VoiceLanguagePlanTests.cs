using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>Language voice sets: which pack files form a language voice set (<c>audio/voice/&lt;lang&gt;/</c>).</summary>
    public class VoiceLanguagePlanTests
    {
        private static KeyValuePair<string, PackContentIndex> PackOf(string id, params string[] paths) =>
            new KeyValuePair<string, PackContentIndex>(id, PackContentIndex.Build(paths));

        [Fact]
        public void A_language_folder_becomes_a_set_and_stays_out_of_the_global_preload()
        {
            var packs = new[]
            {
                PackOf(
                    "my.thai",
                    "manifest.json",
                    "audio/voice/Voice_Global.ogg",
                    "audio/voice/th/Voice_Tutorial_1_001.ogg",
                    "audio/voice/th/Voice_Tutorial_1_002.ogg"),
            };

            var plan = LanguageVoicePlan.Build(packs);

            Assert.Single(plan.Sets);
            var set = plan.Sets[0];
            Assert.Equal("th", set.Code);
            Assert.Equal(2, set.Count);
            Assert.Equal(new[] { "Voice_Tutorial_1_001", "Voice_Tutorial_1_002" }, set.Steps.Select(s => s.Name));
            Assert.Equal("audio/voice/th/Voice_Tutorial_1_001.ogg", set.Steps[0].RelativePath);
            Assert.Equal(VoiceLoaderKind.EngineAsync, set.Steps[0].Loader);
            Assert.Equal(new[] { "my.thai" }, set.PackIds);
            Assert.Empty(plan.Skipped);

            // The global preload only ever sees the direct child: language clips are never global.
            var global = VoiceLoadPlan.Build(packs);
            Assert.Equal(new[] { "Voice_Global" }, global.Steps.Select(s => s.Name));
        }

        [Theory]
        [InlineData("audio/voice/TH/Voice_A.ogg")]
        [InlineData("audio/voice/thai/Voice_A.ogg")]
        [InlineData("audio/voice/th-TH/Voice_A.ogg")]
        [InlineData("Audio/Voice/th/Voice_A.ogg")]
        public void Folder_spellings_of_one_language_make_one_set(string path)
        {
            var plan = LanguageVoicePlan.Build(new[] { PackOf("p", path) });

            Assert.True(plan.TryGet("th", out var set));
            Assert.Equal("th", set!.Code);
            Assert.True(plan.Has("THAI"));
            Assert.Equal("Voice_A", set.Steps[0].Name);
        }

        [Fact]
        public void Every_refused_file_says_why_and_hidden_files_are_ignored()
        {
            var plan = LanguageVoicePlan.Build(new[]
            {
                PackOf(
                    "p",
                    "audio/voice/th/deeper/Voice_A.ogg",
                    "audio/voice/th/README.md",
                    "audio/voice/th/bad name.ogg",
                    "audio/voice/t h/Voice_B.ogg",
                    "audio/voice/th/.DS_Store",
                    "audio/voice/th/._Voice_C.ogg",
                    "audio/voice/th/Voice_OK.wav"),
            });

            Assert.Equal(new[] { "Voice_OK" }, plan.Sets.Single().Steps.Select(s => s.Name));
            Assert.Equal(VoiceLoaderKind.WavSync, plan.Sets.Single().Steps[0].Loader);

            var reasons = plan.Skipped.ToDictionary(s => s.RelativePath, s => s.Reason);
            Assert.Equal(4, reasons.Count);
            Assert.Contains("nested deeper", reasons["audio/voice/th/deeper/Voice_A.ogg"]);
            Assert.Contains("not a voice format", reasons["audio/voice/th/README.md"]);
            Assert.Contains("not a usable voice name", reasons["audio/voice/th/bad name.ogg"]);
            Assert.Contains("not a language code", reasons["audio/voice/t h/Voice_B.ogg"]);
        }

        [Fact]
        public void A_later_pack_overrides_an_earlier_one_within_a_language()
        {
            var plan = LanguageVoicePlan.Build(new[]
            {
                PackOf("base", "audio/voice/th/Voice_A.ogg", "audio/voice/th/Voice_B.ogg"),
                PackOf("fix", "audio/voice/th/Voice_A.ogg"),
            });

            var set = plan.Sets.Single();
            Assert.Equal(2, set.Count);
            Assert.Equal("fix", set.Find("Voice_A")!.PackId);
            Assert.Equal("base", set.Find("Voice_B")!.PackId);
            Assert.Equal(new[] { "base", "fix" }, set.PackIds);

            var skip = Assert.Single(plan.Skipped);
            Assert.Equal("base", skip.PackId);
            Assert.Equal("overridden by fix/audio/voice/th/Voice_A.ogg", skip.Reason);
        }

        [Fact]
        public void Within_one_pack_the_later_path_wins_so_timing_never_decides()
        {
            // Ordinal path order: ".ogg" < ".wav", so the WAV is the later claim.
            var plan = LanguageVoicePlan.Build(new[]
            {
                PackOf("p", "audio/voice/th/Voice_A.ogg", "audio/voice/th/Voice_A.wav"),
            });

            var step = plan.Sets.Single().Steps.Single();
            Assert.Equal("audio/voice/th/Voice_A.wav", step.RelativePath);
            Assert.Single(plan.Skipped);
        }

        [Fact]
        public void Two_languages_are_two_sets_sorted_by_code_and_counted()
        {
            var plan = LanguageVoicePlan.Build(new[]
            {
                PackOf("p", "audio/voice/th/Voice_A.ogg", "audio/voice/ja/Voice_A.ogg", "audio/voice/ja/Voice_B.ogg"),
            });

            Assert.Equal(new[] { "ja", "th" }, plan.Codes);
            Assert.Equal(3, plan.Count);
            Assert.Equal("ja: 2 clip(s) from p, th: 1 clip(s) from p; 0 skipped", plan.Summarize());
            Assert.False(plan.Has("en"));
            Assert.Null(plan.Sets[1].Find("Voice_B"));
        }

        [Fact]
        public void Nothing_in_nothing_out()
        {
            Assert.Same(LanguageVoicePlan.Empty, LanguageVoicePlan.Build(null));
            Assert.Same(LanguageVoicePlan.Empty, LanguageVoicePlan.Build(new[] { PackOf("p", "audio/voice/Voice_A.ogg") }));
            Assert.Equal(LanguageVoicePlan.NothingSummary, LanguageVoicePlan.Empty.Summarize());

            var blank = new KeyValuePair<string, PackContentIndex>(" ", PackContentIndex.Build(new[] { "audio/voice/th/Voice_A.ogg" }));
            Assert.Empty(LanguageVoicePlan.Build(new[] { blank }).Sets);
        }

        [Theory]
        [InlineData("audio/voice/th/Voice_A.ogg", true, "th", "Voice_A.ogg", null)]
        [InlineData("audio/voice/Voice_A.ogg", false, "", "", null)]
        [InlineData("audio/music/th/x.ogg", false, "", "", null)]
        [InlineData("audio/voice/th/a/b.ogg", false, "", "", "nested deeper than audio/voice/<lang>/; a language set takes direct children only")]
        [InlineData(null, false, "", "", null)]
        public void Paths_split_into_folder_and_file(string? path, bool ok, string folder, string file, string? reason)
        {
            Assert.Equal(ok, LanguageVoicePlan.TryParsePath(path, out var actualFolder, out var actualFile, out var actualReason));
            Assert.Equal(folder, actualFolder);
            Assert.Equal(file, actualFile);
            Assert.Equal(reason, actualReason);
        }

        [Theory]
        [InlineData("th", true, "th")]
        [InlineData("zh-TW", true, "zh-hant")]
        [InlineData("pt", true, "pt-br")]
        [InlineData("x_y", false, "")]
        [InlineData("", false, "")]
        [InlineData("averyveryverylongname", false, "")]
        public void Folder_names_are_normalised_language_codes(string folder, bool ok, string code)
        {
            Assert.Equal(ok, LanguageVoicePlan.TryNormalizeCode(folder, out var actual, out var reason));
            Assert.Equal(code, actual);
            Assert.Equal(ok, reason is null);
        }
    }
}
