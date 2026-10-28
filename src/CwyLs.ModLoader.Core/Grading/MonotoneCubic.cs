using System;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// A piecewise cubic Hermite spline through a set of points with Fritsch-Carlson tangents: it
    /// never overshoots between two points, so a tone curve drawn through monotone points stays
    /// monotone (no ringing, no inverted tones), and a local extremum gets a flat tangent.
    /// <para>
    /// The tangents are exactly what Unity's <c>Keyframe(time, value, inTangent, outTangent)</c>
    /// wants - an <c>AnimationCurve</c> evaluates a segment as the same cubic Hermite - so the curve
    /// the game samples is the curve tested here.
    /// </para>
    /// </summary>
    public sealed class MonotoneCubic
    {
        private readonly double[] _x;
        private readonly double[] _y;
        private readonly double[] _m;

        /// <summary>Builds the spline.</summary>
        /// <param name="xs">Strictly increasing inputs.</param>
        /// <param name="ys">Outputs, same length as <paramref name="xs"/>.</param>
        /// <exception cref="ArgumentException">The arrays are empty, of different lengths, or the inputs are not strictly increasing.</exception>
        public MonotoneCubic(double[] xs, double[] ys)
        {
            if (xs is null || ys is null || xs.Length == 0 || xs.Length != ys.Length)
            {
                throw new ArgumentException("a spline needs one or more points, with as many outputs as inputs");
            }

            for (var i = 1; i < xs.Length; i++)
            {
                if (!(xs[i] > xs[i - 1]))
                {
                    throw new ArgumentException("spline inputs must be strictly increasing", nameof(xs));
                }
            }

            _x = (double[])xs.Clone();
            _y = (double[])ys.Clone();
            _m = Tangents(_x, _y);
        }

        /// <summary>How many points the spline passes through.</summary>
        public int Count => _x.Length;

        /// <summary>Input of point <paramref name="i"/>.</summary>
        /// <param name="i">Point index.</param>
        public double X(int i) => _x[i];

        /// <summary>Output of point <paramref name="i"/>.</summary>
        /// <param name="i">Point index.</param>
        public double Y(int i) => _y[i];

        /// <summary>Slope at point <paramref name="i"/> (the Keyframe's in- and out-tangent).</summary>
        /// <param name="i">Point index.</param>
        public double Tangent(int i) => _m[i];

        /// <summary>
        /// The spline at <paramref name="x"/>; flat (the end value) outside the points.
        /// </summary>
        /// <param name="x">Input.</param>
        public double Evaluate(double x)
        {
            var n = _x.Length;
            if (n == 1 || x <= _x[0])
            {
                return _y[0];
            }

            if (x >= _x[n - 1])
            {
                return _y[n - 1];
            }

            var k = Array.BinarySearch(_x, x);
            if (k >= 0)
            {
                return _y[k];
            }

            var i = ~k - 1;
            var h = _x[i + 1] - _x[i];
            var t = (x - _x[i]) / h;
            var t2 = t * t;
            var t3 = t2 * t;

            var h00 = (2 * t3) - (3 * t2) + 1;
            var h10 = t3 - (2 * t2) + t;
            var h01 = (-2 * t3) + (3 * t2);
            var h11 = t3 - t2;

            return (h00 * _y[i]) + (h10 * h * _m[i]) + (h01 * _y[i + 1]) + (h11 * h * _m[i + 1]);
        }

        private static double[] Tangents(double[] x, double[] y)
        {
            var n = x.Length;
            var m = new double[n];
            if (n == 1)
            {
                return m;
            }

            var d = new double[n - 1];
            for (var i = 0; i < n - 1; i++)
            {
                d[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
            }

            m[0] = d[0];
            m[n - 1] = d[n - 2];
            for (var i = 1; i < n - 1; i++)
            {
                m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
            }

            for (var i = 0; i < n - 1; i++)
            {
                if (d[i] == 0)
                {
                    m[i] = 0;
                    m[i + 1] = 0;
                    continue;
                }

                var a = m[i] / d[i];
                var b = m[i + 1] / d[i];

                // A tangent pointing against the secant would overshoot; Fritsch-Carlson zeroes it.
                if (a < 0)
                {
                    m[i] = 0;
                    a = 0;
                }

                if (b < 0)
                {
                    m[i + 1] = 0;
                    b = 0;
                }

                var s = (a * a) + (b * b);
                if (s > 9)
                {
                    var tau = 3 / Math.Sqrt(s);
                    m[i] = tau * a * d[i];
                    m[i + 1] = tau * b * d[i];
                }
            }

            return m;
        }
    }
}
