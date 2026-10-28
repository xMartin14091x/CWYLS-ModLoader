using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>The four point curves of Lightroom's tone curve panel.</summary>
    public enum GradingCurveChannel
    {
        /// <summary>The RGB (master) curve: every channel through the same curve.</summary>
        Master = 0,

        /// <summary>The red channel curve.</summary>
        Red,

        /// <summary>The green channel curve.</summary>
        Green,

        /// <summary>The blue channel curve.</summary>
        Blue,
    }

    /// <summary>
    /// One control point of a tone curve, in Lightroom's units: input and output on 0..255, display
    /// (gamma-encoded) values.
    /// </summary>
    public readonly struct GradingCurvePoint : IEquatable<GradingCurvePoint>
    {
        /// <summary>The largest coordinate.</summary>
        public const double Max = 255.0;

        /// <summary>Creates a point.</summary>
        /// <param name="x">Input, 0..255.</param>
        /// <param name="y">Output, 0..255.</param>
        public GradingCurvePoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        /// <summary>Input level, 0..255.</summary>
        public double X { get; }

        /// <summary>Output level, 0..255.</summary>
        public double Y { get; }

        /// <inheritdoc />
        public bool Equals(GradingCurvePoint other) => Math.Abs(X - other.X) < 1e-9 && Math.Abs(Y - other.Y) < 1e-9;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is GradingCurvePoint other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (Math.Round(X, 6), Math.Round(Y, 6)).GetHashCode();

        /// <inheritdoc />
        public override string ToString() =>
            "[" + X.ToString("0.##", CultureInfo.InvariantCulture) + "," + Y.ToString("0.##", CultureInfo.InvariantCulture) + "]";
    }

    /// <summary>Helpers for point lists.</summary>
    public static class GradingCurves
    {
        /// <summary>Most points one curve may have; Lightroom allows 16.</summary>
        public const int MaxPoints = 16;

        /// <summary>The JSON names of the channels: <c>master</c>, <c>red</c>, <c>green</c>, <c>blue</c>.</summary>
        public static IReadOnlyList<string> ChannelNames { get; } = new[] { "master", "red", "green", "blue" };

        /// <summary>The JSON name of a channel.</summary>
        /// <param name="channel">The channel.</param>
        public static string NameOf(GradingCurveChannel channel) => ChannelNames[(int)channel];

        /// <summary>Reads a channel name, case-insensitively; <c>rgb</c> is accepted for master.</summary>
        /// <param name="name">The name.</param>
        /// <param name="channel">The channel.</param>
        /// <returns>True when known.</returns>
        public static bool TryParseChannel(string? name, out GradingCurveChannel channel)
        {
            channel = GradingCurveChannel.Master;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (string.Equals(name, "rgb", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            for (var i = 0; i < ChannelNames.Count; i++)
            {
                if (string.Equals(name, ChannelNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    channel = (GradingCurveChannel)i;
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when every point lies on the diagonal, so the curve changes nothing.</summary>
        /// <param name="points">The points; null or empty is an identity.</param>
        public static bool IsIdentity(IReadOnlyList<GradingCurvePoint>? points)
        {
            if (points is null)
            {
                return true;
            }

            foreach (var point in points)
            {
                if (Math.Abs(point.X - point.Y) > 1e-6)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The curve evaluated as a function on 0..1 display values: a monotone cubic through the
        /// points (normalised to 0..1), flat beyond the first and last point - Lightroom's
        /// behaviour - and the identity when there are no points.
        /// </summary>
        /// <param name="points">Points sorted by <see cref="GradingCurvePoint.X"/>, distinct X.</param>
        public static Func<double, double> AsFunction(IReadOnlyList<GradingCurvePoint>? points)
        {
            if (points is null || points.Count == 0)
            {
                return v => v;
            }

            var xs = new double[points.Count];
            var ys = new double[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                xs[i] = points[i].X / GradingCurvePoint.Max;
                ys[i] = points[i].Y / GradingCurvePoint.Max;
            }

            var spline = new MonotoneCubic(xs, ys);
            return spline.Evaluate;
        }

        /// <summary>
        /// Moves every point toward the diagonal: <c>y' = x + t (y - x)</c>. <c>t = 0</c> is the
        /// identity, <c>t = 1</c> the curve itself.
        /// </summary>
        /// <param name="points">The curve.</param>
        /// <param name="t">Blend, clamped to 0..1.</param>
        public static IReadOnlyList<GradingCurvePoint> TowardIdentity(IReadOnlyList<GradingCurvePoint> points, double t)
        {
            var k = t < 0 ? 0 : t > 1 ? 1 : t;
            var result = new GradingCurvePoint[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                result[i] = new GradingCurvePoint(points[i].X, points[i].X + (k * (points[i].Y - points[i].X)));
            }

            return result;
        }

        /// <summary>True when both lists hold the same points in the same order.</summary>
        /// <param name="a">One list; null is an empty list.</param>
        /// <param name="b">The other.</param>
        public static bool SamePoints(IReadOnlyList<GradingCurvePoint>? a, IReadOnlyList<GradingCurvePoint>? b)
        {
            var countA = a?.Count ?? 0;
            var countB = b?.Count ?? 0;
            if (countA != countB)
            {
                return false;
            }

            for (var i = 0; i < countA; i++)
            {
                if (!a![i].Equals(b![i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
