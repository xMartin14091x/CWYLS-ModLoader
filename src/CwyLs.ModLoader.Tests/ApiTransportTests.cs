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
    [Collection("TraceBus")]
    public class ApiTransportTests
    {
        [Theory]
        [InlineData(ApiTransportMode.Tcp, "tcp")]
        [InlineData(ApiTransportMode.Auto, "httplistener")]
        public void Both_transports_serve_the_router_with_all_checks(ApiTransportMode mode, string expected)
        {
            var port = ApiTestKit.FreePort();
            var rig = new RouterRig(port);
            var started = ApiServer.Start(port, mode, rig.Router.Handle, _ => { });
            try
            {
                Assert.True(started.IsRunning, started.Detail);
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
            var port = ApiTestKit.FreePort();
            var rig = new RouterRig(port, allowActions: true);
            rig.Services.UseRunner(new FakeRunner());
            rig.Register(new ActionDescriptor("music.play", ActionTier.Act, "Plays.", new[] { new ActionParameter("title", ActionValueKind.String, true, "Title.") }));
            var started = ApiServer.Start(port, ApiTransportMode.Tcp, rig.Router.Handle, _ => { });
            try
            {
                Assert.True(started.IsRunning, started.Detail);
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
        public void Stop_frees_the_port_at_once(ApiTransportMode mode)
        {
            var port = ApiTestKit.FreePort();
            var started = ApiServer.Start(port, mode, new RouterRig(port).Router.Handle, _ => { });
            Assert.True(started.IsRunning, started.Detail);

            started.Transport!.Stop();
            started.Transport.Stop();

            Assert.Equal(ApiPortState.Free, ApiPortProbe.Check(port, _ => { }, out var detail));
            Assert.Equal(0, ApiLoopbackClient.Send(port, "GET", "/v1/info", timeoutMs: 500).Status);
            Assert.Equal(string.Empty, detail);
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
