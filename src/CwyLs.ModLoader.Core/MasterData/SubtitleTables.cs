using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// The text tables a pack ships in <c>data/subtitles/&lt;lang&gt;.json</c> - one per language
    /// the game has no column for - and the <see cref="ISubtitleLookup"/> over them.
    /// <para>
    /// One file per language, named by its code - <c>th.json</c> is Thai - holding a flat JSON
    /// object of <c>localizationId -&gt; text</c> (the text below stands in for the translated line):
    /// </para>
    /// <code>
    /// {
    ///   "cwy_example_01_001": "Hello from the mod loader"
    /// }
    /// </code>
    /// <para>
    /// A flat map rather than an array of rows because a subtitle table is written by a translator
    /// working line by line, and because that shape diffs cleanly when one line changes.
    /// </para>
    /// </summary>
    public sealed class SubtitleTables : ISubtitleLookup
    {
        /// <summary>Pack-relative folder every subtitle table lives in.</summary>
        public const string Folder = "data/subtitles/";

        /// <summary>File extension of a subtitle table.</summary>
        public const string Extension = ".json";

        private static readonly string[] NoLanguages = new string[0];

        private readonly Dictionary<string, Dictionary<string, string>> _byLanguage;
        private readonly string[] _languages;

        private SubtitleTables(Dictionary<string, Dictionary<string, string>> byLanguage, string[] languages)
        {
            _byLanguage = byLanguage;
            _languages = languages;
            Count = CountAll(byLanguage);
        }

        /// <summary>A lookup holding nothing. Used before any pack has loaded.</summary>
        public static SubtitleTables Empty { get; } = new SubtitleTables(
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase), NoLanguages);

        /// <inheritdoc />
        public IReadOnlyList<string> Languages => _languages;

        /// <inheritdoc />
        public int Count { get; }

        /// <summary>
        /// The language code a subtitle file's path names, or null when the path is not a subtitle
        /// table. <c>data/subtitles/th.json</c> is <c>th</c>; a file in a sub-folder is refused,
        /// because a language code has no second level.
        /// </summary>
        /// <param name="relativePath">A pack-relative, forward-slash path.</param>
        public static string? LanguageOf(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            var path = relativePath!.Trim().Replace('\\', '/');
            if (!path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var name = path.Substring(Folder.Length, path.Length - Folder.Length - Extension.Length);
            if (name.Length == 0 || name.IndexOf('/') >= 0)
            {
                return null;
            }

            return name.ToLowerInvariant();
        }

        /// <summary>
        /// Parses one subtitle file. Entries come back in document order; a value that is not a
        /// string, and a blank key, are rejected individually.
        /// </summary>
        /// <param name="json">File contents; null or blank is reported as an empty file.</param>
        /// <param name="file">Display path used in rejections, e.g. <c>cwy.example/data/subtitles/th.json</c>.</param>
        /// <param name="language">The language code, normally from <see cref="LanguageOf"/>.</param>
        public static MasterDataParse<SubtitleEntry> Parse(string? json, string file, string? language)
        {
            var entries = new List<SubtitleEntry>();
            var rejected = new List<MasterDataRejection>();

            if (string.IsNullOrWhiteSpace(language))
            {
                rejected.Add(new MasterDataRejection(
                    file,
                    MasterDataRejection.WholeFile,
                    null,
                    "the file name is not a language code; expected " + Folder + "<lang>" + Extension));
                return new MasterDataParse<SubtitleEntry>(file, entries, rejected);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                rejected.Add(new MasterDataRejection(
                    file, MasterDataRejection.WholeFile, null, "the file is empty"));
                return new MasterDataParse<SubtitleEntry>(file, entries, rejected);
            }

            if (!JsonReader.TryParse(json!, out var document, out var error))
            {
                rejected.Add(new MasterDataRejection(file, MasterDataRejection.WholeFile, null, error!));
                return new MasterDataParse<SubtitleEntry>(file, entries, rejected);
            }

            if (!(document is JsonObject table))
            {
                rejected.Add(new MasterDataRejection(
                    file,
                    MasterDataRejection.WholeFile,
                    null,
                    "the top-level value must be an object of localizationId -> text, not "
                    + JsonReader.TypeName(document)));
                return new MasterDataParse<SubtitleEntry>(file, entries, rejected);
            }

            var index = 0;
            foreach (var pair in table)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    rejected.Add(new MasterDataRejection(file, index, null, "a localization id cannot be blank"));
                    index++;
                    continue;
                }

                if (!(pair.Value is string text))
                {
                    rejected.Add(new MasterDataRejection(
                        file, index, pair.Key, "the text must be a string, not " + JsonReader.TypeName(pair.Value)));
                    index++;
                    continue;
                }

                entries.Add(new SubtitleEntry(language!, pair.Key, text));
                index++;
            }

            return new MasterDataParse<SubtitleEntry>(file, entries, rejected);
        }

        /// <summary>
        /// Builds a lookup. Entries are applied in order, so a later pack's entry replaces an
        /// earlier one for the same language and id - the same rule the master-data merge uses.
        /// </summary>
        /// <param name="entries">The entries, in pack order; null means none.</param>
        public static SubtitleTables Build(IEnumerable<SubtitleEntry>? entries)
        {
            if (entries is null)
            {
                return Empty;
            }

            var byLanguage = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (entry is null)
                {
                    continue;
                }

                if (!byLanguage.TryGetValue(entry.Language, out var table))
                {
                    table = new Dictionary<string, string>(StringComparer.Ordinal);
                    byLanguage.Add(entry.Language, table);
                }

                table[entry.LocalizationId] = entry.Text;
            }

            var languages = new string[byLanguage.Count];
            byLanguage.Keys.CopyTo(languages, 0);
            Array.Sort(languages, StringComparer.Ordinal);

            return new SubtitleTables(byLanguage, languages);
        }

        /// <inheritdoc />
        public bool TryGet(string? localizationId, string? language, out string? text)
        {
            text = null;

            if (string.IsNullOrEmpty(localizationId) || string.IsNullOrEmpty(language))
            {
                return false;
            }

            return _byLanguage.TryGetValue(language!, out var table) && table.TryGetValue(localizationId!, out text);
        }

        /// <inheritdoc />
        public int CountFor(string? language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return 0;
            }

            return _byLanguage.TryGetValue(language!, out var table) ? table.Count : 0;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> IdsFor(string? language)
        {
            if (string.IsNullOrEmpty(language) || !_byLanguage.TryGetValue(language!, out var table))
            {
                return NoLanguages;
            }

            var ids = new string[table.Count];
            table.Keys.CopyTo(ids, 0);
            Array.Sort(ids, StringComparer.Ordinal);
            return ids;
        }

        /// <summary><c>n row(s) in lang, lang</c>.</summary>
        public override string ToString()
        {
            if (_languages.Length == 0)
            {
                return "no subtitles";
            }

            var parts = new string[_languages.Length];
            for (var i = 0; i < _languages.Length; i++)
            {
                parts[i] = _languages[i] + " " + CountFor(_languages[i]);
            }

            return string.Join(", ", parts);
        }

        private static int CountAll(Dictionary<string, Dictionary<string, string>> byLanguage)
        {
            var total = 0;
            foreach (var table in byLanguage.Values)
            {
                total += table.Count;
            }

            return total;
        }
    }
}
