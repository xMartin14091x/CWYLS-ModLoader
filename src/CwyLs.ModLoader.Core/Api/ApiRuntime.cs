using System;
using System.Diagnostics;
using System.Globalization;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>What one API session is built from. The plugin fills it from config and the game-side backends.</summary>
    public sealed class ApiRuntimeOptions
    {
        /// <summary>The port.</summary>
        public int Port { get; set; } = ApiDefaults.DefaultPort;

        /// <summary>The clock.</summary>
        public IApiClock Clock { get; set; } = SystemApiClock.Instance;

        /// <summary>The action registry.</summary>
        public ActionRegistry Registry { get; set; } = ActionRegistry.Shared;

        /// <summary>The runner and confirmation slots.</summary>
        public ActionServices Services { get; set; } = ActionServices.Shared;

        /// <summary><c>[Api] AllowActions</c>, read per request.</summary>
        public Func<bool> AllowActions { get; set; } = () => ApiSwitches.AllowActions;

        /// <summary><c>[Api] ConsoleAuditReads</c>, read per request.</summary>
        public Func<bool> ConsoleAuditReads { get; set; } = () => false;

        /// <summary>The facts for <c>/v1/info</c>.</summary>
        public ApiInfoFacts Facts { get; set; } = new ApiInfoFacts();

        /// <summary>
        /// Builds the data-bus snapshot on the main thread; it may return null (the data plugin is not
        /// installed or not registered yet), and a null delegate means the same.
        /// </summary>
        public Func<GameDataSnapshot?>? GameData { get; set; }

        /// <summary>The display language code; null when unknown.</summary>
        public Func<string?>? Language { get; set; }

        /// <summary>Events kept.</summary>
        public int EventCapacity { get; set; } = ApiDefaults.EventCapacity;

        /// <summary>The refresh policy; null means the defaults.</summary>
        public ApiRefreshPolicy? Refresh { get; set; }

        /// <summary>Where diagnostics go (file-only bus records when null).</summary>
        public Action<string>? Diagnostics { get; set; }

        /// <summary>The audit log; null builds one with <see cref="ConsoleAuditReads"/>.</summary>
        public ApiAuditLog? Audit { get; set; }
    }

    /// <summary>
    /// One API session from <c>api on</c> to <c>api off</c>: the router, the event ring, the
    /// live state and the state document, plus the main-thread intake the plugin wires to the game
    /// (events in, a once-per-frame <see cref="Tick"/>). Unity-free so every part of it is tested;
    /// the plugin owns the listener, the key file, the subscriptions and the ticker.
    /// </summary>
    public sealed class ApiRuntime
    {
        /// <summary>How often the live part is republished without a change, in seconds (keeps <c>ageMs</c> honest).</summary>
        public const double RepublishSeconds = 1;

        private readonly ApiRuntimeOptions _o;
        private readonly ApiRefreshPolicy _policy;
        private readonly Action<string> _diagnostics;
        private JsonObject? _data;
        private bool _dataAvailable;
        private bool _dataDirty = true;
        private bool _liveDirty = true;
        private double _lastPublish = double.NegativeInfinity;
        private long _sourceFailures;

        /// <summary>Creates a session (nothing listens yet).</summary>
        /// <param name="key">The access key.</param>
        /// <param name="options">The parts.</param>
        public ApiRuntime(ApiToken key, ApiRuntimeOptions options)
        {
            _o = options ?? throw new ArgumentNullException(nameof(options));
            _policy = _o.Refresh ?? new ApiRefreshPolicy();
            _diagnostics = _o.Diagnostics ?? (message => ConsoleEvents.File(ApiAuditLog.Sys, "diagnostic", ("message", message)));
            Events = new ApiEventRing(_o.EventCapacity, () => _o.Clock.Now);
            State = new ApiStateStore(_o.Clock);
            Router = new ApiRouter(key, new ApiRouterOptions
            {
                Port = _o.Port,
                Clock = _o.Clock,
                Registry = _o.Registry,
                Services = _o.Services,
                AllowActions = _o.AllowActions,
                State = State,
                Events = Events,
                Audit = _o.Audit ?? new ApiAuditLog(_o.Clock, _o.ConsoleAuditReads),
                Facts = _o.Facts,
                Diagnostics = _diagnostics,
            });
        }

        /// <summary>The request pipeline (hand <see cref="ApiRouter.Handle"/> to a transport).</summary>
        public ApiRouter Router { get; }

        /// <summary>The event stream.</summary>
        public ApiEventRing Events { get; }

        /// <summary>The state document.</summary>
        public ApiStateStore State { get; }

        /// <summary>The live part of the state.</summary>
        public ApiLiveState Live { get; } = new ApiLiveState();

        /// <summary>The refresh policy (its counters feed <c>api status</c>).</summary>
        public ApiRefreshPolicy Refresh => _policy;

        /// <summary>Data snapshot builds that threw.</summary>
        public long SourceFailures => _sourceFailures;

        /// <summary>Takes one framework event (main thread): into the ring if it is published, into the live state always.</summary>
        /// <param name="name">The channel name.</param>
        /// <param name="args">The event.</param>
        public void OnEvent(string? name, ICwyEvent? args)
        {
            if (ApiEventMapper.TryMap(name, args, out var type, out var data))
            {
                Events.Append(type, data);
            }

            if (Live.Observe(args, _o.Clock.Now))
            {
                _liveDirty = true;
            }
        }

        /// <summary>Takes one data-bus change (main thread): an event, and the save-backed part is rebuilt soon.</summary>
        /// <param name="change">The change.</param>
        public void OnDataChanged(GameDataChanged? change)
        {
            if (change is null)
            {
                return;
            }

            Events.Append(ApiEventMapper.DataChanged, ApiEventMapper.FromDataChanged(change));
            _dataDirty = true;
        }

        /// <summary>
        /// Once a frame on the main thread: rebuilds the save-backed part when the refresh policy
        /// says so (timed, adapting to its cost against the 4 ms budget), and republishes the
        /// document when anything changed or a second has passed.
        /// </summary>
        /// <param name="frame">The frame number, recorded in the document.</param>
        /// <returns>True when a new document was published.</returns>
        public bool Tick(int frame)
        {
            var now = _o.Clock.MonotonicSeconds;
            var rebuilt = false;
            if (_policy.IsDue(now, _dataDirty))
            {
                var watch = Stopwatch.StartNew();
                _data = BuildData();
                watch.Stop();
                _policy.Record(now, watch.Elapsed.TotalMilliseconds);
                if (watch.Elapsed.TotalMilliseconds > _policy.BudgetMs)
                {
                    ConsoleEvents.File(
                        ApiAuditLog.Sys,
                        "stateOverBudget",
                        ("ms", Math.Round(watch.Elapsed.TotalMilliseconds, 2)),
                        ("budgetMs", _policy.BudgetMs),
                        ("intervalSec", _policy.IntervalSeconds));
                }

                _dataDirty = false;
                rebuilt = true;
            }

            if (!rebuilt && !_liveDirty && now - _lastPublish < RepublishSeconds)
            {
                return false;
            }

            Publish(frame, now);
            return true;
        }

        /// <summary>Two lines for <c>api status</c>: events, state and refresh numbers.</summary>
        public string Describe() =>
            "events: epoch " + Events.Epoch + ", latest " + Events.Latest.ToString(CultureInfo.InvariantCulture)
            + ", dropped " + Events.DroppedTotal.ToString(CultureInfo.InvariantCulture)
            + "; state: version " + State.Version.ToString(CultureInfo.InvariantCulture)
            + ", rebuilds " + _policy.Builds.ToString(CultureInfo.InvariantCulture)
            + " (last " + _policy.LastCostMs.ToString("0.##", CultureInfo.InvariantCulture) + " ms, over budget "
            + _policy.OverBudget.ToString(CultureInfo.InvariantCulture) + ", interval "
            + _policy.IntervalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s"
            + (_sourceFailures > 0 ? ", failures " + _sourceFailures.ToString(CultureInfo.InvariantCulture) : string.Empty) + ")";

        private JsonObject? BuildData()
        {
            var source = _o.GameData;
            if (source is null)
            {
                _dataAvailable = false;
                return null;
            }

            try
            {
                var snapshot = source();
                _dataAvailable = snapshot != null;
                return snapshot is null ? null : ApiStateProjection.FromSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                _sourceFailures++;
                if (_sourceFailures == 1 || _sourceFailures % 100 == 0)
                {
                    _diagnostics("state: the data-bus snapshot failed (" + _sourceFailures + " so far): " + ex.GetType().Name + ": " + ex.Message);
                }

                return _data;
            }
        }

        private void Publish(int frame, double now)
        {
            string? language = null;
            try
            {
                language = _o.Language?.Invoke();
            }
            catch (Exception ex)
            {
                _diagnostics("state: reading the display language failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            var members = new JsonObject
            {
                ["ready"] = _data != null && _data.TryGetValue("ready", out var ready) && ready is bool b && b,
                ["dataAvailable"] = _dataAvailable,
                ["live"] = Live.ToJson(language),
            };
            if (_data != null)
            {
                foreach (var pair in _data)
                {
                    if (pair.Key != "ready")
                    {
                        members[pair.Key] = pair.Value;
                    }
                }
            }

            State.Publish(members, frame);
            _liveDirty = false;
            _lastPublish = now;
        }
    }
}
