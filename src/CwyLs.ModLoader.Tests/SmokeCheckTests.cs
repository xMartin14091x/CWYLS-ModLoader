using System;
using CwyLs.ModLoader.Core.Smoke;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class SmokeCheckTests
    {
        [Fact]
        public void Execute_returns_the_checks_own_result()
        {
            var check = new SmokeCheck("p0.paths", "P0", () => SmokeCheckResult.Pass("p0.paths", "both exist"));

            var result = check.Execute();

            Assert.True(result.Ok);
            Assert.Equal("p0.paths", result.Id);
            Assert.Equal("both exist", result.Detail);
            Assert.True(result.ElapsedMs >= 0);
        }

        [Fact]
        public void Execute_turns_an_exception_into_a_failed_result()
        {
            // The in-game checks reflect over game types; a renamed type must be a reported
            // failure, not an exception escaping into the runner.
            var check = new SmokeCheck(
                "p0.seam.HeroineService",
                "P0",
                () => throw new InvalidOperationException("boom"));

            var result = check.Execute();

            Assert.False(result.Ok);
            Assert.Equal("p0.seam.HeroineService", result.Id);
            Assert.Equal("InvalidOperationException: boom", result.Detail);
        }

        [Fact]
        public void Execute_fails_a_check_that_returns_nothing()
        {
            var check = new SmokeCheck("p0.null", "P0", () => null!);

            var result = check.Execute();

            Assert.False(result.Ok);
            Assert.Equal("check returned no result", result.Detail);
        }

        [Fact]
        public void Execute_reports_under_the_registered_id()
        {
            // A copy-pasted check body that names the wrong id must not rename the report key.
            var check = new SmokeCheck("p0.paths", "P0", () => SmokeCheckResult.Fail("typo", "nope"));

            var result = check.Execute();

            Assert.Equal("p0.paths", result.Id);
            Assert.Equal("nope", result.Detail);
            Assert.False(result.Ok);
        }

        [Fact]
        public void Constructor_rejects_an_incomplete_descriptor()
        {
            Assert.Throws<ArgumentException>(() => new SmokeCheck("", "P0", () => SmokeCheckResult.Pass("x")));
            Assert.Throws<ArgumentException>(() => new SmokeCheck("x", "", () => SmokeCheckResult.Pass("x")));
            Assert.Throws<ArgumentNullException>(() => new SmokeCheck("x", "P0", null!));
        }

        [Fact]
        public void Result_detail_is_never_null_and_elapsed_cannot_be_negative()
        {
            Assert.Equal(string.Empty, SmokeCheckResult.Pass("x").Detail);
            Assert.Throws<ArgumentOutOfRangeException>(() => new SmokeCheckResult("x", true, "d", -1));
            Assert.Throws<ArgumentException>(() => new SmokeCheckResult("", true));
        }

        [Fact]
        public void WithId_and_WithElapsed_copy_without_mutating()
        {
            var original = SmokeCheckResult.Pass("a", "d");

            var moved = original.WithId("b").WithElapsed(42);

            Assert.Equal("a", original.Id);
            Assert.Equal(0, original.ElapsedMs);
            Assert.Equal("b", moved.Id);
            Assert.Equal(42, moved.ElapsedMs);
            Assert.Equal("d", moved.Detail);
        }
    }
}
