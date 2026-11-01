using System;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Backups
{
    /// <summary>
    /// <c>backup.json</c>, the marker in every launch save backup. A folder whose marker does not parse, or
    /// whose <c>kind</c> is not <see cref="Kind"/>, is not one of ours and is never deleted.
    /// </summary>
    /// <remarks>
    /// <code>
    /// {
    ///   "kind": "cwyls.save-backup",
    ///   "schema": 1,
    ///   "created": "2026-11-01T14:03:12.345+07:00",
    ///   "loaderVersion": "0.9.0",
    ///   "gameVersion": "1.18.0",
    ///   "files": 46,
    ///   "bytes": 188416,
    ///   "contentSha256": "&lt;64 lowercase hex digits&gt;"
    /// }
    /// </code>
    /// <c>contentSha256</c> is <see cref="SaveBackupContent"/>'s hash of the <c>SaveData</c> folder in the backup.
    /// </remarks>
    public sealed class SaveBackupManifest
    {
        /// <summary>The <c>kind</c> every marker carries.</summary>
        public const string Kind = "cwyls.save-backup";

        /// <summary>The schema this build writes.</summary>
        public const int CurrentSchema = 1;

        /// <summary>Creates a manifest.</summary>
        public SaveBackupManifest(
            int schema,
            DateTimeOffset? created,
            string? loaderVersion,
            string? gameVersion,
            long files,
            long bytes,
            string? contentSha256)
        {
            Schema = schema;
            Created = created;
            LoaderVersion = loaderVersion;
            GameVersion = gameVersion;
            Files = files;
            Bytes = bytes;
            ContentSha256 = contentSha256;
        }

        /// <summary>The schema it was written with (1 or more).</summary>
        public int Schema { get; }

        /// <summary>When the backup was made, with the local offset; null when the marker lacks it.</summary>
        public DateTimeOffset? Created { get; }

        /// <summary>The loader version that made it.</summary>
        public string? LoaderVersion { get; }

        /// <summary>The game version (Unity <c>Application.version</c>) it was made under.</summary>
        public string? GameVersion { get; }

        /// <summary>Files in the backup's <c>SaveData</c>.</summary>
        public long Files { get; }

        /// <summary>Their combined size.</summary>
        public long Bytes { get; }

        /// <summary>The content hash, 64 lowercase hex digits; null when the marker lacks a valid one.</summary>
        public string? ContentSha256 { get; }

        /// <summary>The marker text: indented JSON, one member per line, ending in a newline.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder("{\n");
            Member(sb, "kind", MiniJson.String(Kind), first: true);
            Member(sb, "schema", MiniJson.Number(Schema));
            Member(sb, "created", Created.HasValue ? MiniJson.String(MiniJson.Iso8601(Created.Value)) : "null");
            Member(sb, "loaderVersion", MiniJson.String(LoaderVersion));
            Member(sb, "gameVersion", MiniJson.String(GameVersion));
            Member(sb, "files", MiniJson.Number(Files));
            Member(sb, "bytes", MiniJson.Number(Bytes));
            Member(sb, "contentSha256", MiniJson.String(ContentSha256));
            return sb.Append("\n}\n").ToString();
        }

        /// <summary>
        /// Reads a marker. Accepted only when it is a JSON object whose <c>kind</c> is <see cref="Kind"/> and
        /// whose <c>schema</c> is a whole number of 1 or more; every other member is optional (a missing or
        /// malformed hash just means "compare as changed").
        /// </summary>
        /// <param name="json">The file's text.</param>
        /// <param name="manifest">The manifest, when accepted.</param>
        /// <param name="problem">Why it was not accepted.</param>
        public static bool TryParse(string? json, out SaveBackupManifest? manifest, out string? problem)
        {
            manifest = null;
            if (json is null)
            {
                problem = "backup.json is empty";
                return false;
            }

            if (!JsonReader.TryParse(json, out var root, out var error))
            {
                problem = "backup.json is not valid JSON (" + error + ")";
                return false;
            }

            if (!(root is JsonObject obj))
            {
                problem = "backup.json is not a JSON object";
                return false;
            }

            if (!(Get(obj, "kind") is string kind) || !string.Equals(kind, Kind, StringComparison.Ordinal))
            {
                problem = "backup.json is not a CWY:LS save backup marker (kind is not \"" + Kind + "\")";
                return false;
            }

            if (!(Get(obj, "schema") is long schema) || schema < 1 || schema > int.MaxValue)
            {
                problem = "backup.json has no valid schema";
                return false;
            }

            DateTimeOffset? created = null;
            if (Get(obj, "created") is string createdText
                && DateTimeOffset.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                created = parsed;
            }

            var hash = Get(obj, "contentSha256") as string;
            manifest = new SaveBackupManifest(
                (int)schema,
                created,
                Get(obj, "loaderVersion") as string,
                Get(obj, "gameVersion") as string,
                Get(obj, "files") is long files && files >= 0 ? files : 0,
                Get(obj, "bytes") is long bytes && bytes >= 0 ? bytes : 0,
                IsSha256Hex(hash) ? hash : null);
            problem = null;
            return true;
        }

        /// <summary>True for exactly 64 lowercase hex digits.</summary>
        public static bool IsSha256Hex(string? value)
        {
            if (value is null || value.Length != 64)
            {
                return false;
            }

            foreach (var c in value)
            {
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        private static object? Get(JsonObject obj, string key) => obj.TryGetValue(key, out var value) ? value : null;

        private static void Member(StringBuilder sb, string key, string jsonValue, bool first = false)
        {
            if (!first)
            {
                sb.Append(",\n");
            }

            sb.Append("  ").Append(MiniJson.String(key)).Append(": ").Append(jsonValue);
        }
    }
}
