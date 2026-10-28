using System;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>
    /// When the API's main-thread ticker rebuilds the save-backed part of <c>/v1/state</c>: soon
    /// after a change (<see cref="MinIntervalSeconds"/>), and at least every
    /// <see cref="MaxAgeSeconds"/>. A rebuild that costs more than the per-frame budget doubles
    /// the interval (up to <see cref="MaxIntervalSeconds"/>); cheap ones halve it back. Pure.
    /// </summary>
    public sealed class ApiRefreshPolicy
    {
        /// <summary>Creates the policy.</summary>
        /// <param name="minIntervalSeconds">Shortest gap between rebuilds.</param>
        /// <param name="maxAgeSeconds">Rebuild at least this often even without a change.</param>
        /// <param name="budgetMs">Per-frame budget of one rebuild (<see cref="ApiDefaults.FrameBudgetMs"/>).</param>
        /// <param name="maxIntervalSeconds">Longest gap the back-off reaches.</param>
        public ApiRefreshPolicy(
            double minIntervalSeconds = 0.25,
            double maxAgeSeconds = 5,
            double budgetMs = ApiDefaults.FrameBudgetMs,
            double maxIntervalSeconds = 8)
        {
            MinIntervalSeconds = minIntervalSeconds;
            MaxAgeSeconds = maxAgeSeconds;
            BudgetMs = budgetMs;
            MaxIntervalSeconds = Math.Max(minIntervalSeconds, maxIntervalSeconds);
            IntervalSeconds = minIntervalSeconds;
        }

        /// <summary>Shortest gap between rebuilds.</summary>
        public double MinIntervalSeconds { get; }

        /// <summary>Rebuild at least this often.</summary>
        public double MaxAgeSeconds { get; }

        /// <summary>Per-frame budget of one rebuild, in milliseconds.</summary>
        public double BudgetMs { get; }

        /// <summary>Longest gap the back-off reaches.</summary>
        public double MaxIntervalSeconds { get; }

        /// <summary>The current gap after a change.</summary>
        public double IntervalSeconds { get; private set; }

        /// <summary>Rebuilds that went over the budget.</summary>
        public long OverBudget { get; private set; }

        /// <summary>Rebuilds so far.</summary>
        public long Builds { get; private set; }

        /// <summary>The last rebuild's cost, in milliseconds.</summary>
        public double LastCostMs { get; private set; }

        private double _last = double.NegativeInfinity;

        /// <summary>True when a rebuild should run now.</summary>
        /// <param name="now">Monotonic seconds.</param>
        /// <param name="dirty">True when a change arrived since the last rebuild.</param>
        public bool IsDue(double now, bool dirty)
        {
            var since = now - _last;
            return (dirty && since >= IntervalSeconds) || since >= Math.Max(MaxAgeSeconds, IntervalSeconds);
        }

        /// <summary>Records a rebuild and adapts the interval to its cost.</summary>
        /// <param name="now">Monotonic seconds when it ran.</param>
        /// <param name="costMs">What it cost.</param>
        public void Record(double now, double costMs)
        {
            _last = now;
            Builds++;
            LastCostMs = costMs;
            if (costMs > BudgetMs)
            {
                OverBudget++;
                IntervalSeconds = Math.Min(MaxIntervalSeconds, IntervalSeconds * 2);
            }
            else if (costMs <= BudgetMs / 2)
            {
                IntervalSeconds = Math.Max(MinIntervalSeconds, IntervalSeconds / 2);
            }
        }
    }

    /// <summary>
    /// The latest <c>/v1/state</c> document, published on the main thread and read by request
    /// threads without touching the game ("reads come from snapshots"). Each
    /// published version carries the frame and time it was built, and every response adds its age,
    /// so a client can tell a stale document (after a sleep, a stalled loop) from a fresh one.
    /// Thread-safe.
    /// </summary>
    public sealed class ApiStateStore
    {
        private readonly IApiClock _clock;
        private volatile Published _current;

        /// <summary>Creates a store holding the "not built yet" document.</summary>
        /// <param name="clock">The clock.</param>
        public ApiStateStore(IApiClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _current = new Published(0, null, 0, 0, "\"ready\":false");
        }

        /// <summary>The published version (0 before the first publish).</summary>
        public long Version => _current.Version;

        /// <summary>Publishes a new document. Call on the main thread.</summary>
        /// <param name="members">The members after the header (<c>ready</c>, <c>live</c>, the sections).</param>
        /// <param name="frame">The frame it was built on.</param>
        public void Publish(JsonObject members, int frame)
        {
            if (members is null)
            {
                throw new ArgumentNullException(nameof(members));
            }

            var json = ApiJson.Write(members);
            var inner = json.Length > 2 ? json.Substring(1, json.Length - 2) : string.Empty;
            var previous = _current;
            _current = new Published(previous.Version + 1, _clock.Now, _clock.MonotonicSeconds, frame, inner);
        }

        /// <summary>
        /// The response body of <c>GET /v1/state</c>: <c>{"schema":1,"version":N,"capturedAt":"…",
        /// "ageMs":…,"frame":…, …members}</c>.
        /// </summary>
        public string Render()
        {
            var p = _current;
            var sb = new StringBuilder(p.Inner.Length + 128);
            sb.Append("{\"schema\":").Append(ApiDefaults.StateSchema.ToString(CultureInfo.InvariantCulture))
              .Append(",\"version\":").Append(p.Version.ToString(CultureInfo.InvariantCulture))
              .Append(",\"capturedAt\":").Append(p.CapturedAt is null ? "null" : MiniJson.String(ApiJson.Time(p.CapturedAt.Value)))
              .Append(",\"ageMs\":").Append(p.CapturedAt is null
                  ? "null"
                  : Math.Max(0, (long)Math.Round((_clock.MonotonicSeconds - p.CapturedMono) * 1000)).ToString(CultureInfo.InvariantCulture))
              .Append(",\"frame\":").Append(p.Frame.ToString(CultureInfo.InvariantCulture));
            if (p.Inner.Length > 0)
            {
                sb.Append(',').Append(p.Inner);
            }

            return sb.Append('}').ToString();
        }

        private sealed class Published
        {
            internal Published(long version, DateTimeOffset? capturedAt, double capturedMono, int frame, string inner)
            {
                Version = version;
                CapturedAt = capturedAt;
                CapturedMono = capturedMono;
                Frame = frame;
                Inner = inner;
            }

            internal long Version { get; }

            internal DateTimeOffset? CapturedAt { get; }

            internal double CapturedMono { get; }

            internal int Frame { get; }

            internal string Inner { get; }
        }
    }
}
