using System;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The lock-free replacement for the per-call rate limiter. The old one took a process-wide
    /// lock and a string-keyed dictionary lookup on every traced call, which measured as +55 %
    /// frame time with AudioPlayer.Stop alone at ~13,000 calls a second.
    /// </summary>
    public class TraceAdmissionTests
    {
        /// <summary>Ticks per "second" in these tests. Real code passes Stopwatch.Frequency.</summary>
        private const long Window = 1000L;

        private const int Permits = 20;

        private static TraceEntry NewEntry(bool curated = false) =>
            new TraceEntry("Bulbul", "MusicService", "GetCurrentMusicProgress", curated);

        /// <summary>Drives one window with <paramref name="calls"/> calls; returns the first verdict.</summary>
        private static AdmitResult Window1(TraceEntry entry, ref long now, int calls, long window = Window)
        {
            var first = default(AdmitResult);
            for (var i = 0; i < calls; i++)
            {
                var result = TraceAdmission.Admit(entry, now, Window, Window, Permits);
                if (i == 0)
                {
                    first = result;
                }

                if (result.RemembersPayload)
                {
                    TraceAdmission.RememberArgs(entry, new object?[] { "call" + i });
                }
            }

            now += window;
            return first;
        }

        [Fact]
        public void The_first_call_is_always_written()
        {
            var entry = NewEntry();

            var result = TraceAdmission.Admit(entry, 0L, Window, Window, Permits);

            Assert.Equal(TraceAdmit.Write, result.Action);
            Assert.False(entry.IsPolling);
        }

        [Fact]
        public void Calls_past_the_cap_are_suppressed_inside_a_window()
        {
            var entry = NewEntry();

            for (var i = 0; i < Permits; i++)
            {
                Assert.Equal(TraceAdmit.Write, TraceAdmission.Admit(entry, 10L, Window, Window, Permits).Action);
            }

            Assert.Equal(TraceAdmit.Suppress, TraceAdmission.Admit(entry, 10L, Window, Window, Permits).Action);
        }

        [Fact]
        public void A_reopened_window_reports_what_the_previous_one_dropped()
        {
            var entry = NewEntry();
            long now = 0;

            for (var i = 0; i < Permits + 5; i++)
            {
                TraceAdmission.Admit(entry, now, Window, Window, Permits);
            }

            var result = TraceAdmission.Admit(entry, now + Window, Window, Window, Permits);

            Assert.Equal(TraceAdmit.Write, result.Action);
            Assert.Equal(5, result.Suppressed);
            Assert.Equal(Permits + 5, result.ObservedPerSecond);
        }

        [Fact]
        public void Three_hot_windows_in_a_row_switch_a_method_to_polling()
        {
            var entry = NewEntry();
            long now = 0;

            Window1(entry, ref now, 200);
            Assert.False(entry.IsPolling);
            Window1(entry, ref now, 200);
            Assert.False(entry.IsPolling);
            Window1(entry, ref now, 200);

            var fourth = Window1(entry, ref now, 200);

            Assert.True(fourth.EnteredPolling);
            Assert.Equal(200, fourth.ObservedPerSecond);
            Assert.True(entry.IsPolling);
        }

        [Fact]
        public void A_quiet_window_between_hot_ones_breaks_the_streak()
        {
            var entry = NewEntry();
            long now = 0;

            Window1(entry, ref now, 200);
            Window1(entry, ref now, 3);
            Window1(entry, ref now, 200);
            Window1(entry, ref now, 200);
            Window1(entry, ref now, 200);

            Assert.False(entry.IsPolling);
        }

        [Fact]
        public void Curated_methods_aggregate_when_hot_but_are_never_unpatched()
        {
            var entry = NewEntry(curated: true);
            long now = 0;

            for (var i = 0; i < 10; i++)
            {
                Window1(entry, ref now, 500);
            }

            // A curated poller (e.g. MusicService.GetCurrentMusicProgress at 4700/s) aggregates like any other,
            // otherwise it floods the file and the console; "curated" only means mirror-eligible and never unpatched.
            Assert.True(entry.IsPolling);
            Assert.False(TraceAdmission.ShouldUnpatch(entry, now + Window * 1000, 1, 0));
        }

        [Fact]
        public void In_polling_mode_a_window_becomes_one_line_with_the_exact_count()
        {
            var entry = NewEntry();
            long now = 0;
            for (var i = 0; i < 4; i++)
            {
                Window1(entry, ref now, 200);
            }

            Assert.True(entry.IsPolling);

            var aggregates = 0;
            var suppressed = 0;
            for (var i = 0; i < 300; i++)
            {
                var result = TraceAdmission.Admit(entry, now, Window, Window, Permits);
                if (result.Action == TraceAdmit.Aggregate)
                {
                    aggregates++;
                    Assert.Equal(200, result.AggregateCount);
                }
                else if (result.Action == TraceAdmit.Suppress)
                {
                    suppressed++;
                }
            }

            // The first call of the window closes the previous one; the other 299 are merged.
            Assert.Equal(1, aggregates);
            Assert.Equal(299, suppressed);
        }

        [Fact]
        public void The_aggregate_carries_the_last_arguments_of_the_window_it_closes()
        {
            var entry = NewEntry();
            long now = 0;
            for (var i = 0; i < 4; i++)
            {
                Window1(entry, ref now, 200);
            }

            Assert.True(entry.IsPolling);

            TraceAdmission.Admit(entry, now, Window, Window, Permits);
            TraceAdmission.RememberArgs(entry, new object?[] { "0.10" });
            TraceAdmission.Admit(entry, now, Window, Window, Permits);
            TraceAdmission.RememberArgs(entry, new object?[] { "0.30" });

            var closing = TraceAdmission.Admit(entry, now + Window, Window, Window, Permits);

            Assert.Equal(TraceAdmit.Aggregate, closing.Action);
            Assert.Equal(2, closing.AggregateCount);
            Assert.Equal(new object?[] { "0.30" }, Assert.IsType<object?[]>(closing.AggregatePayload));
        }

        [Fact]
        public void Five_quiet_windows_take_a_method_back_out_of_polling()
        {
            var entry = NewEntry();
            long now = 0;
            for (var i = 0; i < 4; i++)
            {
                Window1(entry, ref now, 200);
            }

            Assert.True(entry.IsPolling);

            // Six rollovers, not five: the first one closes the window that was still hot, and only
            // the five after it count as quiet.
            AdmitResult last = default;
            var rollovers = 0;
            while (!last.LeftPolling && rollovers < 10)
            {
                last = Window1(entry, ref now, 3);
                rollovers++;
            }

            Assert.True(last.LeftPolling);
            Assert.Equal(TraceAdmission.WindowsToLeavePolling + 1, rollovers);
            Assert.False(entry.IsPolling);
            Assert.False(last.RemembersPayload);
        }

        [Fact]
        public void A_zero_cap_means_no_limit_and_no_state()
        {
            var entry = NewEntry();

            for (var i = 0; i < 1000; i++)
            {
                Assert.Equal(TraceAdmit.Write, TraceAdmission.Admit(entry, i, Window, Window, 0).Action);
            }

            Assert.False(entry.IsPolling);
        }

        [Fact]
        public void A_null_entry_is_suppressed_rather_than_fatal()
        {
            Assert.Equal(TraceAdmit.Suppress, TraceAdmission.Admit(null!, 0L, Window, Window, Permits).Action);
        }

        [Fact]
        public void Concurrent_callers_never_lose_or_double_count_a_call()
        {
            // The whole design rests on the window rollover being elected by exactly one thread.
            var entry = NewEntry();
            const int threads = 8;
            const int perThread = 5000;
            var written = 0;
            var suppressed = 0;

            Parallel.For(0, threads, _ =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    var result = TraceAdmission.Admit(entry, 500L, Window, Window, Permits);
                    if (result.Action == TraceAdmit.Suppress)
                    {
                        Interlocked.Increment(ref suppressed);
                    }
                    else
                    {
                        Interlocked.Increment(ref written);
                    }
                }
            });

            Assert.Equal(threads * perThread, written + suppressed);
            Assert.Equal(Permits, written);
        }

        // ----- unpatch decision ----------------------------------------------------------------

        [Fact]
        public void A_method_is_unpatched_only_once_it_has_been_hot_for_long_enough()
        {
            var entry = NewEntry();
            long now = 0;
            for (var i = 0; i < 4; i++)
            {
                Window1(entry, ref now, 3000);
            }

            Assert.True(entry.IsPolling);

            // Hot enough, but not for long enough yet.
            Assert.False(TraceAdmission.ShouldUnpatch(entry, now, 2000, 20 * Window));
            Assert.True(TraceAdmission.ShouldUnpatch(entry, now + (20 * Window), 2000, 20 * Window));
        }

        [Fact]
        public void A_merely_busy_method_is_never_unpatched()
        {
            var entry = NewEntry();
            long now = 0;
            for (var i = 0; i < 4; i++)
            {
                Window1(entry, ref now, 100);
            }

            Assert.True(entry.IsPolling);
            Assert.False(TraceAdmission.ShouldUnpatch(entry, now + (60 * Window), 2000, 20 * Window));
        }

        [Fact]
        public void Curated_and_already_unpatched_methods_are_left_alone()
        {
            var curated = NewEntry(curated: true);
            long now = 0;
            for (var i = 0; i < 6; i++)
            {
                Window1(curated, ref now, 5000);
            }

            Assert.False(TraceAdmission.ShouldUnpatch(curated, now + (60 * Window), 2000, 20 * Window));

            var hot = NewEntry();
            now = 0;
            for (var i = 0; i < 4; i++)
            {
                Window1(hot, ref now, 5000);
            }

            Assert.True(hot.MarkUnpatched());
            Assert.False(hot.MarkUnpatched());
            Assert.False(TraceAdmission.ShouldUnpatch(hot, now + (60 * Window), 2000, 20 * Window));
        }
    }
}
