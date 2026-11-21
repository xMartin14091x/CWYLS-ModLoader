using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>The files an export ships: everything in the pack folder except creator-only data and links.</summary>
    public sealed class MakerPackFileSet
    {
        /// <summary>Pack-relative paths with '/' and their sizes, ordinally sorted.</summary>
        public List<KeyValuePair<string, long>> Included { get; } = new List<KeyValuePair<string, long>>();
        public int Excluded { get; set; }
        public long ExcludedBytes { get; set; }
        /// <summary>Symbolic links (files or folders) that were skipped.</summary>
        public List<string> Links { get; } = new List<string>();
        public long IncludedBytes => Included.Sum(p => p.Value);

        /// <summary>
        /// Walks <paramref name="root"/> without following links. Excluded: every segment that starts with a dot
        /// (<c>.maker/</c> with take sources and renders, hidden temp files, <c>.DS_Store</c>), the top
        /// <c>_maker_backup/</c>, and <c>Thumbs.db</c> / <c>desktop.ini</c>.
        /// </summary>
        public static MakerPackFileSet Walk(string root)
        {
            var set = new MakerPackFileSet();
            Visit(set, root, string.Empty, excluded: false);
            set.Included.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return set;
        }

        /// <summary>True when a pack-relative path never ships in an export.</summary>
        public static bool IsCreatorOnly(string relative)
        {
            var parts = relative.Split('/');
            if (parts.Length > 1 && string.Equals(parts[0], MakerEditRules.BackupFolder, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var part in parts)
            {
                if (part.Length == 0 || part[0] == '.') return true;
            }

            var last = parts[parts.Length - 1];
            return string.Equals(last, "Thumbs.db", StringComparison.OrdinalIgnoreCase) || string.Equals(last, "desktop.ini", StringComparison.OrdinalIgnoreCase);
        }

        private static void Visit(MakerPackFileSet set, string folder, string prefix, bool excluded)
        {
            foreach (var dir in Directory.GetDirectories(folder))
            {
                var rel = prefix + Path.GetFileName(dir);
                if (IsLink(dir))
                {
                    if (!excluded && !IsCreatorOnly(rel)) set.Links.Add(rel + "/");
                    continue;
                }

                Visit(set, dir, rel + "/", excluded || IsCreatorOnly(rel + "/x"));
            }

            foreach (var file in Directory.GetFiles(folder))
            {
                var rel = prefix + Path.GetFileName(file);
                if (IsLink(file))
                {
                    if (!excluded && !IsCreatorOnly(rel)) set.Links.Add(rel);
                    continue;
                }

                var size = new FileInfo(file).Length;
                if (excluded || IsCreatorOnly(rel))
                {
                    set.Excluded++;
                    set.ExcludedBytes += size;
                }
                else
                {
                    set.Included.Add(new KeyValuePair<string, long>(rel, size));
                }
            }
        }

        private static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (IOException) { return true; }
        }
    }

    /// <summary>The export checks (contract v2.3), version arithmetic and <c>CHANGELOG.md</c> handling. Pure file reads.</summary>
    public static class MakerPackChecks
    {
        public const string ChangelogFile = "CHANGELOG.md";
        public const string IconFile = "icon.png";
        public const long MaxExportBytes = 2L * 1024 * 1024 * 1024 - 64L * 1024 * 1024;
        public const long MaxDownloadBytes = 256L * 1024 * 1024;
        public const long MaxFileBytes = 512L * 1024 * 1024;
        public const int MaxNotes = 4000;
        internal const long MaxMetadataBytes = 4L * 1024 * 1024;
        private const int MaxPaths = 20;

        /// <summary>Runs every check on the pack folder.</summary>
        /// <param name="files">The pack folder's root.</param>
        /// <param name="packId">The route's pack id; the manifest must name the same.</param>
        /// <param name="set">The walked file set.</param>
        /// <param name="progress">Per language: the catalogue's overall progress, or null when it is not available now.</param>
        public static List<MakerExportCheck> Run(MakerPackFiles files, string packId, MakerPackFileSet set,
            IReadOnlyDictionary<string, MakerStudioProgress?>? progress, out PackManifest? manifest) =>
            Run(files, packId, set, progress, null, out manifest);

        /// <summary>Runs every check on the pack folder, including the paid DLC check.</summary>
        /// <param name="files">The pack folder's root.</param>
        /// <param name="packId">The route's pack id; the manifest must name the same.</param>
        /// <param name="set">The walked file set.</param>
        /// <param name="progress">Per language: the catalogue's overall progress, or null when it is not available now.</param>
        /// <param name="paid">
        /// What the DLC gate hides now; null when no gate is wired (no check). A pack with content for a hidden line or clip, or an
        /// unknown verdict, adds an error check <c>paid</c> (export refused); otherwise no <c>paid</c> check is listed.
        /// </param>
        /// <param name="manifest">The parsed manifest, or null.</param>
        public static List<MakerExportCheck> Run(MakerPackFiles files, string packId, MakerPackFileSet set,
            IReadOnlyDictionary<string, MakerStudioProgress?>? progress, MakerDlcHidden? paid, out PackManifest? manifest)
        {
            // Refuse unsupported sizes and ZIP widths before any whole-file parser can allocate.
            var size = Size(set, packId);
            if (size.Severity == MakerExportSeverity.Error)
            {
                manifest = null;
                return new List<MakerExportCheck>
                {
                    C(Make("manifest", MakerExportSeverity.Warning, "Not checked until the size limits pass.", Array.Empty<string>()), "notChecked"),
                    C(Make("subtitles", MakerExportSeverity.Warning, "Not checked until the size limits pass.", Array.Empty<string>()), "notChecked"),
                    C(Make("audio", MakerExportSeverity.Warning, "Not checked until the size limits pass.", Array.Empty<string>()), "notChecked"),
                    C(Make("provenance", MakerExportSeverity.Warning, "Not checked until the size limits pass.", Array.Empty<string>()), "notChecked"),
                    size, Progress(progress), C(Ok("links", "Links are never included."), "linksNever"),
                };
            }
            var checks = new List<MakerExportCheck>
            {
                Manifest(files, packId, out manifest),
                Subtitles(files, set),
                Audio(files, set),
                Provenance(files, set),
                size,
                Progress(progress),
            };
            checks.Add(set.Links.Count == 0
                ? C(Ok("links", "No links inside the pack."), "linksOk")
                : C(Make("links", MakerExportSeverity.Warning, set.Links.Count + " link(s) are skipped; copy the real files into the pack instead.", set.Links), "linksSkipped", ("n", set.Links.Count)));
            var dlc = Paid(files, set, paid);
            if (dlc != null) checks.Add(dlc);
            return checks;
        }

        /// <summary>
        /// Paid DLC content the player is not known to own is never exported: subtitle entries and <c>localization.json</c> rows for
        /// hidden lines, and voice files for hidden clips, refuse the export (paths only, never text). Null: nothing to report.
        /// </summary>
        private static MakerExportCheck? Paid(MakerPackFiles files, MakerPackFileSet set, MakerDlcHidden? hidden)
        {
            if (hidden is null) return null;
            if (!hidden.IsKnown)
                return C(Make("paid", MakerExportSeverity.Error, "Pack Maker could not check the pack for paid DLC lines (" + hidden.Summary + "); try again in a moment.", Array.Empty<string>()), "paidUnknown", ("detail", hidden.Summary));
            if (hidden.LineCount == 0 && hidden.VoiceCount == 0) return null;
            var hits = new List<string>();
            foreach (var pair in set.Included)
            {
                var lang = SubtitleTables.LanguageOf(pair.Key);
                if (lang != null)
                {
                    if (SubtitleTables.Parse(ReadMetadata(files, pair.Key, out _), pair.Key, lang).Rows.Any(r => hidden.IsLine(r.LocalizationId))) hits.Add(pair.Key);
                }
                else if (pair.Key == PackContentIndex.LocalizationPath)
                {
                    if (MasterDataPackParser.ParseLocalization(ReadMetadata(files, pair.Key, out _), pair.Key).Rows.Any(r => hidden.IsLine(r.Id))) hits.Add(pair.Key);
                }
                else if (pair.Key.StartsWith(PackContentIndex.VoiceFolder, StringComparison.Ordinal)
                         && hidden.IsVoice(Path.GetFileNameWithoutExtension(pair.Key)))
                {
                    hits.Add(pair.Key);
                }
            }

            return hits.Count == 0 ? null
                : C(Make("paid", MakerExportSeverity.Error, hits.Count + " file(s) have content for paid DLC lines that Pack Maker cannot confirm you own, so the pack is not exported.", hits), "paidFound", ("n", hits.Count));
        }

        /// <summary>The languages the pack has subtitles or dubs for, ordinally sorted.</summary>
        public static List<string> Languages(MakerPackFileSet set)
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var pair in set.Included)
            {
                var lang = SubtitleTables.LanguageOf(pair.Key);
                if (lang != null) found.Add(lang);
                else if (VoiceLanguage(pair.Key) is string voice) found.Add(voice);
            }

            return found.ToList();
        }

        public static bool TryBump(string current, string bump, out string next)
        {
            next = current;
            if (!SemVer.TryParse(current, out var v) || v is null) return false;
            switch (bump)
            {
                case "none": return true;
                case "patch": next = v.IsPrerelease ? Core(v.Major, v.Minor, v.Patch) : Core(v.Major, v.Minor, v.Patch + 1); return true;
                case "minor": next = Core(v.Major, v.Minor + 1, 0); return true;
                case "major": next = Core(v.Major + 1, 0, 0); return true;
                default: return false;
            }
        }

        /// <summary>Replaces the top-level <c>"version"</c> string of a manifest, keeping every other byte.</summary>
        public static bool TrySetVersion(string json, string version, out string updated)
        {
            updated = json;
            var depth = 0;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '"')
                {
                    var end = StringEnd(json, i);
                    if (end < 0) return false;
                    if (depth == 1)
                    {
                        var j = SkipSpace(json, end + 1);
                        if (j < json.Length && json[j] == ':' && json.Substring(i + 1, end - i - 1) == "version")
                        {
                            j = SkipSpace(json, j + 1);
                            if (j >= json.Length || json[j] != '"') return false;
                            var valueEnd = StringEnd(json, j);
                            if (valueEnd < 0) return false;
                            updated = json.Substring(0, j) + MiniJson.String(version) + json.Substring(valueEnd + 1);
                            return true;
                        }
                    }

                    i = end;
                }
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }

            return false;
        }

        /// <summary>The newest section of a changelog: <c>## version (date)</c> and its text.</summary>
        public static MakerChangelogEntry? TopEntry(string? changelog)
        {
            if (string.IsNullOrEmpty(changelog)) return null;
            var text = changelog!.Replace("\r\n", "\n");
            var start = SectionStart(text, 0);
            if (start < 0) return null;
            var lineEnd = text.IndexOf('\n', start);
            var heading = (lineEnd < 0 ? text.Substring(start + 3) : text.Substring(start + 3, lineEnd - start - 3)).Trim();
            var space = heading.IndexOf(' ');
            var version = space < 0 ? heading : heading.Substring(0, space);
            var next = lineEnd < 0 ? -1 : SectionStart(text, lineEnd + 1);
            var body = lineEnd < 0 ? string.Empty : (next < 0 ? text.Substring(lineEnd + 1) : text.Substring(lineEnd + 1, next - lineEnd - 1));
            return new MakerChangelogEntry { Version = version, Notes = body.Trim() };
        }

        /// <summary>The changelog with a section for <paramref name="version"/> on top (an existing top section for it is replaced).</summary>
        public static string WithEntry(string? changelog, string version, string date, string notes)
        {
            var section = "## " + version + " (" + date + ")\n\n" + notes.Replace("\r\n", "\n").Trim() + "\n";
            var text = string.IsNullOrWhiteSpace(changelog) ? "# Changelog\n" : changelog!.Replace("\r\n", "\n");
            var start = SectionStart(text, 0);
            if (start < 0) return text.TrimEnd('\n') + "\n\n" + section;
            var top = TopEntry(text);
            var rest = text.Substring(start);
            if (top != null && top.Version == version)
            {
                var lineEnd = text.IndexOf('\n', start);
                var next = lineEnd < 0 ? -1 : SectionStart(text, lineEnd + 1);
                rest = next < 0 ? string.Empty : text.Substring(next);
            }

            return text.Substring(0, start) + section + (rest.Length == 0 ? string.Empty : "\n" + rest);
        }

        /// <summary>Null when the notes are acceptable, otherwise the problem.</summary>
        public static string? NotesProblem(string? notes)
        {
            if (notes is null) return null;
            if (!MakerEditRules.TryCodePointCount(notes, out var count)) return "notes contain an unpaired Unicode surrogate";
            if (count > MaxNotes) return "notes may be at most " + MaxNotes + " characters";
            foreach (var c in notes)
            {
                if ((c < ' ' && c != '\n' && c != '\r' && c != '\t') || c == '\u007f') return "notes must not contain control characters";
            }

            return null;
        }

        /// <summary>Width and height of a PNG from its IHDR chunk, or false when the bytes are not a PNG.</summary>
        public static bool TryPngSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (png is null || png.Length < 33) return false;
            for (var i = 0; i < signature.Length; i++) if (png[i] != signature[i]) return false;
            if (BigEndian(png, 8) != 13 || Encoding.ASCII.GetString(png, 12, 4) != "IHDR") return false;
            width = BigEndian(png, 16);
            height = BigEndian(png, 20);
            return width > 0 && height > 0;
        }

        private static MakerExportCheck Manifest(MakerPackFiles files, string packId, out PackManifest? manifest)
        {
            manifest = null;
            var text = ReadMetadata(files, PackContentIndex.ManifestFileName, out _);
            if (text is null) return C(Make("manifest", MakerExportSeverity.Error, "manifest.json is missing.", new[] { PackContentIndex.ManifestFileName }), "manifestMissing");
            if (!PackManifestParser.TryParse(text, out manifest, out var problem) || manifest is null)
                return C(Make("manifest", MakerExportSeverity.Error, "manifest.json is not valid: " + problem, new[] { PackContentIndex.ManifestFileName }), "manifestInvalid", ("detail", problem));
            if (!string.Equals(manifest.Id, packId, StringComparison.OrdinalIgnoreCase))
                return C(Make("manifest", MakerExportSeverity.Error, "manifest.json names pack '" + manifest.Id + "', not '" + packId + "'.", new[] { PackContentIndex.ManifestFileName }), "manifestId", ("a", manifest.Id), ("b", packId));
            return C(Ok("manifest", manifest.Name + " " + manifest.Version + "."), "manifestOk", ("name", manifest.Name), ("version", manifest.Version.ToString()));
        }

        private static MakerExportCheck Subtitles(MakerPackFiles files, MakerPackFileSet set)
        {
            var bad = new List<string>();
            var empty = new List<string>();
            var tables = 0;
            var entries = 0;
            foreach (var pair in set.Included)
            {
                var lang = SubtitleTables.LanguageOf(pair.Key);
                if (lang is null) continue;
                tables++;
                var parse = SubtitleTables.Parse(ReadMetadata(files, pair.Key, out _), pair.Key, lang);
                if (!parse.Clean) bad.Add(pair.Key);
                else if (parse.Rows.Count == 0) empty.Add(pair.Key);
                entries += parse.Rows.Count;
            }

            if (bad.Count > 0) return C(Make("subtitles", MakerExportSeverity.Error, bad.Count + " subtitle table(s) have entries the game refuses.", bad), "subBad", ("n", bad.Count));
            if (empty.Count > 0) return C(Make("subtitles", MakerExportSeverity.Warning, empty.Count + " subtitle table(s) are empty.", empty), "subEmpty", ("n", empty.Count));
            return tables == 0 ? C(Ok("subtitles", "No subtitle tables.", entries), "subNone")
                : C(Ok("subtitles", entries + " subtitle(s) in " + tables + " table(s).", entries), "subCount", ("n", entries), ("m", tables));
        }

        private static MakerExportCheck Audio(MakerPackFiles files, MakerPackFileSet set)
        {
            var broken = new List<string>();
            var odd = new List<string>();
            var count = 0;
            foreach (var pair in set.Included)
            {
                if (!pair.Key.StartsWith(PackContentIndex.VoiceFolder, StringComparison.Ordinal)) continue;
                var ext = Path.GetExtension(pair.Key).ToLowerInvariant();
                if (VoiceLanguage(pair.Key) is null || (ext != ".wav" && ext != ".ogg"))
                {
                    odd.Add(pair.Key);
                    continue;
                }

                count++;
                if (pair.Value == 0 || pair.Value > MaxFileBytes) { broken.Add(pair.Key); continue; }
                if (ext == ".wav")
                {
                    // The strict request parser has a 16 MiB WAV limit. Larger runtime files are
                    // already warnings; do not allocate a 512 MiB array just to obtain that verdict.
                    if (pair.Value > MakerEditRules.MaxVoiceBytes) { odd.Add(pair.Key); continue; }
                    var verdict = MakerWavCheck.Check(files.ReadBytes(pair.Key), out _, out _);
                    if (verdict == MakerWavVerdict.Unsupported) broken.Add(pair.Key);
                    else if (verdict == MakerWavVerdict.TooLong) odd.Add(pair.Key);
                }
                else
                {
                    using var stream = File.OpenRead(files.Resolve(pair.Key));
                    if (!MakerAudioLength.TryOggVorbis(stream, out _)) broken.Add(pair.Key);
                }
            }

            if (broken.Count > 0) return C(Make("audio", MakerExportSeverity.Error, broken.Count + " voice file(s) are empty, broken or in a format the game cannot play.", broken), "audioBad", ("n", broken.Count));
            if (odd.Count > 0) return C(Make("audio", MakerExportSeverity.Warning, odd.Count + " file(s) under audio/voice/ are ignored by the game or longer than 60 s.", odd), "audioOdd", ("n", odd.Count));
            return C(Ok("audio", count + " voice file(s).", count), "audioCount", ("n", count));
        }

        /// <summary>
        /// D-148: every shipped dub needs a valid voice label. Errors, in order: an unreadable file, unlabelled dubs (no entry, an
        /// unknown origin, no current confirmation, or other bytes; fixed by one click with the pack default), labels whose audio
        /// file is gone, two files for one clip, Studio publications whose take lost its consent, Studio publications to publish again.
        /// </summary>
        private static MakerExportCheck Provenance(MakerPackFiles files, MakerPackFileSet set)
        {
            var text = ReadMetadata(files, MakerStudioPaths.Provenance, out _);
            var entries = new List<MakerProvenanceEntry>();
            if (text != null && !MakerVoiceProvenance.TryRead(text, out entries, out var problem))
                return C(Make("provenance", MakerExportSeverity.Error, "voice-provenance.json is not valid: " + problem, new[] { MakerStudioPaths.Provenance }), "provInvalid", ("detail", problem));

            var scan = MakerVoiceLabels.Scan(files, set, entries);
            List<string> Paths(MakerDubLabelState state) => scan.In(state).Select(d => d.Path).ToList();
            var unlabelled = Paths(MakerDubLabelState.Unlabelled);
            if (unlabelled.Count > 0)
                return C(Make("provenance", MakerExportSeverity.Error, unlabelled.Count + " dub(s) have no valid voice label; label them with the pack default.", unlabelled), "provUnlabelled", ("n", unlabelled.Count));
            if (scan.MissingAudio.Count > 0) return C(Make("provenance", MakerExportSeverity.Error, scan.MissingAudio.Count + " published dub(s) are missing their audio file.", scan.MissingAudio), "provAudio", ("n", scan.MissingAudio.Count));
            var duplicate = Paths(MakerDubLabelState.Duplicate);
            if (duplicate.Count > 0)
                return C(Make("provenance", MakerExportSeverity.Error, duplicate.Count + " dub file(s) duplicate another file for the same clip; keep one file per clip.", duplicate), "provDuplicate", ("n", duplicate.Count));
            var unconfirmed = Paths(MakerDubLabelState.TakeConsent);
            if (unconfirmed.Count > 0) return C(Make("provenance", MakerExportSeverity.Error, unconfirmed.Count + " published dub(s) have no confirmed voice statement for their origin; confirm it for the published take.", unconfirmed), "provConsent", ("n", unconfirmed.Count));
            var changed = Paths(MakerDubLabelState.Republish);
            if (changed.Count > 0) return C(Make("provenance", MakerExportSeverity.Error, changed.Count + " dub(s) changed after they were published; publish them again.", changed), "provChanged", ("n", changed.Count));
            return scan.Dubs.Count == 0
                ? C(Ok("provenance", "No dubs, so no voice disclosure is needed."), "provNone")
                : C(Ok("provenance", scan.Dubs.Count + " dub(s) disclosed.", scan.Dubs.Count), "provCount", ("n", scan.Dubs.Count));
        }

        private static MakerExportCheck Size(MakerPackFileSet set, string packId)
        {
            var bytes = set.IncludedBytes;
            if (set.Included.Count > MakerZipWriter.MaxEntries)
                return C(Make("size", MakerExportSeverity.Error, "A non-ZIP64 export may contain at most 65535 files.", Array.Empty<string>()), "sizeEntries", ("n", set.Included.Count));
            var wide = set.Included.Where(p => Encoding.UTF8.GetByteCount(packId + "/" + p.Key) > ushort.MaxValue).Select(p => p.Key).ToList();
            if (wide.Count > 0) return C(Make("size", MakerExportSeverity.Error, "ZIP entry names may contain at most 65535 UTF-8 bytes.", wide), "sizeNames", ("n", wide.Count));
            var metadata = set.Included.Where(p => p.Value > MaxMetadataBytes && (p.Key == PackContentIndex.ManifestFileName
                || p.Key == ChangelogFile || p.Key == MakerStudioPaths.Provenance || SubtitleTables.LanguageOf(p.Key) != null)).Select(p => p.Key).ToList();
            if (metadata.Count > 0) return C(Make("size", MakerExportSeverity.Error, "Metadata files may be at most 4 MiB; split large subtitle tables before exporting.", metadata), "sizeMetadata", ("n", metadata.Count));
            var huge = set.Included.Where(p => p.Value > MaxFileBytes).Select(p => p.Key).ToList();
            if (huge.Count > 0) return C(Make("size", MakerExportSeverity.Error, huge.Count + " file(s) are over 512 MiB.", huge), "sizeFile", ("n", huge.Count));
            if (bytes > MaxExportBytes) return C(Make("size", MakerExportSeverity.Error, "The pack is " + Mib(bytes) + "; an export may be at most 1984 MiB.", new string[0]), "sizeLimit", ("size", Mib(bytes)));
            if (bytes > MaxDownloadBytes) return C(Make("size", MakerExportSeverity.Warning, "The pack is " + Mib(bytes) + ": too big to download in the browser; the zip is still saved in CWYLS Mods/.maker-exports.", new string[0]), "sizeBrowser", ("size", Mib(bytes)));
            return C(Ok("size", Mib(bytes) + " in " + set.Included.Count + " file(s).", set.Included.Count), "sizeCount", ("size", Mib(bytes)), ("n", set.Included.Count));
        }

        internal static bool FitsInputLimits(MakerPackFileSet set, string packId) => Size(set, packId).Severity != MakerExportSeverity.Error;

        internal static string? ReadMetadata(MakerPackFiles files, string path, out bool bom, long limit = MaxMetadataBytes)
        {
            bom = false;
            var full = files.Resolve(path);
            if (!File.Exists(full)) return null;
            using var stream = File.OpenRead(full);
            if (stream.Length > limit) throw new IOException(path + " exceeds the metadata size limit");
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > limit) throw new IOException(path + " grew beyond the metadata limit");
                buffer.Write(chunk, 0, read);
            }
            var bytes = buffer.ToArray();
            bom = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
            return Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }

        internal static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static MakerExportCheck Progress(IReadOnlyDictionary<string, MakerStudioProgress?>? progress)
        {
            if (progress is null || progress.Count == 0) return C(Ok("progress", "No languages to compare with the game's lines."), "progressNone");
            var notes = new List<string>();
            var langs = new List<object?>();
            var unknown = 0;
            foreach (var pair in progress)
            {
                if (pair.Value is null) { unknown++; continue; }
                var p = pair.Value;
                var noDub = Math.Max(0, p.DubbableLines - p.DubPresent);
                var noSub = Math.Max(0, p.TotalLines - p.SubtitlePresent);
                if (noDub > 0 || noSub > 0)
                {
                    notes.Add(pair.Key + ": " + noDub + " of " + p.DubbableLines + " voiced line(s) without a dub, " + noSub + " of " + p.TotalLines + " line(s) without a subtitle");
                    langs.Add(new Dictionary<string, object?>
                    {
                        ["lang"] = pair.Key, ["dub"] = (long)noDub, ["voiced"] = (long)p.DubbableLines, ["sub"] = (long)noSub, ["total"] = (long)p.TotalLines,
                    });
                }
            }

            if (notes.Count > 0) return C(Make("progress", MakerExportSeverity.Warning, string.Join("; ", notes) + ".", new string[0], notes.Count), "progress", ("langs", langs));
            return unknown > 0 ? C(Ok("progress", "The game's lines are not loaded yet, so progress was not compared."), "progressWaiting")
                : C(Ok("progress", "Every line has its subtitle and dub."), "progressOk");
        }

        private static string? VoiceLanguage(string relative)
        {
            if (!relative.StartsWith(PackContentIndex.VoiceFolder, StringComparison.Ordinal)) return null;
            var rest = relative.Substring(PackContentIndex.VoiceFolder.Length);
            var slash = rest.IndexOf('/');
            if (slash <= 0 || rest.IndexOf('/', slash + 1) >= 0) return null;
            return LanguageVoicePlan.TryNormalizeCode(rest.Substring(0, slash), out var code, out _) ? code : null;
        }

        /// <summary>
        /// <paramref name="check"/> with its <see cref="MakerExportCheck.Code"/> and <see cref="MakerExportCheck.Params"/> (TD-13):
        /// ints become <c>long</c>, as JSON reads them back.
        /// </summary>
        private static MakerExportCheck C(MakerExportCheck check, string code, params (string Key, object? Value)[] values)
        {
            var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in values) bag[key] = value is int n ? (long)n : value;
            check.Code = code;
            check.Params = bag;
            return check;
        }

        private static MakerExportCheck Ok(string id, string message, int count = 0) =>
            new MakerExportCheck { Id = id, Severity = MakerExportSeverity.Ok, Message = message, Count = count };

        private static MakerExportCheck Make(string id, string severity, string message, IReadOnlyList<string> paths, int? count = null) =>
            new MakerExportCheck { Id = id, Severity = severity, Message = message, Count = count ?? paths.Count, Paths = paths.Take(MaxPaths).ToList() };

        private static string Mib(long bytes) => (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";

        private static string Core(int major, int minor, int patch) =>
            major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture) + "." + patch.ToString(CultureInfo.InvariantCulture);

        private static int SectionStart(string text, int from)
        {
            var i = from;
            while (i < text.Length)
            {
                if (string.CompareOrdinal(text, i, "## ", 0, 3) == 0) return i;
                var newline = text.IndexOf('\n', i);
                if (newline < 0) break;
                i = newline + 1;
            }

            return -1;
        }

        private static int StringEnd(string s, int open)
        {
            for (var i = open + 1; i < s.Length; i++)
            {
                if (s[i] == '\\') i++;
                else if (s[i] == '"') return i;
            }

            return -1;
        }

        private static int SkipSpace(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        private static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
    }
}
