using System;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Grading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: the 3D LUT - <c>.cube</c> parsing, trilinear sampling, and the strip layout URP's
    /// <c>ColorLookup</c> samples (<c>ApplyLut2D</c>: x = r + N * b, y = g, row 0 at the bottom).
    /// </summary>
    public class GradingLutTests
    {
        private static string Cube(int size, Func<double, double, double, (double, double, double)> f, string header = "")
        {
            var sb = new StringBuilder("TITLE \"test\"\n# comment\n").Append(header)
                .Append("LUT_3D_SIZE ").Append(size.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (var b = 0; b < size; b++)
            {
                for (var g = 0; g < size; g++)
                {
                    for (var r = 0; r < size; r++)
                    {
                        var (x, y, z) = f(r / (double)(size - 1), g / (double)(size - 1), b / (double)(size - 1));
                        sb.Append(x.ToString("0.######", CultureInfo.InvariantCulture)).Append(' ')
                          .Append(y.ToString("0.######", CultureInfo.InvariantCulture)).Append(' ')
                          .Append(z.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
                    }
                }
            }

            return sb.ToString();
        }

        [Fact]
        public void An_identity_cube_samples_as_the_identity()
        {
            var lut = Lut3D.ParseCube(Cube(17, (r, g, b) => (r, g, b)), out var problem);

            Assert.Null(problem);
            Assert.Equal(17, lut!.Size);
            var c = lut.Sample(new Rgb(0.123, 0.456, 0.789));
            Assert.Equal(0.123, c.R, 5);
            Assert.Equal(0.456, c.G, 5);
            Assert.Equal(0.789, c.B, 5);
        }

        [Fact]
        public void Red_varies_fastest_in_a_cube()
        {
            var lut = Lut3D.ParseCube(Cube(2, (r, g, b) => (r, 0, 0)), out _)!;

            Assert.Equal(1, lut.At(1, 0, 0).R, 6);
            Assert.Equal(0, lut.At(0, 1, 0).R, 6);
        }

        [Fact]
        public void A_domain_is_folded_in()
        {
            // Table indexed over 0..2: output = input / 2 means identity on the unit cube after folding.
            var text = Cube(5, (r, g, b) => (r, g, b), "DOMAIN_MIN 0 0 0\nDOMAIN_MAX 2 2 2\n");
            var lut = Lut3D.ParseCube(text, out var problem)!;

            Assert.Null(problem);
            Assert.Equal(0.25, lut.Sample(Rgb.Grey(0.5)).R, 5);
        }

        [Theory]
        [InlineData("", "empty")]
        [InlineData("LUT_1D_SIZE 16\n", "1D LUT")]
        [InlineData("LUT_3D_SIZE 1\n", "LUT_3D_SIZE must be")]
        [InlineData("0 0 0\n", "no LUT_3D_SIZE")]
        [InlineData("LUT_3D_SIZE 2\n0 0 0\n", "needs 8 entries")]
        [InlineData("LUT_3D_SIZE 2\n0 0\n", "three numbers")]
        public void A_broken_cube_is_refused_with_a_reason(string text, string expected)
        {
            var lut = Lut3D.ParseCube(text, out var problem);

            Assert.Null(lut);
            Assert.Contains(expected, problem!);
        }

        [Fact]
        public void The_strip_layout_is_r_plus_n_times_b_by_g()
        {
            const int n = 4;
            var lut = Lut3D.Bake(n, c => c);
            var strip = lut.ToStripRgba();
            var width = n * n;

            Assert.Equal(width * n * 4, strip.Length);

            // Grid (r=1, g=2, b=3) -> texel x = 1 + 4 * 3 = 13, y = 2.
            var o = 4 * (13 + (width * 2));
            Assert.Equal(1 / 3.0, strip[o], 5);
            Assert.Equal(2 / 3.0, strip[o + 1], 5);
            Assert.Equal(3 / 3.0, strip[o + 2], 5);
            Assert.Equal(1f, strip[o + 3]);
        }

        [Fact]
        public void A_strip_reads_back_as_the_same_table()
        {
            var lut = Lut3D.Bake(8, c => new Rgb(c.G, c.B, c.R * 0.5));
            var back = Lut3D.FromStrip(64, 8, lut.ToStripRgba(), out var problem)!;

            Assert.Null(problem);
            for (var b = 0; b < 8; b++)
            {
                for (var g = 0; g < 8; g++)
                {
                    for (var r = 0; r < 8; r++)
                    {
                        Assert.Equal(lut.At(r, g, b), back.At(r, g, b));
                    }
                }
            }
        }

        [Fact]
        public void A_strip_of_the_wrong_shape_is_refused()
        {
            Assert.Null(Lut3D.FromStrip(64, 16, new float[64 * 16 * 4], out var problem));
            Assert.Contains("wide", problem!);
        }

        [Fact]
        public void Sampling_clamps_to_the_cube()
        {
            var lut = Lut3D.Identity(9);
            Assert.Equal(Rgb.Grey(1), lut.Sample(Rgb.Grey(3)));
            Assert.Equal(Rgb.Grey(0), lut.Sample(Rgb.Grey(-1)));
        }

        [Fact]
        public void Bake_rejects_bad_sizes()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Lut3D.Identity(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Lut3D.Identity(66));
        }

        [Fact]
        public void Resampling_a_cube_to_the_pipeline_size_keeps_the_function()
        {
            var lut = Lut3D.ParseCube(Cube(17, (r, g, b) => (1 - r, g * g, b)), out _)!;
            var resampled = Lut3D.Bake(32, lut.Sample);

            var c = resampled.Sample(new Rgb(0.25, 0.5, 0.75));
            Assert.Equal(0.75, c.R, 3);
            Assert.Equal(0.25, c.G, 2);
            Assert.Equal(0.75, c.B, 3);
        }
    }
}
