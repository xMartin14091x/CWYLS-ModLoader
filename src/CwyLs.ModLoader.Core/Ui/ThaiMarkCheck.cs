using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>One drawn character as TextMeshPro laid it out (from <c>textInfo.characterInfo</c>).</summary>
    public readonly struct GlyphBox
    {
        /// <summary>Creates a glyph box.</summary>
        /// <param name="codePoint">The character.</param>
        /// <param name="font">The font asset that drew it.</param>
        /// <param name="missing">True when the font had no glyph for it (glyph index 0).</param>
        /// <param name="minX">Left edge.</param>
        /// <param name="maxX">Right edge.</param>
        /// <param name="minY">Bottom edge.</param>
        /// <param name="maxY">Top edge.</param>
        public GlyphBox(int codePoint, string font, bool missing, float minX, float maxX, float minY, float maxY)
        {
            CodePoint = codePoint;
            Font = font ?? string.Empty;
            Missing = missing;
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        /// <summary>The character.</summary>
        public int CodePoint { get; }

        /// <summary>The font asset that drew it.</summary>
        public string Font { get; }

        /// <summary>True when the font had no glyph for it.</summary>
        public bool Missing { get; }

        /// <summary>Left edge.</summary>
        public float MinX { get; }

        /// <summary>Right edge.</summary>
        public float MaxX { get; }

        /// <summary>Bottom edge.</summary>
        public float MinY { get; }

        /// <summary>Top edge.</summary>
        public float MaxY { get; }
    }

    /// <summary>What <see cref="ThaiMarkCheck.Check"/> found.</summary>
    public sealed class ThaiMarkReport
    {
        internal ThaiMarkReport(int thaiCharacters, int missing, int marksChecked, IReadOnlyList<string> problems, IReadOnlyDictionary<string, int> fonts)
        {
            ThaiCharacters = thaiCharacters;
            Missing = missing;
            MarksChecked = marksChecked;
            Problems = problems;
            Fonts = fonts;
        }

        /// <summary>Thai characters drawn.</summary>
        public int ThaiCharacters { get; }

        /// <summary>Characters (other than spaces) the fonts had no glyph for.</summary>
        public int Missing { get; }

        /// <summary>Tone marks after an upper vowel that were checked.</summary>
        public int MarksChecked { get; }

        /// <summary>Each misplaced mark, one line each.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>Thai characters per font asset that drew them.</summary>
        public IReadOnlyDictionary<string, int> Fonts { get; }

        /// <summary>True when every Thai character has a glyph and every checked mark sits right.</summary>
        public bool Ok => ThaiCharacters > 0 && Missing == 0 && Problems.Count == 0;

        /// <summary>One line for a smoke result.</summary>
        public string Describe()
        {
            var fonts = new StringBuilder();
            foreach (var pair in Fonts)
            {
                if (fonts.Length > 0)
                {
                    fonts.Append(", ");
                }

                fonts.Append(pair.Key).Append(" x").Append(pair.Value.ToString(CultureInfo.InvariantCulture));
            }

            var text = ThaiCharacters + " Thai characters (" + fonts + "), " + Missing + " missing, "
                + MarksChecked + " stacked tone marks checked";
            return Problems.Count == 0 ? text : text + "; " + string.Join("; ", Problems);
        }
    }

    /// <summary>
    /// Checks Thai text: every
    /// Thai character has a glyph, and each tone mark (U+0E48-U+0E4B) that follows an upper vowel
    /// (U+0E31, U+0E34-U+0E37, U+0E47) sits above it without overlapping and over its base consonant.
    /// Those are the cases the pack's mark records (<c>LanguageFonts</c>) fix. Pure.
    /// </summary>
    public static class ThaiMarkCheck
    {
        /// <summary>The line the check renders: five words where a tone mark stacks on an upper vowel.</summary>
        public const string Sample = "ที่นี่ ชื่อ นี้ เริ่ม ตั้ง";

        /// <summary>How far a mark may dip into the vowel below it (TextMeshPro rounding).</summary>
        public const float VerticalSlack = 1f;

        /// <summary>How far a mark's centre may sit outside its base consonant.</summary>
        public const float HorizontalSlack = 2f;

        /// <summary>True for a code point in the Thai block (U+0E00-U+0E7F).</summary>
        /// <param name="codePoint">The code point.</param>
        public static bool IsThai(int codePoint) => codePoint >= 0x0E00 && codePoint <= 0x0E7F;

        /// <summary>True for a tone mark.</summary>
        /// <param name="codePoint">The code point.</param>
        public static bool IsToneMark(int codePoint) => codePoint >= 0x0E48 && codePoint <= 0x0E4B;

        /// <summary>True for a vowel or sign drawn above the consonant.</summary>
        /// <param name="codePoint">The code point.</param>
        public static bool IsUpperVowel(int codePoint) =>
            codePoint == 0x0E31 || (codePoint >= 0x0E34 && codePoint <= 0x0E37) || codePoint == 0x0E47;

        /// <summary>Checks a laid-out line.</summary>
        /// <param name="glyphs">The characters in text order.</param>
        public static ThaiMarkReport Check(IReadOnlyList<GlyphBox> glyphs)
        {
            if (glyphs is null)
            {
                throw new ArgumentNullException(nameof(glyphs));
            }

            var thai = 0;
            var missing = 0;
            var checkedMarks = 0;
            var problems = new List<string>();
            var fonts = new SortedDictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < glyphs.Count; i++)
            {
                var glyph = glyphs[i];
                if (glyph.CodePoint != ' ' && glyph.Missing)
                {
                    missing++;
                }

                if (!IsThai(glyph.CodePoint))
                {
                    continue;
                }

                thai++;
                fonts[glyph.Font] = fonts.TryGetValue(glyph.Font, out var n) ? n + 1 : 1;

                if (!IsToneMark(glyph.CodePoint) || i < 2 || !IsUpperVowel(glyphs[i - 1].CodePoint))
                {
                    continue;
                }

                checkedMarks++;
                var vowel = glyphs[i - 1];
                var consonant = glyphs[i - 2];
                var centre = (glyph.MinX + glyph.MaxX) * 0.5f;
                var label = "U+" + glyph.CodePoint.ToString("X4", CultureInfo.InvariantCulture)
                    + " at " + i.ToString(CultureInfo.InvariantCulture);

                if (glyph.MinY < vowel.MaxY - VerticalSlack)
                {
                    problems.Add(label + " overlaps the vowel below it by "
                        + (vowel.MaxY - glyph.MinY).ToString("0.#", CultureInfo.InvariantCulture));
                }

                if (centre < consonant.MinX - HorizontalSlack || centre > consonant.MaxX + HorizontalSlack)
                {
                    problems.Add(label + " is not over its consonant");
                }
            }

            return new ThaiMarkReport(thai, missing, checkedMarks, problems, fonts);
        }
    }
}
