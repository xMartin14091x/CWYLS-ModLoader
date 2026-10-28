using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Collapses identical log messages inside a time window. A Unity exception thrown from Update
    /// arrives sixty times a second; writing it sixty times buries everything else and tells the
    /// reader nothing the count would not.
    /// </summary>
    public sealed class LogDedupe
    {
        /// <summary>Default window in which repeats are merged.</summary>
        public const double DefaultWindowSeconds = 5d;

        /// <summary>Keys tracked before the oldest are forgotten, so a unique-message storm cannot grow without bound.</summary>
        private const int MaxKeys = 512;

        private sealed class Entry
        {
            public long LastEmittedMs;
            public int Repeats;
            public long LastSeenMs;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly long _windowMs;
        private readonly Func<long> _nowMs;

        /// <summary>Creates a deduper.</summary>
        /// <param name="windowSeconds">Seconds during which repeats are merged.</param>
        /// <param name="nowMilliseconds">Monotonic clock. Defaults to <see cref="Environment.TickCount"/>.</param>
        public LogDedupe(double windowSeconds = DefaultWindowSeconds, Func<long>? nowMilliseconds = null)
        {
            _windowMs = (long)(windowSeconds * 1000d);
            if (_windowMs < 1)
            {
                _windowMs = 1;
            }

            _nowMs = nowMilliseconds ?? (() => Environment.TickCount);
        }

        /// <summary>Distinct messages currently tracked.</summary>
        public int TrackedKeys
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>
        /// Whether this occurrence should be written.
        /// </summary>
        /// <param name="key">Usually message plus stack.</param>
        /// <param name="count">
        /// How many occurrences the line stands for: 1 the first time, and on a later emit the
        /// number that piled up while the window was closed, this one included.
        /// </param>
        public bool ShouldEmit(string key, out int count)
        {
            count = 1;

            if (key is null)
            {
                return true;
            }

            var now = _nowMs();

            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    entry.LastSeenMs = now;

                    if (now - entry.LastEmittedMs < _windowMs)
                    {
                        entry.Repeats++;
                        return false;
                    }

                    count = entry.Repeats + 1;
                    entry.Repeats = 0;
                    entry.LastEmittedMs = now;
                    return true;
                }

                if (_entries.Count >= MaxKeys)
                {
                    ForgetOldestLocked();
                }

                _entries[key] = new Entry { LastEmittedMs = now, LastSeenMs = now, Repeats = 0 };
                return true;
            }
        }

        private void ForgetOldestLocked()
        {
            string? oldestKey = null;
            var oldestSeen = long.MaxValue;

            foreach (var pair in _entries)
            {
                if (pair.Value.LastSeenMs < oldestSeen)
                {
                    oldestSeen = pair.Value.LastSeenMs;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey != null)
            {
                _entries.Remove(oldestKey);
            }
        }
    }
}
