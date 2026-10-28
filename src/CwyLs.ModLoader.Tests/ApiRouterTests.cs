using System;
using System.Linq;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>S-API: the request pipeline's checks in order (browser, Host, key), routes, queries, limits and the audit record.</summary>
    public class ApiRouterTests
    {
        [Fact]
        public void A_good_request_reads_info()
        {
            var rig = new RouterRig();
            var response = rig.Send(ApiTestKit.Get("/v1/info", rig.Key));

            Assert.Equal(200, response.Status);
            var info = ApiTestKit.Json(response);
            Assert.Equal(1L, ((JsonObject)info["api"]!)["version"]);
            Assert.Equal("http://127.0.0.1:7870/", ((JsonObject)info["api"]!)["endpoint"]);
            Assert.Equal("0.9.0", ((JsonObject)info["loader"]!)["version"]);
            Assert.Equal("0.9.0", ((JsonObject)info["loader"]!)["modApi"]);
            Assert.False(info.ContainsKey("patcher"));
            Assert.Equal("1.17.3", ((JsonObject)info["game"]!)["version"]);
            Assert.DoesNotContain(rig.Key.Reveal(), response.Body);
        }

        [Theory]
        [InlineData("Origin", "http://evil.example")]
        [InlineData("Origin", "null")]
        [InlineData("Origin", "")]
        [InlineData("Sec-Fetch-Site", "cross-site")]
        [InlineData("Sec-Fetch-Mode", "no-cors")]
        public void Any_browser_marker_is_refused_even_with_the_right_key(string header, string value)
        {
            var rig = new RouterRig();
            var response = rig.Send(ApiTestKit.Get("/v1/info", rig.Key, ApiTestKit.Port, (header, value)));

            Assert.Equal(403, response.Status);
            Assert.Equal(ApiErrorCodes.BrowserRefused, ApiTestKit.ErrorCode(response));
        }

        [Theory]
        [InlineData("localhost:7870")]
        [InlineData("127.0.0.1:7871")]
        [InlineData("127.0.0.1")]
        [InlineData("attacker.example")]
        [InlineData("[::1]:7870")]
        [InlineData("")]
        public void Host_must_be_the_literal_loopback_authority(string host)
        {
            var rig = new RouterRig();
            var response = rig.Send(ApiTestKit.Get("/v1/info", rig.Key, ApiTestKit.Port, ("Host", host)));

            Assert.Equal(421, response.Status);
            Assert.Equal(ApiErrorCodes.HostRefused, ApiTestKit.ErrorCode(response));
        }

        [Fact]
        public void A_doubled_host_header_is_refused()
        {
            var rig = new RouterRig();
            var request = ApiTestKit.Get("/v1/info", rig.Key);
            request.Headers.Add("Host", "127.0.0.1:7870");

            Assert.Equal(421, rig.Send(request).Status);
        }

        [Fact]
        public void A_missing_host_header_is_refused()
        {
            var rig = new RouterRig();
            var headers = new ApiHeaders();
            headers.Add("Authorization", ApiTestKit.AuthValue(rig.Key));
            var request = new ApiRequest("test", "GET", "/v1/info", headers, null, false, null);

            Assert.Equal(421, rig.Send(request).Status);
        }

        [Fact]
        public void No_key_is_401_with_a_challenge()
        {
            var rig = new RouterRig();
            var response = rig.Send(ApiTestKit.Get("/v1/info", null));

            Assert.Equal(401, response.Status);
            Assert.Equal(ApiErrorCodes.Unauthorized, ApiTestKit.ErrorCode(response));
            Assert.StartsWith(ApiToken.Scheme, ApiTestKit.Header(response, "WWW-Authenticate"));
        }

        [Fact]
        public void A_wrong_or_doubled_key_is_401()
        {
            var rig = new RouterRig();
            var other = ApiToken.Create();

            Assert.Equal(401, rig.Send(ApiTestKit.Get("/v1/info", other)).Status);

            var doubled = ApiTestKit.Get("/v1/info", rig.Key);
            doubled.Headers.Add("Authorization", ApiTestKit.AuthValue(rig.Key));
            Assert.Equal(401, rig.Send(doubled).Status);
        }

        [Fact]
        public void A_rotated_key_replaces_the_old_one_at_once()
        {
            var rig = new RouterRig();
            var fresh = ApiToken.Create();
            rig.Router.UseKey(fresh);

            Assert.Equal(401, rig.Send(ApiTestKit.Get("/v1/info", rig.Key)).Status);
            Assert.Equal(200, rig.Send(ApiTestKit.Get("/v1/info", fresh)).Status);
            Assert.Equal(fresh.Id, rig.Router.KeyId);
        }

        [Fact]
        public void The_browser_check_comes_before_the_key_check()
        {
            var rig = new RouterRig();

            Assert.Equal(403, rig.Send(ApiTestKit.Get("/v1/info", null, ApiTestKit.Port, ("Origin", "http://x"))).Status);
        }

        [Theory]
        [InlineData("GET", "/v1/nope", 404)]
        [InlineData("GET", "/v2/info", 404)]
        [InlineData("GET", "/", 404)]
        [InlineData("POST", "/v1/info", 405)]
        [InlineData("DELETE", "/v1/state", 405)]
        [InlineData("GET", "/v1/actions/music.play", 405)]
        [InlineData("POST", "/v1/actions/act-0123456789abcdef01234567", 405)]
        [InlineData("GET", "/v1/actions/NOT A NAME", 404)]
        [InlineData("GET", "/v1/info/", 404)]
        public void Routes_answer_404_or_405(string method, string target, int status)
        {
            var rig = new RouterRig();
            var response = rig.Send(ApiTestKit.Build(method, target, rig.Key, null, ApiTestKit.Port));

            Assert.Equal(status, response.Status);
            if (status == 405)
            {
                Assert.NotNull(ApiTestKit.Header(response, "Allow"));
            }
        }

        [Theory]
        [InlineData("/v1/info?x=1")]
        [InlineData("/v1/state?verbose=true")]
        [InlineData("/v1/events?after=1&after=2")]
        [InlineData("/v1/events?after=-1")]
        [InlineData("/v1/events?wait=26")]
        [InlineData("/v1/events?wait=abc")]
        [InlineData("/v1/events?limit=0")]
        [InlineData("/v1/events?limit=501")]
        [InlineData("/v1/events?since=3")]
        [InlineData("/v1/%69nfo")]
        [InlineData("/v1/../v1/info")]
        [InlineData("//v1/info")]
        [InlineData("http://127.0.0.1:7870/v1/info")]
        public void Bad_targets_and_queries_are_400_or_404(string target)
        {
            var rig = new RouterRig();
            var status = rig.Send(ApiTestKit.Get(target, rig.Key)).Status;

            Assert.True(status == 400 || status == 404, target + " answered " + status);
        }

        [Fact]
        public void A_get_with_a_body_is_refused()
        {
            var rig = new RouterRig();
            var request = ApiTestKit.Build("GET", "/v1/state", rig.Key, "{}", ApiTestKit.Port);

            Assert.Equal(400, rig.Send(request).Status);
        }

        [Fact]
        public void Rate_limits_answer_429_with_retry_after_and_count_only_authenticated_requests()
        {
            var clock = new ManualApiClock();
            var rig = new RouterRig(limiter: new ApiRateLimiter(clock, 1, 1, 1));

            for (var i = 0; i < 10; i++)
            {
                Assert.Equal(401, rig.Send(ApiTestKit.Get("/v1/state", null)).Status);
            }

            Assert.Equal(200, rig.Send(ApiTestKit.Get("/v1/state", rig.Key)).Status);
            Assert.Equal(200, rig.Send(ApiTestKit.Get("/v1/state", rig.Key)).Status);
            var limited = rig.Send(ApiTestKit.Get("/v1/state", rig.Key));
            Assert.Equal(429, limited.Status);
            Assert.Equal("1", ApiTestKit.Header(limited, "Retry-After"));

            clock.Advance(1.5);
            Assert.Equal(200, rig.Send(ApiTestKit.Get("/v1/state", rig.Key)).Status);
        }

        [Fact]
        public void After_stop_every_request_is_503()
        {
            var rig = new RouterRig();
            rig.Router.BeginStop();

            Assert.Equal(503, rig.Send(ApiTestKit.Get("/v1/info", rig.Key)).Status);
            Assert.True(rig.Router.Events.IsClosed);
        }

        [Fact]
        public void Every_request_writes_exactly_one_audit_record_without_the_key()
        {
            var rig = new RouterRig();
            rig.Send(ApiTestKit.Get("/v1/info", rig.Key, ApiTestKit.Port, ("X-CWY-Client", "cwy-mcp")));
            rig.Send(ApiTestKit.Get("/v1/info", null));
            rig.Send(ApiTestKit.Get("/v1/nope?q=hidden", rig.Key));

            Assert.Equal(3, rig.Records.Count);
            Assert.Equal("cwy-mcp", rig.Records[0].Record.Client);
            Assert.True(rig.Records[0].Record.Authenticated);
            Assert.Equal(ApiAuditChannel.File, rig.Records[0].Channel);
            Assert.False(rig.Records[1].Record.Authenticated);
            Assert.Equal(ApiAuditChannel.Console, rig.Records[1].Channel);
            Assert.Equal("/v1/nope", rig.Records[2].Record.Path);
            foreach (var (record, _) in rig.Records)
            {
                var all = string.Join("|", record.Method, record.Path, record.Client, record.Transport, record.Error, record.Primary);
                Assert.DoesNotContain(rig.Key.Reveal(), all);
                Assert.DoesNotContain("hidden", all);
            }
        }

        [Fact]
        public void Control_characters_never_reach_the_audit_line()
        {
            var rig = new RouterRig();
            rig.Send(ApiTestKit.Get("/v1/no\u0001pe\u007f", rig.Key));
            rig.Send(ApiTestKit.Get("/v1/x\u001b[31m", null));

            Assert.Equal("/v1/no?pe?", rig.Records[0].Record.Path);
            Assert.Equal("/v1/x?[31m", rig.Records[1].Record.Path);
        }

        [Theory]
        [InlineData(null, "api")]
        [InlineData("", "api")]
        [InlineData("cwy-mcp", "cwy-mcp")]
        [InlineData("satone.ai_2", "satone.ai_2")]
        [InlineData("has space", "api")]
        [InlineData("x\r\ny", "api")]
        [InlineData("abcdefghijklmnopqrstuvwxyz0123456789", "api")]
        public void Client_names_are_sanitised(string? header, string expected) =>
            Assert.Equal(expected, ApiRouter.ClientName(header));

        [Fact]
        public void State_is_served_from_the_published_document()
        {
            var rig = new RouterRig();
            var before = ApiTestKit.Json(rig.Send(ApiTestKit.Get("/v1/state", rig.Key)));
            Assert.Equal(0L, before["version"]);
            Assert.Equal(false, before["ready"]);

            rig.Router.State.Publish(new JsonObject { ["ready"] = true, ["live"] = new JsonObject { ["scene"] = "RoomScene" } }, 42);
            rig.Clock.Advance(0.25);
            var after = ApiTestKit.Json(rig.Send(ApiTestKit.Get("/v1/state", rig.Key)));

            Assert.Equal(1L, after["schema"]);
            Assert.Equal(1L, after["version"]);
            Assert.Equal(42L, after["frame"]);
            Assert.Equal(250L, after["ageMs"]);
            Assert.Equal(true, after["ready"]);
            Assert.Equal(new[] { "schema", "version", "capturedAt", "ageMs", "frame", "ready", "live" }, after.Keys.ToArray());
        }
    }
}
