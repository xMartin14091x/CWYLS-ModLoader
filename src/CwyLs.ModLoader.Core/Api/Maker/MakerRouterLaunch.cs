using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    public sealed partial class MakerRouter
    {
        private const double LaunchLifetimeSeconds = 60;
        private const int MaxPendingLaunches = 16;
        private readonly object _launchGate = new object();
        private readonly List<(ApiToken Nonce, double Expires)> _launches = new List<(ApiToken, double)>();
        private readonly ApiToken _pageSession = ApiToken.Create();

        private string PageCookieName => "cwyls-maker-" + Port.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Mints a single-use browser launch URL, valid for 60 seconds. Each call makes a fresh link;
        /// at most 16 unconsumed links are retained. Only the command's caller receives it, never request logs.
        /// The browser exchanges it for an HttpOnly, host-only session cookie under /maker/ and redirects
        /// to the clean page URL. The cookie permits reloads until this router stops.
        /// </summary>
        /// <exception cref="InvalidOperationException">Pack Maker is closing.</exception>
        public string CreateOpenUrl()
        {
            lock (_launchGate)
            {
                if (IsStopping) throw new InvalidOperationException("Pack Maker is closing");
                var now = _o.Clock.MonotonicSeconds;
                _launches.RemoveAll(link => now >= link.Expires);
                if (_launches.Count == MaxPendingLaunches) _launches.RemoveAt(0);
                var nonce = ApiToken.Create();
                _launches.Add((nonce, now + LaunchLifetimeSeconds));
                return MakerDefaults.WebUrl(Port) + "?k=" + nonce.Reveal();
            }
        }

        // Null means the page session is authenticated. A supplied nonce always needs to be valid,
        // even with an existing cookie: a spent link never succeeds a second time.
        private ApiResponse? PageAccess(ApiRequest request, ApiTarget target)
        {
            if (target.Get("k") is string nonce)
            {
                var consumed = false;
                lock (_launchGate)
                {
                    var now = _o.Clock.MonotonicSeconds;
                    _launches.RemoveAll(link => now >= link.Expires);
                    var found = -1;
                    for (var i = 0; i < _launches.Count; i++)
                    {
                        if (_launches[i].Nonce.Matches(ApiToken.Scheme + " " + nonce)) found = i;
                    }

                    if (found >= 0)
                    {
                        _launches.RemoveAt(found);
                        consumed = true;
                    }
                }

                if (!consumed) return OpenFromGamePage();
                return new ApiResponse(302, "{}", new[]
                {
                    ApiResponse.Header("Location", MakerDefaults.WebPath + "/"),
                    ApiResponse.Header("Set-Cookie", PageCookieName + "=" + _pageSession.Reveal() + "; Path=/maker/; HttpOnly; SameSite=Strict"),
                    ApiResponse.Header("Referrer-Policy", "no-referrer"),
                    ApiResponse.Header("X-Frame-Options", "DENY"),
                    ApiResponse.Header("Content-Security-Policy", "frame-ancestors 'none'"),
                });
            }

            // A duplicated cookie name (including comma-joined Cookie headers) is never accepted.
            var cookies = request.Headers.Get("Cookie");
            string? value = null;
            if (cookies != null && cookies.IndexOf(',') < 0)
            {
                foreach (var part in cookies.Split(';'))
                {
                    var pair = part.Trim();
                    if (!pair.StartsWith(PageCookieName + "=", StringComparison.Ordinal)) continue;
                    if (value != null) return OpenFromGamePage();
                    value = pair.Substring(PageCookieName.Length + 1);
                }
            }

            return _pageSession.Matches(value is null ? null : ApiToken.Scheme + " " + value) ? null : OpenFromGamePage();
        }

        private static ApiResponse OpenFromGamePage() => ApiResponse.Raw(
            200,
            Encoding.UTF8.GetBytes("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Open Pack Maker</title></head><body>" +
                "<p>Open Pack Maker from the game: type <code>maker</code> in the console</p>" +
                "<p lang=\"th\">เปิด Pack Maker จากในเกมนะ: พิมพ์ <code>maker</code> ในคอนโซล</p></body></html>"),
            "text/html; charset=utf-8",
            ApiResponse.Header("X-Frame-Options", "DENY"),
            ApiResponse.Header("Content-Security-Policy", "frame-ancestors 'none'"),
            ApiResponse.Header("Referrer-Policy", "no-referrer"));
    }
}
