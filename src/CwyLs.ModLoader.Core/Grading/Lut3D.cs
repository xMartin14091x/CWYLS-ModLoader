using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// A 3D colour lookup table: <c>Size</c>^3 RGB entries over the unit cube, red varying fastest
    /// (the <c>.cube</c> order). Pure data with trilinear sampling, <c>.cube</c> parsing, and the
    /// conversions to and from the horizontal strip layout URP's <c>ColorLookup</c> samples.
    /// <para>
    /// Strip layout (URP <c>ApplyLut2D</c>): width = N x N, height = N; the texel at
    /// <c>x = r + N * b</c>, <c>y = g</c> (y = 0 is the bottom row, Unity's texture convention) holds
    /// the output for input <c>(r, g, b) / (N - 1)</c>. Inputs and outputs are sRGB-encoded: URP
    /// converts to sRGB before the lookup and back after it.
    /// </para>
    /// </summary>
    public sealed class Lut3D
    {
        /// <summary>Smallest size accepted.</summary>
        public const int MinSize = 2;

        /// <summary>Largest size accepted (65 is the largest common <c>.cube</c> size).</summary>
        public const int MaxSize = 65;

        private readonly float[] _data;

        private Lut3D(int size, float[] data)
        {
            Size = size;
            _data = data;
        }

        /// <summary>Entries per axis.</summary>
        public int Size { get; }

        /// <summary>The identity table: every entry is its own input.</summary>
        /// <param name="size">Entries per axis.</param>
        /// <exception cref="ArgumentOutOfRangeException">The size is outside <see cref="MinSize"/>..<see cref="MaxSize"/>.</exception>
        public static Lut3D Identity(int size) => Bake(size, c => c);

        /// <summary>Builds a table by evaluating <paramref name="transform"/> at every grid point.</summary>
        /// <param name="size">Entries per axis.</param>
        /// <param name="transform">The colour function, on 0..1 sRGB-encoded colours.</param>
        /// <exception cref="ArgumentOutOfRangeException">The size is outside <see cref="MinSize"/>..<see cref="MaxSize"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="transform"/> is null.</exception>
        public static Lut3D Bake(int size, Func<Rgb, Rgb> transform)
        {
            RequireSize(size);
            if (transform is null)
            {
                throw new ArgumentNullException(nameof(transform));
            }

            var data = new float[size * size * size * 3];
            var scale = 1.0 / (size - 1);
            var k = 0;

            for (var b = 0; b < size; b++)
            {
                for (var g = 0; g < size; g++)
                {
                    for (var r = 0; r < size; r++)
                    {
                        var output = transform(new Rgb(r * scale, g * scale, b * scale));
                        data[k++] = (float)output.R;
                        data[k++] = (float)output.G;
                        data[k++] = (float)output.B;
                    }
                }
            }

            return new Lut3D(size, data);
        }

        /// <summary>The output stored at a grid point.</summary>
        /// <param name="r">Red index.</param>
        /// <param name="g">Green index.</param>
        /// <param name="b">Blue index.</param>
        public Rgb At(int r, int g, int b)
        {
            var i = 3 * (r + (Size * (g + (Size * b))));
            return new Rgb(_data[i], _data[i + 1], _data[i + 2]);
        }

        /// <summary>Trilinear lookup; the input is clamped to the unit cube.</summary>
        /// <param name="input">An sRGB-encoded colour.</param>
        public Rgb Sample(Rgb input)
        {
            var n = Size - 1;
            Split(GradingColor.Clamp01(input.R) * n, n, out var r0, out var fr);
            Split(GradingColor.Clamp01(input.G) * n, n, out var g0, out var fg);
            Split(GradingColor.Clamp01(input.B) * n, n, out var b0, out var fb);

            var c00 = Rgb.Lerp(At(r0, g0, b0), At(r0 + 1, g0, b0), fr);
            var c10 = Rgb.Lerp(At(r0, g0 + 1, b0), At(r0 + 1, g0 + 1, b0), fr);
            var c01 = Rgb.Lerp(At(r0, g0, b0 + 1), At(r0 + 1, g0, b0 + 1), fr);
            var c11 = Rgb.Lerp(At(r0, g0 + 1, b0 + 1), At(r0 + 1, g0 + 1, b0 + 1), fr);

            return Rgb.Lerp(Rgb.Lerp(c00, c10, fg), Rgb.Lerp(c01, c11, fg), fb);
        }

        /// <summary>
        /// The table as a strip texture's pixels: RGBA floats, row by row from the bottom row up,
        /// width = N x N, height = N, alpha 1.
        /// </summary>
        public float[] ToStripRgba()
        {
            var n = Size;
            var width = n * n;
            var pixels = new float[width * n * 4];

            for (var g = 0; g < n; g++)
            {
                for (var b = 0; b < n; b++)
                {
                    for (var r = 0; r < n; r++)
                    {
                        var x = r + (n * b);
                        var o = 4 * (x + (width * g));
                        var src = 3 * (r + (n * (g + (n * b))));
                        pixels[o] = _data[src];
                        pixels[o + 1] = _data[src + 1];
                        pixels[o + 2] = _data[src + 2];
                        pixels[o + 3] = 1f;
                    }
                }
            }

            return pixels;
        }

        /// <summary>Reads a strip image back into a table (the inverse of <see cref="ToStripRgba"/>).</summary>
        /// <param name="width">Image width; must be height squared.</param>
        /// <param name="height">Image height = entries per axis.</param>
        /// <param name="rgba">Pixels, RGBA, row by row from the bottom row up (Unity's <c>GetPixels</c> order).</param>
        /// <param name="problem">Why the image is not a strip LUT, or null.</param>
        /// <returns>The table, or null.</returns>
        public static Lut3D? FromStrip(int width, int height, IReadOnlyList<float> rgba, out string? problem)
        {
            problem = null;
            if (height < MinSize || height > MaxSize || width != height * height)
            {
                problem = "a strip LUT is (size x size) wide and size high with size " + MinSize + ".." + MaxSize +
                          "; this image is " + width.ToString(CultureInfo.InvariantCulture) + "x" +
                          height.ToString(CultureInfo.InvariantCulture);
                return null;
            }

            if (rgba is null || rgba.Count != width * height * 4)
            {
                problem = "the pixel data does not match the image size";
                return null;
            }

            var n = height;
            var data = new float[n * n * n * 3];
            for (var g = 0; g < n; g++)
            {
                for (var b = 0; b < n; b++)
                {
                    for (var r = 0; r < n; r++)
                    {
                        var o = 4 * (r + (n * b) + (width * g));
                        var dst = 3 * (r + (n * (g + (n * b))));
                        data[dst] = rgba[o];
                        data[dst + 1] = rgba[o + 1];
                        data[dst + 2] = rgba[o + 2];
                    }
                }
            }

            return new Lut3D(n, data);
        }

        /// <summary>
        /// Parses an Adobe/Resolve <c>.cube</c> file: <c>LUT_3D_SIZE</c>, optional
        /// <c>TITLE</c>/<c>DOMAIN_MIN</c>/<c>DOMAIN_MAX</c>, <c>#</c> comments, then size^3 lines of
        /// three numbers with red varying fastest. A non-default domain is folded in by resampling.
        /// </summary>
        /// <param name="text">The whole file.</param>
        /// <param name="problem">Why the file was refused, or null.</param>
        /// <returns>The table, or null.</returns>
        public static Lut3D? ParseCube(string? text, out string? problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                problem = "the .cube file is empty";
                return null;
            }

            var size = 0;
            var min = new[] { 0.0, 0.0, 0.0 };
            var max = new[] { 1.0, 1.0, 1.0 };
            var values = new List<float>();
            var lineNumber = 0;

            foreach (var rawLine in text!.Split('\n'))
            {
                lineNumber++;
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var keyword = parts[0].ToUpperInvariant();

                if (keyword == "TITLE")
                {
                    continue;
                }

                if (keyword == "LUT_1D_SIZE")
                {
                    problem = "line " + lineNumber + ": a 1D LUT is not supported; export a 3D LUT";
                    return null;
                }

                if (keyword == "LUT_3D_SIZE")
                {
                    if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) ||
                        size < MinSize || size > MaxSize)
                    {
                        problem = "line " + lineNumber + ": LUT_3D_SIZE must be " + MinSize + ".." + MaxSize;
                        return null;
                    }

                    continue;
                }

                if (keyword == "DOMAIN_MIN" || keyword == "DOMAIN_MAX")
                {
                    var target = keyword == "DOMAIN_MIN" ? min : max;
                    if (parts.Length != 4 || !ParseTriple(parts, 1, target))
                    {
                        problem = "line " + lineNumber + ": " + keyword + " needs three numbers";
                        return null;
                    }

                    continue;
                }

                if (keyword.Length > 0 && (char.IsLetter(keyword[0]) || keyword[0] == '_'))
                {
                    // Other keywords (LUT_IN_VIDEO_RANGE, ...) do not change the table's meaning here.
                    continue;
                }

                var triple = new double[3];
                if (parts.Length != 3 || !ParseTriple(parts, 0, triple))
                {
                    problem = "line " + lineNumber + ": expected three numbers";
                    return null;
                }

                values.Add((float)triple[0]);
                values.Add((float)triple[1]);
                values.Add((float)triple[2]);
            }

            if (size == 0)
            {
                problem = "no LUT_3D_SIZE line";
                return null;
            }

            var expected = size * size * size;
            if (values.Count != expected * 3)
            {
                problem = "LUT_3D_SIZE " + size + " needs " + expected + " entries, the file has " + (values.Count / 3);
                return null;
            }

            for (var i = 0; i < 3; i++)
            {
                if (!(max[i] > min[i]))
                {
                    problem = "DOMAIN_MAX must be above DOMAIN_MIN";
                    return null;
                }
            }

            var table = new Lut3D(size, values.ToArray());
            var identityDomain = min[0] == 0 && min[1] == 0 && min[2] == 0 && max[0] == 1 && max[1] == 1 && max[2] == 1;
            if (identityDomain)
            {
                return table;
            }

            // Resample so the table is indexed over the unit cube like every other one.
            return Bake(size, c => table.Sample(new Rgb(
                (c.R - min[0]) / (max[0] - min[0]),
                (c.G - min[1]) / (max[1] - min[1]),
                (c.B - min[2]) / (max[2] - min[2]))));
        }

        private static bool ParseTriple(string[] parts, int start, double[] into)
        {
            for (var i = 0; i < 3; i++)
            {
                if (!double.TryParse(parts[start + i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
                    double.IsNaN(v) || double.IsInfinity(v))
                {
                    return false;
                }

                into[i] = v;
            }

            return true;
        }

        private static void Split(double scaled, int n, out int index, out double fraction)
        {
            index = (int)Math.Floor(scaled);
            if (index >= n)
            {
                index = n - 1;
            }

            if (index < 0)
            {
                index = 0;
            }

            fraction = scaled - index;
        }

        private static void RequireSize(int size)
        {
            if (size < MinSize || size > MaxSize)
            {
                throw new ArgumentOutOfRangeException(nameof(size), size, "a LUT size is " + MinSize + ".." + MaxSize);
            }
        }
    }
}
