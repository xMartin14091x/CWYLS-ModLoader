using System;
using System.Threading;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Wall-clock time for trace lines, read from a cheap monotonic counter and put back on the
    /// system clock whenever the two part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trace stamps hundreds of lines a second, so the game thread only reads a counter
    /// (<c>Stopwatch.GetTimestamp</c>) and the writer thread turns that reading into a time from
    /// one pairing of counter and system time, the anchor. On macOS the counter stops while the
    /// machine sleeps: in one test trace game-method lines ran about 24 minutes
    /// behind the bus lines after a sleep. The counter also cannot see the system clock being set
    /// (by the user or NTP), nor a time-zone or daylight-saving change.
    /// </para>
    /// <para>
    /// <see cref="Check"/> compares the counter's idea of "now" with the system clock. When they
    /// differ by more than <see cref="Threshold"/>, or the UTC offset changed, it records a new
    /// anchor and returns a <see cref="TraceClockJump"/> for the caller to log. A reading keeps
    /// converting with the anchor that was current when it was taken (the newest
    /// <see cref="AnchorHistory"/> anchors are kept), so a line queued before a sleep keeps its time
    /// even when it is written after the re-anchor. A reading taken after the machine woke but
    /// before the next check still uses the old anchor; <see cref="Poll"/> keeps that window to
    /// <see cref="PollInterval"/>.
    /// </para>
    /// <para>
    /// Pure: the counter, its frequency and the system clock are injected, so tests drive all
    /// three. Thread-safe: conversions read an immutable anchor list without a lock; checks take
    /// a lock, so two threads that notice the same jump re-anchor once.
    /// </para>
    /// </remarks>
    public sealed class TraceClock
    {
        /// <summary>Difference between the counter and the system clock above which the clock re-anchors.</summary>
        public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(250);

        /// <summary>How often <see cref="Poll"/> measures, in counter time.</summary>
        public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

        /// <summary>Anchors kept for readings taken before the latest re-anchor.</summary>
        public const int AnchorHistory = 8;

        private readonly Func<long> _counter;
        private readonly Func<DateTimeOffset> _systemNow;
        private readonly double _timeSpanTicksPerCount;
        private readonly long _pollCounts;
        private readonly object _gate = new object();

        /// <summary>Newest first. Replaced whole under <see cref="_gate"/>, never changed in place.</summary>
        private Anchor[] _anchors;

        private long _lastCheck;
        private int _reanchors;

        /// <summary>Creates a clock anchored at the current counter reading and system time.</summary>
        /// <param name="counter">A monotonic counter, e.g. <c>Stopwatch.GetTimestamp</c>.</param>
        /// <param name="frequency">Counter units per second, e.g. <c>Stopwatch.Frequency</c>.</param>
        /// <param name="systemNow">The system clock, e.g. <c>() =&gt; DateTimeOffset.Now</c>.</param>
        /// <param name="threshold">Re-anchor above this difference; null uses <see cref="DefaultThreshold"/>.</param>
        /// <param name="pollInterval">Counter time between <see cref="Poll"/> measurements; null uses <see cref="DefaultPollInterval"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="counter"/> or <paramref name="systemNow"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="frequency"/> is not positive, or <paramref name="threshold"/> or
        /// <paramref name="pollInterval"/> is negative.
        /// </exception>
        public TraceClock(
            Func<long> counter,
            long frequency,
            Func<DateTimeOffset> systemNow,
            TimeSpan? threshold = null,
            TimeSpan? pollInterval = null)
        {
            _counter = counter ?? throw new ArgumentNullException(nameof(counter));
            _systemNow = systemNow ?? throw new ArgumentNullException(nameof(systemNow));

            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "must be positive");
            }

            Threshold = threshold ?? DefaultThreshold;
            if (Threshold < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(threshold), Threshold, "must not be negative");
            }

            PollInterval = pollInterval ?? DefaultPollInterval;
            if (PollInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(pollInterval), PollInterval, "must not be negative");
            }

            Frequency = frequency;
            _timeSpanTicksPerCount = TimeSpan.TicksPerSecond / (double)frequency;
            _pollCounts = (long)Math.Round(PollInterval.Ticks * (frequency / (double)TimeSpan.TicksPerSecond));

            var start = _counter();
            _anchors = new[] { new Anchor(start, _systemNow()) };
            _lastCheck = start;
        }

        /// <summary>Counter units per second.</summary>
        public long Frequency { get; }

        /// <summary>Difference between the counter and the system clock above which <see cref="Check"/> re-anchors.</summary>
        public TimeSpan Threshold { get; }

        /// <summary>Counter time between two measurements made by <see cref="Poll"/>.</summary>
        public TimeSpan PollInterval { get; }

        /// <summary>How many times the clock has re-anchored since it was created.</summary>
        public int Reanchors => Volatile.Read(ref _reanchors);

        /// <summary>The current counter reading, to be converted later with <see cref="ToWall"/>.</summary>
        public long Timestamp => _counter();

        /// <summary>Now, from the counter and the current anchor. Never reads the system clock.</summary>
        public DateTimeOffset Now => ToWall(_counter());

        /// <summary>
        /// The wall-clock time of a counter reading, using the newest anchor taken at or before it.
        /// A reading older than every kept anchor uses the oldest one.
        /// </summary>
        /// <param name="timestamp">A reading of the same counter this clock was built with.</param>
        /// <returns>The wall-clock time, in the UTC offset of the anchor used.</returns>
        public DateTimeOffset ToWall(long timestamp) => ToWall(timestamp, Volatile.Read(ref _anchors));

        /// <summary>
        /// Measures the counter against the system clock now and re-anchors when they differ by
        /// more than <see cref="Threshold"/> or the system clock's UTC offset changed.
        /// </summary>
        /// <param name="reason">What prompted the measurement, for the log (e.g. <c>focus</c>).</param>
        /// <returns>The jump that was corrected, or null when the clock was still right.</returns>
        public TraceClockJump? Check(string reason)
        {
            lock (_gate)
            {
                // Both read inside the lock, so anchors are pushed in counter order even when two
                // threads check at once.
                var counter = _counter();
                var system = _systemNow();
                Volatile.Write(ref _lastCheck, counter);

                var anchors = _anchors;
                var traceTime = ToWall(counter, anchors);
                var drift = system - traceTime;
                var offsetChanged = system.Offset != anchors[0].Wall.Offset;

                if (drift.Duration() <= Threshold && !offsetChanged)
                {
                    return null;
                }

                var kept = Math.Min(anchors.Length, AnchorHistory - 1);
                var next = new Anchor[kept + 1];
                next[0] = new Anchor(counter, system);
                Array.Copy(anchors, 0, next, 1, kept);
                Volatile.Write(ref _anchors, next);

                var count = Interlocked.Increment(ref _reanchors);
                return new TraceClockJump(reason, traceTime, system, offsetChanged, count);
            }
        }

        /// <summary>
        /// <see cref="Check"/>, but only when at least <see cref="PollInterval"/> of counter time has
        /// passed since the last measurement. Cheap enough to call from a loop.
        /// </summary>
        /// <param name="reason">What prompted the measurement, for the log.</param>
        /// <returns>The jump that was corrected, or null.</returns>
        public TraceClockJump? Poll(string reason)
        {
            var counter = _counter();
            if (counter - Volatile.Read(ref _lastCheck) < _pollCounts)
            {
                return null;
            }

            return Check(reason);
        }

        private DateTimeOffset ToWall(long timestamp, Anchor[] anchors)
        {
            var anchor = anchors[anchors.Length - 1];
            for (var i = 0; i < anchors.Length; i++)
            {
                if (timestamp >= anchors[i].Counter)
                {
                    anchor = anchors[i];
                    break;
                }
            }

            var ticks = (long)Math.Round((timestamp - anchor.Counter) * _timeSpanTicksPerCount);
            return anchor.Wall.AddTicks(ticks);
        }

        /// <summary>One pairing of a counter reading with the system time it stood for.</summary>
        private readonly struct Anchor
        {
            public Anchor(long counter, DateTimeOffset wall)
            {
                Counter = counter;
                Wall = wall;
            }

            public long Counter { get; }

            public DateTimeOffset Wall { get; }
        }
    }
}
