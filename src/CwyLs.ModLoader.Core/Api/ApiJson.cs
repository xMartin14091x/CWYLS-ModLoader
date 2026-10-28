using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>A JSON fragment that is already serialised; <see cref="ApiJson"/> copies it verbatim.</summary>
    public sealed class ApiRawJson
    {
        /// <summary>Wraps a fragment.</summary>
        /// <param name="json">A complete JSON value.</param>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        public ApiRawJson(string json) => Json = json ?? throw new ArgumentNullException(nameof(json));

        /// <summary>The fragment.</summary>
        public string Json { get; }

        /// <summary>The fragment.</summary>
        public override string ToString() => Json;
    }

    /// <summary>
    /// The API's JSON writer: ordered objects (<see cref="JsonObject"/>), lists and scalars with
    /// fixed rules, so every payload has a stable member order. <see cref="ulong"/> ids are written
    /// as strings (they exceed 2^53, same rule as <see cref="JsonWriter"/>); non-finite numbers are
    /// <c>null</c>; anything else falls back to the reflective <see cref="JsonWriter"/>.
    /// </summary>
    public static class ApiJson
    {
        private const int MaxDepth = 32;

        /// <summary>Serialises a value to compact JSON.</summary>
        /// <param name="value">A <see cref="JsonObject"/>, dictionary, list, scalar or <see cref="ApiRawJson"/>.</param>
        public static string Write(object? value)
        {
            var sb = new StringBuilder(256);
            Append(sb, value, 0);
            return sb.ToString();
        }

        /// <summary>Appends a value's JSON to a builder.</summary>
        /// <param name="sb">The builder.</param>
        /// <param name="value">The value.</param>
        public static void Append(StringBuilder sb, object? value) => Append(sb, value, 0);

        /// <summary>The standard error body: <c>{"error":{"code":"…","message":"…"}}</c>.</summary>
        /// <param name="code">A stable error code (<see cref="ApiErrorCodes"/>).</param>
        /// <param name="message">One sentence for people.</param>
        public static string Error(string code, string message) =>
            Write(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

        /// <summary><c>WorkStarted</c> → <c>workStarted</c>; null stays null.</summary>
        /// <param name="name">A Pascal-case name.</param>
        public static string? Camel(string? name) =>
            string.IsNullOrEmpty(name) || char.IsLower(name![0])
                ? name
                : char.ToLowerInvariant(name[0]) + name.Substring(1);

        /// <summary>ISO-8601 with offset and milliseconds, e.g. <c>2026-10-02T21:30:00.123+07:00</c>.</summary>
        /// <param name="when">The time.</param>
        public static string Time(DateTimeOffset when) => MiniJson.Iso8601(when);

        /// <summary>A calendar date as <c>yyyy-MM-dd</c>.</summary>
        /// <param name="date">The day.</param>
        public static string Date(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>A local or UTC time as ISO-8601 with its offset; an unspecified kind is read as local.</summary>
        /// <param name="time">The time, or null.</param>
        public static string? Time(DateTime? time)
        {
            if (time is null)
            {
                return null;
            }

            var value = time.Value;
            return value.Kind == DateTimeKind.Utc
                ? MiniJson.Iso8601(new DateTimeOffset(value, TimeSpan.Zero))
                : MiniJson.Iso8601(new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local)));
        }

        private static void Append(StringBuilder sb, object? value, int depth)
        {
            if (depth > MaxDepth)
            {
                sb.Append("null");
                return;
            }

            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case ApiRawJson raw:
                    sb.Append(raw.Json);
                    return;
                case string s:
                    sb.Append(MiniJson.String(s));
                    return;
                case bool b:
                    sb.Append(MiniJson.Bool(b));
                    return;
                case ulong u:
                    sb.Append(MiniJson.String(u.ToString(CultureInfo.InvariantCulture)));
                    return;
                case sbyte or byte or short or ushort or int or uint or long:
                    sb.Append(MiniJson.Number(Convert.ToInt64(value, CultureInfo.InvariantCulture)));
                    return;
                case float f:
                    sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : ((double)f).ToString("R", CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : MiniJson.Number(d));
                    return;
                case DateTimeOffset dto:
                    sb.Append(MiniJson.String(MiniJson.Iso8601(dto)));
                    return;
                case Enum e:
                    sb.Append(MiniJson.String(Camel(e.ToString())));
                    return;
                case JsonObject obj:
                    AppendMembers(sb, obj, depth);
                    return;
                case IReadOnlyDictionary<string, object?> ro:
                    AppendMembers(sb, ro, depth);
                    return;
                case IDictionary<string, object?> rw:
                    AppendMembers(sb, rw, depth);
                    return;
                case IEnumerable sequence:
                    sb.Append('[');
                    var first = true;
                    foreach (var item in sequence)
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        Append(sb, item, depth + 1);
                    }

                    sb.Append(']');
                    return;
                default:
                    // DateTime, Guid, TimeSpan, decimal and arbitrary objects (a handler's result
                    // data): the framework's reflective writer, same rules as snapshot.json.
                    sb.Append(JsonWriter.Write(value));
                    return;
            }
        }

        private static void AppendMembers(StringBuilder sb, IEnumerable<KeyValuePair<string, object?>> members, int depth)
        {
            sb.Append('{');
            var first = true;
            foreach (var member in members)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append(MiniJson.String(member.Key)).Append(':');
                Append(sb, member.Value, depth + 1);
            }

            sb.Append('}');
        }
    }

    /// <summary>The stable error codes the API answers with (documented in <c>Core/Api/README.md</c>).</summary>
    public static class ApiErrorCodes
    {
        /// <summary>400: the request target, query, body or JSON is malformed.</summary>
        public const string BadRequest = "bad_request";

        /// <summary>400: the action arguments do not fit the action's parameters.</summary>
        public const string InvalidArgs = "invalid_args";

        /// <summary>401: no access key, or the wrong one.</summary>
        public const string Unauthorized = "unauthorized";

        /// <summary>403: the request carries an <c>Origin</c> or <c>Sec-Fetch-*</c> header (a browser).</summary>
        public const string BrowserRefused = "browser_refused";

        /// <summary>403: the tier gate refused the action for API callers.</summary>
        public const string Denied = "denied";

        /// <summary>403: the action needs the in-game confirmation, which is not installed.</summary>
        public const string ConfirmationUnavailable = "confirmation_unavailable";

        /// <summary>404: no such route, action or handle.</summary>
        public const string NotFound = "not_found";

        /// <summary>405: the route exists but not for this method (see <c>Allow</c>).</summary>
        public const string MethodNotAllowed = "method_not_allowed";

        /// <summary>409: the idempotency key was used before with a different request.</summary>
        public const string IdempotencyConflict = "idempotency_conflict";

        /// <summary>411/501: the body has no <c>Content-Length</c> (chunked bodies are not accepted).</summary>
        public const string LengthRequired = "length_required";

        /// <summary>413: the body is larger than the cap.</summary>
        public const string TooLarge = "too_large";

        /// <summary>415: a body that is not <c>application/json</c>.</summary>
        public const string UnsupportedMediaType = "unsupported_media_type";

        /// <summary>421: the <c>Host</c> header is not <c>127.0.0.1:&lt;port&gt;</c>.</summary>
        public const string HostRefused = "host_refused";

        /// <summary>429: a rate limit or a concurrency cap; see <c>Retry-After</c>.</summary>
        public const string RateLimited = "rate_limited";

        /// <summary>500: the API failed on this request.</summary>
        public const string Internal = "internal";

        /// <summary>503: the API is stopping, too busy, or actions are not installed.</summary>
        public const string Unavailable = "unavailable";
    }
}
