using System.Collections.Generic;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>DS-02: the per-line rules (filters, language codes, omitted and runtime rows, search) and the two small caches.</summary>
    public class MakerCatalogRulesTests
    {
        [Theory]
        [InlineData(null, MakerLineFilter.All)]
        [InlineData("", MakerLineFilter.All)]
        [InlineData("  ", MakerLineFilter.All)]
        [InlineData("all", MakerLineFilter.All)]
        [InlineData("missing-subtitle", MakerLineFilter.MissingSubtitle)]
        [InlineData("missing-dub", MakerLineFilter.MissingDub)]
        [InlineData("done", MakerLineFilter.Done)]
        [InlineData(" done ", MakerLineFilter.Done)]
        public void The_four_contract_filters_parse(string? value, MakerLineFilter expected)
        {
            Assert.True(MakerLineRules.TryParseFilter(value, out var filter));
            Assert.Equal(expected, filter);
        }

        [Theory]
        [InlineData("Done")]
        [InlineData("missing")]
        [InlineData("missing_dub")]
        [InlineData("omitted")]
        [InlineData("*")]
        public void Any_other_filter_is_refused(string value)
        {
            Assert.False(MakerLineRules.TryParseFilter(value, out _));
        }

        [Theory]
        [InlineData("th", "th")]
        [InlineData("pt-br", "pt-br")]
        [InlineData("zh-hant", "zh-hant")]
        [InlineData("fil", "fil")]
        [InlineData("TH", "th")]
        [InlineData(" th ", "th")]
        public void Language_codes_follow_the_contract_pattern(string value, string expected)
        {
            Assert.True(MakerLineRules.TryParseLang(value, out var lang));
            Assert.Equal(expected, lang);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("t")]
        [InlineData("thai")]
        [InlineData("th_TH")]
        [InlineData("th-")]
        [InlineData("th-x")]
        [InlineData("../th")]
        [InlineData("th/../en")]
        [InlineData("th-abcdefghi")]
        public void Anything_else_is_not_a_language_code(string? value)
        {
            Assert.False(MakerLineRules.TryParseLang(value, out var lang));
            Assert.Equal(string.Empty, lang);
        }

        [Fact]
        public void Omitted_ids_end_in_omit_and_runtime_rows_start_with_the_say_prefix()
        {
            Assert.True(MakerLineRules.IsOmittedId("sample_intro_003_omit"));
            Assert.True(MakerLineRules.IsOmittedId("sample_intro_003_OMIT"));
            Assert.False(MakerLineRules.IsOmittedId("sample_omitted_003"));
            Assert.False(MakerLineRules.IsOmittedId(null));

            Assert.True(MakerLineRules.IsRuntimeRow("cwy_say_12_001"));
            Assert.False(MakerLineRules.IsRuntimeRow("sample_cwy_say_12"));
            Assert.False(MakerLineRules.IsRuntimeRow(""));
        }

        [Fact]
        public void A_line_needs_a_subtitle_for_text_and_a_dub_for_a_voice()
        {
            var voiced = MakerLineState.Of(false, "おはよう", "Morning", "Voice_A", null, null);
            Assert.True(voiced.NeedsSubtitle);
            Assert.True(voiced.NeedsDub);
            Assert.False(voiced.HasSubtitle);
            Assert.False(voiced.HasDub);

            var textOnly = MakerLineState.Of(false, "", "Settings", null, null, null);
            Assert.True(textOnly.NeedsSubtitle);
            Assert.False(textOnly.NeedsDub);

            var grunt = MakerLineState.Of(false, "", " ", "Voice_Grunt", null, null);
            Assert.False(grunt.NeedsSubtitle);
            Assert.True(grunt.NeedsDub);

            var omitted = MakerLineState.Of(true, "おはよう", "Morning", "Voice_A", null, null);
            Assert.True(omitted.Omitted);
            Assert.False(omitted.NeedsSubtitle);
            Assert.False(omitted.NeedsDub);

            var empty = MakerLineState.Of(false, "おはよう", "Morning", "Voice_A", "", "");
            Assert.False(empty.HasSubtitle);
            Assert.False(empty.HasDub);
        }

        [Fact]
        public void Filters_partition_what_a_line_still_needs()
        {
            var bare = MakerLineState.Of(false, "a", "b", "Voice_A", null, null);
            var subtitled = MakerLineState.Of(false, "a", "b", "Voice_A", "x", null);
            var dubbed = MakerLineState.Of(false, "a", "b", "Voice_A", null, "pack");
            var finished = MakerLineState.Of(false, "a", "b", "Voice_A", "x", "pack");
            var textDone = MakerLineState.Of(false, "a", "b", null, "x", null);
            var gruntDone = MakerLineState.Of(false, "", "", "Voice_G", null, "pack");
            var nothing = MakerLineState.Of(false, "", "", null, null, null);
            var omitted = MakerLineState.Of(true, "a", "b", "Voice_A", null, null);

            Assert.Equal(new[] { true, false, true, false, false, false, false, false }, Each(MakerLineFilter.MissingSubtitle));
            Assert.Equal(new[] { true, true, false, false, false, false, false, false }, Each(MakerLineFilter.MissingDub));
            Assert.Equal(new[] { false, false, false, true, true, true, false, false }, Each(MakerLineFilter.Done));
            Assert.Equal(new[] { true, true, true, true, true, true, true, true }, Each(MakerLineFilter.All));

            bool[] Each(MakerLineFilter filter) => new[]
            {
                MakerLineRules.Matches(bare, filter),
                MakerLineRules.Matches(subtitled, filter),
                MakerLineRules.Matches(dubbed, filter),
                MakerLineRules.Matches(finished, filter),
                MakerLineRules.Matches(textDone, filter),
                MakerLineRules.Matches(gruntDone, filter),
                MakerLineRules.Matches(nothing, filter),
                MakerLineRules.Matches(omitted, filter),
            };
        }

        [Fact]
        public void Search_ignores_case_for_latin_letters_and_matches_other_scripts_exactly()
        {
            Assert.True(MakerLineRules.MatchesSearch("GOOD", "sample_001", null, "おはよう", "Good morning"));
            Assert.True(MakerLineRules.MatchesSearch("voice_sample", "sample_001", "Voice_Sample_001"));
            Assert.True(MakerLineRules.MatchesSearch("おは", "sample_001", null, "おはよう"));
            Assert.True(MakerLineRules.MatchesSearch("สวัสดี", "sample_001", null, null, null, "สวัสดีตอนเช้า"));
            Assert.False(MakerLineRules.MatchesSearch("night", "sample_001", "Voice_Sample_001", "おはよう", "Good morning", null));
            Assert.True(MakerLineRules.MatchesSearch(string.Empty, "anything"));
            Assert.False(MakerLineRules.MatchesSearch("x", null, null));
        }

        [Fact]
        public void The_spoken_cache_keeps_only_the_games_own_clip_lengths()
        {
            var cache = new MakerSpokenCache();
            cache.Note("Voice_A", "sample_a", "story_a", 2.34567, gameClip: true);
            cache.Note("Voice_B", "sample_b", "clickReaction", 4.0, gameClip: false);
            cache.Note("Voice_C", null, null, 0d, gameClip: true);
            cache.Note(null, "sample_d", "  ", 1d, gameClip: true);

            Assert.True(cache.TryGetDuration("Voice_A", out var a));
            Assert.Equal(2.346, a);
            Assert.False(cache.TryGetDuration("Voice_B", out _));
            Assert.False(cache.TryGetDuration("Voice_C", out _));
            Assert.Equal(1, cache.DurationCount);

            Assert.True(cache.TryGetGroup("sample_a", out var groupA));
            Assert.Equal("story_a", groupA);
            Assert.True(cache.TryGetGroup("sample_b", out var groupB));
            Assert.Equal("clickReaction", groupB);
            Assert.False(cache.TryGetGroup("sample_d", out _));
            Assert.False(cache.TryGetGroup(null, out _));
        }

        [Fact]
        public void Game_data_drops_repeated_ids_and_treats_an_empty_clip_cache_as_unknown()
        {
            var rows = new List<MakerGameRow?>
            {
                new MakerGameRow("sample_a", "Voice_A", "story_a", "あ", "A"),
                null,
                new MakerGameRow("sample_a", "Voice_Other", null, "い", "I"),
                new MakerGameRow("sample_b", "", "", null, null),
            };

            var data = new MakerGameData(rows, new Dictionary<string, double>(), null);
            Assert.Equal(2, data.Rows.Count);
            Assert.True(data.TryFind("sample_a", out var a));
            Assert.Equal("Voice_A", a!.Voice);
            Assert.True(data.TryFind("sample_b", out var b));
            Assert.Null(b!.Voice);
            Assert.Null(b.Group);
            Assert.Equal(string.Empty, b.Ja);
            Assert.False(data.ClipCacheKnown);
            Assert.True(data.IsLoaded);

            var clips = new MakerGameData(rows, new Dictionary<string, double> { ["Voice_A"] = 0d, ["Voice_B"] = 1.5 }, new[] { "Voice_R" });
            Assert.True(clips.ClipCacheKnown);
            Assert.True(clips.HasGameClip("Voice_A"));
            Assert.False(clips.TryGetClipLength("Voice_A", out _));
            Assert.True(clips.TryGetClipLength("Voice_B", out var length));
            Assert.Equal(1.5, length);
            Assert.True(clips.IsRegistered("Voice_R"));

            var notLoaded = new MakerGameData(null, null, null, "not loaded yet");
            Assert.False(notLoaded.IsLoaded);
            Assert.Empty(notLoaded.Rows);
        }
    }
}
