using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>The Studio v2 catalog reads and pack creation routes (CONTRACT-v2 sections 4 and 6); the rest of the router is in MakerRouter.cs.</summary>
    public sealed partial class MakerRouter
    {
        private const string OccurrenceHint = "32 lowercase hexadecimal characters";

        /// <summary><c>POST /studio/packs</c>. Without an installed <c>newPacks</c> backend the answer is 503 before the body is read.</summary>
        private ApiResponse CreatePackRoute(ApiRequest r, ApiTarget target)
        {
            var noQuery = NoQuery(target);
            if (noQuery != null) return noQuery;
            var backend = ReadStudio()?.NewPacks;
            if (backend is null) return ApiResponse.Error(503, MakerErrorCodes.Unavailable, "newPacks is not installed");
            if (!Take(ApiRateClass.Action, out var limited)) return limited!;
            var jsonProblem = ReadJsonObject(r, out var body);
            if (jsonProblem != null) return jsonProblem;
            if (!TryCreatePack(body!, out var request, out var problem)) return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
            return Call(cancel => backend.CreatePackAsync(request!, cancel), MakerStudioCatalogJson.PackCreated, 201);
        }

        /// <summary>The Studio catalog's four GET routes; null when the path is none of them.</summary>
        private ApiResponse? StudioCatalogRoute(ApiRequest r, ApiTarget target, string[] s, string path, MakerStudioBackends studio)
        {
            if (s.Length == 2 && s[1] == "packs")
            {
                if (r.Method != "GET") return NoRoute(r.Method, path, "GET or POST");
                if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                var bodyProblem = RefuseBody(r); if (bodyProblem != null) return bodyProblem;
                var queryProblem = NoQuery(target); if (queryProblem != null) return queryProblem;
                var catalog = studio.Catalog;
                return catalog is null
                    ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio catalog is not installed")
                    : CatalogCall(cancel => catalog.PacksAsync(cancel), MakerStudioCatalogJson.Packs);
            }

            if (s.Length < 6 || s.Length > 7 || s[1] != "packs" || s[3] != "languages" || (s[5] != "lines" && s[5] != "groups")) return null;
            if (s.Length == 7 && s[5] != "lines") return null;
            if (!MakerSegments.IsPackId(s[2])) return BadSegment("the pack id", "[a-z0-9][a-z0-9._-]{0,63}");
            if (!MakerSegments.IsLang(s[4])) return BadSegment("the language", "[a-z]{2,3}(-[a-z0-9]{2,8})?");
            if (s.Length == 7 && !MakerSegments.IsId(s[6])) return BadSegment("the line id", "[A-Za-z0-9_.()-]{1,128}");
            if (r.Method != "GET") return NoRoute(r.Method, path, "GET");
            if (!Take(ApiRateClass.Read, out var readLimited)) return readLimited!;
            var noBody = RefuseBody(r); if (noBody != null) return noBody;
            var backend = studio.Catalog;

            if (s[5] == "groups")
            {
                var noQuery = NoQuery(target); if (noQuery != null) return noQuery;
                return backend is null
                    ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio catalog is not installed")
                    : CatalogCall(cancel => backend.GroupsAsync(s[2], s[4], cancel), MakerStudioCatalogJson.Groups);
            }

            if (s.Length == 7)
            {
                string? occurrence = null;
                foreach (var pair in target.Query)
                {
                    if (pair.Key != "occurrenceId") return UnknownQuery(pair.Key, "occurrenceId");
                    if (!MakerStudioPaths.IsOperationId(pair.Value)) return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "occurrenceId must be " + OccurrenceHint);
                    occurrence = pair.Value;
                }

                return backend is null
                    ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio catalog is not installed")
                    : CatalogCall(cancel => backend.LineAsync(s[2], s[4], s[6], occurrence, cancel), MakerStudioCatalogJson.Line);
            }

            if (!TryLineQuery(target, s[2], s[4], out var query, out var queryError)) return queryError!;
            return backend is null
                ? ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio catalog is not installed")
                : CatalogCall(cancel => backend.LinesAsync(query!, cancel), MakerStudioCatalogJson.LinePage);
        }

        private static bool TryLineQuery(ApiTarget target, string packId, string lang, out MakerStudioLineQuery? query, out ApiResponse? problem)
        {
            query = null;
            var result = new MakerStudioLineQuery { PackId = packId, Lang = lang, Filter = "all", Offset = 0, Limit = MakerDefaults.DefaultLinesLimit };
            foreach (var pair in target.Query)
            {
                switch (pair.Key)
                {
                    case "q":
                        if (pair.Value.Length > MakerDefaults.MaxSearchChars)
                        {
                            problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, "q may be at most " + MakerDefaults.MaxSearchChars + " characters");
                            return false;
                        }

                        result.Search = pair.Value.Length == 0 ? null : pair.Value;
                        break;
                    case "group":
                        if (pair.Value.Length == 0 || pair.Value.Length > 200)
                        {
                            problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, "group must be 1-200 characters");
                            return false;
                        }

                        result.Group = pair.Value;
                        break;
                    case "filter":
                        if (!Array.Exists(StudioFilters, f => f == pair.Value))
                        {
                            problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest,
                                "filter must be all, todo, draft, done, needs-retake, starred, missing-subtitle or missing-dub");
                            return false;
                        }

                        result.Filter = pair.Value;
                        break;
                    case "offset":
                        if (!int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                        {
                            problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, "offset must be a whole number of 0 or more");
                            return false;
                        }

                        result.Offset = offset;
                        break;
                    case "limit":
                        if (!int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
                            || limit < 1 || limit > MakerDefaults.MaxLinesLimit)
                        {
                            problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, "limit must be 1-" + MakerDefaults.MaxLinesLimit);
                            return false;
                        }

                        result.Limit = limit;
                        break;
                    case "catalogRevision":
                        if (!ValidToken(pair.Value, "catalogRevision", out var tokenProblem))
                        {
                            problem = ApiResponse.Error(400, MakerErrorCodes.BadRequest, tokenProblem);
                            return false;
                        }

                        result.CatalogRevision = pair.Value;
                        break;
                    default:
                        problem = UnknownQuery(pair.Key, "q, group, filter, offset, limit, catalogRevision");
                        return false;
                }
            }

            query = result;
            problem = null;
            return true;
        }

        private static readonly string[] StudioFilters =
            { "all", "todo", "draft", "done", "needs-retake", "starred", "missing-subtitle", "missing-dub" };

        private static bool TryCreatePack(JsonObject body, out MakerStudioCreatePackRequest? request, out string problem)
        {
            request = null;
            string? operation = null, id = null, name = null, author = null, description = null;
            MakerStudioLanguage? language = null;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "operationId": operation = member.Value as string; break;
                    case "id": id = member.Value as string; break;
                    case "name": name = member.Value as string; break;
                    case "author":
                        if (member.Value != null && !(member.Value is string)) { problem = "author must be a string or null"; return false; }
                        author = member.Value as string;
                        break;
                    case "description":
                        if (member.Value != null && !(member.Value is string)) { problem = "description must be a string or null"; return false; }
                        description = member.Value as string;
                        break;
                    case "language":
                        if (!(member.Value is JsonObject languageObject)) { problem = "language must be an object"; return false; }
                        if (!TryLanguage(languageObject, out language, out problem)) return false;
                        break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!MakerStudioPaths.IsOperationId(operation)) { problem = "operationId must be " + OccurrenceHint; return false; }
            if (string.IsNullOrEmpty(id)) { problem = "id is required"; return false; }
            if (name is null) { problem = "name is required"; return false; }
            if (language is null) { problem = "language is required"; return false; }
            request = new MakerStudioCreatePackRequest { OperationId = operation!, Id = id!, Name = name, Author = author, Description = description, Language = language };
            problem = string.Empty;
            return true;
        }

        private static bool TryLanguage(JsonObject obj, out MakerStudioLanguage? language, out string problem)
        {
            language = null;
            string? code = null, caption = null, english = null, script = null;
            List<string>? fallback = null;
            foreach (var member in obj)
            {
                switch (member.Key)
                {
                    case "code": code = member.Value as string; break;
                    case "caption": caption = Optional(member.Value, out problem); if (problem.Length > 0) return false; break;
                    case "englishName": english = Optional(member.Value, out problem); if (problem.Length > 0) return false; break;
                    case "script": script = Optional(member.Value, out problem); if (problem.Length > 0) return false; break;
                    case "fallback":
                        if (member.Value is null) break;
                        if (!(member.Value is IList<object?> list)) { problem = "fallback must be an array of language codes"; return false; }
                        fallback = new List<string>();
                        foreach (var item in list)
                        {
                            if (!(item is string text)) { problem = "fallback must be an array of language codes"; return false; }
                            fallback.Add(text);
                        }

                        break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (string.IsNullOrEmpty(code)) { problem = "language.code is required"; return false; }
            language = new MakerStudioLanguage { Code = code!, Caption = caption, EnglishName = english, Script = script, Fallback = fallback };
            problem = string.Empty;
            return true;
        }

        private static string? Optional(object? value, out string problem)
        {
            if (value != null && !(value is string)) { problem = "language members must be strings or null"; return null; }
            problem = string.Empty;
            return value as string;
        }
    }
}
