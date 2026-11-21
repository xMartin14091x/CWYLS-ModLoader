using System;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// A throwaway web app folder in the temp directory (invented files), with a secret file beside it that the
    /// traversal tests try to reach. Deleted when the test class is done.
    /// </summary>
    public sealed class MakerWebFolder : IDisposable
    {
        internal const string IndexHtml = "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><title>Pack Maker</title></head><body><header>x</header></body></html>";

        public MakerWebFolder()
        {
            Parent = Path.Combine(Path.GetTempPath(), "cwy-maker-test-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(Parent, "maker");
            Directory.CreateDirectory(Path.Combine(Root, "js"));
            Directory.CreateDirectory(Path.Combine(Root, "css"));
            Directory.CreateDirectory(Path.Combine(Root, "sub"));
            Directory.CreateDirectory(Path.Combine(Parent, "outside"));
            File.WriteAllText(Path.Combine(Root, "index.html"), IndexHtml);
            File.WriteAllText(Path.Combine(Root, "js", "app.js"), "console.log('maker');");
            File.WriteAllText(Path.Combine(Root, "css", "style.css"), "body{margin:0}");
            File.WriteAllText(Path.Combine(Root, "logo.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
            File.WriteAllText(Path.Combine(Root, "sub", "index.html"), "<html><head></head></html>");
            File.WriteAllText(Path.Combine(Root, ".secret"), "dotfile");
            File.WriteAllText(Path.Combine(Root, "_draft.html"), "draft");
            File.WriteAllText(Path.Combine(Root, "notes.py"), "print('no')");
            File.WriteAllText(Path.Combine(Parent, "outside", "secret.txt"), "outside the web folder");
            File.WriteAllText(Path.Combine(Parent, "maker-sibling.txt"), "a sibling folder with the same prefix");
            Directory.CreateDirectory(Path.Combine(Parent, "maker2"));
            File.WriteAllText(Path.Combine(Parent, "maker2", "x.txt"), "prefix trick");
            try
            {
                File.CreateSymbolicLink(Path.Combine(Root, "link.txt"), Path.Combine(Parent, "outside", "secret.txt"));
                File.CreateSymbolicLink(Path.Combine(Root, "linkdir"), Path.Combine(Parent, "outside"));
                HasLinks = true;
            }
            catch (Exception)
            {
                HasLinks = false; // no symlink privilege (Windows without developer mode)
            }
        }

        public string Parent { get; }

        public string Root { get; }

        public bool HasLinks { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Parent, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder left behind is harmless.
            }
        }
    }

    /// <summary>DS-01: the web app under <c>/maker/</c>: traversal refused, types, the token meta in index.html, the redirect.</summary>
    public class MakerStaticFilesTests : IClassFixture<MakerWebFolder>
    {
        private readonly MakerWebFolder _web;

        public MakerStaticFilesTests(MakerWebFolder web) => _web = web;

        [Fact]
        public void The_index_carries_the_token_meta_right_after_head()
        {
            var rig = new MakerRig(webRoot: _web.Root);
            var cookie = MakerHttp.OpenPageCookie(rig.Router);
            foreach (var target in new[] { "/maker/", "/maker/index.html", "/maker/?v=2" })
            {
                var response = rig.Send(MakerHttp.Get(target, null, ("Cookie", cookie)));

                Assert.Equal(200, response.Status);
                Assert.Equal("text/html; charset=utf-8", response.MediaType);
                var html = Encoding.UTF8.GetString(response.BodyBytes());
                var meta = "<meta name=\"cwyls-maker-token\" content=\"" + rig.Token.Reveal() + "\">";
                Assert.Contains("<head>" + meta + "<meta charset", html);
                Assert.StartsWith("<!doctype html>", html);
                Assert.Equal(1, Count(html, "cwyls-maker-token"));
            }
        }

        [Fact]
        public void Each_router_injects_its_own_token_and_only_the_top_index_gets_one()
        {
            var a = new MakerRig(webRoot: _web.Root);
            var b = new MakerRig(webRoot: _web.Root);
            var pageA = Encoding.UTF8.GetString(a.Send(MakerHttp.Get("/maker/", null, ("Cookie", MakerHttp.OpenPageCookie(a.Router)))).BodyBytes());
            var pageB = Encoding.UTF8.GetString(b.Send(MakerHttp.Get("/maker/", null, ("Cookie", MakerHttp.OpenPageCookie(b.Router)))).BodyBytes());
            var nested = Encoding.UTF8.GetString(a.Send(MakerHttp.Get("/maker/sub/index.html", null)).BodyBytes());

            Assert.Contains(a.Token.Reveal(), pageA);
            Assert.DoesNotContain(b.Token.Reveal(), pageA);
            Assert.Contains(b.Token.Reveal(), pageB);
            Assert.DoesNotContain("cwyls-maker-token", nested);
            Assert.DoesNotContain(a.Token.Reveal(), File.ReadAllText(Path.Combine(_web.Root, "index.html")));
        }

        [Theory]
        [InlineData("/maker/js/app.js", "text/javascript; charset=utf-8")]
        [InlineData("/maker/css/style.css", "text/css; charset=utf-8")]
        [InlineData("/maker/logo.svg", "image/svg+xml")]
        public void Files_are_served_with_their_types_and_no_framing(string target, string type)
        {
            var rig = new MakerRig(webRoot: _web.Root);
            var response = rig.Send(MakerHttp.Get(target, null, ("Sec-Fetch-Site", "same-origin")));

            Assert.Equal(200, response.Status);
            Assert.Equal(type, response.MediaType);
            Assert.Equal("DENY", ApiTestKit.Header(response, "X-Frame-Options"));
            Assert.Equal("frame-ancestors 'none'", ApiTestKit.Header(response, "Content-Security-Policy"));
            Assert.Null(ApiTestKit.Header(response, "Access-Control-Allow-Origin"));
            Assert.Equal(File.ReadAllBytes(Path.Combine(_web.Root, target.Substring("/maker/".Length).Replace('/', Path.DirectorySeparatorChar))), response.BodyBytes());
        }

        [Fact]
        public void Bare_maker_redirects_to_the_folder()
        {
            var rig = new MakerRig(webRoot: _web.Root);
            var response = rig.Send(MakerHttp.Get("/maker", null));

            Assert.Equal(302, response.Status);
            Assert.Equal("/maker/", ApiTestKit.Header(response, "Location"));
            Assert.Equal("Found", ApiResponse.Reason(302));
        }

        [Theory]
        [InlineData("/maker/../outside/secret.txt")]
        [InlineData("/maker/js/../../outside/secret.txt")]
        [InlineData("/maker/..")]
        [InlineData("/maker/%2e%2e/outside/secret.txt")]
        [InlineData("/maker/%2E%2E%2Foutside%2Fsecret.txt")]
        [InlineData("/maker/..%2foutside%2fsecret.txt")]
        [InlineData("/maker/..\\outside\\secret.txt")]
        [InlineData("/maker/js\\..\\..\\outside\\secret.txt")]
        [InlineData("/maker//etc/passwd")]
        [InlineData("/maker/./index.html")]
        [InlineData("/maker/.secret")]
        [InlineData("/maker/js/.hidden.js")]
        [InlineData("/maker/_draft.html")]
        [InlineData("/maker/js/")]
        [InlineData("/maker/C:/Windows/win.ini")]
        [InlineData("/maker/~/secret.txt")]
        public void Traversal_and_hidden_paths_are_400(string target)
        {
            var rig = new MakerRig(webRoot: _web.Root);
            var response = rig.Send(MakerHttp.Get(target, null));

            Assert.Equal(400, response.Status);
            Assert.Equal(MakerErrorCodes.BadRequest, ApiTestKit.ErrorCode(response));
            Assert.DoesNotContain("outside the web folder", response.Body);
        }

        [Theory]
        [InlineData("/maker/nope.html")]
        [InlineData("/maker/notes.py")]
        [InlineData("/maker/js")]
        [InlineData("/maker/maker-sibling.txt")]
        [InlineData("/maker/index")]
        public void Missing_files_and_unserved_types_are_404(string target)
        {
            var rig = new MakerRig(webRoot: _web.Root);

            Assert.Equal(404, rig.Send(MakerHttp.Get(target, null)).Status);
        }

        [Fact]
        public void Links_out_of_the_folder_are_not_followed()
        {
            if (!_web.HasLinks)
            {
                // Only Windows without developer mode may refuse to create links; everywhere else this must run.
                Assert.True(OperatingSystem.IsWindows(), "symbolic links could not be created on this host");
                return;
            }

            var rig = new MakerRig(webRoot: _web.Root);

            Assert.Equal(404, rig.Send(MakerHttp.Get("/maker/link.txt", null)).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Get("/maker/linkdir/secret.txt", null)).Status);
        }

        [Fact]
        public void The_static_read_never_leaves_the_root_even_when_called_directly()
        {
            foreach (var relative in new[] { "../outside/secret.txt", "..", "js/../../outside/secret.txt", "/etc/passwd", "\\outside\\secret.txt", "a\0.txt" })
            {
                var status = MakerStaticFiles.Read(_web.Root, relative, null, out var bytes, out _, out _);

                Assert.Equal(MakerStaticStatus.BadPath, status);
                Assert.Empty(bytes);
            }
        }

        [Fact]
        public void Without_the_web_folder_the_page_is_503()
        {
            var missing = new MakerRig(webRoot: Path.Combine(_web.Parent, "not-there"));
            var none = new MakerRig();

            Assert.Equal(503, missing.Send(MakerHttp.Get("/maker/", null, ("Cookie", MakerHttp.OpenPageCookie(missing.Router)))).Status);
            var cookie = MakerHttp.OpenPageCookie(none.Router);
            Assert.Equal(503, none.Send(MakerHttp.Get("/maker/", null, ("Cookie", cookie))).Status);
            Assert.Equal(MakerErrorCodes.Unavailable, ApiTestKit.ErrorCode(none.Send(MakerHttp.Get("/maker/", null, ("Cookie", cookie)))));
        }

        [Fact]
        public void Only_get_without_a_body_is_served()
        {
            var rig = new MakerRig(webRoot: _web.Root);

            Assert.Equal(404, rig.Send(MakerHttp.Build("POST", "/maker/", null, null, null)).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Build("PUT", "/maker/index.html", null, Encoding.UTF8.GetBytes("x"), "text/html")).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Build("POST", "/maker", null, null, null)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("GET", "/maker/", null, "{}")).Status);
        }

        [Fact]
        public void A_static_page_needs_no_token_but_the_host_and_origin_rules_hold()
        {
            var rig = new MakerRig(webRoot: _web.Root);

            Assert.Equal(200, rig.Send(MakerHttp.Get("/maker/", null, ("Sec-Fetch-Site", "none"), ("Sec-Fetch-Mode", "navigate"))).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Get("/maker/", null, ("Host", "localhost:7870"))).Status);
            Assert.Equal(421, rig.Send(MakerHttp.Get("/maker/", null, ("Host", "rebound.example:7870"))).Status);
            Assert.Equal(403, rig.Send(MakerHttp.Get("/maker/", null, ("Sec-Fetch-Site", "cross-site"))).Status);
            Assert.Equal(403, rig.Send(MakerHttp.Get("/maker/js/app.js", null, ("Origin", "http://127.0.0.1:7871"))).Status);
        }

        [Theory]
        [InlineData("<html><head><title>t</title></head></html>", "<html><head>{M}<title>")]
        [InlineData("<html><HEAD lang=\"th\"><title>t</title></HEAD></html>", "<HEAD lang=\"th\">{M}<title>")]
        [InlineData("<html><header>x</header><head></head></html>", "<head>{M}</head>")]
        [InlineData("<!DOCTYPE html><title>no head</title>", "<!DOCTYPE html>{M}<title>")]
        [InlineData("<p>no head, no doctype</p>", "{M}<p>")]
        [InlineData("\uFEFF<!doctype html><html><head></head></html>", "<head>{M}</head>")]
        public void The_meta_goes_after_the_head_tag_or_the_doctype(string html, string expected)
        {
            var token = ApiToken.Create().Reveal();
            var meta = "<meta name=\"cwyls-maker-token\" content=\"" + token + "\">";
            var result = Encoding.UTF8.GetString(MakerStaticFiles.InjectToken(Encoding.UTF8.GetBytes(html), token));

            Assert.Contains(expected.Replace("{M}", meta), result);
            Assert.Equal(-1, result.IndexOf('\uFEFF'));
        }

        [Fact]
        public void A_token_that_is_not_hex_is_refused()
        {
            Assert.Throws<ArgumentException>(() => MakerStaticFiles.InjectToken(Encoding.UTF8.GetBytes("<head></head>"), "\"><script>"));
        }

        [Fact]
        public void Every_known_type_has_a_content_type_and_others_none()
        {
            Assert.Equal("text/html; charset=utf-8", MakerStaticFiles.ContentTypeOf("index.html"));
            Assert.Equal("text/javascript; charset=utf-8", MakerStaticFiles.ContentTypeOf("app.mjs"));
            Assert.Equal("application/json; charset=utf-8", MakerStaticFiles.ContentTypeOf("strings.json"));
            Assert.Equal("font/woff2", MakerStaticFiles.ContentTypeOf("plex.woff2"));
            Assert.Equal("image/png", MakerStaticFiles.ContentTypeOf("ICON.PNG"));
            Assert.Null(MakerStaticFiles.ContentTypeOf("tool.py"));
            Assert.Null(MakerStaticFiles.ContentTypeOf("run.exe"));
            Assert.Null(MakerStaticFiles.ContentTypeOf("Makefile"));
            Assert.Null(MakerStaticFiles.ContentTypeOf("voice.wav"));
        }

        private static int Count(string text, string part)
        {
            var n = 0;
            for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
            {
                n++;
            }

            return n;
        }
    }
}
