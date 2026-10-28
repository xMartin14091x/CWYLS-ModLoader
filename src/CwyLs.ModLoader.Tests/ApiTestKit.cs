using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>S-API test helpers: a manual clock, request builders, a fake runner, a router with its own registry and services.</summary>
    internal static class ApiTestKit
    {
        internal const int Port = 7870;

        internal static string Authority(int port = Port) => ApiDefaults.HostAuthority(port);

        internal static string AuthValue(ApiToken key) => ApiToken.Scheme + " " + key.Reveal();

        internal static ApiRequest Get(string target, ApiToken? key, int port = Port, params (string Name, string Value)[] extra) =>
            Build("GET", target, key, null, port, extra);

        internal static ApiRequest Post(string target, ApiToken? key, string? body, int port = Port, params (string Name, string Value)[] extra) =>
            Build("POST", target, key, body, port, extra);

        internal static ApiRequest Build(string method, string target, ApiToken? key, string? body, int port, params (string Name, string Value)[] extra)
        {
            var headers = new ApiHeaders();
            var hostGiven = false;
            foreach (var (name, value) in extra)
            {
                hostGiven |= string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase);
            }

            if (!hostGiven)
            {
                headers.Add("Host", Authority(port));
            }

            if (key != null)
            {
                headers.Add("Authorization", AuthValue(key));
            }

            var bytes = body is null ? null : Encoding.UTF8.GetBytes(body);
            if (bytes != null)
            {
                headers.Add("Content-Type", "application/json");
            }

            foreach (var (name, value) in extra)
            {
                headers.Add(name, value);
            }

            return new ApiRequest(
                "test",
                method,
                target,
                headers,
                bytes?.Length,
                headers.Has("Transfer-Encoding"),
                bytes is null ? null : max => bytes.Length > max ? ApiBody.TooLarge() : ApiBody.Of(bytes));
        }

        internal static JsonObject Json(ApiResponse response) => JsonReader.ParseObject(response.Body);

        internal static string ErrorCode(ApiResponse response) =>
            (string)((JsonObject)Json(response)["error"]!)["code"]!;

        internal static string? Header(ApiResponse response, string name)
        {
            foreach (var pair in response.Headers)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        /// <summary>A free loopback port right now (bind to 0, read, release).</summary>
        internal static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    internal sealed class ManualApiClock : IApiClock
    {
        private double _seconds = 1000;

        public double MonotonicSeconds => Volatile.Read(ref _seconds);

        public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 10, 2, 21, 30, 0, TimeSpan.FromHours(7));

        public void Advance(double seconds)
        {
            Volatile.Write(ref _seconds, _seconds + seconds);
            Now = Now.AddSeconds(seconds);
        }
    }

    /// <summary>A runner that records requests and answers with a fixed result, or waits until released.</summary>
    internal sealed class FakeRunner : IActionRunner
    {
        private readonly object _gate = new object();
        private readonly List<ActionRequest> _requests = new List<ActionRequest>();
        private readonly TaskCompletionSource<bool>? _release;

        internal FakeRunner(ActionResult? result = null, bool hold = false)
        {
            Result = result ?? ActionResult.Ok("done", ActionPath.Semantic, new Dictionary<string, object?> { ["n"] = 1L });
            _release = hold ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        }

        internal ActionResult Result { get; }

        internal IReadOnlyList<ActionRequest> Requests
        {
            get { lock (_gate) { return _requests.ToArray(); } }
        }

        internal void Release() => _release?.TrySetResult(true);

        public async Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _requests.Add(request);
            }

            if (_release != null)
            {
                await _release.Task.ConfigureAwait(false);
            }

            return Result;
        }
    }

    /// <summary>A confirmation gate that approves everything (stands in for E-WRITES' dialog).</summary>
    internal sealed class ApprovingGate : IConfirmationGate
    {
        public Task<ConfirmationResult> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ConfirmationResult.Approved());
    }

    /// <summary>A router with its own registry, services, clock and audit sink.</summary>
    internal sealed class RouterRig
    {
        internal RouterRig(int port = ApiTestKit.Port, bool allowActions = false, ApiRateLimiter? limiter = null)
        {
            Clock = new ManualApiClock();
            Key = ApiToken.Create();
            Registry = new ActionRegistry();
            Services = new ActionServices();
            AllowActions = allowActions;
            Audit = new ApiAuditLog(Clock, () => false, (record, channel, quiet) =>
            {
                lock (Records)
                {
                    Records.Add((record, channel));
                }
            });
            Router = new ApiRouter(Key, new ApiRouterOptions
            {
                Port = port,
                Clock = Clock,
                Registry = Registry,
                Services = Services,
                AllowActions = () => AllowActions,
                Audit = Audit,
                Limiter = limiter ?? new ApiRateLimiter(Clock, 1000, 1000, 1000),
                Facts = new ApiInfoFacts { LoaderVersion = "0.9.0", ModApiVersion = "0.9.0", GameVersion = "1.17.3" },
                Diagnostics = message => { lock (Diagnostics) { Diagnostics.Add(message); } },
            });
        }

        internal ManualApiClock Clock { get; }

        internal ApiToken Key { get; }

        internal ActionRegistry Registry { get; }

        internal ActionServices Services { get; }

        internal bool AllowActions { get; set; }

        internal ApiAuditLog Audit { get; }

        internal ApiRouter Router { get; }

        internal List<(ApiAuditRecord Record, ApiAuditChannel Channel)> Records { get; } = new List<(ApiAuditRecord, ApiAuditChannel)>();

        internal List<string> Diagnostics { get; } = new List<string>();

        internal ApiResponse Send(ApiRequest request) => Router.Handle(request);

        internal void Register(ActionDescriptor descriptor, ActionHandler? handler = null) =>
            Registry.Register(ActionRegistry.FrameworkOwner, descriptor, handler ?? (_ => ActionResult.Ok("ran")));
    }
}
