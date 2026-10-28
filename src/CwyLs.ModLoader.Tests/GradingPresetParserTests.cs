using System.Linq;
using CwyLs.ModLoader.Core.Grading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: reading <c>grading/&lt;name&gt;.json</c> - the documented shape, what is fatal, what
    /// costs one member, and what is only a warning (clamping, unknown names, Lightroom settings with
    /// no faithful mapping).
    /// </summary>
    public class GradingPresetParserTests
    {
        private const string Full = @"{
  ""schema"": 1,
  ""name"": ""Warm film"",
  ""description"": ""test"",
  ""blend"": 0.8,
  ""basic"": { ""exposure"": 0.15, ""contrast"": 12, ""highlights"": -20, ""shadows"": 15, ""whites"": -10, ""blacks"": 25 },
  ""whiteBalance"": { ""temperature"": 18, ""tint"": 4 },
  ""presence"": { ""vibrance"": 10, ""saturation"": -8 },
  ""hsl"": { ""oranges"": { ""saturation"": -10, ""luminance"": 6 }, ""blues"": { ""hue"": -12 } },
  ""splitToning"": { ""shadowHue"": 190, ""shadowSaturation"": 22, ""highlightHue"": 38, ""highlightSaturation"": 28, ""balance"": 10 },
  ""colorGrading"": { ""shadows"": { ""hue"": 200, ""saturation"": 12 }, ""global"": { ""luminance"": -5 }, ""balance"": 20 },
  ""toneCurve"": { ""master"": [[0, 20], [64, 60], [192, 200], [255, 245]], ""blue"": [[0, 10], [255, 250]] },
  ""effects"": { ""vignette"": { ""amount"": -18, ""midpoint"": 45, ""feather"": 60 }, ""grain"": { ""amount"": 22, ""size"": 30 }, ""bloom"": 15 },
  ""lut"": { ""file"": ""luts/film.cube"", ""amount"": 80 },
  ""night"": { ""basic"": { ""exposure"": 0.3 }, ""blend"": 0.6 },
  ""x-note"": ""mine""
}";

        [Fact]
        public void Reads_the_documented_shape()
        {
            var result = GradingPresetParser.Parse(Full);

            Assert.True(result.Ok);
            Assert.Empty(result.Problems);

            var p = result.Preset!;
            Assert.Equal("Warm film", p.Name);
            Assert.Equal("test", p.Description);
            Assert.Equal(0.8, p.Get("blend"));
            Assert.Equal(0.15, p.Get("basic.exposure"));
            Assert.Equal(25, p.Get("basic.blacks"));
            Assert.Equal(18, p.Get("whiteBalance.temperature"));
            Assert.Equal(-10, p.Get("hsl.oranges.saturation"));
            Assert.Equal(-12, p.Get("hsl.blues.hue"));
            Assert.Equal(190, p.Get("splitToning.shadowHue"));
            Assert.Equal(200, p.Get("colorGrading.shadows.hue"));
            Assert.Equal(-5, p.Get("colorGrading.global.luminance"));
            Assert.Equal(20, p.Get("colorGrading.balance"));
            Assert.Equal(-18, p.Get("effects.vignette.amount"));
            Assert.Equal(30, p.Get("effects.grain.size"));
            Assert.Equal(15, p.Get("effects.bloom"));
            Assert.Equal(80, p.Get("lut.amount"));
            Assert.Equal("luts/film.cube", p.LutFile);
            Assert.Equal(4, p.Curve(GradingCurveChannel.Master)!.Count);
            Assert.Equal(2, p.Curve(GradingCurveChannel.Blue)!.Count);
            Assert.Null(p.Curve(GradingCurveChannel.Red));
            Assert.Single(p.Blocks);
            Assert.Equal(0.3, p.Blocks[GradingTimeOfDay.Night].Get("basic.exposure"));
        }

        [Fact]
        public void An_unset_control_reads_as_its_neutral_value()
        {
            var p = GradingPresetParser.Parse("{\"schema\":1}").Preset!;

            Assert.Equal(0, p.Get("basic.exposure"));
            Assert.Equal(50, p.Get("effects.vignette.midpoint"));
            Assert.Equal(1, p.BlendWeight);
            Assert.False(p.Has("basic.exposure"));
            Assert.True(p.IsEmpty);
        }

        [Theory]
        [InlineData(null, "empty")]
        [InlineData("   ", "empty")]
        [InlineData("[1,2]", "must be a JSON object")]
        [InlineData("{\"schema\":1,", "invalid JSON at")]
        [InlineData("{\"schema\":2}", "not readable by this build")]
        [InlineData("{\"schema\":0}", "not readable by this build")]
        [InlineData("{\"schema\":\"1\"}", "whole number")]
        public void A_broken_document_is_fatal(string? json, string expected)
        {
            var result = GradingPresetParser.Parse(json);

            Assert.False(result.Ok);
            Assert.Contains(result.Problems, p => p.IsError && p.Message.Contains(expected));
        }

        [Fact]
        public void A_missing_schema_is_a_warning_read_as_1()
        {
            var result = GradingPresetParser.Parse("{\"basic\":{\"exposure\":1}}");

            Assert.True(result.Ok);
            Assert.Equal(1, result.WarningCount);
            Assert.Equal("schema", result.Problems[0].Path);
            Assert.Equal(1, result.Preset!.Get("basic.exposure"));
        }

        [Fact]
        public void Out_of_range_values_are_clamped_with_a_warning()
        {
            var result = GradingPresetParser.Parse(
                "{\"schema\":1,\"basic\":{\"exposure\":9,\"contrast\":-300},\"splitToning\":{\"shadowHue\":400}}");

            Assert.True(result.Ok);
            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(3, result.WarningCount);
            Assert.Equal(5, result.Preset!.Get("basic.exposure"));
            Assert.Equal(-100, result.Preset.Get("basic.contrast"));
            Assert.Equal(40, result.Preset.Get("splitToning.shadowHue"), 9);
            Assert.Contains(result.Problems, p => p.Path == "basic.exposure" && p.Message.Contains("clamped to 5"));
            Assert.Contains(result.Problems, p => p.Path == "splitToning.shadowHue" && p.Message.Contains("wrapped"));
        }

        [Fact]
        public void Unsupported_lightroom_settings_are_named_and_ignored()
        {
            var result = GradingPresetParser.Parse(
                "{\"schema\":1,\"presence\":{\"clarity\":30,\"dehaze\":10,\"vibrance\":5},\"effects\":{\"grain\":{\"roughness\":50}}}");

            Assert.True(result.Ok);
            Assert.Equal(0, result.ErrorCount);
            Assert.Contains(result.Problems, p => p.Path == "presence.clarity" && p.Message.Contains("local contrast"));
            Assert.Contains(result.Problems, p => p.Path == "presence.dehaze" && p.Message.Contains("not supported"));
            Assert.Contains(result.Problems, p => p.Path == "effects.grain.roughness");
            Assert.Equal(5, result.Preset!.Get("presence.vibrance"));
            Assert.Single(result.Preset.Values);
        }

        [Fact]
        public void A_wrong_type_costs_only_that_member()
        {
            var result = GradingPresetParser.Parse(
                "{\"schema\":1,\"basic\":{\"exposure\":\"bright\",\"contrast\":10},\"whiteBalance\":7}");

            Assert.True(result.Ok);
            Assert.Equal(2, result.ErrorCount);
            Assert.Equal(10, result.Preset!.Get("basic.contrast"));
            Assert.False(result.Preset.Has("basic.exposure"));
        }

        [Fact]
        public void Unknown_members_are_warnings_and_x_members_are_silent()
        {
            var result = GradingPresetParser.Parse(
                "{\"schema\":1,\"basic\":{\"exposre\":1,\"x-mine\":2},\"sharpen\":3,\"x-tool\":\"lr\"}");

            Assert.True(result.Ok);
            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(2, result.WarningCount);
            Assert.Contains(result.Problems, p => p.Path == "basic.exposre" && p.Message.Contains("unknown"));
            Assert.Contains(result.Problems, p => p.Path == "sharpen");
        }

        [Theory]
        [InlineData("{\"schema\":1,\"toneCurve\":{\"master\":[[0,0]]}}", "2 to 16 points")]
        [InlineData("{\"schema\":1,\"toneCurve\":{\"master\":[[0,0],[0,10]]}}", "share input")]
        [InlineData("{\"schema\":1,\"toneCurve\":{\"master\":[[0,0],[255]]}}", "[input, output]")]
        [InlineData("{\"schema\":1,\"toneCurve\":{\"master\":\"s-curve\"}}", "array of [input, output]")]
        public void A_broken_curve_is_an_error_that_costs_the_curve(string json, string expected)
        {
            var result = GradingPresetParser.Parse(json);

            Assert.True(result.Ok);
            Assert.Contains(result.Problems, p => p.IsError && p.Message.Contains(expected));
            Assert.Null(result.Preset!.Curve(GradingCurveChannel.Master));
        }

        [Fact]
        public void Curve_points_are_sorted_clamped_and_rgb_means_master()
        {
            var result = GradingPresetParser.Parse("{\"schema\":1,\"toneCurve\":{\"rgb\":[[255,300],[0,-5],[128,140]]}}");

            Assert.True(result.Ok);
            Assert.Contains(result.Problems, p => !p.IsError && p.Message.Contains("clamped"));
            var points = result.Preset!.Curve(GradingCurveChannel.Master)!;
            Assert.Equal(new[] { 0.0, 128.0, 255.0 }, points.Select(x => x.X).ToArray());
            Assert.Equal(0, points[0].Y);
            Assert.Equal(255, points[2].Y);
        }

        [Fact]
        public void A_lut_path_outside_the_folder_is_refused()
        {
            var result = GradingPresetParser.Parse("{\"schema\":1,\"lut\":{\"file\":\"../../x.cube\",\"amount\":50}}");

            Assert.True(result.Ok);
            Assert.Null(result.Preset!.LutFile);
            Assert.Equal(50, result.Preset.Get("lut.amount"));
            Assert.Contains(result.Problems, p => p.IsError && p.Path == "lut.file");
        }

        [Fact]
        public void Time_of_day_blocks_take_controls_and_refuse_nesting()
        {
            var result = GradingPresetParser.Parse(
                "{\"schema\":1,\"day\":{\"whiteBalance\":{\"temperature\":-10},\"name\":\"x\",\"night\":{}},\"Sunset\":{\"blend\":0.5}}");

            Assert.True(result.Ok);
            var p = result.Preset!;
            Assert.Equal(2, p.Blocks.Count);
            Assert.Equal(-10, p.Blocks[GradingTimeOfDay.Day].Get("whiteBalance.temperature"));
            Assert.Equal(0.5, p.Blocks[GradingTimeOfDay.Sunset].Get("blend"));
            Assert.Contains(result.Problems, x => x.Path == "day.name" && x.Message.Contains("ignored"));
            Assert.Contains(result.Problems, x => x.Path == "day.night" && x.Message.Contains("nested"));
        }

        [Fact]
        public void Warnings_inside_a_block_carry_the_block_path()
        {
            var result = GradingPresetParser.Parse("{\"schema\":1,\"night\":{\"basic\":{\"exposure\":10}}}");

            Assert.Contains(result.Problems, p => p.Path == "night.basic.exposure" && !p.IsError);
        }
    }
}
