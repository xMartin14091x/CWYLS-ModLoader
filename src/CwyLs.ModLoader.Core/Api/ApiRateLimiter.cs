using System;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>Which rate limit a route counts against.</summary>
    public enum ApiRateClass
    {
        /// <summary><c>/v1/info</c>, <c>/v1/state</c>, <c>/v1/actions</c> and handle reads.</summary>
        Read = 0,

        /// <summary><c>/v1/events</c>.</summary>
        Events = 1,

        /// <summary><c>POST /v1/actions/&lt;name&gt;</c>.</summary>
        Action = 2,
    }

    /// <summary>
    /// A token bucket: <see cref="Burst"/> requests at once, refilled at <see cref="PerSecond"/>.
    /// Thread-safe.
    /// </summary>
    public sealed class ApiTokenBucket
    {
        private readonly object _gate = new object();
        private readonly IApiClock _clock;
        private double _tokens;
        private double _last;

        /// <summary>Creates a full bucket.</summary>
        /// <param name="perSecond">Refill rate; must be positive.</param>
        /// <param name="burst">Capacity; at least 1.</param>
        /// <param name="clock">The clock.</param>
        /// <exception cref="ArgumentOutOfRangeException">A rate or capacity is not positive.</exception>
        public ApiTokenBucket(double perSecond, int burst, IApiClock clock)
        {
            if (perSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(perSecond), perSecond, "the rate must be positive");
            }

            if (burst < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(burst), burst, "the burst must be at least 1");
            }

            PerSecond = perSecond;
            Burst = burst;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _tokens = burst;
            _last = clock.MonotonicSeconds;
        }

        /// <summary>Refill rate.</summary>
        public double PerSecond { get; }

        /// <summary>Capacity.</summary>
        public int Burst { get; }

        /// <summary>Takes one request's worth if there is one.</summary>
        /// <param name="retryAfterSeconds">When refused: how long until one is available; 0 otherwise.</param>
        /// <returns>True when the request may go ahead.</returns>
        public bool TryTake(out double retryAfterSeconds)
        {
            lock (_gate)
            {
                var now = _clock.MonotonicSeconds;
                _tokens = Math.Min(Burst, _tokens + Math.Max(0, now - _last) * PerSecond);
                _last = now;
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    retryAfterSeconds = 0;
                    return true;
                }

                retryAfterSeconds = (1 - _tokens) / PerSecond;
                return false;
            }
        }
    }

    /// <summary>
    /// The API's rate limits, one bucket per <see cref="ApiRateClass"/>. Only requests that passed
    /// the access-key check count, so an unauthenticated local process cannot use up the
    /// legitimate client's budget. Defaults: reads 20/s (burst 40), events 10/s (burst 20),
    /// actions 4/s (burst 8). Thread-safe.
    /// </summary>
    public sealed class ApiRateLimiter
    {
        /// <summary>Default read rate per second.</summary>
        public const double DefaultReadsPerSecond = 20;

        /// <summary>Default event-poll rate per second.</summary>
        public const double DefaultEventsPerSecond = 10;

        /// <summary>Default action-start rate per second.</summary>
        public const double DefaultActionsPerSecond = 4;

        private readonly ApiTokenBucket _read;
        private readonly ApiTokenBucket _events;
        private readonly ApiTokenBucket _action;

        /// <summary>Creates the limiter with the default rates.</summary>
        /// <param name="clock">The clock.</param>
        public ApiRateLimiter(IApiClock clock)
            : this(clock, DefaultReadsPerSecond, DefaultEventsPerSecond, DefaultActionsPerSecond)
        {
        }

        /// <summary>Creates the limiter with given rates; each burst is twice its rate.</summary>
        /// <param name="clock">The clock.</param>
        /// <param name="readsPerSecond">Read rate.</param>
        /// <param name="eventsPerSecond">Event-poll rate.</param>
        /// <param name="actionsPerSecond">Action-start rate.</param>
        public ApiRateLimiter(IApiClock clock, double readsPerSecond, double eventsPerSecond, double actionsPerSecond)
        {
            _read = new ApiTokenBucket(readsPerSecond, Burst(readsPerSecond), clock);
            _events = new ApiTokenBucket(eventsPerSecond, Burst(eventsPerSecond), clock);
            _action = new ApiTokenBucket(actionsPerSecond, Burst(actionsPerSecond), clock);
        }

        /// <summary>The rate of one class, per second.</summary>
        /// <param name="rateClass">The class.</param>
        public double RateOf(ApiRateClass rateClass) => Bucket(rateClass).PerSecond;

        /// <summary>Takes one request of a class.</summary>
        /// <param name="rateClass">The class.</param>
        /// <param name="retryAfterSeconds">When refused: seconds until the next one is allowed.</param>
        /// <returns>True when allowed.</returns>
        public bool TryTake(ApiRateClass rateClass, out double retryAfterSeconds) =>
            Bucket(rateClass).TryTake(out retryAfterSeconds);

        private ApiTokenBucket Bucket(ApiRateClass rateClass) => rateClass switch
        {
            ApiRateClass.Events => _events,
            ApiRateClass.Action => _action,
            _ => _read,
        };

        private static int Burst(double perSecond) => Math.Max(1, (int)Math.Ceiling(perSecond * 2));
    }
}
