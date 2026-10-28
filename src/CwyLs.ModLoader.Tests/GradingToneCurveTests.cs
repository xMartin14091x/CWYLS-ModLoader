using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Grading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: the tone curve - the monotone spline, the Lightroom-to-URP domain mapping
    /// (display white sits at curve input 0.5 in HDR grading), whites/blacks as levels, vibrance.
    /// </summary>
    public class GradingToneCurveTests
    {
        private static double Eval(IReadOnlyList<CurveKey> keys, double x) => CurveKey.Evaluate(keys, x);

        [Fact]
        public void The_spline_passes_through_its_points_and_is_flat_outside()
        {
            var s = new MonotoneCubic(new[] { 0.2, 0.5, 0.8 }, new[] { 0.1, 0.6, 0.7 });

            Assert.Equal(0.1, s.Evaluate(0.2), 12);
            Assert.Equal(0.6, s.Evaluate(0.5), 12);
            Assert.Equal(0.7, s.Evaluate(0.8), 12);
            Assert.Equal(0.1, s.Evaluate(0.0), 12);
            Assert.Equal(0.7, s.Evaluate(1.0), 12);
        }

        [Fact]
        public void The_spline_never_overshoots_monotone_data()
        {
            var s = new MonotoneCubic(new[] { 0.0, 0.1, 0.2, 1.0 }, new[] { 0.0, 0.0, 0.9, 1.0 });

            var previous = -1.0;
            for (var x = 0.0; x <= 1.0; x += 0.001)
            {
                var y = s.Evaluate(x);
                Assert.True(y >= previous - 1e-12, "not monotone at " + x);
                Assert.InRange(y, -1e-12, 1.0 + 1e-12);
                previous = y;
            }
        }

        [Fact]
        public void A_local_extremum_gets_a_flat_tangent()
        {
            var s = new MonotoneCubic(new[] { 0.0, 0.5, 1.0 }, new[] { 0.0, 1.0, 0.0 });
            Assert.Equal(0, s.Tangent(1), 12);
        }

        [Fact]
        public void The_spline_refuses_unsorted_input()
        {
            Assert.Throws<ArgumentException>(() => new MonotoneCubic(new[] { 0.5, 0.5 }, new[] { 0.0, 1.0 }));
            Assert.Throws<ArgumentException>(() => new MonotoneCubic(new double[0], new double[0]));
        }

        [Fact]
        public void Display_white_is_curve_input_one_half()
        {
            Assert.Equal(0, GradingToneCurves.ToCurveSpace(0), 12);
            Assert.Equal(0.5, GradingToneCurves.ToCurveSpace(1), 12);

            // Mid grey: sRGB 0.5 -> linear 0.214 -> 0.214 / 1.214.
            var lin = GradingColor.SrgbToLinear(0.5);
            Assert.Equal(lin / (1 + lin), GradingToneCurves.ToCurveSpace(0.5), 12);
        }

        [Fact]
        public void An_identity_curve_and_neutral_levels_produce_no_keys()
        {
            var identity = GradingPreset.Neutral.WithCurve(
                GradingCurveChannel.Master, new[] { new GradingCurvePoint(0, 0), new GradingCurvePoint(128, 128), new GradingCurvePoint(255, 255) });

            Assert.Null(GradingToneCurves.Master(identity));
            Assert.Null(GradingToneCurves.Master(GradingPreset.Neutral));
            Assert.Null(GradingToneCurves.Channel(null));
        }

        [Fact]
        public void A_master_curve_is_mapped_and_keeps_the_headroom_an_identity()
        {
            var preset = GradingPreset.Neutral.WithCurve(
                GradingCurveChannel.Master, new[] { new GradingCurvePoint(0, 30), new GradingCurvePoint(255, 255) });

            var keys = GradingToneCurves.Master(preset)!;

            // Display black now lands where display 30/255 would.
            Assert.Equal(GradingToneCurves.ToCurveSpace(30 / 255.0), Eval(keys, 0), 6);

            // Display white is unchanged, and the HDR range above it is an identity up to (1, 1).
            Assert.Equal(0.5, Eval(keys, 0.5), 6);
            Assert.Equal(1.0, keys[keys.Count - 1].Time, 12);
            Assert.Equal(1.0, keys[keys.Count - 1].Value, 12);
            Assert.Equal(0.75, Eval(keys, 0.75), 9);
            Assert.Equal(1.0, keys[keys.Count - 1].InTangent, 9);
        }

        [Fact]
        public void Positive_blacks_fade_and_negative_blacks_crush()
        {
            var faded = GradingToneCurves.Master(GradingPreset.Neutral.WithValue("basic.blacks", 100))!;
            var crushed = GradingToneCurves.Master(GradingPreset.Neutral.WithValue("basic.blacks", -100))!;

            Assert.Equal(GradingToneCurves.ToCurveSpace(GradingToneCurves.LevelsRange), Eval(faded, 0), 6);
            Assert.Equal(0, Eval(crushed, GradingToneCurves.ToCurveSpace(GradingToneCurves.LevelsRange * 0.9)), 6);
        }

        [Fact]
        public void Negative_whites_dim_white_and_positive_whites_clip_sooner()
        {
            var dimmed = GradingToneCurves.Master(GradingPreset.Neutral.WithValue("basic.whites", -100))!;
            var clipped = GradingToneCurves.Master(GradingPreset.Neutral.WithValue("basic.whites", 100))!;

            Assert.Equal(GradingToneCurves.ToCurveSpace(1 - GradingToneCurves.LevelsRange), Eval(dimmed, 0.5), 6);
            Assert.Equal(0.5, Eval(clipped, GradingToneCurves.ToCurveSpace(1 - (GradingToneCurves.LevelsRange * 0.5))), 6);
        }

        [Fact]
        public void Levels_compose_before_the_point_curve()
        {
            GradingToneCurves.Levels(50, -50, out var x0, out var y0, out var x1, out var y1);

            Assert.Equal(0, x0);
            Assert.Equal(0.075, y0, 12);
            Assert.Equal(1, x1);
            Assert.Equal(0.925, y1, 12);
        }

        [Fact]
        public void The_composed_curve_is_monotone_for_an_s_curve()
        {
            var preset = GradingPreset.Neutral
                .WithValue("basic.blacks", 20)
                .WithCurve(GradingCurveChannel.Master, new[] { new GradingCurvePoint(0, 0), new GradingCurvePoint(64, 50), new GradingCurvePoint(192, 210), new GradingCurvePoint(255, 255) });

            var keys = GradingToneCurves.Master(preset)!;
            var previous = -1.0;
            for (var x = 0.0; x <= 1.0; x += 0.002)
            {
                var y = Eval(keys, x);
                Assert.True(y >= previous - 1e-9, "not monotone at " + x);
                previous = y;
            }
        }

        [Fact]
        public void Key_evaluation_matches_the_spline_when_tangents_agree()
        {
            var spline = new MonotoneCubic(new[] { 0.0, 0.3, 1.0 }, new[] { 0.1, 0.5, 0.9 });
            var keys = CurveKey.FromSpline(spline);

            for (var x = 0.0; x <= 1.0; x += 0.05)
            {
                Assert.Equal(spline.Evaluate(x), Eval(keys, x), 12);
            }
        }

        [Fact]
        public void Vibrance_boosts_greys_most_and_leaves_full_saturation_alone()
        {
            var keys = GradingToneCurves.Vibrance(100)!;

            Assert.Equal((1 + GradingToneCurves.VibranceBoost) / 2, keys[0].Value, 9);
            Assert.Equal(0.5, keys[keys.Count - 1].Value, 9);
            Assert.Null(GradingToneCurves.Vibrance(0));

            var cut = GradingToneCurves.Vibrance(-100)!;
            Assert.Equal((1 - GradingToneCurves.VibranceCut) / 2, cut[0].Value, 9);
        }
    }
}
