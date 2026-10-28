using System;
using CwyLs.ModLoader.Core.Grading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: the colour arithmetic behind the grading layer - sRGB transfer, HSV/HSL, and the
    /// HSL colour mixer that is baked into the LUT.
    /// </summary>
    public class GradingColorTests
    {
        [Theory]
        [InlineData(0.0)]
        [InlineData(0.02)]
        [InlineData(0.5)]
        [InlineData(1.0)]
        public void Srgb_transfer_round_trips(double v)
        {
            Assert.Equal(v, GradingColor.LinearToSrgb(GradingColor.SrgbToLinear(v)), 12);
        }

        [Fact]
        public void Srgb_mid_grey_is_about_0_214_linear()
        {
            Assert.Equal(0.2140, GradingColor.SrgbToLinear(0.5), 4);
        }

        [Theory]
        [InlineData(0, 1, 0, 0)]
        [InlineData(120, 0, 1, 0)]
        [InlineData(240, 0, 0, 1)]
        [InlineData(60, 1, 1, 0)]
        public void Hsv_primary_hues(double hue, double r, double g, double b)
        {
            var c = GradingColor.FromHsv(hue, 1, 1);
            Assert.Equal(r, c.R, 12);
            Assert.Equal(g, c.G, 12);
            Assert.Equal(b, c.B, 12);

            GradingColor.ToHsv(c, out var h, out var s, out var v);
            Assert.Equal(hue, h, 9);
            Assert.Equal(1, s, 12);
            Assert.Equal(1, v, 12);
        }

        [Fact]
        public void Hsl_at_saturation_zero_is_its_lightness_grey()
        {
            var c = GradingColor.FromHsl(300, 0, 0.5);
            Assert.Equal(Rgb.Grey(0.5), c);
        }

        [Fact]
        public void Hsl_band_weights_are_a_partition_of_unity()
        {
            var w = new double[8];
            for (var h = -30.0; h < 400; h += 7.5)
            {
                HslAdjustment.Weights(h, w);
                var sum = 0.0;
                foreach (var x in w)
                {
                    Assert.True(x >= 0);
                    sum += x;
                }

                Assert.Equal(1.0, sum, 12);
            }
        }

        [Fact]
        public void A_band_centre_belongs_to_that_band_only()
        {
            var w = new double[8];
            HslAdjustment.Weights(240, w);
            Assert.Equal(1.0, w[5], 12);

            HslAdjustment.Weights(45, w);
            Assert.Equal(0.5, w[1], 12);
            Assert.Equal(0.5, w[2], 12);
        }

        private static HslAdjustment Band(int band, double hue, double sat, double lum)
        {
            var h = new double[8];
            var s = new double[8];
            var l = new double[8];
            h[band] = hue;
            s[band] = sat;
            l[band] = lum;
            return new HslAdjustment(h, s, l);
        }

        [Fact]
        public void Greys_never_move()
        {
            var hsl = Band(5, 100, 100, -100);
            foreach (var v in new[] { 0.0, 0.3, 0.7, 1.0 })
            {
                Assert.Equal(Rgb.Grey(v), hsl.Apply(Rgb.Grey(v)));
            }
        }

        [Fact]
        public void Blue_luminance_minus_darkens_blue_and_leaves_red()
        {
            var hsl = Band(5, 0, 0, -100);
            var blue = new Rgb(0.2, 0.3, 0.9);
            var red = new Rgb(0.9, 0.2, 0.15);

            var darker = hsl.Apply(blue);
            Assert.True(GradingColor.Luminance(GradingColor.ToLinear(darker)) < GradingColor.Luminance(GradingColor.ToLinear(blue)));
            Assert.Equal(red, hsl.Apply(red));
        }

        [Fact]
        public void Saturation_minus_100_greys_the_band()
        {
            var hsl = Band(3, 0, -100, 0);
            var green = GradingColor.FromHsv(120, 0.8, 0.7);

            GradingColor.ToHsv(hsl.Apply(green), out _, out var s, out var v);
            Assert.Equal(0, s, 9);
            Assert.Equal(0.7, v, 9);
        }

        [Fact]
        public void Hue_plus_turns_reds_toward_orange()
        {
            var hsl = Band(0, 100, 0, 0);
            GradingColor.ToHsv(hsl.Apply(GradingColor.FromHsv(0, 1, 1)), out var h, out _, out _);
            Assert.Equal(HslAdjustment.MaxHueShiftDegrees, h, 6);
        }

        [Fact]
        public void A_neutral_preset_has_no_hsl()
        {
            Assert.Null(HslAdjustment.FromPreset(GradingPreset.Neutral));
            Assert.NotNull(HslAdjustment.FromPreset(GradingPreset.Neutral.WithValue("hsl.aquas.hue", 5)));
        }

        [Fact]
        public void Wrong_array_sizes_are_refused()
        {
            Assert.Throws<ArgumentException>(() => new HslAdjustment(new double[7], new double[8], new double[8]));
        }

        [Fact]
        public void The_cache_key_changes_with_any_value()
        {
            Assert.NotEqual(Band(1, 10, 0, 0).CacheKey(), Band(1, 11, 0, 0).CacheKey());
            Assert.Equal(Band(1, 10, 0, 0).CacheKey(), Band(1, 10, 0, 0).CacheKey());
        }
    }
}
