using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>An RGB triple of doubles. Which space it is in (display sRGB or linear) is the caller's contract.</summary>
    public readonly struct Rgb : IEquatable<Rgb>
    {
        /// <summary>Creates a colour.</summary>
        /// <param name="r">Red.</param>
        /// <param name="g">Green.</param>
        /// <param name="b">Blue.</param>
        public Rgb(double r, double g, double b)
        {
            R = r;
            G = g;
            B = b;
        }

        /// <summary>Red.</summary>
        public double R { get; }

        /// <summary>Green.</summary>
        public double G { get; }

        /// <summary>Blue.</summary>
        public double B { get; }

        /// <summary>Grey of one value.</summary>
        /// <param name="v">The value of all three channels.</param>
        public static Rgb Grey(double v) => new Rgb(v, v, v);

        /// <summary>Componentwise linear interpolation.</summary>
        /// <param name="a">At 0.</param>
        /// <param name="b">At 1.</param>
        /// <param name="t">Weight.</param>
        public static Rgb Lerp(Rgb a, Rgb b, double t) =>
            new Rgb(a.R + (t * (b.R - a.R)), a.G + (t * (b.G - a.G)), a.B + (t * (b.B - a.B)));

        /// <summary>Every channel clamped to 0..1.</summary>
        public Rgb Clamp01() => new Rgb(GradingColor.Clamp01(R), GradingColor.Clamp01(G), GradingColor.Clamp01(B));

        /// <summary>Every channel multiplied by <paramref name="k"/>.</summary>
        /// <param name="k">The factor.</param>
        public Rgb Scale(double k) => new Rgb(R * k, G * k, B * k);

        /// <inheritdoc />
        public bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Rgb other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (R, G, B).GetHashCode();

        /// <inheritdoc />
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.####}, {1:0.####}, {2:0.####})", R, G, B);
    }

    /// <summary>
    /// The colour arithmetic the grading layer needs, all of it pure: sRGB transfer functions,
    /// HSV/HSL conversions, Rec. 709 luminance.
    /// </summary>
    public static class GradingColor
    {
        /// <summary>Clamps to 0..1.</summary>
        /// <param name="v">The value.</param>
        public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>sRGB-encoded to linear (IEC 61966-2-1); extended to values above 1 with the same curve.</summary>
        /// <param name="c">Encoded value.</param>
        public static double SrgbToLinear(double c) =>
            c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        /// <summary>Linear to sRGB-encoded; extended to values above 1 with the same curve.</summary>
        /// <param name="c">Linear value.</param>
        public static double LinearToSrgb(double c) =>
            c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(c, 1.0 / 2.4)) - 0.055;

        /// <summary>Converts each channel from sRGB to linear.</summary>
        /// <param name="c">sRGB colour.</param>
        public static Rgb ToLinear(Rgb c) => new Rgb(SrgbToLinear(c.R), SrgbToLinear(c.G), SrgbToLinear(c.B));

        /// <summary>Converts each channel from linear to sRGB.</summary>
        /// <param name="c">Linear colour.</param>
        public static Rgb ToSrgb(Rgb c) => new Rgb(LinearToSrgb(c.R), LinearToSrgb(c.G), LinearToSrgb(c.B));

        /// <summary>Rec. 709 luminance (the weights URP's <c>GetLuminance</c> uses).</summary>
        /// <param name="c">A linear colour.</param>
        public static double Luminance(Rgb c) => (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);

        /// <summary>RGB (0..1) to HSV: hue in degrees [0, 360), saturation and value 0..1.</summary>
        /// <param name="c">The colour.</param>
        /// <param name="h">Hue, degrees; 0 for a grey.</param>
        /// <param name="s">Saturation.</param>
        /// <param name="v">Value.</param>
        public static void ToHsv(Rgb c, out double h, out double s, out double v)
        {
            var max = Math.Max(c.R, Math.Max(c.G, c.B));
            var min = Math.Min(c.R, Math.Min(c.G, c.B));
            var delta = max - min;
            v = max;
            s = max <= 0 ? 0 : delta / max;

            if (delta <= 0)
            {
                h = 0;
                return;
            }

            if (max == c.R)
            {
                h = 60.0 * (((c.G - c.B) / delta) % 6.0);
            }
            else if (max == c.G)
            {
                h = 60.0 * (((c.B - c.R) / delta) + 2.0);
            }
            else
            {
                h = 60.0 * (((c.R - c.G) / delta) + 4.0);
            }

            if (h < 0)
            {
                h += 360.0;
            }
        }

        /// <summary>HSV to RGB.</summary>
        /// <param name="h">Hue in degrees; any value, wrapped.</param>
        /// <param name="s">Saturation 0..1.</param>
        /// <param name="v">Value.</param>
        public static Rgb FromHsv(double h, double s, double v)
        {
            var hue = h % 360.0;
            if (hue < 0)
            {
                hue += 360.0;
            }

            var c = v * s;
            var x = c * (1 - Math.Abs(((hue / 60.0) % 2) - 1));
            var m = v - c;

            double r, g, b;
            if (hue < 60)
            {
                (r, g, b) = (c, x, 0);
            }
            else if (hue < 120)
            {
                (r, g, b) = (x, c, 0);
            }
            else if (hue < 180)
            {
                (r, g, b) = (0, c, x);
            }
            else if (hue < 240)
            {
                (r, g, b) = (0, x, c);
            }
            else if (hue < 300)
            {
                (r, g, b) = (x, 0, c);
            }
            else
            {
                (r, g, b) = (c, 0, x);
            }

            return new Rgb(r + m, g + m, b + m);
        }

        /// <summary>HSL to RGB: at saturation 0 every hue is the grey <paramref name="l"/>.</summary>
        /// <param name="h">Hue in degrees; any value, wrapped.</param>
        /// <param name="s">Saturation 0..1.</param>
        /// <param name="l">Lightness 0..1.</param>
        public static Rgb FromHsl(double h, double s, double l)
        {
            var c = (1 - Math.Abs((2 * l) - 1)) * s;
            var v = l + (c / 2);
            var sv = v <= 0 ? 0 : c / v;
            return FromHsv(h, sv, v);
        }
    }
}
