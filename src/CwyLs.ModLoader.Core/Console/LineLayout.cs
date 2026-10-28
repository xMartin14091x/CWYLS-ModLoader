using System;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>Which edge of the text box a <see cref="LineWindow"/> is pinned to.</summary>
    public enum LineAnchor
    {
        /// <summary>The first drawn line starts exactly at the box's top edge; text grows down.</summary>
        Top,

        /// <summary>The last drawn line ends exactly at the box's bottom edge; text grows up.</summary>
        Bottom
    }

    /// <summary>
    /// What a line view draws for one scroll position: which lines, and which edge of the text box
    /// they are pinned to. Produced by <see cref="LineLayout.Plan"/>.
    /// </summary>
    public readonly struct LineWindow : IEquatable<LineWindow>
    {
        /// <summary>Creates a plan.</summary>
        /// <param name="start">First line to draw.</param>
        /// <param name="end">One past the last line to draw.</param>
        /// <param name="anchor">The edge the drawn lines are pinned to.</param>
        /// <param name="offset">The scroll position actually used, in lines above the newest.</param>
        /// <param name="maxOffset">The largest offset: line 0 at the top of the box.</param>
        /// <param name="pageLines">Lines that fit wholly in the box counted from line 0.</param>
        /// <param name="fullyVisible">Lines of this plan that are wholly inside the box.</param>
        public LineWindow(int start, int end, LineAnchor anchor, int offset, int maxOffset, int pageLines, int fullyVisible)
        {
            Start = start;
            End = end;
            Anchor = anchor;
            Offset = offset;
            MaxOffset = maxOffset;
            PageLines = pageLines;
            FullyVisible = fullyVisible;
        }

        /// <summary>First line to draw.</summary>
        public int Start { get; }

        /// <summary>One past the last line to draw.</summary>
        public int End { get; }

        /// <summary>The edge the drawn lines are pinned to.</summary>
        public LineAnchor Anchor { get; }

        /// <summary>The scroll position actually used, in lines above the newest (0 = following the newest).</summary>
        public int Offset { get; }

        /// <summary>The largest offset: line 0 sits at the top of the box. 0 when every line fits.</summary>
        public int MaxOffset { get; }

        /// <summary>
        /// How many lines fit wholly in the box counted from line 0 (at least 1 when there are
        /// lines). <c>LineScroll.MaxOffset(total, PageLines)</c> equals <see cref="MaxOffset"/>, so
        /// it is the "visible" count the <see cref="LineScroll"/> scrollbar maths take.
        /// </summary>
        public int PageLines { get; }

        /// <summary>Lines of this plan wholly inside the box: what one page of scrolling moves.</summary>
        public int FullyVisible { get; }

        /// <summary>Lines to draw.</summary>
        public int Count => End - Start;

        /// <summary>
        /// True when there is nothing to scroll: every line fits, or the list is one line taller
        /// than the box (shown from its start).
        /// </summary>
        public bool Fits => MaxOffset == 0;

        /// <inheritdoc />
        public bool Equals(LineWindow other) =>
            Start == other.Start && End == other.End && Anchor == other.Anchor && Offset == other.Offset
            && MaxOffset == other.MaxOffset && PageLines == other.PageLines && FullyVisible == other.FullyVisible;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is LineWindow other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Start;
                hash = (hash * 397) ^ End;
                hash = (hash * 397) ^ (int)Anchor;
                hash = (hash * 397) ^ Offset;
                hash = (hash * 397) ^ MaxOffset;
                hash = (hash * 397) ^ PageLines;
                return (hash * 397) ^ FullyVisible;
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            "[" + Start + ", " + End + ") " + Anchor + " offset " + Offset + "/" + MaxOffset
            + " page " + PageLines + " full " + FullyVisible;
    }

    /// <summary>
    /// Which lines a scrolling line view draws, and where, when lines have different heights (a
    /// wrapped line is several rows tall; a row in a fallback font can be taller). Pure, so the
    /// rules can be tested without an engine; <c>GameStyleTextView</c> measures the lines and
    /// applies the plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan pins one edge exactly and lets the other edge clip. Scrolled to the top (and
    /// whenever every line fits), line 0 starts at the box's top edge and the text grows down;
    /// anywhere else - following the newest line, or scrolled part-way - the chosen bottom line
    /// ends at the box's bottom edge and the text grows up. The pinned edge never clips and never
    /// leaves a gap; the other edge may show part of a line, which the view's mask cuts at the box
    /// edge. Pinning by alignment (rather than by adding up heights) is what keeps the pinned line
    /// exact even when a measurement is a little off.
    /// </para>
    /// <para>
    /// Heights follow TextMeshPro's own stacking (TMP_Text.CalculatePreferredValues in the game's
    /// Unity.TextMeshPro): a line measured on its own spans its first row's ascender to its last
    /// row's descender, and consecutive lines are <c>spacing</c> apart (the font's line gap), so a
    /// run of n lines is the sum of their heights plus (n - 1) spacings.
    /// </para>
    /// </remarks>
    public static class LineLayout
    {
        /// <summary>
        /// Gap between a text box and the panel around it, top and bottom, in canvas units: the
        /// game's note window (<c>UI_FacilityNote</c> in the game's RoomScene, v1.17.2) keeps its body text
        /// 7.3 units above and 8.2 units inside its outlined background (text area insets plus TMP
        /// margins), with the mask on the same edge. Rounded to 8.
        /// </summary>
        public const float Padding = 8f;

        /// <summary>How far a line may overshoot the box and still count as wholly visible (measurement rounding).</summary>
        public const float FitTolerance = 0.5f;

        /// <summary>
        /// Plans the lines to draw.
        /// </summary>
        /// <param name="total">Lines in the list.</param>
        /// <param name="offset">
        /// Wanted scroll position in lines above the newest line: 0 follows the newest line; any
        /// value at or above the maximum (e.g. <see cref="int.MaxValue"/>) means "line 0 at the top".
        /// Clamped.
        /// </param>
        /// <param name="boxHeight">Height of the text box (inside the padding).</param>
        /// <param name="spacing">Distance between one line's last row and the next line's first row.</param>
        /// <param name="heightOf">Height of line i on its own at the box's width; NaN or negative counts as 0.</param>
        public static LineWindow Plan(int total, int offset, float boxHeight, float spacing, Func<int, float> heightOf)
        {
            if (heightOf is null)
            {
                throw new ArgumentNullException(nameof(heightOf));
            }

            if (total <= 0)
            {
                return new LineWindow(0, 0, LineAnchor.Top, 0, 0, 0, 0);
            }

            var box = Sanitize(boxHeight);
            var gap = float.IsNaN(spacing) || float.IsInfinity(spacing) ? 0f : spacing;

            var pageLines = Math.Max(1, CountFromTop(total, box, gap, heightOf));
            var maxOffset = total - pageLines;
            var used = Math.Max(0, Math.Min(offset, maxOffset));

            if (used == maxOffset)
            {
                var end = WalkDown(0, total, box, gap, heightOf, out var fullyDown);
                return new LineWindow(0, end, LineAnchor.Top, used, maxOffset, pageLines, fullyDown);
            }

            var last = total - 1 - used;
            var start = WalkUp(last, box, gap, heightOf, out var fullyUp);
            return new LineWindow(start, last + 1, LineAnchor.Bottom, used, maxOffset, pageLines, fullyUp);
        }

        /// <summary>Height of lines [start, end) stacked: their heights plus a spacing between each pair.</summary>
        /// <param name="start">First line.</param>
        /// <param name="end">One past the last line.</param>
        /// <param name="spacing">Distance between consecutive lines.</param>
        /// <param name="heightOf">Height of line i on its own.</param>
        public static float RunHeight(int start, int end, float spacing, Func<int, float> heightOf)
        {
            if (heightOf is null)
            {
                throw new ArgumentNullException(nameof(heightOf));
            }

            var height = 0f;
            for (var i = start; i < end; i++)
            {
                height += (i > start ? spacing : 0f) + Sanitize(heightOf(i));
            }

            return height;
        }

        /// <summary>Lines that fit wholly in the box counted from line 0; stops at the first that does not.</summary>
        private static int CountFromTop(int total, float box, float gap, Func<int, float> heightOf)
        {
            var bottom = 0f;
            for (var i = 0; i < total; i++)
            {
                var top = i == 0 ? 0f : bottom + gap;
                var next = top + Sanitize(heightOf(i));
                if (next > box + FitTolerance)
                {
                    return i;
                }

                bottom = next;
            }

            return total;
        }

        /// <summary>
        /// From <paramref name="first"/> at the top edge downwards: every line that starts inside
        /// the box (the first always). Returns one past the last of them.
        /// </summary>
        private static int WalkDown(int first, int total, float box, float gap, Func<int, float> heightOf, out int fully)
        {
            fully = 0;
            var bottom = 0f;
            var i = first;
            for (; i < total; i++)
            {
                var top = i == first ? 0f : bottom + gap;
                if (i != first && top >= box)
                {
                    break;
                }

                bottom = top + Sanitize(heightOf(i));
                if (bottom <= box + FitTolerance)
                {
                    fully++;
                }
            }

            return i;
        }

        /// <summary>
        /// From <paramref name="last"/> at the bottom edge upwards: every line that ends inside the
        /// box (the last always). Returns the first of them.
        /// </summary>
        private static int WalkUp(int last, float box, float gap, Func<int, float> heightOf, out int fully)
        {
            fully = 0;
            var top = 0f;
            var i = last;
            for (; i >= 0; i--)
            {
                var bottom = i == last ? 0f : top + gap;
                if (i != last && bottom >= box)
                {
                    break;
                }

                top = bottom + Sanitize(heightOf(i));
                if (top <= box + FitTolerance)
                {
                    fully++;
                }
            }

            return i + 1;
        }

        private static float Sanitize(float value) => float.IsNaN(value) || value < 0f ? 0f : value;
    }
}
