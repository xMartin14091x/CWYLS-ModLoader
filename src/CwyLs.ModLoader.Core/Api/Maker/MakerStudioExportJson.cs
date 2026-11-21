using System.Collections.Generic;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>Contract v2.3 wire shapes: PackInfo, icon, ExportPreview, ExportResult (and the export receipt).</summary>
    public static class MakerStudioExportJson
    {
        public static JsonObject Info(MakerPackInfo i) => new JsonObject
        {
            ["id"] = i.Id, ["name"] = i.Name, ["version"] = i.Version, ["author"] = i.Author, ["description"] = i.Description,
            ["languages"] = List(i.Languages), ["hasIcon"] = i.HasIcon, ["sizeBytes"] = i.SizeBytes, ["pending"] = i.Pending,
            ["changelog"] = Changelog(i.Changelog),
        };

        public static JsonObject Icon(MakerPackIcon i) => new JsonObject
        {
            ["hasIcon"] = i.HasIcon, ["sizeBytes"] = i.SizeBytes, ["width"] = (long)i.Width, ["height"] = (long)i.Height,
        };

        public static JsonObject Preview(MakerExportPreview p)
        {
            var checks = new List<object?>();
            foreach (var c in p.Checks)
            {
                checks.Add(new JsonObject
                {
                    ["id"] = c.Id, ["severity"] = c.Severity, ["message"] = c.Message, ["count"] = (long)c.Count, ["paths"] = List(c.Paths),
                    ["code"] = c.Code, ["params"] = Params(c.Params),
                });
            }

            return new JsonObject
            {
                ["packId"] = p.PackId, ["version"] = p.Version,
                ["nextVersions"] = new JsonObject { ["patch"] = p.NextVersions.Patch, ["minor"] = p.NextVersions.Minor, ["major"] = p.NextVersions.Major },
                ["ok"] = p.Ok, ["errorCount"] = (long)p.ErrorCount, ["warningCount"] = (long)p.WarningCount, ["checks"] = checks,
                ["files"] = new JsonObject
                {
                    ["included"] = (long)p.Files.Included, ["includedBytes"] = p.Files.IncludedBytes,
                    ["excluded"] = (long)p.Files.Excluded, ["excludedBytes"] = p.Files.ExcludedBytes,
                },
                ["changelog"] = Changelog(p.Changelog),
                ["lastExport"] = p.LastExport is null ? null : Last(p.LastExport),
            };
        }

        /// <summary>The result; <c>folder</c> only when known (it is never set on the copy a receipt stores).</summary>
        public static JsonObject Result(MakerExportResult r) => WithFolder(new JsonObject
        {
            ["packId"] = r.PackId, ["version"] = r.Version, ["previousVersion"] = r.PreviousVersion, ["fileName"] = r.FileName,
            ["sizeBytes"] = r.SizeBytes, ["sha256"] = r.Sha256, ["files"] = (long)r.Files, ["downloadable"] = r.Downloadable,
            ["manifestUpdated"] = r.ManifestUpdated, ["changelogUpdated"] = r.ChangelogUpdated, ["warningCount"] = (long)r.WarningCount,
            ["note"] = r.Note,
        }, r.Folder);

        /// <summary>The last export; <c>folder</c> only when known (it is never set on the copy <c>last.json</c> stores).</summary>
        public static JsonObject Last(MakerLastExport l) => WithFolder(new JsonObject
        {
            ["fileName"] = l.FileName, ["version"] = l.Version, ["sizeBytes"] = l.SizeBytes, ["createdAt"] = l.CreatedAt,
        }, l.Folder);

        private static JsonObject WithFolder(JsonObject o, string? folder)
        {
            if (folder != null) o["folder"] = folder;
            return o;
        }

        /// <summary>The stored receipt: the result plus the request fingerprint.</summary>
        public static string Receipt(MakerExportResult r, string fingerprint) =>
            ApiJson.Write(new JsonObject { ["schema"] = 1L, ["fingerprint"] = fingerprint, ["result"] = Result(r) }) + "\n";

        public static bool TryReceipt(string text, out MakerExportResult? result, out string? fingerprint)
        {
            result = null;
            fingerprint = null;
            if (!JsonReader.TryParse(text, out var parsed, out _) || !(parsed is JsonObject o) || !(o.TryGetValue("result", out var node) && node is JsonObject r))
                return false;
            fingerprint = o.TryGetValue("fingerprint", out var f) ? f as string : null;
            result = new MakerExportResult
            {
                PackId = Text(r, "packId"), Version = Text(r, "version"), PreviousVersion = Text(r, "previousVersion"), FileName = Text(r, "fileName"),
                SizeBytes = Number(r, "sizeBytes"), Sha256 = Text(r, "sha256"), Files = (int)Number(r, "files"), Downloadable = Flag(r, "downloadable"),
                ManifestUpdated = Flag(r, "manifestUpdated"), ChangelogUpdated = Flag(r, "changelogUpdated"), WarningCount = (int)Number(r, "warningCount"),
                Note = Text(r, "note"),
            };
            return fingerprint != null && result.FileName.Length > 0;
        }

        public static bool TryLast(string text, out MakerLastExport? last)
        {
            last = null;
            if (!JsonReader.TryParse(text, out var parsed, out _) || !(parsed is JsonObject o)) return false;
            last = new MakerLastExport { FileName = Text(o, "fileName"), Version = Text(o, "version"), SizeBytes = Number(o, "sizeBytes"), CreatedAt = Text(o, "createdAt") };
            return last.FileName.Length > 0;
        }

        private static JsonObject Params(IReadOnlyDictionary<string, object?> values)
        {
            var o = new JsonObject();
            foreach (var pair in values)
            {
                o[pair.Key] = pair.Value is IReadOnlyDictionary<string, object?> nested ? Params(nested)
                    : pair.Value is IReadOnlyList<object?> list ? ParamList(list)
                    : pair.Value;
            }

            return o;
        }

        private static List<object?> ParamList(IReadOnlyList<object?> items)
        {
            var list = new List<object?>(items.Count);
            foreach (var item in items) list.Add(item is IReadOnlyDictionary<string, object?> nested ? Params(nested) : item);
            return list;
        }

        private static JsonObject? Changelog(MakerChangelogEntry? c) => c is null ? null : new JsonObject { ["version"] = c.Version, ["notes"] = c.Notes };

        private static List<object?> List(IReadOnlyList<string> items)
        {
            var list = new List<object?>(items.Count);
            foreach (var item in items) list.Add(item);
            return list;
        }

        private static string Text(JsonObject o, string key) => o.TryGetValue(key, out var v) && v is string s ? s : string.Empty;
        private static long Number(JsonObject o, string key) => o.TryGetValue(key, out var v) && v is long n ? n : 0;
        private static bool Flag(JsonObject o, string key) => o.TryGetValue(key, out var v) && v is bool b && b;
    }
}
