using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>The fixed facts <c>/v1/info</c> reports, set by the plugin.</summary>
    public sealed class ApiInfoFacts
    {
        /// <summary>The framework version, e.g. <c>0.9.0</c>.</summary>
        public string LoaderVersion { get; set; } = string.Empty;

        /// <summary>The mod API version (<c>CwyApi.VersionText</c>).</summary>
        public string ModApiVersion { get; set; } = string.Empty;

        /// <summary>The game version (<c>Application.version</c>).</summary>
        public string GameVersion { get; set; } = string.Empty;
    }

    /// <summary>Everything the router works with. The plugin builds one per start; tests build their own.</summary>
    public sealed class ApiRouterOptions
    {
        /// <summary>The port (decides the only accepted <c>Host</c>).</summary>
        public int Port { get; set; } = ApiDefaults.DefaultPort;

        /// <summary>The clock.</summary>
        public IApiClock Clock { get; set; } = SystemApiClock.Instance;

        /// <summary>The action registry (<see cref="ActionRegistry.Shared"/> in the game).</summary>
        public ActionRegistry Registry { get; set; } = ActionRegistry.Shared;

        /// <summary>The runner and confirmation slots (<see cref="ActionServices.Shared"/> in the game).</summary>
        public ActionServices Services { get; set; } = ActionServices.Shared;

        /// <summary><c>[Api] AllowActions</c>, read per request.</summary>
        public Func<bool> AllowActions { get; set; } = () => ApiSwitches.AllowActions;

        /// <summary>The <c>/v1/state</c> document; created when null.</summary>
        public ApiStateStore? State { get; set; }

        /// <summary>The event stream; created when null.</summary>
        public ApiEventRing? Events { get; set; }

        /// <summary>The audit log; null publishes through the bus with console reads off.</summary>
        public ApiAuditLog? Audit { get; set; }

        /// <summary>The facts for <c>/v1/info</c>.</summary>
        public ApiInfoFacts Facts { get; set; } = new ApiInfoFacts();

        /// <summary>Rate limits; null means the defaults.</summary>
        public ApiRateLimiter? Limiter { get; set; }

        /// <summary>Most simultaneous long-polls.</summary>
        public int MaxConcurrentWaits { get; set; } = ApiDefaults.MaxConcurrentWaits;

        /// <summary>Largest body accepted.</summary>
        public int MaxBodyBytes { get; set; } = ApiDefaults.MaxBodyBytes;

        /// <summary>Longest long-poll, in seconds.</summary>
        public double MaxWaitSeconds { get; set; } = ApiDefaults.MaxWaitSeconds;

        /// <summary>How long an action waits for the main thread.</summary>
        public TimeSpan ActionTimeout { get; set; } = ApiDefaults.ActionMainThreadTimeout;

        /// <summary>Where diagnostics of the router itself go (file-only bus records when null).</summary>
        public Action<string>? Diagnostics { get; set; }
    }

    /// <summary>
    /// The local game API's request pipeline, shared by both transports. Order: stopping →
    /// browser checks (any <c>Origin</c> or <c>Sec-Fetch-*</c> header: 403) → <c>Host</c> must be
    /// exactly <c>127.0.0.1:&lt;port&gt;</c> (421) → access key in constant time (401) → route
    /// (404/405) → rate limit (429) → query and body (400/411/413/415) → handler. Exactly one
    /// audit record per request. Never throws; never touches Unity: reads come from the published
    /// snapshot and the event ring, actions go to the runner (<see cref="ActionServices"/>), which
    /// owns the main-thread hop with the 2 s wall-clock timeout.
    /// </summary>
    public sealed class ApiRouter
    {
        private const string AuthorizationHeader = "Authorization";

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly ApiRouterOptions _o;
        private readonly ApiStateStore _state;
        private readonly ApiEventRing _events;
        private readonly ApiAuditLog _audit;
        private readonly ApiRateLimiter _limiter;
        private readonly ApiActionHandles _handles;
        private readonly ApiIdempotencyCache _idempotency;
        private readonly string _authority;
        private ApiToken _key;
        private int _waiters;
        private int _stopping;

        /// <summary>Creates a router.</summary>
        /// <param name="key">The access key.</param>
        /// <param name="options">The dependencies.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">The port is out of range.</exception>
        public ApiRouter(ApiToken key, ApiRouterOptions options)
        {
            _key = key ?? throw new ArgumentNullException(nameof(key));
            _o = options ?? throw new ArgumentNullException(nameof(options));
            if (!ApiDefaults.IsValidPort(_o.Port))
            {
                throw new ArgumentException("the port must be " + ApiDefaults.MinPort + "-" + ApiDefaults.MaxPort, nameof(options));
            }

            _state = _o.State ?? new ApiStateStore(_o.Clock);
            _events = _o.Events ?? new ApiEventRing(ApiDefaults.EventCapacity);
            _audit = _o.Audit ?? new ApiAuditLog(_o.Clock, () => false);
            _limiter = _o.Limiter ?? new ApiRateLimiter(_o.Clock);
            _handles = new ApiActionHandles(_o.Clock, ApiDefaults.HandleCapacity, ApiDefaults.HandleLifetime, ApiDefaults.MaxActionsInFlight);
            _idempotency = new ApiIdempotencyCache(_o.Clock, ApiDefaults.IdempotencyCapacity, ApiDefaults.IdempotencyLifetime);
            _authority = ApiDefaults.HostAuthority(_o.Port);
            StartedAt = _o.Clock.Now;
            StartedMono = _o.Clock.MonotonicSeconds;
        }

        /// <summary>The port.</summary>
        public int Port => _o.Port;

        /// <summary>The event stream.</summary>
        public ApiEventRing Events => _events;

        /// <summary>The state document.</summary>
        public ApiStateStore State => _state;

        /// <summary>The handles of API-started actions.</summary>
        public ApiActionHandles Handles => _handles;

        /// <summary>Request counters.</summary>
        public ApiCounters Counters { get; } = new ApiCounters();

        /// <summary>When the router was created.</summary>
        public DateTimeOffset StartedAt { get; }

        /// <summary>Monotonic seconds when the router was created.</summary>
        public double StartedMono { get; }

        /// <summary>The transport's name, set by the plugin once the listener is up (<c>/v1/info</c>).</summary>
        public string TransportName { get; set; } = string.Empty;

        /// <summary>Why the TCP fallback is in use, or null.</summary>
        public string? FallbackReason { get; set; }

        /// <summary>The access key's non-secret id.</summary>
        public string KeyId => Volatile.Read(ref _key).Id;

        /// <summary>Long-polls waiting right now (events and handles).</summary>
        public int Waiting => Volatile.Read(ref _waiters);

        /// <summary>True once <see cref="BeginStop"/> was called.</summary>
        public bool IsStopping => Volatile.Read(ref _stopping) == 1;

        /// <summary>Replaces the access key (<c>api rotate</c>); requests with the old one get 401 from now on.</summary>
        /// <param name="key">The new key.</param>
        public void UseKey(ApiToken key) => Volatile.Write(ref _key, key ?? throw new ArgumentNullException(nameof(key)));

        /// <summary>Answers 503 from now on, wakes every long-poll and stops waiting for pending actions.</summary>
        public void BeginStop()
        {
            Volatile.Write(ref _stopping, 1);
            _events.Close();
            _handles.CancelAll();
        }

        /// <summary>Handles one request. Never throws; writes exactly one audit record.</summary>
        /// <param name="request">The request.</param>
        public ApiResponse Handle(ApiRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var started = _o.Clock.MonotonicSeconds;
            var audit = new ApiAuditRecord { Method = request.Method, Path = AuditPath(request.Target), Transport = request.Transport };
            ApiResponse response;
            try
            {
                response = HandleCore(request, audit);
            }
            catch (Exception ex)
            {
                Diagnose("request " + request.Method + " " + audit.Path + " failed: " + ex.GetType().Name + ": " + ex.Message);
                response = ApiResponse.Error(500, ApiErrorCodes.Internal, "the API failed on this request (" + ex.GetType().Name + "); details are in the trace file");
            }

            audit.Status = response.Status;
            audit.Error = response.ErrorCode;
            audit.Ms = (_o.Clock.MonotonicSeconds - started) * 1000;
            Counters.Record(response.Status);
            try
            {
                _audit.Write(audit);
            }
            catch (Exception ex)
            {
                Diagnose("audit write failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return response;
        }

        private ApiResponse HandleCore(ApiRequest r, ApiAuditRecord audit)
        {
            if (IsStopping)
            {
                return ApiResponse.Error(503, ApiErrorCodes.Unavailable, "the API is stopping");
            }

            if (r.Headers.Has("Origin"))
            {
                return ApiResponse.Error(403, ApiErrorCodes.BrowserRefused, "requests carrying an Origin header are refused: browsers may not use this API");
            }

            if (r.Headers.Has("Sec-Fetch-Site") || r.Headers.Has("Sec-Fetch-Mode"))
            {
                return ApiResponse.Error(403, ApiErrorCodes.BrowserRefused, "requests from a browser (Sec-Fetch headers) are refused");
            }

            if (!string.Equals(r.Headers.Get("Host"), _authority, StringComparison.Ordinal))
            {
                return ApiResponse.Error(421, ApiErrorCodes.HostRefused, "the Host header must be exactly " + _authority);
            }

            if (!Volatile.Read(ref _key).Matches(r.Headers.Get(AuthorizationHeader)))
            {
                return ApiResponse.Error(
                    401,
                    ApiErrorCodes.Unauthorized,
                    "missing or wrong access key: send the key from the token file in the " + AuthorizationHeader +
                    " header, scheme " + ApiToken.Scheme + " (re-read the file after api rotate or a restart)",
                    ApiResponse.Header("WWW-Authenticate", ApiToken.Scheme + " realm=\"cwy\""));
            }

            audit.Authenticated = true;
            var client = ClientName(r.Headers.Get(ApiDefaults.ClientHeader));
            audit.Client = client;

            if (!ApiTarget.TryParse(r.Target, out var target, out var targetProblem))
            {
                return ApiResponse.Error(400, ApiErrorCodes.BadRequest, targetProblem);
            }

            var path = target!.Path;
            audit.Path = AuditPath(path);
            var method = r.Method;

            // Route table. A route that exists answers 405 with Allow for any other method.
            if (path == ApiDefaults.PathPrefix + "/info" || path == ApiDefaults.PathPrefix + "/state"
                || path == ApiDefaults.PathPrefix + "/events" || path == ApiDefaults.PathPrefix + "/actions")
            {
                if (method != "GET")
                {
                    return NotAllowed("GET");
                }

                var rateClass = path.EndsWith("/events", StringComparison.Ordinal) ? ApiRateClass.Events : ApiRateClass.Read;
                if (!_limiter.TryTake(rateClass, out var retry))
                {
                    return RateLimited(retry, "too many requests to " + path);
                }

                var bodyProblem = RefuseBodyOnGet(r);
                if (bodyProblem != null)
                {
                    return bodyProblem;
                }

                switch (path.Substring(ApiDefaults.PathPrefix.Length))
                {
                    case "/info":
                        return NoQuery(target) ?? ApiResponse.Json(200, Info());
                    case "/state":
                        return NoQuery(target) ?? ApiResponse.Json(200, _state.Render());
                    case "/actions":
                        return NoQuery(target) ?? ApiResponse.Json(200, ApiActionCatalog.CatalogJson(_o.Registry, _o.Services, _o.AllowActions()));
                    default:
                        return EventsRoute(target);
                }
            }

            const string actionsPrefix = ApiDefaults.PathPrefix + "/actions/";
            if (path.StartsWith(actionsPrefix, StringComparison.Ordinal) && path.Length > actionsPrefix.Length)
            {
                var segment = path.Substring(actionsPrefix.Length);
                if (ApiActionHandles.IsHandleId(segment))
                {
                    if (method != "GET")
                    {
                        return NotAllowed("GET");
                    }

                    if (!_limiter.TryTake(ApiRateClass.Read, out var retry))
                    {
                        return RateLimited(retry, "too many handle reads");
                    }

                    return RefuseBodyOnGet(r) ?? HandleRoute(segment, target);
                }

                if (ActionDescriptor.IsValidName(segment))
                {
                    if (method != "POST")
                    {
                        return NotAllowed("POST");
                    }

                    if (!_limiter.TryTake(ApiRateClass.Action, out var retry))
                    {
                        return RateLimited(retry, "too many actions started");
                    }

                    return NoQuery(target) ?? StartAction(segment, r, client, audit);
                }
            }

            return ApiResponse.Error(404, ApiErrorCodes.NotFound, "no route " + path + " (routes: /v1/info, /v1/state, /v1/events, /v1/actions, /v1/actions/<name>, /v1/actions/<handle>)");
        }

        private ApiResponse EventsRoute(ApiTarget target)
        {
            long after = 0;
            var limit = ApiDefaults.DefaultEventsPerRead;
            double wait = 0;
            foreach (var pair in target.Query)
            {
                switch (pair.Key)
                {
                    case "after":
                        if (!long.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out after))
                        {
                            return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "after must be a whole number of 0 or more");
                        }

                        break;
                    case "limit":
                        if (!int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                            || limit < 1 || limit > ApiDefaults.MaxEventsPerRead)
                        {
                            return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "limit must be 1-" + ApiDefaults.MaxEventsPerRead);
                        }

                        break;
                    case "wait":
                        if (!TryWait(pair.Value, out wait, out var waitProblem))
                        {
                            return ApiResponse.Error(400, ApiErrorCodes.BadRequest, waitProblem);
                        }

                        break;
                    default:
                        return UnknownQuery(pair.Key, "after, wait, limit");
                }
            }

            if (wait <= 0)
            {
                return ApiResponse.Json(200, _events.Read(after, limit).ToJson());
            }

            if (Interlocked.Increment(ref _waiters) > _o.MaxConcurrentWaits)
            {
                Interlocked.Decrement(ref _waiters);
                return RateLimited(1, "at most " + _o.MaxConcurrentWaits + " long-polls may wait at once");
            }

            try
            {
                return ApiResponse.Json(200, _events.Wait(after, limit, TimeSpan.FromSeconds(wait)).ToJson());
            }
            finally
            {
                Interlocked.Decrement(ref _waiters);
            }
        }

        private ApiResponse HandleRoute(string id, ApiTarget target)
        {
            double wait = 0;
            foreach (var pair in target.Query)
            {
                if (pair.Key != "wait")
                {
                    return UnknownQuery(pair.Key, "wait");
                }

                if (!TryWait(pair.Value, out wait, out var problem))
                {
                    return ApiResponse.Error(400, ApiErrorCodes.BadRequest, problem);
                }
            }

            if (!_handles.TryGet(id, out var handle) || handle is null)
            {
                return ApiResponse.Error(
                    404,
                    ApiErrorCodes.NotFound,
                    "no action handle " + id + " (unknown, or finished more than " +
                    ApiDefaults.HandleLifetime.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " minutes ago)");
            }

            if (wait > 0 && !handle.IsDone)
            {
                if (Interlocked.Increment(ref _waiters) > _o.MaxConcurrentWaits)
                {
                    Interlocked.Decrement(ref _waiters);
                    return RateLimited(1, "at most " + _o.MaxConcurrentWaits + " long-polls may wait at once");
                }

                try
                {
                    handle.Wait(TimeSpan.FromSeconds(wait));
                }
                finally
                {
                    Interlocked.Decrement(ref _waiters);
                }
            }

            return ApiResponse.Json(200, handle.ToJson());
        }

        private ApiResponse StartAction(string name, ApiRequest r, string client, ApiAuditRecord audit)
        {
            if (!_o.Registry.TryGet(name, out var binding) || binding is null)
            {
                return ApiResponse.Error(404, ApiErrorCodes.NotFound, "no action named " + name + " (GET /v1/actions lists them)");
            }

            var descriptor = binding.Descriptor;
            audit.Tier = ApiActionCatalog.TierName(descriptor.Tier);
            var verdict = ApiActionCatalog.Decide(descriptor, _o.AllowActions(), _o.Services.HasConfirmationGate, out var reason);
            if (verdict == ApiActionVerdict.Denied)
            {
                return ApiResponse.Error(403, ApiErrorCodes.Denied, descriptor.Name + ": " + reason);
            }

            if (verdict == ApiActionVerdict.NeedsConfirmation)
            {
                return ApiResponse.Error(403, ApiErrorCodes.ConfirmationUnavailable, descriptor.Name + ": " + reason);
            }

            if (!_o.Services.HasRunner)
            {
                return ApiResponse.Error(503, ApiErrorCodes.Unavailable, UnavailableActionRunner.Reason);
            }

            var parsed = ReadJsonBody(r, out var args);
            if (parsed != null)
            {
                return parsed;
            }

            if (!ApiActionCatalog.TryBind(descriptor, args, out var bound, out var bindProblem) || bound is null)
            {
                return ApiResponse.Error(400, ApiErrorCodes.InvalidArgs, bindProblem);
            }

            var idempotencyKey = r.Headers.Get(ApiDefaults.IdempotencyHeader);
            if (idempotencyKey != null && !ApiIdempotencyCache.IsValidKey(idempotencyKey))
            {
                return ApiResponse.Error(400, ApiErrorCodes.BadRequest, ApiDefaults.IdempotencyHeader + " must be 1-64 characters of letters, digits and . _ : -");
            }

            ApiActionHandle? handle;
            var replayed = false;
            if (idempotencyKey is null)
            {
                if (!_handles.TryCreate(descriptor.Name, descriptor.Tier, client, out handle))
                {
                    return RateLimited(1, "at most " + _handles.MaxInFlight + " actions may run at once");
                }
            }
            else
            {
                var outcome = _idempotency.GetOrCreate(
                    idempotencyKey,
                    ApiActionCatalog.Fingerprint(descriptor.Name, bound),
                    () => _handles.TryCreate(descriptor.Name, descriptor.Tier, client, out var created) ? created : null,
                    out handle);
                switch (outcome)
                {
                    case ApiIdempotencyOutcome.Conflict:
                        return ApiResponse.Error(409, ApiErrorCodes.IdempotencyConflict, "this " + ApiDefaults.IdempotencyHeader + " was used with a different action or arguments");
                    case ApiIdempotencyOutcome.Full:
                        return RateLimited(1, "at most " + _handles.MaxInFlight + " actions may run at once");
                    case ApiIdempotencyOutcome.Replayed:
                        replayed = true;
                        break;
                }
            }

            if (handle is null)
            {
                return ApiResponse.Error(500, ApiErrorCodes.Internal, "no action handle could be created");
            }

            audit.Handle = handle.Id;
            if (replayed)
            {
                return ApiResponse.Json(200, handle.ToJson(replayed: true), Location(handle));
            }

            Run(handle, new ActionRequest(descriptor.Name, bound, ActionCaller.Api, "api:" + client, idempotencyKey, _o.ActionTimeout));
            return ApiResponse.Json(202, handle.ToJson(), Location(handle));
        }

        private void Run(ApiActionHandle handle, ActionRequest request)
        {
            handle.Cancellation.CancelAfter(ApiDefaults.ActionHardLimit);
            Task<ActionResult> task;
            try
            {
                task = _o.Services.RunAsync(request, handle.Cancellation.Token);
            }
            catch (Exception ex)
            {
                Finish(handle, ActionResult.Failed("the action runner failed to start " + request.Name + ": " + ex.GetType().Name + ": " + ex.Message));
                return;
            }

            task.ContinueWith(
                t =>
                {
                    ActionResult result;
                    if (t.IsCanceled)
                    {
                        result = ActionResult.TimedOut("the API stopped waiting for " + request.Name);
                    }
                    else if (t.IsFaulted)
                    {
                        var error = t.Exception?.GetBaseException();
                        result = ActionResult.Failed("the action runner failed on " + request.Name + ": " + (error is null ? "unknown error" : error.GetType().Name + ": " + error.Message));
                    }
                    else
                    {
                        result = t.Result ?? ActionResult.Failed("the action runner returned no result for " + request.Name);
                    }

                    Finish(handle, result);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void Finish(ApiActionHandle handle, ActionResult result)
        {
            if (handle.IsDone)
            {
                return;
            }

            // The event and the trace record go out before the handle completes, so a client that
            // waits on the handle finds the completion event already in the stream.
            try
            {
                _events.Append(ApiEventMapper.ActionCompleted, ApiEventMapper.FromActionCompleted(handle.Id, handle.Action, handle.Tier, result.Status));
                ConsoleEvents.File(
                    ApiAuditLog.Sys,
                    "actionDone",
                    ("handle", handle.Id),
                    ("action", handle.Action),
                    ("tier", ApiActionCatalog.TierName(handle.Tier)),
                    ("status", ApiActionCatalog.StatusName(result.Status)),
                    ("path", ApiActionCatalog.PathName(result.Path)),
                    ("client", handle.Client),
                    ("ms", Math.Round((_o.Clock.MonotonicSeconds - handle.CreatedMono) * 1000, 1)));
            }
            catch (Exception ex)
            {
                Diagnose("recording the end of " + handle.Id + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            _handles.Complete(handle, result);
        }

        private ApiResponse? ReadJsonBody(ApiRequest r, out JsonObject? args)
        {
            args = null;
            if (r.Chunked)
            {
                return ApiResponse.Error(411, ApiErrorCodes.LengthRequired, "send the body with Content-Length; chunked bodies are not accepted");
            }

            if (r.ContentLength is long declared && declared > _o.MaxBodyBytes)
            {
                return ApiResponse.Error(413, ApiErrorCodes.TooLarge, "the body is larger than " + _o.MaxBodyBytes + " bytes");
            }

            var body = r.ReadBody(_o.MaxBodyBytes);
            if (body.Status == ApiBodyStatus.TooLarge)
            {
                return ApiResponse.Error(413, ApiErrorCodes.TooLarge, "the body is larger than " + _o.MaxBodyBytes + " bytes");
            }

            if (body.Status == ApiBodyStatus.Failed)
            {
                return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "the body could not be read: " + body.Problem);
            }

            if (body.Bytes.Length == 0)
            {
                return null;
            }

            if (!IsJsonContentType(r.Headers.Get("Content-Type")))
            {
                return ApiResponse.Error(415, ApiErrorCodes.UnsupportedMediaType, "a body must be Content-Type: application/json");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(body.Bytes);
            }
            catch (ArgumentException ex)
            {
                return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "the body is not valid UTF-8: " + ex.Message);
            }

            if (!JsonReader.TryParse(text, out var value, out var jsonProblem))
            {
                return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "the body is not strict JSON: " + jsonProblem);
            }

            if (!(value is JsonObject envelope))
            {
                return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "the body must be a JSON object like {\"args\":{...}}");
            }

            foreach (var member in envelope)
            {
                if (member.Key != "args")
                {
                    return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "unknown member \"" + member.Key + "\"; the body is {\"args\":{...}}");
                }

                if (member.Value is null)
                {
                    continue;
                }

                if (!(member.Value is JsonObject a))
                {
                    return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "\"args\" must be a JSON object");
                }

                args = a;
            }

            return null;
        }

        private string Info()
        {
            var services = _o.Services;
            return ApiJson.Write(new JsonObject
            {
                ["api"] = new JsonObject
                {
                    ["version"] = ApiDefaults.WireVersion,
                    ["basePath"] = ApiDefaults.PathPrefix,
                    ["endpoint"] = ApiDefaults.BaseUrl(_o.Port),
                    ["stateSchema"] = ApiDefaults.StateSchema,
                },
                ["loader"] = new JsonObject { ["version"] = _o.Facts.LoaderVersion, ["modApi"] = _o.Facts.ModApiVersion },
                ["game"] = new JsonObject { ["version"] = _o.Facts.GameVersion },
                ["server"] = new JsonObject
                {
                    ["transport"] = TransportName,
                    ["fallbackReason"] = FallbackReason,
                    ["startedAt"] = ApiJson.Time(StartedAt),
                    ["uptimeSec"] = Math.Round(_o.Clock.MonotonicSeconds - StartedMono, 1),
                    ["keyId"] = KeyId,
                },
                ["capabilities"] = new JsonObject
                {
                    ["state"] = true,
                    ["events"] = true,
                    ["actions"] = new JsonObject
                    {
                        ["registered"] = _o.Registry.Count,
                        ["runner"] = services.HasRunner,
                        ["allowActions"] = _o.AllowActions(),
                        ["confirmationGate"] = services.HasConfirmationGate,
                    },
                    ["voiceStreams"] = false,
                    ["micUtterances"] = false,
                },
                ["events"] = new JsonObject
                {
                    ["epoch"] = _events.Epoch,
                    ["latest"] = _events.Latest,
                    ["oldest"] = _events.Oldest,
                    ["capacity"] = _events.Capacity,
                    ["droppedTotal"] = _events.DroppedTotal,
                    ["types"] = new List<object?>(ApiEventMapper.Types),
                },
                ["limits"] = new JsonObject
                {
                    ["maxBodyBytes"] = _o.MaxBodyBytes,
                    ["maxWaitSec"] = _o.MaxWaitSeconds,
                    ["maxEventsPerRead"] = ApiDefaults.MaxEventsPerRead,
                    ["maxConcurrentWaits"] = _o.MaxConcurrentWaits,
                    ["maxActionsInFlight"] = _handles.MaxInFlight,
                    ["actionTimeoutMs"] = (long)_o.ActionTimeout.TotalMilliseconds,
                    ["ratePerSec"] = new JsonObject
                    {
                        ["read"] = _limiter.RateOf(ApiRateClass.Read),
                        ["events"] = _limiter.RateOf(ApiRateClass.Events),
                        ["actions"] = _limiter.RateOf(ApiRateClass.Action),
                    },
                },
            });
        }

        private bool TryWait(string text, out double wait, out string problem)
        {
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out wait)
                || double.IsNaN(wait) || wait < 0 || wait > _o.MaxWaitSeconds)
            {
                wait = 0;
                problem = "wait must be a number of seconds from 0 to " + _o.MaxWaitSeconds.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static ApiResponse? NoQuery(ApiTarget target) =>
            target.Query.Count == 0 ? null : UnknownQuery(target.Query[0].Key, "none");

        private static ApiResponse UnknownQuery(string name, string accepted) =>
            ApiResponse.Error(400, ApiErrorCodes.BadRequest, "unknown query parameter \"" + name + "\" (accepted: " + accepted + ")");

        private static ApiResponse? RefuseBodyOnGet(ApiRequest r) =>
            r.Chunked || (r.ContentLength is long n && n > 0)
                ? ApiResponse.Error(400, ApiErrorCodes.BadRequest, "a GET request has no body")
                : null;

        private static ApiResponse NotAllowed(string allow) =>
            ApiResponse.Error(405, ApiErrorCodes.MethodNotAllowed, "use " + allow + " on this route", ApiResponse.Header("Allow", allow));

        private static ApiResponse RateLimited(double retrySeconds, string message) =>
            ApiResponse.Error(429, ApiErrorCodes.RateLimited, message, ApiResponse.RetryAfter(retrySeconds));

        private static KeyValuePair<string, string> Location(ApiActionHandle handle) =>
            ApiResponse.Header("Location", ApiDefaults.PathPrefix + "/actions/" + handle.Id);

        private static bool IsJsonContentType(string? value)
        {
            if (value is null)
            {
                return false;
            }

            var semicolon = value.IndexOf(';');
            var media = (semicolon >= 0 ? value.Substring(0, semicolon) : value).Trim();
            if (!string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (semicolon < 0)
            {
                return true;
            }

            var parameter = value.Substring(semicolon + 1).Trim();
            return parameter.Length == 0
                || string.Equals(parameter.Replace(" ", string.Empty), "charset=utf-8", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The caller's name for the audit line: <c>[A-Za-z0-9._-]{1,32}</c>, otherwise <c>api</c>.</summary>
        /// <param name="header">The <c>X-CWY-Client</c> value, or null.</param>
        public static string ClientName(string? header)
        {
            if (string.IsNullOrEmpty(header) || header!.Length > 32)
            {
                return "api";
            }

            foreach (var c in header)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
                {
                    return "api";
                }
            }

            return header;
        }

        /// <summary>The path for the audit line: no query, at most 120 characters, control characters replaced by <c>?</c>.</summary>
        private static string AuditPath(string target)
        {
            var q = target.IndexOf('?');
            var path = q >= 0 ? target.Substring(0, q) : target;
            if (path.Length > 120)
            {
                path = path.Substring(0, 120) + "…";
            }

            var chars = path.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] < ' ' || chars[i] == '\u007f')
                {
                    chars[i] = '?';
                }
            }

            return new string(chars);
        }

        private void Diagnose(string message)
        {
            if (_o.Diagnostics != null)
            {
                _o.Diagnostics(message);
                return;
            }

            ConsoleEvents.File(ApiAuditLog.Sys, "diagnostic", ("message", message));
        }
    }
}
