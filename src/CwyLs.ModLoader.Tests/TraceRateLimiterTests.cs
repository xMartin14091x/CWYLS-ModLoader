using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class TraceRateLimiterTests
    {
        /// <summary>A hand-cranked clock, so the tests never depend on wall time.</summary>
        private sealed class FakeClock
        {
            public long Now;

            public long Read() => Now;
        }

        [Fact]
        public void Allows_the_budget_then_suppresses_the_rest()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(3, clock.Read);

            for (var i = 0; i < 3; i++)
            {
                Assert.True(limiter.TryAcquire("HeroineAI.ChangeState", out var suppressed), $"call {i} should pass");
                Assert.Equal(0, suppressed);
            }

            Assert.False(limiter.TryAcquire("HeroineAI.ChangeState", out _));
            Assert.False(limiter.TryAcquire("HeroineAI.ChangeState", out _));
            Assert.Equal(2L, limiter.PendingSuppressed);
        }

        [Fact]
        public void Reports_the_suppressed_count_when_the_window_reopens()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(1, clock.Read);

            Assert.True(limiter.TryAcquire("MusicService.PlayNextMusic", out _));
            Assert.False(limiter.TryAcquire("MusicService.PlayNextMusic", out _));
            Assert.False(limiter.TryAcquire("MusicService.PlayNextMusic", out _));

            clock.Now += 1000;

            Assert.True(limiter.TryAcquire("MusicService.PlayNextMusic", out var suppressed));
            Assert.Equal(2, suppressed);

            // The count is reported once, not on every later call.
            clock.Now += 1000;
            Assert.True(limiter.TryAcquire("MusicService.PlayNextMusic", out var again));
            Assert.Equal(0, again);
        }

        [Fact]
        public void Budgets_are_per_key()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(1, clock.Read);

            Assert.True(limiter.TryAcquire("A.One", out _));
            Assert.False(limiter.TryAcquire("A.One", out _));
            Assert.True(limiter.TryAcquire("B.Two", out _));
            Assert.Equal(2, limiter.TrackedKeys);
        }

        [Fact]
        public void A_zero_budget_means_no_limit_at_all()
        {
            var limiter = new RateLimiter(0);

            Assert.True(limiter.IsUnlimited);
            for (var i = 0; i < 1000; i++)
            {
                Assert.True(limiter.TryAcquire("A.One", out var suppressed));
                Assert.Equal(0, suppressed);
            }

            Assert.Equal(0, limiter.TrackedKeys);
        }

        [Fact]
        public void A_window_that_has_not_elapsed_keeps_refusing()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(1, clock.Read);

            Assert.True(limiter.TryAcquire("A.One", out _));
            clock.Now += 999;
            Assert.False(limiter.TryAcquire("A.One", out _));
            clock.Now += 1;
            Assert.True(limiter.TryAcquire("A.One", out var suppressed));
            Assert.Equal(1, suppressed);
        }
    }
}
