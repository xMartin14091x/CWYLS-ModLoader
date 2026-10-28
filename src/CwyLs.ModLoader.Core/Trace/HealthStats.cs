using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Accumulates per-frame times over one snapshot interval and reduces them to the numbers a
    /// person reads: average/min/max frame rate, average/p95/max frame time, and how many frames
    /// stuttered. Pure and unit-tested, so the arithmetic is not buried in a Unity Update loop.
    /// </summary>
    public sealed class HealthStats
    {
        /// <summary>A frame longer than this is counted as a spike.</summary>
        public const double SpikeMs = 100d;

        /// <summary>A frame longer than this gets its own immediate health event.</summary>
        public const double BigSpikeMs = 250d;

        /// <summary>Samples kept per interval. 4096 frames is over a minute at 60 fps.</summary>
        private const int MaxSamples = 4096;

        private readonly object _gate = new object();
        private readonly List<double> _samples = new List<double>(512);

        private double _sum;
        private double _min = double.MaxValue;
        private double _max;
        private int _spikes;
        private int _count;

        /// <summary>Records one frame time in milliseconds. Non-finite and negative values are ignored.</summary>
        public void AddFrame(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0d)
            {
                return;
            }

            lock (_gate)
            {
                _count++;
                _sum += milliseconds;

                if (milliseconds < _min)
                {
                    _min = milliseconds;
                }

                if (milliseconds > _max)
                {
                    _max = milliseconds;
                }

                if (milliseconds > SpikeMs)
                {
                    _spikes++;
                }

                // Past the cap the percentile is computed from the frames we did keep; the average,
                // extremes and spike count stay exact, which is what a reader acts on.
                if (_samples.Count < MaxSamples)
                {
                    _samples.Add(milliseconds);
                }
            }
        }

        /// <summary>Frames recorded since the last reset.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        /// <summary>Mean frame time in milliseconds, or 0 with no samples.</summary>
        public double AverageMs
        {
            get
            {
                lock (_gate)
                {
                    return _count == 0 ? 0d : _sum / _count;
                }
            }
        }

        /// <summary>Shortest frame in milliseconds, or 0 with no samples.</summary>
        public double MinMs
        {
            get
            {
                lock (_gate)
                {
                    return _count == 0 ? 0d : _min;
                }
            }
        }

        /// <summary>Longest frame in milliseconds, or 0 with no samples.</summary>
        public double MaxMs
        {
            get
            {
                lock (_gate)
                {
                    return _max;
                }
            }
        }

        /// <summary>Frames longer than <see cref="SpikeMs"/>.</summary>
        public int Spikes
        {
            get
            {
                lock (_gate)
                {
                    return _spikes;
                }
            }
        }

        /// <summary>
        /// 95th-percentile frame time in milliseconds: the number that tells you whether the
        /// occasional frame hurts, which an average never does.
        /// </summary>
        public double P95Ms
        {
            get
            {
                lock (_gate)
                {
                    if (_samples.Count == 0)
                    {
                        return 0d;
                    }

                    var sorted = _samples.ToArray();
                    Array.Sort(sorted);

                    // Nearest-rank: the smallest value at or above 95% of the samples.
                    var rank = (int)Math.Ceiling(0.95d * sorted.Length) - 1;
                    if (rank < 0)
                    {
                        rank = 0;
                    }

                    return sorted[rank];
                }
            }
        }

        /// <summary>Mean frames per second over the interval.</summary>
        public double AverageFps => Fps(AverageMs);

        /// <summary>Lowest frames per second, i.e. the longest frame.</summary>
        public double MinFps => Fps(MaxMs);

        /// <summary>Highest frames per second, i.e. the shortest frame.</summary>
        public double MaxFps => Fps(MinMs);

        private static double Fps(double milliseconds) => milliseconds <= 0d ? 0d : 1000d / milliseconds;

        /// <summary>Clears every sample, ready for the next interval.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _samples.Clear();
                _sum = 0d;
                _min = double.MaxValue;
                _max = 0d;
                _spikes = 0;
                _count = 0;
            }
        }
    }
}
