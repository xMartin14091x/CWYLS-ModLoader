using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// One language a content pack declares in its <c>language.json</c>: what it is called, how to
    /// fall back when a row is missing, which font draws it, and which script helpers apply to it.
    /// Immutable; build one with <see cref="LanguageDeclarationParser"/> rather than by hand outside
    /// tests.
    /// </summary>
    /// <remarks>
    /// The full schema is in the author's guide (<c>docs/guide/manifest.md</c>). A declaration says
    /// nothing about <em>which</em> rows are translated: those are the pack's
    /// <c>data/subtitles/&lt;code&gt;.json</c> text table, the same convention every pack already
    /// uses for text in a language the game has no column for.
    /// </remarks>
    public sealed class LanguageDeclaration
    {
        private static readonly string[] NoStrings = new string[0];

        /// <summary>Creates a declaration. Arguments are taken as given; the parser validates them.</summary>
        /// <param name="packId">The pack that declared it.</param>
        /// <param name="code">Normalised language code, e.g. <c>th</c>.</param>
        /// <param name="caption">What the language selector shows: the language's own name for itself.</param>
        /// <param name="englishName">The English name, for logs.</param>
        /// <param name="script">Lower-cased ISO 15924 script name (<c>thai</c>), or empty when not declared.</param>
        /// <param name="fallback">Normalised codes tried in order when a row is missing. Never contains <paramref name="code"/>.</param>
        /// <param name="fontAssets">Names of font assets the game ships that draw this language, in preference order.</param>
        /// <param name="marksPath">Pack-relative path of the mark-positioning table, or null.</param>
        /// <param name="lineBreaks">Line-break helper id (see <see cref="LineBreakHelpers"/>).</param>
        /// <param name="sourcePath">The pack-relative file it came from, for messages.</param>
        /// <exception cref="ArgumentException"><paramref name="packId"/> or <paramref name="code"/> is blank.</exception>
        public LanguageDeclaration(
            string packId,
            string code,
            string caption,
            string englishName,
            string? script = null,
            IReadOnlyList<string>? fallback = null,
            IReadOnlyList<string>? fontAssets = null,
            string? marksPath = null,
            string? lineBreaks = null,
            string? sourcePath = null)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("a language code is required", nameof(code));
            }

            PackId = packId;
            Code = code;
            Caption = caption ?? string.Empty;
            EnglishName = englishName ?? string.Empty;
            Script = script ?? string.Empty;
            Fallback = fallback ?? NoStrings;
            FontAssets = fontAssets ?? NoStrings;
            MarksPath = string.IsNullOrWhiteSpace(marksPath) ? null : marksPath;
            LineBreaks = string.IsNullOrWhiteSpace(lineBreaks) ? LineBreakHelpers.Auto : lineBreaks!;
            SourcePath = string.IsNullOrWhiteSpace(sourcePath) ? LanguageDeclarationParser.FileName : sourcePath!;
        }

        /// <summary>The pack that declared this language.</summary>
        public string PackId { get; }

        /// <summary>Normalised language code, e.g. <c>th</c>. Unique across every loaded pack.</summary>
        public string Code { get; }

        /// <summary>The selector caption: the endonym, e.g. <c>ไทย</c>.</summary>
        public string Caption { get; }

        /// <summary>The English name, e.g. <c>Thai</c>, for logs and the smoke report.</summary>
        public string EnglishName { get; }

        /// <summary>Lower-cased ISO 15924 script name, e.g. <c>thai</c>; empty when not declared.</summary>
        public string Script { get; }

        /// <summary>
        /// Codes tried, in order, when this language's table has no row. May name another pack
        /// language or one of the game's own; empty means "English".
        /// </summary>
        public IReadOnlyList<string> Fallback { get; }

        /// <summary>
        /// Font assets the game already ships that can draw this language, e.g.
        /// <c>Base_TH</c>, <c>Dynamic_TH</c>. Empty when the game's own fonts already cover it.
        /// </summary>
        public IReadOnlyList<string> FontAssets { get; }

        /// <summary>Pack-relative path of the mark-positioning table, e.g. <c>fonts/thai-marks.json</c>, or null.</summary>
        public string? MarksPath { get; }

        /// <summary>
        /// The line-break helper: <see cref="LineBreakHelpers.Auto"/> (pick by <see cref="Script"/>),
        /// <see cref="LineBreakHelpers.None"/>, or a helper id such as <c>thai</c>.
        /// </summary>
        public string LineBreaks { get; }

        /// <summary>The pack-relative file this came from, e.g. <c>language.json</c>.</summary>
        public string SourcePath { get; }

        /// <summary><c>code (English name) from pack</c>.</summary>
        public override string ToString() => Code + " (" + EnglishName + ") from " + PackId;
    }
}
