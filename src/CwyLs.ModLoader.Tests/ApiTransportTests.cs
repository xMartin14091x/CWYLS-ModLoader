using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Api;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// S-API over real sockets on 127.0.0.1 only, on free ephemeral ports (never 7870; no test
    /// opens or connects to any other address): both transports behind the same router, the
    /// port-taken rule and the release of the port on stop. In the TraceBus collection: a started action writes a
    /// trace record.
    /// </summary>
    [Collection(SocketTestCollection.Name)]
    public class ApiTransportTests
    {
        [Theory]
        [InlineData(ApiTransportMode.Tcp, "tcp")]
        [InlineData(ApiTransportMode.Auto, "httplistener")]
        public void Both_transports_serve_the_router_with_all_checks(ApiTransportMode mode, string expected)
        {
            var started = SocketTestServer.Start(mode, port => new RouterRig(port), (rig, request) => rig.Router.Handle(request));
            var port = started.Port; var rig = started.Rig;
            try
            {
                Assert.True(started.Transport.IsRunning);
                Assert.Equal(expected, started.Transport!.Name);

                var auth = Auth(rig);
                var ok = ApiLoopbackClient.Send(port, "GET", "/v1/info", new[] { auth });
                Assert.Equal(200, ok.Status);
                Assert.StartsWith("application/json", ok.Headers.Get("Content-Type"));
                Assert.Equal("no-store", ok.Headers.Get("Cache-Control"));
                Assert.Null(ok.Headers.Get("Access-Control-Allow-Origin"));

                Assert.Equal(403, ApiLoopbackClient.Send(port, "GET", "/v1/info", new[] { auth, H("Origin", "http://evil.example") }).Status);
                Assert.Equal(401, ApiLoopbackClient.Send(port, "GET", "/v1/info").Status);
                // The router answers 421; HttpListener refuses a foreign Host before the router
                // (Mono 6.13: 400, measured in game; .NET 10: 404, measured here).
                var wrongHost = ApiLoopbackClient.Send(port, "GET", "/v1/info", new[] { auth, H("Host", "localhost:" + port) }).Status;
                Assert.True(wrongHost == 421 || wrongHost == 400 || wrongHost == 404, "wrong Host answered " + wrongHost);
            }
            finally
            {
                started.Transport?.Stop();
            }
        }

        [Fact]
        public void The_tcp_transport_reads_bodies_and_refuses_bad_heads()
        {
            var started = SocketTestServer.Start(ApiTransportMode.Tcp, port => new RouterRig(port, allowActions: true),
                (rig, request) => rig.Router.Handle(request));
            var port = started.Port; var rig = started.Rig;
            rig.Services.UseRunner(new FakeRunner());
            rig.Register(new ActionDescriptor("music.play", ActionTier.Act, "Plays.", new[] { new ActionParameter("title", ActionValueKind.String, true, "Title.") }));
            try
            {
                Assert.True(started.Transport.IsRunning);
                var auth = Auth(rig);
                var post = ApiLoopbackClient.Send(
                    port, "POST", "/v1/actions/music.play", new[] { auth, H("Content-Type", "application/json") }, "{\"args\":{\"title\":\"雨の音\"}}");
                Assert.Equal(202, post.Status);
                Assert.StartsWith("/v1/actions/act-", post.Headers.Get("Location"));

                var hugeHeader = ApiLoopbackClient.Send(port, "GET", "/v1/info", new[] { auth, H("X-Pad", new string('a', ApiDefaults.MaxHeadBytes)) });
                Assert.Equal(431, hugeHeader.Status);

                var chunked = ApiLoopbackClient.Send(port, "POST", "/v1/actions/music.play", new[] { auth, H("Transfer-Encoding", "chunked") });
                Assert.Equal(411, chunked.Status);

                var doubledHost = ApiLoopbackClient.Send(port, "GET", "/v1/info", new[] { auth, H("Host", ApiDefaults.HostAuthority(port)), H("Host", ApiDefaults.HostAuthority(port)) });
                Assert.Equal(421, doubledHost.Status);
            }
            finally
            {
                started.Transport?.Stop();
            }
        }

        [Fact]
        public void A_port_held_on_loopback_leaves_the_api_off_without_hopping()
        {
            var holder = new TcpListener(IPAddress.Loopback, 0);
            holder.Start();
            var port = ((IPEndPoint)holder.LocalEndpoint).Port;
            try
            {
                foreach (var mode in new[] { ApiTransportMode.Auto, ApiTransportMode.HttpListener, ApiTransportMode.Tcp })
                {
                    var result = ApiServer.Start(port, mode, new RouterRig(port).Router.Handle, _ => { });
                    Assert.False(result.IsRunning);
                    Assert.Equal(ApiStartProblem.PortTaken, result.Problem);
                    Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Detail);
                }
            }
            finally
            {
                holder.Stop();
            }
        }

        [Theory]
        [InlineData(ApiTransportMode.Tcp)]
        [InlineData(ApiTransportMode.HttpListener)]
        public void Stop_is_idempotent_and_marks_our_listener_stopped(ApiTransportMode mode)
        {
            var started = SocketTestServer.Start(mode, port => new RouterRig(port), (rig, request) => rig.Router.Handle(request));
            Assert.True(started.Transport.IsRunning);

            started.Transport!.Stop();
            started.Transport.Stop();

            Assert.False(started.Transport.IsRunning);
            started.Transport.Dispose();

            Assert.Equal(0, ApiLoopbackClient.Send(started.Port, "GET", "/v1/info").Status);
            SocketTestServer.Until(() => ApiPortProbe.Check(started.Port, _ => { }, out _) == ApiPortState.Free,
                "stopped transport did not release port " + started.Port);
        }

        /// <summary>
        /// Windows refuses a connection to a free loopback port only after about two seconds (measured 2026-11-01:
        /// 2045 ms on Windows 11, 0 ms on macOS), so a probe that times out there says nothing; elsewhere it means a
        /// listener with a full backlog. Before this, the API could never start on Windows ("port is taken").
        /// </summary>
        [Fact]
        public void A_probe_timeout_means_taken_everywhere_but_windows()
        {
            Assert.True(ApiPortProbe.SilenceMeansTaken(windows: false));
            Assert.False(ApiPortProbe.SilenceMeansTaken(windows: true));
        }

        [Fact]
        public void A_port_we_still_own_is_taken_and_tcp_reports_its_assigned_port()
        {
            using var transport = new TcpApiTransport(0, _ => ApiResponse.Json(200, "{}"), _ => { });
            transport.Start();
            Assert.InRange(transport.Port, 1024, 65535);
            Assert.Equal(ApiPortState.Taken, ApiPortProbe.Check(transport.Port, _ => { }, out _));
        }

        [Fact]
        public void Ports_outside_the_range_are_refused_before_binding()
        {
            Assert.Equal(ApiStartProblem.InvalidPort, ApiServer.Start(80, ApiTransportMode.Auto, new RouterRig().Router.Handle, _ => { }).Problem);
            Assert.Equal(ApiStartProblem.InvalidPort, ApiServer.Start(70000, ApiTransportMode.Auto, new RouterRig().Router.Handle, _ => { }).Problem);
        }

        [Fact]
        public void Address_in_use_is_recognised_in_every_runtime_shape()
        {
            Assert.True(ApiPortProbe.IsAddressInUse(new SocketException((int)SocketError.AddressAlreadyInUse)));
            Assert.True(ApiPortProbe.IsAddressInUse(new InvalidOperationException("wrapped", new SocketException((int)SocketError.AddressAlreadyInUse))));
            Assert.True(ApiPortProbe.IsAddressInUse(new HttpListenerException(10048)));
            Assert.True(ApiPortProbe.IsAddressInUse(new HttpListenerException(48)));
            Assert.True(ApiPortProbe.IsAddressInUse(new HttpListenerException(400, "There's another listener for http://127.0.0.1:7870/")));
            Assert.False(ApiPortProbe.IsAddressInUse(new HttpListenerException(5, "Access denied")));
            Assert.False(ApiPortProbe.IsAddressInUse(new SocketException((int)SocketError.AccessDenied)));
            Assert.False(ApiPortProbe.IsAddressInUse(null));
        }

        private static KeyValuePair<string, string> Auth(RouterRig rig) => H("Authorization", ApiTestKit.AuthValue(rig.Key));

        private static KeyValuePair<string, string> H(string name, string value) => new KeyValuePair<string, string>(name, value);
    }
}
