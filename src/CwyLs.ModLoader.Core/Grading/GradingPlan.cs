using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>Four floats: a colour with alpha, or a URP <c>Vector4</c> parameter.</summary>
    public readonly struct Float4 : IEquatable<Float4>
    {
        /// <summary>Creates the value.</summary>
        /// <param name="x">X (red).</param>
        /// <param name="y">Y (green).</param>
        /// <param name="z">Z (blue).</param>
        /// <param name="w">W (alpha, or a URP luminance offset).</param>
        public Float4(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        /// <summary>X (red).</summary>
        public double X { get; }

        /// <summary>Y (green).</summary>
        public double Y { get; }

        /// <summary>Z (blue).</summary>
        public double Z { get; }

        /// <summary>W (alpha, or a URP luminance offset).</summary>
        public double W { get; }

        /// <summary>A colour with the given alpha (or W).</summary>
        /// <param name="c">The colour.</param>
        /// <param name="w">The fourth component.</param>
        public static Float4 Of(Rgb c, double w) => new Float4(c.R, c.G, c.B, w);

        /// <inheritdoc />
        public bool Equals(Float4 other) =>
            Math.Abs(X - other.X) < 1e-9 && Math.Abs(Y - other.Y) < 1e-9 &&
            Math.Abs(Z - other.Z) < 1e-9 && Math.Abs(W - other.W) < 1e-9;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Float4 other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (Math.Round(X, 6), Math.Round(Y, 6), Math.Round(Z, 6), Math.Round(W, 6)).GetHashCode();

        /// <inheritdoc />
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", X, Y, Z, W);
    }

    /// <summary>One key of a Unity <c>AnimationCurve</c>: <c>Keyframe(time, value, inTangent, outTangent)</c>.</summary>
    public readonly struct CurveKey
    {
        /// <summary>Creates a key.</summary>
        /// <param name="time">Input.</param>
        /// <param name="value">Output.</param>
        /// <param name="inTangent">Slope arriving at the key.</param>
        /// <param name="outTangent">Slope leaving the key.</param>
        public CurveKey(double time, double value, double inTangent, double outTangent)
        {
            Time = time;
            Value = value;
            InTangent = inTangent;
            OutTangent = outTangent;
        }

        /// <summary>Input.</summary>
        public double Time { get; }

        /// <summary>Output.</summary>
        public double Value { get; }

        /// <summary>Slope arriving at the key.</summary>
        public double InTangent { get; }

        /// <summary>Slope leaving the key.</summary>
        public double OutTangent { get; }

        /// <summary>The keys of a spline: one per point, tangent = the spline's slope there.</summary>
        /// <param name="spline">The spline.</param>
        public static IReadOnlyList<CurveKey> FromSpline(MonotoneCubic spline)
        {
            var keys = new CurveKey[spline.Count];
            for (var i = 0; i < spline.Count; i++)
            {
                keys[i] = new CurveKey(spline.X(i), spline.Y(i), spline.Tangent(i), spline.Tangent(i));
            }

            return keys;
        }

        /// <summary>
        /// Evaluates keys the way Unity's <c>AnimationCurve</c> does for unweighted tangents: a cubic
        /// Hermite per segment using the left key's out-tangent and the right key's in-tangent; flat
        /// outside the keys.
        /// </summary>
        /// <param name="keys">Keys sorted by time.</param>
        /// <param name="x">Input.</param>
        public static double Evaluate(IReadOnlyList<CurveKey> keys, double x)
        {
            if (keys is null || keys.Count == 0)
            {
                throw new ArgumentException("no keys", nameof(keys));
            }

            if (keys.Count == 1 || x <= keys[0].Time)
            {
                return keys[0].Value;
            }

            var last = keys[keys.Count - 1];
            if (x >= last.Time)
            {
                return last.Value;
            }

            var i = 0;
            while (i < keys.Count - 2 && x > keys[i + 1].Time)
            {
                i++;
            }

            var a = keys[i];
            var b = keys[i + 1];
            var h = b.Time - a.Time;
            var t = (x - a.Time) / h;
            var t2 = t * t;
            var t3 = t2 * t;
            return (((2 * t3) - (3 * t2) + 1) * a.Value) + ((t3 - (2 * t2) + t) * h * a.OutTangent) +
                   (((-2 * t3) + (3 * t2)) * b.Value) + ((t3 - t2) * h * b.InTangent);
        }

        /// <inheritdoc />
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.###}->{1:0.###}", Time, Value);
    }

    /// <summary>URP <c>SplitToning</c>: two tint colours (grey 0.5 = none) and a balance (-100..100).</summary>
    /// <param name="Shadows">Shadow tint colour (gamma); (0.5, 0.5, 0.5) is no tint.</param>
    /// <param name="Highlights">Highlight tint colour (gamma).</param>
    /// <param name="Balance">-100..100; + favours the highlight tint.</param>
    public sealed record SplitToningPlan(Rgb Shadows, Rgb Highlights, double Balance);

    /// <summary>
    /// URP <c>ShadowsMidtonesHighlights</c>: three <c>Vector4</c>s (xyz = colour, (1,1,1) neutral;
    /// w = luminance offset, 0 neutral) and the two ranges, null when left at URP's defaults.
    /// </summary>
    /// <param name="Shadows">Shadows wheel.</param>
    /// <param name="Midtones">Midtones wheel.</param>
    /// <param name="Highlights">Highlights wheel.</param>
    /// <param name="ShadowsEnd">Where the shadow range ends (URP default 0.3); null = default.</param>
    /// <param name="HighlightsStart">Where the highlight range starts (URP default 0.55); null = default.</param>
    public sealed record SmhPlan(Float4 Shadows, Float4 Midtones, Float4 Highlights, double? ShadowsEnd, double? HighlightsStart);

    /// <summary>URP <c>Vignette</c>.</summary>
    /// <param name="Color">Edge colour: black darkens, above 1 lightens.</param>
    /// <param name="Intensity">URP intensity 0..1 (how far it reaches).</param>
    /// <param name="Smoothness">URP smoothness 0.01..1 (edge falloff).</param>
    /// <param name="Rounded">Circular rather than fitted to the screen.</param>
    public sealed record VignettePlan(Rgb Color, double Intensity, double Smoothness, bool Rounded);

    /// <summary>URP <c>FilmGrain</c>.</summary>
    /// <param name="TypeIndex">Index into <c>FilmGrainLookup</c> (Thin1 = 0 .. Large02 = 9).</param>
    /// <param name="Intensity">0..1.</param>
    /// <param name="Response">0..1; how much the grain fades in bright areas.</param>
    public sealed record FilmGrainPlan(int TypeIndex, double Intensity, double Response);

    /// <summary>
    /// What a flat, blended preset means for the renderer: the URP volume overrides to set, the
    /// relative change to the game's own Beautify bloom, and the LUT to bake. Built by
    /// <see cref="GradingMapper.Map"/>; every member is null when the preset leaves it alone.
    /// </summary>
    public sealed class GradingPlan
    {
        /// <summary>ColorAdjustments.postExposure, EV.</summary>
        public double? PostExposure { get; init; }

        /// <summary>ColorAdjustments.contrast, -100..100.</summary>
        public double? Contrast { get; init; }

        /// <summary>ColorAdjustments.saturation, -100..100.</summary>
        public double? Saturation { get; init; }

        /// <summary>WhiteBalance.temperature, -100..100.</summary>
        public double? Temperature { get; init; }

        /// <summary>WhiteBalance.tint, -100..100.</summary>
        public double? Tint { get; init; }

        /// <summary>SplitToning.</summary>
        public SplitToningPlan? SplitToning { get; init; }

        /// <summary>ShadowsMidtonesHighlights.</summary>
        public SmhPlan? ShadowsMidtonesHighlights { get; init; }

        /// <summary>ColorCurves.master.</summary>
        public IReadOnlyList<CurveKey>? MasterCurve { get; init; }

        /// <summary>ColorCurves.red.</summary>
        public IReadOnlyList<CurveKey>? RedCurve { get; init; }

        /// <summary>ColorCurves.green.</summary>
        public IReadOnlyList<CurveKey>? GreenCurve { get; init; }

        /// <summary>ColorCurves.blue.</summary>
        public IReadOnlyList<CurveKey>? BlueCurve { get; init; }

        /// <summary>ColorCurves.satVsSat (vibrance); values are halved multipliers (0.5 = unchanged).</summary>
        public IReadOnlyList<CurveKey>? SatVsSatCurve { get; init; }

        /// <summary>Vignette.</summary>
        public VignettePlan? Vignette { get; init; }

        /// <summary>FilmGrain.</summary>
        public FilmGrainPlan? FilmGrain { get; init; }

        /// <summary>Multiplier for the game's own Beautify <c>bloomIntensity</c> (1 = unchanged).</summary>
        public double? BloomFactor { get; init; }

        /// <summary>The HSL adjustment to bake into the ColorLookup LUT.</summary>
        public HslAdjustment? Hsl { get; init; }

        /// <summary>The preset's LUT file, relative to the preset's folder.</summary>
        public string? LutFile { get; init; }

        /// <summary>How much of the LUT file to apply, 0..1.</summary>
        public double LutAmount { get; init; } = 1.0;

        /// <summary>True when a ColorLookup LUT has to be baked (HSL, or a LUT file with amount above 0).</summary>
        public bool NeedsLut => Hsl != null || (LutFile != null && LutAmount > 0);

        /// <summary>True when the plan changes nothing.</summary>
        public bool IsEmpty => Components.Count == 0;

        /// <summary>The URP/Beautify components this plan touches, e.g. <c>ColorAdjustments</c>.</summary>
        public IReadOnlyList<string> Components
        {
            get
            {
                var list = new List<string>();
                if (PostExposure.HasValue || Contrast.HasValue || Saturation.HasValue)
                {
                    list.Add("ColorAdjustments");
                }

                if (Temperature.HasValue || Tint.HasValue)
                {
                    list.Add("WhiteBalance");
                }

                if (SplitToning != null)
                {
                    list.Add("SplitToning");
                }

                if (ShadowsMidtonesHighlights != null)
                {
                    list.Add("ShadowsMidtonesHighlights");
                }

                if (MasterCurve != null || RedCurve != null || GreenCurve != null || BlueCurve != null || SatVsSatCurve != null)
                {
                    list.Add("ColorCurves");
                }

                if (NeedsLut)
                {
                    list.Add("ColorLookup");
                }

                if (Vignette != null)
                {
                    list.Add("Vignette");
                }

                if (FilmGrain != null)
                {
                    list.Add("FilmGrain");
                }

                if (BloomFactor.HasValue)
                {
                    list.Add("Beautify.bloom");
                }

                return list;
            }
        }

        /// <summary>One line: the components touched, e.g. <c>ColorAdjustments+SplitToning+Vignette</c>, or <c>none</c>.</summary>
        public string Describe()
        {
            var components = Components;
            if (components.Count == 0)
            {
                return "none";
            }

            var sb = new StringBuilder();
            foreach (var name in components)
            {
                if (sb.Length > 0)
                {
                    sb.Append('+');
                }

                sb.Append(name);
            }

            return sb.ToString();
        }
    }
}
