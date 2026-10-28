using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class TraceHealthTests
    {
        private sealed class FakeClock
        {
            public long Now;

            public long Read() => Now;
        }

        // ----- HealthStats ---------------------------------------------------------------------

        [Fact]
        public void An_empty_interval_reports_zeroes_rather_than_infinities()
        {
            var stats = new HealthStats();

            Assert.Equal(0, stats.Count);
            Assert.Equal(0d, stats.AverageMs);
            Assert.Equal(0d, stats.MinMs);
            Assert.Equal(0d, stats.MaxMs);
            Assert.Equal(0d, stats.P95Ms);
            Assert.Equal(0d, stats.AverageFps);
            Assert.Equal(0d, stats.MinFps);
            Assert.Equal(0, stats.Spikes);
        }

        [Fact]
        public void Average_min_and_max_describe_the_interval()
        {
            var stats = new HealthStats();
            stats.AddFrame(10d);
            stats.AddFrame(20d);
            stats.AddFrame(30d);

            Assert.Equal(3, stats.Count);
            Assert.Equal(20d, stats.AverageMs, 6);
            Assert.Equal(10d, stats.MinMs);
            Assert.Equal(30d, stats.MaxMs);
        }

        [Fact]
        public void Frames_per_second_is_the_reciprocal_of_the_matching_frame_time()
        {
            var stats = new HealthStats();
            stats.AddFrame(10d);   // 100 fps
            stats.AddFrame(10d);
            stats.AddFrame(40d);   // 25 fps, the worst frame

            Assert.Equal(1000d / 20d, stats.AverageFps, 6);
            Assert.Equal(25d, stats.MinFps, 6);    // from the longest frame
            Assert.Equal(100d, stats.MaxFps, 6);   // from the shortest
        }

        [Fact]
        public void P95_is_the_nearest_rank_value()
        {
            var stats = new HealthStats();
            for (var i = 1; i <= 100; i++)
            {
                stats.AddFrame(i);
            }

            // 100 samples of 1..100: the 95th is the smallest value at or above 95% of them.
            Assert.Equal(95d, stats.P95Ms);
            Assert.Equal(100d, stats.MaxMs);
            Assert.Equal(50.5d, stats.AverageMs, 6);
        }

        [Fact]
        public void Only_frames_over_the_threshold_count_as_spikes()
        {
            var stats = new HealthStats();
            stats.AddFrame(16.7d);
            stats.AddFrame(HealthStats.SpikeMs);        // exactly the threshold is not a spike
            stats.AddFrame(HealthStats.SpikeMs + 0.1d);
            stats.AddFrame(400d);

            Assert.Equal(2, stats.Spikes);
        }

        [Fact]
        public void Nonsense_samples_are_ignored()
        {
            var stats = new HealthStats();
            stats.AddFrame(double.NaN);
            stats.AddFrame(double.PositiveInfinity);
            stats.AddFrame(-5d);
            stats.AddFrame(16d);

            Assert.Equal(1, stats.Count);
            Assert.Equal(16d, stats.AverageMs);
        }

        [Fact]
        public void Reset_starts_the_next_interval_clean()
        {
            var stats = new HealthStats();
            stats.AddFrame(500d);
            stats.Reset();
            stats.AddFrame(10d);

            Assert.Equal(1, stats.Count);
            Assert.Equal(10d, stats.MaxMs);
            Assert.Equal(10d, stats.MinMs);
            Assert.Equal(0, stats.Spikes);
        }

        // ----- LogDedupe -----------------------------------------------------------------------

        [Fact]
        public void The_first_occurrence_is_always_written()
        {
            var dedupe = new LogDedupe(5d, new FakeClock().Read);

            Assert.True(dedupe.ShouldEmit("NullReferenceException at Foo", out var count));
            Assert.Equal(1, count);
        }

        [Fact]
        public void Repeats_inside_the_window_are_merged_into_the_next_line()
        {
            var clock = new FakeClock();
            var dedupe = new LogDedupe(5d, clock.Read);

            Assert.True(dedupe.ShouldEmit("boom", out _));
            for (var i = 0; i < 59; i++)
            {
                Assert.False(dedupe.ShouldEmit("boom", out _));
            }

            clock.Now += 5000;

            // The next one stands for itself plus the 59 that were merged.
            Assert.True(dedupe.ShouldEmit("boom", out var count));
            Assert.Equal(60, count);
        }

        [Fact]
        public void The_count_resets_after_it_has_been_reported()
        {
            var clock = new FakeClock();
            var dedupe = new LogDedupe(5d, clock.Read);

            dedupe.ShouldEmit("boom", out _);
            dedupe.ShouldEmit("boom", out _);
            clock.Now += 5000;
            dedupe.ShouldEmit("boom", out _);
            clock.Now += 5000;

            Assert.True(dedupe.ShouldEmit("boom", out var count));
            Assert.Equal(1, count);
        }

        [Fact]
        public void Different_messages_do_not_share_a_window()
        {
            var dedupe = new LogDedupe(5d, new FakeClock().Read);

            Assert.True(dedupe.ShouldEmit("first", out _));
            Assert.True(dedupe.ShouldEmit("second", out _));
            Assert.False(dedupe.ShouldEmit("first", out _));
            Assert.Equal(2, dedupe.TrackedKeys);
        }

        [Fact]
        public void A_window_that_has_not_elapsed_still_merges()
        {
            var clock = new FakeClock();
            var dedupe = new LogDedupe(5d, clock.Read);

            dedupe.ShouldEmit("boom", out _);
            clock.Now += 4999;
            Assert.False(dedupe.ShouldEmit("boom", out _));
            clock.Now += 1;
            Assert.True(dedupe.ShouldEmit("boom", out var count));
            Assert.Equal(2, count);
        }

        // ----- rendering -----------------------------------------------------------------------

        [Fact]
        public void A_bus_event_flattens_its_payload_to_top_level()
        {
            var builder = new TraceLineBuilder();
            var payload = new Dictionary<string, object?>
            {
                ["voice"] = "Voice_Tutorial_1_001",
                ["isStory"] = true,
                ["length"] = 3.25,
                ["tags"] = new[] { "a", "b" },
            };

            var line = builder.Bus(
                new DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, TimeSpan.FromHours(9)),
                frame: 1200, realtime: 42.5, gameClock: "13:45:01",
                system: "satone", name: "LineSpoken", payload: payload, threadId: 1);

            Assert.Equal(
                "{\"t\":\"2026-09-22T13:45:01.123+09:00\",\"rt\":42.5,\"gt\":\"13:45:01\",\"f\":1200," +
                "\"sys\":\"satone\",\"cls\":\"bus\",\"m\":\"LineSpoken\",\"th\":1," +
                "\"voice\":\"Voice_Tutorial_1_001\",\"isStory\":true,\"length\":3.25,\"tags\":[\"a\",\"b\"]}",
                line);
        }

        [Fact]
        public void A_bus_payload_cannot_overwrite_the_members_that_identify_the_line()
        {
            var builder = new TraceLineBuilder();
            var payload = new Dictionary<string, object?> { ["sys"] = "spoofed", ["f"] = -1, ["ok"] = true };

            var line = builder.Bus(DateTimeOffset.Now, 5, 1d, null, "event", "Changed", payload, 1);

            Assert.DoesNotContain("spoofed", line);
            Assert.Contains("\"f\":5", line);
            Assert.Contains("\"ok\":true", line);
        }

        [Fact]
        public void A_bus_event_with_no_payload_is_still_a_complete_line()
        {
            var builder = new TraceLineBuilder();

            var line = builder.Bus(DateTimeOffset.Now, 5, 1d, null, "health", "lowMemory", null, 1);

            Assert.Contains("\"sys\":\"health\"", line);
            Assert.Contains("\"cls\":\"bus\"", line);
            Assert.EndsWith("\"m\":\"lowMemory\",\"th\":1}", line);
        }

        [Fact]
        public void The_health_section_names_every_field_even_when_unreadable()
        {
            var builder = new TraceLineBuilder();

            var json = builder.Health(new TraceHealth
            {
                Fps = new FpsHealth { Average = 59.94, Min = 12.5, Max = 61.0 },
                FrameMs = new FrameMsHealth { Average = 16.68, P95 = 24.123, Max = 80d },
                Spikes = 2,
                UptimeSec = 90.44,
                ErrorsInInterval = 1,
                WarningsInInterval = 3,
            });

            Assert.Contains("\"fps\":{\"avg\":59.9,\"min\":12.5,\"max\":61}", json);
            Assert.Contains("\"frameMs\":{\"avg\":16.68,\"p95\":24.12,\"max\":80}", json);
            Assert.Contains("\"spikes\":2", json);
            Assert.Contains("\"uptimeSec\":90.4", json);
            Assert.Contains("\"errorsInInterval\":1", json);
            Assert.Contains("\"warningsInInterval\":3", json);

            // Readings the platform did not give us are null, never a guessed zero.
            foreach (var field in new[] { "gcMB", "monoUsedMB", "monoHeapMB", "allocatedMB", "reservedMB", "rssMB", "threads", "tweens", "targetFps", "vsync", "focused", "timeScale", "steam" })
            {
                Assert.Contains("\"" + field + "\":null", json);
            }

            Assert.Contains("\"audio\":null", json);
            Assert.Contains("\"screen\":null", json);
        }

        [Fact]
        public void A_snapshot_with_no_health_reading_writes_null()
        {
            var builder = new TraceLineBuilder();

            Assert.Contains("\"health\":null", builder.Snapshot(new TraceSnapshot()));
        }

        [Fact]
        public void The_footer_reports_what_the_bus_carried()
        {
            var builder = new TraceLineBuilder();

            var line = builder.Footer(
                DateTimeOffset.Now, 5400, 90.5, 4200, 0, null, busEmitted: 812, busDropped: 3,
                unpatchedMethods: new[] { "AudioPlayer.Stop" },
                cost: new TraceHealth
                {
                    HookNs = new HookCostHealth { Average = 412.6, P95 = 980.2, Samples = 1200 },
                    Queue = new QueueHealth { Length = 17, Dropped = 0 },
                });

            Assert.Contains("\"bus\":{\"emitted\":812,\"dropped\":3}", line);
            Assert.Contains("\"unpatchedCount\":1,\"unpatched\":[\"AudioPlayer.Stop\"]", line);
            Assert.EndsWith("\"hookNs\":{\"avg\":413,\"p95\":980,\"samples\":1200},\"queue\":{\"len\":17,\"dropped\":0}}", line);
        }
    }
}
