using CwyLs.ModLoader.Core.Grading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: time-of-day selection (a block overrides the base while the game shows that look)
    /// and interpolation (blend, and the general lerp between two presets).
    /// </summary>
    public class GradingBlendTests
    {
        private static GradingPreset WithBlocks() =>
            GradingPreset.Neutral
                .WithValue("basic.exposure", 0.2)
                .WithValue("whiteBalance.temperature", 20)
                .WithCurve(GradingCurveChannel.Master, new[] { new GradingCurvePoint(0, 20), new GradingCurvePoint(255, 255) })
                .WithBlock(GradingTimeOfDay.Night, GradingPreset.Neutral.WithValue("whiteBalance.temperature", -30).WithValue("blend", 0.5))
                .WithBlock(GradingTimeOfDay.Sunset, GradingPreset.Neutral.WithLutFile("sunset.cube"));

        [Fact]
        public void Without_a_matching_block_the_base_applies()
        {
            var day = WithBlocks().Resolve(GradingTimeOfDay.Day);

            Assert.Equal(20, day.Get("whiteBalance.temperature"));
            Assert.Equal(1, day.BlendWeight);
            Assert.Empty(day.Blocks);
        }

        [Fact]
        public void A_matching_block_overrides_only_what_it_sets()
        {
            var night = WithBlocks().Resolve(GradingTimeOfDay.Night);

            Assert.Equal(-30, night.Get("whiteBalance.temperature"));
            Assert.Equal(0.2, night.Get("basic.exposure"));
            Assert.Equal(0.5, night.BlendWeight);
            Assert.NotNull(night.Curve(GradingCurveChannel.Master));
        }

        [Fact]
        public void A_block_can_bring_its_own_lut()
        {
            Assert.Equal("sunset.cube", WithBlocks().Resolve(GradingTimeOfDay.Sunset).LutFile);
            Assert.Null(WithBlocks().Resolve(GradingTimeOfDay.Day).LutFile);
        }

        [Fact]
        public void Unknown_time_of_day_uses_the_base()
        {
            Assert.Equal(20, WithBlocks().Resolve(GradingTimeOfDay.Unknown).Get("whiteBalance.temperature"));
        }

        [Fact]
        public void Blend_scales_strengths_toward_neutral_and_keeps_shapes()
        {
            var preset = GradingPreset.Neutral
                .WithValue("basic.exposure", 1.0)
                .WithValue("effects.vignette.amount", -40)
                .WithValue("effects.vignette.midpoint", 20)
                .WithValue("splitToning.shadowHue", 200)
                .WithValue("splitToning.shadowSaturation", 30)
                .WithValue("lut.amount", 80)
                .WithValue("blend", 0.5);

            var blended = preset.ApplyBlend();

            Assert.Equal(0.5, blended.Get("basic.exposure"), 9);
            Assert.Equal(-20, blended.Get("effects.vignette.amount"), 9);
            Assert.Equal(20, blended.Get("effects.vignette.midpoint"), 9);
            Assert.Equal(200, blended.Get("splitToning.shadowHue"), 9);
            Assert.Equal(15, blended.Get("splitToning.shadowSaturation"), 9);
            Assert.Equal(90, blended.Get("lut.amount"), 9);
            Assert.False(blended.Has("blend"));
        }

        [Fact]
        public void Blend_moves_curve_points_toward_the_diagonal()
        {
            var preset = GradingPreset.Neutral
                .WithCurve(GradingCurveChannel.Master, new[] { new GradingCurvePoint(0, 40), new GradingCurvePoint(255, 215) })
                .WithValue("blend", 0.25);

            var points = preset.ApplyBlend().Curve(GradingCurveChannel.Master)!;

            Assert.Equal(10, points[0].Y, 9);
            Assert.Equal(245, points[1].Y, 9);
        }

        [Fact]
        public void Blend_of_one_changes_nothing_and_zero_is_the_game_look()
        {
            var preset = GradingPreset.Neutral.WithValue("basic.contrast", 30);

            Assert.Equal(30, preset.ApplyBlend().Get("basic.contrast"));
            Assert.True(GradingMapper.Map(preset.WithValue("blend", 0).ApplyBlend()).IsEmpty);
        }

        [Fact]
        public void Lerp_takes_the_short_way_round_for_hues()
        {
            var a = GradingPreset.Neutral.WithValue("splitToning.highlightHue", 350);
            var b = GradingPreset.Neutral.WithValue("splitToning.highlightHue", 10);

            Assert.Equal(0, GradingPreset.Lerp(a, b, 0.5).Get("splitToning.highlightHue"), 9);
        }

        [Fact]
        public void Lerp_between_two_curves_samples_both()
        {
            var a = GradingPreset.Neutral.WithCurve(GradingCurveChannel.Red, new[] { new GradingCurvePoint(0, 0), new GradingCurvePoint(255, 200) });
            var b = GradingPreset.Neutral.WithCurve(GradingCurveChannel.Red, new[] { new GradingCurvePoint(0, 50), new GradingCurvePoint(255, 255) });

            var mid = GradingPreset.Lerp(a, b, 0.5).Curve(GradingCurveChannel.Red)!;

            Assert.Equal(25, mid[0].Y, 6);
            Assert.Equal(227.5, mid[mid.Count - 1].Y, 6);
        }

        [Fact]
        public void Lerp_endpoints_are_the_inputs()
        {
            var a = GradingPreset.Neutral.WithValue("basic.exposure", -1);
            var b = GradingPreset.Neutral.WithValue("basic.exposure", 2);

            Assert.Equal(-1, GradingPreset.Lerp(a, b, 0).Get("basic.exposure"), 9);
            Assert.Equal(2, GradingPreset.Lerp(a, b, 1).Get("basic.exposure"), 9);
            Assert.Equal(2, GradingPreset.Lerp(a, b, 7).Get("basic.exposure"), 9);
        }

        [Fact]
        public void With_value_clamps_and_presets_are_immutable()
        {
            var original = GradingPreset.Neutral.WithValue("basic.exposure", 1);
            var changed = original.WithValue("basic.exposure", 99);

            Assert.Equal(1, original.Get("basic.exposure"));
            Assert.Equal(5, changed.Get("basic.exposure"));
            Assert.False(GradingPreset.Neutral.Has("basic.exposure"));
        }
    }
}
