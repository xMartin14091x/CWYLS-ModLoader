using System;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// Where a line view is scrolled to, and what the reader asked for: follow the newest line,
    /// stay on the lines being read, or stay at the top. Pure, so the rules can be tested without
    /// an engine; <c>GameStyleTextView</c> keeps one and lays it out every time something changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The position is an offset in lines above the newest line, as in <see cref="LineScroll"/>:
    /// 0 follows the newest line, a larger value stays on the same lines while more arrive. "At
    /// the top" is kept as its own intent rather than as a number, so a view opened at line 0
    /// stays at line 0 when the window is resized or the content grows, instead of drifting to
    /// whatever offset the old size happened to give.
    /// </para>
    /// <para>Every change bumps <see cref="Version"/>, so a view knows when to lay out again.</para>
    /// </remarks>
    public sealed class LineScrollState
    {
        /// <summary>Lines above the newest line; 0 follows the newest line.</summary>
        public int Offset { get; private set; }

        /// <summary>True while the reader wants line 0 at the top of the box.</summary>
        public bool PinnedToTop { get; private set; }

        /// <summary>The last plan <see cref="Layout"/> made.</summary>
        public LineWindow Last { get; private set; }

        /// <summary>Lines in the list at the last <see cref="Layout"/>.</summary>
        public int Total { get; private set; }

        /// <summary>Grows by one on every change of position or intent.</summary>
        public long Version { get; private set; }

        /// <summary>
        /// <paramref name="lines"/> new lines arrived at the end. A view following the newest line
        /// keeps following; a view scrolled up moves up with them, so the lines being read stay put.
        /// </summary>
        /// <param name="lines">How many; zero or less changes nothing.</param>
        public void Appended(int lines)
        {
            if (lines <= 0)
            {
                return;
            }

            if (Offset > 0 && !PinnedToTop)
            {
                Offset = Add(Offset, lines);
            }

            Version++;
        }

        /// <summary>
        /// Scrolls by whole lines, positive towards older lines. Reaching the top pins the view
        /// there; any move down unpins it. Clamped to the last layout's range.
        /// </summary>
        /// <param name="lines">How far.</param>
        public void Scroll(int lines)
        {
            if (lines == 0)
            {
                return;
            }

            var max = Last.MaxOffset;
            var from = PinnedToTop ? max : Offset;
            Offset = Math.Max(0, Math.Min(Add(from, lines), max));
            PinnedToTop = lines > 0 && max > 0 && Offset >= max;
            Version++;
        }

        /// <summary>Up by a page: the lines wholly visible now, less one line of context.</summary>
        public void PageUp() => Scroll(LineScroll.Page(Last.FullyVisible));

        /// <summary>Down by a page.</summary>
        public void PageDown() => Scroll(-LineScroll.Page(Last.FullyVisible));

        /// <summary>Jumps to the newest line and follows it again.</summary>
        public void ToEnd()
        {
            Offset = 0;
            PinnedToTop = false;
            Version++;
        }

        /// <summary>Jumps to line 0 and stays there: how a freshly opened document reads.</summary>
        public void ToStart()
        {
            PinnedToTop = true;
            Version++;
        }

        /// <summary>
        /// Moves to where a bottom-to-top scrollbar was dragged (0 newest, 1 oldest), using the last
        /// layout's range. Dragging to the very top pins the view there.
        /// </summary>
        /// <param name="value">Scrollbar value.</param>
        public void FromScrollbar(float value)
        {
            Offset = LineScroll.OffsetFromScrollbar(value, Total, Last.PageLines);
            PinnedToTop = Last.MaxOffset > 0 && Offset >= Last.MaxOffset;
            Version++;
        }

        /// <summary>
        /// Plans the lines to draw for the current position (<see cref="LineLayout.Plan"/>) and
        /// keeps the position the plan actually used. Does not bump <see cref="Version"/>.
        /// </summary>
        /// <param name="total">Lines in the list.</param>
        /// <param name="boxHeight">Height of the text box.</param>
        /// <param name="spacing">Distance between consecutive lines.</param>
        /// <param name="heightOf">Height of line i on its own at the box's width.</param>
        public LineWindow Layout(int total, float boxHeight, float spacing, Func<int, float> heightOf)
        {
            var plan = LineLayout.Plan(total, PinnedToTop ? int.MaxValue : Offset, boxHeight, spacing, heightOf);
            Offset = plan.Offset;
            Total = Math.Max(0, total);
            Last = plan;
            return plan;
        }

        /// <summary>The scrollbar handle position for the current position in the last layout's range (0 newest, 1 oldest).</summary>
        public float ScrollbarValue => LineScroll.ScrollbarValue(PinnedToTop ? int.MaxValue : Offset, Total, Last.PageLines);

        /// <summary>The scrollbar handle size for the last layout, as a share of the track.</summary>
        public float HandleSize => LineScroll.HandleSize(Total, Last.PageLines);

        private static int Add(int value, int lines)
        {
            var sum = (long)value + lines;
            return sum > int.MaxValue ? int.MaxValue : sum < int.MinValue ? int.MinValue : (int)sum;
        }
    }
}
