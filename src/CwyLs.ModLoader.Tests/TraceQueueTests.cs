using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class TraceQueueTests
    {
        [Fact]
        public void Items_come_out_in_the_order_they_went_in()
        {
            var queue = new BoundedTraceQueue<int>(10);

            queue.TryEnqueue(1);
            queue.TryEnqueue(2);
            queue.TryEnqueue(3);

            Assert.True(queue.TryDequeue(out var first));
            Assert.True(queue.TryDequeue(out var second));
            Assert.True(queue.TryDequeue(out var third));
            Assert.Equal(1, first);
            Assert.Equal(2, second);
            Assert.Equal(3, third);
            Assert.False(queue.TryDequeue(out _));
        }

        [Fact]
        public void Count_tracks_what_is_waiting()
        {
            var queue = new BoundedTraceQueue<string>(10);

            Assert.Equal(0, queue.Count);
            queue.TryEnqueue("a");
            queue.TryEnqueue("b");
            Assert.Equal(2, queue.Count);

            queue.TryDequeue(out _);
            Assert.Equal(1, queue.Count);
        }

        [Fact]
        public void A_full_queue_drops_rather_than_blocks()
        {
            // A game thread waiting on a log is the one outcome worse than a missing line.
            var queue = new BoundedTraceQueue<int>(3);

            Assert.True(queue.TryEnqueue(1));
            Assert.True(queue.TryEnqueue(2));
            Assert.True(queue.TryEnqueue(3));
            Assert.False(queue.TryEnqueue(4));
            Assert.False(queue.TryEnqueue(5));

            Assert.Equal(3, queue.Count);
            Assert.Equal(2L, queue.Dropped);
        }

        [Fact]
        public void Room_freed_by_a_reader_is_usable_again()
        {
            var queue = new BoundedTraceQueue<int>(2);

            queue.TryEnqueue(1);
            queue.TryEnqueue(2);
            Assert.False(queue.TryEnqueue(3));

            queue.TryDequeue(out _);

            Assert.True(queue.TryEnqueue(3));
            Assert.Equal(1L, queue.Dropped);
        }

        [Fact]
        public void A_nonsense_capacity_still_behaves()
        {
            var queue = new BoundedTraceQueue<int>(0);

            Assert.Equal(1, queue.Capacity);
            Assert.True(queue.TryEnqueue(1));
            Assert.False(queue.TryEnqueue(2));
            Assert.Equal(1L, queue.Dropped);
        }

        [Fact]
        public void Clear_empties_the_queue_without_counting_drops()
        {
            var queue = new BoundedTraceQueue<int>(10);
            queue.TryEnqueue(1);
            queue.TryEnqueue(2);

            queue.Clear();

            Assert.Equal(0, queue.Count);
            Assert.Equal(0L, queue.Dropped);
            Assert.True(queue.TryEnqueue(3));
        }

        [Fact]
        public void Enqueued_plus_dropped_equals_what_was_offered_under_contention()
        {
            // Many game threads produce, one writer thread consumes: the accounting has to add up
            // or the "dropped" number in the footer would be a lie.
            var queue = new BoundedTraceQueue<int>(64);
            const int threads = 8;
            const int perThread = 2000;
            var accepted = 0;

            Parallel.For(0, threads, _ =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    if (queue.TryEnqueue(i))
                    {
                        Interlocked.Increment(ref accepted);
                    }
                }
            });

            Assert.Equal(threads * perThread, accepted + queue.Dropped);
            Assert.True(queue.Count <= queue.Capacity);
        }

        // ----- hook cost meter -----------------------------------------------------------------

        [Fact]
        public void The_meter_samples_one_call_in_every_N()
        {
            var meter = new HookCostMeter(sampleEvery: 8);
            var sampled = 0;

            for (var i = 0; i < 800; i++)
            {
                if (meter.ShouldSample())
                {
                    sampled++;
                }
            }

            Assert.Equal(100, sampled);
        }

        [Fact]
        public void The_meter_reports_an_average_and_a_percentile_then_resets()
        {
            var meter = new HookCostMeter();

            for (var i = 1; i <= 100; i++)
            {
                meter.Add(i);
            }

            Assert.Equal(100L, meter.Samples);
            meter.Read(out var average, out var p95);

            // Ticks are converted to nanoseconds, so only the ratio is assertable here.
            Assert.True(average > 0d);
            Assert.True(p95 >= average);
            Assert.Equal(0L, meter.Samples);

            meter.Read(out var emptyAverage, out var emptyP95);
            Assert.Equal(0d, emptyAverage);
            Assert.Equal(0d, emptyP95);
        }

        [Fact]
        public void The_meter_ignores_a_negative_reading()
        {
            var meter = new HookCostMeter();

            meter.Add(-1);

            Assert.Equal(0L, meter.Samples);
        }
    }
}
