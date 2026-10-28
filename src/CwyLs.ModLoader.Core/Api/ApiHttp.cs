using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>The API's clock: monotonic seconds for limits and ages, wall time for payloads. Tests supply their own.</summary>
    public interface IApiClock
    {
        /// <summary>Monotonic seconds; only differences mean anything. Safe on any thread.</summary>
        double MonotonicSeconds { get; }

        /// <summary>The local wall time with offset.</summary>
        DateTimeOffset Now { get; }
    }

    /// <summary>The real clock: a <see cref="Stopwatch"/> and <see cref="DateTimeOffset.Now"/>.</summary>
    public sealed class SystemApiClock : IApiClock
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        /// <summary>The one instance.</summary>
        public static SystemApiClock Instance { get; } = new SystemApiClock();

        /// <inheritdoc />
        public double MonotonicSeconds => _watch.Elapsed.TotalSeconds;

        /// <inheritdoc />
        public DateTimeOffset Now => DateTimeOffset.Now;
    }

    /// <summary>
    /// Request headers, names case-insensitive. A header sent twice is kept as one comma-joined
    /// value (what HttpListener does), so a doubled <c>Host</c> or authorization header never
    /// equals the single expected value.
    /// </summary>
    public sealed class ApiHeaders
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many distinct header names there are.</summary>
        public int Count => _values.Count;

        /// <summary>Adds a header; a repeated name is joined to the earlier value with a comma.</summary>
        /// <param name="name">The header name.</param>
        /// <param name="value">The value; null counts as empty.</param>
        public void Add(string name, string? value)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            var v = value ?? string.Empty;
            _values[name] = _values.TryGetValue(name, out var existing) ? existing + "," + v : v;
        }

        /// <summary>True when the header was sent (even empty).</summary>
        /// <param name="name">The header name.</param>
        public bool Has(string name) => _values.ContainsKey(name);

        /// <summary>The header's value, or null when absent.</summary>
        /// <param name="name">The header name.</param>
        public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>How reading a request body ended.</summary>
    public enum ApiBodyStatus
    {
        /// <summary>The body was read (possibly empty).</summary>
        Ok = 0,

        /// <summary>The body is larger than the cap; it was not read in full.</summary>
        TooLarge = 1,

        /// <summary>The connection failed or timed out while reading.</summary>
        Failed = 2,
    }

    /// <summary>The outcome of reading a request body. Immutable.</summary>
    public sealed class ApiBody
    {
        private static readonly byte[] NoBytes = new byte[0];

        private ApiBody(ApiBodyStatus status, byte[] bytes, string problem)
        {
            Status = status;
            Bytes = bytes;
            Problem = problem;
        }

        /// <summary>An empty body.</summary>
        public static ApiBody Empty { get; } = new ApiBody(ApiBodyStatus.Ok, NoBytes, string.Empty);

        /// <summary>How it ended.</summary>
        public ApiBodyStatus Status { get; }

        /// <summary>The bytes when <see cref="Status"/> is <see cref="ApiBodyStatus.Ok"/>; empty otherwise.</summary>
        public byte[] Bytes { get; }

        /// <summary>Why reading failed; empty on success.</summary>
        public string Problem { get; }

        /// <summary>A body that was read.</summary>
        /// <param name="bytes">The bytes.</param>
        public static ApiBody Of(byte[] bytes) => new ApiBody(ApiBodyStatus.Ok, bytes ?? NoBytes, string.Empty);

        /// <summary>A body over the cap.</summary>
        public static ApiBody TooLarge() => new ApiBody(ApiBodyStatus.TooLarge, NoBytes, "the body is larger than the cap");

        /// <summary>A body that could not be read.</summary>
        /// <param name="problem">Why.</param>
        public static ApiBody Failed(string problem) => new ApiBody(ApiBodyStatus.Failed, NoBytes, problem ?? "read failed");
    }

    /// <summary>
    /// One HTTP request as the router sees it, whatever transport carried it. The body is read
    /// lazily (<see cref="ReadBody"/>) so the router can refuse a request on its headers alone
    /// before a single body byte is read.
    /// </summary>
    public sealed class ApiRequest
    {
        private readonly Func<int, ApiBody> _bodyReader;
        private ApiBody? _body;

        /// <summary>Creates a request.</summary>
        /// <param name="transport">The transport's name, e.g. <c>httplistener</c> or <c>tcp</c>.</param>
        /// <param name="method">The HTTP method as sent.</param>
        /// <param name="target">The request target as sent (origin form: path and query).</param>
        /// <param name="headers">The headers.</param>
        /// <param name="contentLength">The declared body length, or null when none was declared.</param>
        /// <param name="chunked">True when the body uses <c>Transfer-Encoding</c> (refused).</param>
        /// <param name="bodyReader">Reads the body up to a byte cap; null for a request without a body.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public ApiRequest(
            string transport,
            string method,
            string target,
            ApiHeaders headers,
            long? contentLength,
            bool chunked,
            Func<int, ApiBody>? bodyReader)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Method = (method ?? throw new ArgumentNullException(nameof(method))).ToUpperInvariant();
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Headers = headers ?? throw new ArgumentNullException(nameof(headers));
            ContentLength = contentLength;
            Chunked = chunked;
            _bodyReader = bodyReader ?? (_ => ApiBody.Empty);
        }

        /// <summary>The transport's name.</summary>
        public string Transport { get; }

        /// <summary>The method, upper case.</summary>
        public string Method { get; }

        /// <summary>The request target as sent.</summary>
        public string Target { get; }

        /// <summary>The headers.</summary>
        public ApiHeaders Headers { get; }

        /// <summary>The declared body length, or null.</summary>
        public long? ContentLength { get; }

        /// <summary>True when the body uses <c>Transfer-Encoding</c>.</summary>
        public bool Chunked { get; }

        /// <summary>Reads the body once, up to <paramref name="maxBytes"/>; later calls return the same result.</summary>
        /// <param name="maxBytes">The cap.</param>
        public ApiBody ReadBody(int maxBytes) => _body ??= _bodyReader(maxBytes);
    }

    /// <summary>One HTTP response: status, JSON body and extra headers. Immutable.</summary>
    public sealed class ApiResponse
    {
        private static readonly KeyValuePair<string, string>[] NoHeaders = new KeyValuePair<string, string>[0];

        /// <summary>Creates a response.</summary>
        /// <param name="status">The HTTP status.</param>
        /// <param name="body">The JSON body.</param>
        /// <param name="headers">Extra headers (<c>Retry-After</c>, <c>Allow</c>, <c>Location</c>, ...), or null.</param>
        /// <param name="errorCode">The <see cref="ApiErrorCodes"/> value for an error, for the audit line; null on success.</param>
        public ApiResponse(int status, string body, IReadOnlyList<KeyValuePair<string, string>>? headers = null, string? errorCode = null)
        {
            Status = status;
            Body = body ?? "{}";
            Headers = headers ?? NoHeaders;
            ErrorCode = errorCode;
        }

        /// <summary>The HTTP status.</summary>
        public int Status { get; }

        /// <summary>The JSON body.</summary>
        public string Body { get; }

        /// <summary>Extra headers.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        /// <summary>The error code, or null.</summary>
        public string? ErrorCode { get; }

        /// <summary>The headers every response carries besides <c>Content-Type</c> and <c>Content-Length</c>.</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> CommonHeaders { get; } = new[]
        {
            new KeyValuePair<string, string>("Cache-Control", "no-store"),
            new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff"),
        };

        /// <summary>The value of the <c>Content-Type</c> header of every response.</summary>
        public const string ContentType = "application/json; charset=utf-8";

        /// <summary>A JSON success response.</summary>
        /// <param name="status">200, 202, ...</param>
        /// <param name="json">The body.</param>
        /// <param name="headers">Extra headers, or null.</param>
        public static ApiResponse Json(int status, string json, params KeyValuePair<string, string>[] headers) =>
            new ApiResponse(status, json, headers);

        /// <summary>An error response with the standard body.</summary>
        /// <param name="status">The HTTP status.</param>
        /// <param name="code">The error code.</param>
        /// <param name="message">One sentence for people.</param>
        /// <param name="headers">Extra headers, or none.</param>
        public static ApiResponse Error(int status, string code, string message, params KeyValuePair<string, string>[] headers) =>
            new ApiResponse(status, ApiJson.Error(code, message), headers, code);

        /// <summary>A header pair.</summary>
        /// <param name="name">The name.</param>
        /// <param name="value">The value.</param>
        public static KeyValuePair<string, string> Header(string name, string value) => new KeyValuePair<string, string>(name, value);

        /// <summary>A <c>Retry-After</c> header, whole seconds rounded up (at least 1).</summary>
        /// <param name="seconds">Seconds to wait.</param>
        public static KeyValuePair<string, string> RetryAfter(double seconds) =>
            Header("Retry-After", Math.Max(1, (int)Math.Ceiling(seconds)).ToString(CultureInfo.InvariantCulture));

        /// <summary>The reason phrase of a status code, for the TCP transport's status line.</summary>
        /// <param name="status">The status.</param>
        public static string Reason(int status) => status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            409 => "Conflict",
            411 => "Length Required",
            413 => "Payload Too Large",
            415 => "Unsupported Media Type",
            421 => "Misdirected Request",
            429 => "Too Many Requests",
            431 => "Request Header Fields Too Large",
            500 => "Internal Server Error",
            501 => "Not Implemented",
            503 => "Service Unavailable",
            _ => "Status",
        };
    }

    /// <summary>A parsed origin-form request target: path plus query parameters.</summary>
    public sealed class ApiTarget
    {
        private readonly List<KeyValuePair<string, string>> _query;

        private ApiTarget(string path, List<KeyValuePair<string, string>> query)
        {
            Path = path;
            _query = query;
        }

        /// <summary>The path, e.g. <c>/v1/events</c>.</summary>
        public string Path { get; }

        /// <summary>The query parameters in order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Query => _query;

        /// <summary>
        /// Parses a target. Strict: origin form only (starts with <c>/</c>), no <c>%</c> in the
        /// path (every route segment is plain <c>[a-z0-9._-]</c>), no <c>..</c> segment, no
        /// fragment, each query parameter at most once, percent-decoding only in query values.
        /// </summary>
        /// <param name="target">The target as sent.</param>
        /// <param name="parsed">The result, or null.</param>
        /// <param name="problem">Why it was refused, or empty.</param>
        public static bool TryParse(string? target, out ApiTarget? parsed, out string problem)
        {
            parsed = null;
            if (string.IsNullOrEmpty(target) || target![0] != '/')
            {
                problem = "the request target must be a path starting with '/'";
                return false;
            }

            if (target.Length > 2048)
            {
                problem = "the request target is longer than 2048 characters";
                return false;
            }

            if (target.IndexOf('#') >= 0)
            {
                problem = "the request target must not contain a fragment";
                return false;
            }

            var q = target.IndexOf('?');
            var path = q >= 0 ? target.Substring(0, q) : target;
            if (path.IndexOf('%') >= 0 || path.IndexOf('\\') >= 0 || path.Contains("/../") || path.EndsWith("/..", StringComparison.Ordinal) || path.Contains("//"))
            {
                problem = "the path must be plain: no '%', '\\', '//' or '..' segments";
                return false;
            }

            var query = new List<KeyValuePair<string, string>>();
            if (q >= 0 && q < target.Length - 1)
            {
                foreach (var pair in target.Substring(q + 1).Split('&'))
                {
                    if (pair.Length == 0)
                    {
                        continue;
                    }

                    var eq = pair.IndexOf('=');
                    var name = eq >= 0 ? pair.Substring(0, eq) : pair;
                    var raw = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;
                    string value;
                    try
                    {
                        value = Uri.UnescapeDataString(raw.Replace('+', ' '));
                    }
                    catch (UriFormatException ex)
                    {
                        problem = "the query parameter '" + name + "' is not valid percent-encoding: " + ex.Message;
                        return false;
                    }

                    foreach (var existing in query)
                    {
                        if (string.Equals(existing.Key, name, StringComparison.Ordinal))
                        {
                            problem = "the query parameter '" + name + "' is given twice";
                            return false;
                        }
                    }

                    query.Add(new KeyValuePair<string, string>(name, value));
                }
            }

            parsed = new ApiTarget(path, query);
            problem = string.Empty;
            return true;
        }

        /// <summary>The value of a query parameter, or null.</summary>
        /// <param name="name">The parameter name (case-sensitive).</param>
        public string? Get(string name)
        {
            foreach (var pair in _query)
            {
                if (string.Equals(pair.Key, name, StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }

            return null;
        }
    }
}
