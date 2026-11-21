using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>One published clip's disclosure in <c>voice-provenance.json</c> (CONTRACT-v2 §2).</summary>
    public sealed class MakerProvenanceEntry
    {
        public string Lang { get; set; } = string.Empty;
        public string Clip { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string StatementVersion { get; set; } = string.Empty;
        public string ConfirmedAt { get; set; } = string.Empty;
        /// <summary>SHA-256 (lowercase hex) of the published runtime WAV.</summary>
        public string Sha256 { get; set; } = string.Empty;
        public string PublishedAt { get; set; } = string.Empty;
    }

    /// <summary>
    /// The pack-root <c>voice-provenance.json</c>: published clip disclosures only. It records the creator's assertion
    /// (origin, consent statement version and time), the published WAV's SHA-256 and the publication time. It holds no
    /// source recording, take id, private note, machine path or identity document, and claims no rights verification.
    /// </summary>
    public static class MakerVoiceProvenance
    {
        public const int Schema = 1;

        /// <summary>
        /// The <c>confirmedAt</c> of a disclosure whose take lost its consent after publication (IG-01: an origin changed
        /// without a fresh confirmation, or consent withdrawn). The origin is still the take's current one; export refuses it.
        /// </summary>
        public const string Unconfirmed = "";

        /// <summary>True when the disclosure carries a consent: the current statement version and a confirmation time.</summary>
        public static bool IsConfirmed(MakerProvenanceEntry entry) =>
            entry.StatementVersion == MakerStudioContract.ConsentVersion && !string.IsNullOrEmpty(entry.ConfirmedAt);

        /// <summary>Parses the document; false (with <paramref name="problem"/>) when it is not a schema-1 provenance file.</summary>
        public static bool TryRead(string text, out List<MakerProvenanceEntry> entries, out string problem)
        {
            entries = new List<MakerProvenanceEntry>();
            problem = string.Empty;
            if (!JsonReader.TryParse(text, out var parsed, out var error) || !(parsed is JsonObject root))
            {
                problem = error ?? "not a JSON object";
                return false;
            }

            if (!(root.TryGetValue("schema", out var schema) && schema is long version && version == Schema)
                || !(root.TryGetValue("clips", out var node) && node is IList<object?> clips))
            {
                problem = "expected {\"schema\":1,\"clips\":[...]}";
                return false;
            }

            foreach (var item in clips)
            {
                if (!(item is JsonObject o)
                    || !(Text(o, "lang") is string lang) || !(Text(o, "clip") is string clip) || !(Text(o, "origin") is string origin)
                    || !(Text(o, "statementVersion") is string statement) || !(Text(o, "confirmedAt") is string confirmed)
                    || !(Text(o, "sha256") is string sha) || !(Text(o, "publishedAt") is string published))
                {
                    problem = "every clip needs lang, clip, origin, statementVersion, confirmedAt, sha256 and publishedAt";
                    return false;
                }

                entries.Add(new MakerProvenanceEntry
                {
                    Lang = lang, Clip = clip, Origin = origin, StatementVersion = statement, ConfirmedAt = confirmed, Sha256 = sha, PublishedAt = published,
                });
            }

            return true;
        }

        /// <summary>The document for <paramref name="entries"/>, sorted by language then clip so rewrites are stable.</summary>
        public static byte[] Write(IEnumerable<MakerProvenanceEntry> entries)
        {
            var clips = entries.OrderBy(e => e.Lang, StringComparer.Ordinal).ThenBy(e => e.Clip, StringComparer.Ordinal)
                .Select(e => (object?)new JsonObject
                {
                    ["lang"] = e.Lang, ["clip"] = e.Clip, ["origin"] = e.Origin, ["statementVersion"] = e.StatementVersion,
                    ["confirmedAt"] = e.ConfirmedAt, ["sha256"] = e.Sha256, ["publishedAt"] = e.PublishedAt,
                }).ToList();
            return MakerProjectStore.Utf8(ApiJson.Write(new JsonObject { ["schema"] = Schema, ["clips"] = clips }) + "\n");
        }

        public static string Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static string? Text(JsonObject o, string key) => o.TryGetValue(key, out var v) ? v as string : null;
    }
}
