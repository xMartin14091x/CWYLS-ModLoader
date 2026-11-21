using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Loaders;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// DS-01: Pack Maker's request rules (Host, Origin, Sec-Fetch-Site, token), segments, queries, body limits,
    /// every route reaching a fake backend, 503 without backends, error mapping, timeouts, the events long-poll,
    /// and the hook in the API router. Sample data is invented; nothing here comes from the game.
    /// </summary>
    public class MakerRouterTests
    {
        // ---- request rules -------------------------------------------------------------------------------

        [Theory]
        [InlineData("127.0.0.1:7870")]
        [InlineData("localhost:7870")]
        public void Both_loopback_hosts_are_accepted(string host)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token, ("Host", host)));

            Assert.Equal(200, response.Status);
        }

        [Theory]
        [InlineData("localhost:7871")]
        [InlineData("127.0.0.1:7871")]
        [InlineData("127.0.0.1")]
        [InlineData("localhost")]
        [InlineData("evil.example:7870")]
        [InlineData("[::1]:7870")]
        [InlineData("127.0.0.2:7870")]
        [InlineData("")]
        public void Any_other_host_is_421_on_every_maker_path(string host)
        {
            var rig = new MakerRig();
            foreach (var target in new[] { "/v1/maker/status", "/maker/", "/maker" })
            {
                var response = rig.Send(MakerHttp.Get(target, rig.Token, ("Host", host)));

                Assert.Equal(421, response.Status);
                Assert.Equal(MakerHttpCodes.HostRefused, ApiTestKit.ErrorCode(response));
            }
        }

        [Fact]
        public void A_missing_or_doubled_host_is_421()
        {
            var rig = new MakerRig();
            var headers = new ApiHeaders();
            headers.Add("Authorization", ApiTestKit.AuthValue(rig.Token));
            Assert.Equal(421, rig.Send(new ApiRequest("test", "GET", "/v1/maker/status", headers, null, false, null)).Status);

            var doubled = MakerHttp.Get("/v1/maker/status", rig.Token);
            doubled.Headers.Add("Host", "127.0.0.1:7870");
            Assert.Equal(421, rig.Send(doubled).Status);
        }

        [Theory]
        [InlineData("http://127.0.0.1:7870")]
        [InlineData("http://localhost:7870")]
        public void The_pages_own_origins_are_accepted(string origin)
        {
            var rig = new MakerRig();

            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token, ("Origin", origin))).Status);
        }

        [Theory]
        [InlineData("http://127.0.0.1:7871")]
        [InlineData("http://localhost:3000")]
        [InlineData("http://localhost:7871")]
        [InlineData("https://127.0.0.1:7870")]
        [InlineData("http://127.0.0.1:7870/")]
        [InlineData("http://evil.example")]
        [InlineData("http://evil.example:7870")]
        [InlineData("null")]
        [InlineData("")]
        public void Another_origin_is_403_even_another_localhost_port(string origin)
        {
            var rig = new MakerRig();
            foreach (var target in new[] { "/v1/maker/status", "/maker/" })
            {
                var response = rig.Send(MakerHttp.Get(target, rig.Token, ("Origin", origin)));

                Assert.Equal(403, response.Status);
                Assert.Equal(MakerHttpCodes.ForbiddenOrigin, ApiTestKit.ErrorCode(response));
            }

            Assert.Empty(rig.Backend.Calls);
        }

        [Theory]
        [InlineData("same-origin", 200)]
        [InlineData("none", 200)]
        [InlineData("cross-site", 403)]
        [InlineData("same-site", 403)]
        [InlineData("", 403)]
        [InlineData("SAME-ORIGIN", 403)]
        public void Sec_fetch_site_must_be_same_origin_or_none(string site, int status)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token, ("Sec-Fetch-Site", site), ("Sec-Fetch-Mode", "cors")));

            Assert.Equal(status, response.Status);
            if (status == 403)
            {
                Assert.Equal(MakerHttpCodes.ForbiddenOrigin, ApiTestKit.ErrorCode(response));
            }
        }

        [Fact]
        public void A_same_origin_page_request_with_all_browser_headers_passes()
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Get(
                "/v1/maker/status",
                rig.Token,
                ("Origin", "http://127.0.0.1:7870"),
                ("Sec-Fetch-Site", "same-origin"),
                ("Sec-Fetch-Mode", "cors"),
                ("Sec-Fetch-Dest", "empty")));

            Assert.Equal(200, response.Status);
            Assert.Null(ApiTestKit.Header(response, "Access-Control-Allow-Origin"));
        }

        [Fact]
        public void Checks_run_host_then_origin_then_token()
        {
            var rig = new MakerRig();

            Assert.Equal(421, rig.Send(MakerHttp.Get("/v1/maker/status", null, ("Host", "evil.example"), ("Origin", "http://evil.example"))).Status);
            Assert.Equal(403, rig.Send(MakerHttp.Get("/v1/maker/status", null, ("Origin", "http://evil.example"))).Status);
            Assert.Equal(401, rig.Send(MakerHttp.Get("/v1/maker/status", null)).Status);
        }

        [Fact]
        public void A_missing_token_is_401_with_a_challenge()
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Get("/v1/maker/status", null));

            Assert.Equal(401, response.Status);
            Assert.Equal(MakerHttpCodes.Unauthorized, ApiTestKit.ErrorCode(response));
            Assert.StartsWith(ApiToken.Scheme, ApiTestKit.Header(response, "WWW-Authenticate"));
            Assert.Empty(rig.Backend.Calls);
        }

        [Fact]
        public void A_wrong_doubled_or_malformed_token_is_401()
        {
            var rig = new MakerRig();

            Assert.Equal(401, rig.Send(MakerHttp.Get("/v1/maker/status", ApiToken.Create())).Status);

            var doubled = MakerHttp.Get("/v1/maker/status", rig.Token);
            doubled.Headers.Add("Authorization", ApiTestKit.AuthValue(rig.Token));
            Assert.Equal(401, rig.Send(doubled).Status);

            foreach (var value in new[] { rig.Token.Reveal(), "Basic " + rig.Token.Reveal(), "Bearer  " + rig.Token.Reveal(), "Bearer " + rig.Token.Reveal().ToUpperInvariant() })
            {
                var r = MakerHttp.Get("/v1/maker/status", null, ("Authorization", value));
                Assert.Equal(401, rig.Send(r).Status);
            }

            Assert.Empty(rig.Backend.Calls);
        }

        [Fact]
        public void The_scheme_word_is_case_insensitive()
        {
            var rig = new MakerRig();
            var r = MakerHttp.Get("/v1/maker/status", null, ("Authorization", "bearer " + rig.Token.Reveal()));

            Assert.Equal(200, rig.Send(r).Status);
        }

        [Fact]
        public void The_token_never_reaches_a_response_body_header_or_audit_record_of_the_api_routes()
        {
            var rig = new MakerRig();
            var responses = new[]
            {
                rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token)),
                rig.Send(MakerHttp.Get("/v1/maker/status", null)),
                rig.Send(MakerHttp.Get("/v1/maker/nope", rig.Token)),
                rig.Send(MakerHttp.Get("/v1/maker/lines?lang=XX", rig.Token)),
            };

            var secret = rig.Token.Reveal();
            foreach (var response in responses)
            {
                Assert.DoesNotContain(secret, response.Body);
                Assert.DoesNotContain(response.Headers, h => h.Value.Contains(secret));
            }

            Assert.Equal(responses.Length, rig.Records.Count);
            foreach (var (record, _) in rig.Records)
            {
                Assert.DoesNotContain(secret, record.Path + record.Client + record.Error + record.Primary);
                Assert.DoesNotContain("?", record.Path);
            }

            Assert.DoesNotContain(secret, string.Join("\n", rig.Diagnostics));
        }

        [Fact]
        public void The_audit_names_the_maker_client_after_the_token_and_a_dash_before()
        {
            var rig = new MakerRig();
            rig.Send(MakerHttp.Get("/v1/maker/status", null));
            rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token));
            rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token, (ApiDefaults.ClientHeader, "pack-maker")));

            Assert.Equal("-", rig.Records[0].Record.Client);
            Assert.False(rig.Records[0].Record.Authenticated);
            Assert.Equal("maker", rig.Records[1].Record.Client);
            Assert.True(rig.Records[1].Record.Authenticated);
            Assert.Equal("pack-maker", rig.Records[2].Record.Client);
            Assert.Equal("GET /v1/maker/status 200", rig.Records[2].Record.Primary);
        }

        [Fact]
        public void The_maker_only_listener_answers_404_for_every_other_route()
        {
            var rig = new MakerRig();
            foreach (var target in new[] { "/v1/info", "/v1/state", "/", "/makerx", "/v1/makerx", "/v1/events" })
            {
                var response = rig.Send(MakerHttp.Get(target, rig.Token));

                Assert.Equal(404, response.Status);
            }

            Assert.Empty(rig.Backend.Calls);
        }

        // ---- every route reaches the backend --------------------------------------------------------------

        [Fact]
        public void Status_reaches_the_catalog()
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token));

            Assert.Equal(200, response.Status);
            Assert.Equal("{\"gameVersion\":\"1.18.0\",\"loaderVersion\":\"0.9.2\",\"gameLanguage\":\"th\",\"gameLanguageIsPack\":true,\"roomReady\":true}", response.Body);
            Assert.Equal(new[] { "status" }, rig.Backend.Calls);
            Assert.Equal(ApiResponse.ContentType, response.MediaType);
        }

        [Fact]
        public void Packs_reach_the_catalog()
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Get("/v1/maker/packs", rig.Token));

            Assert.Equal(200, response.Status);
            Assert.Equal(
                "{\"packs\":[{\"id\":\"thai-dub\",\"name\":\"Thai dub\",\"version\":\"0.1.0\",\"languages\":[\"th\"],\"editable\":true,\"readOnlyReason\":null}," +
                "{\"id\":\"code-mod\",\"name\":\"Code mod\",\"version\":\"1.0.0\",\"languages\":[],\"editable\":false,\"readOnlyReason\":\"a code mod\"}]}",
                response.Body);
            Assert.Equal(new[] { "packs" }, rig.Backend.Calls);
        }

        [Fact]
        public void Lines_pass_every_query_member_and_the_defaults()
        {
            var rig = new MakerRig();
            var full = rig.Send(MakerHttp.Get("/v1/maker/lines?lang=pt-br&q=rain%20song&filter=missing-dub&offset=5&limit=50", rig.Token));
            var plain = rig.Send(MakerHttp.Get("/v1/maker/lines?lang=th", rig.Token));

            Assert.Equal(200, full.Status);
            Assert.Equal(200, plain.Status);
            Assert.Equal(new[] { "lines pt-br [rain song] missing-dub 5 50", "lines th [] all 0 100" }, rig.Backend.Calls);
            var page = ApiTestKit.Json(full);
            Assert.Equal(2L, page["total"]);
            Assert.Equal(5L, page["offset"]);
            var item = (JsonObject)((List<object?>)page["items"]!)[0]!;
            Assert.Equal(
                new[] { "id", "voice", "group", "durationSec", "ja", "en", "subtitle", "subtitlePack", "dubPack", "dubDurationSec" },
                item.Keys.ToArray());
            Assert.Equal("sample_001", item["id"]);
            Assert.Null(item["dubPack"]);
        }

        [Fact]
        public void One_line_reaches_the_catalog()
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Get("/v1/maker/lines/sample_001?lang=th", rig.Token));

            Assert.Equal(200, response.Status);
            Assert.Equal("sample_001", ApiTestKit.Json(response)["id"]);
            Assert.Equal(new[] { "line sample_001 th" }, rig.Backend.Calls);
        }

        [Fact]
        public void Subtitle_put_and_delete_reach_the_editor()
        {
            var rig = new MakerRig();
            var put = rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/thai-dub/subtitles/th/sample_001", rig.Token, "{\"text\":\"สวัสดีค่ะ\\nบรรทัดสอง\"}"));
            var delete = rig.Send(MakerHttp.Build("DELETE", "/v1/maker/packs/thai-dub/subtitles/th/sample_001", rig.Token, null, null));

            Assert.Equal(200, put.Status);
            Assert.Equal(200, delete.Status);
            Assert.Equal(new[] { "subtitle thai-dub th sample_001 [สวัสดีค่ะ\nบรรทัดสอง]", "subtitle thai-dub th sample_001 <null>" }, rig.Backend.Calls);
            Assert.Equal("{\"live\":true,\"path\":\"data/subtitles/th.json\",\"durationSec\":null,\"note\":\"Saved.\"}", put.Body);
        }

        [Fact]
        public void Voice_put_and_delete_reach_the_editor_with_the_exact_bytes()
        {
            var rig = new MakerRig();
            var wav = TestWav.Tone(seconds: 1.5, rate: 22050, channels: 2);
            var put = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/thai-dub/voice/th/voice_sample_01", rig.Token, wav, "audio/wav"));
            Assert.Equal(wav, rig.Backend.LastWav);
            var delete = rig.Send(MakerHttp.Build("DELETE", "/v1/maker/packs/thai-dub/voice/th/voice_sample_01", rig.Token, null, null));

            Assert.Equal(200, put.Status);
            Assert.Equal(200, delete.Status);
            Assert.Equal(new[] { "voice thai-dub th voice_sample_01 " + wav.Length, "voice thai-dub th voice_sample_01 <null>" }, rig.Backend.Calls);
            Assert.Null(rig.Backend.LastWav);
        }

        [Fact]
        public void Play_reaches_the_editor()
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Json("POST", "/v1/maker/play", rig.Token, "{\"id\":\"sample_001\",\"variant\":\"dub\",\"lang\":\"th\"}"));
            var original = rig.Send(MakerHttp.Json("POST", "/v1/maker/play", rig.Token, "{\"variant\":\"original\",\"lang\":\"th\",\"id\":\"sample_002\"}"));

            Assert.Equal(200, response.Status);
            Assert.Equal(200, original.Status);
            Assert.Equal(new[] { "play sample_001 dub th", "play sample_002 original th" }, rig.Backend.Calls);
        }

        [Fact]
        public void Events_answer_with_the_v1_events_envelope()
        {
            var rig = new MakerRig();
            rig.Router.Events.Publish("maker.subtitle.saved", new Dictionary<string, object?> { ["packId"] = "thai-dub", ["lang"] = "th", ["id"] = "sample_001", ["removed"] = false });
            var response = rig.Send(MakerHttp.Get("/v1/maker/events?after=0", rig.Token));

            Assert.Equal(200, response.Status);
            var page = ApiTestKit.Json(response);
            Assert.Equal(new[] { "epoch", "events", "next", "latest", "oldest", "dropped", "reset", "timedOut" }, page.Keys.ToArray());
            var e = (JsonObject)((List<object?>)page["events"]!)[0]!;
            Assert.Equal(1L, e["seq"]);
            Assert.Equal("maker.subtitle.saved", e["type"]);
            Assert.Equal("{\"packId\":\"thai-dub\",\"lang\":\"th\",\"id\":\"sample_001\",\"removed\":false}", ApiJson.Write(e["data"]));
            Assert.Empty(rig.Backend.Calls);
        }

        // ---- no backend, backend errors, timeouts ---------------------------------------------------------

        public static IEnumerable<object[]> EveryBackendRoute()
        {
            yield return new object[] { "GET", "/v1/maker/status", null!, null! };
            yield return new object[] { "GET", "/v1/maker/packs", null!, null! };
            yield return new object[] { "GET", "/v1/maker/lines?lang=th", null!, null! };
            yield return new object[] { "GET", "/v1/maker/lines/sample_001?lang=th", null!, null! };
            yield return new object[] { "PUT", "/v1/maker/packs/thai-dub/subtitles/th/sample_001", "{\"text\":\"x\"}", "application/json; charset=utf-8" };
            yield return new object[] { "DELETE", "/v1/maker/packs/thai-dub/subtitles/th/sample_001", null!, null! };
            yield return new object[] { "PUT", "/v1/maker/packs/thai-dub/voice/th/clip_01", "WAV", "audio/wav" };
            yield return new object[] { "DELETE", "/v1/maker/packs/thai-dub/voice/th/clip_01", null!, null! };
            yield return new object[] { "POST", "/v1/maker/play", "{\"id\":\"sample_001\",\"variant\":\"original\",\"lang\":\"th\"}", "application/json" };
        }

        [Theory]
        [MemberData(nameof(EveryBackendRoute))]
        public void Every_route_is_503_without_backends(string method, string target, string? body, string? type)
        {
            var rig = new MakerRig(backends: false);
            var response = rig.Send(MakerHttp.Build(method, target, rig.Token, MakerHttp.BodyOf(body), type));

            Assert.Equal(503, response.Status);
            Assert.Equal(MakerErrorCodes.Unavailable, ApiTestKit.ErrorCode(response));
        }

        [Theory]
        [MemberData(nameof(EveryBackendRoute))]
        public void Every_route_maps_a_backend_error_to_its_status(string method, string target, string? body, string? type)
        {
            var rig = new MakerRig();
            rig.Backend.FailCode = MakerErrorCodes.NotReady;
            var response = rig.Send(MakerHttp.Build(method, target, rig.Token, MakerHttp.BodyOf(body), type));

            Assert.Equal(409, response.Status);
            Assert.Equal(MakerErrorCodes.NotReady, ApiTestKit.ErrorCode(response));
            Assert.Single(rig.Backend.Calls);
        }

        [Theory]
        [InlineData(MakerErrorCodes.BadRequest, 400)]
        [InlineData(MakerErrorCodes.NotFound, 404)]
        [InlineData(MakerErrorCodes.ReadOnlyPack, 403)]
        [InlineData(MakerErrorCodes.NotReady, 409)]
        [InlineData(MakerErrorCodes.TooLarge, 413)]
        [InlineData(MakerErrorCodes.UnsupportedMedia, 415)]
        [InlineData(MakerErrorCodes.Unavailable, 503)]
        [InlineData(MakerErrorCodes.Internal, 500)]
        public void Backend_error_codes_map_to_the_contract_statuses(string code, int status)
        {
            var rig = new MakerRig();
            rig.Backend.FailCode = code;
            rig.Backend.FailMessage = "the pack thai-dub is read-only";
            var response = rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/thai-dub/subtitles/th/sample_001", rig.Token, "{\"text\":\"x\"}"));

            Assert.Equal(status, response.Status);
            Assert.Equal(code, ApiTestKit.ErrorCode(response));
            Assert.Equal("the pack thai-dub is read-only", ((JsonObject)ApiTestKit.Json(response)["error"]!)["message"]);
        }

        [Fact]
        public void The_contract_error_table_is_complete()
        {
            Assert.Equal(400, MakerDefaults.StatusOf("bad_request"));
            Assert.Equal(401, MakerDefaults.StatusOf("unauthorized"));
            Assert.Equal(403, MakerDefaults.StatusOf("read_only_pack"));
            Assert.Equal(403, MakerDefaults.StatusOf("forbidden_origin"));
            Assert.Equal(404, MakerDefaults.StatusOf("not_found"));
            Assert.Equal(409, MakerDefaults.StatusOf("not_ready"));
            Assert.Equal(413, MakerDefaults.StatusOf("too_large"));
            Assert.Equal(415, MakerDefaults.StatusOf("unsupported_media_type"));
            Assert.Equal(421, MakerDefaults.StatusOf("host_refused"));
            Assert.Equal(429, MakerDefaults.StatusOf("rate_limited"));
            Assert.Equal(500, MakerDefaults.StatusOf("internal"));
            Assert.Equal(503, MakerDefaults.StatusOf("unavailable"));
            Assert.Equal(500, MakerDefaults.StatusOf("something_new"));
        }

        [Fact]
        public void An_unknown_backend_code_becomes_internal()
        {
            var rig = new MakerRig();
            rig.Backend.FailCode = "made_up";
            var response = rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token));

            Assert.Equal(500, response.Status);
            Assert.Equal(MakerErrorCodes.Internal, ApiTestKit.ErrorCode(response));
            Assert.Contains(rig.Diagnostics, d => d.Contains("made_up"));
        }

        [Fact]
        public void A_backend_that_throws_faults_or_returns_nothing_is_500_and_never_crashes()
        {
            var rig = new MakerRig();
            rig.Backend.ThrowAtOnce = true;
            Assert.Equal(500, rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token)).Status);

            rig.Backend.ThrowAtOnce = false;
            rig.Backend.Fault = true;
            Assert.Equal(500, rig.Send(MakerHttp.Get("/v1/maker/packs", rig.Token)).Status);

            rig.Backend.Fault = false;
            rig.Backend.NullResult = true;
            Assert.Equal(500, rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token)).Status);

            rig.Backend.NullResult = false;
            rig.Backend.NullValue = true;
            Assert.Equal(500, rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token)).Status);
        }

        [Fact]
        public void A_backend_that_never_answers_is_503_after_the_timeout_and_its_call_is_cancelled()
        {
            var rig = new MakerRig(timeout: TimeSpan.FromMilliseconds(100), grace: TimeSpan.FromMilliseconds(50));
            rig.Backend.Hang = true;
            var watch = Stopwatch.StartNew();
            var response = rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token));
            watch.Stop();

            Assert.Equal(503, response.Status);
            Assert.Equal(MakerErrorCodes.Unavailable, ApiTestKit.ErrorCode(response));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "took " + watch.Elapsed);
            Assert.True(rig.Backend.LastToken.IsCancellationRequested);
        }

        [Fact]
        public void A_backend_that_ignores_cancellation_still_gets_503_on_time()
        {
            var rig = new MakerRig(timeout: TimeSpan.FromMilliseconds(100), grace: TimeSpan.FromMilliseconds(50));
            rig.Backend.HangForever = true;
            var watch = Stopwatch.StartNew();
            var response = rig.Send(MakerHttp.Json("POST", "/v1/maker/play", rig.Token, "{\"id\":\"a\",\"variant\":\"dub\",\"lang\":\"th\"}"));

            Assert.Equal(503, response.Status);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "took " + watch.Elapsed);
        }

        [Fact]
        public void The_default_backend_wait_is_the_dispatchers_two_seconds_plus_grace()
        {
            Assert.Equal(TimeSpan.FromSeconds(2), MakerDefaults.BackendTimeout);
            Assert.Equal(TimeSpan.FromSeconds(2), new MakerRouterOptions().BackendTimeout);
            Assert.True(MakerDefaults.BackendGrace > TimeSpan.Zero && MakerDefaults.BackendGrace <= TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task After_stop_every_maker_request_is_503_and_backend_calls_are_cancelled()
        {
            var rig = new MakerRig(timeout: TimeSpan.FromSeconds(5));
            rig.Backend.Hang = true;
            var pending = Task.Run(() => rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token)));
            SpinWait.SpinUntil(() => rig.Backend.Calls.Count == 1, 2000);
            rig.Router.BeginStop();

            var answered = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(503, answered.Status);
            Assert.Equal(503, rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token)).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Get("/v1/info", rig.Token)).Status);
        }

        // ---- routes, methods, segments, queries -----------------------------------------------------------

        [Theory]
        [InlineData("POST", "/v1/maker/status")]
        [InlineData("PUT", "/v1/maker/lines")]
        [InlineData("DELETE", "/v1/maker/lines/sample_001?lang=th")]
        [InlineData("GET", "/v1/maker/play")]
        [InlineData("GET", "/v1/maker/packs/thai-dub/subtitles/th/sample_001")]
        [InlineData("POST", "/v1/maker/packs/thai-dub/voice/th/clip_01")]
        [InlineData("PATCH", "/v1/maker/packs/thai-dub/subtitles/th/sample_001")]
        [InlineData("POST", "/maker/")]
        [InlineData("GET", "/v1/maker")]
        [InlineData("GET", "/v1/maker/")]
        [InlineData("GET", "/v1/maker/status/")]
        [InlineData("GET", "/v1/maker/nope")]
        [InlineData("GET", "/v1/maker/packs/thai-dub")]
        [InlineData("GET", "/v1/maker/packs/thai-dub/audio/th/x")]
        [InlineData("GET", "/v1/maker/lines/a/b?lang=th")]
        public void Unknown_routes_and_methods_are_404(string method, string target)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Build(method, target, rig.Token, null, null));

            Assert.Equal(404, response.Status);
            Assert.Equal(MakerErrorCodes.NotFound, ApiTestKit.ErrorCode(response));
            Assert.Empty(rig.Backend.Calls);
        }

        [Theory]
        [InlineData("/v1/maker/packs/Thai-Dub/subtitles/th/sample_001")]
        [InlineData("/v1/maker/packs/-thai/subtitles/th/sample_001")]
        [InlineData("/v1/maker/packs/.thai/subtitles/th/sample_001")]
        [InlineData("/v1/maker/packs/thai_dub!/subtitles/th/sample_001")]
        [InlineData("/v1/maker/packs/a0123456789012345678901234567890123456789012345678901234567890123/subtitles/th/sample_001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/TH/sample_001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/t/sample_001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/thai/sample_001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/th-/sample_001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/th-x/sample_001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/th_TH/sample_001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/th-123456789/sample_001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/th/sample 001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/th/sample:001")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/th/.")]
        [InlineData("/v1/maker/packs/thai-dub/subtitles/th/...")]
        [InlineData("/v1/maker/packs/thai-dub/voice/th/..")]
        [InlineData("/v1/maker/packs/thai-dub/voice/th/%2e%2e")]
        [InlineData("/v1/maker/packs/thai-dub/voice/th/a%2fb")]
        [InlineData("/v1/maker/packs/thai-dub/voice/th/a\\b")]
        [InlineData("/v1/maker/packs/thai-dub/voice/th/x0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
        [InlineData("/v1/maker/lines/bad$id?lang=th")]
        public void Bad_segments_are_400_before_any_backend_call(string target)
        {
            var rig = new MakerRig();
            var method = target.StartsWith("/v1/maker/lines", StringComparison.Ordinal) ? "GET" : "DELETE";
            var response = rig.Send(MakerHttp.Build(method, target, rig.Token, null, null));

            Assert.Equal(400, response.Status);
            Assert.Equal(MakerErrorCodes.BadRequest, ApiTestKit.ErrorCode(response));
            Assert.Empty(rig.Backend.Calls);
        }

        [Theory]
        [InlineData("a", true)]
        [InlineData("thai-dub.v2_x", true)]
        [InlineData("0123456789012345678901234567890123456789012345678901234567890123", true)]
        [InlineData("01234567890123456789012345678901234567890123456789012345678901234", false)]
        [InlineData("", false)]
        [InlineData("_x", false)]
        public void Pack_ids_follow_the_contract(string value, bool ok) => Assert.Equal(ok, MakerSegments.IsPackId(value));

        [Theory]
        [InlineData("th", true)]
        [InlineData("fil", true)]
        [InlineData("pt-br", true)]
        [InlineData("zh-hant", true)]
        [InlineData("en-12345678", true)]
        [InlineData("en-123456789", false)]
        [InlineData("e", false)]
        [InlineData("engl", false)]
        [InlineData("en-b", false)]
        [InlineData("en-BR", false)]
        [InlineData("en-br-x1", false)]
        [InlineData("", false)]
        public void Languages_follow_the_contract(string value, bool ok) => Assert.Equal(ok, MakerSegments.IsLang(value));

        [Theory]
        [InlineData("Voice_MainScenario_12(5)_001", true)]
        [InlineData("sample_(1)", true)]
        [InlineData("()", true)]
        [InlineData("(.)", true)]
        [InlineData("a.b-c_d", true)]
        [InlineData(".", false)]
        [InlineData("..", false)]
        [InlineData("...", false)]
        [InlineData("a[1]", false)]
        [InlineData("a{1}", false)]
        [InlineData("a'1", false)]
        [InlineData("a!", false)]
        [InlineData("a b", false)]
        [InlineData("a/b", false)]
        [InlineData("a%28b", false)]
        [InlineData("", false)]
        public void Ids_and_clips_follow_contract_v1_1_with_parentheses(string value, bool ok) => Assert.Equal(ok, MakerSegments.IsId(value));

        [Fact]
        public void A_game_voice_name_with_parentheses_works_as_id_and_clip_on_every_route()
        {
            const string Name = "Voice_MainScenario_12(5)_001";
            var rig = new MakerRig();
            var wav = TestWav.Tone(0.5, 16000, 1);

            Assert.Equal(200, rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/thai-dub/subtitles/th/" + Name, rig.Token, "{\"text\":\"x\"}")).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Build("DELETE", "/v1/maker/packs/thai-dub/subtitles/th/" + Name, rig.Token, null, null)).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/thai-dub/voice/th/" + Name, rig.Token, wav, "audio/wav")).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Build("DELETE", "/v1/maker/packs/thai-dub/voice/th/" + Name, rig.Token, null, null)).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/lines/" + Name + "?lang=th", rig.Token)).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Json("POST", "/v1/maker/play", rig.Token, "{\"id\":\"" + Name + "\",\"variant\":\"original\",\"lang\":\"th\"}")).Status);

            Assert.Equal(
                new[]
                {
                    "subtitle thai-dub th " + Name + " [x]",
                    "subtitle thai-dub th " + Name + " <null>",
                    "voice thai-dub th " + Name + " " + wav.Length,
                    "voice thai-dub th " + Name + " <null>",
                    "line " + Name + " th",
                    "play " + Name + " original th",
                },
                rig.Backend.Calls);
        }

        [Theory]
        [InlineData("DELETE", "/v1/maker/packs/thai-dub/voice/th/..", 400)]
        [InlineData("DELETE", "/v1/maker/packs/thai-dub/subtitles/th/..", 400)]
        [InlineData("DELETE", "/v1/maker/packs/thai-dub/voice/th/Voice_MainScenario_12%285%29_001", 400)]
        [InlineData("DELETE", "/v1/maker/packs/thai-dub/subtitles/th/Voice_MainScenario_12%285%29_001", 400)]
        [InlineData("GET", "/v1/maker/lines/Voice_MainScenario_12%285%29_001?lang=th", 400)]
        [InlineData("DELETE", "/v1/maker/packs/thai-dub/voice/th/Voice(5)%2f..%2fx", 400)]
        [InlineData("DELETE", "/v1/maker/packs/thai-dub/voice/th/Voice(5)\\x", 400)]
        [InlineData("DELETE", "/v1/maker/packs/thai-dub/voice/th/Voice[5]", 400)]
        [InlineData("DELETE", "/v1/maker/packs/thai-dub/voice/th/(..)/x", 404)]
        [InlineData("DELETE", "/v1/maker/packs/thai-dub/voice/th/Voice(5)/x", 404)]
        public void Encoded_traversal_and_slashed_names_stay_refused(string method, string target, int status)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Build(method, target, rig.Token, null, null));

            // A raw slash makes another path (no route: 404); everything else is 400 before any backend call.
            Assert.Equal(status, response.Status);
            Assert.Empty(rig.Backend.Calls);
        }

        [Theory]
        [InlineData("/v1/maker/lines")]
        [InlineData("/v1/maker/lines?lang=")]
        [InlineData("/v1/maker/lines?lang=TH")]
        [InlineData("/v1/maker/lines?lang=th&filter=missing")]
        [InlineData("/v1/maker/lines?lang=th&limit=0")]
        [InlineData("/v1/maker/lines?lang=th&limit=501")]
        [InlineData("/v1/maker/lines?lang=th&limit=x")]
        [InlineData("/v1/maker/lines?lang=th&offset=-1")]
        [InlineData("/v1/maker/lines?lang=th&page=2")]
        [InlineData("/v1/maker/lines?lang=th&lang=en")]
        [InlineData("/v1/maker/lines/sample_001")]
        [InlineData("/v1/maker/lines/sample_001?lang=th&q=x")]
        [InlineData("/v1/maker/status?x=1")]
        [InlineData("/v1/maker/packs?verbose=true")]
        [InlineData("/v1/maker/events?after=-1")]
        [InlineData("/v1/maker/events?wait=26")]
        [InlineData("/v1/maker/events?limit=501")]
        [InlineData("/v1/maker/events?since=3")]
        public void Bad_queries_are_400(string target)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Get(target, rig.Token));

            Assert.Equal(400, response.Status);
            Assert.Empty(rig.Backend.Calls);
        }

        [Fact]
        public void A_search_longer_than_200_characters_is_400()
        {
            var rig = new MakerRig();

            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/lines?lang=th&q=" + new string('a', 200), rig.Token)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Get("/v1/maker/lines?lang=th&q=" + new string('a', 201), rig.Token)).Status);
        }

        [Fact]
        public void A_get_or_delete_with_a_body_is_400()
        {
            var rig = new MakerRig();

            Assert.Equal(400, rig.Send(MakerHttp.Json("GET", "/v1/maker/status", rig.Token, "{}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("DELETE", "/v1/maker/packs/thai-dub/subtitles/th/a", rig.Token, "{}")).Status);
            Assert.Empty(rig.Backend.Calls);
        }

        [Fact]
        public void Rate_limits_answer_429_with_retry_after()
        {
            var clock = new ManualApiClock();
            var rig = new MakerRig(limiter: new ApiRateLimiter(clock, 1, 1, 1));

            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token)).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token)).Status);
            var limited = rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token));
            Assert.Equal(429, limited.Status);
            Assert.Equal(MakerHttpCodes.RateLimited, ApiTestKit.ErrorCode(limited));
            Assert.Equal("1", ApiTestKit.Header(limited, "Retry-After"));

            // Requests without the token never use up the budget.
            clock.Advance(2);
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(401, rig.Send(MakerHttp.Get("/v1/maker/status", null)).Status);
            }

            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/status", rig.Token)).Status);
        }

        // ---- JSON bodies --------------------------------------------------------------------------------

        [Fact]
        public void A_json_body_of_64_KiB_is_read_and_one_byte_more_is_413()
        {
            var rig = new MakerRig();
            var exact = PaddedTextBody(MakerDefaults.MaxJsonBytes);
            var over = PaddedTextBody(MakerDefaults.MaxJsonBytes + 1);

            // 64 KiB is read (then refused for its text length, not its size); 64 KiB + 1 is too large.
            var atLimit = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, exact, "application/json"));
            Assert.Equal(400, atLimit.Status);
            Assert.Contains("characters", ((JsonObject)ApiTestKit.Json(atLimit)["error"]!)["message"] as string);

            var tooLarge = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, over, "application/json"));
            Assert.Equal(413, tooLarge.Status);
            Assert.Equal(MakerErrorCodes.TooLarge, ApiTestKit.ErrorCode(tooLarge));

            var undeclared = MakerHttp.Build("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, over, "application/json", declareLength: false);
            Assert.Equal(413, rig.Send(undeclared).Status);
            Assert.Empty(rig.Backend.Calls);
        }

        [Fact]
        public void A_declared_oversize_json_body_is_refused_without_reading_it()
        {
            var rig = new MakerRig();
            var read = false;
            var headers = MakerHttp.Headers(rig.Token, "application/json");
            var request = new ApiRequest("test", "PUT", "/v1/maker/packs/p/subtitles/th/a", headers, MakerDefaults.MaxJsonBytes + 1, false, _ =>
            {
                read = true;
                return ApiBody.Empty;
            });

            Assert.Equal(413, rig.Send(request).Status);
            Assert.False(read);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("text/plain")]
        [InlineData("application/x-www-form-urlencoded")]
        [InlineData("multipart/form-data; boundary=x")]
        [InlineData("application/json; charset=latin1")]
        [InlineData("audio/wav")]
        public void A_json_route_needs_application_json(string? type)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, Encoding.UTF8.GetBytes("{\"text\":\"x\"}"), type));

            Assert.Equal(415, response.Status);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, ApiTestKit.ErrorCode(response));
            Assert.Empty(rig.Backend.Calls);
        }

        [Theory]
        [InlineData("application/json")]
        [InlineData("application/json; charset=utf-8")]
        [InlineData("Application/JSON;charset=UTF-8")]
        public void Json_content_types_with_utf8_are_accepted(string type)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, Encoding.UTF8.GetBytes("{\"text\":\"x\"}"), type));

            Assert.Equal(200, response.Status);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{}")]
        [InlineData("[]")]
        [InlineData("\"text\"")]
        [InlineData("{\"text\":\"\"}")]
        [InlineData("{\"text\":null}")]
        [InlineData("{\"text\":3}")]
        [InlineData("{\"text\":\"x\",\"pack\":\"p\"}")]
        [InlineData("{\"text\":\"x\",\"text\":\"y\"}")]
        [InlineData("{'text':'x'}")]
        [InlineData("{\"text\":\"x\",}")]
        public void Bad_subtitle_bodies_are_400(string body)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, body));

            Assert.Equal(400, response.Status);
            Assert.Equal(MakerErrorCodes.BadRequest, ApiTestKit.ErrorCode(response));
            Assert.Empty(rig.Backend.Calls);
        }

        [Fact]
        public void Invalid_utf8_and_chunked_bodies_are_400()
        {
            var rig = new MakerRig();
            var invalid = MakerHttp.Build("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, new byte[] { (byte)'{', 0xC3, 0x28, (byte)'}' }, "application/json");
            Assert.Equal(400, rig.Send(invalid).Status);

            var chunked = MakerHttp.Build("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, Encoding.UTF8.GetBytes("{\"text\":\"x\"}"), "application/json", ("Transfer-Encoding", "chunked"));
            Assert.Equal(400, rig.Send(chunked).Status);
            Assert.Empty(rig.Backend.Calls);
        }

        [Fact]
        public void Subtitle_text_is_1_to_2000_characters_counted_as_code_points()
        {
            var rig = new MakerRig();
            string Body(string text) => "{\"text\":" + MiniJsonString(text) + "}";

            Assert.Equal(200, rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, Body("ก"))).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, Body(new string('ก', 2000)))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, Body(new string('ก', 2001)))).Status);

            var emoji = string.Concat(Enumerable.Repeat("\U0001F319", 2000)); // 4000 UTF-16 units, 2000 characters
            Assert.Equal(200, rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/p/subtitles/th/a", rig.Token, Body(emoji))).Status);
        }

        [Theory]
        [InlineData("{\"id\":\"a\",\"variant\":\"dub\"}")]
        [InlineData("{\"variant\":\"dub\",\"lang\":\"th\"}")]
        [InlineData("{\"id\":\"a\",\"variant\":\"remix\",\"lang\":\"th\"}")]
        [InlineData("{\"id\":\"a\",\"variant\":\"DUB\",\"lang\":\"th\"}")]
        [InlineData("{\"id\":\"a b\",\"variant\":\"dub\",\"lang\":\"th\"}")]
        [InlineData("{\"id\":\"a\",\"variant\":\"dub\",\"lang\":\"Thai\"}")]
        [InlineData("{\"id\":1,\"variant\":\"dub\",\"lang\":\"th\"}")]
        [InlineData("{\"id\":\"a\",\"variant\":\"dub\",\"lang\":\"th\",\"volume\":1}")]
        public void Bad_play_bodies_are_400(string body)
        {
            var rig = new MakerRig();

            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/play", rig.Token, body)).Status);
            Assert.Empty(rig.Backend.Calls);
        }

        // ---- voice bodies ---------------------------------------------------------------------------------

        [Fact]
        public void A_voice_body_of_exactly_16_MiB_is_accepted_and_one_byte_more_is_413()
        {
            var rig = new MakerRig();
            var exact = TestWav.PaddedTo(MakerDefaults.MaxWavBytes);
            var over = TestWav.PaddedTo(MakerDefaults.MaxWavBytes + 2);

            Assert.Equal(MakerDefaults.MaxWavBytes, exact.Length);
            Assert.Equal(200, rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/voice/th/clip", rig.Token, exact, "audio/wav")).Status);
            Assert.Equal(MakerDefaults.MaxWavBytes, rig.Backend.LastWav!.Length);

            var tooLarge = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/voice/th/clip", rig.Token, over, "audio/wav"));
            Assert.Equal(413, tooLarge.Status);
            Assert.Equal(MakerErrorCodes.TooLarge, ApiTestKit.ErrorCode(tooLarge));
            Assert.Equal(413, rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/voice/th/clip", rig.Token, over, "audio/wav", declareLength: false)).Status);
            Assert.Single(rig.Backend.Calls);
        }

        [Fact]
        public void A_declared_oversize_voice_body_is_refused_without_reading_it()
        {
            var rig = new MakerRig();
            var read = false;
            var request = new ApiRequest("test", "PUT", "/v1/maker/packs/p/voice/th/clip", MakerHttp.Headers(rig.Token, "audio/wav"), MakerDefaults.MaxWavBytes + 1L, false, _ =>
            {
                read = true;
                return ApiBody.Empty;
            });

            Assert.Equal(413, rig.Send(request).Status);
            Assert.False(read);
        }

        [Fact]
        public void A_dub_of_60_seconds_is_accepted_and_longer_is_413()
        {
            var rig = new MakerRig();

            Assert.Equal(200, rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/voice/th/clip", rig.Token, TestWav.Silence(60, 8000, 1), "audio/wav")).Status);
            var tooLong = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/voice/th/clip", rig.Token, TestWav.Silence(60.01, 8000, 1), "audio/wav"));
            Assert.Equal(413, tooLong.Status);
            Assert.Equal(MakerErrorCodes.TooLarge, ApiTestKit.ErrorCode(tooLong));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("audio/x-wav")]
        [InlineData("audio/wave")]
        [InlineData("audio/mpeg")]
        [InlineData("application/octet-stream")]
        [InlineData("application/json")]
        public void A_voice_body_needs_audio_wav(string? type)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/voice/th/clip", rig.Token, TestWav.Tone(0.5, 16000, 1), type));

            Assert.Equal(415, response.Status);
            Assert.Empty(rig.Backend.Calls);
        }

        public static IEnumerable<object[]> NotSupportedWavs()
        {
            var tone = TestWav.Tone(0.5, 16000, 1);
            var noFmt = TestWav.Tone(0.5, 16000, 1);
            noFmt[12] = (byte)'x'; // the "fmt " chunk id becomes "xmt "
            var shortFmt = TestWav.Header(1, 1, 16000, 16, 1600);
            shortFmt[16] = 8; // the "fmt " chunk claims 8 bytes
            yield return new object[] { "not RIFF at all", Encoding.ASCII.GetBytes("ID3 not a wav at all"), "not a WAV" };
            yield return new object[] { "JSON sent as audio/wav", Encoding.UTF8.GetBytes("{\"text\":\"x\"}"), "not a WAV" };
            yield return new object[] { "eleven bytes", Encoding.ASCII.GetBytes("RIFF1234WAV"), "not a WAV" };
            yield return new object[] { "RIFF but not WAVE", Encoding.ASCII.GetBytes("RIFF\u0004\0\0\0AVI LIST"), "not a WAV" };
            yield return new object[] { "truncated data", tone.Take(tone.Length - 100).ToArray(), "truncated" };
            yield return new object[] { "no fmt chunk", noFmt, "'fmt '" };
            yield return new object[] { "fmt chunk too short", shortFmt, "16 bytes" };
            yield return new object[] { "header only, no data", tone.Take(36).ToArray(), "'data'" };
            yield return new object[] { "empty data chunk", TestWav.Header(1, 1, 16000, 16, 0), "no sound" };
            yield return new object[] { "24-bit", TestWav.Header(1, 1, 44100, 24, 4410 * 3), "24-bit" };
            yield return new object[] { "32-bit float", TestWav.Header(3, 1, 44100, 32, 4410 * 4), "format 3" };
            yield return new object[] { "extensible", TestWav.Header(0xFFFE, 1, 44100, 16, 4410 * 2), "format 65534" };
            yield return new object[] { "3 channels", TestWav.Header(1, 3, 44100, 16, 4410 * 6), "3 channels" };
            yield return new object[] { "96 kHz", TestWav.Header(1, 1, 96000, 16, 9600 * 2), "96000 Hz" };
            yield return new object[] { "7999 Hz", TestWav.Header(1, 1, 7999, 16, 800 * 2), "7999 Hz" };
        }

        [Theory]
        [MemberData(nameof(NotSupportedWavs))]
        public void Any_body_that_is_not_a_supported_wav_is_415_naming_the_problem(string what, byte[] body, string named)
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/voice/th/clip", rig.Token, body, "audio/wav"));

            Assert.True(415 == response.Status, what + " answered " + response.Status);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, ApiTestKit.ErrorCode(response));
            Assert.Contains(named, ((JsonObject)ApiTestKit.Json(response)["error"]!)["message"] as string);
            Assert.Equal(MakerWavVerdict.Unsupported, MakerWavCheck.Check(body, out _, out var problem));
            Assert.Contains(named, problem);
            Assert.Empty(rig.Backend.Calls);
        }

        [Fact]
        public void An_empty_voice_body_is_a_malformed_request_400()
        {
            var rig = new MakerRig();
            var response = rig.Send(MakerHttp.Build("PUT", "/v1/maker/packs/p/voice/th/clip", rig.Token, new byte[0], "audio/wav"));

            Assert.Equal(400, response.Status);
            Assert.Equal(MakerErrorCodes.BadRequest, ApiTestKit.ErrorCode(response));
            Assert.Empty(rig.Backend.Calls);
        }

        [Theory]
        [InlineData(8000, 1)]
        [InlineData(16000, 2)]
        [InlineData(44100, 1)]
        [InlineData(48000, 2)]
        public void Pcm16_mono_or_stereo_at_8_to_48_kHz_is_accepted(int rate, int channels)
        {
            var wav = TestWav.Tone(0.25, rate, channels);

            Assert.Equal(MakerWavVerdict.Ok, MakerWavCheck.Check(wav, out var seconds, out var problem));
            Assert.Equal(0.25, seconds, 2);
            Assert.Equal(string.Empty, problem);
        }

        // ---- events long-poll -----------------------------------------------------------------------------

        [Fact]
        public async Task A_long_poll_returns_as_soon_as_an_event_is_published()
        {
            var rig = new MakerRig();
            var poll = Task.Run(() => rig.Send(MakerHttp.Get("/v1/maker/events?after=0&wait=10", rig.Token)));
            SpinWait.SpinUntil(() => rig.Router.Waiting == 1, 2000);
            var watch = Stopwatch.StartNew();
            rig.Router.Events.Publish("maker.play", new Dictionary<string, object?> { ["id"] = "sample_001", ["variant"] = "dub", ["lang"] = "th", ["voice"] = "voice_sample_01" });

            var response = await poll.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            var page = ApiTestKit.Json(response);
            Assert.Equal(false, page["timedOut"]);
            Assert.Equal(1L, page["next"]);
            Assert.Single((List<object?>)page["events"]!);
        }

        [Fact]
        public void A_long_poll_with_nothing_new_times_out_empty()
        {
            var rig = new MakerRig();
            rig.Router.Events.Publish("room.ready", new Dictionary<string, object?> { ["ready"] = true });
            var page = ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/events?after=1&wait=0.2", rig.Token)));

            Assert.Equal(true, page["timedOut"]);
            Assert.Empty((List<object?>)page["events"]!);
            Assert.Equal(1L, page["next"]);
        }

        [Fact]
        public async Task Long_polls_are_capped_and_wake_when_the_router_stops()
        {
            var rig = new MakerRig(maxWaits: 1);
            var first = Task.Run(() => rig.Send(MakerHttp.Get("/v1/maker/events?wait=20", rig.Token)));
            SpinWait.SpinUntil(() => rig.Router.Waiting == 1, 2000);

            var second = rig.Send(MakerHttp.Get("/v1/maker/events?wait=20", rig.Token));
            Assert.Equal(429, second.Status);
            Assert.NotNull(ApiTestKit.Header(second, "Retry-After"));

            rig.Router.BeginStop();
            var woken = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(200, woken.Status);
        }

        [Fact]
        public void The_event_ring_keeps_1024_and_reports_drops()
        {
            var rig = new MakerRig();
            for (var i = 0; i < 1030; i++)
            {
                rig.Router.Events.Publish("line.spoken", new Dictionary<string, object?> { ["localizationId"] = "sample_" + i });
            }

            var page = ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/events?after=0&limit=1", rig.Token)));
            Assert.Equal(1024, rig.Router.Events.Ring.Capacity);
            Assert.Equal(6L, page["dropped"]);
            Assert.Equal(7L, page["oldest"]);
            Assert.Equal(1030L, page["latest"]);
        }

        // ---- the hook in the API router -------------------------------------------------------------------

        [Fact]
        public void Through_the_api_router_maker_routes_accept_the_page_and_other_routes_still_refuse_browsers()
        {
            var api = new RouterRig();
            var maker = new MakerRig();
            api.Router.UseMaker(maker.Router);
            var origin = ("Origin", "http://127.0.0.1:7870");

            Assert.Equal(200, api.Send(MakerHttp.Get("/v1/maker/status", maker.Token, origin)).Status);
            var info = api.Send(ApiTestKit.Get("/v1/info", api.Key, ApiTestKit.Port, origin));
            Assert.Equal(403, info.Status);
            Assert.Equal(ApiErrorCodes.BrowserRefused, ApiTestKit.ErrorCode(info));
            Assert.Equal(403, api.Send(ApiTestKit.Get("/v1/info", api.Key, ApiTestKit.Port, ("Sec-Fetch-Site", "same-origin"))).Status);
            Assert.Equal(421, api.Send(ApiTestKit.Get("/v1/info", api.Key, ApiTestKit.Port, ("Host", "localhost:7870"))).Status);

            // The two keys never stand in for each other.
            Assert.Equal(401, api.Send(MakerHttp.Get("/v1/maker/status", api.Key)).Status);
            Assert.Equal(401, api.Send(ApiTestKit.Get("/v1/info", maker.Token)).Status);
            Assert.Equal(200, api.Send(ApiTestKit.Get("/v1/info", api.Key)).Status);

            // One audit record per request, written by the API router, with the maker's client name.
            var makerRecords = api.Records.Where(r => r.Record.Path.StartsWith("/v1/maker", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, makerRecords.Count);
            Assert.Equal("maker", makerRecords[0].Record.Client);
            Assert.Equal(7, api.Records.Count);
            Assert.Empty(maker.Records);
        }

        [Fact]
        public void Without_the_hook_the_api_router_treats_maker_paths_like_any_other()
        {
            var api = new RouterRig();
            var maker = new MakerRig();

            Assert.Equal(403, api.Send(MakerHttp.Get("/v1/maker/status", maker.Token, ("Origin", "http://127.0.0.1:7870"))).Status);
            Assert.Equal(404, api.Send(ApiTestKit.Get("/v1/maker/status", api.Key)).Status);

            api.Router.UseMaker(maker.Router);
            api.Router.UseMaker(null);
            Assert.Equal(404, api.Send(ApiTestKit.Get("/maker/", api.Key)).Status);
        }

        [Fact]
        public void A_maker_path_never_escapes_to_an_api_route()
        {
            var api = new RouterRig();
            var maker = new MakerRig();
            api.Router.UseMaker(maker.Router);

            foreach (var target in new[] { "/v1/maker/../info", "/maker/../v1/info", "/v1/maker/..", "/maker/%2e%2e/v1/info" })
            {
                var response = api.Send(ApiTestKit.Get(target, api.Key));
                Assert.Equal(400, response.Status);
            }
        }

        [Fact]
        public void Owns_takes_exactly_the_maker_paths()
        {
            Assert.True(MakerDefaults.Owns("/maker"));
            Assert.True(MakerDefaults.Owns("/maker?x=1"));
            Assert.True(MakerDefaults.Owns("/maker/"));
            Assert.True(MakerDefaults.Owns("/maker/app.js"));
            Assert.True(MakerDefaults.Owns("/v1/maker"));
            Assert.True(MakerDefaults.Owns("/v1/maker/status"));
            Assert.False(MakerDefaults.Owns("/makers"));
            Assert.False(MakerDefaults.Owns("/v1/makerx"));
            Assert.False(MakerDefaults.Owns("/v1/info"));
            Assert.False(MakerDefaults.Owns("/MAKER/"));
            Assert.False(MakerDefaults.Owns("http://127.0.0.1:7870/maker/"));
            Assert.False(MakerDefaults.Owns(""));
            Assert.False(MakerDefaults.Owns(null));
        }

        private static byte[] PaddedTextBody(int size)
        {
            var head = "{\"text\":\"";
            var tail = "\"}";
            return Encoding.UTF8.GetBytes(head + new string('a', size - head.Length - tail.Length) + tail);
        }

        private static string MiniJsonString(string text) => Core.MiniJson.String(text);
    }

    /// <summary>A Pack Maker router with a fake backend, its own clock, limiter and audit sink.</summary>
    internal sealed class MakerRig
    {
        internal MakerRig(
            int port = ApiTestKit.Port,
            bool backends = true,
            string? webRoot = null,
            TimeSpan? timeout = null,
            TimeSpan? grace = null,
            ApiRateLimiter? limiter = null,
            int maxWaits = ApiDefaults.MaxConcurrentWaits,
            Func<MakerStudioBackends?>? studio = null, IMakerEditor? editor = null)
        {
            Clock = new ManualApiClock();
            Token = ApiToken.Create();
            Backend = new FakeMakerBackend();
            Audit = new ApiAuditLog(Clock, () => false, (record, channel, quiet) =>
            {
                lock (Records)
                {
                    Records.Add((record, channel));
                }
            });
            Router = new MakerRouter(Token, new MakerRouterOptions
            {
                Port = port,
                Clock = Clock,
                WebRoot = webRoot,
                Catalog = () => backends ? Backend : null,
                Editor = () => backends ? editor ?? Backend : null,
                Studio = studio ?? (() => null),
                Audit = Audit,
                Limiter = limiter ?? new ApiRateLimiter(Clock, 1000, 1000, 1000),
                BackendTimeout = timeout ?? TimeSpan.FromSeconds(2),
                BackendGrace = grace ?? TimeSpan.FromMilliseconds(200),
                MaxConcurrentWaits = maxWaits,
                Diagnostics = message => { lock (Diagnostics) { Diagnostics.Add(message); } },
            });
        }

        internal ManualApiClock Clock { get; }

        internal ApiToken Token { get; }

        internal FakeMakerBackend Backend { get; }

        internal ApiAuditLog Audit { get; }

        internal MakerRouter Router { get; }

        internal List<(ApiAuditRecord Record, ApiAuditChannel Channel)> Records { get; } = new List<(ApiAuditRecord, ApiAuditChannel)>();

        internal List<string> Diagnostics { get; } = new List<string>();

        internal ApiResponse Send(ApiRequest request) => Router.Handle(request);
    }

    /// <summary>Request builders for the maker routes (Host 127.0.0.1:7870 unless given).</summary>
    internal static class MakerHttp
    {
        internal static string OpenPageCookie(MakerRouter router)
        {
            var target = new Uri(router.CreateOpenUrl()).PathAndQuery;
            var reply = router.Handle(Get(target, null, ("Host", ApiDefaults.HostAuthority(router.Port))));
            Assert.Equal(302, reply.Status);
            return ApiTestKit.Header(reply, "Set-Cookie")!.Split(';')[0];
        }

        internal static ApiRequest Get(string target, ApiToken? token, params (string Name, string Value)[] extra) =>
            Build("GET", target, token, null, null, extra);

        internal static ApiRequest Json(string method, string target, ApiToken? token, string body, params (string Name, string Value)[] extra) =>
            Build(method, target, token, Encoding.UTF8.GetBytes(body), "application/json; charset=utf-8", extra);

        internal static ApiRequest Build(string method, string target, ApiToken? token, byte[]? body, string? contentType, params (string Name, string Value)[] extra) =>
            Build(method, target, token, body, contentType, true, extra);

        internal static ApiRequest Build(
            string method, string target, ApiToken? token, byte[]? body, string? contentType, bool declareLength, params (string Name, string Value)[] extra)
        {
            var headers = new ApiHeaders();
            if (!extra.Any(e => string.Equals(e.Name, "Host", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add("Host", ApiTestKit.Authority());
            }

            if (token != null)
            {
                headers.Add("Authorization", ApiTestKit.AuthValue(token));
            }

            if (contentType != null)
            {
                headers.Add("Content-Type", contentType);
            }

            foreach (var (name, value) in extra)
            {
                headers.Add(name, value);
            }

            return new ApiRequest(
                "test",
                method,
                target,
                headers,
                body is null || !declareLength ? (long?)null : body.Length,
                headers.Has("Transfer-Encoding"),
                body is null ? null : max => body.Length > max ? ApiBody.TooLarge() : ApiBody.Of(body));
        }

        internal static ApiHeaders Headers(ApiToken token, string contentType)
        {
            var headers = new ApiHeaders();
            headers.Add("Host", ApiTestKit.Authority());
            headers.Add("Authorization", ApiTestKit.AuthValue(token));
            headers.Add("Content-Type", contentType);
            return headers;
        }

        /// <summary>The body of a <see cref="MakerRouterTests.EveryBackendRoute"/> row: "WAV" is a short tone.</summary>
        internal static byte[]? BodyOf(string? body) =>
            body is null ? null : body == "WAV" ? TestWav.Tone(0.5, 16000, 1) : Encoding.UTF8.GetBytes(body);
    }

    /// <summary>Invented WAV files: tones, silence and hand-made headers. No game audio.</summary>
    internal static class TestWav
    {
        internal static byte[] Tone(double seconds, int rate, int channels)
        {
            var frames = (int)Math.Round(seconds * rate);
            var samples = new float[frames * channels];
            for (var i = 0; i < frames; i++)
            {
                var v = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * i / rate));
                for (var c = 0; c < channels; c++)
                {
                    samples[i * channels + c] = v;
                }
            }

            return WavWriter.Write(samples, channels, rate);
        }

        internal static byte[] Silence(double seconds, int rate, int channels) =>
            WavWriter.Write(new float[(int)Math.Round(seconds * rate) * channels], channels, rate);

        /// <summary>A RIFF/WAVE file with the given format fields and a zero-filled data chunk.</summary>
        internal static byte[] Header(int formatTag, int channels, int rate, int bits, int dataBytes)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(4 + 8 + 16 + 8 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)formatTag);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * bits / 8);
                w.Write((short)(channels * bits / 8));
                w.Write((short)bits);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                w.Write(new byte[dataBytes]);
                return ms.ToArray();
            }
        }

        /// <summary>A valid one-second 8 kHz mono WAV padded with a <c>junk</c> chunk to exactly <paramref name="total"/> bytes (even).</summary>
        internal static byte[] PaddedTo(int total)
        {
            const int dataBytes = 16000;
            var junk = total - 12 - (8 + 16) - 8 - (8 + dataBytes);
            using (var ms = new MemoryStream(total))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(total - 8);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(8000);
                w.Write(16000);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("junk"));
                w.Write(junk);
                w.Write(new byte[junk]);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                w.Write(new byte[dataBytes]);
                return ms.ToArray();
            }
        }
    }

    /// <summary>A fake catalog and editor: records each call with its arguments and answers with invented sample data.</summary>
    internal sealed class FakeMakerBackend : IMakerCatalog, IMakerEditor
    {
        private readonly object _gate = new object();
        private readonly List<string> _calls = new List<string>();
        private CancellationToken _lastToken;

        internal string? FailCode { get; set; }

        internal string FailMessage { get; set; } = "fake failure";

        internal bool ThrowAtOnce { get; set; }

        internal bool Fault { get; set; }

        internal bool NullResult { get; set; }

        internal bool NullValue { get; set; }

        /// <summary>Never answers until cancelled, then cancels.</summary>
        internal bool Hang { get; set; }

        /// <summary>Never answers at all.</summary>
        internal bool HangForever { get; set; }

        internal byte[]? LastWav { get; private set; }

        internal CancellationToken LastToken
        {
            get { lock (_gate) { return _lastToken; } }
        }

        internal IReadOnlyList<string> Calls
        {
            get { lock (_gate) { return _calls.ToArray(); } }
        }

        public Task<MakerResult<MakerStatus>> StatusAsync(CancellationToken cancel) =>
            Answer("status", new MakerStatus { GameVersion = "1.18.0", LoaderVersion = "0.9.2", GameLanguage = "th", GameLanguageIsPack = true, RoomReady = true }, cancel);

        public Task<MakerResult<IReadOnlyList<MakerPack>>> PacksAsync(CancellationToken cancel) =>
            Answer<IReadOnlyList<MakerPack>>(
                "packs",
                new[]
                {
                    new MakerPack { Id = "thai-dub", Name = "Thai dub", Version = "0.1.0", Languages = new[] { "th" }, Editable = true },
                    new MakerPack { Id = "code-mod", Name = "Code mod", Version = "1.0.0", Editable = false, ReadOnlyReason = "a code mod" },
                },
                cancel);

        public Task<MakerResult<MakerLinePage>> LinesAsync(MakerLineQuery query, CancellationToken cancel) =>
            Answer(
                "lines " + query.Lang + " [" + query.Search + "] " + query.Filter + " " + query.Offset + " " + query.Limit,
                new MakerLinePage { Total = 2, Offset = query.Offset, Items = new[] { Line("sample_001"), Line("sample_002") } },
                cancel);

        public Task<MakerResult<MakerLine>> LineAsync(string id, string lang, CancellationToken cancel) =>
            Answer("line " + id + " " + lang, Line(id), cancel);

        public Task<MakerResult<MakerSaved>> SaveSubtitleAsync(string packId, string lang, string id, string? text, CancellationToken cancel) =>
            Answer("subtitle " + packId + " " + lang + " " + id + " " + (text is null ? "<null>" : "[" + text + "]"),
                new MakerSaved { Live = true, Path = "data/subtitles/" + lang + ".json", Note = "Saved." },
                cancel);

        public Task<MakerResult<MakerSaved>> SaveVoiceAsync(string packId, string lang, string clip, byte[]? wav, CancellationToken cancel)
        {
            LastWav = wav;
            return Answer("voice " + packId + " " + lang + " " + clip + " " + (wav is null ? "<null>" : wav.Length.ToString()),
                new MakerSaved { Live = true, Path = "audio/voice/" + lang + "/" + clip + ".wav", DurationSec = 1.5, Note = "Saved." },
                cancel);
        }

        public Task<MakerResult<MakerSaved>> PlayAsync(string id, string variant, string lang, CancellationToken cancel) =>
            Answer("play " + id + " " + variant + " " + lang, new MakerSaved { Live = true, Note = "Playing." }, cancel);

        private static MakerLine Line(string id) => new MakerLine
        {
            Id = id,
            Voice = "voice_" + id,
            Group = "sampleChapter",
            DurationSec = 2.5,
            Ja = "サンプルの台詞",
            En = "An invented sample line",
            Subtitle = "บรรทัดตัวอย่าง",
            SubtitlePack = "thai-dub",
        };

        private Task<MakerResult<T>> Answer<T>(string call, T value, CancellationToken cancel) where T : class
        {
            lock (_gate)
            {
                _calls.Add(call);
                _lastToken = cancel;
            }

            if (ThrowAtOnce)
            {
                throw new InvalidOperationException("the fake backend threw");
            }

            if (Fault)
            {
                return Task.FromException<MakerResult<T>>(new InvalidOperationException("the fake backend faulted"));
            }

            if (Hang)
            {
                var pending = new TaskCompletionSource<MakerResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancel.Register(() => pending.TrySetCanceled());
                return pending.Task;
            }

            if (HangForever)
            {
                return new TaskCompletionSource<MakerResult<T>>().Task;
            }

            if (NullResult)
            {
                return Task.FromResult<MakerResult<T>>(null!);
            }

            if (NullValue)
            {
                return Task.FromResult(MakerResult<T>.Success(null!));
            }

            return Task.FromResult(FailCode != null ? MakerResult<T>.Fail(FailCode, FailMessage) : MakerResult<T>.Success(value));
        }
    }
}
