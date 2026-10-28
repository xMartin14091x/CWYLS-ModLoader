using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>What the limiter decided about one call.</summary>
    public enum RateAction
    {
        /// <summary>Write the call as its own line.</summary>
        Write,

        /// <summary>Write one aggregate line standing for the whole window that just closed.</summary>
        Aggregate,

        /// <summary>Write nothing; the call has been counted.</summary>
        Suppress,
    }

    /// <summary>The limiter's answer for one call. A value type: this is the hot path.</summary>
    public readonly struct RateVerdict
    {
        internal RateVerdict(
            RateAction action,
            int suppressedSinceLastPermit,
            int aggregateCount,
            object? aggregatePayload,
            bool remembersPayload,
            bool enteredPolling,
            bool leftPolling,
            int observedPerSecond)
        {
            Action = action;
            SuppressedSinceLastPermit = suppressedSinceLastPermit;
            AggregateCount = aggregateCount;
            AggregatePayload = aggregatePayload;
            RemembersPayload = remembersPayload;
            EnteredPolling = enteredPolling;
            LeftPolling = leftPolling;
            ObservedPerSecond = observedPerSecond;
        }

        /// <summary>What to do with this call.</summary>
        public RateAction Action { get; }

        /// <summary>On the first permit of a new window, how many calls the previous one dropped.</summary>
        public int SuppressedSinceLastPermit { get; }

        /// <summary>For <see cref="RateAction.Aggregate"/>: calls observed in the window just closed.</summary>
        public int AggregateCount { get; }

        /// <summary>For <see cref="RateAction.Aggregate"/>: the last payload remembered in that window.</summary>
        public object? AggregatePayload { get; }

        /// <summary>True while the key is aggregating, so the caller should call <see cref="RateLimiter.Remember"/>.</summary>
        public bool RemembersPayload { get; }

        /// <summary>True exactly once, on the call that puts the key into polling mode.</summary>
        public bool EnteredPolling { get; }

        /// <summary>True exactly once, on the call that takes the key back out of polling mode.</summary>
        public bool LeftPolling { get; }

        /// <summary>Calls seen in the window that triggered a transition, for the log line.</summary>
        public int ObservedPerSecond { get; }
    }

    /// <summary>
    /// A one-second token bucket per method key, with an adaptive downsample for polling methods.
    /// </summary>
    /// <remarks>
    /// The first 90-second run produced 61,444 lines, of which the great majority came from methods
    /// the game calls every frame on purpose — <c>GetCurrentMusicProgress</c>, <c>AudioPlayer.Stop</c>
    /// (883,406 suppressed calls on its own), input and cursor polling. A flat cap pins each of them
    /// at the cap forever, so the file is dominated by noise and the count of what was dropped is
    /// lost. A method that overruns the cap in three consecutive windows is therefore reclassified
    /// as polling and switched to one aggregate line per window carrying the exact call count and
    /// the last arguments seen; five quiet windows put it back. Nothing is lost, only merged.
    /// </remarks>
    public sealed class RateLimiter
    {
        /// <summary>Consecutive over-cap windows before a key is treated as polling.</summary>
        public const int WindowsToEnterPolling = 3;

        /// <summary>Consecutive under-cap windows before a polling key goes back to normal.</summary>
        public const int WindowsToLeavePolling = 5;

        /// <summary>Default aggregate lines per second for a polling key.</summary>
        public const int DefaultPollingCapPerSecond = 1;

        private const long NormalWindowMs = 1000L;

        private sealed class Bucket
        {
            public long WindowStartMs;
            public int Used;
            public int Suppressed;

            public bool Polling;
            public int HotWindows;
            public int QuietWindows;

            public object? Payload;
            public int ClosedCount;
            public object? ClosedPayload;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        private readonly SortedSet<string> _polling = new SortedSet<string>(StringComparer.Ordinal);
        private readonly int _permitsPerSecond;
        private readonly long _pollingWindowMs;
        private readonly Func<long> _nowMs;

        /// <summary>Creates a limiter.</summary>
        /// <param name="permitsPerSecond">Lines written per key per second. 0 or less means no limit at all.</param>
        /// <param name="nowMilliseconds">Monotonic clock in milliseconds. Defaults to <see cref="Environment.TickCount"/>.</param>
        /// <param name="pollingCapPerSecond">Aggregate lines per second once a key is classified as polling.</param>
        public RateLimiter(
            int permitsPerSecond,
            Func<long>? nowMilliseconds = null,
            int pollingCapPerSecond = DefaultPollingCapPerSecond)
        {
            _permitsPerSecond = permitsPerSecond;
            _nowMs = nowMilliseconds ?? (() => Environment.TickCount);
            _pollingWindowMs = pollingCapPerSecond < 1 ? NormalWindowMs : NormalWindowMs / pollingCapPerSecond;
            if (_pollingWindowMs < 1)
            {
                _pollingWindowMs = 1;
            }
        }

        /// <summary>Whether this limiter ever refuses a call.</summary>
        public bool IsUnlimited => _permitsPerSecond <= 0;

        /// <summary>Number of keys currently tracked.</summary>
        public int TrackedKeys
        {
            get
            {
                lock (_gate)
                {
                    return _buckets.Count;
                }
            }
        }

        /// <summary>Keys currently in polling mode, sorted. Written into the footer.</summary>
        public IReadOnlyList<string> PollingKeys
        {
            get
            {
                lock (_gate)
                {
                    return new List<string>(_polling);
                }
            }
        }

        /// <summary>True when the key is currently being aggregated.</summary>
        public bool IsPolling(string key)
        {
            lock (_gate)
            {
                return _buckets.TryGetValue(key, out var bucket) && bucket.Polling;
            }
        }

        /// <summary>Calls dropped so far and not yet reported, across every key.</summary>
        public long PendingSuppressed
        {
            get
            {
                lock (_gate)
                {
                    long total = 0;
                    foreach (var bucket in _buckets.Values)
                    {
                        total += bucket.Suppressed;
                    }

                    return total;
                }
            }
        }

        /// <summary>
        /// Stores the payload (normally the formatted arguments) of the call just observed, so the
        /// next aggregate line can carry the last values seen. Only meaningful while the key is
        /// polling; the caller knows that from <see cref="RateVerdict.RemembersPayload"/>.
        /// </summary>
        public void Remember(string key, object? payload)
        {
            if (key is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_buckets.TryGetValue(key, out var bucket) && bucket.Polling)
                {
                    bucket.Payload = payload;
                }
            }
        }

        /// <summary>
        /// The old boolean form, kept for callers that only need a flat cap. Never classifies a key
        /// as polling.
        /// </summary>
        public bool TryAcquire(string key, out int suppressedSinceLastPermit)
        {
            var verdict = Observe(key, exempt: true);
            suppressedSinceLastPermit = verdict.SuppressedSinceLastPermit;
            return verdict.Action == RateAction.Write;
        }

        /// <summary>Takes one permit for <paramref name="key"/> and says what to do with the call.</summary>
        /// <param name="key">Usually <c>Class.Method</c>.</param>
        /// <param name="exempt">
        /// True for curated events: they keep the flat cap and are never downsampled, because their
        /// whole purpose is to be visible one call at a time.
        /// </param>
        public RateVerdict Observe(string key, bool exempt = false)
        {
            if (IsUnlimited)
            {
                return new RateVerdict(RateAction.Write, 0, 0, null, false, false, false, 0);
            }

            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var now = _nowMs();

            lock (_gate)
            {
                if (!_buckets.TryGetValue(key, out var bucket))
                {
                    _buckets[key] = new Bucket { WindowStartMs = now, Used = 1 };
                    return new RateVerdict(RateAction.Write, 0, 0, null, false, false, false, 0);
                }

                return bucket.Polling
                    ? ObservePolling(key, bucket, now)
                    : ObserveNormal(key, bucket, now, exempt);
            }
        }

        private RateVerdict ObserveNormal(string key, Bucket bucket, long now, bool exempt)
        {
            if (now - bucket.WindowStartMs < NormalWindowMs)
            {
                bucket.Used++;
                if (bucket.Used <= _permitsPerSecond)
                {
                    return new RateVerdict(RateAction.Write, 0, 0, null, false, false, false, 0);
                }

                bucket.Suppressed++;
                return new RateVerdict(RateAction.Suppress, 0, 0, null, false, false, false, 0);
            }

            // The window closed. Decide whether this key is a poller before starting the next one.
            var observed = bucket.Used;
            var suppressed = bucket.Suppressed;
            bucket.HotWindows = !exempt && observed > _permitsPerSecond ? bucket.HotWindows + 1 : 0;

            bucket.WindowStartMs = now;
            bucket.Used = 1;
            bucket.Suppressed = 0;

            if (bucket.HotWindows >= WindowsToEnterPolling)
            {
                bucket.Polling = true;
                bucket.HotWindows = 0;
                bucket.QuietWindows = 0;
                bucket.Payload = null;
                bucket.ClosedCount = 0;
                bucket.ClosedPayload = null;
                _polling.Add(key);

                return new RateVerdict(RateAction.Write, suppressed, 0, null, true, true, false, observed);
            }

            return new RateVerdict(RateAction.Write, suppressed, 0, null, false, false, false, 0);
        }

        private RateVerdict ObservePolling(string key, Bucket bucket, long now)
        {
            if (now - bucket.WindowStartMs < _pollingWindowMs)
            {
                bucket.Used++;
                return new RateVerdict(RateAction.Suppress, 0, 0, null, true, false, false, 0);
            }

            // Window closed: this call emits the aggregate for it, then opens the next one.
            bucket.ClosedCount = bucket.Used;
            bucket.ClosedPayload = bucket.Payload;
            bucket.QuietWindows = bucket.Used <= _permitsPerSecond ? bucket.QuietWindows + 1 : 0;

            bucket.WindowStartMs = now;
            bucket.Used = 1;

            if (bucket.QuietWindows >= WindowsToLeavePolling)
            {
                bucket.Polling = false;
                bucket.QuietWindows = 0;
                bucket.HotWindows = 0;
                bucket.Payload = null;
                _polling.Remove(key);

                return new RateVerdict(
                    RateAction.Aggregate, 0, bucket.ClosedCount, bucket.ClosedPayload, false, false, true, bucket.ClosedCount);
            }

            return new RateVerdict(
                RateAction.Aggregate, 0, bucket.ClosedCount, bucket.ClosedPayload, true, false, false, 0);
        }
    }
}
