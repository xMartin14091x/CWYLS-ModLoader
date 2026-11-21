using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>A listener that turns HTTP requests into <see cref="ApiRequest"/>s for one handler.</summary>
    public interface IApiTransport : IDisposable
    {
        /// <summary><c>httplistener</c> or <c>tcp</c>.</summary>
        string Name { get; }

        /// <summary>The port.</summary>
        int Port { get; }

        /// <summary>True between a successful <see cref="Start"/> and <see cref="Stop"/>.</summary>
        bool IsRunning { get; }

        /// <summary>Binds <c>127.0.0.1:&lt;port&gt;</c> and starts accepting. Throws when it cannot bind.</summary>
        void Start();

        /// <summary>Closes the listener at once (in-flight connections are aborted). Idempotent; never throws.</summary>
        void Stop();
    }

    /// <summary>The HTTP/1.1 request head parser of the TCP fallback. Pure.</summary>
    public static class ApiHttpParser
    {
        /// <summary>
        /// Parses a request head (request line and header lines, without the blank line). Strict:
        /// <c>METHOD SP target SP HTTP/1.0|1.1</c>; header names are tokens with no space before the
        /// colon; no obsolete line folding; a repeated header is comma-joined (see <see cref="ApiHeaders"/>).
        /// </summary>
        /// <param name="head">The head text (ASCII/Latin-1).</param>
        /// <param name="method">The method.</param>
        /// <param name="target">The request target.</param>
        /// <param name="headers">The headers.</param>
        /// <param name="problem">Why it was refused, or empty.</param>
        public static bool TryParse(string head, out string method, out string target, out ApiHeaders headers, out string problem)
        {
            method = string.Empty;
            target = string.Empty;
            headers = new ApiHeaders();
            if (string.IsNullOrEmpty(head))
            {
                problem = "empty request";
                return false;
            }

            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0
                || (parts[2] != "HTTP/1.1" && parts[2] != "HTTP/1.0"))
            {
                problem = "bad request line";
                return false;
            }

            foreach (var c in parts[0])
            {
                if (c < 'A' || c > 'Z')
                {
                    problem = "bad method";
                    return false;
                }
            }

            method = parts[0];
            target = parts[1];
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == ' ' || line[0] == '\t')
                {
                    problem = "folded header lines are not accepted";
                    return false;
                }

                var colon = line.IndexOf(':');
                if (colon <= 0 || !IsToken(line, colon))
                {
                    problem = "bad header line";
                    return false;
                }

                headers.Add(line.Substring(0, colon), line.Substring(colon + 1).Trim());
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>Parses a <c>Content-Length</c> value: digits only, one value.</summary>
        /// <param name="value">The header value, or null.</param>
        /// <param name="length">The length, or null when absent.</param>
        public static bool TryContentLength(string? value, out long? length)
        {
            length = null;
            if (value is null)
            {
                return true;
            }

            if (value.Length == 0 || value.Length > 18)
            {
                return false;
            }

            foreach (var c in value)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            length = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
            return true;
        }

        private static bool IsToken(string line, int end)
        {
            for (var i = 0; i < end; i++)
            {
                var c = line[i];
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                         || c == '-' || c == '_' || c == '.' || c == '!' || c == '#' || c == '$' || c == '%'
                         || c == '&' || c == '\'' || c == '*' || c == '+' || c == '^' || c == '`' || c == '|' || c == '~';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The fallback transport: a small HTTP/1.1 server on a <see cref="TcpListener"/> bound to the
    /// IPv4 loopback only. One request per connection (<c>Connection: close</c>), each on its own
    /// background thread, head capped at <see cref="ApiDefaults.MaxHeadBytes"/>, 2 s idle timeouts on head reads
    /// and on writes, at most <see cref="ApiDefaults.MaxConcurrentRequests"/> requests at once (more get 503). The whole
    /// request, head and body, must arrive within <see cref="ReadDeadline"/> of the connection being accepted:
    /// one fixed deadline, not per read and not restarted when the head ends, so a client that drips a byte at
    /// a time cannot keep a request slot past it (DS-06 finding 4). A body is bounded by that deadline alone, so a
    /// body that stalls outright and one that drips both end in 408 <c>request_timeout</c>, as on HttpListener.
    /// </summary>
    public sealed class TcpApiTransport : IApiTransport
    {
        /// <summary>
        /// The longest a whole request (head and body) may take to arrive, counted from the accept; every route,
        /// <c>/v1</c> included. A 16 MiB dub over loopback takes well under a second.
        /// </summary>
        public static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(5);

        private const int IoTimeoutMs = 2000;
        private const int LingerIdleMs = 200;
        private const int LingerTotalMs = 1000;

        private readonly Func<ApiRequest, ApiResponse> _handler;
        private readonly Action<string> _diagnostics;
        private TcpListener? _listener;
        private Thread? _thread;
        private volatile bool _running;
        private int _active;

        /// <summary>Creates the transport (not started).</summary>
        /// <param name="port">The port.</param>
        /// <param name="handler">The router.</param>
        /// <param name="diagnostics">Where connection failures are reported.</param>
        public TcpApiTransport(int port, Func<ApiRequest, ApiResponse> handler, Action<string> diagnostics)
        {
            Port = port;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        /// <inheritdoc />
        public string Name => "tcp";

        /// <summary>Admitted connections, including response drain, which have not released their slot.</summary>
        public int ActiveRequests => Volatile.Read(ref _active);

        /// <inheritdoc />
        public int Port { get; private set; }

        /// <inheritdoc />
        public bool IsRunning => _running;

        /// <inheritdoc />
        public void Start()
        {
            ApiDefaults.EnsureMinWorkerThreads();
            var listener = new TcpListener(IPAddress.Loopback, Port);
            ApiSockets.Exclusive(listener.Server, _diagnostics);
            listener.Start(32);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "cwy-api-tcp" };
            _thread.Start();
        }

        /// <inheritdoc />
        public void Stop()
        {
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch (Exception ex)
            {
                _diagnostics("tcp: stopping the listener failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <inheritdoc />
        public void Dispose() => Stop();

        private void Loop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    if (!_running)
                    {
                        break;
                    }

                    _diagnostics("tcp: accept failed: " + ex.GetType().Name + ": " + ex.Message);
                    Thread.Sleep(50);
                    continue;
                }

                var deadline = Deadline(ReadDeadline);
                if (Interlocked.Increment(ref _active) > ApiDefaults.MaxConcurrentRequests)
                {
                    Interlocked.Decrement(ref _active);
                    ThreadPool.QueueUserWorkItem(_ => Busy(client));
                    continue;
                }

                // A thread of its own (at most MaxConcurrentRequests at once), not the pool: requests block while
                // they read and wait, and a pool starved by them would start a queued request only after its
                // deadline had passed, refusing a complete request it never read.
                try
                {
                    new Thread(() => Serve(client, deadline)) { IsBackground = true, Name = "cwy-api-tcp-request" }.Start();
                }
                catch (Exception ex)
                {
                    Interlocked.Decrement(ref _active);
                    _diagnostics("tcp: no thread for a request: " + ex.GetType().Name + ": " + ex.Message);
                    client.Dispose();
                }
            }
        }

        private void Busy(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.SendTimeout = IoTimeoutMs;
                    Write(client.GetStream(), ApiResponse.Error(503, ApiErrorCodes.Unavailable, "too many requests at once"));
                }
                catch (Exception ex)
                {
                    _diagnostics("tcp: busy reply failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private void Serve(TcpClient client, long deadline)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = IoTimeoutMs;
                    client.SendTimeout = IoTimeoutMs;
                    var stream = client.GetStream();
                    var response = Answer(stream, deadline);
                    Write(stream, response);
                    Linger(client);
                }
            }
            catch (Exception ex)
            {
                _diagnostics("tcp: request failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        /// <summary>
        /// Half-closes and discards what the client still sends (an unread body after a refusal)
        /// for a moment, so closing does not reset the connection before the client has read the
        /// response. At most 1 MiB and one second in all, so a client that keeps dripping cannot
        /// hold the slot here either.
        /// </summary>
        private void Linger(TcpClient client)
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
                var deadline = Deadline(TimeSpan.FromMilliseconds(LingerTotalMs));
                var scratch = new byte[8192];
                var drained = 0;
                while (drained < 1024 * 1024)
                {
                    var wait = RemainingMs(deadline);
                    if (wait <= 0)
                    {
                        break;
                    }

                    client.Client.ReceiveTimeout = Math.Min(LingerIdleMs, wait);
                    var n = client.Client.Receive(scratch);
                    if (n <= 0)
                    {
                        break;
                    }

                    drained += n;
                }
            }
            catch (Exception ex)
            {
                // Usually harmless (the client went quiet past the 200 ms drain, or reset); recorded
                // in the trace file only, because the response was already written.
                _diagnostics("tcp: the drain after the response ended with " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private ApiResponse Answer(NetworkStream stream, long deadline)
        {
            var buffer = new byte[ApiDefaults.MaxHeadBytes];
            var filled = 0;
            var headEnd = -1;
            while (headEnd < 0)
            {
                if (filled == buffer.Length)
                {
                    return ApiResponse.Error(431, ApiErrorCodes.BadRequest, "the request head is larger than " + ApiDefaults.MaxHeadBytes + " bytes");
                }

                // Each read waits at most the idle timeout and never past the request's deadline.
                var wait = RemainingMs(deadline);
                if (wait <= 0)
                {
                    return HeadTooSlow();
                }

                int n;
                try
                {
                    stream.ReadTimeout = Math.Min(IoTimeoutMs, wait);
                    n = stream.Read(buffer, filled, buffer.Length - filled);
                }
                catch (IOException ex)
                {
                    return RemainingMs(deadline) <= 0
                        ? HeadTooSlow()
                        : ApiResponse.Error(400, ApiErrorCodes.BadRequest, "the request head could not be read: " + ex.Message);
                }

                if (n <= 0)
                {
                    return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "the connection closed before the request head ended");
                }

                var scanFrom = Math.Max(0, filled - 3);
                filled += n;
                for (var i = scanFrom + 3; i < filled; i++)
                {
                    if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                    {
                        headEnd = i + 1;
                        break;
                    }
                }
            }

            // Latin-1 by hand (one byte, one char): no code-page lookup, which differs between runtimes.
            var chars = new char[headEnd - 4];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)buffer[i];
            }

            var head = new string(chars);
            if (!ApiHttpParser.TryParse(head, out var method, out var target, out var headers, out var problem))
            {
                return ApiResponse.Error(400, ApiErrorCodes.BadRequest, problem);
            }

            if (!ApiHttpParser.TryContentLength(headers.Get("Content-Length"), out var length))
            {
                return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "Content-Length must be one whole number");
            }

            var chunked = headers.Has("Transfer-Encoding");
            var leftover = filled - headEnd;
            var request = new ApiRequest(
                Name,
                method,
                target,
                headers,
                length,
                chunked,
                max => ReadBody(stream, buffer, headEnd, leftover, length, max, deadline));
            return _handler(request);
        }

        private ApiResponse HeadTooSlow()
        {
            _diagnostics("tcp: a client did not send its request head within " + Seconds(ReadDeadline) + " s; disconnected");
            return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "the request head did not arrive within " + Seconds(ReadDeadline) + " s");
        }

        private static ApiBody ReadBody(NetworkStream stream, byte[] buffer, int start, int buffered, long? length, int max, long deadline)
        {
            var total = length ?? 0;
            if (total == 0)
            {
                return ApiBody.Empty;
            }

            if (total > max)
            {
                return ApiBody.TooLarge();
            }

            var tooSlow = "the request did not arrive within " + Seconds(ReadDeadline) + " s";
            var body = new byte[total];
            var have = (int)Math.Min(buffered, total);
            Buffer.BlockCopy(buffer, start, body, 0, have);
            try
            {
                while (have < total)
                {
                    var wait = RemainingMs(deadline);
                    if (wait <= 0)
                    {
                        return ApiBody.TimedOut(tooSlow);
                    }

                    // No idle limit here: a body that stalls outright must reach the deadline (408), not the 2 s idle
                    // timeout the head uses (which answered 400 before the deadline).
                    stream.ReadTimeout = wait;
                    var n = stream.Read(body, have, (int)(total - have));
                    if (n <= 0)
                    {
                        return ApiBody.Failed("the connection closed before the body ended");
                    }

                    have += n;
                }
            }
            catch (IOException ex)
            {
                // The read timeout is the time left to the deadline, so a read that timed out means the deadline passed.
                return RemainingMs(deadline) <= 0 || IsReadTimeout(ex) ? ApiBody.TimedOut(tooSlow) : ApiBody.Failed(ex.Message);
            }

            return ApiBody.Of(body);
        }

        private static bool IsReadTimeout(IOException ex) =>
            ex.InnerException is SocketException socket
            && (socket.SocketErrorCode == SocketError.TimedOut || socket.SocketErrorCode == SocketError.WouldBlock);

        /// <summary>A <see cref="Stopwatch"/> timestamp <paramref name="span"/> from now.</summary>
        private static long Deadline(TimeSpan span) =>
            Stopwatch.GetTimestamp() + (long)(span.TotalSeconds * Stopwatch.Frequency);

        /// <summary>Whole milliseconds left until <paramref name="deadline"/> (0 when it has passed).</summary>
        private static int RemainingMs(long deadline)
        {
            var ms = (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            return ms <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(ms));
        }

        private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

        private static void Write(Stream stream, ApiResponse response)
        {
            var body = response.BodyBytes();
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(ApiResponse.Reason(response.Status)).Append("\r\n")
              .Append("Content-Type: ").Append(response.MediaType).Append("\r\n")
              .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
              .Append("Connection: close\r\n");
            foreach (var header in ApiResponse.CommonHeaders)
            {
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            foreach (var header in response.Headers)
            {
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }

    /// <summary>
    /// The primary transport: <see cref="HttpListener"/> (Mono's managed implementation in the
    /// game, verified in testing) on the literal prefix <c>http://127.0.0.1:&lt;port&gt;/</c>.
    /// Requests are served on thread-pool threads; at most
    /// <see cref="ApiDefaults.MaxConcurrentRequests"/> at once (more get 503). Once HttpListener has delivered
    /// a complete request head, a request with a body has one fixed <see cref="ReadDeadline"/> to finish arriving.
    /// A dedicated watchdog aborts slow contexts, so blocked pool readers cannot starve their own deadline.
    /// </summary>
    public sealed class HttpListenerApiTransport : IApiTransport
    {
        /// <summary>
        /// The longest a request body may take to arrive after HttpListener has delivered its complete head.
        /// HttpListener owns head reads, so this transport cannot apply the TCP transport's head deadline.
        /// </summary>
        public static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(5);

        private readonly Func<ApiRequest, ApiResponse> _handler;
        private readonly Action<string> _diagnostics;
        private readonly object _bodyGate = new object();
        private readonly Dictionary<long, PendingBody> _bodies = new Dictionary<long, PendingBody>();
        private HttpListener? _listener;
        private Thread? _thread;
        private Thread? _bodyWatchdog;
        private volatile bool _running;
        private int _active;
        private long _nextBodyId;
        private int _bodyReaders;
        private readonly Action<HttpListenerResponse> _abortResponse;

        // Test seam (set by reflection in MakerHttpListenerDeadlineTests): runs after a body has been read in full
        // and just before the worker claims it, the one point where the watchdog can still expire it.
        private Action? _beforeBodyClaim = null;

        /// <summary>Creates the transport (not started).</summary>
        /// <param name="port">The port.</param>
        /// <param name="handler">The router.</param>
        /// <param name="diagnostics">Where request failures are reported.</param>
        /// <param name="abortResponse">Optional runtime abort adapter; defaults to HttpListenerResponse.Abort.</param>
        public HttpListenerApiTransport(int port, Func<ApiRequest, ApiResponse> handler, Action<string> diagnostics,
            Action<HttpListenerResponse>? abortResponse = null)
        {
            Port = port;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _abortResponse = abortResponse ?? (response => response.Abort());
        }

        /// <inheritdoc />
        public string Name => "httplistener";

        /// <inheritdoc />
        public int Port { get; }

        /// <inheritdoc />
        public bool IsRunning => _running;

        /// <summary>Requests admitted and not yet finished, including handlers running after their body arrived.</summary>
        public int ActiveRequests => Volatile.Read(ref _active);
        /// <summary>Workers currently inside body acquisition, including a blocked stream read.</summary>
        public int ActiveBodyReads => Volatile.Read(ref _bodyReaders);

        /// <summary>Request bodies currently guarded by the fixed read deadline.</summary>
        public int ReadingBodies
        {
            get { lock (_bodyGate) { return _bodies.Count; } }
        }

        /// <inheritdoc />
        public void Start()
        {
            ApiDefaults.EnsureMinWorkerThreads();
            var listener = new HttpListener();
            listener.Prefixes.Add(ApiDefaults.BaseUrl(Port));
            listener.IgnoreWriteExceptions = true;
            try
            {
                listener.Start();
            }
            catch (Exception)
            {
                Close(listener);
                throw;
            }

            _listener = listener;
            _running = true;
            _bodyWatchdog = new Thread(WatchBodies) { IsBackground = true, Name = "cwy-api-http-body-deadline" };
            _bodyWatchdog.Start();
            _thread = new Thread(Loop) { IsBackground = true, Name = "cwy-api-http" };
            _thread.Start();
        }

        /// <inheritdoc />
        public void Stop()
        {
            lock (_bodyGate)
            {
                _running = false;
                Monitor.PulseAll(_bodyGate);
            }
            var listener = _listener;
            if (listener != null)
            {
                Close(listener);
            }

            AbortBodies();
            var watchdog = _bodyWatchdog;
            if (watchdog != null && !ReferenceEquals(Thread.CurrentThread, watchdog))
            {
                watchdog.Join(1000);
            }
        }

        /// <inheritdoc />
        public void Dispose() => Stop();

        private void Close(HttpListener listener)
        {
            try
            {
                if (listener.IsListening)
                {
                    listener.Stop();
                }

                listener.Close();
            }
            catch (Exception ex)
            {
                _diagnostics("httplistener: closing the listener failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Loop()
        {
            var failures = 0;
            while (_running)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener!.GetContext();
                    failures = 0;
                }
                catch (Exception ex)
                {
                    if (!_running)
                    {
                        break;
                    }

                    _diagnostics("httplistener: GetContext failed: " + ex.GetType().Name + ": " + ex.Message);
                    if (++failures >= 20 || !(_listener?.IsListening ?? false))
                    {
                        _diagnostics("httplistener: the listener stopped accepting; the accept loop ends");
                        break;
                    }

                    Thread.Sleep(50);
                    continue;
                }

                if (Interlocked.Increment(ref _active) > ApiDefaults.MaxConcurrentRequests)
                {
                    Interlocked.Decrement(ref _active);
                    // Closing a rejected entity can drain it on Mono. Guard the refusal itself, including
                    // bodyless responses, so the accept loop cannot remain blocked by a non-reading client.
                    var refusal = ArmBody(context);
                    if (refusal is null) { Abort(context); continue; }
                    try { Respond(context, ApiResponse.Error(503, ApiErrorCodes.Unavailable, "too many requests at once"), refusal); }
                    finally { DisarmBody(refusal); }
                    continue;
                }

                PendingBody? pending = null;
                try
                {
                    if (context.Request.HasEntityBody)
                    {
                        pending = ArmBody(context);
                    }
                }
                catch (Exception ex)
                {
                    _diagnostics("httplistener: inspecting the request body failed: " + ex.GetType().Name + ": " + ex.Message);
                    Abort(context);
                    DisarmBody(pending);
                    Interlocked.Decrement(ref _active);
                    continue;
                }

                if (!_running)
                {
                    Abort(context);
                    DisarmBody(pending);
                    Interlocked.Decrement(ref _active);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Serve(context, pending));
            }
        }

        private void Serve(HttpListenerContext context, PendingBody? pending)
        {
            try
            {
                if (pending?.TimedOut == true) return; // The watchdog owns this response.
                if (!_running) { Abort(context); return; }
                var r = context.Request;
                var headers = new ApiHeaders();
                foreach (var key in r.Headers.AllKeys)
                {
                    if (key != null)
                    {
                        headers.Add(key, r.Headers.Get(key));
                    }
                }

                var chunked = headers.Has("Transfer-Encoding");
                long? length = headers.Has("Content-Length") && r.ContentLength64 >= 0 ? r.ContentLength64 : (long?)null;
                var request = new ApiRequest(
                    Name,
                    r.HttpMethod,
                    r.RawUrl ?? string.Empty,
                    headers,
                    length,
                    chunked,
                    max => ReadBody(r, length, max, pending));
                var result = _handler(request);
                Respond(context, result, pending);
            }
            catch (Exception ex)
            {
                _diagnostics("httplistener: request failed: " + ex.GetType().Name + ": " + ex.Message);
                if (pending?.TimedOut != true) Abort(context, sendFailure: pending?.ResponseStarted != true);
            }
            finally
            {
                DisarmBody(pending);
                Interlocked.Decrement(ref _active);
            }
        }

        private ApiBody ReadBody(HttpListenerRequest r, long? length, int max, PendingBody? pending)
        {
            if (!r.HasEntityBody)
            {
                DisarmBody(pending);
                return ApiBody.Empty;
            }

            // The context owns this stream. Disposing an incompletely consumed entity here can drain
            // it on some runtimes before the refusal reaches Respond.
            return ReadBodyStream(r.InputStream, length, max, pending);
        }

        private ApiBody ReadBodyStream(Stream input, long? length, int max, PendingBody? pending)
        {
            Interlocked.Increment(ref _bodyReaders);
            try
            {
                if (length is long declared && declared > max)
                {
                    return ApiBody.TooLarge();
                }

                var tooSlow = "the request body did not arrive within "
                    + ReadDeadline.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s";
                using (var copy = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (pending?.TimedOut == true)
                        {
                            return ApiBody.TimedOut(tooSlow);
                        }

                        if (copy.Length + n > max)
                        {
                            return ApiBody.TooLarge();
                        }

                        copy.Write(buffer, 0, n);
                    }

                    if (length is long expected && copy.Length != expected)
                    {
                        return ApiBody.Failed("the connection closed before the body ended");
                    }

                    _beforeBodyClaim?.Invoke();
                    // The deadline check and the claim are one step under the watchdog's gate, so exactly one side wins:
                    // either the watchdog has already answered 408 (the handler must not act on this body), or the
                    // body leaves the watchdog's watch and the handler owns the request.
                    if (!Claim(pending))
                    {
                        return ApiBody.TimedOut(tooSlow);
                    }

                    return ApiBody.Of(copy.ToArray());
                }
            }
            catch (Exception ex)
            {
                if (pending?.TimedOut == true)
                {
                    return ApiBody.TimedOut("the request body did not arrive within "
                        + ReadDeadline.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
                }

                return ApiBody.Failed(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // A refusal/failed read may leave an entity for Mono to drain on close. Keep its guard
                // through Respond and Serve.finally. Only a claimed (fully consumed) entity leaves it early.
                Interlocked.Decrement(ref _bodyReaders);
            }
        }

        /// <summary>
        /// Takes a fully read body away from the watchdog, unless the watchdog expired it first (then false: the
        /// client already has 408). Checked and removed under <c>_bodyGate</c>, where the watchdog expires bodies.
        /// </summary>
        private bool Claim(PendingBody? body)
        {
            if (body is null)
            {
                return true;
            }

            lock (_bodyGate)
            {
                if (body.TimedOut)
                {
                    return false;
                }

                if (_bodies.Remove(body.Id)) Monitor.PulseAll(_bodyGate);
                return true;
            }
        }

        private PendingBody? ArmBody(HttpListenerContext context)
        {
            var body = new PendingBody(
                Interlocked.Increment(ref _nextBodyId),
                context,
                Stopwatch.GetTimestamp() + (long)Math.Ceiling(ReadDeadline.TotalSeconds * Stopwatch.Frequency));
            lock (_bodyGate)
            {
                if (!_running) return null;
                _bodies.Add(body.Id, body);
                Monitor.PulseAll(_bodyGate);
            }

            return body;
        }

        private void DisarmBody(PendingBody? body)
        {
            if (body is null)
            {
                return;
            }

            lock (_bodyGate)
            {
                if (_bodies.Remove(body.Id)) Monitor.PulseAll(_bodyGate);
            }
        }

        private void WatchBodies()
        {
            while (_running)
            {
                List<PendingBody>? expired = null;
                var waitMs = Timeout.Infinite;
                var now = Stopwatch.GetTimestamp();
                lock (_bodyGate)
                {
                    if (!_running) return;
                    foreach (var body in _bodies.Values)
                    {
                        var left = body.DeadlineTicks - now;
                        if (left <= 0)
                        {
                            expired ??= new List<PendingBody>();
                            expired.Add(body);
                        }
                        else
                        {
                            var candidate = (int)Math.Max(1, Math.Min(int.MaxValue, Math.Ceiling(left * 1000d / Stopwatch.Frequency)));
                            waitMs = waitMs == Timeout.Infinite ? candidate : Math.Min(waitMs, candidate);
                        }
                    }

                    if (expired != null)
                    {
                        foreach (var body in expired)
                        {
                            _bodies.Remove(body.Id);
                            body.TimedOut = true;
                        }
                    }
                    else
                    {
                        Monitor.Wait(_bodyGate, waitMs);
                        continue;
                    }
                }

                if (expired != null)
                {
                    foreach (var body in expired)
                    {
                        // Record what the client was sent: a request that never reaches a handler has no audit line.
                        _diagnostics("httplistener: " + Describe(body.Context) + ": the request body did not arrive within "
                                     + ReadDeadline.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s; "
                                     + (body.ResponseStarted ? "disconnected (its response had already started)" : "answered 408 request_timeout and disconnected"));
                        Abort(body.Context, timedOut: true, sendFailure: !body.ResponseStarted);
                    }

                    continue;
                }

            }
        }

        private void AbortBodies()
        {
            List<PendingBody> pending;
            lock (_bodyGate)
            {
                pending = new List<PendingBody>(_bodies.Values);
                _bodies.Clear();
                foreach (var body in pending) body.TimedOut = true;
            }

            foreach (var body in pending)
            {
                Abort(body.Context, sendFailure: !body.ResponseStarted);
            }
        }

        private sealed class PendingBody
        {
            internal PendingBody(long id, HttpListenerContext context, long deadlineTicks)
            {
                Id = id;
                Context = context;
                DeadlineTicks = deadlineTicks;
            }

            internal long Id { get; }
            internal HttpListenerContext Context { get; }
            internal long DeadlineTicks { get; }
            internal volatile bool TimedOut;
            internal bool ResponseStarted;
        }

        private void Respond(HttpListenerContext context, ApiResponse response, PendingBody? pending = null)
        {
            bool timedOut;
            lock (_bodyGate)
            {
                timedOut = pending?.TimedOut == true;
                if (!timedOut && pending != null) pending.ResponseStarted = true;
            }

            if (timedOut)
            {
                // The audit line has the handler's status; the client got the watchdog's 408.
                _diagnostics("httplistener: " + Describe(context) + ": the handler's "
                             + response.Status.ToString(CultureInfo.InvariantCulture)
                             + " answer was not sent; the client already had 408 request_timeout");
                return;
            }
            try
            {
                var bytes = response.BodyBytes();
                var w = context.Response;
                w.StatusCode = response.Status;
                w.StatusDescription = ApiResponse.Reason(response.Status);
                w.ContentType = response.MediaType;
                foreach (var header in ApiResponse.CommonHeaders)
                {
                    SetHeader(w, header);
                }

                foreach (var header in response.Headers)
                {
                    SetHeader(w, header);
                }

                w.ContentLength64 = bytes.Length;
                w.OutputStream.Write(bytes, 0, bytes.Length);
                w.OutputStream.Close();
                w.Close();
            }
            catch (Exception ex)
            {
                _diagnostics("httplistener: writing the response failed: " + ex.GetType().Name + ": " + ex.Message);
                Abort(context, sendFailure: false);
            }
        }

        /// <summary>The method and path (no query, at most 120 characters) for a diagnostics line.</summary>
        private static string Describe(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.RawUrl ?? string.Empty;
                var query = path.IndexOf('?');
                if (query >= 0) path = path.Substring(0, query);
                if (path.Length > 120) path = path.Substring(0, 120);
                return context.Request.HttpMethod + " " + path;
            }
            catch (Exception)
            {
                return "a request";
            }
        }

        private void SetHeader(HttpListenerResponse w, KeyValuePair<string, string> header)
        {
            try
            {
                w.Headers[header.Key] = header.Value;
            }
            catch (Exception ex)
            {
                // Some runtimes restrict a few response headers on HttpListener; the response is
                // still correct without them (the status and body carry the meaning).
                _diagnostics("httplistener: header " + header.Key + " not set: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Abort(HttpListenerContext context, bool timedOut = false, bool sendFailure = true)
        {
            // Mono's Abort can send the untouched default 200 response. Commit a failure head/body first.
            // Do not Close the response stream here: Mono may drain the stalled request on Close.
            if (sendFailure) try
            {
                var response = ApiResponse.Error(timedOut ? 408 : 503, timedOut ? ApiErrorCodes.RequestTimeout : ApiErrorCodes.Unavailable,
                    timedOut ? "The request body did not arrive before its deadline." : "The request was aborted.");
                var bytes = response.BodyBytes();
                var output = context.Response;
                output.StatusCode = response.Status;
                output.StatusDescription = ApiResponse.Reason(response.Status);
                output.KeepAlive = false;
                output.ContentType = response.MediaType;
                foreach (var header in ApiResponse.CommonHeaders)
                {
                    SetHeader(output, header);
                }

                output.ContentLength64 = bytes.Length;
                output.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch { /* Already sent/closed responses cannot be rewritten; abort still releases the reader. */ }
            try
            {
                _abortResponse(context.Response);
            }
            catch (Exception ex)
            {
                _diagnostics("httplistener: abort failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>Socket options shared by the port probe and the TCP transport.</summary>
    internal static class ApiSockets
    {
        /// <summary>
        /// Makes a not-yet-bound socket refuse to share its port: no <c>SO_REUSEADDR</c> on Unix (so
        /// binding 127.0.0.1 fails while another program listens on 0.0.0.0 at the same port),
        /// exclusive use on Windows.
        /// </summary>
        internal static void Exclusive(Socket socket, Action<string> diagnostics)
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    socket.ExclusiveAddressUse = true;
                }
                else
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                }
            }
            catch (Exception ex)
            {
                diagnostics("socket: exclusive-address option not applied: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>Why the API did not start.</summary>
    public enum ApiStartProblem
    {
        /// <summary>It started.</summary>
        None = 0,

        /// <summary>The port is outside 1024-65535.</summary>
        InvalidPort = 1,

        /// <summary>Another program (or listener) holds the port: the API stays off; it never moves to another port.</summary>
        PortTaken = 2,

        /// <summary>Every transport allowed by the mode failed.</summary>
        Failed = 3,
    }

    /// <summary>The outcome of <see cref="ApiServer.Start"/>. Immutable.</summary>
    public sealed class ApiStartResult
    {
        internal ApiStartResult(IApiTransport? transport, ApiStartProblem problem, string detail, string? fallbackReason)
        {
            Transport = transport;
            Problem = problem;
            Detail = detail;
            FallbackReason = fallbackReason;
        }

        /// <summary>The running transport, or null.</summary>
        public IApiTransport? Transport { get; }

        /// <summary>Why it did not start; <see cref="ApiStartProblem.None"/> when it did.</summary>
        public ApiStartProblem Problem { get; }

        /// <summary>One line for people.</summary>
        public string Detail { get; }

        /// <summary>Why HttpListener was not used (the TCP fallback is running), or null.</summary>
        public string? FallbackReason { get; }

        /// <summary>True when a transport is running.</summary>
        public bool IsRunning => Transport != null;
    }

    /// <summary>
    /// Starts the listener for a mode: <see cref="ApiTransportMode.Auto"/>
    /// probes the port, then tries HttpListener and falls back to the TCP server when HttpListener
    /// fails for any reason other than a taken port. A taken port leaves the API off (no port
    /// hopping). Only <c>127.0.0.1</c> is ever bound.
    /// </summary>
    public static class ApiServer
    {
        /// <summary>Starts a transport.</summary>
        /// <param name="port">The port.</param>
        /// <param name="mode">Which transports may be used.</param>
        /// <param name="handler">The router.</param>
        /// <param name="diagnostics">Where transport failures are reported.</param>
        public static ApiStartResult Start(int port, ApiTransportMode mode, Func<ApiRequest, ApiResponse> handler, Action<string> diagnostics)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            diagnostics ??= _ => { };
            if (!ApiDefaults.IsValidPort(port))
            {
                return new ApiStartResult(null, ApiStartProblem.InvalidPort, "port " + port + " is outside " + ApiDefaults.MinPort + "-" + ApiDefaults.MaxPort, null);
            }

            var probe = ApiPortProbe.Check(port, diagnostics, out var probeDetail);
            if (probe == ApiPortState.Taken)
            {
                return new ApiStartResult(null, ApiStartProblem.PortTaken, "port " + port + " on 127.0.0.1 is taken by another program", null);
            }

            if (probe == ApiPortState.Error)
            {
                diagnostics("port probe: " + probeDetail);
            }

            string? fallbackReason = null;
            if (mode != ApiTransportMode.Tcp)
            {
                var http = new HttpListenerApiTransport(port, handler, diagnostics);
                try
                {
                    http.Start();
                    return new ApiStartResult(http, ApiStartProblem.None, "listening on " + ApiDefaults.BaseUrl(port) + " (httplistener)", null);
                }
                catch (Exception ex)
                {
                    if (ApiPortProbe.IsAddressInUse(ex))
                    {
                        return new ApiStartResult(null, ApiStartProblem.PortTaken, "port " + port + " on 127.0.0.1 is taken: " + ex.Message, null);
                    }

                    fallbackReason = "HttpListener failed: " + ex.GetType().Name + ": " + ex.Message;
                    if (mode == ApiTransportMode.HttpListener)
                    {
                        return new ApiStartResult(null, ApiStartProblem.Failed, fallbackReason + " ([Api] Transport=HttpListener: no fallback)", null);
                    }
                }
            }

            var tcp = new TcpApiTransport(port, handler, diagnostics);
            try
            {
                tcp.Start();
                return new ApiStartResult(tcp, ApiStartProblem.None, "listening on " + ApiDefaults.BaseUrl(port) + " (tcp)", fallbackReason);
            }
            catch (Exception ex)
            {
                var detail = "TcpListener failed: " + ex.GetType().Name + ": " + ex.Message;
                return ApiPortProbe.IsAddressInUse(ex)
                    ? new ApiStartResult(null, ApiStartProblem.PortTaken, "port " + port + " on 127.0.0.1 is taken: " + ex.Message, null)
                    : new ApiStartResult(null, ApiStartProblem.Failed, (fallbackReason != null ? fallbackReason + "; " : string.Empty) + detail, null);
            }
        }
    }

    /// <summary>What the port probe found.</summary>
    public enum ApiPortState
    {
        /// <summary>127.0.0.1:&lt;port&gt; could be bound and was released.</summary>
        Free = 0,

        /// <summary>Another socket holds the port.</summary>
        Taken = 1,

        /// <summary>The probe failed for another reason (the start goes ahead and reports its own failure).</summary>
        Error = 2,
    }

    /// <summary>Checks whether the API's port is free before a listener binds it, and classifies bind failures.</summary>
    public static class ApiPortProbe
    {
        private static readonly int[] InUseCodes = { 48, 98, 10048, 183 };

        /// <summary>
        /// Two checks. First a connection attempt to <c>127.0.0.1:&lt;port&gt;</c>: if anything accepts
        /// it, the port is taken. This catches a program listening on all interfaces (<c>0.0.0.0</c>),
        /// which a bind test misses - on macOS a more specific 127.0.0.1 bind succeeds next to it
        /// (measured 2026-10-02) and would silently take over that program's loopback traffic. Then
        /// a bind and release of <c>127.0.0.1:&lt;port&gt;</c> with exclusive-address options.
        /// </summary>
        /// <param name="port">The port.</param>
        /// <param name="diagnostics">Where option failures are reported.</param>
        /// <param name="detail">What failed, for <see cref="ApiPortState.Error"/>.</param>
        public static ApiPortState Check(int port, Action<string> diagnostics, out string detail)
        {
            detail = string.Empty;
            if (SomethingAccepts(port, diagnostics ?? (_ => { })))
            {
                return ApiPortState.Taken;
            }

            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    ApiSockets.Exclusive(socket, diagnostics ?? (_ => { }));
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    socket.Listen(1);
                }

                return ApiPortState.Free;
            }
            catch (Exception ex)
            {
                if (IsAddressInUse(ex))
                {
                    return ApiPortState.Taken;
                }

                detail = ex.GetType().Name + ": " + ex.Message;
                return ApiPortState.Error;
            }
        }

        private static bool SomethingAccepts(int port, Action<string> diagnostics)
        {
            if (OnWindows)
            {
                // Windows refuses a connection to a closed loopback port only after about two seconds (it retries;
                // measured 2026-11-01: 2045 ms on Windows 11, 0 ms on macOS), so the short connection probe below would
                // call every free port taken. The TCP table answers at once and lists listeners on 127.0.0.1 and on all
                // interfaces alike.
                var listening = ListenerInTcpTable(port, diagnostics);
                if (listening.HasValue)
                {
                    return listening.Value;
                }
            }

            try
            {
                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    var attempt = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!attempt.AsyncWaitHandle.WaitOne(500))
                    {
                        if (!SilenceMeansTaken(OnWindows))
                        {
                            diagnostics("port probe: connecting to 127.0.0.1:" + port + " got no answer within 500 ms; on Windows that is the normal wait for a free port, so the bind check decides");
                            return false;
                        }

                        // A listener with a full backlog does not answer at once; nothing on loopback
                        // is that slow otherwise, so treat silence as "in use".
                        diagnostics("port probe: connecting to 127.0.0.1:" + port + " neither succeeded nor failed within 500 ms; treated as taken");
                        return true;
                    }

                    client.EndConnect(attempt);
                    return client.Connected;
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // The normal answer for a free port: nobody listens there (one trace record per start).
                diagnostics("port probe: 127.0.0.1:" + port + " refused a connection, so nothing listens there (" + ex.SocketErrorCode + ")");
                return false;
            }
            catch (Exception ex)
            {
                diagnostics("port probe: connecting to 127.0.0.1:" + port + " failed unexpectedly: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>True on Windows, where a refused loopback connection takes about two seconds.</summary>
        internal static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// What a connection attempt that neither succeeded nor failed within the wait means: on Windows nothing (a
        /// free port is refused only after about two seconds), elsewhere a listener with a full backlog, so taken.
        /// </summary>
        /// <param name="windows">True on Windows.</param>
        public static bool SilenceMeansTaken(bool windows) => !windows;

        /// <summary>
        /// Whether the TCP table lists a listener on <paramref name="port"/> on 127.0.0.1 or on all interfaces; null
        /// when the table cannot be read (the connection probe takes over).
        /// </summary>
        private static bool? ListenerInTcpTable(int port, Action<string> diagnostics)
        {
            try
            {
                foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                {
                    if (endpoint.Port == port
                        && (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)))
                    {
                        diagnostics("port probe: the TCP table lists a listener on " + endpoint);
                        return true;
                    }
                }

                diagnostics("port probe: the TCP table lists no listener on port " + port);
                return false;
            }
            catch (Exception ex)
            {
                diagnostics("port probe: the TCP table could not be read (" + ex.GetType().Name + ": " + ex.Message + "); trying a connection");
                return null;
            }
        }

        /// <summary>
        /// True when an exception (or one inside it) says the address is in use: a socket error
        /// <c>AddressAlreadyInUse</c>, an <c>HttpListenerException</c> with EADDRINUSE / WSAEADDRINUSE /
        /// ERROR_ALREADY_EXISTS, or Mono's "another listener" message for the same prefix.
        /// </summary>
        /// <param name="error">The exception.</param>
        public static bool IsAddressInUse(Exception? error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    return true;
                }

                if (e is HttpListenerException http && Array.IndexOf(InUseCodes, http.ErrorCode) >= 0)
                {
                    return true;
                }

                var message = e.Message ?? string.Empty;
                if (message.IndexOf("another listener", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("already in use", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
