using System;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// Scrolling by whole lines through a list that only grows at the end - the console's
    /// scrollback, a panel's text. Pure, so the rules can be tested without an engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The position is an <em>offset from the newest line</em>: 0 means "showing the newest lines
    /// and following them", N means "N lines above that". Counting from the bottom is what makes
    /// following the tail free - a new line at offset 0 simply appears - and what lets a view the
    /// user scrolled up stay on the same lines while more arrive (<see cref="LineScrollState"/>).
    /// </para>
    /// <para>
    /// <c>Range</c>, <c>Follow</c> and <c>Capacity</c> were removed: the text view measures every
    /// line (<see cref="LineLayout"/>), so only tests called them.
    /// </para>
    /// <para>
    /// Only the visible lines are ever handed to the text renderer. A single TextMeshPro mesh holds
    /// at most 65,535 vertices (about 16,000 characters), which a full 300-line scrollback can
    /// exceed, so rendering the whole buffer and letting a ScrollRect move it is not an option.
    /// </para>
    /// </remarks>
    public static class LineScroll
    {
        /// <summary>Smallest scrollbar handle, as a share of the track, so it stays grabbable.</summary>
        public const float MinHandleSize = 0.05f;

        /// <summary>The largest offset: the oldest line at the top of the view.</summary>
        /// <param name="total">Lines in the list.</param>
        /// <param name="visible">Lines the view shows at once.</param>
        public static int MaxOffset(int total, int visible) => Math.Max(0, total - Math.Max(1, visible));

        /// <summary>An offset brought back into range.</summary>
        /// <param name="offset">The wanted offset.</param>
        /// <param name="total">Lines in the list.</param>
        /// <param name="visible">Lines the view shows at once.</param>
        public static int Clamp(int offset, int total, int visible) =>
            Math.Max(0, Math.Min(offset, MaxOffset(total, visible)));

        /// <summary>How far one PageUp or PageDown moves: a screen, less one line of context.</summary>
        /// <param name="visible">Lines the view shows at once.</param>
        public static int Page(int visible) => Math.Max(1, visible - 1);

        /// <summary>
        /// The scrollbar handle's size as a share of the track: what fraction of the list is
        /// visible, never below <see cref="MinHandleSize"/>.
        /// </summary>
        /// <param name="total">Lines in the list.</param>
        /// <param name="visible">Lines the view shows at once.</param>
        public static float HandleSize(int total, int visible)
        {
            if (total <= 0 || visible >= total)
            {
                return 1f;
            }

            return Math.Max(MinHandleSize, Math.Min(1f, (float)Math.Max(1, visible) / total));
        }

        /// <summary>
        /// The scrollbar value for an offset, for a bottom-to-top scrollbar: 0 is the newest line
        /// (handle at the bottom), 1 the oldest (handle at the top).
        /// </summary>
        /// <param name="offset">Offset from the newest line.</param>
        /// <param name="total">Lines in the list.</param>
        /// <param name="visible">Lines the view shows at once.</param>
        public static float ScrollbarValue(int offset, int total, int visible)
        {
            var max = MaxOffset(total, visible);
            return max == 0 ? 0f : (float)Clamp(offset, total, visible) / max;
        }

        /// <summary>The offset a scrollbar value stands for; the inverse of <see cref="ScrollbarValue"/>.</summary>
        /// <param name="value">Scrollbar value, 0 (newest) to 1 (oldest). Out-of-range values are clamped.</param>
        /// <param name="total">Lines in the list.</param>
        /// <param name="visible">Lines the view shows at once.</param>
        public static int OffsetFromScrollbar(float value, int total, int visible)
        {
            var max = MaxOffset(total, visible);
            if (max == 0 || float.IsNaN(value))
            {
                return 0;
            }

            var clamped = Math.Max(0f, Math.Min(1f, value));
            return Clamp((int)Math.Round(clamped * max, MidpointRounding.AwayFromZero), total, visible);
        }
    }
}
