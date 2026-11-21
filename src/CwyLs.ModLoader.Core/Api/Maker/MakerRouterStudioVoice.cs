using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>Contract v2.4 route: <c>GET</c>/<c>PUT P/voice</c>, the pack's voice origin and consent default; D-148 <c>POST P/voice/label</c>.</summary>
    public sealed partial class MakerRouter
    {
        /// <summary>The v2.4 voice default route; null when the path is not it.</summary>
        private ApiResponse? StudioVoiceRoute(ApiRequest r, ApiTarget target, string[] s, string path, MakerStudioBackends studio)
        {
            if (s.Length == 5 && s[1] == "packs" && s[3] == "voice" && s[4] == "label") return StudioVoiceLabelRoute(r, target, s, path, studio);
            if (s.Length != 4 || s[1] != "packs" || s[3] != "voice") return null;
            if (!MakerSegments.IsPackId(s[2])) return BadSegment("the pack id", "[a-z0-9][a-z0-9._-]{0,63}");
            var packId = s[2];
            var backend = studio.VoiceDefaults;
            var missing = ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio voice defaults are not installed");

            if (r.Method == "GET")
            {
                if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                var noBody = RefuseBody(r); if (noBody != null) return noBody;
                var noQuery = NoQuery(target); if (noQuery != null) return noQuery;
                return backend is null ? missing : Call(cancel => backend.VoiceDefaultAsync(packId, cancel), MakerStudioJson.VoiceDefault);
            }

            if (r.Method != "PUT") return NoRoute(r.Method, path, "GET or PUT");
            if (!Take(ApiRateClass.Action, out var writeLimited)) return writeLimited!;
            var query = NoQuery(target); if (query != null) return query;
            var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
            if (!TryVoiceDefault(body!, packId, out var request, out var problem)) return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
            return backend is null ? missing : Call(cancel => backend.SaveVoiceDefaultAsync(request!, cancel), MakerStudioJson.VoiceDefault);
        }

        /// <summary>D-148 route: <c>POST P/voice/label</c>, labels every unlabelled dub with the pack's voice default.</summary>
        private ApiResponse StudioVoiceLabelRoute(ApiRequest r, ApiTarget target, string[] s, string path, MakerStudioBackends studio)
        {
            if (!MakerSegments.IsPackId(s[2])) return BadSegment("the pack id", "[a-z0-9][a-z0-9._-]{0,63}");
            var packId = s[2];
            var backend = studio.VoiceLabels;
            if (r.Method != "POST") return NoRoute(r.Method, path, "POST");
            if (!Take(ApiRateClass.Action, out var limited)) return limited!;
            var query = NoQuery(target); if (query != null) return query;
            var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
            if (!TryVoiceLabel(body!, packId, out var request, out var problem)) return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
            return backend is null
                ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio voice labels are not installed")
                : Call(cancel => backend.LabelDubsAsync(request!, cancel), MakerStudioJson.VoiceLabelled);
        }

        /// <summary>
        /// <c>{expectedRevision, operationId, origin, consent}</c>, all four required: the voice default's revision and origin as
        /// the page showed them, and a confirmed <c>consent-1</c> (the creator's click). Unknown members are refused.
        /// </summary>
        private static bool TryVoiceLabel(JsonObject body, string packId, out MakerStudioVoiceLabelRequest? request, out string problem)
        {
            request = null;
            string? expected = null, operation = null, origin = null;
            MakerStudioConsent? consent = null;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "expectedRevision": expected = member.Value as string; break;
                    case "operationId": operation = member.Value as string; break;
                    case "origin": origin = member.Value as string; break;
                    case "consent":
                        if (!(member.Value is JsonObject c)) { problem = "consent must be an object"; return false; }
                        if (!TryConsent(c, out consent, out problem)) return false;
                        break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!ValidMutation(expected, operation, out problem)) return false;
            var invalid = MakerVoiceLabelStore.Validate(origin, consent);
            if (invalid != null) { problem = invalid; return false; }
            request = new MakerStudioVoiceLabelRequest
            {
                PackId = packId, ExpectedRevision = expected!, OperationId = operation!, Origin = origin!, Consent = consent!,
            };
            return true;
        }

        /// <summary>
        /// <c>{expectedRevision, operationId, origin, consent}</c>, all four required: a known origin with a confirmed
        /// <c>consent-1</c> sets the default, <c>origin:null, consent:null</c> clears it. Unknown members are refused.
        /// </summary>
        private static bool TryVoiceDefault(JsonObject body, string packId, out MakerStudioVoiceDefaultRequest? request, out string problem)
        {
            request = null;
            string? expected = null, operation = null, origin = null;
            MakerStudioConsent? consent = null;
            bool hasOrigin = false, hasConsent = false;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "expectedRevision": expected = member.Value as string; break;
                    case "operationId": operation = member.Value as string; break;
                    case "origin":
                        hasOrigin = true;
                        if (member.Value != null && !(member.Value is string)) { problem = "origin must be own, ai-assisted, ai-generated or null"; return false; }
                        origin = member.Value as string;
                        break;
                    case "consent":
                        hasConsent = true;
                        if (member.Value is null) break;
                        if (!(member.Value is JsonObject c)) { problem = "consent must be an object or null"; return false; }
                        if (!TryConsent(c, out consent, out problem)) return false;
                        break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!ValidMutation(expected, operation, out problem)) return false;
            if (!hasOrigin || !hasConsent) { problem = "origin and consent are required (send both as null to clear the default)"; return false; }
            var invalid = MakerVoiceDefaultStore.Validate(origin, consent);
            if (invalid != null) { problem = invalid; return false; }
            request = new MakerStudioVoiceDefaultRequest
            {
                PackId = packId, ExpectedRevision = expected!, OperationId = operation!, Origin = origin, Consent = consent,
            };
            return true;
        }
    }
}
