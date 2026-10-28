using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>A response read by <see cref="ApiLoopbackClient"/>. Immutable.</summary>
    public sealed class ApiLoopbackResponse
    {
        internal ApiLoopbackResponse(int status, ApiHeaders headers, string body)
        {
            Status = status;
            Headers = headers;
            Body = body;
        }

        /// <summary>The HTTP status, or 0 when the request failed before a status line arrived.</summary>
        public int Status { get; }

        /// <summary>The response headers.</summary>
        public ApiHeaders Headers { get; }

        /// <summary>The body text (UTF-8), or the failure text when <see cref="Status"/> is 0.</summary>
        public string Body { get; }
    }

    /// <summary>
    /// A minimal HTTP/1.1 client for <c>127.0.0.1</c> only, with no proxy lookup: the API's smoke
    /// self-test and the unit tests use it. One request per connection; the API always answers
    /// with <c>Content-Length</c>, so chunked responses are not decoded.
    /// </summary>
    public static class ApiLoopbackClient
    {
        /// <summary>Sends one request to <c>127.0.0.1:&lt;port&gt;</c>. Never throws: a failure is status 0.</summary>
        /// <param name="port">The port.</param>
        /// <param name="method">The method.</param>
        /// <param name="target">The request target, e.g. <c>/v1/info</c>.</param>
        /// <param name="headers">Header lines to send (name, value); a <c>Host</c> header is added unless one is given.</param>
        /// <param name="body">The body, or null.</param>
        /// <param name="timeoutMs">Connect, send and receive timeout.</param>
        public static ApiLoopbackResponse Send(
            int port,
            string method,
            string target,
            IEnumerable<KeyValuePair<string, string>>? headers = null,
            string? body = null,
            int timeoutMs = 2000)
        {
            try
            {
                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    client.ReceiveTimeout = timeoutMs;
                    client.SendTimeout = timeoutMs;
                    var connect = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        return new ApiLoopbackResponse(0, new ApiHeaders(), "connect timed out");
                    }

                    client.EndConnect(connect);
                    var bodyBytes = body is null ? new byte[0] : Encoding.UTF8.GetBytes(body);
                    var sb = new StringBuilder();
                    sb.Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
                    var hasHost = false;
                    if (headers != null)
                    {
                        foreach (var h in headers)
                        {
                            hasHost |= string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase);
                            sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                        }
                    }

                    if (!hasHost)
                    {
                        sb.Append("Host: ").Append(ApiDefaults.HostAuthority(port)).Append("\r\n");
                    }

                    if (body != null)
                    {
                        sb.Append("Content-Length: ").Append(bodyBytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                    }

                    sb.Append("Connection: close\r\n\r\n");
                    var stream = client.GetStream();
                    var head = Encoding.ASCII.GetBytes(sb.ToString());
                    stream.Write(head, 0, head.Length);
                    stream.Write(bodyBytes, 0, bodyBytes.Length);
                    stream.Flush();

                    using (var all = new MemoryStream())
                    {
                        var buffer = new byte[8192];
                        int n;
                        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            all.Write(buffer, 0, n);
                        }

                        return Parse(all.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                return new ApiLoopbackResponse(0, new ApiHeaders(), ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static ApiLoopbackResponse Parse(byte[] raw)
        {
            var text = Encoding.UTF8.GetString(raw);
            var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0)
            {
                return new ApiLoopbackResponse(0, new ApiHeaders(), "no response head");
            }

            var lines = text.Substring(0, split).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var status = 0;
            var parts = lines[0].Split(' ');
            if (parts.Length >= 2)
            {
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out status);
            }

            var headers = new ApiHeaders();
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers.Add(lines[i].Substring(0, colon).Trim(), lines[i].Substring(colon + 1).Trim());
                }
            }

            var bodyText = text.Substring(split + 4);
            return new ApiLoopbackResponse(status, headers, bodyText);
        }
    }
}
