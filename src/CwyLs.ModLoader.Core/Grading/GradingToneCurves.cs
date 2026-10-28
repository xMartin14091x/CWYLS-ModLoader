using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// Turns Lightroom-style tone controls into the keys of URP <c>ColorCurves</c>.
    /// <para>
    /// <b>The domain is the trap.</b> Lightroom's curve takes display values (0..255, gamma
    /// encoded). URP's HDR LUT builder (this game grades in HDR: <c>m_ColorGradingMode: 1</c> in
    /// every <c>PC_URP-*.asset</c>) evaluates the YRGB curves on <c>FastTonemap(linear)</c> =
    /// <c>c / (1 + max(r, g, b))</c>, so display white (linear 1) sits at curve input 0.5 and the
    /// upper half of the curve is HDR headroom. A Lightroom point (x, y) is therefore placed at
    /// (<see cref="ToCurveSpace"/>(x), <see cref="ToCurveSpace"/>(y)), and a key at (1, 1) keeps
    /// the headroom an identity. Exact for greys; for saturated colours the shared denominator
    /// makes it an approximation.
    /// </para>
    /// </summary>
    public static class GradingToneCurves
    {
        /// <summary>
        /// How far <c>basic.blacks</c>/<c>basic.whites</c> at +/-100 move the black or white point, in
        /// display units (0.15 = 38 of 255).
        /// </summary>
        public const double LevelsRange = 0.15;

        /// <summary>How many evenly spaced display values a composed curve is sampled at (plus its own knots).</summary>
        public const int Samples = 17;

        /// <summary>Vibrance +100 multiplies the saturation of a grey-ish colour by 1 + this.</summary>
        public const double VibranceBoost = 0.6;

        /// <summary>Vibrance -100 multiplies the saturation of a grey-ish colour by 1 - this.</summary>
        public const double VibranceCut = 0.8;

        /// <summary>
        /// A display value (0..1, sRGB-encoded) as URP's curve input: linearise, then
        /// <c>L / (1 + L)</c>. 0 maps to 0, display white to 0.5.
        /// </summary>
        /// <param name="display">Display value.</param>
        public static double ToCurveSpace(double display)
        {
            var linear = GradingColor.SrgbToLinear(GradingColor.Clamp01(display));
            return linear / (1 + linear);
        }

        /// <summary>
        /// The master curve: <c>basic.blacks</c> and <c>basic.whites</c> as levels, then the
        /// <c>toneCurve.master</c> points, composed and mapped into URP's curve space. Null when
        /// both are neutral.
        /// </summary>
        /// <param name="preset">A flat, blended preset.</param>
        public static IReadOnlyList<CurveKey>? Master(GradingPreset preset)
        {
            if (preset is null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            var blacks = preset.Get("basic.blacks");
            var whites = preset.Get("basic.whites");
            var points = preset.Curve(GradingCurveChannel.Master);

            if (blacks == 0 && whites == 0 && GradingCurves.IsIdentity(points))
            {
                return null;
            }

            Levels(blacks, whites, out var x0, out var y0, out var x1, out var y1);
            var curve = GradingCurves.AsFunction(points);
            Func<double, double> levels = v =>
                y0 + ((y1 - y0) * GradingColor.Clamp01((v - x0) / (x1 - x0)));

            var knots = new List<double> { x0, x1 };
            AddKnots(knots, points);
            return Compose(v => curve(levels(v)), knots);
        }

        /// <summary>One of the red/green/blue point curves, mapped into URP's curve space; null when unset or an identity.</summary>
        /// <param name="points">The channel's points.</param>
        public static IReadOnlyList<CurveKey>? Channel(IReadOnlyList<GradingCurvePoint>? points)
        {
            if (GradingCurves.IsIdentity(points))
            {
                return null;
            }

            var curve = GradingCurves.AsFunction(points);
            var knots = new List<double>();
            AddKnots(knots, points);
            return Compose(curve, knots);
        }

        /// <summary>
        /// The <c>satVsSat</c> curve for <c>presence.vibrance</c>: a saturation multiplier that is
        /// largest for greyish colours and fades to 1 for fully saturated ones; stored halved (URP
        /// multiplies the curve value by 2). Null at 0.
        /// </summary>
        /// <param name="vibrance">-100..100.</param>
        public static IReadOnlyList<CurveKey>? Vibrance(double vibrance)
        {
            if (vibrance == 0)
            {
                return null;
            }

            var amount = vibrance / 100.0;
            var xs = new[] { 0.0, 0.125, 0.25, 0.5, 0.75, 1.0 };
            var ys = new double[xs.Length];
            for (var i = 0; i < xs.Length; i++)
            {
                var falloff = (1 - xs[i]) * (1 - xs[i]);
                var multiplier = amount >= 0
                    ? 1 + (VibranceBoost * amount * falloff)
                    : 1 + (VibranceCut * amount * falloff);
                ys[i] = Math.Max(0, multiplier) / 2.0;
            }

            return CurveKey.FromSpline(new MonotoneCubic(xs, ys));
        }

        /// <summary>The levels implied by blacks and whites: input x0..x1 maps to output y0..y1.</summary>
        /// <param name="blacks">-100..100.</param>
        /// <param name="whites">-100..100.</param>
        /// <param name="x0">Input black point.</param>
        /// <param name="y0">Output black point.</param>
        /// <param name="x1">Input white point.</param>
        /// <param name="y1">Output white point.</param>
        public static void Levels(double blacks, double whites, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = blacks < 0 ? (-blacks / 100.0) * LevelsRange : 0;
            y0 = blacks > 0 ? (blacks / 100.0) * LevelsRange : 0;
            x1 = whites > 0 ? 1 - ((whites / 100.0) * LevelsRange) : 1;
            y1 = whites < 0 ? 1 + ((whites / 100.0) * LevelsRange) : 1;
        }

        private static void AddKnots(List<double> knots, IReadOnlyList<GradingCurvePoint>? points)
        {
            if (points is null)
            {
                return;
            }

            foreach (var point in points)
            {
                knots.Add(point.X / GradingCurvePoint.Max);
            }
        }

        /// <summary>
        /// Samples a display-space function at evenly spaced values plus its knots, maps every sample
        /// into curve space, adds the (1, 1) headroom key, and fits monotone tangents.
        /// </summary>
        private static IReadOnlyList<CurveKey> Compose(Func<double, double> display, List<double> knots)
        {
            var inputs = new SortedSet<double>();
            for (var i = 0; i < Samples; i++)
            {
                inputs.Add(i / (double)(Samples - 1));
            }

            foreach (var knot in knots)
            {
                inputs.Add(GradingColor.Clamp01(knot));
            }

            var xs = new List<double>(inputs.Count + 1);
            var ys = new List<double>(inputs.Count + 1);
            foreach (var v in inputs)
            {
                var x = ToCurveSpace(v);
                if (xs.Count > 0 && x - xs[xs.Count - 1] < 1e-7)
                {
                    continue;
                }

                xs.Add(x);
                ys.Add(ToCurveSpace(display(v)));
            }

            // Headroom: display white sits at 0.5; above it the curve runs straight to (1, 1).
            xs.Add(1.0);
            ys.Add(1.0);

            var keys = new List<CurveKey>(CurveKey.FromSpline(new MonotoneCubic(xs.ToArray(), ys.ToArray())));

            // Make the headroom segment a straight line (an identity when display white is left
            // alone): Unity keys carry separate in- and out-tangents, so the kink sits exactly at
            // display white and the display range keeps its monotone shape.
            var n = keys.Count;
            var white = keys[n - 2];
            var slope = (1.0 - white.Value) / (1.0 - white.Time);
            keys[n - 2] = new CurveKey(white.Time, white.Value, white.InTangent, slope);
            keys[n - 1] = new CurveKey(1.0, 1.0, slope, slope);
            return keys;
        }
    }
}
