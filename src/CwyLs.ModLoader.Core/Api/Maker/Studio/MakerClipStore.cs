using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>One file Studio publication moved aside, and where it went.</summary>
    public sealed class MakerClipBackup
    {
        public string Original { get; set; } = string.Empty;
        public string Backup { get; set; } = string.Empty;
    }

    /// <summary>
    /// The Studio publication of one (language, clip) in <c>.maker/clips/&lt;lang&gt;/&lt;clip&gt;.json</c>. Canonical per clip:
    /// every line sharing the clip (an alias) sees the same publication and the same clip revision.
    /// </summary>
    public sealed class MakerClipRecord
    {
        public string Lang { get; set; } = string.Empty;
        public string Clip { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
        public string LineId { get; set; } = string.Empty;
        public string TakeId { get; set; } = string.Empty;
        public string TakeRevision { get; set; } = string.Empty;
        /// <summary>The pack-relative runtime WAV the publication wrote.</summary>
        public string Path { get; set; } = string.Empty;
        public string WavFingerprint { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string PublishedAt { get; set; } = string.Empty;
        /// <summary>Files that were in the clip's place before Studio first published it; unpublish puts them back.</summary>
        public List<MakerClipBackup> Backups { get; } = new List<MakerClipBackup>();
    }

    /// <summary>What a pack has for one (language, clip) right now.</summary>
    public sealed class MakerClipState
    {
        public string Lang { get; set; } = string.Empty;
        public string Clip { get; set; } = string.Empty;
        /// <summary>
        /// The clip revision (CONTRACT-v2 §3): the publication's own revision while it is intact, otherwise derived from
        /// the files on disk, so a v1 upload or an edit outside Studio advances it deterministically.
        /// </summary>
        public string Revision { get; set; } = string.Empty;
        /// <summary>The intact Studio publication, or null (nothing published, or the runtime file changed outside Studio).</summary>
        public MakerClipRecord? Publication { get; set; }
        /// <summary>A record file exists, intact or not (unpublish removes a stale one).</summary>
        public bool RecordPresent { get; set; }
        /// <summary>The selected pack's runtime voice files for the clip (pack-relative, ordinal order).</summary>
        public IReadOnlyList<string> VoiceFiles { get; set; } = new string[0];
    }

    /// <summary>Reads per-(language, clip) publication state; writes happen only inside publish/unpublish commits.</summary>
    public sealed class MakerClipStore
    {
        private readonly MakerProjectStore _store;

        public MakerClipStore(MakerProjectStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

        /// <summary>
        /// The clip's state. Cheap enough for every row of a catalogue page: it stats the runtime voice files (the derived
        /// revision uses size and write time; only a published file is hashed, once per change) and reads one small record.
        /// Shared-clip aliases call it with the same clip and agree; the catalogue, the take page and publish all use it.
        /// </summary>
        public MakerClipState Read(string lang, string clip) => Read(lang, clip, _store.Files.VoiceFiles(lang, clip));

        /// <summary>
        /// <see cref="Read(string, string)"/> with the clip's runtime voice files already known (from
        /// <see cref="Editor.MakerPackFiles.VoiceIndex"/>, one folder listing for a whole catalogue page).
        /// </summary>
        /// <param name="lang">The language.</param>
        /// <param name="clip">The clip.</param>
        /// <param name="files">What <see cref="Editor.MakerPackFiles.VoiceFiles"/> would return for it.</param>
        public MakerClipState Read(string lang, string clip, IReadOnlyList<string> files)
        {
            var recordPath = MakerStudioPaths.Clip(lang, clip);
            var text = _store.ReadTextSnapshot(recordPath, out _, out _);
            var record = text != null && TryRecord(text, out var parsed) && parsed.Lang == lang && parsed.Clip == clip ? parsed : null;
            var intact = record != null && files.Count == 1 && string.Equals(files[0], record.Path, StringComparison.Ordinal)
                && string.Equals(_store.FileStamp(record.Path), record.WavFingerprint, StringComparison.Ordinal);
            var state = new MakerClipState { Lang = lang, Clip = clip, RecordPresent = text != null, VoiceFiles = files, Publication = intact ? record : null };
            state.Revision = intact
                ? record!.Revision
                : MakerProjectRevision.StableId(string.Join("\n", new[] { "clip", lang, clip, text is null ? "<none>" : Hash(text) }
                    .Concat(files.Select(f => f + "=" + Stamp(f)))));
            return state;
        }

        internal static byte[] Write(MakerClipRecord r) => MakerProjectStore.Utf8(ApiJson.Write(new JsonObject
        {
            ["schema"] = 1, ["lang"] = r.Lang, ["clip"] = r.Clip, ["revision"] = r.Revision, ["lineId"] = r.LineId,
            ["takeId"] = r.TakeId, ["takeRevision"] = r.TakeRevision, ["path"] = r.Path, ["wavFingerprint"] = r.WavFingerprint,
            ["sha256"] = r.Sha256, ["publishedAt"] = r.PublishedAt,
            ["backups"] = r.Backups.Select(b => (object?)new JsonObject { ["original"] = b.Original, ["backup"] = b.Backup }).ToList(),
        }));

        internal static bool TryRecord(string text, out MakerClipRecord record)
        {
            record = new MakerClipRecord();
            if (!JsonReader.TryParse(text, out var parsed, out _) || !(parsed is JsonObject o)
                || !(o.TryGetValue("schema", out var schema) && schema is long version && version == 1)
                || !(o.TryGetValue("backups", out var node) && node is IList<object?> backups))
                return false;
            string? Get(string key) => o.TryGetValue(key, out var v) ? v as string : null;
            if (!(Get("lang") is string lang) || !(Get("clip") is string clip) || !(Get("revision") is string revision)
                || !(Get("lineId") is string lineId) || !(Get("takeId") is string takeId) || !(Get("takeRevision") is string takeRevision)
                || !(Get("path") is string path) || !MakerStudioPaths.IsVoicePublicationPath(path) || path.StartsWith("_", StringComparison.Ordinal)
                || !(Get("wavFingerprint") is string fingerprint) || !(Get("sha256") is string sha) || !(Get("publishedAt") is string published))
                return false;
            record = new MakerClipRecord
            {
                Lang = lang, Clip = clip, Revision = revision, LineId = lineId, TakeId = takeId, TakeRevision = takeRevision,
                Path = path, WavFingerprint = fingerprint, Sha256 = sha, PublishedAt = published,
            };
            foreach (var item in backups)
            {
                if (!(item is JsonObject b) || !(b.TryGetValue("original", out var original) && original is string from)
                    || !(b.TryGetValue("backup", out var copy) && copy is string to)
                    || !MakerStudioPaths.IsVoicePublicationPath(from) || !MakerStudioPaths.IsVoicePublicationPath(to))
                    return false;
                record.Backups.Add(new MakerClipBackup { Original = from, Backup = to });
            }

            return true;
        }

        private string Stamp(string relativePath)
        {
            var info = new FileInfo(_store.Files.Resolve(relativePath));
            return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "<gone>";
        }

        private static string Hash(string text) => Editor.MakerPackFiles.Fingerprint(MakerProjectStore.Utf8(text));
    }
}
