using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>
    /// One audit record per request: route, status, time taken, caller name, transport, and for
    /// actions the tier and handle. Never the access key, never a header value, never a body
    /// (bodies can hold the player's text). Immutable once written.
    /// </summary>
    public sealed class ApiAuditRecord
    {
        /// <summary>The method.</summary>
        public string Method { get; set; } = string.Empty;

        /// <summary>The path without the query (or the raw target cut to 120 characters when it did not parse).</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>The HTTP status sent.</summary>
        public int Status { get; set; }

        /// <summary>The error code, or null.</summary>
        public string? Error { get; set; }

        /// <summary>Milliseconds spent in the router (long-polls included).</summary>
        public double Ms { get; set; }

        /// <summary>The caller's name (<c>X-CWY-Client</c>), <c>api</c> when not given, <c>-</c> before the key check passed.</summary>
        public string Client { get; set; } = "-";

        /// <summary>The transport.</summary>
        public string Transport { get; set; } = string.Empty;

        /// <summary>For actions: the tier.</summary>
        public string? Tier { get; set; }

        /// <summary>For actions: the handle.</summary>
        public string? Handle { get; set; }

        /// <summary>True when the access key matched.</summary>
        public bool Authenticated { get; set; }

        /// <summary>The primary console column: <c>GET /v1/state 200</c>.</summary>
        public string Primary => Method + " " + Path + " " + Status.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Where one audit record goes.</summary>
    public enum ApiAuditChannel
    {
        /// <summary>The console (and <c>cwy-console.log</c>) and the trace file.</summary>
        Console = 0,

        /// <summary>The trace file only.</summary>
        File = 1,
    }

    /// <summary>
    /// Writes the per-request audit line with tag <c>API</c> (bus <c>api/request</c>). Actions and
    /// rejections reach the console; successful reads (polling) stay in the trace file unless
    /// <c>[Api] ConsoleAuditReads</c> is on; a burst of the same rejection is limited to
    /// <see cref="BurstPerWindow"/> console lines per <see cref="WindowSeconds"/> (the rest are
    /// file-only, and the next console line counts them). Thread-safe.
    /// </summary>
    public sealed class ApiAuditLog
    {
        /// <summary>Bus subsystem of every API record.</summary>
        public const string Sys = "api";

        /// <summary>Console lines per rejection status per window.</summary>
        public const int BurstPerWindow = 5;

        /// <summary>The rejection window, in seconds.</summary>
        public const double WindowSeconds = 10;

        private readonly object _gate = new object();
        private readonly Dictionary<int, Window> _windows = new Dictionary<int, Window>();
        private readonly IApiClock _clock;
        private readonly Func<bool> _consoleReads;
        private readonly Action<ApiAuditRecord, ApiAuditChannel, int>? _sink;

        /// <summary>Creates the log.</summary>
        /// <param name="clock">The clock.</param>
        /// <param name="consoleReads"><c>[Api] ConsoleAuditReads</c>, read per record.</param>
        /// <param name="sink">Replaces the bus publish (tests); null publishes through <see cref="ConsoleEvents"/>.</param>
        public ApiAuditLog(IApiClock clock, Func<bool> consoleReads, Action<ApiAuditRecord, ApiAuditChannel, int>? sink = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _consoleReads = consoleReads ?? (() => false);
            _sink = sink;
        }

        /// <summary>Records written so far.</summary>
        public long Written => Interlocked.Read(ref _written);

        private long _written;

        /// <summary>
        /// Where a record goes by the rules alone (no throttling): reads that succeeded go to the
        /// file unless console reads are on; everything else goes to the console.
        /// </summary>
        /// <param name="record">The record.</param>
        /// <param name="consoleReads"><c>[Api] ConsoleAuditReads</c>.</param>
        public static ApiAuditChannel Route(ApiAuditRecord record, bool consoleReads)
        {
            if (record is null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var success = record.Status < 400;
            var read = string.Equals(record.Method, "GET", StringComparison.Ordinal);
            return success && read && !consoleReads ? ApiAuditChannel.File : ApiAuditChannel.Console;
        }

        /// <summary>Writes one record.</summary>
        /// <param name="record">The record.</param>
        public void Write(ApiAuditRecord record)
        {
            if (record is null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var channel = Route(record, _consoleReads());
            var quiet = 0;
            if (channel == ApiAuditChannel.Console && record.Status >= 400)
            {
                channel = Throttle(record.Status, out quiet);
            }

            Interlocked.Increment(ref _written);
            if (_sink != null)
            {
                _sink(record, channel, quiet);
                return;
            }

            Publish(record, channel, quiet);
        }

        private ApiAuditChannel Throttle(int status, out int quiet)
        {
            lock (_gate)
            {
                var now = _clock.MonotonicSeconds;
                if (!_windows.TryGetValue(status, out var window) || now - window.Start >= WindowSeconds)
                {
                    var carried = window?.Suppressed ?? 0;
                    _windows[status] = new Window { Start = now, Shown = 1 };
                    quiet = carried;
                    return ApiAuditChannel.Console;
                }

                if (window.Shown < BurstPerWindow)
                {
                    window.Shown++;
                    quiet = window.Suppressed;
                    window.Suppressed = 0;
                    return ApiAuditChannel.Console;
                }

                window.Suppressed++;
                quiet = 0;
                return ApiAuditChannel.File;
            }
        }

        private static void Publish(ApiAuditRecord r, ApiAuditChannel channel, int quiet)
        {
            var fields = new List<(string Key, object? Value)>
            {
                ("method", r.Method),
                ("path", r.Path),
                ("status", r.Status),
                ("ms", Math.Round(r.Ms, 1)),
                ("client", r.Client),
                ("transport", r.Transport),
                ("auth", r.Authenticated),
            };
            var shown = new List<string> { "ms", "client" };
            if (r.Tier != null)
            {
                fields.Add(("tier", r.Tier));
                shown.Add("tier");
            }

            if (r.Handle != null)
            {
                fields.Add(("handle", r.Handle));
                shown.Add("handle");
            }

            if (r.Error != null)
            {
                fields.Add(("error", r.Error));
                shown.Add("error");
            }

            if (quiet > 0)
            {
                fields.Add(("quiet", quiet));
                shown.Add("quiet");
            }

            if (channel == ApiAuditChannel.File)
            {
                ConsoleEvents.File(Sys, "request", fields.ToArray());
                return;
            }

            var level = r.Status == 401 || r.Status == 403 || r.Status == 421 || r.Status == 429 || r.Status >= 500
                ? ConsoleLevel.Warn
                : ConsoleLevel.Info;
            if (level == ConsoleLevel.Info)
            {
                ConsoleEvents.Emit(Sys, "request", ConsoleTags.Api, r.Primary, string.Join(",", shown), fields.ToArray());
            }
            else
            {
                ConsoleEvents.EmitAt(Sys, "request", ConsoleTags.Api, level, r.Primary, string.Join(",", shown), fields.ToArray());
            }
        }

        private sealed class Window
        {
            internal double Start;
            internal int Shown;
            internal int Suppressed;
        }
    }

    /// <summary>Request counters for <c>api status</c> and <c>/v1/info</c>. Thread-safe.</summary>
    public sealed class ApiCounters
    {
        private readonly object _gate = new object();
        private readonly SortedDictionary<int, long> _byStatus = new SortedDictionary<int, long>();
        private long _total;

        /// <summary>Requests answered.</summary>
        public long Total => Interlocked.Read(ref _total);

        /// <summary>Counts one response.</summary>
        /// <param name="status">Its status.</param>
        public void Record(int status)
        {
            Interlocked.Increment(ref _total);
            lock (_gate)
            {
                _byStatus[status] = _byStatus.TryGetValue(status, out var n) ? n + 1 : 1;
            }
        }

        /// <summary><c>200×12 401×1</c>, or <c>none</c>.</summary>
        public string Describe()
        {
            lock (_gate)
            {
                if (_byStatus.Count == 0)
                {
                    return "none";
                }

                var parts = new List<string>(_byStatus.Count);
                foreach (var pair in _byStatus)
                {
                    parts.Add(pair.Key.ToString(CultureInfo.InvariantCulture) + "x" + pair.Value.ToString(CultureInfo.InvariantCulture));
                }

                return string.Join(" ", parts);
            }
        }
    }
}
