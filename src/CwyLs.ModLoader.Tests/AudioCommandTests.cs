using System.Collections.Generic;
using CwyLs.ModLoader.Core.Audio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>D-AUDIO console: 'audio' parsing, row specs for the A/B switch, the list text, loop positions.</summary>
    public class AudioCommandTests
    {
        private static readonly AudioRowInfo LightRain = new AudioRowInfo(
            AudioReplacementPlanTests.LightRain, "cwy.demo.audio/audio/ambient/LightRain.wav", 32.0, AudioVariant.Pack, null, AudioPlaying.Replacement, 1, 0.42f);

        private static readonly AudioRowInfo HeavyRain = new AudioRowInfo(
            AudioReplacementPlanTests.HeavyRain, "cwy.demo.audio/audio/ambient/HeavyRain.wav", 24.576, AudioVariant.Original, null, AudioPlaying.Original, 1, 0f);

        private static readonly AudioRowInfo Thunder = new AudioRowInfo(
            AudioReplacementPlanTests.ThunderRain, null, 0, AudioVariant.Pack, null, AudioPlaying.None, 0, -1f);

        private static readonly AudioRowInfo Click = new AudioRowInfo(
            AudioReplacementPlanTests.Click, null, 0, AudioVariant.Pack, null, AudioPlaying.Unknown, 0, -1f);

        private static readonly AudioRowInfo Main32 = new AudioRowInfo(
            AudioReplacementPlanTests.Main32, null, 0, AudioVariant.Pack, "story music 'main32_Music' plays the soundtrack track 'Without You'", AudioPlaying.None, 0, -1f);

        private static readonly IReadOnlyList<AudioRowInfo> Rows = new[] { LightRain, HeavyRain, Thunder, Click, Main32 };

        [Fact]
        public void No_arguments_is_status()
        {
            Assert.Equal(AudioVerb.Status, AudioCommandParser.Parse(new string[0]).Verb);
            Assert.Equal(AudioVerb.Status, AudioCommandParser.Parse(null).Verb);
        }

        [Theory]
        [InlineData("original", AudioVariant.Original)]
        [InlineData("ORIG", AudioVariant.Original)]
        [InlineData("game", AudioVariant.Original)]
        [InlineData("pack", AudioVariant.Pack)]
        [InlineData("new", AudioVariant.Pack)]
        public void Ab_reads_the_variant(string word, AudioVariant expected)
        {
            var request = AudioCommandParser.Parse(new[] { "ab", "LightRain", word });

            Assert.True(request.IsValid, request.Problem);
            Assert.Equal(AudioVerb.Ab, request.Verb);
            Assert.Equal("LightRain", request.Target);
            Assert.Equal(expected, request.Variant);
        }

        [Fact]
        public void Ab_without_a_variant_toggles()
        {
            Assert.Null(AudioCommandParser.Parse(new[] { "ab", "all" }).Variant);
            Assert.Null(AudioCommandParser.Parse(new[] { "ab", "all", "toggle" }).Variant);
        }

        [Theory]
        [InlineData("ab")]
        [InlineData("ab LightRain louder")]
        [InlineData("ab a b c")]
        [InlineData("play")]
        [InlineData("list a b")]
        [InlineData("reload now")]
        [InlineData("dance")]
        public void Bad_lines_are_refused_with_a_reason(string line)
        {
            var request = AudioCommandParser.Parse(line.Split(' '));

            Assert.False(request.IsValid);
            Assert.NotEqual(string.Empty, request.Problem);
        }

        [Theory]
        [InlineData("list", AudioVerb.List)]
        [InlineData("music", AudioVerb.Music)]
        [InlineData("on", AudioVerb.On)]
        [InlineData("off", AudioVerb.Off)]
        [InlineData("reload", AudioVerb.Reload)]
        [InlineData("help", AudioVerb.Help)]
        [InlineData("status", AudioVerb.Status)]
        public void Simple_verbs_parse(string word, AudioVerb expected)
        {
            var request = AudioCommandParser.Parse(new[] { word });

            Assert.True(request.IsValid, request.Problem);
            Assert.Equal(expected, request.Verb);
        }

        [Fact]
        public void A_spec_finds_a_row_by_name_key_clip_name_or_table()
        {
            Assert.Same(LightRain, Assert.Single(AudioRowSpec.Resolve("lightrain", Rows, false).Rows));
            Assert.Same(LightRain, Assert.Single(AudioRowSpec.Resolve("ambient/LightRain", Rows, false).Rows));
            Assert.Same(Thunder, Assert.Single(AudioRowSpec.Resolve("Rainthunder", Rows, false).Rows));
            Assert.Equal(3, AudioRowSpec.Resolve("ambient", Rows, false).Rows.Count);
            Assert.Equal(5, AudioRowSpec.Resolve("all", Rows, false).Rows.Count);
        }

        [Fact]
        public void The_ab_switch_only_takes_rows_that_have_a_pack_clip()
        {
            var all = AudioRowSpec.Resolve("all", Rows, true);
            Assert.Equal(new[] { LightRain, HeavyRain }, all.Rows);

            var thunder = AudioRowSpec.Resolve("ThunderRain", Rows, true);
            Assert.False(thunder.Ok);
            Assert.Contains("ambient/ThunderRain has no pack clip; nothing to switch", thunder.Problem);

            var paid = AudioRowSpec.Resolve("main32_Music", Rows, true);
            Assert.False(paid.Ok);
            Assert.Contains("is refused", paid.Problem);
        }

        [Fact]
        public void An_unknown_spec_suggests_close_rows()
        {
            var result = AudioRowSpec.Resolve("rain", Rows, false);

            Assert.False(result.Ok);
            Assert.Contains("did you mean ambient/LightRain, ambient/HeavyRain, ambient/ThunderRain", result.Problem);
        }

        [Fact]
        public void The_list_shows_length_pack_selection_and_what_is_playing()
        {
            var text = AudioListFormatter.Format(Rows, null);
            var lines = text.Split('\n');

            Assert.Equal("audio: 5 row(s); 2 replaced (1 using pack, 1 switched to original); 1 refused (paid)", lines[0]);
            Assert.Equal(6, lines.Length);
            Assert.Contains("ambient/LightRain", lines[1]);
            Assert.Contains("32.0s  pack=yes (cwy.demo.audio/audio/ambient/LightRain.wav 32.0s)  using=pack  playing=pack vol=0.42", lines[1]);
            Assert.Contains("using=original  playing=original vol=0.00", lines[2]);
            Assert.EndsWith("pack=no  playing=-", lines[3]);
            Assert.EndsWith("pack=no  playing=?", lines[4]);
            Assert.Contains("pack=REFUSED (paid content)", lines[5]);
        }

        [Fact]
        public void The_list_filters_by_table_or_replacement()
        {
            Assert.Equal(4, AudioListFormatter.Format(Rows, "ambient").Split('\n').Length);
            Assert.Equal(4, AudioListFormatter.Format(Rows, "replaced").Split('\n').Length);
            Assert.StartsWith("unknown filter 'weather'", AudioListFormatter.Format(Rows, "weather"));
            Assert.EndsWith("(no rows)", AudioListFormatter.Format(new AudioRowInfo[0], "all"));
        }

        [Theory]
        [InlineData(12.5, 32.0, true, 12.5)]
        [InlineData(40.0, 32.0, true, 8.0)]
        [InlineData(40.0, 32.0, false, 0.0)]
        [InlineData(10.0, 32.0, false, 10.0)]
        [InlineData(-3.0, 32.0, true, 0.0)]
        [InlineData(5.0, 0.0, true, 0.0)]
        [InlineData(double.NaN, 32.0, true, 0.0)]
        public void A_moved_loop_continues_where_it_was(double position, double length, bool loop, double expected)
        {
            Assert.Equal(expected, LoopPosition.Map(position, length, loop), 6);
        }

        [Fact]
        public void Sample_positions_are_clamped_to_the_clip()
        {
            Assert.Equal(600000, LoopPosition.ToSamples(12.5, 48000, 1536000));
            Assert.Equal(1535999, LoopPosition.ToSamples(40.0, 48000, 1536000));
            Assert.Equal(0, LoopPosition.ToSamples(1.0, 0, 10));
        }
    }
}
