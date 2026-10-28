using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
    /// IPv4 loopback only. One request per connection (<c>Connection: close</c>), head capped at
    /// <see cref="ApiDefaults.MaxHeadBytes"/>, 2 s read/write timeouts, at most
    /// <see cref="ApiDefaults.MaxConcurrentRequests"/> requests at once (more get 503).
    /// </summary>
    public sealed class TcpApiTransport : IApiTransport
    {
        private const int IoTimeoutMs = 2000;

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

        /// <inheritdoc />
        public int Port { get; }

        /// <inheritdoc />
        public bool IsRunning => _running;

        /// <inheritdoc />
        public void Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, Port);
            ApiSockets.Exclusive(listener.Server, _diagnostics);
            listener.Start(32);
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

                if (Interlocked.Increment(ref _active) > ApiDefaults.MaxConcurrentRequests)
                {
                    Interlocked.Decrement(ref _active);
                    ThreadPool.QueueUserWorkItem(_ => Busy(client));
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ => Serve(client));
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

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = IoTimeoutMs;
                    client.SendTimeout = IoTimeoutMs;
                    var stream = client.GetStream();
                    var response = Answer(stream);
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
        /// response.
        /// </summary>
        private void Linger(TcpClient client)
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
                client.Client.ReceiveTimeout = 200;
                var scratch = new byte[8192];
                var drained = 0;
                int n;
                while (drained < 1024 * 1024 && (n = client.Client.Receive(scratch)) > 0)
                {
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

        private ApiResponse Answer(NetworkStream stream)
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

                int n;
                try
                {
                    n = stream.Read(buffer, filled, buffer.Length - filled);
                }
                catch (IOException ex)
                {
                    return ApiResponse.Error(400, ApiErrorCodes.BadRequest, "the request head could not be read: " + ex.Message);
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
                max => ReadBody(stream, buffer, headEnd, leftover, length, max));
            return _handler(request);
        }

        private static ApiBody ReadBody(NetworkStream stream, byte[] buffer, int start, int buffered, long? length, int max)
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

            var body = new byte[total];
            var have = (int)Math.Min(buffered, total);
            Buffer.BlockCopy(buffer, start, body, 0, have);
            try
            {
                while (have < total)
                {
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
                return ApiBody.Failed(ex.Message);
            }

            return ApiBody.Of(body);
        }

        private static void Write(Stream stream, ApiResponse response)
        {
            var body = Encoding.UTF8.GetBytes(response.Body);
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(ApiResponse.Reason(response.Status)).Append("\r\n")
              .Append("Content-Type: ").Append(ApiResponse.ContentType).Append("\r\n")
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
    /// <see cref="ApiDefaults.MaxConcurrentRequests"/> at once (more get 503).
    /// </summary>
    public sealed class HttpListenerApiTransport : IApiTransport
    {
        private readonly Func<ApiRequest, ApiResponse> _handler;
        private readonly Action<string> _diagnostics;
        private HttpListener? _listener;
        private Thread? _thread;
        private volatile bool _running;
        private int _active;

        /// <summary>Creates the transport (not started).</summary>
        /// <param name="port">The port.</param>
        /// <param name="handler">The router.</param>
        /// <param name="diagnostics">Where request failures are reported.</param>
        public HttpListenerApiTransport(int port, Func<ApiRequest, ApiResponse> handler, Action<string> diagnostics)
        {
            Port = port;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        /// <inheritdoc />
        public string Name => "httplistener";

        /// <inheritdoc />
        public int Port { get; }

        /// <inheritdoc />
        public bool IsRunning => _running;

        /// <inheritdoc />
        public void Start()
        {
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
            _thread = new Thread(Loop) { IsBackground = true, Name = "cwy-api-http" };
            _thread.Start();
        }

        /// <inheritdoc />
        public void Stop()
        {
            _running = false;
            var listener = _listener;
            if (listener != null)
            {
                Close(listener);
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
                    ThreadPool.QueueUserWorkItem(_ => Respond(context, ApiResponse.Error(503, ApiErrorCodes.Unavailable, "too many requests at once")));
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ => Serve(context));
            }
        }

        private void Serve(HttpListenerContext context)
        {
            try
            {
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
                    max => ReadBody(r, length, max));
                Respond(context, _handler(request));
            }
            catch (Exception ex)
            {
                _diagnostics("httplistener: request failed: " + ex.GetType().Name + ": " + ex.Message);
                Abort(context);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static ApiBody ReadBody(HttpListenerRequest r, long? length, int max)
        {
            if (!r.HasEntityBody)
            {
                return ApiBody.Empty;
            }

            if (length is long declared && declared > max)
            {
                return ApiBody.TooLarge();
            }

            try
            {
                using (var input = r.InputStream)
                using (var copy = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (copy.Length + n > max)
                        {
                            return ApiBody.TooLarge();
                        }

                        copy.Write(buffer, 0, n);
                    }

                    return ApiBody.Of(copy.ToArray());
                }
            }
            catch (Exception ex)
            {
                return ApiBody.Failed(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Respond(HttpListenerContext context, ApiResponse response)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(response.Body);
                var w = context.Response;
                w.StatusCode = response.Status;
                w.StatusDescription = ApiResponse.Reason(response.Status);
                w.ContentType = ApiResponse.ContentType;
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
                Abort(context);
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

        private void Abort(HttpListenerContext context)
        {
            try
            {
                context.Response.Abort();
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
            try
            {
                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    var attempt = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!attempt.AsyncWaitHandle.WaitOne(500))
                    {
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
