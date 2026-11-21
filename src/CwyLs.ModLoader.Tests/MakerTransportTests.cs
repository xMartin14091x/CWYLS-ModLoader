using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// DS-01 over real sockets on 127.0.0.1 only, on free ephemeral ports (never 7870): the maker-only listener on
    /// both transports serves the page with its type and no-store, the API routes with the token, a binary WAV
    /// upload byte for byte, and refuses an oversize upload and every non-maker route.
    /// </summary>
    [Collection(SocketTestCollection.Name)]
    public class MakerTransportTests : IClassFixture<MakerWebFolder>
    {
        private readonly MakerWebFolder _web;

        public MakerTransportTests(MakerWebFolder web) => _web = web;

        [Theory]
        [InlineData(ApiTransportMode.Tcp, "tcp")]
        [InlineData(ApiTransportMode.Auto, "httplistener")]
        public void The_maker_only_listener_serves_the_page_the_routes_and_binary_uploads(ApiTransportMode mode, string expected)
        {
            var started = SocketTestServer.Start(mode, port => new MakerRig(port: port, webRoot: _web.Root),
                (rig, request) => rig.Router.Handle(request));
            var port = started.Port; var rig = started.Rig;
            try
            {
                Assert.True(started.Transport.IsRunning);
                Assert.Equal(expected, started.Transport!.Name);

                var bare = RawHttp.Send(port, "GET", "/maker/");
                Assert.DoesNotContain("cwyls-maker-token", bare.Text);
                Assert.Null(bare.Headers.Get("Set-Cookie"));
                var target = new Uri(rig.Router.CreateOpenUrl()).PathAndQuery;
                var launch = RawHttp.Send(port, "GET", target, new[] { H("Sec-Fetch-Site", "none") });
                Assert.Equal(302, launch.Status);
                Assert.Equal("/maker/", launch.Headers.Get("Location"));
                var cookie = launch.Headers.Get("Set-Cookie")!;
                Assert.Contains("; HttpOnly; SameSite=Strict", cookie);
                var pageHeaders = new[] { H("Cookie", cookie.Split(';')[0]), H("Sec-Fetch-Site", "same-origin") };
                var page = RawHttp.Send(port, "GET", "/maker/", pageHeaders);
                Assert.Equal(200, page.Status);
                Assert.Equal("text/html; charset=utf-8", page.Headers.Get("Content-Type"));
                Assert.Equal("no-store", page.Headers.Get("Cache-Control"));
                Assert.Equal("nosniff", page.Headers.Get("X-Content-Type-Options"));
                Assert.Equal("DENY", page.Headers.Get("X-Frame-Options"));
                Assert.Null(page.Headers.Get("Access-Control-Allow-Origin"));
                Assert.Contains("<meta name=\"cwyls-maker-token\" content=\"" + rig.Token.Reveal() + "\">", page.Text);
                Assert.Equal("no-referrer", page.Headers.Get("Referrer-Policy"));
                Assert.Contains(rig.Token.Reveal(), RawHttp.Send(port, "GET", "/maker/", pageHeaders).Text);
                Assert.DoesNotContain("cwyls-maker-token", RawHttp.Send(port, "GET", target, pageHeaders).Text);
                Assert.DoesNotContain("cwyls-maker-token", RawHttp.Send(port, "GET", "/maker/").Text);

                var script = RawHttp.Send(port, "GET", "/maker/js/app.js");
                Assert.Equal(200, script.Status);
                Assert.Equal("text/javascript; charset=utf-8", script.Headers.Get("Content-Type"));

                var auth = H("Authorization", ApiTestKit.AuthValue(rig.Token));
                var status = RawHttp.Send(port, "GET", "/v1/maker/status", new[] { auth, H("Origin", "http://127.0.0.1:" + port) });
                Assert.Equal(200, status.Status);
                Assert.StartsWith("application/json", status.Headers.Get("Content-Type"));
                Assert.Equal("no-store", status.Headers.Get("Cache-Control"));

                Assert.Equal(403, RawHttp.Send(port, "GET", "/v1/maker/status", new[] { auth, H("Origin", "http://127.0.0.1:" + (port == 65535 ? port - 1 : port + 1)) }).Status);
                Assert.Equal(401, RawHttp.Send(port, "GET", "/v1/maker/status").Status);
                Assert.Equal(404, RawHttp.Send(port, "GET", "/v1/info", new[] { auth }).Status);

                var wav = TestWav.Tone(4, 44100, 2);
                var upload = RawHttp.Send(port, "PUT", "/v1/maker/packs/thai-dub/voice/th/voice_sample_01", new[] { auth, H("Content-Type", "audio/wav") }, wav);
                Assert.Equal(200, upload.Status);
                Assert.Equal(wav, rig.Backend.LastWav);

                // Parentheses (CONTRACT.md v1.1) arrive unencoded through the transport and ApiTarget; a
                // percent-encoded form stays refused (400), because no maker path is ever percent-decoded.
                const string Name = "Voice_MainScenario_12(5)_001";
                var json = H("Content-Type", "application/json; charset=utf-8");
                var subtitle = RawHttp.Send(port, "PUT", "/v1/maker/packs/thai-dub/subtitles/th/" + Name, new[] { auth, json }, Encoding.UTF8.GetBytes("{\"text\":\"x\"}"));
                Assert.Equal(200, subtitle.Status);
                var named = RawHttp.Send(port, "PUT", "/v1/maker/packs/thai-dub/voice/th/" + Name, new[] { auth, H("Content-Type", "audio/wav") }, TestWav.Tone(0.25, 16000, 1));
                Assert.Equal(200, named.Status);
                Assert.Equal(200, RawHttp.Send(port, "GET", "/v1/maker/lines/" + Name + "?lang=th", new[] { auth }).Status);
                Assert.Contains("subtitle thai-dub th " + Name + " [x]", rig.Backend.Calls);
                Assert.Contains("line " + Name + " th", rig.Backend.Calls);
                Assert.Equal(400, RawHttp.Send(port, "DELETE", "/v1/maker/packs/thai-dub/voice/th/Voice_MainScenario_12%285%29_001", new[] { auth }).Status);

                var oversize = RawHttp.Send(
                    port,
                    "PUT",
                    "/v1/maker/packs/thai-dub/voice/th/voice_sample_01",
                    new[] { auth, H("Content-Type", "audio/wav"), H("Content-Length", (MakerDefaults.MaxWavBytes + 1).ToString(CultureInfo.InvariantCulture)) },
                    null);
                Assert.Equal(413, oversize.Status);
            }
            finally
            {
                started.Transport?.Stop();
            }
        }

        [Fact]
        public void Through_the_api_listener_the_maker_page_is_served_beside_the_api()
        {
            var started = SocketTestServer.Start(ApiTransportMode.Tcp, port => new RouterRig(port), (rig, request) => rig.Router.Handle(request));
            var port = started.Port; var api = started.Rig;
            var maker = new MakerRig(port: port, webRoot: _web.Root);
            api.Router.UseMaker(maker.Router);
            try
            {
                Assert.True(started.Transport.IsRunning);
                var bare = RawHttp.Send(port, "GET", "/maker/");
                Assert.DoesNotContain(maker.Token.Reveal(), bare.Text);
                var launch = RawHttp.Send(port, "GET", new Uri(maker.Router.CreateOpenUrl()).PathAndQuery);
                Assert.Equal(302, launch.Status);
                var page = RawHttp.Send(port, "GET", "/maker/", new[] { H("Cookie", launch.Headers.Get("Set-Cookie")!.Split(';')[0]) });
                Assert.Equal(200, page.Status);
                Assert.Equal("text/html; charset=utf-8", page.Headers.Get("Content-Type"));
                Assert.Contains(maker.Token.Reveal(), page.Text);
                Assert.DoesNotContain(api.Key.Reveal(), page.Text);

                Assert.Equal(200, RawHttp.Send(port, "GET", "/v1/info", new[] { H("Authorization", ApiTestKit.AuthValue(api.Key)) }).Status);
                Assert.Equal(403, RawHttp.Send(port, "GET", "/v1/info", new[] { H("Authorization", ApiTestKit.AuthValue(api.Key)), H("Origin", "http://127.0.0.1:" + port) }).Status);
            }
            finally
            {
                started.Transport?.Stop();
            }
        }

        private static KeyValuePair<string, string> H(string name, string value) => new KeyValuePair<string, string>(name, value);
    }

    /// <summary>A loopback HTTP/1.1 client that sends binary bodies (the shared one sends text only).</summary>
    internal static class RawHttp
    {
        internal sealed class Reply
        {
            internal int Status { get; set; }

            internal ApiHeaders Headers { get; } = new ApiHeaders();

            internal byte[] Body { get; set; } = new byte[0];

            internal string Text => Encoding.UTF8.GetString(Body);
        }

        internal static Reply Send(int port, string method, string target, IEnumerable<KeyValuePair<string, string>>? headers = null, byte[]? body = null)
        {
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                client.Connect(IPAddress.Loopback, port);
                var sb = new StringBuilder();
                sb.Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
                sb.Append("Host: 127.0.0.1:").Append(port.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                var hasLength = false;
                foreach (var h in headers ?? new KeyValuePair<string, string>[0])
                {
                    hasLength |= string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase);
                    sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                }

                if (body != null && !hasLength)
                {
                    sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }

                sb.Append("Connection: close\r\n\r\n");
                var stream = client.GetStream();
                var head = Encoding.ASCII.GetBytes(sb.ToString());
                stream.Write(head, 0, head.Length);
                if (body != null)
                {
                    stream.Write(body, 0, body.Length);
                }

                stream.Flush();
                using (var all = new MemoryStream())
                {
                    var buffer = new byte[65536];
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
                        // The server may reset after a refusal of an unsent body; what arrived is the reply.
                    }

                    return Parse(all.ToArray());
                }
            }
        }

        private static Reply Parse(byte[] raw)
        {
            var reply = new Reply();
            var split = -1;
            for (var i = 3; i < raw.Length; i++)
            {
                if (raw[i - 3] == '\r' && raw[i - 2] == '\n' && raw[i - 1] == '\r' && raw[i] == '\n')
                {
                    split = i - 3;
                    break;
                }
            }

            if (split < 0)
            {
                return reply;
            }

            var lines = Encoding.ASCII.GetString(raw, 0, split).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            reply.Status = parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    reply.Headers.Add(lines[i].Substring(0, colon).Trim(), lines[i].Substring(colon + 1).Trim());
                }
            }

            var body = new byte[raw.Length - split - 4];
            Buffer.BlockCopy(raw, split + 4, body, 0, body.Length);
            reply.Body = body;
            return reply;
        }
    }
}
