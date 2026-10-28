using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>S-API building blocks: the access key, JSON writing, the HTTP head parser, targets, rate limits, audit routing and the owner-only key file.</summary>
    public class ApiPrimitivesTests
    {
        [Fact]
        public void Keys_are_256_bit_hex_unique_and_never_printed()
        {
            var a = ApiToken.Create();
            var b = ApiToken.Create();

            Assert.Equal(ApiToken.TextLength, a.Reveal().Length);
            Assert.All(a.Reveal(), c => Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
            Assert.NotEqual(a.Reveal(), b.Reveal());
            Assert.DoesNotContain(a.Reveal(), a.ToString());
            Assert.DoesNotContain(a.Reveal().Substring(0, 8), a.ToString());
            Assert.Equal(8, a.Id.Length);
            Assert.DoesNotContain(a.Id, a.Reveal());
        }

        [Fact]
        public void A_key_matches_only_its_own_scheme_and_value()
        {
            var key = ApiToken.Create();
            var value = key.Reveal();

            Assert.True(key.Matches(ApiToken.Scheme + " " + value));
            Assert.True(key.Matches(ApiToken.Scheme.ToLowerInvariant() + " " + value));
            Assert.False(key.Matches(null));
            Assert.False(key.Matches(""));
            Assert.False(key.Matches(value));
            Assert.False(key.Matches(ApiToken.Scheme + "  " + value));
            Assert.False(key.Matches(ApiToken.Scheme + " " + value + " "));
            Assert.False(key.Matches(ApiToken.Scheme + " " + value.Substring(1)));
            Assert.False(key.Matches(ApiToken.Scheme + " " + value.ToUpperInvariant()));
            Assert.False(key.Matches("Basic " + value));
        }

        [Fact]
        public void Keys_parse_back_from_the_file_form_only()
        {
            var key = ApiToken.Create();

            Assert.True(ApiToken.TryParse(key.Reveal(), out var parsed));
            Assert.True(parsed!.Matches(ApiToken.Scheme + " " + key.Reveal()));
            Assert.False(ApiToken.TryParse(key.Reveal().ToUpperInvariant(), out _));
            Assert.False(ApiToken.TryParse("abc", out _));
            Assert.False(ApiToken.TryParse(null, out _));
        }

        [Fact]
        public void Json_keeps_member_order_and_writes_big_ids_as_strings()
        {
            var json = ApiJson.Write(new JsonObject
            {
                ["z"] = 1,
                ["a"] = 638000000000000001UL,
                ["nan"] = double.NaN,
                ["kind"] = DayOfWeek.Monday,
                ["raw"] = new ApiRawJson("[1,2]"),
                ["list"] = new List<object?> { "x", null, true },
                ["text"] = "a\"b\n",
            });

            Assert.Equal("{\"z\":1,\"a\":\"638000000000000001\",\"nan\":null,\"kind\":\"monday\",\"raw\":[1,2],\"list\":[\"x\",null,true],\"text\":\"a\\\"b\\n\"}", json);
            Assert.Equal("{\"error\":{\"code\":\"c\",\"message\":\"m\"}}", ApiJson.Error("c", "m"));
            Assert.Equal("workStarted", ApiJson.Camel("WorkStarted"));
        }

        [Fact]
        public void The_head_parser_accepts_plain_http_1_1_and_joins_repeated_headers()
        {
            Assert.True(ApiHttpParser.TryParse("GET /v1/info HTTP/1.1\r\nHost: 127.0.0.1:7870\r\nX-A: 1\r\nx-a: 2", out var method, out var target, out var headers, out _));
            Assert.Equal("GET", method);
            Assert.Equal("/v1/info", target);
            Assert.Equal("127.0.0.1:7870", headers.Get("host"));
            Assert.Equal("1,2", headers.Get("X-A"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("GET /v1/info")]
        [InlineData("GET /v1/info HTTP/2")]
        [InlineData("get /v1/info HTTP/1.1")]
        [InlineData("GET  /v1/info HTTP/1.1")]
        [InlineData("GET /v1/info HTTP/1.1\r\nHost : x")]
        [InlineData("GET /v1/info HTTP/1.1\r\nHost: x\r\n folded")]
        [InlineData("GET /v1/info HTTP/1.1\r\nno colon")]
        public void The_head_parser_refuses_anything_unusual(string head) =>
            Assert.False(ApiHttpParser.TryParse(head, out _, out _, out _, out _));

        [Theory]
        [InlineData(null, true, null)]
        [InlineData("0", true, 0L)]
        [InlineData("1234", true, 1234L)]
        [InlineData("-1", false, null)]
        [InlineData("1,2", false, null)]
        [InlineData(" 5", false, null)]
        [InlineData("", false, null)]
        [InlineData("9999999999999999999", false, null)]
        public void Content_length_is_one_plain_number(string? value, bool ok, long? expected)
        {
            Assert.Equal(ok, ApiHttpParser.TryContentLength(value, out var length));
            Assert.Equal(expected, length);
        }

        [Fact]
        public void Targets_split_path_and_query_strictly()
        {
            Assert.True(ApiTarget.TryParse("/v1/events?after=12&wait=2.5&limit=", out var t, out _));
            Assert.Equal("/v1/events", t!.Path);
            Assert.Equal("12", t.Get("after"));
            Assert.Equal("2.5", t.Get("wait"));
            Assert.Equal(string.Empty, t.Get("limit"));
            Assert.Null(t.Get("missing"));

            Assert.False(ApiTarget.TryParse("/v1/a?x=1&x=2", out _, out var duplicate));
            Assert.Contains("twice", duplicate);
            Assert.False(ApiTarget.TryParse("/v1/info#frag", out _, out _));
            Assert.False(ApiTarget.TryParse("v1/info", out _, out _));
            Assert.False(ApiTarget.TryParse("/v1/%2e%2e/info", out _, out _));
            Assert.False(ApiTarget.TryParse("/" + new string('a', 2100), out _, out _));
        }

        [Fact]
        public void A_bucket_allows_its_burst_then_refills_at_its_rate()
        {
            var clock = new ManualApiClock();
            var bucket = new ApiTokenBucket(2, 3, clock);

            Assert.True(bucket.TryTake(out _));
            Assert.True(bucket.TryTake(out _));
            Assert.True(bucket.TryTake(out _));
            Assert.False(bucket.TryTake(out var retry));
            Assert.Equal(0.5, retry, 3);

            clock.Advance(0.5);
            Assert.True(bucket.TryTake(out _));
            Assert.False(bucket.TryTake(out _));

            clock.Advance(100);
            for (var i = 0; i < 3; i++)
            {
                Assert.True(bucket.TryTake(out _));
            }

            Assert.False(bucket.TryTake(out _));
        }

        [Theory]
        [InlineData("GET", 200, false, ApiAuditChannel.File)]
        [InlineData("GET", 200, true, ApiAuditChannel.Console)]
        [InlineData("GET", 401, false, ApiAuditChannel.Console)]
        [InlineData("GET", 404, false, ApiAuditChannel.Console)]
        [InlineData("POST", 202, false, ApiAuditChannel.Console)]
        [InlineData("POST", 403, false, ApiAuditChannel.Console)]
        public void Audit_routing_keeps_polling_out_of_the_console(string method, int status, bool consoleReads, ApiAuditChannel expected) =>
            Assert.Equal(expected, ApiAuditLog.Route(new ApiAuditRecord { Method = method, Status = status }, consoleReads));

        [Fact]
        public void A_burst_of_one_rejection_is_limited_on_the_console_and_counted()
        {
            var clock = new ManualApiClock();
            var seen = new List<(ApiAuditChannel Channel, int Quiet)>();
            var log = new ApiAuditLog(clock, () => false, (r, c, q) => seen.Add((c, q)));

            for (var i = 0; i < 12; i++)
            {
                log.Write(new ApiAuditRecord { Method = "GET", Path = "/v1/info", Status = 401 });
            }

            Assert.Equal(ApiAuditLog.BurstPerWindow, seen.Count(s => s.Channel == ApiAuditChannel.Console));
            Assert.Equal(12 - ApiAuditLog.BurstPerWindow, seen.Count(s => s.Channel == ApiAuditChannel.File));
            Assert.Equal(12, log.Written);

            clock.Advance(ApiAuditLog.WindowSeconds + 1);
            log.Write(new ApiAuditRecord { Method = "GET", Path = "/v1/info", Status = 401 });
            Assert.Equal((ApiAuditChannel.Console, 12 - ApiAuditLog.BurstPerWindow), seen.Last());

            log.Write(new ApiAuditRecord { Method = "GET", Path = "/v1/info", Status = 403 });
            Assert.Equal(ApiAuditChannel.Console, seen.Last().Channel);
        }

        [Fact]
        public void The_key_file_is_owner_only_and_carries_the_endpoint()
        {
            var folder = Path.Combine(Path.GetTempPath(), "cwy-api-test-" + Guid.NewGuid().ToString("N"), "api");
            var path = Path.Combine(folder, ApiDefaults.TokenFileName);
            var key = ApiToken.Create();
            try
            {
                var result = ApiKeyFile.Write(path, ApiKeyFile.Content(key, 7870, "tcp", DateTimeOffset.Now));

                Assert.True(result.Ok, result.Problem);
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
                }

                var json = JsonReader.ParseObject(File.ReadAllText(path));
                Assert.Equal(1L, json["schema"]);
                Assert.Equal("http://127.0.0.1:7870/", json["baseUrl"]);
                Assert.Equal(7870L, json["port"]);
                Assert.Equal("tcp", json["transport"]);
                Assert.Equal(key.Reveal(), json["token"]);
                Assert.True(json.ContainsKey("pid"));

                var next = ApiToken.Create();
                Assert.True(ApiKeyFile.Write(path, ApiKeyFile.Content(next, 7870, "tcp", DateTimeOffset.Now)).Ok);
                Assert.Equal(next.Reveal(), JsonReader.ParseObject(File.ReadAllText(path))["token"]);
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                }

                Assert.True(ApiKeyFile.Delete(path).Ok);
                Assert.False(File.Exists(path));
                Assert.True(ApiKeyFile.Delete(path).Ok);
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
            }
        }

        [Fact]
        public void The_defaults_are_the_documented_contract()
        {
            Assert.Equal("127.0.0.1", ApiDefaults.LoopbackAddress);
            Assert.Equal(7870, ApiDefaults.DefaultPort);
            Assert.Equal("http://127.0.0.1:7870/", ApiDefaults.BaseUrl(7870));
            Assert.Equal("127.0.0.1:7870", ApiDefaults.HostAuthority(7870));
            Assert.Equal(TimeSpan.FromSeconds(2), ApiDefaults.ActionMainThreadTimeout);
            Assert.Equal(4, ApiDefaults.FrameBudgetMs);
            Assert.False(ApiDefaults.IsValidPort(1023));
            Assert.True(ApiDefaults.IsValidPort(65535));
            Assert.False(ApiSwitches.AllowActions);
        }
    }
}
