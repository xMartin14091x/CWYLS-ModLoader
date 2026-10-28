using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// Script-level text helpers a language declaration can switch on: today one line-break helper,
    /// selected by the declared <em>script</em>, never by a language code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TextMeshPro breaks lines at spaces and zero-width spaces only. A script written without
    /// spaces (Thai, Lao, Khmer, Burmese) therefore wraps mid-syllable unless something marks the
    /// break opportunities. A helper inserts <see cref="ThaiText.SoftBreak"/> (U+200B), which is
    /// invisible. The Thai helper also applies <see cref="ThaiText.ShapeForDisplay"/> (a tone mark
    /// before ำ is drawn as ํ + tone + า, the font's own split), because TextMeshPro runs none of the
    /// font's Thai rules. <see cref="Strip"/> undoes both, so a drawn line compares with its source.
    /// </para>
    /// <para>
    /// The helper for a declaration is resolved once: an explicit <c>lineBreaks</c> id wins, <c>none</c>
    /// switches it off, and <c>auto</c> (the default) picks the helper registered for the declared
    /// <c>script</c>, if there is one.
    /// </para>
    /// </remarks>
    public static class LineBreakHelpers
    {
        /// <summary>Pick the helper registered for the declared script.</summary>
        public const string Auto = "auto";

        /// <summary>No helper: the text is displayed exactly as the pack wrote it.</summary>
        public const string None = "none";

        /// <summary>Syllable-boundary breaks for the Thai script (<see cref="ThaiText.AddSoftBreaks"/>).</summary>
        public const string Thai = "thai";

        private static readonly Dictionary<string, Func<string, string>> Helpers =
            new Dictionary<string, Func<string, string>>(StringComparer.Ordinal)
            {
                [Thai] = text => ThaiText.ForDisplay(text) ?? text,
            };

        /// <summary>The character rewrites of each helper without its line breaks (<see cref="Shape"/>).</summary>
        private static readonly Dictionary<string, Func<string, string>> Shapers =
            new Dictionary<string, Func<string, string>>(StringComparer.Ordinal)
            {
                [Thai] = text => ThaiText.ShapeForDisplay(text) ?? text,
            };

        private static readonly Dictionary<string, string> ByScript =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // ISO 15924 name, lower-cased -> helper id.
                ["thai"] = Thai,
            };

        /// <summary>Every helper id a declaration may name, plus <see cref="Auto"/> and <see cref="None"/>.</summary>
        public static IReadOnlyList<string> KnownIds { get; } = new[] { Auto, None, Thai };

        /// <summary>True when <paramref name="id"/> is <see cref="Auto"/>, <see cref="None"/> or a registered helper.</summary>
        /// <param name="id">A helper id, as written in <c>language.json</c>.</param>
        public static bool IsKnown(string? id)
        {
            var key = Key(id);
            return key == Auto || key == None || Helpers.ContainsKey(key);
        }

        /// <summary>
        /// The helper a declaration ends up with.
        /// </summary>
        /// <param name="lineBreaks">The declaration's <c>lineBreaks</c> value.</param>
        /// <param name="script">The declaration's <c>script</c> value.</param>
        /// <returns>A registered helper id, or the empty string for "no helper".</returns>
        public static string Resolve(string? lineBreaks, string? script)
        {
            var key = Key(lineBreaks);
            if (key.Length == 0 || key == Auto)
            {
                return ByScript.TryGetValue(Key(script), out var byScript) ? byScript : string.Empty;
            }

            return Helpers.ContainsKey(key) ? key : string.Empty;
        }

        /// <summary>Applies a helper to one line. An empty or unknown helper returns the text unchanged.</summary>
        /// <param name="helperId">A resolved helper id from <see cref="Resolve"/>.</param>
        /// <param name="text">The text to display; null is returned as empty.</param>
        public static string Apply(string? helperId, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return !string.IsNullOrEmpty(helperId) && Helpers.TryGetValue(helperId!, out var helper)
                ? helper(text!)
                : text!;
        }

        /// <summary>
        /// Applies only a helper's character rewrites, not its line breaks - for text drawn while line
        /// breaks are switched off. An empty or unknown helper returns the text unchanged.
        /// </summary>
        /// <param name="helperId">A resolved helper id from <see cref="Resolve"/>.</param>
        /// <param name="text">The text to display; null is returned as empty.</param>
        public static string Shape(string? helperId, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return !string.IsNullOrEmpty(helperId) && Shapers.TryGetValue(helperId!, out var shaper)
                ? shaper(text!)
                : text!;
        }

        /// <summary>Undoes every helper (soft breaks and display rewrites), so displayed text compares with its source.</summary>
        /// <param name="text">The displayed text; null is returned as empty.</param>
        public static string Strip(string? text) => ThaiText.FromDisplay(text) ?? string.Empty;

        private static string Key(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value!.Trim().ToLowerInvariant();
    }
}
