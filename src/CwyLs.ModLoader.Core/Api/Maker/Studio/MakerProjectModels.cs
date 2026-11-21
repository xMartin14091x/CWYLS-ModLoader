using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    public sealed class MakerProjectHeader
    {
        public int Schema { get; set; } = MakerStudioContract.StorageSchema;
        public string ProjectId { get; set; } = string.Empty;
    }

    public sealed class MakerLineDocument
    {
        internal bool ExternalDrift { get; set; }
        public int Schema { get; set; } = MakerStudioContract.StorageSchema;
        public string Revision { get; set; } = string.Empty;
        public string SubtitleFingerprint { get; set; } = string.Empty;
        public MakerStudioWorkflow Workflow { get; set; } = new MakerStudioWorkflow();
        public List<MakerHistoryRevision> History { get; } = new List<MakerHistoryRevision>();
        public bool HistoryGap { get; set; }
        public string? FavouriteTakeId { get; set; }
        public string? PublishedTakeId { get; set; }
        public string? PublishedTakeRevision { get; set; }
        /// <summary>The clip this line's take was published to, so a later publication by an alias line is noticed.</summary>
        public string? PublishedClip { get; set; }
        public List<MakerTakeRecord> Takes { get; } = new List<MakerTakeRecord>();
    }

    public sealed class MakerTakeRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public MakerStudioConsent Consent { get; set; } = new MakerStudioConsent();
        public string? SourceRevision { get; set; }
        public double? SourceDurationSec { get; set; }
        public double? RenderDurationSec { get; set; }
        public double? TrimStartSec { get; set; }
        public double? TrimEndSec { get; set; }
        public bool EvenOutVolume { get; set; }
        public bool SourcePresent { get; set; }
        public bool RenderReady { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public string? SourceFingerprint { get; set; }
        public string? RenderFingerprint { get; set; }
        /// <summary>The source fingerprint the render was validated against; the render is ready only for that source.</summary>
        public string? RenderSourceFingerprint { get; set; }
        /// <summary>v2.4: <c>pack</c> when the take inherits the pack's voice default (v2.5: and follows its changes); otherwise <c>take</c>.</summary>
        public string ConsentSource { get; set; } = MakerStudioConsentSources.Take;

        public MakerStudioTake ToContract() => new MakerStudioTake
        {
            Id = Id,
            Origin = Origin,
            Consent = new MakerStudioConsent
            {
                Confirmed = Consent.Confirmed,
                StatementVersion = Consent.StatementVersion,
                ConfirmedAt = Consent.ConfirmedAt,
            },
            SourceRevision = SourceRevision,
            SourceDurationSec = SourceDurationSec,
            RenderDurationSec = RenderDurationSec,
            TrimStartSec = TrimStartSec,
            TrimEndSec = TrimEndSec,
            EvenOutVolume = EvenOutVolume,
            SourcePresent = SourcePresent,
            RenderReady = RenderReady,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ConsentSource = ConsentSource,
        };
    }

    public sealed class MakerHistoryRevision
    {
        public string RevisionId { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string? Text { get; set; }
        public string Source { get; set; } = string.Empty;
    }

    public sealed class MakerGlossaryDocument
    {
        public int Schema { get; set; } = MakerStudioContract.StorageSchema;
        public string Revision { get; set; } = string.Empty;
        public List<MakerStudioGlossaryEntry> Entries { get; } = new List<MakerStudioGlossaryEntry>();
    }

    public sealed class MakerOperationReceipt
    {
        public int Schema { get; set; } = 1;
        public string OperationId { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
        public bool Saved { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public JsonObject Response { get; set; } = new JsonObject();
    }

    internal static class MakerProjectJson
    {
        internal static string WriteHeader(MakerProjectHeader value) => MiniJson.Object(
            Pair("schema", MiniJson.Number(value.Schema)),
            Pair("projectId", MiniJson.String(value.ProjectId)));

        internal static string WriteLine(MakerLineDocument value)
        {
            var history = value.History.Select(h => MiniJson.Object(
                Pair("revisionId", MiniJson.String(h.RevisionId)),
                Pair("createdAt", MiniJson.String(h.CreatedAt)),
                Pair("text", MiniJson.String(h.Text)),
                Pair("source", MiniJson.String(h.Source))));
            var takes = value.Takes.Select(t => MiniJson.Object(TakeMembers(t)));
            return MiniJson.Object(
                Pair("schema", MiniJson.Number(value.Schema)),
                Pair("revision", MiniJson.String(value.Revision)),
                Pair("subtitleFingerprint", MiniJson.String(value.SubtitleFingerprint)),
                Pair("workflow", MiniJson.Object(
                    Pair("status", MiniJson.String(value.Workflow.Status)),
                    Pair("starred", MiniJson.Bool(value.Workflow.Starred)))),
                Pair("history", "[" + string.Join(",", history) + "]"),
                Pair("historyGap", MiniJson.Bool(value.HistoryGap)),
                Pair("favouriteTakeId", MiniJson.String(value.FavouriteTakeId)),
                Pair("publishedTakeId", MiniJson.String(value.PublishedTakeId)),
                Pair("publishedTakeRevision", MiniJson.String(value.PublishedTakeRevision)),
                Pair("publishedClip", MiniJson.String(value.PublishedClip)),
                Pair("takes", "[" + string.Join(",", takes) + "]"));
        }

        /// <summary>
        /// One stored take. <c>consentSource</c> is written only for an inherited take, so records of takes with their
        /// own consent stay byte-identical to v2.3 (older builds ignore the unknown member).
        /// </summary>
        private static List<KeyValuePair<string, string>> TakeMembers(MakerTakeRecord t)
        {
            var members = new List<KeyValuePair<string, string>>
            {
                Pair("id", MiniJson.String(t.Id)),
                Pair("origin", MiniJson.String(t.Origin)),
                Pair("consent", MiniJson.Object(
                    Pair("confirmed", MiniJson.Bool(t.Consent.Confirmed)),
                    Pair("statementVersion", MiniJson.String(t.Consent.StatementVersion)),
                    Pair("confirmedAt", MiniJson.String(t.Consent.ConfirmedAt)))),
                Pair("sourceRevision", MiniJson.String(t.SourceRevision)),
                Pair("sourceDurationSec", OptionalNumber(t.SourceDurationSec)),
                Pair("renderDurationSec", OptionalNumber(t.RenderDurationSec)),
                Pair("trimStartSec", OptionalNumber(t.TrimStartSec)),
                Pair("trimEndSec", OptionalNumber(t.TrimEndSec)),
                Pair("evenOutVolume", MiniJson.Bool(t.EvenOutVolume)),
                Pair("sourcePresent", MiniJson.Bool(t.SourcePresent)),
                Pair("renderReady", MiniJson.Bool(t.RenderReady)),
                Pair("createdAt", MiniJson.String(t.CreatedAt)),
                Pair("updatedAt", MiniJson.String(t.UpdatedAt)),
                Pair("sourceFingerprint", MiniJson.String(t.SourceFingerprint)),
                Pair("renderFingerprint", MiniJson.String(t.RenderFingerprint)),
                Pair("renderSourceFingerprint", MiniJson.String(t.RenderSourceFingerprint)),
            };
            if (string.Equals(t.ConsentSource, MakerStudioConsentSources.Pack, StringComparison.Ordinal))
                members.Add(Pair("consentSource", MiniJson.String(MakerStudioConsentSources.Pack)));
            return members;
        }

        internal static string WriteGlossary(MakerGlossaryDocument value)
        {
            var entries = value.Entries.Select(e => MiniJson.Object(
                Pair("id", MiniJson.String(e.Id)),
                Pair("sourceJa", MiniJson.String(e.SourceJa)),
                Pair("sourceEn", MiniJson.String(e.SourceEn)),
                Pair("target", MiniJson.String(e.Target)),
                Pair("note", MiniJson.String(e.Note))));
            return MiniJson.Object(
                Pair("schema", MiniJson.Number(value.Schema)),
                Pair("revision", MiniJson.String(value.Revision)),
                Pair("entries", "[" + string.Join(",", entries) + "]"));
        }

        internal static string WriteReceipt(MakerOperationReceipt value) => MiniJson.Object(
            Pair("schema", MiniJson.Number(value.Schema)),
            Pair("operationId", MiniJson.String(value.OperationId)),
            Pair("fingerprint", MiniJson.String(value.Fingerprint)),
            Pair("kind", MiniJson.String(value.Kind)),
            Pair("revision", MiniJson.String(value.Revision)),
            Pair("saved", MiniJson.Bool(value.Saved)),
            Pair("createdAt", MiniJson.String(value.CreatedAt)),
            Pair("response", WriteObject(value.Response)));

        internal static bool TryHeader(string text, out MakerProjectHeader value, out string problem)
        {
            value = new MakerProjectHeader();
            if (!TryObject(text, out var obj, out problem)
                || !TrySchema(obj!, out var schema, out problem)
                || !String(obj!, "projectId", false, out var id, out problem))
            {
                return false;
            }

            value.Schema = schema;
            value.ProjectId = id!;
            return true;
        }

        internal static bool TryLine(string text, out MakerLineDocument value, out string problem)
        {
            value = new MakerLineDocument();
            if (!TryObject(text, out var obj, out problem)
                || !TrySchema(obj!, out var schema, out problem)
                || !String(obj!, "revision", false, out var revision, out problem)
                || !String(obj!, "subtitleFingerprint", false, out var fingerprint, out problem)
                || !Object(obj!, "workflow", out var workflow, out problem)
                || !String(workflow!, "status", false, out var status, out problem)
                || !Boolean(workflow!, "starred", out var starred, out problem)
                || !Array(obj!, "history", out var history, out problem)
                || !Boolean(obj!, "historyGap", out var historyGap, out problem))
            {
                return false;
            }

            value.Schema = schema;
            value.Revision = revision!;
            value.SubtitleFingerprint = fingerprint!;
            value.Workflow = new MakerStudioWorkflow { Status = status!, Starred = starred };
            value.HistoryGap = historyGap;
            foreach (var node in history!)
            {
                if (!(node is JsonObject item)
                    || !String(item, "revisionId", false, out var revisionId, out problem)
                    || !String(item, "createdAt", false, out var createdAt, out problem)
                    || !String(item, "text", true, out var line, out problem)
                    || !String(item, "source", false, out var source, out problem))
                {
                    problem = "history entry: " + problem;
                    return false;
                }

                value.History.Add(new MakerHistoryRevision
                {
                    RevisionId = revisionId!, CreatedAt = createdAt!, Text = line, Source = source!,
                });
            }

            if (!OptionalString(obj!, "favouriteTakeId", out var favouriteTakeId, out problem)
                || !OptionalString(obj!, "publishedTakeId", out var publishedTakeId, out problem)
                || !OptionalString(obj!, "publishedTakeRevision", out var publishedTakeRevision, out problem)
                || !OptionalString(obj!, "publishedClip", out var publishedClip, out problem)
                || !TryTakes(obj!, value.Takes, out problem))
            {
                return false;
            }

            if ((favouriteTakeId != null && !MakerStudioPaths.IsTakeId(favouriteTakeId))
                || (publishedTakeId != null && !MakerStudioPaths.IsTakeId(publishedTakeId)))
            {
                problem = "take id must be 32 lowercase hexadecimal characters";
                return false;
            }

            value.FavouriteTakeId = favouriteTakeId;
            value.PublishedTakeId = publishedTakeId;
            value.PublishedTakeRevision = publishedTakeRevision;
            value.PublishedClip = publishedClip != null && Editor.MakerEditRules.IsClip(publishedClip) ? publishedClip : null;
            return true;
        }

        private static bool TryTakes(JsonObject obj, List<MakerTakeRecord> takes, out string problem)
        {
            problem = string.Empty;
            if (!obj.TryGetValue("takes", out var node))
            {
                return true;
            }

            if (!(node is IList<object?> list) || list.Count > MakerStudioContract.TakesPerLine)
            {
                problem = "takes must be an array of at most " + MakerStudioContract.TakesPerLine + " items";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in list)
            {
                if (!(element is JsonObject item)
                    || !String(item, "id", false, out var id, out problem)
                    || !MakerStudioPaths.IsTakeId(id)
                    || !seen.Add(id!)
                    || !String(item, "origin", false, out var origin, out problem)
                    || !MakerTakeRules.IsOrigin(origin)
                    || !Object(item, "consent", out var consent, out problem)
                    || !Boolean(consent!, "confirmed", out var confirmed, out problem)
                    || !String(consent!, "statementVersion", false, out var statementVersion, out problem)
                    || !String(consent!, "confirmedAt", true, out var confirmedAt, out problem)
                    || !OptionalString(item, "sourceRevision", out var sourceRevision, out problem)
                    || !OptionalDouble(item, "sourceDurationSec", out var sourceDuration, out problem)
                    || !OptionalDouble(item, "renderDurationSec", out var renderDuration, out problem)
                    || !OptionalDouble(item, "trimStartSec", out var trimStart, out problem)
                    || !OptionalDouble(item, "trimEndSec", out var trimEnd, out problem)
                    || !Boolean(item, "evenOutVolume", out var evenOutVolume, out problem)
                    || !Boolean(item, "sourcePresent", out var sourcePresent, out problem)
                    || !Boolean(item, "renderReady", out var renderReady, out problem)
                    || !String(item, "createdAt", false, out var createdAt, out problem)
                    || !String(item, "updatedAt", false, out var updatedAt, out problem)
                    || !OptionalString(item, "sourceFingerprint", out var sourceFingerprint, out problem)
                    || !OptionalString(item, "renderFingerprint", out var renderFingerprint, out problem)
                    || !OptionalString(item, "renderSourceFingerprint", out var renderSourceFingerprint, out problem))
                {
                    problem = string.IsNullOrEmpty(problem) ? "invalid take entry" : "take entry: " + problem;
                    return false;
                }

                takes.Add(new MakerTakeRecord
                {
                    Id = id!,
                    Origin = origin!,
                    Consent = new MakerStudioConsent
                    {
                        Confirmed = confirmed,
                        StatementVersion = statementVersion!,
                        ConfirmedAt = confirmedAt,
                    },
                    SourceRevision = sourceRevision,
                    SourceDurationSec = sourceDuration,
                    RenderDurationSec = renderDuration,
                    TrimStartSec = trimStart,
                    TrimEndSec = trimEnd,
                    EvenOutVolume = evenOutVolume,
                    SourcePresent = sourcePresent,
                    RenderReady = renderReady,
                    CreatedAt = createdAt!,
                    UpdatedAt = updatedAt!,
                    SourceFingerprint = sourceFingerprint,
                    RenderFingerprint = renderFingerprint,
                    // M5c records had no link: their ready render was uploaded for the source they recorded.
                    RenderSourceFingerprint = item.ContainsKey("renderSourceFingerprint") ? renderSourceFingerprint
                        : renderReady ? sourceFingerprint : null,
                    // v2.4, optional: only "pack" marks an inherited take; absent or anything else is the take's own consent.
                    ConsentSource = item.TryGetValue("consentSource", out var consentSource)
                        && string.Equals(consentSource as string, MakerStudioConsentSources.Pack, StringComparison.Ordinal)
                        ? MakerStudioConsentSources.Pack : MakerStudioConsentSources.Take,
                });
            }

            return true;
        }

        internal static bool TryGlossary(string text, out MakerGlossaryDocument value, out string problem)
        {
            value = new MakerGlossaryDocument();
            if (!TryObject(text, out var obj, out problem)
                || !TrySchema(obj!, out var schema, out problem)
                || !String(obj!, "revision", false, out var revision, out problem)
                || !Array(obj!, "entries", out var entries, out problem))
            {
                return false;
            }

            value.Schema = schema;
            value.Revision = revision!;
            foreach (var node in entries!)
            {
                if (!(node is JsonObject item)
                    || !String(item, "id", false, out var id, out problem)
                    || !String(item, "sourceJa", true, out var ja, out problem)
                    || !String(item, "sourceEn", true, out var en, out problem)
                    || !String(item, "target", false, out var target, out problem)
                    || !String(item, "note", true, out var note, out problem))
                {
                    problem = "glossary entry: " + problem;
                    return false;
                }

                value.Entries.Add(new MakerStudioGlossaryEntry { Id = id!, SourceJa = ja, SourceEn = en, Target = target!, Note = note });
            }

            return true;
        }

        internal static bool TryReceipt(string text, out MakerOperationReceipt value, out string problem)
        {
            value = new MakerOperationReceipt();
            if (!TryObject(text, out var obj, out problem)
                || !TrySchema(obj!, out var schema, out problem)
                || !String(obj!, "operationId", false, out var operationId, out problem)
                || !String(obj!, "fingerprint", false, out var fingerprint, out problem)
                || !String(obj!, "kind", false, out var kind, out problem)
                || !String(obj!, "revision", false, out var revision, out problem)
                || !Boolean(obj!, "saved", out var saved, out problem)
                || !String(obj!, "createdAt", false, out var createdAt, out problem)
                || !Object(obj!, "response", out var response, out problem))
            {
                return false;
            }

            value.Schema = schema;
            value.OperationId = operationId!;
            value.Fingerprint = fingerprint!;
            value.Kind = kind!;
            value.Revision = revision!;
            value.Saved = saved;
            value.CreatedAt = createdAt!;
            value.Response = response!;
            return true;
        }

        internal static string WriteObject(JsonObject obj)
        {
            var members = new List<KeyValuePair<string, string>>();
            foreach (var pair in obj)
            {
                members.Add(Pair(pair.Key, WriteValue(pair.Value)));
            }

            return MiniJson.Object(members);
        }

        private static string WriteValue(object? value) => value switch
        {
            null => "null",
            string s => MiniJson.String(s),
            bool b => MiniJson.Bool(b),
            long n => MiniJson.Number(n),
            int n => MiniJson.Number(n),
            double n => MiniJson.Number(n),
            JsonObject obj => WriteObject(obj),
            IList<object?> list => "[" + string.Join(",", list.Select(WriteValue)) + "]",
            _ => throw new ArgumentException("unsupported JSON value " + value.GetType().Name),
        };

        private static bool TryObject(string text, out JsonObject? value, out string problem)
        {
            value = null;
            if (!JsonReader.TryParse(text, out var parsed, out var error) || !(parsed is JsonObject obj))
            {
                problem = error ?? "the top-level value is not an object";
                return false;
            }

            value = obj;
            problem = string.Empty;
            return true;
        }

        private static bool TrySchema(JsonObject obj, out int schema, out string problem)
        {
            schema = 0;
            if (!obj.TryGetValue("schema", out var node) || !(node is long raw) || raw < 1 || raw > int.MaxValue)
            {
                problem = "schema must be a positive whole number";
                return false;
            }

            schema = (int)raw;
            if (schema != MakerStudioContract.StorageSchema)
            {
                problem = "storage schema " + schema + " is not supported (expected " + MakerStudioContract.StorageSchema + ")";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static bool String(JsonObject obj, string key, bool nullable, out string? value, out string problem)
        {
            value = null;
            if (!obj.TryGetValue(key, out var node) || (node is null && !nullable) || (node != null && !(node is string)))
            {
                problem = key + " must be " + (nullable ? "text or null" : "text");
                return false;
            }

            value = node as string;
            problem = string.Empty;
            return true;
        }

        private static bool OptionalString(JsonObject obj, string key, out string? value, out string problem)
        {
            value = null;
            problem = string.Empty;
            if (!obj.TryGetValue(key, out var node) || node is null)
            {
                return true;
            }

            if (!(node is string text))
            {
                problem = key + " must be text or null";
                return false;
            }

            value = text;
            return true;
        }

        private static bool OptionalDouble(JsonObject obj, string key, out double? value, out string problem)
        {
            value = null;
            problem = string.Empty;
            if (!obj.TryGetValue(key, out var node) || node is null)
            {
                return true;
            }

            double number;
            if (node is double d)
            {
                number = d;
            }
            else if (node is long l)
            {
                number = l;
            }
            else
            {
                problem = key + " must be a number or null";
                return false;
            }

            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                problem = key + " must be a finite number";
                return false;
            }

            value = number;
            return true;
        }

        private static bool Boolean(JsonObject obj, string key, out bool value, out string problem)
        {
            value = false;
            if (!obj.TryGetValue(key, out var node) || !(node is bool found))
            {
                problem = key + " must be true or false";
                return false;
            }

            value = found;
            problem = string.Empty;
            return true;
        }

        private static bool Object(JsonObject obj, string key, out JsonObject? value, out string problem)
        {
            value = null;
            if (!obj.TryGetValue(key, out var node) || !(node is JsonObject found))
            {
                problem = key + " must be an object";
                return false;
            }

            value = found;
            problem = string.Empty;
            return true;
        }

        private static bool Array(JsonObject obj, string key, out IList<object?>? value, out string problem)
        {
            value = null;
            if (!obj.TryGetValue(key, out var node) || !(node is IList<object?> found))
            {
                problem = key + " must be an array";
                return false;
            }

            value = found;
            problem = string.Empty;
            return true;
        }

        private static string OptionalNumber(double? value) =>
            value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "null";

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    }

    internal static class MakerProjectRevision
    {
        internal static string New() => Guid.NewGuid().ToString("N");

        internal static string StableId(string value)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
            return string.Concat(hash.Take(16).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        internal static string Initial(string kind, string path, string fingerprint) =>
            "initial-" + MakerPackFiles.Fingerprint(System.Text.Encoding.UTF8.GetBytes(kind + "\n" + path + "\n" + fingerprint));

        internal static string At(DateTimeOffset when) => when.ToString("O", CultureInfo.InvariantCulture);
    }
}
