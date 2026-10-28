using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Covers the adaptive downsample. The numbers come from the first real 90 s session, where
    /// per-frame polling methods (MusicService.GetCurrentMusicProgress, AudioPlayer.Stop with
    /// 883,406 suppressed calls, input/cursor polling) produced most of a 12 MB file.
    /// </summary>
    public class TracePollingTests
    {
        private sealed class FakeClock
        {
            public long Now;

            public long Read() => Now;
        }

        private const string Key = "MusicService.GetCurrentMusicProgress";

        /// <summary>Drives one 1-second window with <paramref name="calls"/> calls; returns every verdict.</summary>
        private static List<RateVerdict> Window(RateLimiter limiter, FakeClock clock, int calls, bool exempt = false)
        {
            var verdicts = new List<RateVerdict>(calls);
            for (var i = 0; i < calls; i++)
            {
                verdicts.Add(limiter.Observe(Key, exempt));
                limiter.Remember(Key, new[] { "call" + i });
            }

            clock.Now += 1000;
            return verdicts;
        }

        [Fact]
        public void Three_hot_windows_in_a_row_switch_a_method_to_polling()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(20, clock.Read);

            Window(limiter, clock, 60);
            Assert.False(limiter.IsPolling(Key));
            Window(limiter, clock, 60);
            Assert.False(limiter.IsPolling(Key));
            Window(limiter, clock, 60);

            // The third rollover is the one that flips it; that rollover is the first call of the
            // fourth window, so the transition is reported there.
            var fourth = Window(limiter, clock, 60);

            Assert.True(fourth[0].EnteredPolling);
            Assert.Equal(60, fourth[0].ObservedPerSecond);
            Assert.True(limiter.IsPolling(Key));
            Assert.Contains(Key, limiter.PollingKeys);
        }

        [Fact]
        public void A_method_that_stays_under_the_cap_never_becomes_polling()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(20, clock.Read);

            for (var i = 0; i < 10; i++)
            {
                var verdicts = Window(limiter, clock, 5);
                Assert.All(verdicts, v => Assert.Equal(RateAction.Write, v.Action));
            }

            Assert.False(limiter.IsPolling(Key));
            Assert.Empty(limiter.PollingKeys);
        }

        [Fact]
        public void One_hot_window_between_quiet_ones_does_not_count()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(20, clock.Read);

            Window(limiter, clock, 60);
            Window(limiter, clock, 5);   // resets the streak
            Window(limiter, clock, 60);
            Window(limiter, clock, 60);
            Window(limiter, clock, 60);

            Assert.False(limiter.IsPolling(Key));
        }

        [Fact]
        public void Curated_methods_are_exempt_from_polling_mode()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(20, clock.Read);

            for (var i = 0; i < 10; i++)
            {
                Window(limiter, clock, 200, exempt: true);
            }

            // Still capped, still counted, but never downsampled: a curated event is meant to be
            // visible one call at a time.
            Assert.False(limiter.IsPolling(Key));
            Assert.Empty(limiter.PollingKeys);
        }

        [Fact]
        public void In_polling_mode_a_window_becomes_one_line_carrying_the_exact_count()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(20, clock.Read);

            Window(limiter, clock, 60);
            Window(limiter, clock, 60);
            Window(limiter, clock, 60);
            Window(limiter, clock, 60);   // enters polling on its first call
            Assert.True(limiter.IsPolling(Key));

            var next = Window(limiter, clock, 300);

            // One aggregate line, 299 silent calls - nothing dropped, everything counted.
            Assert.Equal(RateAction.Aggregate, next[0].Action);
            Assert.Equal(1, next.Count(v => v.Action == RateAction.Aggregate));
            Assert.Equal(299, next.Count(v => v.Action == RateAction.Suppress));
            Assert.Equal(60, next[0].AggregateCount);
        }

        [Fact]
        public void The_aggregate_line_carries_the_last_arguments_of_the_window_it_closes()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(20, clock.Read);

            for (var i = 0; i < 4; i++)
            {
                Window(limiter, clock, 60);
            }

            Assert.True(limiter.IsPolling(Key));

            // A window of 3 calls whose arguments change each time.
            limiter.Observe(Key);
            limiter.Remember(Key, new[] { "0.10" });
            limiter.Observe(Key);
            limiter.Remember(Key, new[] { "0.20" });
            limiter.Observe(Key);
            limiter.Remember(Key, new[] { "0.30" });
            clock.Now += 1000;

            var closing = limiter.Observe(Key);

            Assert.Equal(RateAction.Aggregate, closing.Action);
            Assert.Equal(3, closing.AggregateCount);
            Assert.Equal(new[] { "0.30" }, Assert.IsType<string[]>(closing.AggregatePayload));
        }

        [Fact]
        public void Five_quiet_windows_take_a_method_back_out_of_polling()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(20, clock.Read);

            for (var i = 0; i < 4; i++)
            {
                Window(limiter, clock, 60);
            }

            Assert.True(limiter.IsPolling(Key));

            RateVerdict last = default;
            for (var i = 0; i < 5; i++)
            {
                last = Window(limiter, clock, 3)[0];
            }

            var after = limiter.Observe(Key);

            Assert.True(after.LeftPolling);
            Assert.False(limiter.IsPolling(Key));
            Assert.Empty(limiter.PollingKeys);
            Assert.False(last.LeftPolling);
        }

        [Fact]
        public void A_higher_polling_cap_shortens_the_window()
        {
            var clock = new FakeClock();
            var limiter = new RateLimiter(20, clock.Read, pollingCapPerSecond: 4);

            for (var i = 0; i < 4; i++)
            {
                Window(limiter, clock, 60);
            }

            Assert.True(limiter.IsPolling(Key));

            // 250 ms windows: four aggregate lines across one second instead of one.
            var aggregates = 0;
            for (var tick = 0; tick < 4; tick++)
            {
                for (var i = 0; i < 20; i++)
                {
                    if (limiter.Observe(Key).Action == RateAction.Aggregate)
                    {
                        aggregates++;
                    }
                }

                clock.Now += 250;
            }

            Assert.Equal(4, aggregates);
        }

        [Fact]
        public void An_aggregate_line_is_written_as_agg_and_count()
        {
            var builder = new TraceLineBuilder();

            var line = builder.Call(
                new System.DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, System.TimeSpan.FromHours(9)),
                frame: 1200,
                realtime: 42.5,
                gameClock: "13:45:01",
                systemName: "root",
                className: "MusicService",
                methodName: "GetCurrentMusicProgress",
                args: new[] { "0.30" },
                threadId: 1,
                instance: null,
                returnValue: null,
                note: null,
                aggregateCount: 4730);

            Assert.Contains("\"args\":[\"0.30\"],\"th\":1,\"inst\":null,\"agg\":true,\"count\":4730}", line);
        }

        [Fact]
        public void The_footer_lists_the_methods_that_were_aggregated()
        {
            var builder = new TraceLineBuilder();
            IReadOnlyList<string> polling = new[] { "AudioPlayer.Stop", Key };

            var line = builder.Footer(
                new System.DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, System.TimeSpan.FromHours(9)),
                frame: 5400, realtime: 90.5, lines: 4200, suppressed: 0, pollingMethods: polling);

            Assert.Contains("\"pollingCount\":2", line);
            Assert.Contains("\"polling\":[\"AudioPlayer.Stop\",\"MusicService.GetCurrentMusicProgress\"]", line);

            // A session with nothing to aggregate still writes the members, so readers can rely on them.
            var quiet = builder.Footer(
                new System.DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, System.TimeSpan.FromHours(9)),
                5400, 90.5, 4200, 0);

            Assert.Contains("\"pollingCount\":0,\"polling\":[]", quiet);
        }
    }
}
