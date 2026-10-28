using System;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// What the game's text seam returns for one row in a pack language: the pack's own row, or
    /// the first fallback that has one.
    /// </summary>
    /// <remarks>
    /// This is the whole policy behind <c>LocalizationMasterWrapper.Get</c> for a pack language, kept
    /// Unity-free so every branch is unit-tested. The adapter supplies the two things only the game
    /// has: the text tables (<see cref="ISubtitleSource"/>, i.e. the packs' <c>data/subtitles</c>
    /// and <c>Satone.Say</c>'s own lines) and the game's own column for a built-in language.
    /// </remarks>
    public static class LanguageText
    {
        /// <summary>
        /// Resolves one row for a pack language by walking its <see cref="PackLanguage.Chain"/>.
        /// </summary>
        /// <param name="language">The pack language the game asked for.</param>
        /// <param name="localizationId">The master-data row id.</param>
        /// <param name="tables">The text tables to ask for pack codes; null means none.</param>
        /// <param name="gameColumn">
        /// The game's own text for a built-in value (1-7), or null/empty when that column is blank.
        /// Null means the game's columns are not consulted.
        /// </param>
        /// <returns>The text to display and where it came from; <see cref="LanguageTextResult.Found"/> is false when nothing had it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="language"/> is null.</exception>
        public static LanguageTextResult Resolve(
            PackLanguage language,
            string? localizationId,
            ISubtitleSource? tables,
            Func<int, string?>? gameColumn)
        {
            if (language is null)
            {
                throw new ArgumentNullException(nameof(language));
            }

            if (string.IsNullOrEmpty(localizationId))
            {
                return LanguageTextResult.None;
            }

            foreach (var step in language.Chain)
            {
                if (step.IsBuiltIn)
                {
                    var column = gameColumn?.Invoke(step.Value);
                    if (!string.IsNullOrEmpty(column))
                    {
                        return new LanguageTextResult(column!, step.Code, fromPack: false);
                    }

                    continue;
                }

                if (tables != null
                    && tables.TryGet(localizationId!, step.Code, out var text)
                    && !string.IsNullOrEmpty(text))
                {
                    return new LanguageTextResult(text, step.Code, fromPack: true);
                }
            }

            return LanguageTextResult.None;
        }
    }

    /// <summary>The answer of <see cref="LanguageText.Resolve"/>.</summary>
    public readonly struct LanguageTextResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="text">The text.</param>
        /// <param name="code">The language the text is in.</param>
        /// <param name="fromPack">True when a pack table supplied it, false for a game column.</param>
        public LanguageTextResult(string text, string code, bool fromPack)
        {
            Text = text;
            Code = code;
            FromPack = fromPack;
        }

        /// <summary>Nothing had the row.</summary>
        public static LanguageTextResult None => default;

        /// <summary>The text, or null when <see cref="Found"/> is false.</summary>
        public string? Text { get; }

        /// <summary>The language code the text is in, e.g. <c>th</c> or the fallback <c>en</c>.</summary>
        public string? Code { get; }

        /// <summary>True when a pack table supplied the text; false for one of the game's own columns.</summary>
        public bool FromPack { get; }

        /// <summary>True when there is text to show.</summary>
        public bool Found => !string.IsNullOrEmpty(Text);

        /// <summary><c>pack:th</c>, <c>game:en</c> or <c>none</c>.</summary>
        public override string ToString() => Found ? (FromPack ? "pack:" : "game:") + Code : "none";
    }
}
