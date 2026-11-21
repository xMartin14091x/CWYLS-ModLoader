using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    [Collection(SocketTestCollection.Name)]
    public class MakerHttpListenerDeadlineTests
    {
        [Fact]
        public void Slow_bodies_are_aborted_at_one_fixed_deadline_and_release_every_slot()
        {
            var diagnostics = new List<string>();
            var run = ApiTransportLoopbackTests.StartWithRetry(ApiTestKit.FreePort, diagnostics: message =>
            {
                lock (diagnostics) diagnostics.Add(message);
            });
            var port = run.Port; var rig = run.Rig; var started = run.Started;
            var clients = new List<TcpClient>();
            try
            {
                Assert.True(started.IsRunning);
                var transport = Assert.IsType<HttpListenerApiTransport>(started);
                Assert.Equal(TimeSpan.FromSeconds(5), HttpListenerApiTransport.ReadDeadline);

                for (var i = 0; i < ApiDefaults.MaxConcurrentRequests; i++)
                {
                    var client = new TcpClient(AddressFamily.InterNetwork) { SendTimeout = 15000, ReceiveTimeout = 3000 };
                    clients.Add(client);
                    client.Connect(IPAddress.Loopback, port);
                    var head = "PUT /v1/maker/packs/thai-dub/voice/th/slow_" + i.ToString(CultureInfo.InvariantCulture)
                               + " HTTP/1.1\r\nHost: 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)
                               + "\r\nAuthorization: " + ApiTestKit.AuthValue(rig.Token)
                               + "\r\nContent-Type: audio/wav\r\nContent-Length: 4096\r\nConnection: close\r\n\r\nRIFF";
                    client.GetStream().Write(Encoding.ASCII.GetBytes(head));
                }

                SocketTestServer.Until(
                    () => transport.ActiveRequests == ApiDefaults.MaxConcurrentRequests,
                    "sixteen slow bodies did not fill all request slots");
                Assert.True(transport.ActiveRequests <= ApiDefaults.MaxConcurrentRequests);
                var busy = RawHttp.Send(port, "GET", "/v1/maker/status");
                Assert.Equal(503, busy.Status);
                Assert.Equal("no-store", busy.Headers.Get("Cache-Control"));
                Assert.Equal("nosniff", busy.Headers.Get("X-Content-Type-Options"));
                var busyError = (JsonObject)JsonReader.ParseObject(busy.Text)["error"]!;
                Assert.Equal("unavailable", busyError["code"]);
                Assert.Contains("too many requests at once", (string)busyError["message"]!);
                Assert.True(transport.ActiveRequests <= ApiDefaults.MaxConcurrentRequests);

                // Observe all expired registrations cumulatively. On a busy machine the first bodies
                // may expire before the last is admitted; requiring sixteen simultaneous reads is invalid.
                SocketTestServer.Until(() =>
                {
                    lock (diagnostics)
                        return diagnostics.FindAll(d => d.Contains("did not arrive within 5 s")).Count >= clients.Count
                            && transport.ActiveRequests == 0 && transport.ReadingBodies == 0;
                }, "not every admitted body reached its deadline and released its slot",
                   () => { foreach (var client in clients) TryDrip(client); });
                Assert.Equal(401, RawHttp.Send(port, "GET", "/v1/maker/status").Status);
                lock (diagnostics)
                {
                    Assert.True(diagnostics.FindAll(d => d.Contains("did not arrive within 5 s")).Count >= ApiDefaults.MaxConcurrentRequests);
                }
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
                started?.Stop();
            }
        }

        [Fact]
        public void A_maximum_size_valid_upload_finishes_before_the_deadline()
        {
            var run = ApiTransportLoopbackTests.StartWithRetry(ApiTestKit.FreePort);
            var port = run.Port; var rig = run.Rig; var started = run.Started;
            try
            {
                Assert.True(started.IsRunning);
                var transport = Assert.IsType<HttpListenerApiTransport>(started);
                var wav = TestWav.PaddedTo(MakerDefaults.MaxWavBytes);
                var reply = RawHttp.Send(
                    port,
                    "PUT",
                    "/v1/maker/packs/thai-dub/voice/th/max_upload",
                    new[]
                    {
                        new KeyValuePair<string, string>("Authorization", ApiTestKit.AuthValue(rig.Token)),
                        new KeyValuePair<string, string>("Content-Type", "audio/wav"),
                    },
                    wav);

                Assert.Equal(200, reply.Status);
                Assert.Equal(MakerDefaults.MaxWavBytes, rig.Backend.LastWav?.Length);
                Assert.Equal(0, transport.ReadingBodies);
            }
            finally
            {
                started?.Stop();
            }
        }

        [Fact]
        public void Stop_aborts_a_pending_body_and_leaves_no_reader_or_request()
        {
            using var reading = new ManualResetEventSlim();
            var run = ApiTransportLoopbackTests.StartWithRetry(ApiTestKit.FreePort, handler: request =>
            {
                reading.Set();
                request.ReadBody(2000);
                return ApiResponse.Json(200, "{}");
            });
            var port = run.Port; var started = run.Started;
            using var client = new TcpClient(AddressFamily.InterNetwork) { SendTimeout = 15000, ReceiveTimeout = 15000 };
            Assert.True(started.IsRunning);
            var transport = Assert.IsType<HttpListenerApiTransport>(started);
            client.Connect(IPAddress.Loopback, port);
            var head = "POST / HTTP/1.1\r\nHost: 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)
                       + "\r\nContent-Length: 1000\r\nConnection: close\r\n\r\nx";
            client.GetStream().Write(Encoding.ASCII.GetBytes(head));
            Assert.True(SpinWait.SpinUntil(() => transport.ReadingBodies == 1, TimeSpan.FromSeconds(15)));

            Assert.True(reading.Wait(TimeSpan.FromSeconds(15)), "worker did not enter body acquisition");
            Assert.True(SpinWait.SpinUntil(() => transport.ActiveBodyReads == 1, TimeSpan.FromSeconds(15)),
                "transport did not enter ReadBody");
            transport.Stop();

            Assert.True(SpinWait.SpinUntil(
                () => transport.ReadingBodies == 0 && transport.ActiveRequests == 0,
                TimeSpan.FromSeconds(15)));
        }

        [Fact]
        public async Task Body_deadline_does_not_abort_a_long_handler_or_event_poll()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var run = ApiTransportLoopbackTests.StartWithRetry(ApiTestKit.FreePort, handler: _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return ApiResponse.Json(200, "{}");
            });
            var port = run.Port; var started = run.Started;
            try
            {
                Assert.True(started.IsRunning);
                var transport = Assert.IsType<HttpListenerApiTransport>(started);
                var response = Task.Run(() => ApiLoopbackClient.Send(port, "GET", "/long-poll", timeoutMs: 30000));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
                Assert.Equal(0, transport.ReadingBodies);

                var held = Stopwatch.StartNew();
                SocketTestServer.Until(() => held.Elapsed > HttpListenerApiTransport.ReadDeadline,
                    "the long-handler observation window did not finish", seconds: 15);

                Assert.False(response.IsCompleted);
                Assert.Equal(1, transport.ActiveRequests);
                release.Set();
                Assert.Equal(200, (await response).Status);
            }
            finally
            {
                release.Set();
                started?.Stop();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Oversize_entities_keep_the_watchdog_until_context_completion(bool chunked)
        {
            using var refused = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var run = ApiTransportLoopbackTests.StartWithRetry(ApiTestKit.FreePort, handler: request =>
            {
                var body = request.ReadBody(8);
                Assert.Equal(ApiBodyStatus.TooLarge, body.Status);
                refused.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return ApiResponse.Error(413, "too_large", "test");
            });
            var transport = (HttpListenerApiTransport)run.Started!;
            using var client = new TcpClient(AddressFamily.InterNetwork) { SendTimeout = 15000, ReceiveTimeout = 8000 };
            try
            {
                client.Connect(IPAddress.Loopback, run.Port);
                var entity = chunked ? "Transfer-Encoding: chunked\r\n\r\n4000\r\n" + new string('a', 16384) + "\r\n" : "Content-Length: 4096\r\n\r\nx";
                client.GetStream().Write(Encoding.ASCII.GetBytes(
                    "POST / HTTP/1.1\r\nHost: 127.0.0.1:" + run.Port + "\r\nConnection: close\r\n" + entity));
                Assert.True(refused.Wait(TimeSpan.FromSeconds(15)));
                Assert.Equal(1, transport.ReadingBodies);
                Assert.True(SpinWait.SpinUntil(() => transport.ReadingBodies == 0, TimeSpan.FromSeconds(30)));
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => transport.ActiveRequests == 0, TimeSpan.FromSeconds(15)));
            }
            finally { release.Set(); transport.Dispose(); }
        }

        [Fact]
        public void Stop_cannot_rearm_a_body_and_dispose_owns_no_wait_handle()
        {
            for (var i = 0; i < 8; i++)
            {
                var run = ApiTransportLoopbackTests.StartWithRetry(ApiTestKit.FreePort);
                var transport = (HttpListenerApiTransport)run.Started!;
                transport.Stop();
                var arm = typeof(HttpListenerApiTransport).GetMethod("ArmBody",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                Assert.Null(arm.Invoke(transport, new object?[] { null }));
                transport.Dispose();
                transport.Dispose();
                Assert.Equal(0, transport.ReadingBodies);
            }
            Assert.DoesNotContain(typeof(HttpListenerApiTransport).GetFields(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                f => typeof(WaitHandle).IsAssignableFrom(f.FieldType));
        }

        [Fact]
        public void Over_cap_response_is_guarded_while_its_entity_stalls()
        {
            var source = File.ReadAllText(Path.Combine(TestRepo.Root!, "src/CwyLs.ModLoader.Core/Api/ApiTransports.cs"));
            Assert.Contains("var refusal = ArmBody(context)", source);
            Assert.Contains("finally { DisarmBody(refusal); }", source);
            using var release = new ManualResetEventSlim();
            var run = ApiTransportLoopbackTests.StartWithRetry(ApiTestKit.FreePort, handler: _ =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return ApiResponse.Json(200, "{}");
            });
            var transport = (HttpListenerApiTransport)run.Started!;
            var clients = new List<TcpClient>();
            try
            {
                for (var i = 0; i < ApiDefaults.MaxConcurrentRequests + 1; i++)
                {
                    var c = new TcpClient(AddressFamily.InterNetwork); clients.Add(c);
                    c.Connect(IPAddress.Loopback, run.Port);
                    c.GetStream().Write(Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: 127.0.0.1:"
                        + run.Port + "\r\nContent-Length: 1000\r\n\r\nx"));
                }
                Assert.True(SpinWait.SpinUntil(() => transport.ReadingBodies > 0, TimeSpan.FromSeconds(15)));
                // The accept thread must be able to resume even if closing the rejection drains on this runtime.
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => transport.ActiveRequests == 0 && transport.ReadingBodies == 0,
                    TimeSpan.FromSeconds(30)));
            }
            finally { release.Set(); foreach (var c in clients) c.Dispose(); transport.Dispose(); }
        }

        [Fact]
        public void Timed_out_or_truncated_body_never_commits_after_408_or_premature_eof()
        {
            var committed = 0;
            using var handled = new ManualResetEventSlim();
            HttpListenerApiTransport? transport = null;
            var run = ApiTransportLoopbackTests.StartWithRetry(ApiTestKit.FreePort, handler: request =>
            {
                try
                {
                    // Pre-read case: the watchdog has already timed this body out before the handler reads it.
                    // The race at the claim itself is Watchdog_expiry_at_the_body_claim_answers_408_and_the_handler_never_commits.
                    var bodiesField = typeof(HttpListenerApiTransport).GetField("_bodies",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    var gateField = typeof(HttpListenerApiTransport).GetField("_bodyGate",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    lock (gateField.GetValue(transport!)!)
                    {
                        var dict = (System.Collections.IDictionary)bodiesField.GetValue(transport!)!;
                        foreach (var item in dict.Values)
                        {
                            item!.GetType().GetField("TimedOut",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                                .SetValue(item, true);
                        }
                    }

                    var body = request.ReadBody(4096);
                    if (body.Status == ApiBodyStatus.Ok)
                    {
                        var parsed = JsonReader.ParseObject(Encoding.UTF8.GetString(body.Bytes));
                        if (parsed.TryGetValue("text", out var v) && v is string)
                        {
                            Interlocked.Increment(ref committed);
                        }

                        return ApiResponse.Json(200, "{\"ok\":true}");
                    }

                    return ApiResponse.Error(408, "request_timeout", body.Problem ?? "timeout");
                }
                finally
                {
                    handled.Set();
                }
            });
            transport = (HttpListenerApiTransport)run.Started!;
            try
            {
                using (var raceClient = new TcpClient(AddressFamily.InterNetwork) { SendTimeout = 5000, ReceiveTimeout = 5000 })
                {
                    raceClient.Connect(IPAddress.Loopback, run.Port);
                    var json = "{\"text\":\"hello\"}";
                    var raw = "PUT /v1/maker/packs/thai-dub/subtitles/th/one HTTP/1.1\r\nHost: 127.0.0.1:"
                              + run.Port.ToString(CultureInfo.InvariantCulture)
                              + "\r\nContent-Type: application/json\r\nContent-Length: "
                              + json.Length.ToString(CultureInfo.InvariantCulture)
                              + "\r\nConnection: close\r\n\r\n"
                              + json;
                    var stream = raceClient.GetStream();
                    stream.Write(Encoding.UTF8.GetBytes(raw));
                    Assert.True(handled.Wait(TimeSpan.FromSeconds(10)));
                    Assert.Equal(0, Volatile.Read(ref committed));
                }

                // Also verify ReadBodyStream rejects a stream that returns 0 before the declared Content-Length.
                var readBodyStream = typeof(HttpListenerApiTransport).GetMethod("ReadBodyStream",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                using var truncated = new MemoryStream(Encoding.UTF8.GetBytes("{\"text\":\"hello\"}"));
                var truncatedResult = (ApiBody)readBodyStream.Invoke(transport, new object?[] { truncated, 200L, 4096, null })!;
                Assert.Equal(ApiBodyStatus.Failed, truncatedResult.Status);
                Assert.Contains("before the body ended", truncatedResult.Problem);
            }
            finally
            {
                transport.Dispose();
            }
        }

        [Fact]
        public void Watchdog_expiry_at_the_body_claim_answers_408_and_the_handler_never_commits()
        {
            var diagnostics = new List<string>();
            var run = SocketTestServer.Start(ApiTransportMode.HttpListener, p => new MakerRig(port: p),
                (rig, request) => rig.Router.Handle(request),
                diagnostics: message => { lock (diagnostics) diagnostics.Add(message); },
                // Mono-style abort: the 408 the watchdog wrote reaches the client, then the response closes.
                abortResponse: response => response.Close());
            var transport = (HttpListenerApiTransport)run.Transport;
            using var claimReached = new ManualResetEventSlim();
            // The whole body has arrived. Hold the worker just before it claims the body until the watchdog has
            // expired it, so the expiry lands exactly between the end of the read and the claim.
            typeof(HttpListenerApiTransport).GetField("_beforeBodyClaim",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(transport, (Action)(() =>
                {
                    claimReached.Set();
                    SocketTestServer.Until(() => transport.ReadingBodies == 0,
                        "the watchdog never expired the body held at its claim", seconds: 20);
                }));
            try
            {
                var wav = TestWav.Tone(0.25, 24000, 1);
                using var client = new TcpClient(AddressFamily.InterNetwork) { SendTimeout = 5000, ReceiveTimeout = 20000 };
                client.Connect(IPAddress.Loopback, run.Port);
                var stream = client.GetStream();
                var head = "PUT /v1/maker/packs/thai-dub/voice/th/claim_race HTTP/1.1\r\nHost: 127.0.0.1:"
                           + run.Port.ToString(CultureInfo.InvariantCulture)
                           + "\r\nAuthorization: " + ApiTestKit.AuthValue(run.Rig.Token)
                           + "\r\nContent-Type: audio/wav\r\nContent-Length: " + wav.Length.ToString(CultureInfo.InvariantCulture)
                           + "\r\nConnection: close\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(head));
                stream.Write(wav);
                var reply = ReadToEnd(stream);

                Assert.True(claimReached.IsSet, "the worker never reached the body claim");
                Assert.StartsWith("HTTP/1.1 408", reply);
                Assert.Contains("\"code\":\"request_timeout\"", reply);
                SocketTestServer.Until(() => transport.ActiveRequests == 0, "the timed-out request never finished");
                Assert.Null(run.Rig.Backend.LastWav); // the handler did not commit a save the client was told timed out
                lock (run.Rig.Records)
                {
                    var record = Assert.Single(run.Rig.Records).Record;
                    Assert.Equal(408, record.Status);
                    Assert.Equal("request_timeout", record.Error);
                }

                lock (diagnostics)
                {
                    Assert.Contains(diagnostics, d => d.Contains("PUT /v1/maker/packs/thai-dub/voice/th/claim_race")
                                                      && d.Contains("answered 408 request_timeout"));
                }
            }
            finally
            {
                transport.Dispose();
            }
        }

        private static string ReadToEnd(NetworkStream stream)
        {
            using var received = new MemoryStream();
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) received.Write(buffer, 0, n);
            }
            catch (IOException)
            {
                // The transport closes or resets after its answer.
            }

            return Encoding.UTF8.GetString(received.ToArray());
        }

        private static void TryDrip(TcpClient client)
        {
            try
            {
                client.GetStream().WriteByte((byte)'a');
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // The fixed deadline closed this holder.
            }
        }
    }
}
