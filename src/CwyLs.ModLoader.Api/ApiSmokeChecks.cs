using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Api
{
    /// <summary>
    /// One quiet smoke check for the local API. Off: the key file must be absent (and with
    /// <c>[Api] Enabled=true</c> being off is a failure that names why). On: three loopback requests
    /// through the real listener - <c>/v1/info</c> with the key is 200, the same with an
    /// <c>Origin</c> header is 403, without the key is 401.
    /// </summary>
    internal static class ApiSmokeChecks
    {
        /// <summary>Smoke phase label of the check.</summary>
        internal const string Phase = "S";

        /// <summary>The check id.</summary>
        internal const string SelfTestId = "s.api.selftest";

        /// <summary>Registers the check; idempotent (the registry replaces by id).</summary>
        internal static void Register() => SmokeRegistry.Register(new SmokeCheck(SelfTestId, Phase, SelfTest));

        private static SmokeCheckResult SelfTest()
        {
            var session = ApiFeature.Current;
            if (session is null || session.Transport is null)
            {
                if (File.Exists(ApiFeature.KeyFilePath))
                {
                    return SmokeCheckResult.Fail(SelfTestId, "the API is off but the key file exists: " + ApiFeature.KeyFilePath);
                }

                return ApiFeature.EnabledInConfig
                    ? SmokeCheckResult.Fail(SelfTestId, "[Api] Enabled=true but the API is off: " + ApiFeature.OffReason)
                    : SmokeCheckResult.Pass(SelfTestId, "off (" + ApiFeature.OffReason + "): no listener, key file absent");
            }

            if (!File.Exists(ApiFeature.KeyFilePath))
            {
                return SmokeCheckResult.Fail(SelfTestId, "the API is on but the key file is missing: " + ApiFeature.KeyFilePath);
            }

            var port = session.Runtime.Router.Port;
            var auth = new KeyValuePair<string, string>("Authorization", ApiToken.Scheme + " " + session.Key.Reveal());
            var client = new KeyValuePair<string, string>(Core.Api.ApiDefaults.ClientHeader, "smoke");
            var info = ApiLoopbackClient.Send(port, "GET", "/v1/info", new[] { auth, client }, timeoutMs: 1000);
            var browser = ApiLoopbackClient.Send(
                port, "GET", "/v1/info", new[] { auth, client, new KeyValuePair<string, string>("Origin", "http://example.invalid") }, timeoutMs: 1000);
            var anonymous = ApiLoopbackClient.Send(port, "GET", "/v1/info", new[] { client }, timeoutMs: 1000);
            var detail = session.Transport.Name + " :" + port + " info=" + info.Status + " origin=" + browser.Status + " noKey=" + anonymous.Status;
            return info.Status == 200 && browser.Status == 403 && anonymous.Status == 401
                ? SmokeCheckResult.Pass(SelfTestId, detail)
                : SmokeCheckResult.Fail(SelfTestId, detail + " (expected 200/403/401)");
        }
    }
}
