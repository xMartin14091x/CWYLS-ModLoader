using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.Api.Maker.Catalog
{
    /// <summary>One subtitle and the pack it comes from.</summary>
    public sealed class MakerSubtitle
    {
        /// <summary>Creates a subtitle.</summary>
        /// <param name="text">The text; may be empty (an entry that blanks the line).</param>
        /// <param name="packId">The pack whose table holds it.</param>
        public MakerSubtitle(string text, string packId)
        {
            Text = text ?? string.Empty;
            PackId = packId ?? string.Empty;
        }

        /// <summary>The text.</summary>
        public string Text { get; }

        /// <summary>The pack whose <c>data/subtitles/&lt;lang&gt;.json</c> holds it.</summary>
        public string PackId { get; }

        /// <inheritdoc />
        public override string ToString() => PackId + ": " + Text;
    }

    /// <summary>
    /// What the loaded packs hold on disk right now for Pack Maker: every subtitle table with the
    /// pack each entry comes from, and every language voice set with the pack each clip comes from.
    /// Built by <see cref="MakerPackScanner.Scan"/>; immutable.
    /// </summary>
    /// <remarks>
    /// Read from disk rather than from the game's merged tables because the merged tables have lost
    /// which pack each entry came from, and because Pack Maker's own saves land on disk first. The
    /// rules are the game's own: subtitle tables by <see cref="SubtitleTables.LanguageOf"/>, later
    /// pack over earlier; voice sets by <see cref="LanguageVoicePlan"/> (folder codes normalised,
    /// later pack over earlier, later path over earlier within a pack).
    /// </remarks>
    public sealed class MakerPackContent
    {
        private static readonly string[] NoLanguages = new string[0];

        private readonly Dictionary<string, Dictionary<string, MakerSubtitle>> _subtitles;
        private readonly HashSet<string> _voiceNames;
        private readonly Dictionary<string, string> _roots;
        private readonly Dictionary<string, IReadOnlyList<string>> _languages;

        internal MakerPackContent(
            string fingerprint,
            Dictionary<string, Dictionary<string, MakerSubtitle>> subtitles,
            LanguageVoicePlan voicePlan,
            HashSet<string> voiceNames,
            Dictionary<string, string> roots,
            Dictionary<string, IReadOnlyList<string>> languages,
            IReadOnlyList<string> problems,
            int rejectedEntries)
        {
            Fingerprint = fingerprint;
            _subtitles = subtitles;
            VoicePlan = voicePlan;
            _voiceNames = voiceNames;
            _roots = roots;
            _languages = languages;
            Problems = problems;
            RejectedEntries = rejectedEntries;

            var count = 0;
            foreach (var table in subtitles.Values)
            {
                count += table.Count;
            }

            SubtitleCount = count;
        }

        /// <summary>Content with nothing in it.</summary>
        public static MakerPackContent Empty { get; } = new MakerPackContent(
            string.Empty,
            new Dictionary<string, Dictionary<string, MakerSubtitle>>(StringComparer.OrdinalIgnoreCase),
            LanguageVoicePlan.Empty,
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new string[0],
            0);

        /// <summary>The <see cref="MakerPackScanner.Fingerprint"/> the content was read under.</summary>
        public string Fingerprint { get; }

        /// <summary>The language voice sets, as the voice feature would plan them from these files.</summary>
        public LanguageVoicePlan VoicePlan { get; }

        /// <summary>Subtitle entries across every language, after later packs replaced earlier ones.</summary>
        public int SubtitleCount { get; }

        /// <summary>Subtitle entries the tables' parser refused (not a string, blank key, broken file).</summary>
        public int RejectedEntries { get; }

        /// <summary>Files that could not be read, one line each (<c>pack/path: reason</c>). Never null.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>The subtitle languages found, ordinally sorted.</summary>
        public IReadOnlyList<string> SubtitleLanguages
        {
            get
            {
                var codes = new List<string>(_subtitles.Keys);
                codes.Sort(StringComparer.Ordinal);
                return codes;
            }
        }

        /// <summary>
        /// One language's subtitle table (localisation id to subtitle), or null when no pack has one.
        /// The language is matched as the game matches it: case-insensitively, without aliases.
        /// </summary>
        /// <param name="lang">A language code.</param>
        public IReadOnlyDictionary<string, MakerSubtitle>? SubtitlesFor(string? lang) =>
            !string.IsNullOrEmpty(lang) && _subtitles.TryGetValue(lang!, out var table) ? table : null;

        /// <summary>The subtitle a line shows in a language, when a pack has a non-empty one.</summary>
        /// <param name="lang">A language code.</param>
        /// <param name="id">A localisation id.</param>
        /// <param name="subtitle">The subtitle, or null.</param>
        public bool TryGetSubtitle(string? lang, string? id, out MakerSubtitle? subtitle)
        {
            subtitle = null;
            var table = SubtitlesFor(lang);
            if (table is null || string.IsNullOrEmpty(id) || !table.TryGetValue(id!, out var found) || found.Text.Length == 0)
            {
                return false;
            }

            subtitle = found;
            return true;
        }

        /// <summary>The pack clip for a voice in a language, or null.</summary>
        /// <param name="lang">A language code in any spelling the voice feature accepts.</param>
        /// <param name="voice">The voice name, case-sensitively.</param>
        public VoiceLoadStep? FindDub(string? lang, string? voice) =>
            !string.IsNullOrEmpty(voice) && VoicePlan.TryGet(lang, out var set) && set != null ? set.Find(voice) : null;

        /// <summary>True when any pack ships a clip for this voice, globally or in any language.</summary>
        /// <param name="voice">The voice name.</param>
        public bool ProvidesVoice(string? voice) => !string.IsNullOrEmpty(voice) && _voiceNames.Contains(voice!);

        /// <summary>The absolute folder of a loaded pack, or null.</summary>
        /// <param name="packId">The pack id.</param>
        public string? RootOf(string? packId) =>
            !string.IsNullOrEmpty(packId) && _roots.TryGetValue(packId!, out var root) ? root : null;

        /// <summary>The absolute path of a planned clip, or null when its pack is unknown or the path is unsafe.</summary>
        /// <param name="step">A step of <see cref="VoicePlan"/>.</param>
        public string? FullPathOf(VoiceLoadStep? step)
        {
            var root = RootOf(step?.PackId);
            var relative = PackContentIndex.Normalise(step?.RelativePath);
            return root is null || relative is null
                ? null
                : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>The languages a pack has subtitle tables or a voice set for, ordinally sorted. Never null.</summary>
        /// <param name="packId">The pack id.</param>
        public IReadOnlyList<string> LanguagesOf(string? packId) =>
            !string.IsNullOrEmpty(packId) && _languages.TryGetValue(packId!, out var codes) ? codes : NoLanguages;

        /// <inheritdoc />
        public override string ToString() =>
            SubtitleCount.ToString(CultureInfo.InvariantCulture) + " subtitle(s) in "
            + (_subtitles.Count == 0 ? "no language" : string.Join(", ", SubtitleLanguages))
            + "; voice sets: " + VoicePlan.Summarize();
    }

    /// <summary>
    /// Reads the loaded packs' subtitle tables and voice files off disk into a
    /// <see cref="MakerPackContent"/>, and fingerprints those folders cheaply so the catalog can tell
    /// when to read them again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only two places of a pack are read: <c>data/subtitles/*.json</c> (direct children) and
    /// <c>audio/voice/</c> (its files and its direct sub-folders' files). Hidden files
    /// (<c>.DS_Store</c>, <c>._x.ogg</c>) are ignored. Nothing is written.
    /// </para>
    /// <para>
    /// <b>Links (DS-10a).</b> Nothing is read through a symbolic link or junction, so nothing outside
    /// the pack folder is: a linked table, clip or language folder is skipped, and a
    /// <c>data/subtitles</c> or <c>audio/voice</c> folder that is (or goes through) a link is not read at
    /// all. Each skip is one line in <see cref="MakerPackContent.Problems"/>, which the catalog logs;
    /// <see cref="MakerPackRules.Decide"/> reports such a pack read-only.
    /// </para>
    /// <para>
    /// <b>Fingerprint.</b> Per pack: the last write time of <c>data/subtitles/</c> and the size and
    /// last write time of each table in it, and the last write time of <c>audio/voice/</c> and of each
    /// folder in it. A save that adds, removes or atomically replaces a file changes its folder's
    /// time; a table edited in place changes its own. A clip overwritten in place is not seen here
    /// (the set is unchanged); its new length is, because lengths are keyed by the file's own time
    /// (<see cref="MakerDubLengths"/>).
    /// </para>
    /// </remarks>
    public static class MakerPackScanner
    {
        private static readonly string[] Nothing = new string[0];

        /// <summary>A cheap description of the folders <see cref="Scan"/> reads, for change detection.</summary>
        /// <param name="packs">The packs, in load order; packs that did not load are skipped.</param>
        public static string Fingerprint(IEnumerable<MakerPackSource>? packs)
        {
            var sb = new StringBuilder();
            if (packs is null)
            {
                return string.Empty;
            }

            foreach (var pack in packs)
            {
                if (pack is null || !pack.Loaded || pack.RootPath is null)
                {
                    continue;
                }

                sb.Append(pack.Id).Append('|').Append(pack.RootPath).Append('\n');

                // A linked content folder is not looked into, not even for its times (DS-10a).
                if (MakerPackRules.FirstLinkBelow(pack.RootPath, SubtitlesRelative) != null)
                {
                    sb.Append("s link\n");
                }
                else
                {
                    var subtitles = SubtitlesFolder(pack.RootPath);
                    sb.Append("s ").Append(Stamp(subtitles)).Append('\n');
                    foreach (var file in Files(subtitles))
                    {
                        sb.Append("  ").Append(Path.GetFileName(file)).Append(' ').Append(FileStamp(file)).Append('\n');
                    }
                }

                if (MakerPackRules.FirstLinkBelow(pack.RootPath, VoiceRelative) != null)
                {
                    sb.Append("v link\n");
                }
                else
                {
                    var voices = VoiceFolder(pack.RootPath);
                    sb.Append("v ").Append(Stamp(voices)).Append('\n');
                    foreach (var folder in Folders(voices))
                    {
                        sb.Append("  ").Append(Path.GetFileName(folder)).Append(' ').Append(Stamp(folder)).Append('\n');
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>Reads the packs' subtitle tables and voice files.</summary>
        /// <param name="packs">The packs, in load order; packs that did not load are skipped.</param>
        /// <param name="fingerprint">
        /// The <see cref="Fingerprint"/> taken before reading; null takes one now. Taking it first
        /// means a file changed during the read makes the next fingerprint differ, so it is read again.
        /// </param>
        public static MakerPackContent Scan(IEnumerable<MakerPackSource>? packs, string? fingerprint = null)
        {
            var list = new List<MakerPackSource>();
            if (packs != null)
            {
                foreach (var pack in packs)
                {
                    if (pack != null && pack.Loaded && pack.RootPath != null)
                    {
                        list.Add(pack);
                    }
                }
            }

            var stamp = fingerprint ?? Fingerprint(list);
            var subtitles = new Dictionary<string, Dictionary<string, MakerSubtitle>>(StringComparer.OrdinalIgnoreCase);
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var languages = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var indexes = new List<KeyValuePair<string, PackContentIndex>>(list.Count);
            var problems = new List<string>();
            var rejected = 0;

            foreach (var pack in list)
            {
                var root = pack.RootPath!;
                roots[pack.Id] = root;

                var codes = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var declared in pack.DeclaredLanguages)
                {
                    if (!string.IsNullOrWhiteSpace(declared))
                    {
                        codes.Add(declared.Trim().ToLowerInvariant());
                    }
                }

                rejected += ReadSubtitles(pack, root, subtitles, codes, problems);
                indexes.Add(new KeyValuePair<string, PackContentIndex>(pack.Id, IndexVoices(pack, root, codes, problems)));
                languages[pack.Id] = new List<string>(codes);
            }

            var plan = LanguageVoicePlan.Build(indexes);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var set in plan.Sets)
            {
                foreach (var step in set.Steps)
                {
                    names.Add(step.Name);
                }
            }

            foreach (var step in VoiceLoadPlan.Build(indexes).Steps)
            {
                names.Add(step.Name);
            }

            return new MakerPackContent(stamp, subtitles, plan, names, roots, languages, problems, rejected);
        }

        /// <summary>Reads one pack's <c>data/subtitles/*.json</c>; returns how many entries were refused.</summary>
        private static int ReadSubtitles(
            MakerPackSource pack,
            string root,
            Dictionary<string, Dictionary<string, MakerSubtitle>> subtitles,
            SortedSet<string> codes,
            List<string> problems)
        {
            var rejected = 0;
            var linked = MakerPackRules.FirstLinkBelow(root, SubtitlesRelative);
            if (linked != null)
            {
                problems.Add(pack.Id + "/" + linked + ": " + LinkSkipped);
                return 0;
            }

            foreach (var file in Files(SubtitlesFolder(root), pack.Id + "/" + SubtitlesRelative + "/", problems))
            {
                var relative = SubtitleTables.Folder + Path.GetFileName(file);
                var lang = SubtitleTables.LanguageOf(relative);
                if (lang is null)
                {
                    continue;
                }

                string json;
                try
                {
                    json = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                {
                    problems.Add(pack.Id + "/" + relative + ": " + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                var parsed = SubtitleTables.Parse(json, pack.Id + "/" + relative, lang);
                rejected += parsed.Rejected.Count;
                codes.Add(lang);

                if (!subtitles.TryGetValue(lang, out var table))
                {
                    table = new Dictionary<string, MakerSubtitle>(StringComparer.Ordinal);
                    subtitles.Add(lang, table);
                }

                foreach (var entry in parsed.Rows)
                {
                    table[entry.LocalizationId] = new MakerSubtitle(entry.Text, pack.Id);
                }
            }

            return rejected;
        }

        /// <summary>Lists one pack's voice files as a content index (global files and language folders); links are skipped.</summary>
        private static PackContentIndex IndexVoices(MakerPackSource pack, string root, SortedSet<string> codes, List<string> problems)
        {
            var folder = VoiceFolder(root);
            var paths = new List<string?>();
            var linked = MakerPackRules.FirstLinkBelow(root, VoiceRelative);
            if (linked != null)
            {
                problems.Add(pack.Id + "/" + linked + ": " + LinkSkipped);
                return PackContentIndex.Build(paths);
            }

            var label = pack.Id + "/" + VoiceRelative + "/";
            foreach (var file in Files(folder, label, problems))
            {
                paths.Add(PackContentIndex.VoiceFolder + Path.GetFileName(file));
            }

            foreach (var sub in Folders(folder, label, problems))
            {
                var name = Path.GetFileName(sub);
                var voiced = false;
                foreach (var file in Files(sub, label + name + "/", problems))
                {
                    var fileName = Path.GetFileName(file);
                    paths.Add(PackContentIndex.VoiceFolder + name + "/" + fileName);
                    voiced |= VoiceLoadPlan.TryLoaderFor(Path.GetExtension(fileName), out _);
                }

                if (voiced && LanguageVoicePlan.TryNormalizeCode(name, out var code, out _))
                {
                    codes.Add(code);
                }
            }

            return PackContentIndex.Build(paths);
        }

        /// <summary>The problem text of an entry skipped because it is a link.</summary>
        internal const string LinkSkipped = "skipped: it is a symbolic link or junction (Pack Maker never reads through links)";

        private const string SubtitlesRelative = "data/subtitles";

        private const string VoiceRelative = "audio/voice";

        private static string SubtitlesFolder(string root) => Path.Combine(root, "data", "subtitles");

        private static string VoiceFolder(string root) => Path.Combine(root, "audio", "voice");

        /// <summary>
        /// A folder's non-hidden files that are not links, ordinally sorted; nothing when it is missing or
        /// unreadable. Each link is one line in <paramref name="problems"/> (<paramref name="label"/> + name) when given.
        /// </summary>
        private static IReadOnlyList<string> Files(string folder, string? label = null, List<string>? problems = null) =>
            List(folder, true, label, problems);

        /// <summary>A folder's non-hidden sub-folders that are not links; as <see cref="Files"/>.</summary>
        private static IReadOnlyList<string> Folders(string folder, string? label = null, List<string>? problems = null) =>
            List(folder, false, label, problems);

        private static IReadOnlyList<string> List(string folder, bool files, string? label, List<string>? problems)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return Nothing;
                }

                var entries = files ? Directory.GetFiles(folder) : Directory.GetDirectories(folder);
                var kept = new List<string>(entries.Length);
                List<string>? links = null;
                foreach (var entry in entries)
                {
                    var name = Path.GetFileName(entry);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (MakerPackRules.IsLink(entry))
                    {
                        links ??= new List<string>();
                        links.Add(label + name + ": " + LinkSkipped);
                        continue;
                    }

                    kept.Add(entry);
                }

                if (links != null && problems != null)
                {
                    links.Sort(StringComparer.Ordinal);
                    problems.AddRange(links);
                }

                kept.Sort(StringComparer.Ordinal);
                return kept;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return Nothing;
            }
        }

        private static string Stamp(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    ? Directory.GetLastWriteTimeUtc(folder).Ticks.ToString(CultureInfo.InvariantCulture)
                    : "-";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return "?";
            }
        }

        private static string FileStamp(string file)
        {
            try
            {
                var info = new FileInfo(file);
                return info.Exists
                    ? info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    : "-";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return "?";
            }
        }
    }
}
