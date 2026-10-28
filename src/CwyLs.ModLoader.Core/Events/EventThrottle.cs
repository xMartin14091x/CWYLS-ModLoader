using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// A minimum interval per key. The game raises some of the sources this bus listens to every
    /// frame - the pomodoro timer's update observable, the save manager's burst of ~27 writes when
    /// it saves everything - and a mod wants the transition, not 60 copies of it per second.
    /// </summary>
    /// <remarks>
    /// Keys are independent: throttling <c>Work</c> never delays <c>Break</c>. The first call for a
    /// key always passes, so a rare event is never swallowed by the window of an unrelated one.
    /// </remarks>
    public sealed class EventThrottle
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, long> _lastPassMs = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Func<long> _clockMs;

        private long _passed;
        private long _suppressed;

        /// <summary>Creates a throttle.</summary>
        /// <param name="minIntervalMs">
        /// Milliseconds a key must stay quiet before it passes again. Zero means "never throttle",
        /// which is a legitimate configuration (a user turning the limit off).
        /// </param>
        /// <param name="clockMs">
        /// Monotonic millisecond clock. Defaults to a <see cref="Stopwatch"/> started here; tests
        /// pass their own so they never depend on wall time.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="minIntervalMs"/> is negative.</exception>
        public EventThrottle(long minIntervalMs, Func<long>? clockMs = null)
        {
            if (minIntervalMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minIntervalMs), minIntervalMs, "a minimum interval cannot be negative");
            }

            MinIntervalMs = minIntervalMs;

            if (clockMs != null)
            {
                _clockMs = clockMs;
            }
            else
            {
                var watch = Stopwatch.StartNew();
                _clockMs = () => watch.ElapsedMilliseconds;
            }
        }

        /// <summary>Milliseconds a key must stay quiet before it passes again.</summary>
        public long MinIntervalMs { get; }

        /// <summary>How many calls have been allowed through.</summary>
        public long Passed
        {
            get
            {
                lock (_gate)
                {
                    return _passed;
                }
            }
        }

        /// <summary>How many calls have been dropped.</summary>
        public long Suppressed
        {
            get
            {
                lock (_gate)
                {
                    return _suppressed;
                }
            }
        }

        /// <summary>How many distinct keys the throttle is currently tracking.</summary>
        public int KeyCount
        {
            get
            {
                lock (_gate)
                {
                    return _lastPassMs.Count;
                }
            }
        }

        /// <summary>
        /// Decides whether this call for <paramref name="key"/> may proceed. A passing call resets
        /// the key's window; a dropped call does not extend it, so a key raised continuously passes
        /// on a steady <see cref="MinIntervalMs"/> cadence rather than never passing again.
        /// </summary>
        /// <param name="key">What is being throttled, e.g. the pomodoro type or the save section.</param>
        /// <returns>True to proceed, false to drop.</returns>
        /// <exception cref="ArgumentException"><paramref name="key"/> is null or blank.</exception>
        public bool TryPass(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("must not be null or blank", nameof(key));
            }

            lock (_gate)
            {
                if (MinIntervalMs == 0)
                {
                    _passed++;
                    return true;
                }

                var now = _clockMs();
                if (_lastPassMs.TryGetValue(key, out var last) && now - last < MinIntervalMs)
                {
                    _suppressed++;
                    return false;
                }

                _lastPassMs[key] = now;
                _passed++;
                return true;
            }
        }

        /// <summary>Forgets every key and zeroes the counters.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _lastPassMs.Clear();
                _passed = 0;
                _suppressed = 0;
            }
        }
    }
}
