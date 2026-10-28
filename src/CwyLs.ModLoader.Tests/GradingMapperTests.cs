using System;
using CwyLs.ModLoader.Core.Grading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: Lightroom units to URP overrides - which component each control lands in, the
    /// exact mappings (exposure in EV, white balance and saturation 1:1), and the tuned ones
    /// (contrast, wheels, vignette, grain, bloom).
    /// </summary>
    public class GradingMapperTests
    {
        private static GradingPlan Map(params (string Id, double Value)[] values)
        {
            var preset = GradingPreset.Neutral;
            foreach (var (id, value) in values)
            {
                preset = preset.WithValue(id, value);
            }

            return GradingMapper.Map(preset.ApplyBlend());
        }

        [Fact]
        public void A_neutral_preset_overrides_nothing()
        {
            var plan = GradingMapper.Map(GradingPreset.Neutral);

            Assert.True(plan.IsEmpty);
            Assert.Equal("none", plan.Describe());
            Assert.False(plan.NeedsLut);
        }

        [Fact]
        public void Controls_set_to_neutral_override_nothing()
        {
            var plan = Map(("basic.exposure", 0), ("effects.vignette.midpoint", 30), ("splitToning.shadowHue", 200));

            Assert.True(plan.IsEmpty);
        }

        [Fact]
        public void Exposure_white_balance_and_saturation_map_one_to_one()
        {
            var plan = Map(("basic.exposure", 1.25), ("whiteBalance.temperature", 30), ("whiteBalance.tint", -15), ("presence.saturation", -40));

            Assert.Equal(1.25, plan.PostExposure);
            Assert.Equal(30, plan.Temperature);
            Assert.Equal(-15, plan.Tint);
            Assert.Equal(-40, plan.Saturation);
            Assert.Equal("ColorAdjustments+WhiteBalance", plan.Describe());
        }

        [Fact]
        public void Contrast_is_scaled()
        {
            Assert.Equal(50, Map(("basic.contrast", 100)).Contrast);
            Assert.Equal(-25, Map(("basic.contrast", -50)).Contrast);
        }

        [Fact]
        public void Split_toning_saturation_zero_is_urp_neutral_grey()
        {
            var tint = GradingMapper.SplitTint(123, 0);

            Assert.Equal(0.5, tint.R, 9);
            Assert.Equal(0.5, tint.G, 9);
            Assert.Equal(0.5, tint.B, 9);
        }

        [Fact]
        public void Split_toning_maps_hue_and_saturation_to_a_tint()
        {
            var plan = Map(("splitToning.shadowHue", 200), ("splitToning.shadowSaturation", 40), ("splitToning.highlightHue", 35), ("splitToning.highlightSaturation", 30), ("splitToning.balance", 10));

            Assert.NotNull(plan.SplitToning);
            var shadows = plan.SplitToning!.Shadows;
            var highlights = plan.SplitToning.Highlights;
            Assert.True(shadows.B > shadows.R, "teal shadows are bluer than red: " + shadows);
            Assert.True(highlights.R > highlights.B, "warm highlights are redder than blue: " + highlights);
            Assert.Equal(10, plan.SplitToning.Balance);
        }

        [Fact]
        public void Split_toning_with_no_saturation_is_off()
        {
            Assert.Null(Map(("splitToning.balance", 50)).SplitToning);
        }

        [Fact]
        public void Basic_shadows_and_highlights_drive_the_smh_luminance()
        {
            var plan = Map(("basic.shadows", 100), ("basic.highlights", -100));

            Assert.NotNull(plan.ShadowsMidtonesHighlights);
            var smh = plan.ShadowsMidtonesHighlights!;
            Assert.Equal(GradingMapper.LiftScale, smh.Shadows.W, 9);
            Assert.Equal(-GradingMapper.DarkenScale, smh.Highlights.W, 9);
            Assert.Equal(0, smh.Midtones.W, 9);
            Assert.Equal(1, smh.Shadows.X, 9);
            Assert.Null(smh.ShadowsEnd);
        }

        [Fact]
        public void A_wheel_tints_its_range_and_the_global_wheel_tints_all()
        {
            var plan = Map(("colorGrading.highlights.hue", 30), ("colorGrading.highlights.saturation", 60), ("colorGrading.global.luminance", 20));

            var smh = plan.ShadowsMidtonesHighlights!;
            Assert.True(smh.Highlights.X > smh.Highlights.Z, "orange highlights: " + smh.Highlights);
            Assert.Equal(1, smh.Shadows.X, 9);
            Assert.Equal(GradingMapper.LuminanceOffset(20), smh.Midtones.W, 9);
            Assert.Equal(GradingMapper.LuminanceOffset(20), smh.Shadows.W, 9);
        }

        [Fact]
        public void A_wheel_at_saturation_zero_is_neutral()
        {
            var m = GradingMapper.WheelMultiplier(240, 0);
            Assert.Equal(Rgb.Grey(1), m);
        }

        [Fact]
        public void Wheel_balance_moves_the_ranges()
        {
            var plus = Map(("colorGrading.shadows.saturation", 20), ("colorGrading.balance", 100)).ShadowsMidtonesHighlights!;
            var minus = Map(("colorGrading.shadows.saturation", 20), ("colorGrading.balance", -100)).ShadowsMidtonesHighlights!;

            Assert.True(plus.HighlightsStart < GradingMapper.DefaultHighlightsStart);
            Assert.True(minus.HighlightsStart > GradingMapper.DefaultHighlightsStart);
            Assert.True(plus.ShadowsEnd < minus.ShadowsEnd);
        }

        [Fact]
        public void Luminance_offsets_are_plus_one_stop_and_minus_one_stop()
        {
            // URP: multiplier = 1 + w * (w < 0 ? 1 : 4).
            var lift = GradingMapper.LuminanceOffset(100);
            var darken = GradingMapper.LuminanceOffset(-100);

            Assert.Equal(2.0, 1 + (lift * 4), 9);
            Assert.Equal(0.5, 1 + darken, 9);
        }

        [Fact]
        public void A_darkening_vignette_has_a_dark_edge_and_a_lightening_one_a_bright_edge()
        {
            var dark = Map(("effects.vignette.amount", -60)).Vignette!;
            var light = Map(("effects.vignette.amount", 50)).Vignette!;

            Assert.Equal(0.4, dark.Color.R, 9);
            Assert.True(light.Color.R > 1);
            Assert.False(dark.Rounded);
        }

        [Fact]
        public void Vignette_midpoint_feather_and_roundness_shape_it()
        {
            var near = Map(("effects.vignette.amount", -50), ("effects.vignette.midpoint", 0), ("effects.vignette.feather", 0), ("effects.vignette.roundness", 80)).Vignette!;
            var far = Map(("effects.vignette.amount", -50), ("effects.vignette.midpoint", 100), ("effects.vignette.feather", 100)).Vignette!;

            Assert.True(near.Intensity > far.Intensity);
            Assert.True(near.Smoothness < far.Smoothness);
            Assert.True(near.Rounded);
            Assert.InRange(far.Smoothness, 0.01, 1.0);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(25, 2)]
        [InlineData(50, 5)]
        [InlineData(100, 9)]
        public void Grain_size_picks_a_texture(double size, int index)
        {
            Assert.Equal(index, GradingMapper.GrainType(size));
        }

        [Fact]
        public void Grain_amount_is_intensity_and_zero_is_off()
        {
            var plan = Map(("effects.grain.amount", 40), ("effects.grain.size", 50));

            Assert.Equal(0.4, plan.FilmGrain!.Intensity, 9);
            Assert.Equal(5, plan.FilmGrain.TypeIndex);
            Assert.Null(Map(("effects.grain.size", 80)).FilmGrain);
        }

        [Theory]
        [InlineData(-100, 0)]
        [InlineData(-50, 0.5)]
        [InlineData(100, 2)]
        public void Bloom_is_a_factor_on_the_games_own_bloom(double bloom, double factor)
        {
            Assert.Equal(factor, Map(("effects.bloom", bloom)).BloomFactor!.Value, 9);
        }

        [Fact]
        public void Hsl_or_a_lut_file_needs_a_lut()
        {
            Assert.True(Map(("hsl.blues.luminance", -30)).NeedsLut);

            var withFile = GradingMapper.Map(GradingPreset.Neutral.WithLutFile("a.cube"));
            Assert.True(withFile.NeedsLut);
            Assert.Equal(1.0, withFile.LutAmount);

            var zeroAmount = GradingMapper.Map(GradingPreset.Neutral.WithLutFile("a.cube").WithValue("lut.amount", 0));
            Assert.False(zeroAmount.NeedsLut);
        }

        [Fact]
        public void Vibrance_lands_in_sat_vs_sat_and_whites_in_the_master_curve()
        {
            var plan = Map(("presence.vibrance", 50), ("basic.whites", 30));

            Assert.NotNull(plan.SatVsSatCurve);
            Assert.NotNull(plan.MasterCurve);
            Assert.Null(plan.RedCurve);
            Assert.Equal("ColorCurves", plan.Describe());
        }

        [Fact]
        public void Null_is_refused()
        {
            Assert.Throws<ArgumentNullException>(() => GradingMapper.Map(null!));
        }
    }
}
