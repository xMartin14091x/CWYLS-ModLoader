using System;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>P1-1: SemVer 2.0.0 parsing and precedence, which pack dependencies hang on.</summary>
    public class PacksSemVerTests
    {
        [Theory]
        [InlineData("0.1.0", 0, 1, 0, null, null)]
        [InlineData("1.17.2", 1, 17, 2, null, null)]
        [InlineData("2.0.0-rc.1", 2, 0, 0, "rc.1", null)]
        [InlineData("1.0.0+build.7", 1, 0, 0, null, "build.7")]
        [InlineData("1.0.0-alpha.1+exp.sha.5114f85", 1, 0, 0, "alpha.1", "exp.sha.5114f85")]
        [InlineData("  1.2.3  ", 1, 2, 3, null, null)]
        public void Parses_the_parts(string text, int major, int minor, int patch, string? prerelease, string? build)
        {
            var version = SemVer.Parse(text);

            Assert.Equal(major, version.Major);
            Assert.Equal(minor, version.Minor);
            Assert.Equal(patch, version.Patch);
            Assert.Equal(prerelease, version.Prerelease);
            Assert.Equal(build, version.Build);
        }

        [Theory]
        [InlineData("1.0.0")]
        [InlineData("0.1.0-rc.1")]
        [InlineData("1.2.3+build.7")]
        [InlineData("1.0.0-alpha.1+exp.sha.5114f85")]
        public void Round_trips_through_ToString(string text) =>
            Assert.Equal(text, SemVer.Parse(text).ToString());

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1")]
        [InlineData("1.0")]
        [InlineData("1.0.0.0")]
        [InlineData("1.0.x")]
        [InlineData("01.0.0")]
        [InlineData("1.0.0-")]
        [InlineData("1.0.0-alpha..1")]
        [InlineData("1.0.0-01")]
        [InlineData("-1.0.0")]
        [InlineData("v1.0.0")]
        public void Rejects_what_is_not_a_version(string text)
        {
            Assert.False(SemVer.TryParse(text, out var version));
            Assert.Null(version);
            Assert.Throws<FormatException>(() => SemVer.Parse(text));
        }

        [Fact]
        public void TryParse_explains_the_failure()
        {
            Assert.False(SemVer.TryParse("1.0", out _, out var error));

            Assert.Contains("major.minor.patch", error!);
        }

        [Theory]
        [InlineData("1.0.0", "2.0.0")]
        [InlineData("2.0.0", "2.1.0")]
        [InlineData("2.1.0", "2.1.1")]
        [InlineData("1.0.0-alpha", "1.0.0")]
        [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
        [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
        [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
        [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
        [InlineData("1.0.0-rc.1", "1.0.0")]
        public void Orders_by_precedence(string lower, string higher)
        {
            var low = SemVer.Parse(lower);
            var high = SemVer.Parse(higher);

            Assert.True(low < high, lower + " < " + higher);
            Assert.True(high > low);
            Assert.True(low <= high);
            Assert.True(high >= low);
            Assert.NotEqual(low, high);
        }

        [Fact]
        public void Build_metadata_does_not_affect_precedence()
        {
            var plain = SemVer.Parse("1.0.0");
            var built = SemVer.Parse("1.0.0+20260922");

            Assert.Equal(plain, built);
            Assert.True(plain == built);
            Assert.False(plain < built);
            Assert.Equal(plain.GetHashCode(), built.GetHashCode());
        }

        [Fact]
        public void Null_sorts_below_every_version()
        {
            Assert.True(SemVer.Compare(null, SemVer.Parse("0.0.0")) < 0);
            Assert.True(SemVer.Compare(SemVer.Parse("0.0.0"), null) > 0);
            Assert.Equal(0, SemVer.Compare(null, null));
        }

        [Fact]
        public void A_prerelease_is_flagged_as_one()
        {
            Assert.True(SemVer.Parse("1.0.0-rc.1").IsPrerelease);
            Assert.False(SemVer.Parse("1.0.0").IsPrerelease);
        }

        [Fact]
        public void CompareTo_and_Equals_agree_with_the_operators()
        {
            var a = SemVer.Parse("1.2.3");
            var b = SemVer.Parse("1.2.3");
            var c = SemVer.Parse("1.2.4");

            Assert.Equal(0, a.CompareTo(b));
            Assert.True(a.Equals(b));
            Assert.True(a.Equals((object)b));
            Assert.True(a.CompareTo(c) < 0);
            Assert.True(a != c);
        }
    }
}
