using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>
    /// The fixed facts and default limits of the local game API. Everything that is part of the wire
    /// contract (address, path prefix, schema numbers) is a constant here so the plugin, the tests
    /// and the README cannot drift apart.
    /// </summary>
    public static class ApiDefaults
    {
        /// <summary>
        /// The only address the API ever listens on: the literal IPv4 loopback. Never
        /// <c>localhost</c> (Mono resolves a host name and falls back to every interface when the
        /// lookup fails), never <c>*</c>, <c>+</c> or <c>0.0.0.0</c>.
        /// </summary>
        public const string LoopbackAddress = "127.0.0.1";

        /// <summary>The default port.</summary>
        public const int DefaultPort = 7870;

        /// <summary>Lowest accepted port (no privileged ports).</summary>
        public const int MinPort = 1024;

        /// <summary>Highest accepted port.</summary>
        public const int MaxPort = 65535;

        /// <summary>The versioned path prefix every route starts with.</summary>
        public const string PathPrefix = "/v1";

        /// <summary>The wire contract version the routes under <see cref="PathPrefix"/> implement.</summary>
        public const int WireVersion = 1;

        /// <summary>The schema number of the <c>/v1/state</c> document. Bumped only when a member is removed or changes meaning.</summary>
        public const int StateSchema = 1;

        /// <summary>The schema number of the token file.</summary>
        public const int TokenFileSchema = 1;

        /// <summary>Largest request body accepted, in bytes (64 KiB).</summary>
        public const int MaxBodyBytes = 64 * 1024;

        /// <summary>Largest request head (request line + headers) the TCP fallback reads, in bytes.</summary>
        public const int MaxHeadBytes = 16 * 1024;

        /// <summary>Longest a long-poll (<c>wait</c>) may last, in seconds; below common 30 s client timeouts.</summary>
        public const double MaxWaitSeconds = 25;

        /// <summary>How long an action request waits for the main thread (2 s wall clock).</summary>
        public static readonly TimeSpan ActionMainThreadTimeout = TimeSpan.FromSeconds(2);

        /// <summary>Longest the API keeps an action running before it stops waiting (covers a 30 s in-game confirmation).</summary>
        public static readonly TimeSpan ActionHardLimit = TimeSpan.FromSeconds(120);

        /// <summary>Events kept in the ring buffer.</summary>
        public const int EventCapacity = 1024;

        /// <summary>Most events one <c>/v1/events</c> response carries.</summary>
        public const int MaxEventsPerRead = 500;

        /// <summary>Events one <c>/v1/events</c> response carries when <c>limit</c> is not given.</summary>
        public const int DefaultEventsPerRead = 100;

        /// <summary>Requests served at the same time; more are answered 503 at once.</summary>
        public const int MaxConcurrentRequests = 16;

        /// <summary>Long-polls (<c>wait</c> &gt; 0) waiting at the same time; more are answered 429.</summary>
        public const int MaxConcurrentWaits = 4;

        /// <summary>Actions started through the API and not finished yet; more are answered 429.</summary>
        public const int MaxActionsInFlight = 8;

        /// <summary>Action handles remembered (finished ones are dropped oldest first).</summary>
        public const int HandleCapacity = 256;

        /// <summary>How long a finished action's handle can still be read.</summary>
        public static readonly TimeSpan HandleLifetime = TimeSpan.FromMinutes(10);

        /// <summary>Idempotency keys remembered.</summary>
        public const int IdempotencyCapacity = 256;

        /// <summary>How long an idempotency key maps to its handle.</summary>
        public static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromMinutes(10);

        /// <summary>Per-frame budget of the API's own main-thread work (state refresh), in milliseconds.</summary>
        public const double FrameBudgetMs = 4;

        /// <summary>Folder under <c>BepInEx/data</c> that holds the token file.</summary>
        public const string TokenFolderName = "api";

        /// <summary>The token file's name.</summary>
        public const string TokenFileName = "token.json";

        /// <summary>The request header that names the calling program in the audit line (optional).</summary>
        public const string ClientHeader = "X-CWY-Client";

        /// <summary>The request header that carries an idempotency key on <c>POST /v1/actions/&lt;name&gt;</c> (optional).</summary>
        public const string IdempotencyHeader = "Idempotency-Key";

        /// <summary>The base address for a port: <c>http://127.0.0.1:7870/</c>.</summary>
        /// <param name="port">The port.</param>
        public static string BaseUrl(int port) =>
            "http://" + LoopbackAddress + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";

        /// <summary>The only <c>Host</c> header value accepted for a port: <c>127.0.0.1:7870</c>.</summary>
        /// <param name="port">The port.</param>
        public static string HostAuthority(int port) =>
            LoopbackAddress + ":" + port.ToString(CultureInfo.InvariantCulture);

        /// <summary>True when <paramref name="port"/> is in <see cref="MinPort"/>..<see cref="MaxPort"/>.</summary>
        /// <param name="port">A candidate port.</param>
        public static bool IsValidPort(int port) => port >= MinPort && port <= MaxPort;
    }

    /// <summary>Which listener the API uses (<c>[Api] Transport</c>).</summary>
    public enum ApiTransportMode
    {
        /// <summary>HttpListener first; the TCP fallback when HttpListener fails for any reason other than a taken port.</summary>
        Auto = 0,

        /// <summary>HttpListener only.</summary>
        HttpListener = 1,

        /// <summary>The small TCP HTTP/1.1 server only.</summary>
        Tcp = 2,
    }

    /// <summary>
    /// Live switches other framework parts read without referencing the API plugin. The plugin
    /// sets them from its config; the action runner fills
    /// <c>ActionGateSettings.AllowApiActions</c> from <see cref="AllowActions"/>. Thread-safe.
    /// </summary>
    public static class ApiSwitches
    {
        private static int _allowActions;

        /// <summary>
        /// <c>[Api] AllowActions</c>: whether outside programs may run Act- and Write-tier actions
        /// (reads are always allowed once the API is on). Default false.
        /// </summary>
        public static bool AllowActions
        {
            get => System.Threading.Volatile.Read(ref _allowActions) == 1;
            set => System.Threading.Volatile.Write(ref _allowActions, value ? 1 : 0);
        }
    }
}
