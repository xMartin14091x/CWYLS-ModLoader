using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using CwyLs.ModLoader.Core.Api;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// DS-10b revision (CONTRACT.md: Host must be <c>127.0.0.1:&lt;port&gt;</c>): the HttpListener transport registers
    /// the literal 127.0.0.1 prefix only, never a <c>localhost</c> one (Mono binds every interface when its own lookup
    /// of a host name fails), so <c>Host: localhost</c> is refused by HttpListener before the router, and every
    /// listener on the port is on loopback. Free ephemeral ports on 127.0.0.1, never 7870. Measured on .NET's managed
    /// HttpListener (the port of Mono's); the game's Mono needs the in-game check (DS-07).
    /// </summary>
    [Collection(SocketTestCollection.Name)]
    public sealed class ApiTransportLoopbackTests
    {
        [Fact]
        public void The_httplistener_transport_serves_127_0_0_1_only_and_listens_on_loopback_only()
        {
            var run = StartWithRetry(ApiTestKit.FreePort);
            var port = run.Port;
            var rig = run.Rig;
            var started = run.Started;
            try
            {
                Assert.True(started.IsRunning);
                Assert.IsType<HttpListenerApiTransport>(started);
                var authority = port.ToString(CultureInfo.InvariantCulture);

                // 401 = the router answered (no token).
                Assert.Equal(401, StatusFor(port, "127.0.0.1:" + authority));
                Assert.Equal(1L, rig.Router.Counters.Total);

                // No localhost prefix: HttpListener refuses the Host itself (Mono 400, .NET 400/404); the router never sees it.
                var localhost = StatusFor(port, "localhost:" + authority);
                Assert.True(localhost == 400 || localhost == 404, "Host: localhost answered " + localhost);
                Assert.Equal(1L, rig.Router.Counters.Total);

                IPEndPoint[] listeners;
                try
                {
                    listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                }
                catch (PlatformNotSupportedException)
                {
                    return;
                }

                var ours = listeners.Where(e => e.Port == port).ToList();
                Assert.NotEmpty(ours);
                Assert.All(ours, e => Assert.True(IPAddress.IsLoopback(e.Address), "listening on " + e));
            }
            finally
            {
                started?.Stop();
            }
        }

        [Fact]
        public void A_port_taken_start_result_is_retried_with_a_fresh_port()
        {
            using var holder = new TcpListener(IPAddress.Loopback, 0);
            holder.Start();
            var taken = ((IPEndPoint)holder.LocalEndpoint).Port;
            var calls = 0;

            var run = StartWithRetry(() => calls++ == 0 ? taken : ApiTestKit.FreePort());
            try
            {
                Assert.True(calls >= 2);
                Assert.NotEqual(taken, run.Port);
                Assert.True(run.Started.IsRunning);
            }
            finally
            {
                run.Started?.Stop();
            }
        }

        internal static (int Port, MakerRig Rig, IApiTransport Started) StartWithRetry(
            Func<int> nextPort, int attempts = 10, Func<ApiRequest, ApiResponse>? handler = null, Action<string>? diagnostics = null)
        {
            var run = SocketTestServer.Start(ApiTransportMode.HttpListener, port => new MakerRig(port: port),
                (rig, request) => handler != null ? handler(request) : rig.Router.Handle(request), diagnostics, nextPort, attempts);
            return (run.Port, run.Rig, run.Transport);
        }

        /// <summary>Sends <c>GET /v1/maker/status</c> to 127.0.0.1 with the given Host and returns the status code.</summary>
        private static int StatusFor(int port, string host)
        {
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                client.Connect(IPAddress.Loopback, port);
                var stream = client.GetStream();
                var head = Encoding.ASCII.GetBytes("GET /v1/maker/status HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n");
                stream.Write(head, 0, head.Length);
                using (var all = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    try
                    {
                        int n;
                        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            all.Write(buffer, 0, n);
                        }
                    }
                    catch (IOException)
                    {
                        // A reset after the reply: what arrived is the reply.
                    }

                    var text = Encoding.ASCII.GetString(all.ToArray());
                    var parts = text.Split(' ');
                    Assert.True(parts.Length > 1 && text.StartsWith("HTTP/1.", StringComparison.Ordinal), "no HTTP reply: " + text);
                    return int.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture);
                }
            }
        }
    }
}
