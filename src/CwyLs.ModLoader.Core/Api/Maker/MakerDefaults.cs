using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>
    /// The fixed facts and limits of the Pack Maker surface (ticket DS-01,
    /// <c>research/2026-11-07/dub-studio/CONTRACT.md</c>): paths, body caps, the backend wait, and the
    /// error code to HTTP status table. Constants here so the router, the plugin and the tests cannot drift.
    /// </summary>
    public static class MakerDefaults
    {
        /// <summary>The web app's path (<c>GET /maker</c> redirects to <c>/maker/</c>).</summary>
        public const string WebPath = "/maker";

        /// <summary>The maker API's path prefix; every route is below it.</summary>
        public const string ApiPath = ApiDefaults.PathPrefix + "/maker";

        /// <summary>The <c>name</c> of the meta element that carries the maker token in <c>index.html</c>.</summary>
        public const string TokenMetaName = "cwyls-maker-token";

        /// <summary>The folder under the loader's plugin folder that holds the web app.</summary>
        public const string WebFolderName = "maker";

        /// <summary>Largest JSON body accepted (64 KiB).</summary>
        public const int MaxJsonBytes = 64 * 1024;

        /// <summary>Largest voice body accepted (16 MiB).</summary>
        public const int MaxWavBytes = 16 * 1024 * 1024;

        /// <summary>Longest voice clip accepted, in seconds.</summary>
        public const double MaxWavSeconds = 60;

        /// <summary>Lowest sample rate of a voice clip.</summary>
        public const int MinWavSampleRate = 8000;

        /// <summary>Highest sample rate of a voice clip.</summary>
        public const int MaxWavSampleRate = 48000;

        /// <summary>Shortest subtitle text, in characters (code points).</summary>
        public const int MinSubtitleChars = 1;

        /// <summary>Longest subtitle text, in characters (code points).</summary>
        public const int MaxSubtitleChars = 2000;

        /// <summary>Longest search text (<c>q</c>) on <c>GET /v1/maker/lines</c>, in characters.</summary>
        public const int MaxSearchChars = 200;

        /// <summary>Largest static file served from the web folder (8 MiB).</summary>
        public const int MaxStaticFileBytes = 8 * 1024 * 1024;

        /// <summary>Lines one <c>GET /v1/maker/lines</c> returns when <c>limit</c> is not given.</summary>
        public const int DefaultLinesLimit = 100;

        /// <summary>Most lines one <c>GET /v1/maker/lines</c> returns.</summary>
        public const int MaxLinesLimit = 500;

        /// <summary>Events kept in the maker ring.</summary>
        public const int EventCapacity = ApiDefaults.EventCapacity;

        /// <summary>Read rate (status, packs, lines) per second; the burst is twice this.</summary>
        public const double ReadsPerSecond = 20;

        /// <summary>Event-poll rate per second; the burst is twice this.</summary>
        public const double EventsPerSecond = 10;

        /// <summary>Write and play rate per second; the burst is twice this.</summary>
        public const double WritesPerSecond = 10;

        /// <summary>The audit line's client name when the request names none (<c>X-CWY-Client</c>).</summary>
        public const string ClientName = "maker";

        /// <summary>
        /// How long a backend gets for the main-thread part of a request: the dispatcher's 2 s. The
        /// router waits this plus <see cref="BackendGrace"/>, so a backend that gave up on the main thread
        /// can still answer with its own error, then answers 503 <c>unavailable</c> itself.
        /// </summary>
        public static readonly TimeSpan BackendTimeout = ApiDefaults.ActionMainThreadTimeout;

        /// <summary>Extra wait on top of <see cref="BackendTimeout"/> for the backend's own answer.</summary>
        public static readonly TimeSpan BackendGrace = TimeSpan.FromSeconds(1);

        /// <summary>
        /// The whole wait of a Studio catalog read (packs, lines, line, groups): 12 s, so a cold catalog build in a game that
        /// runs in the background still answers, and still under the page's own 15 s request timeout.
        /// </summary>
        public static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(12);

        /// <summary>Most entries one <c>POST /v1/maker/studio/client-log</c> may carry.</summary>
        public const int MaxClientLogEntries = 100;

        /// <summary>The clean page address: <c>http://127.0.0.1:7870/maker/</c>. Launch links append a one-time nonce.</summary>
        /// <param name="port">The port.</param>
        public static string WebUrl(int port) => ApiDefaults.BaseUrl(port) + WebPath.Substring(1) + "/";

        /// <summary>The two <c>Host</c> values the maker routes accept: <c>127.0.0.1:&lt;port&gt;</c> and <c>localhost:&lt;port&gt;</c>.</summary>
        /// <param name="host">The <c>Host</c> header, or null.</param>
        /// <param name="port">The port.</param>
        public static bool IsAcceptedHost(string? host, int port)
        {
            var p = port.ToString(CultureInfo.InvariantCulture);
            return string.Equals(host, ApiDefaults.LoopbackAddress + ":" + p, StringComparison.Ordinal)
                || string.Equals(host, "localhost:" + p, StringComparison.Ordinal);
        }

        /// <summary>The two <c>Origin</c> values the maker routes accept: <c>http://127.0.0.1:&lt;port&gt;</c> and <c>http://localhost:&lt;port&gt;</c>.</summary>
        /// <param name="origin">The <c>Origin</c> header.</param>
        /// <param name="port">The port.</param>
        public static bool IsAcceptedOrigin(string? origin, int port)
        {
            var p = port.ToString(CultureInfo.InvariantCulture);
            return string.Equals(origin, "http://" + ApiDefaults.LoopbackAddress + ":" + p, StringComparison.Ordinal)
                || string.Equals(origin, "http://localhost:" + p, StringComparison.Ordinal);
        }

        /// <summary>True for the <c>Sec-Fetch-Site</c> values a page of this origin sends: <c>same-origin</c>, or <c>none</c> (typed or opened address).</summary>
        /// <param name="site">The header value.</param>
        public static bool IsAcceptedFetchSite(string? site) =>
            string.Equals(site, "same-origin", StringComparison.Ordinal) || string.Equals(site, "none", StringComparison.Ordinal);

        /// <summary>
        /// True when a request target belongs to Pack Maker: <c>/maker</c>, anything below <c>/maker/</c>,
        /// <c>/v1/maker</c> or anything below <c>/v1/maker/</c> (the query is ignored).
        /// </summary>
        /// <param name="target">The request target as sent.</param>
        public static bool Owns(string? target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return false;
            }

            var q = target!.IndexOf('?');
            var path = q >= 0 ? target.Substring(0, q) : target;
            return IsAtOrBelow(path, WebPath) || IsAtOrBelow(path, ApiPath);
        }

        /// <summary>The HTTP status of a maker error code (CONTRACT.md "Errors"); unknown codes are 500.</summary>
        /// <param name="code">The code.</param>
        public static int StatusOf(string? code) => code switch
        {
            MakerErrorCodes.BadRequest => 400,
            MakerHttpCodes.Unauthorized => 401,
            MakerErrorCodes.ReadOnlyPack => 403,
            MakerHttpCodes.ForbiddenOrigin => 403,
            MakerStudioErrorCodes.ConsentRequired => 403,
            MakerStudioErrorCodes.ExportBlocked => 409,
            MakerErrorCodes.NotFound => 404,
            MakerErrorCodes.RequestTimeout => 408,
            MakerErrorCodes.NotReady => 409,
            MakerStudioErrorCodes.Conflict => 409,
            MakerStudioErrorCodes.GamePaused => 409,
            MakerStudioErrorCodes.RenderRequired => 409,
            MakerStudioErrorCodes.RestartRequired => 409,
            MakerStudioErrorCodes.TakeInUse => 409,
            MakerStudioErrorCodes.TakeLimit => 409,
            MakerErrorCodes.TooLarge => 413,
            MakerStudioErrorCodes.StorageLimit => 413,
            MakerErrorCodes.UnsupportedMedia => 415,
            MakerHttpCodes.HostRefused => 421,
            MakerHttpCodes.RateLimited => 429,
            MakerErrorCodes.Unavailable => 503,
            MakerStudioErrorCodes.MetadataUnavailable => 503,
            MakerStudioErrorCodes.RecoveryRequired => 503,
            _ => 500,
        };

        private static bool IsAtOrBelow(string path, string root) =>
            path.Length >= root.Length
            && string.CompareOrdinal(path, 0, root, 0, root.Length) == 0
            && (path.Length == root.Length || path[root.Length] == '/');
    }

    /// <summary>The maker error codes the HTTP layer adds to <see cref="MakerErrorCodes"/> (CONTRACT.md "Errors").</summary>
    public static class MakerHttpCodes
    {
        /// <summary>401: no maker token, or the wrong one.</summary>
        public const string Unauthorized = ApiErrorCodes.Unauthorized;

        /// <summary>403: the <c>Origin</c> or <c>Sec-Fetch-Site</c> is not this page's own.</summary>
        public const string ForbiddenOrigin = "forbidden_origin";

        /// <summary>408: the request body did not arrive before its fixed deadline.</summary>
        public const string RequestTimeout = ApiErrorCodes.RequestTimeout;

        /// <summary>421: the <c>Host</c> is not <c>127.0.0.1:&lt;port&gt;</c> or <c>localhost:&lt;port&gt;</c>.</summary>
        public const string HostRefused = ApiErrorCodes.HostRefused;

        /// <summary>429: a rate limit or the long-poll cap; see <c>Retry-After</c>.</summary>
        public const string RateLimited = ApiErrorCodes.RateLimited;
    }

    /// <summary>The path segment rules of CONTRACT.md "Segments". Anything else is 400 before a backend is called.</summary>
    public static class MakerSegments
    {
        /// <summary><c>[a-z0-9][a-z0-9._-]{0,63}</c>.</summary>
        /// <param name="value">The segment.</param>
        public static bool IsPackId(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > 64 || !IsLowerOrDigit(value[0]))
            {
                return false;
            }

            for (var i = 1; i < value.Length; i++)
            {
                var c = value[i];
                if (!(IsLowerOrDigit(c) || c == '.' || c == '_' || c == '-'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary><c>[a-z]{2,3}(-[a-z0-9]{2,8})?</c>.</summary>
        /// <param name="value">The segment.</param>
        public static bool IsLang(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            var dash = value!.IndexOf('-');
            var primary = dash < 0 ? value : value.Substring(0, dash);
            if (primary.Length < 2 || primary.Length > 3)
            {
                return false;
            }

            foreach (var c in primary)
            {
                if (c < 'a' || c > 'z')
                {
                    return false;
                }
            }

            if (dash < 0)
            {
                return true;
            }

            var sub = value.Substring(dash + 1);
            if (sub.Length < 2 || sub.Length > 8)
            {
                return false;
            }

            foreach (var c in sub)
            {
                if (!IsLowerOrDigit(c))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// A line id or a voice clip name: <c>[A-Za-z0-9_.()-]{1,128}</c> (CONTRACT.md v1.1: the game has voice names
        /// such as <c>Voice_MainScenario_12(5)_001</c>), and (stricter than the contract, never a real id) not made of
        /// dots only, so <c>.</c> and <c>..</c> can never name a file.
        /// </summary>
        /// <param name="value">The segment.</param>
        public static bool IsId(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > 128)
            {
                return false;
            }

            var onlyDots = true;
            foreach (var c in value)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                      || c == '_' || c == '.' || c == '-' || c == '(' || c == ')'))
                {
                    return false;
                }

                onlyDots &= c == '.';
            }

            return !onlyDots;
        }

        /// <summary><c>all</c>, <c>missing-subtitle</c>, <c>missing-dub</c> or <c>done</c>.</summary>
        /// <param name="value">The filter.</param>
        public static bool IsFilter(string? value) =>
            value == "all" || value == "missing-subtitle" || value == "missing-dub" || value == "done";

        private static bool IsLowerOrDigit(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
    }
}
