using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-LEFTOVERS. In the 2026-09-24 trace (part 4) game-method lines ran about 24 minutes behind
    /// the event lines (19:01:04 against 19:25:03): their time came from a Stopwatch, which on
    /// macOS stops while the machine sleeps. <see cref="TraceClock"/> keeps the Stopwatch and
    /// re-anchors to the system clock when the two part.
    /// </summary>
    public class TraceClockTests
    {
        private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);

        /// <summary>
        /// A hand-cranked machine: a counter that stops during sleep, like mach_absolute_time, and a
        /// system clock that does not, and that the user can set.
        /// </summary>
        private sealed class FakeMachine
        {
            public FakeMachine(long frequency = 1_000_000_000L)
            {
                Frequency = frequency;
                System = new DateTimeOffset(2026, 9, 24, 19, 0, 0, Bangkok);
                Counter = 123_456_789L;
            }

            public long Frequency { get; }

            public long Counter;

            public DateTimeOffset System;

            /// <summary>Time passes while awake: both clocks move.</summary>
            public void Run(TimeSpan span)
            {
                Counter += (long)Math.Round(span.Ticks * (Frequency / (double)TimeSpan.TicksPerSecond));
                System += span;
            }

            /// <summary>The machine sleeps: only the system clock moves.</summary>
            public void Sleep(TimeSpan span) => System += span;

            /// <summary>Someone sets the system clock; the counter does not notice.</summary>
            public void SetClock(TimeSpan by) => System += by;

            public TraceClock Clock(TimeSpan? threshold = null, TimeSpan? poll = null) =>
                new TraceClock(() => Counter, Frequency, () => System, threshold, poll);
        }

        [Fact]
        public void Without_a_jump_the_clock_follows_the_system_clock_and_never_reanchors()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock();

            for (var minute = 0; minute < 60; minute++)
            {
                machine.Run(TimeSpan.FromMinutes(1));
                Assert.Null(clock.Check("poll"));
                Assert.Equal(machine.System, clock.Now);
            }

            Assert.Equal(0, clock.Reanchors);
        }

        [Fact]
        public void Drift_within_the_threshold_is_left_alone()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock();

            machine.Run(TimeSpan.FromMinutes(30));
            machine.SetClock(TimeSpan.FromMilliseconds(200));

            Assert.Null(clock.Check("poll"));
            Assert.Equal(machine.System - TimeSpan.FromMilliseconds(200), clock.Now);
        }

        [Fact]
        public void Drift_just_over_the_threshold_reanchors()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock();

            machine.Run(TimeSpan.FromMinutes(30));
            machine.SetClock(TraceClock.DefaultThreshold + TimeSpan.FromMilliseconds(1));

            var jump = clock.Check("poll");

            Assert.NotNull(jump);
            Assert.Equal(TraceClock.DefaultThreshold + TimeSpan.FromMilliseconds(1), jump!.Jump);
            Assert.Equal(machine.System, clock.Now);
        }

        /// <summary>The 2026-09-24 case: 24 minutes of sleep, then the next check.</summary>
        [Fact]
        public void A_sleep_is_corrected_by_the_next_check()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock();
            machine.Run(TimeSpan.FromMinutes(1));

            machine.Sleep(TimeSpan.FromMinutes(24));
            machine.Run(TimeSpan.FromSeconds(1));

            // Before the check the counter-derived time is 24 minutes behind: the bug.
            Assert.Equal(machine.System - TimeSpan.FromMinutes(24), clock.Now);

            var jump = clock.Check("focus");

            Assert.NotNull(jump);
            Assert.Equal(TimeSpan.FromMinutes(24), jump!.Jump);
            Assert.Equal("+24m00.0s", jump.JumpText);
            Assert.Equal("focus", jump.Reason);
            Assert.False(jump.OffsetChanged);
            Assert.Equal(1, jump.Count);
            Assert.Contains("slept", jump.Summary, StringComparison.Ordinal);

            Assert.Equal(machine.System, clock.Now);
            Assert.Equal(1, clock.Reanchors);

            // Once corrected, it stays corrected.
            machine.Run(TimeSpan.FromMinutes(5));
            Assert.Null(clock.Check("poll"));
            Assert.Equal(machine.System, clock.Now);
        }

        [Fact]
        public void A_reading_taken_before_the_sleep_keeps_its_time_when_written_after_the_reanchor()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock();
            machine.Run(TimeSpan.FromMinutes(1));

            // Queued by the game just before the lid closed, written by the writer thread after.
            var queued = clock.Timestamp;
            var queuedAt = machine.System;

            machine.Sleep(TimeSpan.FromMinutes(24));
            machine.Run(TimeSpan.FromMilliseconds(10));
            Assert.NotNull(clock.Check("poll"));

            Assert.Equal(queuedAt, clock.ToWall(queued));

            // And one taken after the re-anchor is dated from the new anchor.
            machine.Run(TimeSpan.FromSeconds(3));
            Assert.Equal(machine.System, clock.ToWall(clock.Timestamp));
        }

        [Fact]
        public void The_system_clock_set_back_reanchors_backwards()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock();
            machine.Run(TimeSpan.FromMinutes(10));

            machine.SetClock(TimeSpan.FromHours(-1));
            var jump = clock.Check("poll");

            Assert.NotNull(jump);
            Assert.Equal(TimeSpan.FromHours(-1), jump!.Jump);
            Assert.Equal("-1h00m00.0s", jump.JumpText);
            Assert.Contains("set back", jump.Summary, StringComparison.Ordinal);
            Assert.Equal(machine.System, clock.Now);

            // Time keeps moving forward from the new anchor.
            machine.Run(TimeSpan.FromSeconds(30));
            Assert.Equal(machine.System, clock.Now);
        }

        [Fact]
        public void A_utc_offset_change_reanchors_without_moving_the_instant()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock();
            machine.Run(TimeSpan.FromMinutes(10));

            // Daylight saving or a new time zone: same instant, new offset.
            machine.System = machine.System.ToOffset(TimeSpan.FromHours(8));
            var jump = clock.Check("focus");

            Assert.NotNull(jump);
            Assert.True(jump!.OffsetChanged);
            Assert.Equal(TimeSpan.Zero, jump.Jump);
            Assert.Equal("clock re-anchored: UTC offset +07:00 -> +08:00 (time zone or daylight saving)", jump.Summary);
            Assert.Equal(TimeSpan.FromHours(8), clock.Now.Offset);
            Assert.Equal(machine.System, clock.Now);
        }

        [Fact]
        public void Poll_measures_at_most_once_per_interval_of_counter_time()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock(poll: TimeSpan.FromMilliseconds(50));

            machine.Run(TimeSpan.FromMilliseconds(49));
            machine.SetClock(TimeSpan.FromMinutes(5));

            // The counter has not moved 50 ms since the clock was made, so Poll does not look.
            Assert.Null(clock.Poll("poll"));

            machine.Run(TimeSpan.FromMilliseconds(1));
            var jump = clock.Poll("poll");

            Assert.NotNull(jump);
            Assert.Equal(TimeSpan.FromMinutes(5), jump!.Jump);

            // A forced check ignores the interval.
            machine.SetClock(TimeSpan.FromMinutes(1));
            Assert.Null(clock.Poll("poll"));
            Assert.NotNull(clock.Check("resume"));
        }

        /// <summary>
        /// After a sleep the counter has barely moved, so the writer thread's Poll fires within one
        /// interval of the machine waking, not one interval of wall time.
        /// </summary>
        [Fact]
        public void After_a_sleep_poll_fires_within_one_interval_of_waking()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock(poll: TimeSpan.FromMilliseconds(50));
            machine.Run(TimeSpan.FromSeconds(10));
            Assert.Null(clock.Poll("poll"));

            machine.Sleep(TimeSpan.FromHours(8));
            Assert.Null(clock.Poll("poll"));

            machine.Run(TimeSpan.FromMilliseconds(50));
            var jump = clock.Poll("poll");

            Assert.NotNull(jump);
            Assert.Equal(TimeSpan.FromHours(8), jump!.Jump);
        }

        [Fact]
        public void History_is_bounded_and_a_reading_older_than_every_kept_anchor_uses_the_oldest()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock();
            var veryOld = clock.Timestamp;

            var anchors = new List<(long Counter, DateTimeOffset Wall)>();
            for (var i = 0; i < TraceClock.AnchorHistory + 2; i++)
            {
                machine.Run(TimeSpan.FromMinutes(1));
                machine.Sleep(TimeSpan.FromHours(1));
                Assert.NotNull(clock.Check("poll"));
                anchors.Add((machine.Counter, machine.System));
            }

            Assert.Equal(TraceClock.AnchorHistory + 2, clock.Reanchors);

            // The creation anchor and the first two re-anchors have fallen out; the oldest kept is
            // re-anchor number 3, and the very old reading is extrapolated back from it.
            var oldest = anchors[anchors.Count - TraceClock.AnchorHistory];
            var ticks = (long)Math.Round((veryOld - oldest.Counter) * (TimeSpan.TicksPerSecond / (double)machine.Frequency));
            Assert.Equal(oldest.Wall.AddTicks(ticks), clock.ToWall(veryOld));

            // Each kept anchor still serves the readings taken in its own span.
            var third = anchors[anchors.Count - 3];
            Assert.Equal(third.Wall + TimeSpan.FromSeconds(1), clock.ToWall(third.Counter + machine.Frequency));
        }

        [Theory]
        [InlineData(10_000_000L)]       // Mono's Stopwatch: 100 ns units
        [InlineData(24_000_000L)]       // Apple silicon's raw timebase
        [InlineData(1_000_000_000L)]    // .NET on macOS: nanoseconds
        public void The_counter_frequency_is_honoured(long frequency)
        {
            var machine = new FakeMachine(frequency);
            var clock = machine.Clock();

            machine.Run(TimeSpan.FromMinutes(90) + TimeSpan.FromMilliseconds(123));

            Assert.Equal(machine.System, clock.Now);
            Assert.Equal(frequency, clock.Frequency);
        }

        [Fact]
        public async Task Two_threads_that_notice_the_same_jump_reanchor_once()
        {
            var machine = new FakeMachine();
            var clock = machine.Clock();
            machine.Sleep(TimeSpan.FromMinutes(24));

            var jumps = 0;
            using (var start = new ManualResetEventSlim(false))
            {
                var tasks = new Task[8];
                for (var i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = Task.Run(() =>
                    {
                        start.Wait();
                        if (clock.Check("poll") != null)
                        {
                            Interlocked.Increment(ref jumps);
                        }
                    });
                }

                start.Set();
                await Task.WhenAll(tasks);
            }

            Assert.Equal(1, jumps);
            Assert.Equal(1, clock.Reanchors);
            Assert.Equal(machine.System, clock.Now);
        }

        [Fact]
        public void The_constructor_refuses_what_it_cannot_work_with()
        {
            Func<long> counter = () => 0L;
            Func<DateTimeOffset> system = () => DateTimeOffset.Now;

            Assert.Throws<ArgumentNullException>(() => new TraceClock(null!, 1L, system));
            Assert.Throws<ArgumentNullException>(() => new TraceClock(counter, 1L, null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceClock(counter, 0L, system));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceClock(counter, -5L, system));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceClock(counter, 1L, system, TimeSpan.FromTicks(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceClock(counter, 1L, system, null, TimeSpan.FromTicks(-1)));
        }

        [Fact]
        public void Defaults_are_small()
        {
            var clock = new FakeMachine().Clock();

            Assert.Equal(TimeSpan.FromMilliseconds(250), clock.Threshold);
            Assert.Equal(TimeSpan.FromMilliseconds(50), clock.PollInterval);
        }

        [Theory]
        [InlineData(0L, "+0.0s")]
        [InlineData(400L, "+0.4s")]
        [InlineData(50L, "+0.1s")]
        [InlineData(-49L, "-0.0s")]
        [InlineData(59_960L, "+1m00.0s")]
        [InlineData(61_200L, "+1m01.2s")]
        [InlineData(1_440_000L, "+24m00.0s")]
        [InlineData(-3_600_000L, "-1h00m00.0s")]
        [InlineData(3_723_450L, "+1h02m03.5s")]
        [InlineData(90_000_000L, "+25h00m00.0s")]
        public void Jumps_are_written_signed_to_a_tenth_of_a_second(long milliseconds, string expected)
        {
            Assert.Equal(expected, TraceClockJump.FormatJump(TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Fact]
        public void An_offset_change_that_also_moved_the_clock_names_both()
        {
            var before = new DateTimeOffset(2026, 3, 29, 1, 59, 0, TimeSpan.FromHours(1));
            var after = before.AddSeconds(90).ToOffset(TimeSpan.FromHours(2));

            var jump = new TraceClockJump("poll", before, after, offsetChanged: true, count: 3);

            Assert.Equal(
                "clock re-anchored: UTC offset +01:00 -> +02:00 (time zone or daylight saving); +1m30.0s: "
                + "the trace clock had fallen behind the system clock (the machine slept, or the clock was set forward)",
                jump.Summary);
            Assert.Equal(3, jump.Count);
        }
    }
}
