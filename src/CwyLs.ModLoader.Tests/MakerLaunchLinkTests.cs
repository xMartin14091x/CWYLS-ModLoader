using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>D-145 / F12: launch links are single-use, bounded credentials; a bare page never reveals the write token.</summary>
    public sealed class MakerLaunchLinkTests : IClassFixture<MakerWebFolder>
    {
        private readonly MakerWebFolder _web;

        public MakerLaunchLinkTests(MakerWebFolder web) => _web = web;

        private MakerRig Rig() => new MakerRig(webRoot: _web.Root);
        private static string Target(MakerRouter router) => new Uri(router.CreateOpenUrl()).PathAndQuery;
        private static string Html(ApiResponse response) => Encoding.UTF8.GetString(response.BodyBytes());
        private static string Cookie(ApiResponse response) => ApiTestKit.Header(response, "Set-Cookie")!.Split(';')[0];

        private static void Locked(ApiResponse response, MakerRig rig)
        {
            Assert.Equal(200, response.Status);
            Assert.DoesNotContain("cwyls-maker-token", Html(response));
            Assert.DoesNotContain(rig.Token.Reveal(), Html(response));
            Assert.Contains("Open Pack Maker from the game", Html(response));
            Assert.Contains("เปิด Pack Maker จากในเกม", Html(response));
            Assert.Null(ApiTestKit.Header(response, "Set-Cookie"));
            Assert.Equal("no-referrer", ApiTestKit.Header(response, "Referrer-Policy"));
            Assert.Equal("DENY", ApiTestKit.Header(response, "X-Frame-Options"));
        }

        [Theory]
        [InlineData("/maker/")]
        [InlineData("/maker/index.html")]
        [InlineData("/maker/INDEX.HTML")]
        [InlineData("/maker/?v=2")]
        [InlineData("/maker/?k=")]
        [InlineData("/maker/?k=wrong")]
        public void Bare_or_wrong_links_cannot_read_the_token_even_with_an_api_bearer(string target)
        {
            var rig = Rig();
            Locked(rig.Send(MakerHttp.Get(target, rig.Token)), rig);
        }

        [Fact]
        public void Launch_redirects_to_a_clean_url_with_a_separate_hardened_session_cookie_and_reload_works()
        {
            var rig = Rig();
            var target = Target(rig.Router);
            var launch = rig.Send(MakerHttp.Get(target, null));
            Assert.Equal(302, launch.Status);
            Assert.Equal("/maker/", ApiTestKit.Header(launch, "Location"));
            Assert.DoesNotContain(rig.Token.Reveal(), launch.Body);
            var setCookie = ApiTestKit.Header(launch, "Set-Cookie")!;
            Assert.Contains("; Path=/maker/; HttpOnly; SameSite=Strict", setCookie);
            Assert.DoesNotContain("Domain=", setCookie);
            Assert.DoesNotContain("Expires=", setCookie);
            Assert.DoesNotContain("Max-Age=", setCookie);
            Assert.DoesNotContain(rig.Token.Reveal(), setCookie);
            Assert.DoesNotContain(target.Split('=')[1], setCookie);
            var cookie = Cookie(launch);
            foreach (var path in new[] { "/maker/", "/maker/index.html", "/maker/?v=2", "/maker/" })
            {
                var page = rig.Send(MakerHttp.Get(path, null, ("Cookie", cookie)));
                Assert.Equal(200, page.Status);
                Assert.Contains(rig.Token.Reveal(), Html(page));
                Assert.Null(ApiTestKit.Header(page, "Set-Cookie"));
            }

            // The cookie authenticates the page, never the API: CSRF protection still needs the bearer.
            Assert.Equal(401, rig.Send(MakerHttp.Json("POST", "/v1/maker/play", null,
                "{\"id\":\"review_line\",\"variant\":\"original\",\"lang\":\"th\"}", ("Cookie", cookie))).Status);
            Assert.Empty(rig.Backend.Calls);
            Locked(rig.Send(MakerHttp.Get("/maker/", null)), rig);
        }

        [Fact]
        public void A_consumed_link_fails_with_and_without_its_cookie_and_cannot_be_replayed_by_another_client()
        {
            var rig = Rig();
            var target = Target(rig.Router);
            var launch = rig.Send(MakerHttp.Get(target, null));
            Assert.Equal(302, launch.Status);
            Locked(rig.Send(MakerHttp.Get(target, null)), rig);
            Locked(rig.Send(MakerHttp.Get(target, null, ("Cookie", Cookie(launch)))), rig);
        }

        [Theory]
        [InlineData(59.999, 302)]
        [InlineData(60, 200)]
        [InlineData(61, 200)]
        public void Nonces_expire_at_sixty_monotonic_seconds(double elapsed, int status)
        {
            var rig = Rig();
            var target = Target(rig.Router);
            rig.Clock.Advance(elapsed);
            // Moving the wall clock back does not extend a link's lifetime.
            rig.Clock.Now = rig.Clock.Now.AddDays(-1);
            var reply = rig.Send(MakerHttp.Get(target, null));
            Assert.Equal(status, reply.Status);
            if (status == 200) Locked(reply, rig);
        }

        [Fact]
        public void Wrong_nonce_does_not_consume_a_valid_link()
        {
            var rig = Rig();
            var target = Target(rig.Router);
            var last = target[target.Length - 1];
            var wrong = target.Substring(0, target.Length - 1) + (last == '0' ? '1' : '0');
            Locked(rig.Send(MakerHttp.Get(wrong, null)), rig);
            Assert.Equal(302, rig.Send(MakerHttp.Get(target, null)).Status);
        }

        [Fact]
        public async Task Concurrent_redemption_has_exactly_one_winner()
        {
            var rig = Rig();
            var target = Target(rig.Router);
            var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
                Task.Run(() => rig.Send(MakerHttp.Get(target, null)))));
            Assert.Single(replies, r => r.Status == 302);
            foreach (var reply in replies.Where(r => r.Status != 302)) Locked(reply, rig);
        }

        [Fact]
        public void Second_open_gets_a_fresh_link_and_keeps_the_existing_tab_working()
        {
            var rig = Rig();
            var first = Target(rig.Router);
            var cookie = Cookie(rig.Send(MakerHttp.Get(first, null)));
            rig.Clock.Advance(120);
            var second = Target(rig.Router);
            Assert.NotEqual(first, second);
            Assert.Equal(302, rig.Send(MakerHttp.Get(second, null)).Status);
            Assert.Contains(rig.Token.Reveal(), Html(rig.Send(MakerHttp.Get("/maker/", null, ("Cookie", cookie)))));
            Locked(rig.Send(MakerHttp.Get(first, null)), rig);
        }

        [Fact]
        public void Pending_links_are_bounded_without_invalidating_other_recent_opens()
        {
            var rig = Rig();
            var links = Enumerable.Range(0, 17).Select(_ => Target(rig.Router)).ToArray();
            Locked(rig.Send(MakerHttp.Get(links[0], null)), rig);
            Assert.Equal(302, rig.Send(MakerHttp.Get(links[1], null)).Status);
            Assert.Equal(302, rig.Send(MakerHttp.Get(links[16], null)).Status);
        }

        [Fact]
        public void Browser_sessions_do_not_survive_a_restart_or_authenticate_another_router()
        {
            var rig = Rig();
            var cookie = MakerHttp.OpenPageCookie(rig.Router);
            var other = Rig();
            Locked(other.Send(MakerHttp.Get("/maker/", null, ("Cookie", cookie))), other);
            rig.Router.BeginStop();
            Assert.Equal(503, rig.Send(MakerHttp.Get("/maker/", null, ("Cookie", cookie))).Status);
            Assert.Throws<InvalidOperationException>(() => rig.Router.CreateOpenUrl());
        }

        [Fact]
        public void Wrong_duplicate_or_comma_joined_cookies_cannot_authorize_the_page()
        {
            var rig = Rig();
            var cookie = MakerHttp.OpenPageCookie(rig.Router);
            foreach (var value in new[] { "cwyls-maker-7870=wrong", cookie + "; " + cookie, cookie + "," + cookie })
            {
                Locked(rig.Send(MakerHttp.Get("/maker/", null, ("Cookie", value))), rig);
            }
        }

        [Theory]
        [InlineData("Host", "evil.example:7870", 421)]
        [InlineData("Origin", "http://127.0.0.1:7871", 403)]
        [InlineData("Sec-Fetch-Site", "cross-site", 403)]
        [InlineData("Sec-Fetch-Site", "same-site", 403)]
        public void Existing_browser_guards_run_before_redemption(string header, string value, int status)
        {
            var rig = Rig();
            var target = Target(rig.Router);
            Assert.Equal(status, rig.Send(MakerHttp.Get(target, null, (header, value))).Status);
            Assert.Equal(302, rig.Send(MakerHttp.Get(target, null)).Status);
        }

        [Fact]
        public void Bare_maker_preserves_the_nonce_when_adding_the_slash()
        {
            var rig = Rig();
            var target = Target(rig.Router);
            var redirect = rig.Send(MakerHttp.Get(target.Replace("/maker/", "/maker"), null));
            Assert.Equal(302, redirect.Status);
            Assert.Equal(target, ApiTestKit.Header(redirect, "Location"));
            Assert.Equal(302, rig.Send(MakerHttp.Get(ApiTestKit.Header(redirect, "Location")!, null)).Status);
        }
    }

    [Collection("TraceBus")]
    public sealed class MakerLaunchTraceTests
    {
        [Fact]
        public void Launch_credentials_are_absent_from_request_traces_audits_and_diagnostics()
        {
            using var web = new MakerWebFolder();
            var rig = new MakerRig(webRoot: web.Root);
            var target = new Uri(rig.Router.CreateOpenUrl()).PathAndQuery;
            var nonce = target.Split('=')[1];
            var traces = new System.Collections.Generic.List<string>();
            var was = MakerRequestTrace.Enabled;
            string? cookie = null;
            TraceBus.Reset();
            try
            {
                using var subscription = TraceBus.Subscribe(evt =>
                {
                    if (ConsoleEventMap.TryMap(evt, out var mapped)) traces.Add(ConsoleRecordFormatter.Format(mapped!));
                });
                MakerRequestTrace.Enabled = true;
                var launch = rig.Send(MakerHttp.Get(target, null));
                Assert.Equal(302, launch.Status);
                cookie = ApiTestKit.Header(launch, "Set-Cookie")!.Split(';')[0];
                Assert.Equal(200, rig.Send(MakerHttp.Get("/maker/", null, ("Cookie", cookie))).Status);
                Assert.Equal(200, rig.Send(MakerHttp.Get(target, null)).Status); // failed replay is redacted too
            }
            finally
            {
                MakerRequestTrace.Enabled = was;
                TraceBus.Reset();
            }

            Assert.Equal(3, traces.Count(line => line.Contains("MAKER-TRACE")));
            var logs = string.Join("\n", traces) + string.Join("\n", rig.Diagnostics) +
                string.Join("\n", rig.Records.Select(r => r.Record.Path + r.Record.Primary + r.Record.Client + r.Record.Error));
            Assert.DoesNotContain(nonce, logs);
            Assert.DoesNotContain(rig.Token.Reveal(), logs);
            Assert.DoesNotContain(cookie!.Split('=')[1], logs);
            Assert.DoesNotContain("?k=", logs);
        }
    }
}
