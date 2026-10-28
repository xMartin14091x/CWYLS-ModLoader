using System;
using System.Diagnostics;
using System.Threading;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Measures what the trace's own Harmony prefix costs, by timing one call in every N.
    /// </summary>
    /// <remarks>
    /// The overhead belongs in the stream it is the overhead of: a reader should be able to see
    /// from the trace itself whether the trace is what is slowing the game down. Sampling keeps the
    /// measurement from becoming the cost — two stopwatch reads on every call would be most of the
    /// budget on a method called thirteen thousand times a second.
    /// </remarks>
    public sealed class HookCostMeter
    {
        /// <summary>Default: one call in 256 is timed.</summary>
        public const int DefaultSampleEvery = 256;

        /// <summary>Default number of samples kept for the percentile.</summary>
        public const int DefaultReservoir = 512;

        private readonly object _gate = new object();
        private readonly long[] _reservoir;
        private readonly int _mask;

        private int _counter;
        private int _filled;
        private int _next;
        private long _sum;
        private long _samples;
        private long _max;

        /// <summary>Creates a meter.</summary>
        /// <param name="sampleEvery">Sample one call in this many. Rounded up to a power of two.</param>
        /// <param name="reservoir">How many recent samples to keep for the percentile.</param>
        public HookCostMeter(int sampleEvery = DefaultSampleEvery, int reservoir = DefaultReservoir)
        {
            var every = 1;
            while (every < sampleEvery)
            {
                every <<= 1;
            }

            _mask = every - 1;
            _reservoir = new long[reservoir < 1 ? 1 : reservoir];
        }

        /// <summary>Samples taken since the last read.</summary>
        public long Samples => Interlocked.Read(ref _samples);

        /// <summary>
        /// Whether this call should be timed. One interlocked increment and a mask: cheap enough to
        /// sit in front of every traced call.
        /// </summary>
        public bool ShouldSample() => (Interlocked.Increment(ref _counter) & _mask) == 0;

        /// <summary>Records one measured duration, in stopwatch ticks.</summary>
        public void Add(long ticks)
        {
            if (ticks < 0)
            {
                return;
            }

            lock (_gate)
            {
                _sum += ticks;
                _samples++;

                if (ticks > _max)
                {
                    _max = ticks;
                }

                _reservoir[_next] = ticks;
                _next = (_next + 1) % _reservoir.Length;
                if (_filled < _reservoir.Length)
                {
                    _filled++;
                }
            }
        }

        /// <summary>
        /// Reads the mean and 95th-percentile prefix cost in nanoseconds, then starts a new
        /// interval. Returns zeroes when nothing was sampled.
        /// </summary>
        public void Read(out double averageNanoseconds, out double p95Nanoseconds)
        {
            long sum;
            long samples;
            long[] window;

            lock (_gate)
            {
                sum = _sum;
                samples = _samples;
                window = new long[_filled];
                Array.Copy(_reservoir, window, _filled);

                _sum = 0;
                _samples = 0;
                _max = 0;
                _filled = 0;
                _next = 0;
            }

            if (samples == 0)
            {
                averageNanoseconds = 0d;
                p95Nanoseconds = 0d;
                return;
            }

            var nanosecondsPerTick = 1_000_000_000d / Stopwatch.Frequency;
            averageNanoseconds = sum / (double)samples * nanosecondsPerTick;

            if (window.Length == 0)
            {
                p95Nanoseconds = averageNanoseconds;
                return;
            }

            Array.Sort(window);
            var rank = (int)Math.Ceiling(0.95d * window.Length) - 1;
            if (rank < 0)
            {
                rank = 0;
            }

            p95Nanoseconds = window[rank] * nanosecondsPerTick;
        }
    }
}
