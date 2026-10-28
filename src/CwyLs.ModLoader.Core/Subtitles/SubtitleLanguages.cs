using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Subtitles
{
    /// <summary>
    /// Language codes as packs, configs and text tables write them, and how every spelling is
    /// normalised to one.
    /// </summary>
    /// <remarks>
    /// The game ships seven languages (<c>GameLanguageType</c>: Japanese, English,
    /// ChineseSimplified, ChineseTraditional, Portuguese, Korean, Russian). Our codes are the usual
    /// BCP-47-ish short forms, because a content pack author writing <c>"th"</c>/<c>"ja"</c>/<c>"zh-hant"</c>
    /// should not have to know the game's enum. Everything is normalised first, so <c>JP</c>,
    /// <c>jp</c>, <c>ja-JP</c> and <c>ja</c> are one language. Languages beyond the game's seven are
    /// declared by packs (<c>CwyLs.ModLoader.Core.Language.LanguageRegistry</c>); the alias table below is
    /// spelling data only and gives no language any behaviour.
    /// </remarks>
    public static class SubtitleLanguages
    {
        /// <summary>Japanese: the column every master-data row is authored in.</summary>
        public const string Japanese = "ja";

        /// <summary>English.</summary>
        public const string English = "en";

        private static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["jp"] = Japanese,
                ["jpn"] = Japanese,
                ["ja-jp"] = Japanese,
                ["japanese"] = Japanese,
                ["eng"] = English,
                ["en-us"] = English,
                ["en-gb"] = English,
                ["english"] = English,
                ["tha"] = "th",
                ["th-th"] = "th",
                ["thai"] = "th",
                ["zh"] = "zh-hans",
                ["zhcn"] = "zh-hans",
                ["zh-cn"] = "zh-hans",
                ["chinesesimplified"] = "zh-hans",
                ["zhtw"] = "zh-hant",
                ["zh-tw"] = "zh-hant",
                ["chinesetraditional"] = "zh-hant",
                ["pt"] = "pt-br",
                ["ptbr"] = "pt-br",
                ["portuguese"] = "pt-br",
                ["kor"] = "ko",
                ["ko-kr"] = "ko",
                ["korean"] = "ko",
                ["rus"] = "ru",
                ["ru-ru"] = "ru",
                ["russian"] = "ru"
            };

        /// <summary>
        /// Normalises a language code: trimmed, lower-cased, and with the known aliases collapsed
        /// (<c>JP</c> and <c>ja-JP</c> both become <c>ja</c>).
        /// </summary>
        /// <param name="code">Any spelling of a language code, or null.</param>
        /// <returns>The canonical code, or <see cref="string.Empty"/> for null/blank input.</returns>
        public static string Normalize(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return string.Empty;
            }

            var trimmed = code!.Trim();
            if (Aliases.TryGetValue(trimmed, out var alias))
            {
                return alias;
            }

            return trimmed.ToLowerInvariant();
        }

        /// <summary>True when two codes name the same language after normalisation.</summary>
        /// <param name="a">First code.</param>
        /// <param name="b">Second code.</param>
        public static bool Same(string? a, string? b)
        {
            var left = Normalize(a);
            var right = Normalize(b);
            return left.Length > 0 && string.Equals(left, right, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every language the game itself ships, as codes: the seven values of
        /// <c>Bulbul.GameLanguageType</c> other than <c>None</c>.
        /// </summary>
        public static IReadOnlyList<string> InGame { get; } = new[]
        {
            Japanese, English, "zh-hans", "zh-hant", "pt-br", "ko", "ru"
        };

        /// <summary>
        /// True when the game can display this language itself - one of its own seven. A pack may
        /// not declare such a code again: its rows are changed through <c>data/localization.json</c>.
        /// </summary>
        /// <param name="code">A language code in any spelling.</param>
        public static bool IsInGame(string? code)
        {
            var normalized = Normalize(code);
            if (normalized.Length == 0)
            {
                return false;
            }

            for (var i = 0; i < InGame.Count; i++)
            {
                if (string.Equals(InGame[i], normalized, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
