using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>
    /// Pack Maker's request diagnostics: the <c>MAKER-TRACE</c> line per request while <c>maker trace on</c>, the always-on
    /// <c>MAKER-FAIL</c> line for every 500 and every 503 of a backend call, and the page's own log
    /// (<c>POST /v1/maker/studio/client-log</c>, written as <c>MAKER-WEB</c> lines while tracing). All go to the console
    /// (LogOutput.log) and the trace file. Never logs the token, a header value or a request body other than the page's
    /// own log entries.
    /// </summary>
    public sealed partial class MakerRouter
    {
        /// <summary>The bus subsystem of these lines.</summary>
        public const string TraceSys = "maker";

        /// <summary>The path segment of the page's log route under <c>/v1/maker/studio/</c>.</summary>
        public const string ClientLogSegment = "client-log";

        private const int MaxLoggedTarget = 300;
        private const int MaxClientValue = 240;
        private const int MaxClientLine = 600;

        /// <summary><c>POST /v1/maker/studio/client-log</c>: <c>{"entries":[{...}]}</c>, written only while tracing.</summary>
        private ApiResponse ClientLogRoute(ApiRequest r, ApiTarget target, string path)
        {
            if (r.Method != "POST") return NoRoute(r.Method, path, "POST");
            if (!Take(ApiRateClass.Read, out var limited)) return limited!;
            var queryProblem = NoQuery(target);
            if (queryProblem != null) return queryProblem;
            var jsonProblem = ReadJsonObject(r, out var body);
            if (jsonProblem != null) return jsonProblem;
            if (body!.Count != 1 || !body.TryGetValue("entries", out var node) || !(node is IList<object?> entries))
            {
                return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "expected {\"entries\":[...]}");
            }

            if (entries.Count > MakerDefaults.MaxClientLogEntries)
            {
                return ApiResponse.Error(413, MakerErrorCodes.TooLarge, "at most " + MakerDefaults.MaxClientLogEntries + " entries per post");
            }

            var on = MakerRequestTrace.Enabled;
            var accepted = 0;
            if (on)
            {
                foreach (var entry in entries)
                {
                    if (!(entry is JsonObject fields)) continue;
                    ConsoleEvents.Emit(TraceSys, "web", ConsoleTags.Api, "MAKER-WEB " + RenderClientEntry(fields), null);
                    accepted++;
                }
            }

            return ApiResponse.Json(200, ApiJson.Write(new JsonObject
            {
                ["accepted"] = accepted,
                ["dropped"] = entries.Count - accepted,
                ["trace"] = on,
            }));
        }

        /// <summary><c>kind key=value ...</c> from one page entry: scalars only, control characters replaced, every value and the line capped.</summary>
        private static string RenderClientEntry(JsonObject fields)
        {
            var text = new StringBuilder();
            if (fields.TryGetValue("kind", out var kind) && kind is string k)
            {
                AppendClean(text, k, 40);
            }
            else
            {
                text.Append('?');
            }

            foreach (var pair in fields)
            {
                if (pair.Key == "kind") continue;
                if (text.Length >= MaxClientLine) break;
                string value;
                switch (pair.Value)
                {
                    case null: value = "null"; break;
                    case string str: value = str; break;
                    case bool b: value = b ? "true" : "false"; break;
                    case long l: value = l.ToString(CultureInfo.InvariantCulture); break;
                    case double d: value = d.ToString("0.###", CultureInfo.InvariantCulture); break;
                    default: continue; // objects and arrays are not logged
                }

                text.Append(' ');
                AppendClean(text, pair.Key, 32);
                text.Append('=');
                AppendClean(text, value, MaxClientValue);
            }

            if (text.Length > MaxClientLine) text.Length = MaxClientLine;
            return text.ToString();
        }

        private static void AppendClean(StringBuilder text, string value, int max)
        {
            var n = Math.Min(value.Length, max);
            for (var i = 0; i < n; i++)
            {
                var c = value[i];
                text.Append(c < ' ' || c == '\u007f' ? '?' : c);
            }

            if (value.Length > max) text.Append('…');
        }

        /// <summary>The per-request line while tracing: method, path and query, status, total ms, phases, response size.</summary>
        private static void TraceLine(ApiRequest r, ApiResponse response, double totalMs, MakerRequestTrace? trace)
        {
            try
            {
                var bytes = response.Bytes?.Length ?? Encoding.UTF8.GetByteCount(response.Body ?? string.Empty);
                var line = new StringBuilder(160)
                    .Append("MAKER-TRACE ").Append(r.Method).Append(' ').Append(LoggedTarget(r.Target))
                    .Append(' ').Append(response.Status.ToString(CultureInfo.InvariantCulture))
                    .Append(" total=").Append(MakerRequestTrace.Format(totalMs)).Append("ms");
                if (response.ErrorCode != null) line.Append(" error=").Append(response.ErrorCode);
                if (trace != null)
                {
                    line.Append(" | ").Append(trace.Phases());
                    var running = trace.Running();
                    if (running != "-") line.Append(" | running ").Append(running);
                }

                line.Append(" | bytes=").Append(bytes.ToString(CultureInfo.InvariantCulture));
                ConsoleEvents.Emit(TraceSys, "trace", ConsoleTags.Api, line.ToString(), null);
            }
            catch
            {
                // Diagnostics never fail a request.
            }
        }

        /// <summary>The always-on line for a 500, or a 503 of a backend call: what failed and, for a timeout, which phase was still running.</summary>
        private void LogFailure(ApiRequest r, int status, string what, MakerRequestTrace? trace)
        {
            try
            {
                var line = "MAKER-FAIL " + r.Method + " " + LoggedTarget(r.Target) + " " + status.ToString(CultureInfo.InvariantCulture) + " " + what;
                if (trace != null && what.IndexOf("phases:", StringComparison.Ordinal) < 0 && what.IndexOf("finished:", StringComparison.Ordinal) < 0)
                {
                    line += " | phases: " + trace.Phases() + " | running: " + trace.Running();
                }

                ConsoleEvents.EmitAt(TraceSys, "requestFailed", ConsoleTags.Api, ConsoleLevel.Warn, line, null);
            }
            catch (Exception ex)
            {
                Diagnose("logging a failed request failed: " + ex.GetType().Name);
            }
        }

        /// <summary><c>Type: message @ frame | frame | frame</c> (the top three stack frames).</summary>
        private static string Describe(Exception ex)
        {
            var text = new StringBuilder().Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            var stack = ex.StackTrace;
            if (!string.IsNullOrEmpty(stack))
            {
                var frames = stack!.Split('\n');
                var shown = 0;
                foreach (var frame in frames)
                {
                    var f = frame.Trim();
                    if (f.Length == 0) continue;
                    text.Append(shown == 0 ? " @ " : " | ").Append(f.Length > 200 ? f.Substring(0, 200) : f);
                    if (++shown == 3) break;
                }
            }

            return text.ToString();
        }

        /// <summary>The request target for a log line: path and query, control characters replaced, at most 300 characters.</summary>
        private static string LoggedTarget(string target)
        {
            // Launch links are credentials. Preserve API query diagnostics, but never log page queries.
            if (target.StartsWith(MakerDefaults.WebPath, StringComparison.Ordinal))
            {
                var query = target.IndexOf('?');
                if (query >= 0) target = target.Substring(0, query);
            }

            var text = new StringBuilder(Math.Min(target.Length, MaxLoggedTarget) + 1);
            AppendClean(text, target, MaxLoggedTarget);
            return text.ToString();
        }
    }
}
