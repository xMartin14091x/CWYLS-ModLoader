using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>Pack Maker trace, the page's client log, the Studio catalog budget and the one-listing voice index (2026-11-09 live 503).</summary>
    public sealed class MakerTraceTests
    {
        private const string ClientLog = "/v1/maker/studio/client-log";
        private const string Lines = "/v1/maker/studio/packs/sample.pack/languages/th/lines";

        private static MakerRouter Router(ApiToken token, IMakerStudioCatalog? catalog = null, TimeSpan? catalogTimeout = null) =>
            new MakerRouter(token, new MakerRouterOptions
            {
                Catalog = () => null,
                Editor = () => null,
                Studio = () => catalog is null ? null : new MakerStudioBackends(catalog: catalog),
                Limiter = new ApiRateLimiter(new ManualApiClock(), 1000, 1000, 1000),
                BackendTimeout = TimeSpan.FromMilliseconds(100),
                BackendGrace = TimeSpan.FromMilliseconds(50),
                CatalogTimeout = catalogTimeout ?? TimeSpan.FromSeconds(5),
                Diagnostics = _ => { },
            });

        private static string Entries(int n) =>
            "{\"entries\":[" + string.Join(",", Enumerable.Range(0, n).Select(i => "{\"kind\":\"api\",\"route\":\"studioLines\",\"status\":503,\"ms\":3012.2,\"n\":" + i + "}")) + "]}";

        [Fact]
        public void Client_log_is_token_protected_writes_only_while_tracing_and_caps_the_batch()
        {
            var token = ApiToken.Create();
            var router = Router(token); // no Studio backend: the page can still report why it is stuck
            var was = MakerRequestTrace.Enabled;
            try
            {
                Assert.Equal(401, router.Handle(MakerHttp.Json("POST", ClientLog, null, Entries(1))).Status);
                Assert.Equal(404, router.Handle(MakerHttp.Get(ClientLog, token)).Status);
                Assert.Equal(400, router.Handle(MakerHttp.Json("POST", ClientLog, token, "{\"entries\":1}")).Status);
                Assert.Equal(400, router.Handle(MakerHttp.Json("POST", ClientLog + "?x=1", token, Entries(1))).Status);
                Assert.Equal(413, router.Handle(MakerHttp.Json("POST", ClientLog, token, Entries(MakerDefaults.MaxClientLogEntries + 1))).Status);

                MakerRequestTrace.Enabled = false;
                var off = router.Handle(MakerHttp.Json("POST", ClientLog, token, Entries(3)));
                Assert.Equal(200, off.Status);
                var offBody = ApiTestKit.Json(off);
                Assert.Equal(0L, offBody["accepted"]);
                Assert.Equal(3L, offBody["dropped"]);
                Assert.Equal(false, offBody["trace"]);

                MakerRequestTrace.Enabled = true;
                var on = ApiTestKit.Json(router.Handle(MakerHttp.Json("POST", ClientLog, token, Entries(3))));
                Assert.Equal(3L, on["accepted"]);
                Assert.Equal(true, on["trace"]);
            }
            finally
            {
                MakerRequestTrace.Enabled = was;
            }
        }

        [Fact]
        public void A_slow_studio_catalog_read_gets_the_catalog_budget_not_the_main_thread_timeout()
        {
            var token = ApiToken.Create();
            var slow = new SlowCatalog(TimeSpan.FromMilliseconds(600));

            // 600 ms is far past BackendTimeout + BackendGrace (150 ms) but inside the catalog budget.
            var served = Router(token, slow).Handle(MakerHttp.Get(Lines + "?offset=0&limit=500", token));
            Assert.Equal(200, served.Status);

            // Past the catalog budget it is still a bounded 503, not a hang.
            var started = DateTime.UtcNow;
            var refused = Router(token, slow, TimeSpan.FromMilliseconds(200)).Handle(MakerHttp.Get(Lines, token));
            Assert.Equal(503, refused.Status);
            Assert.Equal(MakerErrorCodes.Unavailable, ApiTestKit.ErrorCode(refused));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        }

        [Fact]
        public async Task A_backend_call_carries_an_ambient_trace_that_names_the_running_phase()
        {
            var token = ApiToken.Create();
            var seen = new SlowCatalog(TimeSpan.Zero);
            Assert.Equal(200, Router(token, seen).Handle(MakerHttp.Get(Lines, token)).Status);
            Assert.True(seen.SawTrace);

            var trace = new MakerRequestTrace();
            var previous = MakerRequestTrace.Use(trace);
            try
            {
                using (MakerRequestTrace.Phase("outer"))
                {
                    using (MakerRequestTrace.Phase("done")) await Task.Yield();
                    Assert.Contains("outer(", trace.Running());
                    Assert.DoesNotContain("done(", trace.Running());
                }
            }
            finally
            {
                MakerRequestTrace.Restore(previous);
            }

            Assert.Equal("-", trace.Running());
            Assert.Contains("done=", trace.Phases());
            Assert.Contains("outer=", trace.Phases());

            // Without an ambient trace a phase is a no-op.
            using (MakerRequestTrace.Phase("nothing")) { }
            Assert.Null(MakerRequestTrace.Current);
        }

        [Fact]
        public void The_voice_index_is_the_same_as_listing_each_clip()
        {
            var root = Path.Combine(Path.GetTempPath(), "cwy-voice-index-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var (folder, file) in new[] { ("th", "a.wav"), ("th", "a.ogg"), ("th", "b.wav"), ("th", ".hidden.wav"), ("th", "c.txt"), ("en", "a.wav"), ("th", "Mixed.Name.wav") })
                {
                    var dir = Path.Combine(root, "audio", "voice", folder);
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, file), new byte[] { 0 });
                }

                var files = new MakerPackFiles(root);
                var index = files.VoiceIndex("th");
                foreach (var clip in new[] { "a", "b", "c", ".hidden", "Mixed.Name", "missing" })
                {
                    var listed = files.VoiceFiles("th", clip);
                    var indexed = index.TryGetValue(clip, out var found) ? found : new string[0];
                    Assert.Equal(listed, indexed);
                }

                Assert.Equal(new[] { "audio/voice/th/a.ogg", "audio/voice/th/a.wav" }, index["a"]);
                Assert.Equal(3, index.Count);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        private sealed class SlowCatalog : IMakerStudioCatalog
        {
            private readonly TimeSpan _delay;

            internal SlowCatalog(TimeSpan delay) => _delay = delay;

            internal bool SawTrace { get; private set; }

            public Task<MakerResult<IReadOnlyList<MakerStudioPack>>> PacksAsync(CancellationToken cancel) =>
                Task.FromResult(MakerResult<IReadOnlyList<MakerStudioPack>>.Success(new MakerStudioPack[0]));

            public async Task<MakerResult<MakerStudioLinePage>> LinesAsync(MakerStudioLineQuery query, CancellationToken cancel)
            {
                SawTrace = MakerRequestTrace.Current != null;
                using (MakerRequestTrace.Phase("test.wait"))
                {
                    if (_delay > TimeSpan.Zero) await Task.Delay(_delay).ConfigureAwait(false);
                }

                return MakerResult<MakerStudioLinePage>.Success(new MakerStudioLinePage { Total = 0, CatalogRevision = "cat", Items = new MakerStudioLine[0] });
            }

            public Task<MakerResult<MakerStudioLine>> LineAsync(string packId, string lang, string id, string? occurrenceId, CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioLine>.Fail(MakerErrorCodes.NotFound, "none"));

            public Task<MakerResult<MakerStudioGroups>> GroupsAsync(string packId, string lang, CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioGroups>.Fail(MakerErrorCodes.NotFound, "none"));
        }
    }
}
