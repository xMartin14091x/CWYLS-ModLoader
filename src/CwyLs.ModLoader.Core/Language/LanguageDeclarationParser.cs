using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// Turns the text of a pack's <c>language.json</c> into <see cref="LanguageDeclaration"/>s.
    /// </summary>
    /// <remarks>
    /// <para>The file holds one declaration object, or an array of them:</para>
    /// <code>
    /// {
    ///   "schema": 1,                              // optional; 1 is the only version
    ///   "code": "th",                             // required; not one of the game's own seven
    ///   "caption": "ไทย",                         // required; the selector entry's text
    ///   "englishName": "Thai",                    // required; for logs
    ///   "script": "Thai",                         // optional; ISO 15924 (4 letters)
    ///   "fallback": ["en"],                       // optional; string or array; default ["en"]
    ///   "font": { "gameAssets": ["Base_TH", "Dynamic_TH"] },   // optional
    ///   "marks": "fonts/thai-marks.json",         // optional; pack-relative mark table
    ///   "lineBreaks": "auto"                      // optional; auto | none | thai
    /// }
    /// </code>
    /// <para>
    /// Strict, like <see cref="PackManifestParser"/>: an unknown member is an error unless it
    /// starts with <c>x-</c>, because a misspelled <c>fallbak</c> silently ignored is a language
    /// that falls back to the wrong text with nobody told why.
    /// </para>
    /// </remarks>
    public static class LanguageDeclarationParser
    {
        /// <summary>The file name a pack declares its languages in, at the pack root.</summary>
        public const string FileName = "language.json";

        /// <summary>The only schema version this parser reads.</summary>
        public const int Schema = 1;

        /// <summary>Prefix that marks a member as the pack author's own, to be ignored here.</summary>
        public const string ExtensionPrefix = "x-";

        /// <summary>The fallback chain a declaration gets when it names none.</summary>
        public static IReadOnlyList<string> DefaultFallback { get; } = new[] { SubtitleLanguages.English };

        private const string KeySchema = "schema";
        private const string KeyCode = "code";
        private const string KeyCaption = "caption";
        private const string KeyEnglishName = "englishName";
        private const string KeyScript = "script";
        private const string KeyFallback = "fallback";
        private const string KeyFont = "font";
        private const string KeyMarks = "marks";
        private const string KeyLineBreaks = "lineBreaks";
        private const string KeyGameAssets = "gameAssets";
        private const string KeyFontFile = "file";

        private static readonly string[] KnownKeys =
        {
            KeySchema, KeyCode, KeyCaption, KeyEnglishName, KeyScript, KeyFallback, KeyFont, KeyMarks, KeyLineBreaks,
        };

        private static readonly string[] KnownFontKeys = { KeyGameAssets, KeyFontFile };

        // \A...\z rather than ^...$: .NET's $ also matches before a trailing newline.
        private static readonly Regex CodeRegex =
            new Regex(@"\A[a-z]{2,3}(-[a-z0-9]{2,8})*\z", RegexOptions.CultureInvariant);

        private static readonly Regex ScriptRegex =
            new Regex(@"\A[A-Za-z]{4}\z", RegexOptions.CultureInvariant);

        /// <summary>Parses and validates a <c>language.json</c>.</summary>
        /// <param name="json">The whole file.</param>
        /// <param name="packId">The pack it belongs to.</param>
        /// <param name="file">The file name used in messages; defaults to <see cref="FileName"/>.</param>
        /// <returns>The declarations, in file order. Never empty.</returns>
        /// <exception cref="LanguageDeclarationException">The file is not a valid declaration.</exception>
        public static IReadOnlyList<LanguageDeclaration> Parse(string? json, string packId, string file = FileName)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new LanguageDeclarationException(file + ": the file is empty");
            }

            object? document;
            try
            {
                document = JsonReader.Parse(json!);
            }
            catch (JsonParseException ex)
            {
                throw new LanguageDeclarationException(file + ": invalid JSON at " + ex.Message);
            }

            var declarations = new List<LanguageDeclaration>();

            if (document is JsonObject single)
            {
                declarations.Add(ParseOne(single, packId, file, file));
            }
            else if (document is IList<object?> many)
            {
                if (many.Count == 0)
                {
                    throw new LanguageDeclarationException(file + ": the array declares no language");
                }

                for (var i = 0; i < many.Count; i++)
                {
                    var where = file + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (!(many[i] is JsonObject obj))
                    {
                        throw new LanguageDeclarationException(
                            where + " must be an object, not " + JsonReader.TypeName(many[i]));
                    }

                    declarations.Add(ParseOne(obj, packId, file, where));
                }
            }
            else
            {
                throw new LanguageDeclarationException(
                    file + ": the top-level value must be an object or an array of objects, not "
                    + JsonReader.TypeName(document));
            }

            for (var i = 0; i < declarations.Count; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    if (string.Equals(declarations[i].Code, declarations[j].Code, StringComparison.Ordinal))
                    {
                        throw new LanguageDeclarationException(
                            file + ": \"" + declarations[i].Code + "\" is declared twice");
                    }
                }
            }

            return declarations;
        }

        /// <summary>Parses a <c>language.json</c>, reporting failure instead of throwing.</summary>
        /// <param name="json">The whole file.</param>
        /// <param name="packId">The pack it belongs to.</param>
        /// <param name="declarations">The declarations on success; empty on failure. Never null.</param>
        /// <param name="error">Why it failed, or null on success.</param>
        /// <param name="file">The file name used in messages.</param>
        /// <returns>True when the file is valid.</returns>
        public static bool TryParse(
            string? json,
            string packId,
            out IReadOnlyList<LanguageDeclaration> declarations,
            out string? error,
            string file = FileName)
        {
            try
            {
                declarations = Parse(json, packId, file);
                error = null;
                return true;
            }
            catch (LanguageDeclarationException ex)
            {
                declarations = new LanguageDeclaration[0];
                error = ex.Message;
                return false;
            }
        }

        private static LanguageDeclaration ParseOne(JsonObject root, string packId, string file, string where)
        {
            RejectUnknown(root, KnownKeys, where);

            if (root.TryGetValue(KeySchema, out var schema) && schema != null)
            {
                if (!(schema is long version) || version != Schema)
                {
                    throw new LanguageDeclarationException(
                        where + ": \"" + KeySchema + "\" must be " + Schema.ToString(CultureInfo.InvariantCulture)
                        + ", not " + (schema is long ? schema.ToString() : JsonReader.TypeName(schema)));
                }
            }

            var code = ParseCode(RequiredString(root, KeyCode, where), where + ".\"" + KeyCode + "\"");
            if (SubtitleLanguages.IsInGame(code))
            {
                throw new LanguageDeclarationException(
                    where + ": \"" + KeyCode + "\": \"" + code + "\" is one of the game's own languages; a pack "
                    + "changes those rows with data/localization.json, it does not declare them again");
            }

            var caption = RequiredString(root, KeyCaption, where);
            var englishName = RequiredString(root, KeyEnglishName, where);

            var script = OptionalString(root, KeyScript, where);
            if (script != null && !ScriptRegex.IsMatch(script))
            {
                throw new LanguageDeclarationException(
                    where + ": \"" + KeyScript + "\": \"" + script + "\" must be a 4-letter ISO 15924 script name, e.g. Thai");
            }

            var lineBreaks = OptionalString(root, KeyLineBreaks, where);
            if (lineBreaks != null && !LineBreakHelpers.IsKnown(lineBreaks))
            {
                throw new LanguageDeclarationException(
                    where + ": \"" + KeyLineBreaks + "\": \"" + lineBreaks + "\" is not one of "
                    + string.Join(", ", LineBreakHelpers.KnownIds));
            }

            var marks = OptionalString(root, KeyMarks, where);
            string? marksPath = null;
            if (marks != null)
            {
                marksPath = PackContentIndex.Normalise(marks);
                if (marksPath is null || !marksPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new LanguageDeclarationException(
                        where + ": \"" + KeyMarks + "\": \"" + marks + "\" must be a .json path inside the pack");
                }
            }

            return new LanguageDeclaration(
                packId,
                code,
                caption,
                englishName,
                script?.ToLowerInvariant(),
                ParseFallback(root, code, where),
                ParseFont(root, where),
                marksPath,
                lineBreaks?.ToLowerInvariant(),
                file);
        }

        private static string ParseCode(string raw, string where)
        {
            var code = SubtitleLanguages.Normalize(raw);
            if (!CodeRegex.IsMatch(code))
            {
                throw new LanguageDeclarationException(
                    where + ": \"" + raw + "\" is not a language code (expected e.g. th, vi, pt-br)");
            }

            return code;
        }

        private static IReadOnlyList<string> ParseFallback(JsonObject root, string ownCode, string where)
        {
            if (!root.TryGetValue(KeyFallback, out var value) || value is null)
            {
                return DefaultFallback;
            }

            var raw = new List<object?>();
            if (value is string one)
            {
                raw.Add(one);
            }
            else if (value is IList<object?> list)
            {
                raw.AddRange(list);
            }
            else
            {
                throw new LanguageDeclarationException(
                    where + ": \"" + KeyFallback + "\" must be a code or an array of codes, not " + JsonReader.TypeName(value));
            }

            var chain = new List<string>(raw.Count);
            for (var i = 0; i < raw.Count; i++)
            {
                var item = where + ".\"" + KeyFallback + "\"[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (!(raw[i] is string text) || string.IsNullOrWhiteSpace(text))
                {
                    throw new LanguageDeclarationException(item + " must be a non-blank language code");
                }

                var code = ParseCode(text, item);
                if (string.Equals(code, ownCode, StringComparison.Ordinal))
                {
                    throw new LanguageDeclarationException(item + ": a language cannot fall back to itself");
                }

                if (chain.Contains(code))
                {
                    throw new LanguageDeclarationException(item + ": \"" + code + "\" is listed twice");
                }

                chain.Add(code);
            }

            return chain.Count == 0 ? DefaultFallback : chain;
        }

        private static IReadOnlyList<string> ParseFont(JsonObject root, string where)
        {
            if (!root.TryGetValue(KeyFont, out var value) || value is null)
            {
                return new string[0];
            }

            var at = where + ".\"" + KeyFont + "\"";
            if (!(value is JsonObject font))
            {
                throw new LanguageDeclarationException(at + " must be an object, not " + JsonReader.TypeName(value));
            }

            RejectUnknown(font, KnownFontKeys, at);

            if (font.ContainsKey(KeyFontFile))
            {
                throw new LanguageDeclarationException(
                    at + ".\"" + KeyFontFile + "\" is not supported yet: name a font the game ships in \""
                    + KeyGameAssets + "\"");
            }

            if (!font.TryGetValue(KeyGameAssets, out var assets) || assets is null)
            {
                return new string[0];
            }

            var names = new List<string>();
            if (assets is string single)
            {
                AddAssetName(names, single, at + ".\"" + KeyGameAssets + "\"");
            }
            else if (assets is IList<object?> list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var item = at + ".\"" + KeyGameAssets + "\"[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (!(list[i] is string name))
                    {
                        throw new LanguageDeclarationException(item + " must be a font asset name, not " + JsonReader.TypeName(list[i]));
                    }

                    AddAssetName(names, name, item);
                }
            }
            else
            {
                throw new LanguageDeclarationException(
                    at + ".\"" + KeyGameAssets + "\" must be a name or an array of names, not " + JsonReader.TypeName(assets));
            }

            return names;
        }

        private static void AddAssetName(List<string> names, string name, string where)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new LanguageDeclarationException(where + " must not be blank");
            }

            var trimmed = name.Trim();
            if (names.Contains(trimmed))
            {
                throw new LanguageDeclarationException(where + ": \"" + trimmed + "\" is listed twice");
            }

            names.Add(trimmed);
        }

        private static void RejectUnknown(JsonObject obj, string[] known, string where)
        {
            foreach (var member in obj)
            {
                if (member.Key.StartsWith(ExtensionPrefix, StringComparison.Ordinal)
                    || Array.IndexOf(known, member.Key) >= 0)
                {
                    continue;
                }

                throw new LanguageDeclarationException(
                    where + ": unknown member \"" + member.Key + "\"; expected one of " + string.Join(", ", known)
                    + " (prefix your own fields with \"" + ExtensionPrefix + "\" to have them ignored)");
            }
        }

        private static string RequiredString(JsonObject root, string key, string where)
        {
            var value = OptionalString(root, key, where);
            if (value is null)
            {
                throw new LanguageDeclarationException(where + ": \"" + key + "\" is required");
            }

            return value;
        }

        private static string? OptionalString(JsonObject root, string key, string where)
        {
            if (!root.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            if (!(value is string text))
            {
                throw new LanguageDeclarationException(
                    where + ": \"" + key + "\" must be a string, not " + JsonReader.TypeName(value));
            }

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }
}
