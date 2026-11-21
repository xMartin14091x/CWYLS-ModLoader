using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// DS-06 reproductions: finding assertions describe current unsafe behaviour and pass before fixes.
    /// Reverse those assertions after fixes (findings 1-3 reversed in DS-10a: they now assert the fix);
    /// token bootstrap now requires a one-time launch link (D-145).
    /// All text and PCM samples are invented.
    /// No product code, game files, installed packs or non-loopback services are used.
    /// </summary>
    [Collection(SocketTestCollection.Name)]
    public sealed class MakerSecurityReviewTests
    {
        // Finding 1 (fixed in DS-10a): a second 'data' or 'fmt ' chunk, or one placed after the end the RIFF
        // header declares, used to make the upload check and the catalogue read two different lengths.
        [Theory]
        [InlineData("data", "more than one 'data' chunk")]
        [InlineData("fmt ", "more than one 'fmt ' chunk")]
        [InlineData("data past the RIFF end", "RIFF size")]
        public void Duplicate_wav_chunks_are_refused_as_415_and_never_reach_the_editor(string appended, string named)
        {
            var first = TestWav.Header(1, 1, 8000, 16, 120 * 8000 * 2);
            Assert.Equal(MakerWavVerdict.TooLong, MakerWavCheck.Check(first, out _, out _));

            // The attack: the 120 s file plus a later short data chunk or a later six-times-faster fmt chunk.
            var appendFormat = appended == "fmt ";
            var suffixSource = TestWav.Header(1, 1, 48000, 16, 2);
            var suffixStart = appendFormat ? 12 : 36;
            var suffixCount = appendFormat ? 24 : 10;
            var attack = new byte[first.Length + suffixCount];
            Buffer.BlockCopy(first, 0, attack, 0, first.Length);
            Buffer.BlockCopy(suffixSource, suffixStart, attack, first.Length, suffixCount);
            if (appended != "data past the RIFF end")
            {
                Buffer.BlockCopy(BitConverter.GetBytes(attack.Length - 8), 0, attack, 4, 4);
            }

            Assert.True(attack.Length < MakerDefaults.MaxWavBytes);
            Assert.Equal(MakerWavVerdict.Unsupported, MakerWavCheck.Check(attack, out var checkedSeconds, out var problem));
            Assert.Equal(0d, checkedSeconds);
            Assert.Contains(named, problem);
            var inspected = Core.Api.Maker.Editor.MakerWav.Inspect(attack);
            Assert.False(inspected.Ok);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, inspected.Error);

            // The catalogue's length reader no longer picks one of two answers either.
            if (appended != "data past the RIFF end")
            {
                using (var stream = new MemoryStream(attack))
                {
                    Assert.False(MakerAudioLength.TryWav(stream, out _));
                }
            }

            var rig = new MakerRig();
            var reply = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/lab.review/voice/th/review_clip", rig.Token, attack, "audio/wav"));
            Assert.Equal(415, reply.Status);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, ApiTestKit.ErrorCode(reply));
            Assert.Null(rig.Backend.LastWav);
            Assert.Empty(rig.Backend.Calls);
        }

        // Finding 2 (fixed in DS-10a): headers that contradict themselves used to be forwarded to the editor.
        [Theory]
        [InlineData("riff-size", "RIFF size")]
        [InlineData("riff-size-one-short", "RIFF size")]
        [InlineData("block-align", "block align 0")]
        [InlineData("byte-rate", "byte rate 0")]
        [InlineData("byte-rate-off-by-one", "byte rate 32001")]
        [InlineData("partial-frame", "whole number of sample frames")]
        public void Malformed_pcm_headers_are_refused_as_415_and_never_reach_the_editor(string field, string named)
        {
            // The same header unmodified is accepted, so each refusal below is the one field's doing.
            Assert.Equal(MakerWavVerdict.Ok, MakerWavCheck.Check(TestWav.Header(1, 2, 8000, 16, 8), out _, out _));

            var attack = TestWav.Header(1, 2, 8000, 16, field == "partial-frame" ? 5 : 8);
            if (field == "riff-size") Array.Clear(attack, 4, 4);
            if (field == "riff-size-one-short") attack[4]--;
            if (field == "block-align") Array.Clear(attack, 32, 2);
            if (field == "byte-rate") Array.Clear(attack, 28, 4);
            if (field == "byte-rate-off-by-one") attack[28]++;

            Assert.Equal(MakerWavVerdict.Unsupported, MakerWavCheck.Check(attack, out _, out var problem));
            Assert.Contains(named, problem);
            var inspected = Core.Api.Maker.Editor.MakerWav.Inspect(attack);
            Assert.False(inspected.Ok);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, inspected.Error);

            var rig = new MakerRig();
            var reply = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/lab.review/voice/th/review_clip", rig.Token, attack, "audio/wav"));
            Assert.Equal(415, reply.Status);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, ApiTestKit.ErrorCode(reply));
            Assert.Null(rig.Backend.LastWav);
            Assert.Empty(rig.Backend.Calls);
        }

        // Finding 3 (fixed in DS-10a): the catalogue used to read subtitle text (and could index clips) through
        // links that point outside the pack. Each case also keeps one real entry, to show the rest still reads.
        [Theory]
        [InlineData("subtitles folder")]
        [InlineData("data folder")]
        [InlineData("subtitle file")]
        [InlineData("dangling subtitle file")]
        [InlineData("voice folder")]
        [InlineData("voice language folder")]
        [InlineData("voice file")]
        public void Catalogue_never_reads_through_links_outside_the_pack(string linked)
        {
            // The supported macOS runtime permits unprivileged symlinks. This test needs that
            // facility; Windows requires privileges and is deliberately not claimed as tested.
            if (OperatingSystem.IsWindows()) return;
            var temp = Path.Combine(Path.GetTempPath(), "cwyls-ds06-" + Guid.NewGuid().ToString("N"));
            try
            {
                var mods = Path.Combine(temp, "CWYLS Mods");
                var packRoot = Path.Combine(mods, "lab.review");
                var outside = Path.Combine(temp, "outside-pack");
                Directory.CreateDirectory(Path.Combine(outside, "subtitles"));
                Directory.CreateDirectory(Path.Combine(outside, "th"));
                File.WriteAllText(Path.Combine(outside, "th.json"), "{\"review_line\":\"invented outside-pack secret\"}");
                File.WriteAllText(Path.Combine(outside, "subtitles", "th.json"), "{\"review_line\":\"invented outside-pack secret\"}");
                File.WriteAllBytes(Path.Combine(outside, "Voice_Review_001.wav"), MakerAudioFixtures.Wav(8000, 1, 8000));
                File.WriteAllBytes(Path.Combine(outside, "th", "Voice_Review_001.wav"), MakerAudioFixtures.Wav(8000, 1, 8000));

                var data = Path.Combine(packRoot, "data");
                var subtitles = Path.Combine(data, "subtitles");
                var voice = Path.Combine(packRoot, "audio", "voice");
                Directory.CreateDirectory(packRoot);
                Directory.CreateDirectory(Path.Combine(packRoot, "audio"));
                if (linked == "data folder") Directory.CreateSymbolicLink(data, outside);
                else Directory.CreateDirectory(data);
                if (linked == "subtitles folder") Directory.CreateSymbolicLink(subtitles, outside);
                else if (linked != "data folder") Directory.CreateDirectory(subtitles);
                if (linked == "voice folder") Directory.CreateSymbolicLink(voice, outside);
                else Directory.CreateDirectory(Path.Combine(voice, "ko"));

                if (linked == "subtitle file") File.CreateSymbolicLink(Path.Combine(subtitles, "th.json"), Path.Combine(outside, "th.json"));
                if (linked == "dangling subtitle file") File.CreateSymbolicLink(Path.Combine(subtitles, "th.json"), Path.Combine(outside, "gone.json"));
                if (linked == "voice language folder") Directory.CreateSymbolicLink(Path.Combine(voice, "th"), Path.Combine(outside, "th"));
                if (linked == "voice file")
                {
                    Directory.CreateDirectory(Path.Combine(voice, "th"));
                    File.CreateSymbolicLink(Path.Combine(voice, "th", "Voice_Review_001.wav"), Path.Combine(outside, "Voice_Review_001.wav"));
                }

                // One real table and one real clip inside the pack, when the folders that hold them are real.
                var contentFolderLinked = linked == "subtitles folder" || linked == "data folder" || linked == "voice folder";
                if (linked != "subtitles folder" && linked != "data folder")
                {
                    File.WriteAllText(Path.Combine(subtitles, "ko.json"), "{\"review_line\":\"invented pack text\"}");
                }

                if (linked != "voice folder")
                {
                    File.WriteAllBytes(Path.Combine(voice, "ko", "Voice_Review_001.wav"), MakerAudioFixtures.Wav(8000, 1, 8000));
                }

                var source = new MakerPackSource("lab.review", "Review", "1.0.0", packRoot, false);
                var access = MakerPackRules.Decide(source, mods, _ => true);
                if (contentFolderLinked)
                {
                    var folder = linked == "voice folder" ? "audio/voice" : "data/subtitles";
                    Assert.False(access.Editable);
                    Assert.Equal(MakerPackRules.LinkedFolderReason(folder), access.Reason);
                }
                else
                {
                    Assert.True(access.Editable, access.Reason);
                }

                var content = MakerPackScanner.Scan(new[] { source });
                var game = new MakerGameData(
                    new[] { new MakerGameRow("review_line", "Voice_Review_001", null, "", "Invented reference") }, null, null);
                var view = new MakerCatalogView(game, content);
                var line = view.Line("review_line", "th");
                Assert.True(line.Ok);
                Assert.Null(line.Value!.Subtitle);
                Assert.Null(line.Value.DubPack);
                Assert.Null(content.FindDub("th", "Voice_Review_001"));
                Assert.DoesNotContain(content.Problems, p => p.Contains("secret"));

                // Exactly one log line, naming the pack-relative entry that was skipped.
                var expectedEntry = linked switch
                {
                    "subtitles folder" => "lab.review/data/subtitles",
                    "data folder" => "lab.review/data",
                    "subtitle file" => "lab.review/data/subtitles/th.json",
                    "dangling subtitle file" => "lab.review/data/subtitles/th.json",
                    "voice folder" => "lab.review/audio/voice",
                    "voice language folder" => "lab.review/audio/voice/th",
                    _ => "lab.review/audio/voice/th/Voice_Review_001.wav",
                };
                var problem = Assert.Single(content.Problems);
                Assert.StartsWith(expectedEntry + ": skipped: it is a symbolic link", problem);

                // The rest of the pack still reads.
                if (linked != "subtitles folder" && linked != "data folder")
                {
                    Assert.Equal("invented pack text", view.Line("review_line", "ko").Value!.Subtitle);
                }

                if (linked != "voice folder")
                {
                    Assert.Equal("lab.review", view.Line("review_line", "ko").Value!.DubPack);
                }

                // The fingerprint does not look into a linked content folder either, and stays stable.
                Assert.Equal(MakerPackScanner.Fingerprint(new[] { source }), content.Fingerprint);
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
        }

        // Finding 5, fixed in DS-10b: the backend starts on its own thread, so its synchronous work before it
        // returns a task cannot hold the request past the router's timeout or stop.
        [Theory]
        [InlineData("timeout")]
        [InlineData("stop")]
        public async Task Synchronous_backend_work_is_bounded_by_the_router_timeout_and_stop(string ending)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var backend = new BlockingCatalog(entered, release);
            var token = ApiToken.Create();
            var router = new MakerRouter(token, new MakerRouterOptions
            {
                Catalog = () => backend,
                BackendTimeout = ending == "timeout" ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(30),
                BackendGrace = TimeSpan.Zero,
                Diagnostics = _ => { },
            });
            try
            {
                var call = Task.Run(() => router.Handle(MakerHttp.Get("/v1/maker/status", token)));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                if (ending == "stop")
                {
                    router.BeginStop();
                }

                // The answer comes while the backend is still blocked in its synchronous part (a TimeoutException
                // here means the request waited for that work).
                var reply = await call.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(backend.Returned);
                Assert.Equal(503, reply.Status);
                Assert.Equal(MakerErrorCodes.Unavailable, ApiTestKit.ErrorCode(reply));
                Assert.Equal(1, router.BackendCalls);
            }
            finally
            {
                release.Set();
            }

            // The late result is dropped: the backend finishes having seen the cancellation and frees its place.
            Assert.True(SpinWait.SpinUntil(() => router.BackendCalls == 0, TimeSpan.FromSeconds(5)));
            Assert.True(backend.Returned);
            Assert.True(backend.ObservedCancellation);
        }

        [Fact]
        public void Backend_calls_that_overrun_are_capped_so_they_cannot_pile_up()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var backend = new BlockingCatalog(entered, release);
            var token = ApiToken.Create();
            var router = new MakerRouter(token, new MakerRouterOptions
            {
                Catalog = () => backend,
                BackendTimeout = TimeSpan.FromMilliseconds(100),
                BackendGrace = TimeSpan.Zero,
                MaxBackendCalls = 2,
                Diagnostics = _ => { },
            });
            try
            {
                Assert.Equal(503, router.Handle(MakerHttp.Get("/v1/maker/status", token)).Status);
                Assert.Equal(503, router.Handle(MakerHttp.Get("/v1/maker/status", token)).Status);
                Assert.Equal(2, router.BackendCalls);

                // Both late calls still run: the next one is refused without reaching the backend.
                var refused = router.Handle(MakerHttp.Get("/v1/maker/status", token));
                Assert.Equal(503, refused.Status);
                Assert.Contains("still busy", refused.Body);
                Assert.Equal(2, backend.Entries);
            }
            finally
            {
                release.Set();
            }

            Assert.True(SpinWait.SpinUntil(() => router.BackendCalls == 0, TimeSpan.FromSeconds(5)));
            Assert.Equal(200, router.Handle(MakerHttp.Get("/v1/maker/status", token)).Status);
            Assert.Equal(3, backend.Entries);
        }

        [Fact]
        public void Poc_an_uninvited_loopback_client_cannot_bootstrap_a_token_or_write()
        {
            using var web = new MakerWebFolder();
            var rig = new MakerRig(webRoot: web.Root);
            var page = rig.Send(MakerHttp.Get("/maker/", null));
            var html = Encoding.UTF8.GetString(page.BodyBytes());
            Assert.DoesNotContain("cwyls-maker-token", html);
            Assert.DoesNotContain(rig.Token.Reveal(), html);
            Assert.Null(ApiTestKit.Header(page, "Set-Cookie"));
            Assert.Contains("Open Pack Maker from the game", html);
            Assert.Contains("เปิด Pack Maker จากในเกม", html);
            Assert.Equal(401, rig.Send(MakerHttp.Json("POST", "/v1/maker/play", null,
                "{\"id\":\"review_line\",\"variant\":\"original\",\"lang\":\"th\"}")).Status);
            Assert.Empty(rig.Backend.Calls);
        }

        [Fact]
        public void Symlinks_inside_maker_takes_are_refused_on_read_and_write()
        {
            if (OperatingSystem.IsWindows()) return;
            using var fx = new MakerProjectFixture();
            var takes = new CwyLs.ModLoader.Core.Api.Maker.Studio.MakerTakeStore(fx.Store);
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(new MakerStudioCreateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                ExpectedRevision = rev,
                OperationId = fx.Operation(1),
                Origin = "own",
                Consent = new MakerStudioConsent
                {
                    Confirmed = true,
                    StatementVersion = MakerStudioContract.ConsentVersion,
                },
            }).Value!;
            var takeId = created.Take!.Id;
            var s1 = takes.SaveAudio(new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                Kind = "source",
                ExpectedRevision = created.Revision,
                OperationId = fx.Operation(2),
            }, TestWav.Tone(0.5, 24000, 1)).Value!;

            var outsideWav = Path.Combine(Path.GetTempPath(), "cwy-maker-outside-" + Guid.NewGuid().ToString("N") + ".wav");
            var outsideBytes = TestWav.Tone(0.5, 48000, 1);
            File.WriteAllBytes(outsideWav, outsideBytes);
            try
            {
                var sourceFull = Path.Combine(fx.Root, CwyLs.ModLoader.Core.Api.Maker.Studio.MakerStudioPaths.TakeAudio("th", "one", takeId, "source"));
                File.Delete(sourceFull);
                File.CreateSymbolicLink(sourceFull, outsideWav);

                // Reading take audio or takes metadata refuses the symlink with metadata_unavailable and never serves outside bytes.
                var readAudio = takes.ReadAudio("th", "one", takeId, "source");
                Assert.False(readAudio.Ok);
                Assert.Equal(MakerStudioErrorCodes.MetadataUnavailable, readAudio.Error);

                // Attempting to overwrite through a symlink is also refused without touching the outside file.
                var writeAudio = takes.SaveAudio(new MakerStudioTakeAudioRequest
                {
                    PackId = "sample.pack",
                    Lang = "th",
                    Id = "one",
                    TakeId = takeId,
                    Kind = "source",
                    ExpectedRevision = s1.Revision,
                    OperationId = fx.Operation(3),
                }, TestWav.Tone(0.25, 16000, 1));
                Assert.False(writeAudio.Ok);
                Assert.Equal(outsideBytes, File.ReadAllBytes(outsideWav));
            }
            finally
            {
                if (File.Exists(outsideWav)) File.Delete(outsideWav);
            }
        }

        // Finding 4, fixed in DS-10b: the TCP transport gives the whole request (head and body) one fixed deadline
        // from the accept (5 s) instead of a per-read timeout, so dripping clients lose their request slots.
        [Fact]
        public void Slow_heads_and_slow_bodies_lose_their_tcp_request_slots_at_a_fixed_deadline()
        {
            var started = SocketTestServer.Start(ApiTransportMode.Tcp, port => new MakerRig(port: port),
                (rig, request) => rig.Router.Handle(request));
            var port = started.Port; var rig = started.Rig;
            var clients = new List<TcpClient>();
            var open = new List<bool>();
            try
            {
                Assert.True(started.Transport.IsRunning);
                Assert.Equal(TimeSpan.FromSeconds(5), TcpApiTransport.ReadDeadline);
                for (var i = 0; i < ApiDefaults.MaxConcurrentRequests; i++)
                {
                    var client = new TcpClient(AddressFamily.InterNetwork) { SendTimeout = 1000, ReceiveTimeout = 3000 };
                    clients.Add(client);
                    open.Add(true);
                    client.Connect(IPAddress.Loopback, port);

                    // Half the slots hold an unfinished head (no token needed); the other half send a complete,
                    // authenticated upload head and then hold its body.
                    var start = i % 2 == 0
                        ? "GET /maker/ HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nX-Drip: "
                        : "PUT /v1/maker/packs/lab.review/voice/th/review_clip HTTP/1.1\r\nHost: 127.0.0.1:" + port
                          + "\r\nAuthorization: " + ApiTestKit.AuthValue(rig.Token)
                          + "\r\nContent-Type: audio/wav\r\nContent-Length: 4096\r\n\r\nRIFF";
                    client.GetStream().Write(Encoding.ASCII.GetBytes(start));
                }

                var tcp = (TcpApiTransport)started.Transport;
                SocketTestServer.Until(
                    () => tcp.ActiveRequests == ApiDefaults.MaxConcurrentRequests,
                    "sixteen slow TCP connections did not fill all request slots");
                Assert.True(tcp.ActiveRequests <= ApiDefaults.MaxConcurrentRequests);
                var busy = RawHttp.Send(port, "GET", "/v1/maker/status");
                Assert.Equal(503, busy.Status);
                Assert.Equal("no-store", busy.Headers.Get("Cache-Control"));
                Assert.Equal("nosniff", busy.Headers.Get("X-Content-Type-Options"));
                var busyError = (JsonObject)JsonReader.ParseObject(busy.Text)["error"]!;
                Assert.Equal(ApiErrorCodes.Unavailable, busyError["code"]);
                Assert.Contains("too many requests at once", (string)busyError["message"]!);
                Assert.True(tcp.ActiveRequests <= ApiDefaults.MaxConcurrentRequests);

                SocketTestServer.Until(() => rig.Router.Counters.Total >= ApiDefaults.MaxConcurrentRequests / 2,
                    "the TCP body deadlines did not complete", () =>
                    {
                        for (var i = 0; i < clients.Count; i++)
                            if (open[i]) open[i] = TryDrip(clients[i]);
                    });

                // The router saw only the eight body holders (each refused 408) and a fresh request gets through.
                SocketTestServer.Until(() => tcp.ActiveRequests == 0,
                    "TCP response drain did not release the request slots");
                var fresh = RawHttp.Send(port, "GET", "/v1/maker/status");
                Assert.Equal(401, fresh.Status);
                Assert.Equal(ApiDefaults.MaxConcurrentRequests / 2 + 1, rig.Router.Counters.Total);
                lock (rig.Records)
                {
                    Assert.Equal(ApiDefaults.MaxConcurrentRequests / 2,
                        rig.Records.Count(r => r.Record.Status == 408 && r.Record.Error == "request_timeout"));
                }

                // Every holder was disconnected by the server (end of stream or reset, never a read timeout).
                foreach (var client in clients)
                {
                    Assert.True(ClosedByServer(client), "a slow client's connection is still open");
                }
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
                started.Transport?.Stop();
            }
        }

        private static bool TryDrip(TcpClient client)
        {
            try
            {
                client.GetStream().WriteByte((byte)'a');
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                return false; // the server closed it
            }
        }

        private static bool ClosedByServer(TcpClient client)
        {
            var buffer = new byte[4096];
            try
            {
                var stream = client.GetStream();
                while (stream.Read(buffer, 0, buffer.Length) > 0)
                {
                }

                return true;
            }
            catch (IOException ex) when (ex.InnerException is SocketException socket)
            {
                return socket.SocketErrorCode != SocketError.TimedOut && socket.SocketErrorCode != SocketError.WouldBlock;
            }
            catch (IOException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true; // a failed drip already marked the client disconnected
            }
        }

        private sealed class BlockingCatalog : IMakerCatalog
        {
            private readonly ManualResetEventSlim _entered;
            private readonly ManualResetEventSlim _release;
            private int _entries;
            private volatile bool _returned;
            private volatile bool _observedCancellation;

            internal BlockingCatalog(ManualResetEventSlim entered, ManualResetEventSlim release)
            {
                _entered = entered;
                _release = release;
            }

            internal bool ObservedCancellation => _observedCancellation;

            internal bool Returned => _returned;

            internal int Entries => Volatile.Read(ref _entries);

            public Task<MakerResult<MakerStatus>> StatusAsync(CancellationToken cancel)
            {
                // Models the real catalogue's synchronous disk work before an async method
                // yields (warm game snapshot and an uncontended semaphore).
                Interlocked.Increment(ref _entries);
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(10));
                _observedCancellation = cancel.IsCancellationRequested;
                _returned = true;
                return Task.FromResult(MakerResult<MakerStatus>.Success(new MakerStatus()));
            }

            public Task<MakerResult<IReadOnlyList<MakerPack>>> PacksAsync(CancellationToken cancel) => throw new NotSupportedException();
            public Task<MakerResult<MakerLinePage>> LinesAsync(MakerLineQuery query, CancellationToken cancel) => throw new NotSupportedException();
            public Task<MakerResult<MakerLine>> LineAsync(string id, string lang, CancellationToken cancel) => throw new NotSupportedException();
        }
    }
}
