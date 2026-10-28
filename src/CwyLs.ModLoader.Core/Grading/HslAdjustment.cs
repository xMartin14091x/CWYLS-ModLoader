using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// Lightroom's HSL / colour mixer: hue, saturation and luminance for eight colour bands, as a
    /// pure function on display (sRGB-encoded) colours. It is baked into the LUT that URP's
    /// <c>ColorLookup</c> applies (URP's <c>ColorCurves</c> has hue-vs-hue and hue-vs-saturation but
    /// no hue-vs-luminance, so one baked function keeps the three consistent).
    /// <para>
    /// Band centres (degrees): reds 0, oranges 30, yellows 60, greens 120, aquas 180, blues 240,
    /// purples 270, magentas 300. Between two neighbouring centres the weights cross-fade with a
    /// smoothstep, so the weights always sum to 1 and a colour halfway between orange and yellow
    /// gets half of each band's adjustment.
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Hue -100..100 turns the band up to <see cref="MaxHueShiftDegrees"/> toward its neighbour (reds +100 toward orange).</description></item>
    ///   <item><description>Saturation -100..100 multiplies HSV saturation by 0..2.</description></item>
    ///   <item><description>Luminance -100..100 scales the linear colour by 1 -/+ <see cref="LuminanceScale"/>, weighted by the colour's saturation so greys never move.</description></item>
    /// </list>
    /// </summary>
    public sealed class HslAdjustment
    {
        /// <summary>The largest hue turn of a band at +/-100, in degrees.</summary>
        public const double MaxHueShiftDegrees = 30.0;

        /// <summary>The linear brightness change of a fully saturated colour at luminance +/-100.</summary>
        public const double LuminanceScale = 0.6;

        /// <summary>Below this HSV saturation a colour counts as grey for luminance (full effect from here up).</summary>
        public const double SaturationKnee = 0.25;

        /// <summary>Band centres in degrees, in <see cref="GradingControls.HslBands"/> order.</summary>
        public static IReadOnlyList<double> BandCenters { get; } = new[] { 0.0, 30.0, 60.0, 120.0, 180.0, 240.0, 270.0, 300.0 };

        private readonly double[] _hue;
        private readonly double[] _saturation;
        private readonly double[] _luminance;

        /// <summary>Creates an adjustment from per-band values (-100..100 each), in band order.</summary>
        /// <param name="hue">Eight hue values.</param>
        /// <param name="saturation">Eight saturation values.</param>
        /// <param name="luminance">Eight luminance values.</param>
        /// <exception cref="ArgumentException">An array does not have eight entries.</exception>
        public HslAdjustment(double[] hue, double[] saturation, double[] luminance)
        {
            var bands = GradingControls.HslBands.Count;
            if (hue is null || saturation is null || luminance is null ||
                hue.Length != bands || saturation.Length != bands || luminance.Length != bands)
            {
                throw new ArgumentException("an HSL adjustment needs " + bands + " values per property");
            }

            _hue = (double[])hue.Clone();
            _saturation = (double[])saturation.Clone();
            _luminance = (double[])luminance.Clone();
        }

        /// <summary>
        /// The HSL part of a flat preset, or null when every band is neutral.
        /// </summary>
        /// <param name="preset">A flat (resolved, blended) preset.</param>
        public static HslAdjustment? FromPreset(GradingPreset preset)
        {
            if (preset is null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            var bands = GradingControls.HslBands;
            var hue = new double[bands.Count];
            var sat = new double[bands.Count];
            var lum = new double[bands.Count];
            var any = false;

            for (var i = 0; i < bands.Count; i++)
            {
                hue[i] = preset.Get(GradingControls.HslId(bands[i], "hue"));
                sat[i] = preset.Get(GradingControls.HslId(bands[i], "saturation"));
                lum[i] = preset.Get(GradingControls.HslId(bands[i], "luminance"));
                any |= hue[i] != 0 || sat[i] != 0 || lum[i] != 0;
            }

            return any ? new HslAdjustment(hue, sat, lum) : null;
        }

        /// <summary>
        /// The weight of each band at a hue: two neighbouring bands share it with a smoothstep
        /// cross-fade, so the weights are non-negative and sum to exactly 1.
        /// </summary>
        /// <param name="hueDegrees">Any hue; wrapped.</param>
        /// <param name="weights">Receives eight weights.</param>
        /// <exception cref="ArgumentException"><paramref name="weights"/> is shorter than eight.</exception>
        public static void Weights(double hueDegrees, double[] weights)
        {
            var count = BandCenters.Count;
            if (weights is null || weights.Length < count)
            {
                throw new ArgumentException("needs room for " + count + " weights", nameof(weights));
            }

            Array.Clear(weights, 0, count);

            var h = hueDegrees % 360.0;
            if (h < 0)
            {
                h += 360.0;
            }

            for (var i = 0; i < count; i++)
            {
                var start = BandCenters[i];
                var next = (i + 1) % count;
                var end = next == 0 ? 360.0 : BandCenters[next];

                if (h >= start && h < end)
                {
                    var t = (h - start) / (end - start);
                    var s = t * t * (3 - (2 * t));
                    weights[i] = 1 - s;
                    weights[next] += s;
                    return;
                }
            }

            weights[0] = 1;
        }

        /// <summary>Applies the adjustment to one display (sRGB-encoded, 0..1) colour.</summary>
        /// <param name="srgb">The colour.</param>
        /// <returns>The adjusted colour, clamped to 0..1.</returns>
        public Rgb Apply(Rgb srgb)
        {
            GradingColor.ToHsv(srgb, out var h, out var s, out var v);
            if (s <= 1e-6)
            {
                return srgb;
            }

            var w = new double[BandCenters.Count];
            Weights(h, w);

            double dh = 0, ds = 0, dl = 0;
            for (var i = 0; i < w.Length; i++)
            {
                if (w[i] == 0)
                {
                    continue;
                }

                dh += w[i] * (_hue[i] / 100.0) * MaxHueShiftDegrees;
                ds += w[i] * (_saturation[i] / 100.0);
                dl += w[i] * (_luminance[i] / 100.0);
            }

            if (dh == 0 && ds == 0 && dl == 0)
            {
                // Outside every adjusted band: hand the colour back untouched rather than through
                // an HSV round trip that would move it by a rounding error.
                return srgb;
            }

            var result = GradingColor.FromHsv(h + dh, GradingColor.Clamp01(s * (1 + ds)), v);

            if (dl != 0)
            {
                var knee = s >= SaturationKnee ? 1.0 : s / SaturationKnee;
                var factor = 1 + (dl * LuminanceScale * knee * knee * (3 - (2 * knee)));
                var linear = GradingColor.ToLinear(result).Scale(factor < 0 ? 0 : factor);
                result = GradingColor.ToSrgb(linear.Clamp01());
            }

            return result.Clamp01();
        }

        /// <summary>A stable key for caching a baked LUT: the 24 values, invariant.</summary>
        public string CacheKey()
        {
            var sb = new StringBuilder("hsl");
            for (var i = 0; i < _hue.Length; i++)
            {
                sb.Append('|').Append(_hue[i].ToString("R", CultureInfo.InvariantCulture))
                  .Append(',').Append(_saturation[i].ToString("R", CultureInfo.InvariantCulture))
                  .Append(',').Append(_luminance[i].ToString("R", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
