using System.Collections.Generic;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>Contract v2.3 routes: <c>P/info</c>, <c>P/icon</c>, <c>P/export</c>, <c>P/exports/{fileName}</c> (Pack Maker Phase C).</summary>
    public sealed partial class MakerRouter
    {
        private const string PngMediaType = "image/png";

        /// <summary>The v2.3 pack routes; null when the path is none of them.</summary>
        private ApiResponse? StudioExportRoute(ApiRequest r, ApiTarget target, string[] s, string path, MakerStudioBackends studio)
        {
            var single = s.Length == 4 && (s[3] == "info" || s[3] == "icon" || s[3] == "export");
            var download = s.Length == 5 && s[3] == "exports";
            if (s.Length < 4 || s[1] != "packs" || !(single || download)) return null;
            if (!MakerSegments.IsPackId(s[2])) return BadSegment("the pack id", "[a-z0-9][a-z0-9._-]{0,63}");
            var packId = s[2];
            var backend = studio.Export;
            var missing = ApiResponse.Error(503, MakerErrorCodes.Unavailable, "Studio export is not installed");

            if (download)
            {
                if (r.Method != "GET") return NoRoute(r.Method, path, "GET");
                if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                var noBody = RefuseBody(r); if (noBody != null) return noBody;
                var noQuery = NoQuery(target); if (noQuery != null) return noQuery;
                return backend is null ? missing : CallResponse(cancel => backend.ReadExportAsync(packId, s[4], cancel), Binary, CatalogLimit);
            }

            if (s[3] == "info")
            {
                if (r.Method != "GET") return NoRoute(r.Method, path, "GET");
                if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                var noBody = RefuseBody(r); if (noBody != null) return noBody;
                var noQuery = NoQuery(target); if (noQuery != null) return noQuery;
                return backend is null ? missing : CatalogCall(cancel => backend.InfoAsync(packId, cancel), MakerStudioExportJson.Info);
            }

            if (s[3] == "icon")
            {
                if (r.Method == "GET")
                {
                    if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                    var noBody = RefuseBody(r); if (noBody != null) return noBody;
                    var noQuery = NoQuery(target); if (noQuery != null) return noQuery;
                    return backend is null ? missing : CallResponse(cancel => backend.ReadIconAsync(packId, cancel), Binary);
                }

                if (r.Method != "PUT") return NoRoute(r.Method, path, "GET or PUT");
                if (!Take(ApiRateClass.Action, out var writeLimited)) return writeLimited!;
                var query = NoQuery(target); if (query != null) return query;
                var bodyProblem = ReadPng(r, out var png); if (bodyProblem != null) return bodyProblem;
                return backend is null ? missing : Call(cancel => backend.SaveIconAsync(packId, png, cancel), MakerStudioExportJson.Icon);
            }

            if (r.Method == "GET")
            {
                if (!Take(ApiRateClass.Read, out var limited)) return limited!;
                var noBody = RefuseBody(r); if (noBody != null) return noBody;
                var noQuery = NoQuery(target); if (noQuery != null) return noQuery;
                return backend is null ? missing : CatalogCall(cancel => backend.CheckAsync(packId, cancel), MakerStudioExportJson.Preview);
            }

            if (r.Method != "POST") return NoRoute(r.Method, path, "GET or POST");
            if (!Take(ApiRateClass.Action, out var actionLimited)) return actionLimited!;
            var noQueryPost = NoQuery(target); if (noQueryPost != null) return noQueryPost;
            var jsonProblem = ReadJsonObject(r, out var body); if (jsonProblem != null) return jsonProblem;
            if (!TryExport(body!, packId, out var request, out var problem)) return ApiResponse.Error(400, MakerErrorCodes.BadRequest, problem);
            return backend is null
                ? missing
                : CallResponse(cancel => backend.ExportAsync(request!, cancel), value => ApiResponse.Json(201, ApiJson.Write(MakerStudioExportJson.Result(value))), CatalogLimit);
        }

        private static ApiResponse Binary(MakerBinary value) =>
            value.FileName is null
                ? ApiResponse.Raw(200, value.Bytes, value.MediaType)
                : ApiResponse.Raw(200, value.Bytes, value.MediaType, new KeyValuePair<string, string>("Content-Disposition", "attachment; filename=\"" + value.FileName + "\""));

        private ApiResponse? ReadPng(ApiRequest r, out byte[] png)
        {
            png = new byte[0];
            if (r.Chunked) return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "send the body with Content-Length; chunked bodies are not accepted");
            if (!IsMediaType(r.Headers.Get("Content-Type"), PngMediaType, jsonCharset: false))
                return ApiResponse.Error(415, MakerErrorCodes.UnsupportedMedia, "an icon must be sent as Content-Type: image/png");
            if (r.ContentLength is long declared && declared > MakerPackExporter.MaxIconBytes)
                return ApiResponse.Error(413, MakerErrorCodes.TooLarge, "an icon may be at most 512 KiB");
            var read = r.ReadBody((int)MakerPackExporter.MaxIconBytes);
            if (read.Status == ApiBodyStatus.TooLarge) return ApiResponse.Error(413, MakerErrorCodes.TooLarge, "an icon may be at most 512 KiB");
            if (read.Status == ApiBodyStatus.TimedOut) return ApiResponse.Error(408, MakerErrorCodes.RequestTimeout, "the body could not be read: " + read.Problem);
            if (read.Status == ApiBodyStatus.Failed) return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "the body could not be read: " + read.Problem);
            if (read.Bytes.Length == 0) return ApiResponse.Error(400, MakerErrorCodes.BadRequest, "this route needs the PNG as the body");
            png = read.Bytes;
            return null;
        }

        private static bool TryExport(JsonObject body, string packId, out MakerExportRequest? request, out string problem)
        {
            request = null;
            string? operation = null, expected = null, bump = null, notes = null;
            foreach (var member in body)
            {
                switch (member.Key)
                {
                    case "operationId": operation = member.Value as string; break;
                    case "expectedVersion": expected = member.Value as string; break;
                    case "bump": bump = member.Value as string; break;
                    case "notes":
                        if (member.Value != null && !(member.Value is string)) { problem = "notes must be a string or null"; return false; }
                        notes = member.Value as string;
                        break;
                    default: problem = "unknown member \"" + member.Key + "\""; return false;
                }
            }

            if (!MakerStudioPaths.IsOperationId(operation)) { problem = "operationId must be " + OccurrenceHint; return false; }
            if (string.IsNullOrEmpty(expected)) { problem = "expectedVersion is required (the version GET export returned)"; return false; }
            if (bump != "none" && bump != "patch" && bump != "minor" && bump != "major") { problem = "bump must be none, patch, minor or major"; return false; }
            var notesProblem = MakerPackChecks.NotesProblem(notes);
            if (notesProblem != null) { problem = notesProblem; return false; }
            request = new MakerExportRequest { PackId = packId, OperationId = operation!, ExpectedVersion = expected!, Bump = bump!, Notes = notes };
            problem = string.Empty;
            return true;
        }
    }
}
