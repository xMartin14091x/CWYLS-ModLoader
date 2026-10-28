namespace CwyLs.ModLoader.Core.Subtitles
{
    /// <summary>
    /// Script-level helpers for text in the Thai script: detection by code point and syllable-
    /// boundary line breaks. Selected through a language declaration's <c>script</c>
    /// (<c>CwyLs.ModLoader.Core.Language.LineBreakHelpers</c>), never by a language code.
    /// </summary>
    /// <remarks>
    /// Thai is a single contiguous Unicode block, U+0E00-U+0E7F, and it is not shared with any
    /// other script, so a code-point test is exact - no heuristics, no language guessing. (The
    /// block also holds the Thai digits U+0E50-U+0E59; ASCII digits in a Thai line are Latin and
    /// do not make the line Thai on their own.)
    /// </remarks>
    public static class ThaiText
    {
        /// <summary>First code point of the Thai Unicode block.</summary>
        public const char BlockStart = '\u0E00';

        /// <summary>Last code point of the Thai Unicode block.</summary>
        public const char BlockEnd = '\u0E7F';

        /// <summary>True when the character is in the Thai Unicode block.</summary>
        /// <param name="value">The character to test.</param>
        public static bool IsThai(char value) => value >= BlockStart && value <= BlockEnd;

        /// <summary>True when at least one character of the text is Thai.</summary>
        /// <param name="text">The text to test; null or empty is false.</param>
        public static bool Contains(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (var i = 0; i < text!.Length; i++)
            {
                if (IsThai(text[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The zero-width space TextMeshPro accepts as a line-break opportunity
        /// (<c>TMP_Text.GenerateTextMesh</c> treats code point 8203 as breakable).
        /// </summary>
        public const char SoftBreak = '\u200B';

        /// <summary>First Thai consonant, ko kai.</summary>
        private const char FirstConsonant = '\u0E01';

        /// <summary>Last Thai consonant, ho nokhuk.</summary>
        private const char LastConsonant = '\u0E2E';

        /// <summary>First leading vowel, sara e.</summary>
        private const char FirstLeadingVowel = '\u0E40';

        /// <summary>Last leading vowel, sara ai maimalai.</summary>
        private const char LastLeadingVowel = '\u0E44';

        /// <summary>
        /// Characters that reliably end a Thai syllable or word, so a consonant right after one of
        /// them starts a new syllable. Deliberately short: every character here ends its syllable
        /// on its own. Sara aa (า) is <em>not</em> here and neither is any tone mark, because both
        /// can still be followed by a final consonant - งาน is one syllable, not งา + น.
        /// </summary>
        private static readonly char[] SyllableEnders =
        {
            '\u0E2F', // ฯ paiyannoi (abbreviation)
            '\u0E30', // ะ sara a - a full vowel that takes no final consonant
            '\u0E33', // ำ sara am - already ends in /m/, so nothing can follow it in the syllable
            '\u0E46', // ๆ maiyamok (repetition) - always ends a word
            '\u0E4C', // ์ thanthakhat - silences the letter it sits on, so the word ends
            '\u0E5A', // ๚ angkhankhu
            '\u0E5B'  // ๛ khomut
        };

        /// <summary>
        /// Inserts <see cref="SoftBreak"/>s at Thai syllable boundaries so a long Thai line wraps
        /// somewhere sensible.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Thai is written without spaces, and TextMeshPro has no Thai dictionary: left alone it
        /// breaks a line wherever it happens to run out of room, mid-syllable, which can strand a
        /// vowel or a tone mark on the next line. (Its CJK line-breaking rules -
        /// <c>TMP_Settings.linebreakingRules</c>, the leading/following-character files - do not
        /// cover the Thai block, and this game ships them unchanged.)
        /// </para>
        /// <para>
        /// So this marks the break opportunities a dictionary is not needed for, and only those:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// before a leading vowel (เ แ โ ใ ไ), which by definition begins a syllable;
        /// </description></item>
        /// <item><description>
        /// before a consonant that follows a character which can only end a syllable
        /// (ะ ำ ๆ ์ ฯ ๚ ๛).
        /// </description></item>
        /// </list>
        /// <para>
        /// It never breaks a consonant cluster, never separates a combining mark from its letter,
        /// and never breaks where the text already has a break. Where it finds nothing, TextMeshPro
        /// behaves exactly as it did before - this can only add opportunities, never take one away.
        /// </para>
        /// </remarks>
        /// <param name="text">The line to mark up; null or non-Thai text is returned unchanged.</param>
        /// <returns>The line with zero-width spaces inserted.</returns>
        public static string? AddSoftBreaks(string? text)
        {
            if (string.IsNullOrEmpty(text) || !Contains(text))
            {
                return text;
            }

            var marked = new System.Text.StringBuilder(text!.Length + 8);
            marked.Append(text[0]);

            for (var i = 1; i < text.Length; i++)
            {
                var previous = text[i - 1];
                var current = text[i];

                if (CanBreakBetween(previous, current))
                {
                    marked.Append(SoftBreak);
                }

                marked.Append(current);
            }

            return marked.ToString();
        }

        /// <summary>
        /// Removes every <see cref="SoftBreak"/> from a line, so text that was marked up can be
        /// compared with the text it came from.
        /// </summary>
        /// <param name="text">The line; null is returned unchanged.</param>
        public static string? StripSoftBreaks(string? text) =>
            text is null || text.IndexOf(SoftBreak) < 0 ? text : text.Replace(SoftBreak.ToString(), string.Empty);

        /// <summary>True when a line already carries soft breaks.</summary>
        /// <param name="text">The line to test.</param>
        public static bool HasSoftBreaks(string? text) => text != null && text.IndexOf(SoftBreak) >= 0;

        /// <summary>
        /// Rewrites a line for drawing with TextMeshPro, which runs none of the font's own Thai rules:
        /// a tone mark before sara am (น้ำ, ค่ำ, วนซ้ำ) becomes nikhahit + tone mark + sara aa, and on a
        /// tall letter (ป ฝ ฟ ฬ) a mark above is put before a vowel below (ฟู่ is drawn as ฟ ่ ู).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The game's Thai font (IBM Plex Sans Thai Looped) splits ำ into its circle (nikhahit, U+0E4D)
        /// and sara aa (U+0E32) and stacks the tone mark above the circle; HarfBuzz shapes น้ำ as
        /// น ํ ้ า. TextMeshPro draws the ำ glyph whole, so the tone mark lands on the circle. After this
        /// split the circle is its own mark, and the (ํ, tone) pairs of the mark table
        /// (a language pack's <c>fonts/thai-marks.json</c>) lift the tone mark above it.
        /// </para>
        /// <para>
        /// TextMeshPro's pair records only see neighbours. The mark table moves a mark above ป ฝ ฟ ฬ off
        /// the tall stroke with a (letter, mark) row, but in ฟู่ the vowel below sits between them. Both
        /// marks have no width and are drawn from the same pen position, so putting the upper mark
        /// first changes nothing on screen except that its row now applies.
        /// </para>
        /// <para>
        /// Display only: the text tables keep the canonical spelling, and
        /// <see cref="UnshapeForDisplay"/> turns a drawn line back into it for comparisons.
        /// </para>
        /// </remarks>
        /// <param name="text">The line; null, or a line with nothing to rewrite, is returned unchanged.</param>
        public static string? ShapeForDisplay(string? text)
        {
            if (text is null || !NeedsShaping(text))
            {
                return text;
            }

            var shaped = new System.Text.StringBuilder(text.Length + 4);
            for (var i = 0; i < text.Length; i++)
            {
                if (IsToneMark(text[i]) && i + 1 < text.Length && text[i + 1] == SaraAm)
                {
                    shaped.Append(Nikhahit).Append(text[i]).Append(SaraAa);
                    i++;
                    continue;
                }

                if (IsTallLetter(text[i]) && i + 2 < text.Length && IsLowerVowel(text[i + 1]) && IsMarkAboveAfterLower(text[i + 2]))
                {
                    shaped.Append(text[i]).Append(text[i + 2]).Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                shaped.Append(text[i]);
            }

            return shaped.ToString();
        }

        /// <summary>The inverse of <see cref="ShapeForDisplay"/>: nikhahit + tone mark + sara aa back to tone mark + sara am.</summary>
        /// <param name="text">A drawn line; null is returned unchanged.</param>
        public static string? UnshapeForDisplay(string? text)
        {
            if (text is null || text.Length < 3)
            {
                return text;
            }

            System.Text.StringBuilder? plain = null;
            for (var i = 0; i < text.Length; i++)
            {
                if (i + 2 < text.Length && text[i] == Nikhahit && IsToneMark(text[i + 1]) && text[i + 2] == SaraAa)
                {
                    plain ??= new System.Text.StringBuilder(text.Length).Append(text, 0, i);
                    plain.Append(text[i + 1]).Append(SaraAm);
                    i += 2;
                    continue;
                }

                if (i + 2 < text.Length && IsTallLetter(text[i]) && IsMarkAboveAfterLower(text[i + 1]) && IsLowerVowel(text[i + 2]))
                {
                    plain ??= new System.Text.StringBuilder(text.Length).Append(text, 0, i);
                    plain.Append(text[i]).Append(text[i + 2]).Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                plain?.Append(text[i]);
            }

            if (plain is null)
            {
                return text;
            }

            return plain.ToString();
        }

        /// <summary>
        /// Everything a Thai line needs before TextMeshPro draws it: syllable soft breaks (on the
        /// canonical spelling, where ำ still ends its syllable), then <see cref="ShapeForDisplay"/>.
        /// </summary>
        /// <param name="text">The line; null is returned unchanged.</param>
        public static string? ForDisplay(string? text) => ShapeForDisplay(AddSoftBreaks(text));

        /// <summary>The inverse of <see cref="ForDisplay"/>, for comparing a drawn line with its source.</summary>
        /// <param name="text">A drawn line; null is returned unchanged.</param>
        public static string? FromDisplay(string? text) => UnshapeForDisplay(StripSoftBreaks(text));

        /// <summary>Sara am, ำ.</summary>
        private const char SaraAm = 'ำ';

        /// <summary>Sara aa, า.</summary>
        private const char SaraAa = 'า';

        /// <summary>Nikhahit, ํ - the circle of sara am.</summary>
        private const char Nikhahit = 'ํ';

        /// <summary>Mai ek, mai tho, mai tri, mai chattawa (U+0E48-U+0E4B).</summary>
        private static bool IsToneMark(char value) => value >= '่' && value <= '๋';

        /// <summary>ป ฝ ฟ ฬ: the letters whose tall stroke the mark table moves marks off.</summary>
        private static bool IsTallLetter(char value) =>
            value == '\u0E1B' || value == '\u0E1D' || value == '\u0E1F' || value == '\u0E2C';

        /// <summary>Sara u, sara uu, phinthu (U+0E38-U+0E3A).</summary>
        private static bool IsLowerVowel(char value) => value >= '\u0E38' && value <= '\u0E3A';

        /// <summary>A mark written after a vowel below: the four tone marks and thanthakhat (U+0E48-U+0E4C).</summary>
        private static bool IsMarkAboveAfterLower(char value) => value >= '\u0E48' && value <= '\u0E4C';

        /// <summary>True when <see cref="ShapeForDisplay"/> has anything to rewrite in the text.</summary>
        private static bool NeedsShaping(string text)
        {
            if (HasToneBeforeSaraAm(text))
            {
                return true;
            }

            for (var i = 0; i + 2 < text.Length; i++)
            {
                if (IsTallLetter(text[i]) && IsLowerVowel(text[i + 1]) && IsMarkAboveAfterLower(text[i + 2]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the text has a tone mark right before sara am. Checked first so the common case
        /// (no such pair) returns the caller's own string, without building a copy - the text seam
        /// runs for every localized string the game reads.
        /// </summary>
        private static bool HasToneBeforeSaraAm(string text)
        {
            for (var i = text.IndexOf(SaraAm); i >= 0; i = text.IndexOf(SaraAm, i + 1))
            {
                if (i > 0 && IsToneMark(text[i - 1]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanBreakBetween(char previous, char current)
        {
            // Never stack a break on an existing one, and never break next to whitespace: the text
            // can already be broken there.
            if (previous == SoftBreak || current == SoftBreak
                || char.IsWhiteSpace(previous) || char.IsWhiteSpace(current))
            {
                return false;
            }

            if (!IsThai(previous))
            {
                return false;
            }

            // A leading vowel always starts a syllable - unless the character before it is one too
            // (เเ is a typo for แ, and breaking inside it would look wrong).
            if (IsLeadingVowel(current))
            {
                return !IsLeadingVowel(previous);
            }

            return IsConsonant(current) && IsSyllableEnder(previous);
        }

        private static bool IsConsonant(char value) => value >= FirstConsonant && value <= LastConsonant;

        private static bool IsLeadingVowel(char value) =>
            value >= FirstLeadingVowel && value <= LastLeadingVowel;

        private static bool IsSyllableEnder(char value)
        {
            for (var i = 0; i < SyllableEnders.Length; i++)
            {
                if (SyllableEnders[i] == value)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
