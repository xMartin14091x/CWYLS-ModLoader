using System;
using CwyLs.ModLoader.Core.Grading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: <c>grade save</c> / <c>grade export</c> write what the parser reads, and the text is
    /// a fixed point: write, parse, write again gives the same document.
    /// </summary>
    public class GradingPresetRoundTripTests
    {
        private static GradingPreset Sample() =>
            GradingPreset.Neutral
                .WithName("Round trip", "every kind of member")
                .WithValue("basic.exposure", 0.35)
                .WithValue("basic.contrast", -12)
                .WithValue("whiteBalance.temperature", 22.5)
                .WithValue("presence.vibrance", 8)
                .WithValue("hsl.greens.hue", -30)
                .WithValue("hsl.blues.luminance", -25)
                .WithValue("splitToning.shadowHue", 200)
                .WithValue("splitToning.shadowSaturation", 18)
                .WithValue("colorGrading.highlights.hue", 40)
                .WithValue("colorGrading.highlights.saturation", 10)
                .WithValue("effects.vignette.amount", -25)
                .WithValue("effects.grain.amount", 15)
                .WithValue("effects.bloom", -40)
                .WithValue("lut.amount", 70)
                .WithValue("blend", 0.75)
                .WithCurve(GradingCurveChannel.Master, new[] { new GradingCurvePoint(0, 18), new GradingCurvePoint(128, 132.5), new GradingCurvePoint(255, 240) })
                .WithCurve(GradingCurveChannel.Red, new[] { new GradingCurvePoint(0, 0), new GradingCurvePoint(255, 250) })
                .WithLutFile("luts/look.cube")
                .WithBlock(GradingTimeOfDay.Night, GradingPreset.Neutral.WithValue("basic.exposure", 0.5).WithValue("blend", 0.5))
                .WithBlock(GradingTimeOfDay.Day, GradingPreset.Neutral.WithCurve(GradingCurveChannel.Blue, new[] { new GradingCurvePoint(0, 5), new GradingCurvePoint(255, 255) }));

        [Fact]
        public void A_written_preset_reads_back_the_same()
        {
            var preset = Sample();

            var text = GradingPresetWriter.Write(preset);
            var result = GradingPresetParser.Parse(text);

            Assert.True(result.Ok);
            Assert.Empty(result.Problems);
            Assert.True(preset.SameAs(result.Preset), text);
        }

        [Fact]
        public void Writing_is_a_fixed_point()
        {
            var first = GradingPresetWriter.Write(Sample());
            var second = GradingPresetWriter.Write(GradingPresetParser.Parse(first).Preset!);

            Assert.Equal(first, second);
        }

        [Fact]
        public void Only_what_was_set_is_written_and_the_schema_always_is()
        {
            var text = GradingPresetWriter.Write(GradingPreset.Neutral.WithValue("presence.saturation", -20));

            Assert.Contains("\"schema\": 1", text);
            Assert.Contains("\"saturation\": -20", text);
            Assert.DoesNotContain("exposure", text);
            Assert.DoesNotContain("toneCurve", text);
            Assert.DoesNotContain("lut", text);
        }

        [Fact]
        public void Numbers_are_invariant_and_short()
        {
            Assert.Equal("0.3333", GradingPresetWriter.Number(1.0 / 3.0));
            Assert.Equal("-12", GradingPresetWriter.Number(-12.0));
            Assert.Equal("0", GradingPresetWriter.Number(-0.00001));
            Assert.Equal("1.5", GradingPresetWriter.Number(1.5));
        }

        [Fact]
        public void A_name_with_quotes_survives()
        {
            var preset = GradingPreset.Neutral.WithName("say \"hi\"\\ok", "line\nbreak");
            var back = GradingPresetParser.Parse(GradingPresetWriter.Write(preset)).Preset!;

            Assert.Equal(preset.Name, back.Name);
            Assert.Equal(preset.Description, back.Description);
        }

        [Fact]
        public void Null_is_refused()
        {
            Assert.Throws<ArgumentNullException>(() => GradingPresetWriter.Write(null!));
        }
    }
}
