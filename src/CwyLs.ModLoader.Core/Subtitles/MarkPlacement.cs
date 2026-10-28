using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Subtitles
{
    /// <summary>Which offsets of a <see cref="MarkTable"/> are applied.</summary>
    public enum MarkPlacementMode
    {
        /// <summary>Both offsets, i.e. the font's own design (the default).</summary>
        Full,

        /// <summary>Only the vertical offset: the mark is raised clear of the one below and not moved sideways.</summary>
        VerticalOnly,

        /// <summary>Nothing is applied.</summary>
        Off,
    }

    /// <summary>
    /// Turns a <see cref="MarkTable"/> into the pair-adjustment records one font asset needs - the
    /// pure half of installing a table, so the arithmetic and every refusal are unit-tested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Units.</b> TextMeshPro stores a font asset's glyph metrics at the asset's sampling point
    /// size, and <c>yPlacement</c> is added to <c>horizontalBearingY</c> before both are scaled
    /// (<c>TextMeshProUGUI</c>). So an offset of <c>d</c> font units becomes
    /// <c>d × pointSize / unitsPerEm</c> in the asset's own units: ×0.02 for the game's 20-point
    /// <c>Base_TH</c>.
    /// </para>
    /// <para>
    /// <b>The record's two halves.</b> TMP reads the same record twice: while laying out the first
    /// glyph (its <c>firstAdjustmentRecord</c>) and while laying out the second (its
    /// <c>secondAdjustmentRecord</c>, keyed on the previous glyph, <c>:1704-1707</c>). Only the
    /// second half carries the offset; the first stays zero, or the lower mark would move too.
    /// <see cref="MarkRecord"/> therefore holds just the second glyph's offset.
    /// </para>
    /// <para>
    /// <b>Refusals.</b> The table was measured on one typeface. An asset of another family gets
    /// nothing rather than offsets meant for different outlines; a pair whose glyph cannot be found,
    /// or whose glyph index does not fit TMP's 16-bit key (<c>GlyphPairKey</c>), is skipped.
    /// </para>
    /// </remarks>
    public static class MarkPlacement
    {
        /// <summary>The largest glyph index TMP's pair key can hold.</summary>
        public const uint MaxGlyphIndex = 0xFFFF;

        /// <summary>Plans the records for one font asset.</summary>
        /// <param name="table">The table.</param>
        /// <param name="assetFamily">The asset's <c>faceInfo.familyName</c>.</param>
        /// <param name="assetPointSize">The asset's <c>faceInfo.pointSize</c>.</param>
        /// <param name="assetGlyphOf">
        /// The asset's glyph index for a code point, or null when its character table does not hold
        /// it. When null, the table's own glyph id is used - same family, same font file.
        /// </param>
        /// <param name="mode">Which offsets to apply.</param>
        /// <returns>The plan. Never null.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
        public static MarkPlan Plan(
            MarkTable table,
            string? assetFamily,
            float assetPointSize,
            Func<int, uint?>? assetGlyphOf,
            MarkPlacementMode mode)
        {
            if (table is null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            if (mode == MarkPlacementMode.Off)
            {
                return MarkPlan.Empty("mark placement is off");
            }

            if (!SameFamily(table.Family, assetFamily))
            {
                return MarkPlan.Empty(
                    "font family '" + (assetFamily ?? string.Empty) + "' is not the table's '" + table.Family + "'");
            }

            if (!(assetPointSize > 0f) || float.IsInfinity(assetPointSize))
            {
                return MarkPlan.Empty("the asset reports no point size");
            }

            var scale = assetPointSize / table.UnitsPerEm;
            var records = new List<MarkRecord>(table.Pairs.Count);
            var keys = new HashSet<uint>();
            var skipped = 0;

            foreach (var pair in table.Pairs)
            {
                if (!TryGlyph(table, assetGlyphOf, pair.Preceding, out var first)
                    || !TryGlyph(table, assetGlyphOf, pair.Mark, out var second))
                {
                    skipped++;
                    continue;
                }

                var record = new MarkRecord(
                    pair.Preceding,
                    pair.Mark,
                    first,
                    second,
                    mode == MarkPlacementMode.Full ? pair.Dx * scale : 0f,
                    pair.Dy * scale);

                if (keys.Add(record.Key))
                {
                    records.Add(record);
                }
                else
                {
                    skipped++;
                }
            }

            var detail = records.Count.ToString(CultureInfo.InvariantCulture) + " record(s) at "
                + scale.ToString("0.####", CultureInfo.InvariantCulture) + " per font unit"
                + (skipped > 0 ? ", " + skipped.ToString(CultureInfo.InvariantCulture) + " pair(s) skipped" : string.Empty)
                + (mode == MarkPlacementMode.VerticalOnly ? ", vertical only" : string.Empty);

            return new MarkPlan(records, skipped, detail);
        }

        /// <summary>
        /// True when an asset's family is the table's: case-insensitive, ignoring surrounding
        /// spaces. A blank family never matches.
        /// </summary>
        /// <param name="tableFamily">The table's family.</param>
        /// <param name="assetFamily">The asset's family.</param>
        public static bool SameFamily(string? tableFamily, string? assetFamily) =>
            !string.IsNullOrWhiteSpace(tableFamily)
            && !string.IsNullOrWhiteSpace(assetFamily)
            && string.Equals(tableFamily!.Trim(), assetFamily!.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool TryGlyph(MarkTable table, Func<int, uint?>? assetGlyphOf, int codePoint, out uint glyph)
        {
            var fromAsset = assetGlyphOf?.Invoke(codePoint);
            if (fromAsset.HasValue && fromAsset.Value != 0)
            {
                glyph = fromAsset.Value;
            }
            else if (!table.TryGetGlyphId(codePoint, out glyph) || glyph == 0)
            {
                return false;
            }

            return glyph <= MaxGlyphIndex;
        }
    }

    /// <summary>The records <see cref="MarkPlacement.Plan"/> produced for one font asset.</summary>
    public sealed class MarkPlan
    {
        private static readonly MarkRecord[] NoRecords = new MarkRecord[0];

        /// <summary>Creates a plan.</summary>
        /// <param name="records">The records.</param>
        /// <param name="skipped">How many pairs were skipped.</param>
        /// <param name="detail">One line for logs.</param>
        public MarkPlan(IReadOnlyList<MarkRecord> records, int skipped, string detail)
        {
            Records = records ?? NoRecords;
            Skipped = skipped;
            Detail = detail ?? string.Empty;
        }

        /// <summary>The records to install, each with a unique key.</summary>
        public IReadOnlyList<MarkRecord> Records { get; }

        /// <summary>How many table pairs produced no record.</summary>
        public int Skipped { get; }

        /// <summary>What was planned, or why nothing was.</summary>
        public string Detail { get; }

        /// <summary>A plan with no records and a reason.</summary>
        /// <param name="reason">Why nothing is installed.</param>
        public static MarkPlan Empty(string reason) => new MarkPlan(NoRecords, 0, reason);
    }

    /// <summary>
    /// One pair-adjustment record: when <see cref="SecondGlyph"/> follows <see cref="FirstGlyph"/>,
    /// the second is drawn <see cref="XPlacement"/>/<see cref="YPlacement"/> away, in the asset's units.
    /// </summary>
    public readonly struct MarkRecord
    {
        /// <summary>Creates a record.</summary>
        /// <param name="firstCode">Code point of the preceding mark.</param>
        /// <param name="secondCode">Code point of the mark that moves.</param>
        /// <param name="firstGlyph">Glyph index of the preceding mark in the asset.</param>
        /// <param name="secondGlyph">Glyph index of the mark that moves.</param>
        /// <param name="xPlacement">Horizontal offset, asset units.</param>
        /// <param name="yPlacement">Vertical offset, asset units.</param>
        public MarkRecord(int firstCode, int secondCode, uint firstGlyph, uint secondGlyph, float xPlacement, float yPlacement)
        {
            FirstCode = firstCode;
            SecondCode = secondCode;
            FirstGlyph = firstGlyph;
            SecondGlyph = secondGlyph;
            XPlacement = xPlacement;
            YPlacement = yPlacement;
        }

        /// <summary>Code point of the preceding mark.</summary>
        public int FirstCode { get; }

        /// <summary>Code point of the mark that moves.</summary>
        public int SecondCode { get; }

        /// <summary>Glyph index of the preceding mark in the asset.</summary>
        public uint FirstGlyph { get; }

        /// <summary>Glyph index of the mark that moves.</summary>
        public uint SecondGlyph { get; }

        /// <summary>Horizontal offset of the second glyph, asset units.</summary>
        public float XPlacement { get; }

        /// <summary>Vertical offset of the second glyph, asset units.</summary>
        public float YPlacement { get; }

        /// <summary>TMP's lookup key: <c>(second &lt;&lt; 16) | first</c> (<c>GlyphPairKey</c>).</summary>
        public uint Key => (SecondGlyph << 16) | FirstGlyph;
    }
}
