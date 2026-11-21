using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Api.Maker.Studio;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>Everything the maker router works with. The plugin builds one per start; tests build their own.</summary>
    public sealed class MakerRouterOptions
    {
        /// <summary>The port (decides the accepted <c>Host</c> and <c>Origin</c>).</summary>
        public int Port { get; set; } = ApiDefaults.DefaultPort;

        /// <summary>The clock.</summary>
        public IApiClock Clock { get; set; } = SystemApiClock.Instance;

        /// <summary>The web app folder (<c>BepInEx/plugins/CwyLsModLoader/maker</c>); null answers 503 for <c>/maker/</c>.</summary>
        public string? WebRoot { get; set; }

        /// <summary>The read backend, asked per request (<see cref="MakerBackends.Catalog"/> in the game).</summary>
        public Func<IMakerCatalog?> Catalog { get; set; } = () => MakerBackends.Catalog;

        /// <summary>The write backend, asked per request (<see cref="MakerBackends.Editor"/> in the game).</summary>
        public Func<IMakerEditor?> Editor { get; set; } = () => MakerBackends.Editor;

        /// <summary>The optional Studio v2 feature backends, asked per capability request.</summary>
        public Func<MakerStudioBackends?> Studio { get; set; } = () => MakerBackends.Studio;

        /// <summary>The event stream; created when null.</summary>
        public MakerEventRing? Events { get; set; }

        /// <summary>The audit log of the maker-only listener (<see cref="MakerRouter.Handle"/>); null publishes through the bus with console reads off.</summary>
        public ApiAuditLog? Audit { get; set; }

        /// <summary>Rate limits; null means reads 20/s, event polls 10/s, writes and plays 10/s (bursts twice that).</summary>
        public ApiRateLimiter? Limiter { get; set; }

        /// <summary>The main-thread part of a backend call (2 s, the dispatcher's timeout).</summary>
        public TimeSpan BackendTimeout { get; set; } = MakerDefaults.BackendTimeout;

        /// <summary>Extra wait for the backend's own answer after <see cref="BackendTimeout"/>.</summary>
        public TimeSpan BackendGrace { get; set; } = MakerDefaults.BackendGrace;

        /// <summary>
        /// The whole wait of a Studio catalog read (packs, lines, line, groups): a cold catalog build reads every line and the
        /// pack's files, which takes seconds in a game that runs in the background. Never shorter than
        /// <see cref="BackendTimeout"/> plus <see cref="BackendGrace"/>.
        /// </summary>
        public TimeSpan CatalogTimeout { get; set; } = MakerDefaults.CatalogTimeout;

        /// <summary>
        /// Most backend calls still running at once, including ones the router already gave up on (their late
        /// results are dropped); more are answered 503 without calling the backend.
        /// </summary>
        public int MaxBackendCalls { get; set; } = ApiDefaults.MaxConcurrentRequests;

        /// <summary>Longest long-poll, in seconds.</summary>
        public double MaxWaitSeconds { get; set; } = ApiDefaults.MaxWaitSeconds;

        /// <summary>Most simultaneous long-polls.</summary>
        public int MaxConcurrentWaits { get; set; } = ApiDefaults.MaxConcurrentWaits;

        /// <summary>Where diagnostics of the router itself go (file-only bus records when null).</summary>
        public Action<string>? Diagnostics { get; set; }
    }

    /// <summary>
    /// Pack Maker's request pipeline (ticket DS-01, CONTRACT.md): the web app under <c>/maker/</c> and the
    /// <c>/v1/maker/*</c> routes. It runs inside the API's listener (<see cref="ApiRouter.UseMaker"/>) or on a
    /// maker-only listener (<see cref="Handle"/>) when <c>[Api] Enabled=false</c>. Order: stopping (503) →
    /// <c>Host</c> is accepted by the Maker router (the listener promises only <c>127.0.0.1:&lt;port&gt;</c>) (421) → <c>Origin</c>, if
    /// present, is this page's own and <c>Sec-Fetch-Site</c>, if present, is <c>same-origin</c> or <c>none</c>
    /// (403 <c>forbidden_origin</c>) → for the page a one-time launch link or browser session; for
    /// <c>/v1/maker/*</c> the maker token in constant time (401) → route and
    /// segments (404/400) → rate limit (429) → query and body (400/413/415) → backend (503 when none) with the
    /// main-thread timeout. Never sends CORS headers. Never throws from <see cref="Handle"/>; never touches Unity
    /// (the backends hop to the main thread themselves); never logs the token, a header value or a body.
    /// </summary>
    public sealed partial class MakerRouter
    {
        private const string AuthorizationHeader = "Authorization";
        private const string WavMediaType = "audio/wav";

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly MakerRouterOptions _o;
        private readonly ApiToken _token;
        private readonly MakerEventRing _events;
        private readonly ApiAuditLog _audit;
        private readonly ApiRateLimiter _limiter;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private int _waiters;
        private int _stopping;
        private int _backendCalls;

        [ThreadStatic] private static ApiRequest? _threadRequest;
        [ThreadStatic] private static MakerRequestTrace? _threadTrace;

        /// <summary>Creates a router.</summary>
        /// <param name="token">The maker token (per start; not the API's access key).</param>
        /// <param name="options">The dependencies.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">The port is out of range.</exception>
        public MakerRouter(ApiToken token, MakerRouterOptions options)
        {
            _token = token ?? throw new ArgumentNullException(nameof(token));
            _o = options ?? throw new ArgumentNullException(nameof(options));
            if (!ApiDefaults.IsValidPort(_o.Port))
            {
                throw new ArgumentException("the port must be " + ApiDefaults.MinPort + "-" + ApiDefaults.MaxPort, nameof(options));
            }

            _events = _o.Events ?? new MakerEventRing(MakerDefaults.EventCapacity, () => _o.Clock.Now, _o.Diagnostics);
            _audit = _o.Audit ?? new ApiAuditLog(_o.Clock, () => false);
            _limiter = _o.Limiter ?? new ApiRateLimiter(_o.Clock, MakerDefaults.ReadsPerSecond, MakerDefaults.EventsPerSecond, MakerDefaults.WritesPerSecond);
        }

        /// <summary>The port.</summary>
        public int Port => _o.Port;

        /// <summary>The event stream (install it with <see cref="MakerBackends.UseEvents"/>).</summary>
        public MakerEventRing Events => _events;

        /// <summary>The web app folder, or null.</summary>
        public string? WebRoot => _o.WebRoot;

        /// <summary>The maker token's non-secret id (first 8 hex of its SHA-256).</summary>
        public string TokenId => _token.Id;

        /// <summary>Requests answered by <see cref="Handle"/> (the maker-only listener).</summary>
        public ApiCounters Counters { get; } = new ApiCounters();

        /// <summary>Long-polls waiting right now.</summary>
        public int Waiting => Volatile.Read(ref _waiters);

        /// <summary>Backend calls still running, including ones that already timed out.</summary>
        public int BackendCalls => Volatile.Read(ref _backendCalls);

        /// <summary>True once <see cref="BeginStop"/> was called.</summary>
        public bool IsStopping => Volatile.Read(ref _stopping) == 1;

        /// <summary>Answers 503 from now on, wakes every long-poll and cancels backend calls in flight.</summary>
        public void BeginStop()
        {
            Volatile.Write(ref _stopping, 1);
            _events.Close();
            try
            {
                _stop.Cancel();
            }
            catch (Exception ex)
            {
                Diagnose("cancelling backend calls failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Handles one request on the maker-only listener: maker targets go through <see cref="Route"/>, anything
        /// else is 404. Never throws; writes exactly one audit record.
        /// </summary>
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
                response = MakerDefaults.Owns(request.Target)
                    ? Route(request, audit)
                    : IsStopping
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Pack Maker is closing")
                        : ApiResponse.Error(404, MakerErrorCodes.NotFound, "this listener serves Pack Maker only (" + MakerDefaults.WebPath + "/); the local game API is off");
            }
            catch (Exception ex)
            {
                Diagnose("request " + request.Method + " " + audit.Path + " failed: " + ex.GetType().Name + ": " + ex.Message);
                response = ApiResponse.Error(500, MakerErrorCodes.Internal, "Pack Maker failed on this request (" + ex.GetType().Name + "); details are in the trace file");
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

        /// <summary>
        /// Answers one Pack Maker request (a target <see cref="MakerDefaults.Owns"/> accepts) and fills the audit
        /// record's client and authentication; the caller writes the record. Used by <see cref="ApiRouter"/>
        /// (which owns the audit and the 500 on an exception there) and by <see cref="Handle"/>.
        /// </summary>
        /// <param name="r">The request.</param>
        /// <param name="audit">The audit record to fill.</param>
        public ApiResponse Route(ApiRequest r, ApiAuditRecord audit)
        {
            if (r is null)
            {
                throw new ArgumentNullException(nameof(r));
            }

            if (audit is null)
            {
                throw new ArgumentNullException(nameof(audit));
            }

            // Per-request trace state of this request thread (the backend call below runs synchronously on its behalf).
            _threadRequest = r;
            _threadTrace = null;
            var started = MakerRequestTrace.Enabled ? Stopwatch.GetTimestamp() : 0L;
            ApiResponse response;
            try
            {
                response = RouteCore(r, audit);
            }
            catch (Exception ex)
            {
                LogFailure(r, 500, "exception " + Describe(ex), _threadTrace);
                _threadRequest = null;
                throw;
            }

            if (started != 0L)
            {
                TraceLine(r, response, MakerRequestTrace.MsSince(started), _threadTrace);
            }

            _threadRequest = null;
            _threadTrace = null;
            return response;
        }

        private ApiResponse RouteCore(ApiRequest r, ApiAuditRecord audit)
        {
            if (IsStopping)
            {
                return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Pack Maker is closing");
            }

            if (!MakerDefaults.IsAcceptedHost(r.Headers.Get("Host"), _o.Port))
            {
                return ApiResponse.Error(
                    421,
                    MakerHttpCodes.HostRefused,
                    "the Host header must be " + ApiDefaults.HostAuthority(_o.Port) + " or localhost:" + _o.Port.ToString(CultureInfo.InvariantCulture));
            }

            if (r.Headers.Has("Origin") && !MakerDefaults.IsAcceptedOrigin(r.Headers.Get("Origin"), _o.Port))
            {
                return ApiResponse.Error(403, MakerHttpCodes.ForbiddenOrigin, "Pack Maker answers only its own page (this Origin is another site)");
            }

            if (r.Headers.Has("Sec-Fetch-Site") && !MakerDefaults.IsAcceptedFetchSite(r.Headers.Get("Sec-Fetch-Site")))
            {
                return ApiResponse.Error(403, MakerHttpCodes.ForbiddenOrigin, "Pack Maker answers only its own page (the browser says this request comes from another site)");
            }

            if (!ApiTarget.TryParse(r.Target, out var target, out var targetProblem) || target is null)
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, targetProblem);
            }

            var path = target.Path;
            if (path == MakerDefaults.WebPath)
            {
                var location = MakerDefaults.WebPath + "/" + (target.Get("k") is string key ? "?k=" + Uri.EscapeDataString(key) : string.Empty);
                return r.Method == "GET"
                    ? new ApiResponse(302, ApiJson.Write(new JsonObject { ["location"] = location }), new[] { ApiResponse.Header("Location", location), ApiResponse.Header("Referrer-Policy", "no-referrer") })
                    : NoRoute(r.Method, path, "GET");
            }

            if (path.StartsWith(MakerDefaults.WebPath + "/", StringComparison.Ordinal))
            {
                return StaticFile(r, target, path.Substring(MakerDefaults.WebPath.Length + 1));
            }

            if (!_token.Matches(r.Headers.Get(AuthorizationHeader)))
            {
                return ApiResponse.Error(
                    401,
                    MakerHttpCodes.Unauthorized,
                    "missing or wrong maker token: reload Pack Maker (the token is new every time Pack Maker starts)",
                    ApiResponse.Header("WWW-Authenticate", ApiToken.Scheme + " realm=\"cwy-maker\""));
            }

            audit.Authenticated = true;
            var client = r.Headers.Get(ApiDefaults.ClientHeader);
            audit.Client = client != null && ApiRouter.ClientName(client) == client ? client : MakerDefaults.ClientName;
            return ApiRoute(r, target, path);
        }

        private ApiResponse StaticFile(ApiRequest r, ApiTarget target, string relative)
        {
            if (r.Method != "GET")
            {
                return NoRoute(r.Method, MakerDefaults.WebPath + "/" + relative, "GET");
            }

            var bodyProblem = RefuseBody(r);
            if (bodyProblem != null)
            {
                return bodyProblem;
            }

            if (relative.Length == 0 || string.Equals(relative, MakerStaticFiles.IndexFile, StringComparison.OrdinalIgnoreCase))
            {
                var launch = PageAccess(r, target);
                if (launch != null) return launch;
            }

            switch (MakerStaticFiles.Read(_o.WebRoot, relative, _token.Reveal(), out var bytes, out var contentType, out var problem))
            {
                case MakerStaticStatus.Ok:
                    // The page may not be framed by another site (clickjacking); the rest of the hardening is
                    // the transports' common headers (no-store, nosniff).
                    return ApiResponse.Raw(
                        200,
                        bytes,
                        contentType,
                        ApiResponse.Header("X-Frame-Options", "DENY"),
                        ApiResponse.Header("Referrer-Policy", "no-referrer"),
                        ApiResponse.Header("Content-Security-Policy", "frame-ancestors 'none'"));
                case MakerStaticStatus.BadPath:
                    return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                case MakerStaticStatus.NotFound:
                    return ApiResponse.Error(404, MakerErrorCodes.NotFound, problem);
                default:
                    Diagnose("static: " + problem);
                    return ApiResponse.Error(503, MakerErrorCodes.Unavailable, problem);
            }
        }

        private ApiResponse ApiRoute(ApiRequest r, ApiTarget target, string path)
        {
            var rest = path.Length > MakerDefaults.ApiPath.Length + 1 ? path.Substring(MakerDefaults.ApiPath.Length + 1) : string.Empty;
            var s = rest.Length == 0 ? new string[0] : rest.Split('/');
            var method = r.Method;

            if (s.Length == 2 && s[0] == MakerStudioRouter.Segment
                && (s[1] == MakerStudioRouter.CapabilitiesSegment || s[1] == MakerStudioRouter.StatusSegment))
            {
                if (method != "GET")
                {
                    return NoRoute(method, path, "GET");
                }

                if (!Take(ApiRateClass.Read, out var limited))
                {
                    return limited!;
                }

                var bodyProblem = RefuseBody(r);
                if (bodyProblem != null)
                {
                    return bodyProblem;
                }

                var queryProblem = NoQuery(target);
                if (queryProblem != null)
                {
                    return queryProblem;
                }

                MakerStudioBackends? studio;
                try
                {
                    studio = _o.Studio();
                }
                catch (Exception ex)
                {
                    Diagnose("reading the Studio v2 slots failed: " + ex.GetType().Name + ": " + ex.Message);
                    studio = null;
                }

                if (studio is null)
                {
                    return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio v2 is not installed or not ready yet");
                }

                if (s[1] == MakerStudioRouter.CapabilitiesSegment)
                {
                    return MakerStudioRouter.Capabilities(studio);
                }

                return studio.Status is null
                    ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio v2 game status is not installed or not ready yet")
                    : CachedStudioStatus(studio.Status);
            }

            if (s.Length > 0 && s[0] == MakerStudioRouter.Segment)
            {
                return StudioRoute(r, target, s, path);
            }

            if (s.Length == 1 && (s[0] == "status" || s[0] == "packs" || s[0] == "lines" || s[0] == "events"))
            {
                if (method != "GET")
                {
                    return NoRoute(method, path, "GET");
                }

                if (!Take(s[0] == "events" ? ApiRateClass.Events : ApiRateClass.Read, out var limited))
                {
                    return limited!;
                }

                var bodyProblem = RefuseBody(r);
                if (bodyProblem != null)
                {
                    return bodyProblem;
                }

                switch (s[0])
                {
                    case "status":
                        return NoQuery(target) ?? CallCatalog<MakerStatus>(c => c.StatusAsync, MakerJson.Status);
                    case "packs":
                        return NoQuery(target) ?? CallCatalog<IReadOnlyList<MakerPack>>(c => c.PacksAsync, MakerJson.Packs);
                    case "lines":
                        return LinesRoute(target);
                    default:
                        return EventsRoute(target);
                }
            }

            if (s.Length == 2 && s[0] == "lines")
            {
                if (method != "GET")
                {
                    return NoRoute(method, path, "GET");
                }

                if (!MakerSegments.IsId(s[1]))
                {
                    return BadSegment("the line id", "[A-Za-z0-9_.()-]{1,128}");
                }

                if (!Take(ApiRateClass.Read, out var limited))
                {
                    return limited!;
                }

                var bodyProblem = RefuseBody(r);
                if (bodyProblem != null)
                {
                    return bodyProblem;
                }

                if (!TryLangQuery(target, out var lang, out var queryProblem))
                {
                    return queryProblem!;
                }

                var id = s[1];
                return CallCatalog<MakerLine>(c => cancel => c.LineAsync(id, lang, cancel), MakerJson.Line);
            }

            if (s.Length == 1 && s[0] == "play")
            {
                if (method != "POST")
                {
                    return NoRoute(method, path, "POST");
                }

                if (!Take(ApiRateClass.Action, out var limited))
                {
                    return limited!;
                }

                return NoQuery(target) ?? PlayRoute(r);
            }

            if (s.Length == 5 && s[0] == "packs" && (s[2] == "subtitles" || s[2] == "voice"))
            {
                var voice = s[2] == "voice";
                if (method != "PUT" && method != "DELETE")
                {
                    return NoRoute(method, path, "PUT or DELETE");
                }

                if (!MakerSegments.IsPackId(s[1]))
                {
                    return BadSegment("the pack id", "[a-z0-9][a-z0-9._-]{0,63}");
                }

                if (!MakerSegments.IsLang(s[3]))
                {
                    return BadSegment("the language", "[a-z]{2,3}(-[a-z0-9]{2,8})?");
                }

                if (!MakerSegments.IsId(s[4]))
                {
                    return BadSegment(voice ? "the clip name" : "the line id", "[A-Za-z0-9_.()-]{1,128}");
                }

                if (!Take(ApiRateClass.Action, out var limited))
                {
                    return limited!;
                }

                var queryProblem = NoQuery(target);
                if (queryProblem != null)
                {
                    return queryProblem;
                }

                string packId = s[1], lang = s[3], key = s[4];
                if (method == "DELETE")
                {
                    var bodyProblem = RefuseBody(r);
                    if (bodyProblem != null)
                    {
                        return bodyProblem;
                    }

                    return voice
                        ? CallEditor(e => cancel => e.SaveVoiceAsync(packId, lang, key, null, cancel))
                        : CallEditor(e => cancel => e.SaveSubtitleAsync(packId, lang, key, null, cancel));
                }

                if (voice)
                {
                    var wavProblem = ReadWav(r, out var wav);
                    return wavProblem ?? CallEditor(e => cancel => e.SaveVoiceAsync(packId, lang, key, wav, cancel));
                }

                var jsonProblem = ReadJsonObject(r, out var body);
                if (jsonProblem != null)
                {
                    return jsonProblem;
                }

                if (!TrySubtitleText(body!, out var text, out var textProblem))
                {
                    return ApiResponse.Error(400, MakerErrorCodes.BadRequest, textProblem);
                }

                return CallEditor(e => cancel => e.SaveSubtitleAsync(packId, lang, key, text, cancel));
            }

            return ApiResponse.Error(
                404,
                MakerErrorCodes.NotFound,
                "no route " + path + " (routes: " + MakerDefaults.ApiPath + "/status, /packs, /lines, /lines/<id>, /studio/capabilities, /studio/status, /packs/<packId>/subtitles/<lang>/<id>, " +
                "/packs/<packId>/voice/<lang>/<clip>, /play, /events)");
        }

        private ApiResponse StudioRoute(ApiRequest r, ApiTarget target, string[] s, string path)
        {
            if (s.Length == 6 && s[1] == "packs" && s[3] == "operations" && (s[5] == "recovery" || s[5] == "resolve"))
            {
                if (!MakerSegments.IsPackId(s[2]) || !MakerStudioPaths.IsOperationId(s[4]))
                    return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "Invalid pack or blockedOperationId.");
                var preview = s[5] == "recovery";
                if (r.Method != (preview ? "GET" : "POST")) return NoRoute(r.Method, path, preview ? "GET" : "POST");
                if (!Take(preview ? ApiRateClass.Read : ApiRateClass.Action, out var limited)) return limited!;
                var noQuery = NoQuery(target); if (noQuery != null) return noQuery;
                var backend = ReadStudio()?.Recovery;
                if (preview)
                {
                    var bodyProblem = RefuseBody(r); if (bodyProblem != null) return bodyProblem;
                    return backend is null ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Project recovery is not installed")
                        : Call(cancel => backend.PreviewRecoveryAsync(s[2], s[4], cancel), MakerStudioJson.RecoveryPreview);
                }
                var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                if (body!.Count != 3 || !body.TryGetValue("operationId", out var op) || !(op is string operation)
                    || !MakerStudioPaths.IsOperationId(operation) || operation == s[4]
                    || !body.TryGetValue("expectedRecoveryRevision", out var expected) || !(expected is string revision)
                    || !MakerStudioPaths.IsOperationId(revision)
                    || !body.TryGetValue("strategy", out var strategy) || !(strategy is string choice) || choice != "keep-current")
                    return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "Expected operationId, expectedRecoveryRevision and strategy keep-current.");
                var request = new MakerRecoveryRequest { PackId = s[2], BlockedOperationId = s[4],
                    OperationId = operation, ExpectedRecoveryRevision = revision, Strategy = choice };
                return backend is null ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Project recovery is not installed")
                    : Call(cancel => backend.ResolveRecoveryAsync(request, cancel), MakerStudioJson.RecoveryResolved);
            }
            if (s.Length == 2 && s[1] == "packs" && r.Method == "POST")
                return CreatePackRoute(r, target);
            if (s.Length == 2 && s[1] == ClientLogSegment)
                return ClientLogRoute(r, target, path);
            var studio = ReadStudio();
            if (studio is null)
            {
                return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio v2 is not installed or not ready yet");
            }

            var catalogRoute = StudioCatalogRoute(r, target, s, path, studio);
            if (catalogRoute != null) return catalogRoute;
            var exportRoute = StudioExportRoute(r, target, s, path, studio);
            if (exportRoute != null) return exportRoute;
            var voiceRoute = StudioVoiceRoute(r, target, s, path, studio);
            if (voiceRoute != null) return voiceRoute;

            if (s.Length == 2 && s[1] == "play")
            {
                if (r.Method != "POST") return NoRoute(r.Method, path, "POST");
                if (!Take(ApiRateClass.Action, out var limited)) return limited!;
                var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                if (!TryStudioPlay(body!, out var request, out var problem))
                    return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                return studio.Playback is null
                    ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio playback is not installed")
                    : Call(cancel => studio.Playback.PlayAsync(request!, cancel), MakerStudioJson.Saved);
            }

            // /studio/packs/{packId}/languages/{lang}/lines/{id}/{workflow|subtitle|history}
            if (s.Length == 8 && s[1] == "packs" && s[3] == "languages" && s[5] == "lines")
            {
                var segmentProblem = StudioLineSegments(s[2], s[4], s[6]);
                if (segmentProblem != null) return segmentProblem;
                if (s[7] == "workflow")
                {
                    if (r.Method != "PATCH") return NoRoute(r.Method, path, "PATCH");
                    if (!Take(ApiRateClass.Action, out var limited)) return limited!;
                    var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                    var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                    if (!TryWorkflow(body!, s[2], s[4], s[6], out var request, out var problem))
                        return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                    return studio.Workflow is null
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio workflow is not installed")
                        : Call(cancel => studio.Workflow.UpdateWorkflowAsync(request!, cancel), MakerStudioJson.WorkflowSaved);
                }

                if (s[7] == "subtitle")
                {
                    if (r.Method != "PUT") return NoRoute(r.Method, path, "PUT");
                    if (!Take(ApiRateClass.Action, out var limited)) return limited!;
                    var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                    var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                    if (!TrySubtitle(body!, s[2], s[4], s[6], out var request, out var problem))
                        return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                    return studio.Subtitles is null
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio subtitles are not installed")
                        : Call(cancel => studio.Subtitles.SaveSubtitleAsync(request!, cancel), MakerStudioJson.Saved);
                }

                if (s[7] == "history")
                {
                    if (r.Method != "GET") return NoRoute(r.Method, path, "GET");
                    if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                    var bodyProblem = RefuseBody(r); if (bodyProblem != null) return bodyProblem;
                    if (!TryPage(target, MakerStudioContract.SubtitleRevisions, out var offset, out var limit, out var pageProblem))
                        return pageProblem!;
                    return studio.Subtitles is null
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio subtitles are not installed")
                        : Call(cancel => studio.Subtitles.HistoryAsync(s[2], s[4], s[6], offset, limit, cancel), MakerStudioJson.History);
                }

                if (s[7] == "takes")
                {
                    if (r.Method == "GET")
                    {
                        if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                        var bodyProblem = RefuseBody(r); if (bodyProblem != null) return bodyProblem;
                        var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                        return studio.Dubs is null
                            ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio dubs are not installed")
                            : Call(cancel => studio.Dubs.TakesAsync(s[2], s[4], s[6], cancel), MakerStudioJson.TakePage);
                    }

                    if (r.Method != "POST") return NoRoute(r.Method, path, "GET or POST");
                    if (!Take(ApiRateClass.Action, out var writeLimited)) return writeLimited!;
                    var noQuery = NoQuery(target); if (noQuery != null) return noQuery;
                    var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                    if (!TryCreateTake(body!, s[2], s[4], s[6], out var request, out var problem))
                        return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                    return studio.Dubs is null
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio dubs are not installed")
                        : Call(cancel => studio.Dubs.CreateTakeAsync(request!, cancel), MakerStudioJson.TakeSaved, 201);
                }

                if (s[7] == "favourite")
                {
                    if (r.Method != "PUT") return NoRoute(r.Method, path, "PUT");
                    if (!Take(ApiRateClass.Action, out var limited)) return limited!;
                    var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                    var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                    if (!TryFavourite(body!, s[2], s[4], s[6], out var request, out var problem))
                        return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                    return studio.Dubs is null
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio dubs are not installed")
                        : Call(cancel => studio.Dubs.SetFavouriteAsync(request!, cancel), MakerStudioJson.TakeSaved);
                }

                if (s[7] == "publish")
                {
                    if (r.Method != "POST") return NoRoute(r.Method, path, "POST");
                    if (!Take(ApiRateClass.Action, out var limited)) return limited!;
                    var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                    var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                    if (!TryPublish(body!, s[2], s[4], s[6], out var request, out var problem))
                        return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                    return studio.Dubs is null
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio dubs are not installed")
                        : Call(cancel => studio.Dubs.PublishAsync(request!, cancel), MakerStudioJson.Saved);
                }

                if (s[7] == "unpublish")
                {
                    if (r.Method != "POST") return NoRoute(r.Method, path, "POST");
                    if (!Take(ApiRateClass.Action, out var limited)) return limited!;
                    var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                    var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                    if (!TryUnpublish(body!, s[2], s[4], s[6], out var request, out var problem))
                        return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                    return studio.Dubs is null
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio dubs are not installed")
                        : Call(cancel => studio.Dubs.UnpublishAsync(request!, cancel), MakerStudioJson.Saved);
                }
            }

            // /studio/packs/{packId}/languages/{lang}/lines/{id}/takes/{takeId}
            if (s.Length == 9 && s[1] == "packs" && s[3] == "languages" && s[5] == "lines" && s[7] == "takes")
            {
                var segmentProblem = StudioLineSegments(s[2], s[4], s[6]);
                if (segmentProblem != null) return segmentProblem;
                if (!MakerStudioPaths.IsTakeId(s[8])) return BadSegment("the take id", "32 lowercase hexadecimal characters");
                if (r.Method == "PATCH")
                {
                    if (!Take(ApiRateClass.Action, out var limited)) return limited!;
                    var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                    var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                    if (!TryUpdateTake(body!, s[2], s[4], s[6], s[8], out var request, out var problem))
                        return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                    return studio.Dubs is null
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio dubs are not installed")
                        : Call(cancel => studio.Dubs.UpdateTakeAsync(request!, cancel), MakerStudioJson.TakeSaved);
                }

                if (r.Method != "DELETE") return NoRoute(r.Method, path, "PATCH or DELETE");
                if (!Take(ApiRateClass.Action, out var deleteLimited)) return deleteLimited!;
                var bodyProblem = RefuseBody(r); if (bodyProblem != null) return bodyProblem;
                if (!TryDeleteTakeQuery(target, s[2], s[4], s[6], s[8], out var deleteRequest, out var deleteQueryProblem))
                    return deleteQueryProblem!;
                return studio.Dubs is null
                    ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio dubs are not installed")
                    : Call(cancel => studio.Dubs.DeleteTakeAsync(deleteRequest!, cancel), MakerStudioJson.TakeRemoved);
            }

            // /studio/packs/{packId}/languages/{lang}/lines/{id}/takes/{takeId}/audio
            if (s.Length == 10 && s[1] == "packs" && s[3] == "languages" && s[5] == "lines" && s[7] == "takes" && s[9] == "audio")
            {
                var segmentProblem = StudioLineSegments(s[2], s[4], s[6]);
                if (segmentProblem != null) return segmentProblem;
                if (!MakerStudioPaths.IsTakeId(s[8])) return BadSegment("the take id", "32 lowercase hexadecimal characters");
                if (r.Method == "GET")
                {
                    if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                    var bodyProblem = RefuseBody(r); if (bodyProblem != null) return bodyProblem;
                    if (!TryTakeAudioGetQuery(target, out var kind, out var getQueryProblem))
                        return getQueryProblem!;
                    return studio.Dubs is null
                        ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio dubs are not installed")
                        : CallResponse(cancel => studio.Dubs.ReadAudioAsync(s[2], s[4], s[6], s[8], kind, cancel),
                            audio => ApiResponse.Raw(200, audio.Bytes, WavMediaType));
                }

                if (r.Method != "PUT") return NoRoute(r.Method, path, "GET or PUT");
                if (!Take(ApiRateClass.Action, out var putLimited)) return putLimited!;
                if (!TryTakeAudioPutQuery(target, s[2], s[4], s[6], s[8], out var putRequest, out var putQueryProblem))
                    return putQueryProblem!;
                var wavProblem = ReadWav(r, out var wav);
                if (wavProblem != null) return wavProblem;
                return studio.Dubs is null
                    ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio dubs are not installed")
                    : Call(cancel => studio.Dubs.SaveTakeAudioAsync(putRequest!, wav, cancel), MakerStudioJson.TakeSaved);
            }

            // /studio/packs/{packId}/languages/{lang}/lines/{id}/history/{revisionId}/revert
            if (s.Length == 10 && s[1] == "packs" && s[3] == "languages" && s[5] == "lines" && s[7] == "history" && s[9] == "revert")
            {
                var segmentProblem = StudioLineSegments(s[2], s[4], s[6]);
                if (segmentProblem != null) return segmentProblem;
                if (!MakerStudioPaths.IsOperationId(s[8])) return BadSegment("the revision id", "32 lowercase hexadecimal characters");
                if (r.Method != "POST") return NoRoute(r.Method, path, "POST");
                if (!Take(ApiRateClass.Action, out var limited)) return limited!;
                var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                if (!TryMutation(body!, out var expected, out var operation, out var problem))
                    return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                var request = new MakerStudioRevertRequest
                {
                    PackId = s[2], Lang = s[4], Id = s[6], RevisionId = s[8],
                    ExpectedRevision = expected!, OperationId = operation!,
                };
                return studio.Subtitles is null
                    ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio subtitles are not installed")
                    : Call(cancel => studio.Subtitles.RevertSubtitleAsync(request, cancel), MakerStudioJson.Saved);
            }

            // /studio/packs/{packId}/glossary/{lang}
            if (s.Length == 5 && s[1] == "packs" && s[3] == "glossary")
            {
                if (!MakerSegments.IsPackId(s[2])) return BadSegment("the pack id", "[a-z0-9][a-z0-9._-]{0,63}");
                if (!MakerSegments.IsLang(s[4])) return BadSegment("the language", "[a-z]{2,3}(-[a-z0-9]{2,8})?");
                if (studio.Projects is null) return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio projects are not installed");
                if (r.Method == "GET")
                {
                    if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                    var bodyProblem = RefuseBody(r); if (bodyProblem != null) return bodyProblem;
                    var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                    return Call(cancel => studio.Projects.GlossaryAsync(s[2], s[4], cancel), MakerStudioJson.Glossary);
                }

                if (r.Method != "PUT") return NoRoute(r.Method, path, "GET or PUT");
                if (!Take(ApiRateClass.Action, out var writeLimited)) return writeLimited!;
                var noQuery = NoQuery(target); if (noQuery != null) return noQuery;
                var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
                if (!TryGlossary(body!, s[2], s[4], out var request, out var problem))
                    return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
                return Call(cancel => studio.Projects.SaveGlossaryAsync(request!, cancel), MakerStudioJson.Glossary);
            }

            // /studio/packs/{packId}/operations/{operationId}
            if (s.Length == 5 && s[1] == "packs" && s[3] == "operations")
            {
                if (!MakerSegments.IsPackId(s[2])) return BadSegment("the pack id", "[a-z0-9][a-z0-9._-]{0,63}");
                if (!MakerStudioPaths.IsOperationId(s[4])) return BadSegment("the operation id", "32 lowercase hexadecimal characters");
                if (r.Method != "GET") return NoRoute(r.Method, path, "GET");
                if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                var bodyProblem = RefuseBody(r); if (bodyProblem != null) return bodyProblem;
                var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                return studio.Projects is null
                    ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio projects are not installed")
                    : Call(cancel => studio.Projects.OperationAsync(s[2], s[4], cancel), MakerStudioJson.Operation);
            }

            return ApiResponse.Error(404, MakerErrorCodes.NotFound, "no Studio v2 route " + path);
        }

        private ApiResponse CachedStudioStatus(IMakerStudioStatus status)
        {
            var task = status.StatusAsync(_stop.Token);
            if (task is null || !task.IsCompleted)
            {
                if (task != null) Observe(task);
                return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio status has no completed cached snapshot");
            }
            if (task.IsCanceled) return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio status was cancelled");
            if (task.IsFaulted) { Observe(task); return ApiResponse.Error(500, MakerErrorCodes.Internal, "Studio status failed"); }
            var result = task.Result;
            if (result is null) return ApiResponse.Error(500, MakerErrorCodes.Internal, "Studio status returned no result");
            if (!result.Ok)
            {
                var code = MakerDefaults.StatusOf(result.Error);
                return MakerStudioJson.ErrorResponse(code, code == 500 ? MakerErrorCodes.Internal : result.Error!, result.Message ?? "Studio status failed", result.Details);
            }
            return result.Value is null
                ? ApiResponse.Error(500, MakerErrorCodes.Internal, "Studio status returned no snapshot")
                : ApiResponse.Json(200, ApiJson.Write(MakerStudioJson.Status(result.Value)));
        }

        private MakerStudioBackends? ReadStudio()
        {
            try
            {
                return _o.Studio();
            }
            catch (Exception ex)
            {
                Diagnose("reading the Studio v2 slots failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static ApiResponse? StudioLineSegments(string packId, string lang, string id)
        {
            if (!MakerSegments.IsPackId(packId)) return BadSegment("the pack id", "[a-z0-9][a-z0-9._-]{0,63}");
            if (!MakerSegments.IsLang(lang)) return BadSegment("the language", "[a-z]{2,3}(-[a-z0-9]{2,8})?");
            return !MakerSegments.IsId(id) ? BadSegment("the line id", "[A-Za-z0-9_.()-]{1,128}") : null;
        }

        private ApiResponse LinesRoute(ApiTarget target)
        {
            var query = new MakerLineQuery { Filter = "all", Offset = 0, Limit = MakerDefaults.DefaultLinesLimit };
            string? lang = null;
            foreach (var pair in target.Query)
            {
                switch (pair.Key)
                {
                    case "lang":
                        lang = pair.Value;
                        break;
                    case "q":
                        if (pair.Value.Length > MakerDefaults.MaxSearchChars)
                        {
                            return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "q may be at most " + MakerDefaults.MaxSearchChars + " characters");
                        }

                        query.Search = pair.Value.Length == 0 ? null : pair.Value;
                        break;
                    case "filter":
                        if (!MakerSegments.IsFilter(pair.Value))
                        {
                            return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "filter must be all, missing-subtitle, missing-dub or done");
                        }

                        query.Filter = pair.Value;
                        break;
                    case "offset":
                        if (!int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                        {
                            return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "offset must be a whole number of 0 or more");
                        }

                        query.Offset = offset;
                        break;
                    case "limit":
                        if (!int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
                            || limit < 1 || limit > MakerDefaults.MaxLinesLimit)
                        {
                            return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "limit must be 1-" + MakerDefaults.MaxLinesLimit);
                        }

                        query.Limit = limit;
                        break;
                    default:
                        return UnknownQuery(pair.Key, "lang, q, filter, offset, limit");
                }
            }

            if (lang is null || !MakerSegments.IsLang(lang))
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "lang is required: a language code such as th or pt-br");
            }

            query.Lang = lang;
            return CallCatalog<MakerLinePage>(c => cancel => c.LinesAsync(query, cancel), MakerJson.LinePage);
        }

        private bool TryLangQuery(ApiTarget target, out string lang, out ApiResponse? problem)
        {
            lang = string.Empty;
            foreach (var pair in target.Query)
            {
                if (pair.Key != "lang")
                {
                    problem = UnknownQuery(pair.Key, "lang");
                    return false;
                }

                lang = pair.Value;
            }

            if (!MakerSegments.IsLang(lang))
            {
                problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, "lang is required: a language code such as th or pt-br");
                return false;
            }

            problem = null;
            return true;
        }

        private ApiResponse PlayRoute(ApiRequest r)
        {
            var jsonProblem = ReadJsonObject(r, out var body);
            if (jsonProblem != null)
            {
                return jsonProblem;
            }

            string? id = null, variant = null, lang = null;
            foreach (var member in body!)
            {
                var value = member.Value as string;
                switch (member.Key)
                {
                    case "id":
                        id = value;
                        break;
                    case "variant":
                        variant = value;
                        break;
                    case "lang":
                        lang = value;
                        break;
                    default:
                        return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "unknown member \"" + member.Key + "\"; the body is {\"id\",\"variant\",\"lang\"}");
                }
            }

            if (!MakerSegments.IsId(id))
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "\"id\" must be a line id ([A-Za-z0-9_.()-]{1,128})");
            }

            if (variant != "original" && variant != "dub")
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "\"variant\" must be \"original\" or \"dub\"");
            }

            if (!MakerSegments.IsLang(lang))
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "\"lang\" must be a language code such as th or pt-br");
            }

            return CallEditor(e => cancel => e.PlayAsync(id!, variant!, lang!, cancel));
        }

        private static bool TryStudioPlay(JsonObject body, out MakerStudioPlayRequest? request, out string problem)
        {
            request = null;
            problem = string.Empty;
            string? id = null, variant = null, lang = null, packId = null, expectedRevision = null;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "id":
                        id = member.Value as string;
                        break;
                    case "variant":
                        variant = member.Value as string;
                        break;
                    case "lang":
                        lang = member.Value as string;
                        break;
                    case "packId":
                        if (member.Value != null && !(member.Value is string))
                        {
                            problem = "\"packId\" must be a string or null";
                            return false;
                        }
                        packId = member.Value as string;
                        break;
                    case "expectedRevision":
                        if (member.Value != null && !(member.Value is string))
                        {
                            problem = "\"expectedRevision\" must be a string or null";
                            return false;
                        }
                        expectedRevision = member.Value as string;
                        break;
                    default:
                        problem = "unknown member \"" + member.Key + "\"; the body is {\"id\",\"variant\",\"lang\",\"packId\"?,\"expectedRevision\"?}";
                        return false;
                }
            }

            if (!MakerSegments.IsId(id))
            {
                problem = "\"id\" must be a line id ([A-Za-z0-9_.()-]{1,128})";
                return false;
            }

            if (variant != "original" && variant != "dub" && variant != "sub")
            {
                problem = "\"variant\" must be \"original\", \"dub\" or \"sub\"";
                return false;
            }

            if (variant == "sub" && packId is null)
            {
                problem = "variant 'sub' requires packId";
                return false;
            }

            if (!MakerSegments.IsLang(lang))
            {
                problem = "\"lang\" must be a language code such as th or pt-br";
                return false;
            }

            if (packId != null && !MakerSegments.IsPackId(packId))
            {
                problem = "\"packId\" must be a pack id ([a-z0-9][a-z0-9._-]{0,63})";
                return false;
            }

            if (expectedRevision != null)
            {
                if (packId is null)
                {
                    problem = "\"expectedRevision\" requires \"packId\"";
                    return false;
                }

                if (!ValidToken(expectedRevision, "expectedRevision", out problem))
                {
                    return false;
                }
            }

            request = new MakerStudioPlayRequest
            {
                Id = id!,
                Variant = variant!,
                Lang = lang!,
                PackId = packId,
                ExpectedRevision = expectedRevision,
            };
            return true;
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
                            return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "after must be a whole number of 0 or more");
                        }

                        break;
                    case "limit":
                        if (!int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                            || limit < 1 || limit > ApiDefaults.MaxEventsPerRead)
                        {
                            return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "limit must be 1-" + ApiDefaults.MaxEventsPerRead);
                        }

                        break;
                    case "wait":
                        if (!double.TryParse(pair.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out wait)
                            || double.IsNaN(wait) || wait < 0 || wait > _o.MaxWaitSeconds)
                        {
                            return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "wait must be a number of seconds from 0 to " + _o.MaxWaitSeconds.ToString(CultureInfo.InvariantCulture));
                        }

                        break;
                    default:
                        return UnknownQuery(pair.Key, "after, wait, limit");
                }
            }

            if (wait <= 0)
            {
                return ApiResponse.Json(200, _events.Ring.Read(after, limit).ToJson());
            }

            if (Interlocked.Increment(ref _waiters) > _o.MaxConcurrentWaits)
            {
                Interlocked.Decrement(ref _waiters);
                return ApiResponse.Error(429, MakerHttpCodes.RateLimited, "at most " + _o.MaxConcurrentWaits + " long-polls may wait at once", ApiResponse.RetryAfter(1));
            }

            try
            {
                return ApiResponse.Json(200, _events.Ring.Wait(after, limit, TimeSpan.FromSeconds(wait)).ToJson());
            }
            finally
            {
                Interlocked.Decrement(ref _waiters);
            }
        }

        private ApiResponse CallCatalog<T>(Func<IMakerCatalog, Func<CancellationToken, Task<MakerResult<T>>>> call, Func<T, JsonObject> shape) where T : class
        {
            IMakerCatalog? catalog;
            try
            {
                catalog = _o.Catalog();
            }
            catch (Exception ex)
            {
                Diagnose("reading the catalog slot failed: " + ex.GetType().Name + ": " + ex.Message);
                catalog = null;
            }

            return catalog is null
                ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "the game side of Pack Maker (the catalog) is not installed or not ready yet")
                : Call(call(catalog), shape);
        }

        private ApiResponse CallEditor(Func<IMakerEditor, Func<CancellationToken, Task<MakerResult<MakerSaved>>>> call)
        {
            IMakerEditor? editor;
            try
            {
                editor = _o.Editor();
            }
            catch (Exception ex)
            {
                Diagnose("reading the editor slot failed: " + ex.GetType().Name + ": " + ex.Message);
                editor = null;
            }

            return editor is null
                ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "the game side of Pack Maker (the editor) is not installed or not ready yet")
                : Call(call(editor), MakerJson.Saved);
        }

        /// <summary>
        /// Runs one backend call on its own thread and waits for it on this request thread's behalf: at most the
        /// main-thread timeout plus the grace, and no longer once the router stops. The backend starts off this
        /// thread, so synchronous work it does before returning its task cannot hold the request past either
        /// (DS-06 finding 5). On a timeout or stop the call is cancelled, the answer is 503 and its late result is
        /// dropped; calls still running (late ones included) are capped by <see cref="MakerRouterOptions.MaxBackendCalls"/>.
        /// </summary>
        private ApiResponse Call<T>(Func<CancellationToken, Task<MakerResult<T>>> start, Func<T, JsonObject> shape, int successStatus = 200) where T : class =>
            CallResponse(start, value => ApiResponse.Json(successStatus, ApiJson.Write(shape(value))));

        /// <summary>A Studio catalog read: like <see cref="Call{T}"/> with <see cref="MakerRouterOptions.CatalogTimeout"/> (a cold catalog build takes seconds).</summary>
        private ApiResponse CatalogCall<T>(Func<CancellationToken, Task<MakerResult<T>>> start, Func<T, JsonObject> shape) where T : class =>
            CallResponse(start, value => ApiResponse.Json(200, ApiJson.Write(shape(value))), CatalogLimit);

        private TimeSpan CatalogLimit
        {
            get
            {
                var normal = _o.BackendTimeout + _o.BackendGrace;
                return _o.CatalogTimeout > normal ? _o.CatalogTimeout : normal;
            }
        }

        private ApiResponse CallResponse<T>(Func<CancellationToken, Task<MakerResult<T>>> start, Func<T, ApiResponse> respond, TimeSpan? budget = null) where T : class
        {
            var response = CallResponseTraced(start, respond, budget, out var trace, out var failure);
            if (failure != null && _threadRequest != null)
            {
                LogFailure(_threadRequest, response.Status, failure, trace);
            }

            return response;
        }

        private ApiResponse CallResponseTraced<T>(Func<CancellationToken, Task<MakerResult<T>>> start, Func<T, ApiResponse> respond, TimeSpan? budget,
            out MakerRequestTrace? trace, out string? failure) where T : class
        {
            var limit = budget ?? (_o.BackendTimeout + _o.BackendGrace);
            trace = null;
            failure = null;
            if (Interlocked.Increment(ref _backendCalls) > Math.Max(1, _o.MaxBackendCalls))
            {
                Interlocked.Decrement(ref _backendCalls);
                failure = "refused without calling the game: " + Volatile.Read(ref _backendCalls) + " earlier backend calls still running (cap " + Math.Max(1, _o.MaxBackendCalls) + ")";
                return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "the game is still busy with earlier Pack Maker requests; try again");
            }

            // One small trace per backend call, always: a timeout names the phase that was still running.
            var callTrace = new MakerRequestTrace();
            trace = callTrace;
            _threadTrace = callTrace;
            var queuedAt = Stopwatch.GetTimestamp();

            CancellationTokenSource cancel;
            Task<MakerResult<T>> task;
            try
            {
                cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            }
            catch (Exception ex)
            {
                // Only when the router is being torn down (its stop source disposed).
                Interlocked.Decrement(ref _backendCalls);
                Diagnose("a backend call could not start: " + ex.GetType().Name + ": " + ex.Message);
                return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Pack Maker is closing");
            }

            try
            {
                // A dedicated thread, not the pool: request threads are pool threads waiting below, so a queued pool
                // item could starve behind them. The token source lives until the backend is done with it.
                var token = cancel.Token;
                task = Task.Factory.StartNew(
                    () =>
                    {
                        callTrace.Add("slot", MakerRequestTrace.MsSince(queuedAt));
                        var previous = MakerRequestTrace.Use(callTrace);
                        try
                        {
                            return start(token) ?? throw new MakerNoTaskException();
                        }
                        finally
                        {
                            MakerRequestTrace.Restore(previous);
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
                task.ContinueWith(
                    _ =>
                    {
                        cancel.Dispose();
                        Interlocked.Decrement(ref _backendCalls);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                cancel.Dispose();
                Interlocked.Decrement(ref _backendCalls);
                Diagnose("a backend call could not start: " + ex.GetType().Name + ": " + ex.Message);
                failure = "the backend call could not start: " + Describe(ex);
                return ApiResponse.Error(500, MakerErrorCodes.Internal, "the game side of Pack Maker failed (" + ex.GetType().Name + "); details are in the trace file");
            }

            bool finished;
            try
            {
                finished = task.Wait(WaitMs(limit), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                // The router stopped while the backend was still busy: answer now, drop the late result.
                Abandon(cancel, task);
                return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Pack Maker is closing");
            }
            catch (AggregateException)
            {
                finished = true; // faulted or cancelled: told apart below
            }

            if (!finished)
            {
                failure = "timeout after " + MakerRequestTrace.Format(limit.TotalMilliseconds) + " ms; still running: " + callTrace.Running() + "; finished: " + callTrace.Phases();
                Abandon(cancel, task);
                return ApiResponse.Error(
                    503,
                    MakerErrorCodes.Unavailable,
                    "the game did not answer within " + limit.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s (busy or paused); try again");
            }

            if (task.IsCanceled)
            {
                if (!IsStopping) failure = "the backend cancelled the call; phases: " + callTrace.Phases() + "; still running: " + callTrace.Running();
                return ApiResponse.Error(503, MakerErrorCodes.Unavailable, IsStopping ? "Pack Maker is closing" : "the game cancelled the request; try again");
            }

            if (task.IsFaulted)
            {
                var error = task.Exception?.GetBaseException();
                if (error is MakerNoTaskException)
                {
                    failure = "the backend returned no task";
                    return ApiResponse.Error(500, MakerErrorCodes.Internal, "the game side of Pack Maker returned no task");
                }

                failure = "backend exception " + (error is null ? "unknown error" : Describe(error)) + "; phases: " + callTrace.Phases();
                Diagnose("a backend call failed: " + (error is null ? "unknown error" : error.GetType().Name + ": " + error.Message));
                return ApiResponse.Error(500, MakerErrorCodes.Internal, "the game side of Pack Maker failed (" + (error?.GetType().Name ?? "error") + "); details are in the trace file");
            }

            var result = task.Result;
            if (result is null)
            {
                failure = "the backend returned no result";
                return ApiResponse.Error(500, MakerErrorCodes.Internal, "the game side of Pack Maker returned no result");
            }

            if (!result.Ok)
            {
                var status = MakerDefaults.StatusOf(result.Error);
                var code = status == 500 ? MakerErrorCodes.Internal : result.Error!;
                if (status == 500 && result.Error != MakerErrorCodes.Internal)
                {
                    Diagnose("a backend answered with the unknown error code " + result.Error);
                }

                if (status >= 500)
                {
                    failure = "the backend answered " + (result.Error ?? "?") + ": " + (result.Message ?? string.Empty) + "; phases: " + callTrace.Phases();
                }

                return MakerStudioJson.ErrorResponse(status, code, string.IsNullOrEmpty(result.Message) ? code : result.Message!, result.Details);
            }

            if (result.Value is null)
            {
                failure = "the backend returned no value";
                return ApiResponse.Error(500, MakerErrorCodes.Internal, "the game side of Pack Maker returned no value");
            }

            ApiResponse answer;
            using (new MakerRequestTrace.PhaseScope(callTrace, "respond"))
            {
                answer = respond(result.Value);
            }

            return answer;
        }

        /// <summary>Cancels a call the router gave up on; its result (or failure) is observed and dropped.</summary>
        private void Abandon(CancellationTokenSource cancel, Task task)
        {
            try
            {
                cancel.Cancel();
            }
            catch (Exception ex)
            {
                // Disposed when the backend finished in the same instant, or a cancellation callback threw.
                Diagnose("cancelling a backend call failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            Observe(task);
        }

        private static int WaitMs(TimeSpan limit) =>
            limit <= TimeSpan.Zero ? 0 : (int)Math.Min(int.MaxValue - 1, Math.Ceiling(limit.TotalMilliseconds));

        private ApiResponse? ReadJsonObject(ApiRequest r, out JsonObject? body)
        {
            body = null;
            if (r.Chunked)
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "send the body with Content-Length; chunked bodies are not accepted");
            }

            if (!IsMediaType(r.Headers.Get("Content-Type"), "application/json", jsonCharset: true))
            {
                return ApiResponse.Error(415, MakerErrorCodes.UnsupportedMedia, "the body must be Content-Type: application/json; charset=utf-8");
            }

            if (r.ContentLength is long declared && declared > MakerDefaults.MaxJsonBytes)
            {
                return ApiResponse.Error(413, MakerErrorCodes.TooLarge, "a JSON body may be at most " + MakerDefaults.MaxJsonBytes + " bytes");
            }

            var read = r.ReadBody(MakerDefaults.MaxJsonBytes);
            if (read.Status == ApiBodyStatus.TooLarge)
            {
                return ApiResponse.Error(413, MakerErrorCodes.TooLarge, "a JSON body may be at most " + MakerDefaults.MaxJsonBytes + " bytes");
            }

            if (read.Status == ApiBodyStatus.TimedOut)
            {
                return ApiResponse.Error(408, MakerErrorCodes.RequestTimeout, "the body could not be read: " + read.Problem);
            }

            if (read.Status == ApiBodyStatus.Failed)
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "the body could not be read: " + read.Problem);
            }

            if (read.Bytes.Length == 0)
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "this route needs a JSON body");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(read.Bytes);
            }
            catch (ArgumentException)
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "the body is not valid UTF-8");
            }

            if (!JsonReader.TryParse(text, out var value, out var problem))
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "the body is not strict JSON: " + problem);
            }

            if (!(value is JsonObject obj))
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "the body must be a JSON object");
            }

            body = obj;
            return null;
        }

        private ApiResponse? ReadWav(ApiRequest r, out byte[] wav)
        {
            wav = new byte[0];
            if (r.Chunked)
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "send the body with Content-Length; chunked bodies are not accepted");
            }

            if (!IsMediaType(r.Headers.Get("Content-Type"), WavMediaType, jsonCharset: false))
            {
                return ApiResponse.Error(415, MakerErrorCodes.UnsupportedMedia, "a dub must be sent as Content-Type: audio/wav");
            }

            if (r.ContentLength is long declared && declared > MakerDefaults.MaxWavBytes)
            {
                return ApiResponse.Error(413, MakerErrorCodes.TooLarge, "a dub may be at most 16 MiB");
            }

            var read = r.ReadBody(MakerDefaults.MaxWavBytes);
            if (read.Status == ApiBodyStatus.TooLarge)
            {
                return ApiResponse.Error(413, MakerErrorCodes.TooLarge, "a dub may be at most 16 MiB");
            }

            if (read.Status == ApiBodyStatus.TimedOut)
            {
                return ApiResponse.Error(408, MakerErrorCodes.RequestTimeout, "the body could not be read: " + read.Problem);
            }

            if (read.Status == ApiBodyStatus.Failed)
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "the body could not be read: " + read.Problem);
            }

            if (read.Bytes.Length == 0)
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "this route needs the dub as the body (to remove a dub, send DELETE)");
            }

            // Contract ruling: any body that is not a supported WAV (not RIFF/WAVE, broken header, wrong format) is
            // 415; only a clip over 60 s is 413 (16 MiB is checked above).
            switch (MakerWavCheck.Check(read.Bytes, out _, out var problem))
            {
                case MakerWavVerdict.Ok:
                    wav = read.Bytes;
                    return null;
                case MakerWavVerdict.TooLong:
                    return ApiResponse.Error(413, MakerErrorCodes.TooLarge, problem);
                default:
                    return ApiResponse.Error(415, MakerErrorCodes.UnsupportedMedia, problem);
            }
        }

        private static bool TryWorkflow(
            JsonObject body, string packId, string lang, string id,
            out MakerStudioWorkflowRequest? request, out string problem)
        {
            request = null;
            string? expected = null, operation = null, status = null;
            bool? starred = null;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "expectedRevision": expected = member.Value as string; break;
                    case "operationId": operation = member.Value as string; break;
                    case "status":
                        if (!(member.Value is string state) || (state != "todo" && state != "draft" && state != "done" && state != "needs-retake"))
                        { problem = "status must be todo, draft, done or needs-retake"; return false; }
                        status = state; break;
                    case "starred": if (member.Value is bool b) starred = b; else { problem = "starred must be true or false"; return false; } break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!ValidMutation(expected, operation, out problem)) return false;
            if (status is null && !starred.HasValue) { problem = "status or starred is required"; return false; }
            request = new MakerStudioWorkflowRequest
            {
                PackId = packId, Lang = lang, Id = id, ExpectedRevision = expected!, OperationId = operation!,
                Status = status, Starred = starred,
            };
            return true;
        }

        private static bool TrySubtitle(
            JsonObject body, string packId, string lang, string id,
            out MakerStudioSubtitleRequest? request, out string problem)
        {
            request = null;
            string? expected = null, operation = null, text = null;
            var hasText = false;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "expectedRevision": expected = member.Value as string; break;
                    case "operationId": operation = member.Value as string; break;
                    case "text":
                        hasText = true;
                        if (member.Value != null && !(member.Value is string)) { problem = "text must be a string or null"; return false; }
                        text = member.Value as string;
                        break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!ValidMutation(expected, operation, out problem)) return false;
            if (!hasText) { problem = "text is required (null removes the subtitle)"; return false; }
            request = new MakerStudioSubtitleRequest
            {
                PackId = packId, Lang = lang, Id = id, ExpectedRevision = expected!, OperationId = operation!, Text = text,
            };
            return true;
        }

        private static bool TryMutation(JsonObject body, out string? expected, out string? operation, out string problem)
        {
            expected = null; operation = null;
            foreach (var member in body)
            {
                if (member.Key == "expectedRevision") expected = member.Value as string;
                else if (member.Key == "operationId") operation = member.Value as string;
                else { problem = "unknown member \"" + member.Key + "\""; return false; }
            }

            return ValidMutation(expected, operation, out problem);
        }

        private static bool ValidMutation(string? expected, string? operation, out string problem)
        {
            if (!ValidToken(expected, "expectedRevision", out problem))
            {
                return false;
            }

            if (!MakerStudioPaths.IsOperationId(operation))
            {
                problem = "operationId must be 32 lowercase hexadecimal characters";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static bool ValidToken(string? value, string name, out string problem)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > 256)
            {
                problem = name + " is required and may be at most 256 characters";
                return false;
            }

            foreach (var c in value)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                      || c == '-' || c == '_' || c == '.' || c == ':'))
                {
                    problem = name + " is a malformed opaque token";
                    return false;
                }
            }

            problem = string.Empty;
            return true;
        }

        private static bool TryCreateTake(
            JsonObject body, string packId, string lang, string id,
            out MakerStudioCreateTakeRequest? request, out string problem)
        {
            request = null;
            problem = string.Empty;
            string? expected = null, operation = null, origin = null;
            MakerStudioConsent? consent = null;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "expectedRevision": expected = member.Value as string; break;
                    case "operationId": operation = member.Value as string; break;
                    case "origin":
                        if (!(member.Value is string o)) { problem = "origin must be a string"; return false; }
                        origin = o;
                        break;
                    case "consent":
                        if (!(member.Value is JsonObject c)) { problem = "consent must be an object"; return false; }
                        if (!TryConsent(c, out consent, out problem)) return false;
                        break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!ValidMutation(expected, operation, out problem)) return false;
            // v2.4: omit origin and consent together to inherit the pack's voice default; one without the other is an error.
            if ((origin is null) != (consent is null)) { problem = "send origin and consent together, or omit both to use the pack's voice default"; return false; }
            if (origin != null && !MakerTakeRules.ValidateOrigin(origin, out var originProblem)) { problem = originProblem!; return false; }
            request = new MakerStudioCreateTakeRequest
            {
                PackId = packId, Lang = lang, Id = id, ExpectedRevision = expected!, OperationId = operation!,
                Origin = origin, Consent = consent,
            };
            return true;
        }

        private static bool TryUpdateTake(
            JsonObject body, string packId, string lang, string id, string takeId,
            out MakerStudioUpdateTakeRequest? request, out string problem)
        {
            request = null;
            problem = string.Empty;
            string? expected = null, operation = null, origin = null;
            MakerStudioConsent? consent = null;
            double? trimStart = null, trimEnd = null;
            bool hasTrimStart = false, hasTrimEnd = false;
            bool? evenOut = null;
            var follow = false;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "expectedRevision": expected = member.Value as string; break;
                    case "operationId": operation = member.Value as string; break;
                    case "origin":
                        if (!(member.Value is string o)) { problem = "origin must be own, ai-assisted or ai-generated"; return false; }
                        if (!MakerTakeRules.ValidateOrigin(o, out var originProblem)) { problem = originProblem!; return false; }
                        origin = o;
                        break;
                    case "consent":
                        if (!(member.Value is JsonObject c)) { problem = "consent must be an object"; return false; }
                        if (!TryConsent(c, out consent, out problem)) return false;
                        break;
                    case "trimStartSec":
                        hasTrimStart = true;
                        if (!TryOptionalSeconds(member.Value, "trimStartSec", out trimStart, out problem)) return false;
                        break;
                    case "trimEndSec":
                        hasTrimEnd = true;
                        if (!TryOptionalSeconds(member.Value, "trimEndSec", out trimEnd, out problem)) return false;
                        break;
                    case "evenOutVolume":
                        if (member.Value is bool b) evenOut = b;
                        else { problem = "evenOutVolume must be true or false"; return false; }
                        break;
                    case "followPackDefault": // v2.5.1
                        if (!(member.Value is bool yes && yes)) { problem = "followPackDefault must be true"; return false; }
                        follow = true;
                        break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!ValidMutation(expected, operation, out problem)) return false;
            if (origin is null && consent is null && !hasTrimStart && !hasTrimEnd && !evenOut.HasValue && !follow)
            {
                problem = "at least one take field is required";
                return false;
            }

            if (follow && (origin != null || consent != null))
            {
                problem = MakerTakeStore.FollowWithOwnConsent;
                return false;
            }

            request = new MakerStudioUpdateTakeRequest
            {
                PackId = packId, Lang = lang, Id = id, TakeId = takeId,
                ExpectedRevision = expected!, OperationId = operation!,
                Origin = origin, Consent = consent,
                HasTrimStartSec = hasTrimStart, TrimStartSec = trimStart,
                HasTrimEndSec = hasTrimEnd, TrimEndSec = trimEnd,
                EvenOutVolume = evenOut, FollowPackDefault = follow,
            };
            return true;
        }

        private static bool TryFavourite(
            JsonObject body, string packId, string lang, string id,
            out MakerStudioFavouriteRequest? request, out string problem)
        {
            request = null;
            string? expected = null, operation = null, takeId = null;
            var hasTakeId = false;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "expectedRevision": expected = member.Value as string; break;
                    case "operationId": operation = member.Value as string; break;
                    case "takeId":
                        hasTakeId = true;
                        if (member.Value != null && !(member.Value is string s && MakerStudioPaths.IsTakeId(s)))
                        {
                            problem = "takeId must be 32 lowercase hexadecimal characters or null";
                            return false;
                        }
                        takeId = member.Value as string;
                        break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!ValidMutation(expected, operation, out problem)) return false;
            if (!hasTakeId) { problem = "takeId is required (null clears the favourite take)"; return false; }
            request = new MakerStudioFavouriteRequest
            {
                PackId = packId, Lang = lang, Id = id, ExpectedRevision = expected!, OperationId = operation!, TakeId = takeId,
            };
            return true;
        }

        private static bool TryPublish(
            JsonObject body, string packId, string lang, string id,
            out MakerStudioPublishRequest? request, out string problem)
        {
            request = null;
            string? expected = null, expectedClip = null, operation = null, takeId = null;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "expectedRevision": expected = member.Value as string; break;
                    case "expectedClipRevision": expectedClip = member.Value as string; break;
                    case "operationId": operation = member.Value as string; break;
                    case "takeId": takeId = member.Value as string; break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!ValidMutation(expected, operation, out problem)
                || !ValidToken(expectedClip, "expectedClipRevision", out problem))
            {
                return false;
            }

            if (!MakerStudioPaths.IsTakeId(takeId))
            {
                problem = "takeId must be 32 lowercase hexadecimal characters";
                return false;
            }

            request = new MakerStudioPublishRequest
            {
                PackId = packId, Lang = lang, Id = id, TakeId = takeId!,
                ExpectedRevision = expected!, ExpectedClipRevision = expectedClip!, OperationId = operation!,
            };
            return true;
        }

        private static bool TryUnpublish(
            JsonObject body, string packId, string lang, string id,
            out MakerStudioUnpublishRequest? request, out string problem)
        {
            request = null;
            string? expected = null, expectedClip = null, operation = null;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "expectedRevision": expected = member.Value as string; break;
                    case "expectedClipRevision": expectedClip = member.Value as string; break;
                    case "operationId": operation = member.Value as string; break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!ValidMutation(expected, operation, out problem)
                || !ValidToken(expectedClip, "expectedClipRevision", out problem))
            {
                return false;
            }

            request = new MakerStudioUnpublishRequest
            {
                PackId = packId, Lang = lang, Id = id,
                ExpectedRevision = expected!, ExpectedClipRevision = expectedClip!, OperationId = operation!,
            };
            return true;
        }

        private static bool TryDeleteTakeQuery(
            ApiTarget target, string packId, string lang, string id, string takeId,
            out MakerStudioDeleteTakeRequest? request, out ApiResponse? problem)
        {
            request = null;
            string? expected = null, operation = null;
            foreach (var pair in target.Query)
            {
                if (pair.Key == "expectedRevision") expected = pair.Value;
                else if (pair.Key == "operationId") operation = pair.Value;
                else { problem = UnknownQuery(pair.Key, "expectedRevision, operationId"); return false; }
            }

            if (!ValidMutation(expected, operation, out var mutProblem))
            {
                problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, mutProblem);
                return false;
            }

            request = new MakerStudioDeleteTakeRequest
            {
                PackId = packId, Lang = lang, Id = id, TakeId = takeId,
                ExpectedRevision = expected!, OperationId = operation!,
            };
            problem = null;
            return true;
        }

        private static bool TryTakeAudioGetQuery(ApiTarget target, out string kind, out ApiResponse? problem)
        {
            kind = string.Empty;
            foreach (var pair in target.Query)
            {
                if (pair.Key != "kind")
                {
                    problem = UnknownQuery(pair.Key, "kind");
                    return false;
                }

                kind = pair.Value;
            }

            if (!MakerStudioPaths.IsTakeAudioKind(kind))
            {
                problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, "kind is required and must be source or render");
                return false;
            }

            problem = null;
            return true;
        }

        private static bool TryTakeAudioPutQuery(
            ApiTarget target, string packId, string lang, string id, string takeId,
            out MakerStudioTakeAudioRequest? request, out ApiResponse? problem)
        {
            request = null;
            string? kind = null, expected = null, operation = null;
            foreach (var pair in target.Query)
            {
                if (pair.Key == "kind") kind = pair.Value;
                else if (pair.Key == "expectedRevision") expected = pair.Value;
                else if (pair.Key == "operationId") operation = pair.Value;
                else { problem = UnknownQuery(pair.Key, "kind, expectedRevision, operationId"); return false; }
            }

            if (!MakerStudioPaths.IsTakeAudioKind(kind))
            {
                problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, "kind is required and must be source or render");
                return false;
            }

            if (!ValidMutation(expected, operation, out var mutProblem))
            {
                problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, mutProblem);
                return false;
            }

            request = new MakerStudioTakeAudioRequest
            {
                PackId = packId, Lang = lang, Id = id, TakeId = takeId, Kind = kind!,
                ExpectedRevision = expected!, OperationId = operation!,
            };
            problem = null;
            return true;
        }

        private static bool TryConsent(JsonObject obj, out MakerStudioConsent? consent, out string problem)
        {
            consent = null;
            bool confirmed = false, hasConfirmed = false;
            string? version = null, confirmedAt = null;
            foreach (var member in obj)
            {
                switch (member.Key)
                {
                    case "confirmed":
                        if (!(member.Value is bool b)) { problem = "consent.confirmed must be true or false"; return false; }
                        confirmed = b;
                        hasConfirmed = true;
                        break;
                    case "statementVersion":
                        if (!(member.Value is string v)) { problem = "consent.statementVersion must be a string"; return false; }
                        version = v;
                        break;
                    case "confirmedAt":
                        if (member.Value != null && !(member.Value is string)) { problem = "consent.confirmedAt must be a string or null"; return false; }
                        confirmedAt = member.Value as string;
                        break;
                    default:
                        problem = "unknown consent member \"" + member.Key + "\"";
                        return false;
                }
            }

            if (!hasConfirmed || version is null)
            {
                problem = "consent requires confirmed and statementVersion";
                return false;
            }

            var candidate = new MakerStudioConsent
            {
                Confirmed = confirmed,
                StatementVersion = version,
                ConfirmedAt = confirmedAt,
            };
            if (!MakerTakeRules.ValidateConsent(candidate, out var consentProblem))
            {
                problem = consentProblem!;
                return false;
            }

            consent = candidate;
            problem = string.Empty;
            return true;
        }

        private static bool TryOptionalSeconds(object? node, string name, out double? seconds, out string problem)
        {
            seconds = null;
            if (node is null)
            {
                problem = string.Empty;
                return true;
            }

            double val;
            if (node is double d && !double.IsNaN(d) && !double.IsInfinity(d)) val = d;
            else if (node is long l) val = l;
            else { problem = name + " must be a number or null"; return false; }

            if (val < 0d || val > MakerDefaults.MaxWavSeconds)
            {
                problem = name + " must be between 0 and " + MakerDefaults.MaxWavSeconds;
                return false;
            }

            seconds = val;
            problem = string.Empty;
            return true;
        }

        private static bool TryGlossary(
            JsonObject body, string packId, string lang,
            out MakerStudioGlossaryRequest? request, out string problem)
        {
            request = null;
            string? expected = null, operation = null;
            IList<object?>? nodes = null;
            foreach (var member in body)
            {
                if (member.Key == "expectedRevision") expected = member.Value as string;
                else if (member.Key == "operationId") operation = member.Value as string;
                else if (member.Key == "entries" && member.Value is IList<object?> list) nodes = list;
                else { problem = member.Key == "entries" ? "entries must be an array" : "unknown member \"" + member.Key + "\""; return false; }
            }

            if (!ValidMutation(expected, operation, out problem)) return false;
            if (nodes is null) { problem = "entries is required"; return false; }
            var entries = new List<MakerStudioGlossaryEntry>();
            foreach (var node in nodes)
            {
                if (!(node is JsonObject obj)) { problem = "each glossary entry must be an object"; return false; }
                string? entryId = null, ja = null, en = null, targetText = null, note = null;
                foreach (var member in obj)
                {
                    if (member.Value != null && !(member.Value is string)) { problem = member.Key + " must be text or null"; return false; }
                    switch (member.Key)
                    {
                        case "id": entryId = member.Value as string; break;
                        case "sourceJa": ja = member.Value as string; break;
                        case "sourceEn": en = member.Value as string; break;
                        case "target": targetText = member.Value as string; break;
                        case "note": note = member.Value as string; break;
                        default: problem = "unknown glossary member \"" + member.Key + "\""; return false;
                    }
                }

                if (entryId is null || targetText is null) { problem = "each glossary entry needs id and target"; return false; }
                entries.Add(new MakerStudioGlossaryEntry { Id = entryId, SourceJa = ja, SourceEn = en, Target = targetText, Note = note });
            }

            var entryProblem = Studio.MakerGlossaryStore.Validate(entries);
            if (entryProblem != null) { problem = entryProblem; return false; }

            request = new MakerStudioGlossaryRequest
            {
                PackId = packId, Lang = lang, ExpectedRevision = expected!, OperationId = operation!, Entries = entries,
            };
            return true;
        }

        private static bool TryPage(ApiTarget target, int max, out int offset, out int limit, out ApiResponse? problem)
        {
            offset = 0; limit = max;
            foreach (var pair in target.Query)
            {
                if (pair.Key == "offset")
                {
                    if (!int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
                    { problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, "offset must be a whole number of 0 or more"); return false; }
                }
                else if (pair.Key == "limit")
                {
                    if (!int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > max)
                    { problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, "limit must be 1-" + max); return false; }
                }
                else { problem = UnknownQuery(pair.Key, "offset, limit"); return false; }
            }

            problem = null;
            return true;
        }

        private static bool TrySubtitleText(JsonObject body, out string text, out string problem)
        {
            text = string.Empty;
            foreach (var member in body)
            {
                if (member.Key != "text")
                {
                    problem = "unknown member \"" + member.Key + "\"; the body is {\"text\":\"...\"}";
                    return false;
                }
            }

            if (!body.TryGetValue("text", out var value) || !(value is string s))
            {
                problem = "\"text\" must be a string (to remove a subtitle, send DELETE)";
                return false;
            }

            var chars = CodePoints(s);
            if (chars < MakerDefaults.MinSubtitleChars || chars > MakerDefaults.MaxSubtitleChars)
            {
                problem = "\"text\" must be " + MakerDefaults.MinSubtitleChars + "-" + MakerDefaults.MaxSubtitleChars + " characters (this one is " + chars + ")";
                return false;
            }

            text = s;
            problem = string.Empty;
            return true;
        }

        private static int CodePoints(string s)
        {
            var n = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                }

                n++;
            }

            return n;
        }

        /// <summary>The media type (case-insensitive); for JSON at most a <c>charset=utf-8</c> parameter, for others any parameters.</summary>
        private static bool IsMediaType(string? value, string expected, bool jsonCharset)
        {
            if (value is null)
            {
                return false;
            }

            var semicolon = value.IndexOf(';');
            var media = (semicolon >= 0 ? value.Substring(0, semicolon) : value).Trim();
            if (!string.Equals(media, expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (semicolon < 0 || !jsonCharset)
            {
                return true;
            }

            var parameter = value.Substring(semicolon + 1).Trim();
            return parameter.Length == 0
                || string.Equals(parameter.Replace(" ", string.Empty), "charset=utf-8", StringComparison.OrdinalIgnoreCase);
        }

        private bool Take(ApiRateClass rateClass, out ApiResponse? limited)
        {
            if (_limiter.TryTake(rateClass, out var retry))
            {
                limited = null;
                return true;
            }

            limited = ApiResponse.Error(429, MakerHttpCodes.RateLimited, "too many Pack Maker requests; slow down", ApiResponse.RetryAfter(retry));
            return false;
        }

        private static ApiResponse? RefuseBody(ApiRequest r) =>
            r.Chunked || (r.ContentLength is long n && n > 0)
                ? ApiResponse.Error(400, MakerErrorCodes.BadRequest, "a " + r.Method + " request on this route has no body")
                : null;

        private static ApiResponse? NoQuery(ApiTarget target) =>
            target.Query.Count == 0 ? null : UnknownQuery(target.Query[0].Key, "none");

        private static ApiResponse UnknownQuery(string name, string accepted) =>
            ApiResponse.Error(400, MakerErrorCodes.BadRequest, "unknown query parameter \"" + name + "\" (accepted: " + accepted + ")");

        private static ApiResponse BadSegment(string what, string rule) =>
            ApiResponse.Error(400, MakerErrorCodes.BadRequest, what + " in the path must match " + rule);

        // The contract has no 405 code: a known path with another method is "no route" for that method.
        private static ApiResponse NoRoute(string method, string path, string allowed) =>
            ApiResponse.Error(404, MakerErrorCodes.NotFound, "no route " + method + " " + path + " (use " + allowed + ")");

        private static void Observe(Task task) =>
            task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

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

        /// <summary>A backend returned a null task (500 "returned no task").</summary>
        private sealed class MakerNoTaskException : Exception
        {
        }

        private void Diagnose(string message)
        {
            try
            {
                if (_o.Diagnostics != null)
                {
                    _o.Diagnostics(message);
                    return;
                }

                ConsoleEvents.File(ApiAuditLog.Sys, "diagnostic", ("message", "maker: " + message));
            }
            catch
            {
                // Diagnostics must never turn a handled failure into a crash of the request thread.
            }
        }
    }
}
