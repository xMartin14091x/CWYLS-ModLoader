using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class MakerStudioRouterTests
    {
        [Fact]
        public void Capabilities_are_503_until_a_Studio_backend_is_installed()
        {
            var rig = new MakerRig();

            var response = rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token));

            Assert.Equal(503, response.Status);
            Assert.Equal(MakerErrorCodes.Unavailable, ApiTestKit.ErrorCode(response));
            Assert.Contains("not installed", response.Body);
        }

        [Fact]
        public void Empty_installed_backend_advertises_no_features_and_the_approved_limits()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends());

            var response = rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token));

            Assert.Equal(200, response.Status);
            Assert.Equal(
                "{\"contract\":2,\"features\":[],\"uiText\":false,\"limits\":{\"jsonBytes\":65536,\"wavBytes\":16777216,\"wavSeconds\":60,\"subtitleRevisions\":20,\"takesPerLine\":3,\"projectBytes\":2147483648},\"storageSchema\":1,\"consentVersion\":\"consent-1\"}",
                response.Body);
        }

        [Fact]
        public void Capability_route_keeps_the_v1_security_and_input_rules()
        {
            var studio = new MakerStudioBackends();
            var rig = new MakerRig(studio: () => studio);

            Assert.Equal(401, rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", null)).Status);
            Assert.Equal(403, rig.Send(MakerHttp.Get(
                "/v1/maker/studio/capabilities",
                rig.Token,
                ("Origin", "http://evil.example"))).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Json(
                "POST",
                "/v1/maker/studio/capabilities",
                rig.Token,
                "{}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Get(
                "/v1/maker/studio/capabilities?extra=1",
                rig.Token)).Status);
        }

        [Fact]
        public void A_throwing_Studio_slot_is_unavailable_without_leaking_the_exception()
        {
            var rig = new MakerRig(studio: () => throw new System.InvalidOperationException("secret detail"));

            var response = rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token));

            Assert.Equal(503, response.Status);
            Assert.DoesNotContain("secret detail", response.Body);
            Assert.Contains(rig.Diagnostics, d => d.Contains("secret detail"));
        }

        [Fact]
        public void Cached_status_has_the_binding_nullable_wire_shape_without_advertising_catalog()
        {
            var studio = new MakerStudioBackends(status: new FakeStatus());
            var rig = new MakerRig(studio: () => studio);

            var capabilities = rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token));
            var response = rig.Send(MakerHttp.Get("/v1/maker/studio/status", rig.Token));

            Assert.Equal(200, response.Status);
            Assert.Equal(
                "{\"gameVersion\":\"1.18.0\",\"loaderVersion\":\"0.9.2\",\"gameLanguage\":\"th\",\"gameLanguageIsPack\":true,\"roomReady\":true,\"focused\":false,\"paused\":null,\"busy\":true,\"reason\":\"satone_speaking\",\"observedAt\":\"2026-11-08T00:00:00.0000000+07:00\",\"stale\":false,\"focusOnPlay\":true}",
                response.Body);
            Assert.Empty(Assert.IsType<System.Collections.Generic.List<object?>>(ApiTestKit.Json(capabilities)["features"]));
        }

        [Fact]
        public void Status_requires_its_backend_and_keeps_get_query_and_body_rules()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends());

            Assert.Equal(503, rig.Send(MakerHttp.Get("/v1/maker/studio/status", rig.Token)).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/status", rig.Token, "{}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Get("/v1/maker/studio/status?fresh=true", rig.Token)).Status);
        }

        [Theory]
        [InlineData("consent_required", 403)]
        [InlineData("conflict", 409)]
        [InlineData("game_paused", 409)]
        [InlineData("render_required", 409)]
        [InlineData("restart_required", 409)]
        [InlineData("take_in_use", 409)]
        [InlineData("take_limit", 409)]
        [InlineData("storage_limit", 413)]
        [InlineData("metadata_unavailable", 503)]
        [InlineData("recovery_required", 503)]
        public void Every_Studio_error_keeps_its_contract_status_and_code(string code, int status)
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends(status: new FailingStatus(code)));

            var response = rig.Send(MakerHttp.Get("/v1/maker/studio/status", rig.Token));

            Assert.Equal(status, response.Status);
            Assert.Equal(code, ApiTestKit.ErrorCode(response));
        }

        [Fact]
        public void Status_polls_run_on_the_caller_without_consuming_backend_slots()
        {
            var backend = new InspectStatus();
            var rig = new MakerRig(studio: () => new MakerStudioBackends(status: backend));
            backend.Check = () =>
            {
                Assert.Equal(0, rig.Router.BackendCalls);
                Assert.Equal(backend.CallerThread, System.Environment.CurrentManagedThreadId);
            };
            backend.CallerThread = System.Environment.CurrentManagedThreadId;
            for (var i = 0; i < 100; i++)
                Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/studio/status", rig.Token)).Status);
            Assert.DoesNotContain(typeof(IMakerStudioCatalog).GetMethods(), m => m.Name == "StatusAsync");
        }

        [Fact]
        public void Dubs_routes_require_installed_backend_and_validate_segments_queries_and_bodies()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends());
            var takeId = new string('a', 32);
            var op = new string('1', 32);
            const string lineBase = "/v1/maker/studio/packs/sample.pack/languages/th/lines/one";

            // 503 when the dubs backend is not installed, and capabilities does not advertise "dubs".
            var features = (System.Collections.Generic.IList<object?>)ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token)))["features"]!;
            Assert.DoesNotContain("dubs", features);
            Assert.Equal(503, rig.Send(MakerHttp.Get(lineBase + "/takes", rig.Token)).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Json("POST", lineBase + "/takes", rig.Token,
                "{\"expectedRevision\":\"rev1\",\"operationId\":\"" + op + "\",\"origin\":\"own\",\"consent\":{\"confirmed\":true,\"statementVersion\":\"consent-1\"}}")).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Get(lineBase + "/takes/" + takeId + "/audio?kind=source", rig.Token)).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Build("PUT", lineBase + "/takes/" + takeId + "/audio?kind=source&expectedRevision=rev1&operationId=" + op,
                rig.Token, TestWav.Tone(0.2, 16000, 1), "audio/wav")).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Json("PATCH", lineBase + "/takes/" + takeId, rig.Token,
                "{\"expectedRevision\":\"rev1\",\"operationId\":\"" + op + "\",\"evenOutVolume\":true}")).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Build("DELETE", lineBase + "/takes/" + takeId + "?expectedRevision=rev1&operationId=" + op, rig.Token, null, null)).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Json("PUT", lineBase + "/favourite", rig.Token,
                "{\"expectedRevision\":\"rev1\",\"operationId\":\"" + op + "\",\"takeId\":null}")).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Json("POST", lineBase + "/publish", rig.Token,
                "{\"expectedRevision\":\"rev1\",\"expectedClipRevision\":\"clip1\",\"operationId\":\"" + op + "\",\"takeId\":\"" + takeId + "\"}")).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Json("POST", lineBase + "/unpublish", rig.Token,
                "{\"expectedRevision\":\"rev1\",\"expectedClipRevision\":\"clip1\",\"operationId\":\"" + op + "\"}")).Status);

            // Validation errors before calling backend:
            Assert.Equal(400, rig.Send(MakerHttp.Get(lineBase + "/takes/not-hex-32/audio?kind=source", rig.Token)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Get(lineBase + "/takes/" + takeId + "/audio?kind=other", rig.Token)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", lineBase + "/takes", rig.Token,
                "{\"expectedRevision\":\"rev1\",\"operationId\":\"" + op + "\",\"origin\":\"invalid\",\"consent\":{\"confirmed\":true,\"statementVersion\":\"consent-1\"}}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PATCH", lineBase + "/takes/" + takeId, rig.Token,
                "{\"expectedRevision\":\"rev1\",\"operationId\":\"" + op + "\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Build("DELETE", lineBase + "/takes/" + takeId + "?expectedRevision=rev1", rig.Token, null, null)).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Build("DELETE", lineBase + "/takes", rig.Token, null, null)).Status);
        }

        [Fact]
        public void Play_route_requires_installed_backend_and_validates_query_and_body()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends());
            var features = (System.Collections.Generic.IList<object?>)ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token)))["features"]!;
            Assert.DoesNotContain("playback", features);

            Assert.Equal(503, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"original\",\"lang\":\"th\"}")).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Get("/v1/maker/studio/play", rig.Token)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play?extra=1", rig.Token,
                "{\"id\":\"one\",\"variant\":\"original\",\"lang\":\"th\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"variant\":\"original\",\"lang\":\"th\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"other\",\"lang\":\"th\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"original\",\"lang\":\"INVALID\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"dub\",\"lang\":\"th\",\"packId\":\"BAD_PACK\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"dub\",\"lang\":\"th\",\"expectedRevision\":\"rev1\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"dub\",\"lang\":\"th\",\"packId\":\"sample.pack\",\"expectedRevision\":\"bad token!\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"original\",\"lang\":\"th\",\"unknown\":true}")).Status);
        }

        private sealed class InspectStatus : IMakerStudioStatus
        {
            internal System.Action Check = () => { };
            internal int CallerThread;
            public Task<MakerResult<MakerStudioStatus>> StatusAsync(CancellationToken cancel)
            {
                Check();
                return Task.FromResult(MakerResult<MakerStudioStatus>.Success(new MakerStudioStatus()));
            }
        }

        private sealed class FakeStatus : IMakerStudioStatus
        {
            public Task<MakerResult<MakerStudioStatus>> StatusAsync(CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioStatus>.Success(new MakerStudioStatus
                {
                    GameVersion = "1.18.0",
                    LoaderVersion = "0.9.2",
                    GameLanguage = "th",
                    GameLanguageIsPack = true,
                    RoomReady = true,
                    Focused = false,
                    Paused = null,
                    Busy = true,
                    Reason = "satone_speaking",
                    ObservedAt = "2026-11-08T00:00:00.0000000+07:00",
                    Stale = false,
                    FocusOnPlay = true,
                }));
        }

        private sealed class FailingStatus : IMakerStudioStatus
        {
            private readonly string _code;

            internal FailingStatus(string code) => _code = code;

            public Task<MakerResult<MakerStudioStatus>> StatusAsync(CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioStatus>.Fail(_code, "kept"));
        }
    }
}
