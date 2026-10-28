using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Subtitles
{
    /// <summary>
    /// A mark-positioning table: for a pair of combining marks drawn on the same letter, how far to
    /// move the second so it stacks above the first instead of drawing inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TextMeshPro 3.0.9 runs none of a font's GSUB, <c>mark</c> or <c>mkmk</c> features; the one
    /// piece of OpenType positioning it honours is a pair-adjustment record, looked up on the
    /// current glyph's own font asset (<c>TextMeshProUGUI</c>) and applied as <c>yPlacement</c>.
    /// A table like this one, derived from the font's own <c>mkmk</c> anchors, is what turns into
    /// those records. The format is script-neutral: nothing here knows
    /// which script the marks belong to.
    /// </para>
    /// <para>The file (schema 1), as the tool writes it:</para>
    /// <code>
    /// {
    ///   "schema": 1,
    ///   "font": { "family": "IBM Plex Sans Thai Looped", "style": "Medium", "file": "...", "unitsPerEm": 1000 },
    ///   "source": { ... },                       // provenance, not read
    ///   "fallbackGap": 30,                       // not read
    ///   "marks": [ { "code": "0E34", "glyph": "uni0E34", "gid": 460, ... } ],   // optional: glyph ids
    ///   "pairs": [ { "preceding": "0E34", "mark": "0E48", "dx": -4, "dy": 249 } ]
    /// }
    /// </code>
    /// <para>
    /// <c>dx</c>/<c>dy</c> are in font units of <c>unitsPerEm</c>; a positive <c>dy</c> raises the mark.
    /// </para>
    /// </remarks>
    public sealed class MarkTable
    {
        /// <summary>The only schema version this reader understands.</summary>
        public const int Schema = 1;

        private static readonly string[] KnownKeys = { "schema", "font", "source", "fallbackGap", "marks", "pairs" };

        private readonly Dictionary<int, uint> _glyphIds;

        private MarkTable(
            string family,
            string style,
            int unitsPerEm,
            IReadOnlyList<MarkPair> pairs,
            Dictionary<int, uint> glyphIds)
        {
            Family = family;
            Style = style;
            UnitsPerEm = unitsPerEm;
            Pairs = pairs;
            _glyphIds = glyphIds;
        }

        /// <summary>The font family the table was measured from; the adapter applies it only to assets of this family.</summary>
        public string Family { get; }

        /// <summary>The style, e.g. <c>Medium</c>; informational.</summary>
        public string Style { get; }

        /// <summary>Font units per em of the measured font; <c>dx</c>/<c>dy</c> are in these units.</summary>
        public int UnitsPerEm { get; }

        /// <summary>The pairs, in file order.</summary>
        public IReadOnlyList<MarkPair> Pairs { get; }

        /// <summary>
        /// The measured font's glyph id for a code point, when the file lists it. Used for a font
        /// asset of the same family whose own character table does not hold the character yet
        /// (a dynamic asset fills its table only as text is drawn).
        /// </summary>
        /// <param name="codePoint">A Unicode code point.</param>
        /// <param name="glyphId">The glyph id, or 0.</param>
        /// <returns>True when the file lists the code point.</returns>
        public bool TryGetGlyphId(int codePoint, out uint glyphId) => _glyphIds.TryGetValue(codePoint, out glyphId);

        /// <summary>Parses a table.</summary>
        /// <param name="json">The whole file.</param>
        /// <param name="file">Name used in messages, e.g. <c>my.lang/fonts/thai-marks.json</c>.</param>
        /// <returns>The table.</returns>
        /// <exception cref="FormatException">The file is not a valid schema-1 table; the message names the member.</exception>
        public static MarkTable Parse(string? json, string file)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException(file + ": the file is empty");
            }

            if (!JsonReader.TryParse(json!, out var document, out var error))
            {
                throw new FormatException(file + ": invalid JSON at " + error);
            }

            if (!(document is JsonObject root))
            {
                throw new FormatException(file + ": the top-level value must be an object, not " + JsonReader.TypeName(document));
            }

            foreach (var member in root)
            {
                if (Array.IndexOf(KnownKeys, member.Key) < 0 && !member.Key.StartsWith("x-", StringComparison.Ordinal))
                {
                    throw new FormatException(
                        file + ": unknown member \"" + member.Key + "\"; expected " + string.Join(", ", KnownKeys));
                }
            }

            var schema = Integer(root, "schema", file);
            if (schema != Schema)
            {
                throw new FormatException(file + ": \"schema\" is " + schema.ToString(CultureInfo.InvariantCulture)
                    + ", this framework reads " + Schema.ToString(CultureInfo.InvariantCulture));
            }

            if (!root.TryGetValue("font", out var fontValue) || !(fontValue is JsonObject font))
            {
                throw new FormatException(file + ": \"font\" must be an object with family and unitsPerEm");
            }

            var family = font.TryGetValue("family", out var f) && f is string familyText && familyText.Trim().Length > 0
                ? familyText.Trim()
                : throw new FormatException(file + ": \"font.family\" is required");
            var style = font.TryGetValue("style", out var s) && s is string styleText ? styleText.Trim() : string.Empty;
            var unitsPerEm = Integer(font, "unitsPerEm", file + ": \"font\"");
            if (unitsPerEm <= 0)
            {
                throw new FormatException(file + ": \"font.unitsPerEm\" must be positive");
            }

            var glyphIds = ParseGlyphIds(root, file);
            var pairs = ParsePairs(root, file);

            return new MarkTable(family, style, unitsPerEm, pairs, glyphIds);
        }

        /// <summary>Parses a table, reporting failure instead of throwing.</summary>
        /// <param name="json">The whole file.</param>
        /// <param name="file">Name used in messages.</param>
        /// <param name="table">The table, or null.</param>
        /// <param name="error">Why it failed, or null.</param>
        /// <returns>True on success.</returns>
        public static bool TryParse(string? json, string file, out MarkTable? table, out string? error)
        {
            try
            {
                table = Parse(json, file);
                error = null;
                return true;
            }
            catch (FormatException ex)
            {
                table = null;
                error = ex.Message;
                return false;
            }
        }

        private static List<MarkPair> ParsePairs(JsonObject root, string file)
        {
            if (!root.TryGetValue("pairs", out var value) || !(value is IList<object?> list) || list.Count == 0)
            {
                throw new FormatException(file + ": \"pairs\" must be a non-empty array");
            }

            var pairs = new List<MarkPair>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                var where = file + ": \"pairs\"[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (!(list[i] is JsonObject pair))
                {
                    throw new FormatException(where + " must be an object");
                }

                pairs.Add(new MarkPair(
                    CodePoint(pair, "preceding", where),
                    CodePoint(pair, "mark", where),
                    Integer(pair, "dx", where),
                    Integer(pair, "dy", where)));
            }

            return pairs;
        }

        private static Dictionary<int, uint> ParseGlyphIds(JsonObject root, string file)
        {
            var ids = new Dictionary<int, uint>();
            if (!root.TryGetValue("marks", out var value) || value is null)
            {
                return ids;
            }

            if (!(value is IList<object?> list))
            {
                throw new FormatException(file + ": \"marks\" must be an array");
            }

            for (var i = 0; i < list.Count; i++)
            {
                var where = file + ": \"marks\"[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (!(list[i] is JsonObject mark))
                {
                    throw new FormatException(where + " must be an object");
                }

                if (!mark.TryGetValue("gid", out var gid) || gid is null)
                {
                    continue;
                }

                var id = Integer(mark, "gid", where);
                if (id < 0)
                {
                    throw new FormatException(where + ": \"gid\" must not be negative");
                }

                ids[CodePoint(mark, "code", where)] = (uint)id;
            }

            return ids;
        }

        private static int CodePoint(JsonObject obj, string key, string where)
        {
            if (!obj.TryGetValue(key, out var value) || !(value is string hex)
                || !int.TryParse(hex.Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                || code <= 0 || code > 0x10FFFF)
            {
                throw new FormatException(where + ": \"" + key + "\" must be a hexadecimal code point such as \"0E48\"");
            }

            return code;
        }

        private static int Integer(JsonObject obj, string key, string where)
        {
            if (!obj.TryGetValue(key, out var value) || !(value is long number) || number < int.MinValue || number > int.MaxValue)
            {
                throw new FormatException(where + ": \"" + key + "\" must be an integer");
            }

            return (int)number;
        }
    }

    /// <summary>One row of a <see cref="MarkTable"/>: a mark that follows another mark, and how far it moves.</summary>
    public readonly struct MarkPair
    {
        /// <summary>Creates a pair.</summary>
        /// <param name="preceding">The code point of the mark already on the letter.</param>
        /// <param name="mark">The code point of the mark that follows it.</param>
        /// <param name="dx">Horizontal offset in font units; positive moves right.</param>
        /// <param name="dy">Vertical offset in font units; positive raises.</param>
        public MarkPair(int preceding, int mark, int dx, int dy)
        {
            Preceding = preceding;
            Mark = mark;
            Dx = dx;
            Dy = dy;
        }

        /// <summary>The code point of the mark already on the letter.</summary>
        public int Preceding { get; }

        /// <summary>The code point of the mark that follows it - the one that moves.</summary>
        public int Mark { get; }

        /// <summary>Horizontal offset in font units.</summary>
        public int Dx { get; }

        /// <summary>Vertical offset in font units.</summary>
        public int Dy { get; }

        /// <summary><c>U+0E35+U+0E48 (dx, dy)</c>.</summary>
        public override string ToString() =>
            "U+" + Preceding.ToString("X4", CultureInfo.InvariantCulture) + "+U+" + Mark.ToString("X4", CultureInfo.InvariantCulture)
            + " (" + Dx.ToString(CultureInfo.InvariantCulture) + ", " + Dy.ToString(CultureInfo.InvariantCulture) + ")";
    }
}
