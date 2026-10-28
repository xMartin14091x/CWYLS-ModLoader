using System;
using CwyLs.ModLoader.Core.Events;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class EventsThrottleTests
    {
        /// <summary>A hand-cranked clock, so the tests never depend on wall time.</summary>
        private sealed class FakeClock
        {
            public long Now;

            public long Read() => Now;
        }

        [Fact]
        public void The_first_call_for_a_key_always_passes()
        {
            var clock = new FakeClock();
            var throttle = new EventThrottle(1000, clock.Read);

            Assert.True(throttle.TryPass("Work"));
            Assert.True(throttle.TryPass("Break"));
            Assert.Equal(2L, throttle.Passed);
            Assert.Equal(0L, throttle.Suppressed);
        }

        [Fact]
        public void A_repeat_inside_the_window_is_dropped_and_counted()
        {
            var clock = new FakeClock();
            var throttle = new EventThrottle(1000, clock.Read);

            Assert.True(throttle.TryPass("Work"));

            clock.Now = 999;
            Assert.False(throttle.TryPass("Work"));
            Assert.False(throttle.TryPass("Work"));

            Assert.Equal(1L, throttle.Passed);
            Assert.Equal(2L, throttle.Suppressed);
        }

        [Fact]
        public void The_window_reopens_exactly_at_the_interval()
        {
            var clock = new FakeClock();
            var throttle = new EventThrottle(1000, clock.Read);

            Assert.True(throttle.TryPass("Work"));

            clock.Now = 1000;
            Assert.True(throttle.TryPass("Work"));

            clock.Now = 1999;
            Assert.False(throttle.TryPass("Work"));

            clock.Now = 2000;
            Assert.True(throttle.TryPass("Work"));
        }

        [Fact]
        public void A_dropped_call_does_not_extend_the_window()
        {
            var clock = new FakeClock();
            var throttle = new EventThrottle(1000, clock.Read);

            // The pomodoro update observable fires every frame; the key must still pass once a
            // second rather than never passing again.
            Assert.True(throttle.TryPass("Work"));
            for (clock.Now = 16; clock.Now < 1000; clock.Now += 16)
            {
                Assert.False(throttle.TryPass("Work"));
            }

            clock.Now = 1008;
            Assert.True(throttle.TryPass("Work"));
        }

        [Fact]
        public void Keys_are_independent()
        {
            var clock = new FakeClock();
            var throttle = new EventThrottle(1000, clock.Read);

            Assert.True(throttle.TryPass("Todos"));
            Assert.False(throttle.TryPass("Todos"));

            // The game saves in bursts of different sections; throttling one must not delay another.
            Assert.True(throttle.TryPass("Notes"));
            Assert.True(throttle.TryPass("Setting"));
            Assert.Equal(3, throttle.KeyCount);
        }

        [Fact]
        public void A_zero_interval_never_throttles()
        {
            var clock = new FakeClock();
            var throttle = new EventThrottle(0, clock.Read);

            for (var i = 0; i < 5; i++)
            {
                Assert.True(throttle.TryPass("Work"));
            }

            Assert.Equal(5L, throttle.Passed);
            Assert.Equal(0L, throttle.Suppressed);
        }

        [Fact]
        public void Reset_forgets_every_key_and_zeroes_the_counters()
        {
            var clock = new FakeClock();
            var throttle = new EventThrottle(1000, clock.Read);

            Assert.True(throttle.TryPass("Work"));
            Assert.False(throttle.TryPass("Work"));

            throttle.Reset();

            Assert.Equal(0, throttle.KeyCount);
            Assert.Equal(0L, throttle.Passed);
            Assert.Equal(0L, throttle.Suppressed);
            Assert.True(throttle.TryPass("Work"));
        }

        [Fact]
        public void A_negative_interval_and_a_blank_key_are_rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EventThrottle(-1));

            var throttle = new EventThrottle(10);
            Assert.Throws<ArgumentException>(() => throttle.TryPass(" "));
            Assert.Throws<ArgumentException>(() => throttle.TryPass(null!));
        }

        [Fact]
        public void The_default_clock_is_monotonic_and_usable()
        {
            var throttle = new EventThrottle(60_000);

            Assert.Equal(60_000L, throttle.MinIntervalMs);
            Assert.True(throttle.TryPass("Work"));
            Assert.False(throttle.TryPass("Work"));
        }
    }
}
