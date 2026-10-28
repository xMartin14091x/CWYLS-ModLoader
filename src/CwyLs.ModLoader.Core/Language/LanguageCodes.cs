using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// The two-way map between the game's own seven <c>Bulbul.GameLanguageType</c> values (as
    /// integers) and the short language codes packs and configs are written in.
    /// </summary>
    /// <remarks>
    /// Built-in languages only. Languages that packs declare get their values from a
    /// <see cref="LanguageRegistry"/>, which falls back to this table for the game's own seven.
    /// Codes match <see cref="SubtitleLanguages"/>, so a pack author writes one spelling for a text
    /// table (<c>data/subtitles/en.json</c>), a fallback chain and a language choice.
    /// </remarks>
    public static class LanguageCodes
    {
        /// <summary>What an unknown value maps to: the empty string.</summary>
        public const string Unknown = "";

        private static readonly Dictionary<int, string> ToCodes = new Dictionary<int, string>
        {
            [GameLanguages.Japanese] = SubtitleLanguages.Japanese,
            [GameLanguages.English] = SubtitleLanguages.English,
            [GameLanguages.ChineseSimplified] = "zh-hans",
            [GameLanguages.ChineseTraditional] = "zh-hant",
            [GameLanguages.Portuguese] = "pt-br",
            [GameLanguages.Korean] = "ko",
            [GameLanguages.Russian] = "ru",
        };

        private static readonly Dictionary<int, string> ToNames = new Dictionary<int, string>
        {
            [GameLanguages.None] = "None",
            [GameLanguages.Japanese] = "Japanese",
            [GameLanguages.English] = "English",
            [GameLanguages.ChineseSimplified] = "ChineseSimplified",
            [GameLanguages.ChineseTraditional] = "ChineseTraditional",
            [GameLanguages.Portuguese] = "Portuguese",
            [GameLanguages.Korean] = "Korean",
            [GameLanguages.Russian] = "Russian",
        };

        private static readonly Dictionary<string, int> FromCodes = BuildFromCodes();

        /// <summary>The code for a built-in game language value, e.g. <c>2</c> gives <c>en</c>.</summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        /// <returns>The code, or <see cref="Unknown"/> for <c>None</c> and anything the game does not define.</returns>
        public static string ToCode(int value) =>
            ToCodes.TryGetValue(value, out var code) ? code : Unknown;

        /// <summary>The enum member name for a built-in value, for logs.</summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        /// <returns>The name, or the number as text when the game does not define the value.</returns>
        public static string ToName(int value) =>
            ToNames.TryGetValue(value, out var name) ? name : value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The built-in game language value for a code, accepting every spelling
        /// <see cref="SubtitleLanguages.Normalize"/> accepts (<c>jp</c>, <c>EN</c>, <c>ja-JP</c>).
        /// </summary>
        /// <param name="code">A language code, or null/blank.</param>
        /// <param name="value">The value on success; <see cref="GameLanguages.None"/> otherwise.</param>
        /// <returns>True when the code names one of the game's own languages.</returns>
        public static bool TryFromCode(string? code, out int value)
        {
            value = GameLanguages.None;
            var normalized = SubtitleLanguages.Normalize(code);
            return normalized.Length > 0 && FromCodes.TryGetValue(normalized, out value);
        }

        /// <summary>True when the game itself can display this value (see <see cref="GameLanguages.IsBuiltIn"/>).</summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        public static bool IsInGame(int value) => GameLanguages.IsBuiltIn(value);

        private static Dictionary<string, int> BuildFromCodes()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in ToCodes)
            {
                map[pair.Value] = pair.Key;
            }

            return map;
        }
    }
}
