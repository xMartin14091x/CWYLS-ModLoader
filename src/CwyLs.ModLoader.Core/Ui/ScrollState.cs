using System;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>
    /// Where a scrolling view is, in content units, and whether it follows the end: the chat view
    /// (follows the newest bubble unless the player scrolled up) and the list view (starts at the
    /// top). Pure, so it is unit-tested.
    /// </summary>
    /// <remarks>
    /// <see cref="Top"/> is the content's distance above the viewport's top edge. Following means
    /// "stay at the bottom while content grows": a streamed reply keeps scrolling into view, but
    /// once the player scrolls up the view stays on what they are reading, and scrolling back to the
    /// bottom follows again. Content dropped above the view (the oldest bubbles past the cap) is
    /// subtracted from <see cref="Top"/>, so the lines being read do not jump.
    /// </remarks>
    public sealed class ScrollState
    {
        /// <summary>How close to the bottom (in content units) still counts as "at the bottom".</summary>
        public const float BottomSlack = 2f;

        /// <summary>Smallest scrollbar handle, as a share of the track.</summary>
        public const float MinHandleSize = 0.05f;

        /// <summary>Creates a state.</summary>
        /// <param name="followEnd">True to start following the end (chat); false to start at the top (list).</param>
        public ScrollState(bool followEnd)
        {
            Following = followEnd;
        }

        /// <summary>The content's distance above the viewport's top edge, 0 to <see cref="MaxTop"/>.</summary>
        public float Top { get; private set; }

        /// <summary>True while the view stays at the bottom as content grows.</summary>
        public bool Following { get; private set; }

        /// <summary>The content's height.</summary>
        public float Content { get; private set; }

        /// <summary>The viewport's height.</summary>
        public float Viewport { get; private set; }

        /// <summary>The largest <see cref="Top"/>: the content's bottom at the viewport's bottom.</summary>
        public float MaxTop => Math.Max(0f, Content - Viewport);

        /// <summary>
        /// The scrollbar value for a bottom-to-top scrollbar (as the text view uses): 0 at the bottom,
        /// 1 at the top.
        /// </summary>
        public float ScrollbarValue
        {
            get
            {
                var max = MaxTop;
                return max <= 0f ? 0f : 1f - (Top / max);
            }
        }

        /// <summary>The scrollbar handle's size as a share of the track.</summary>
        public float HandleSize =>
            Content <= Viewport || Content <= 0f ? 1f : Math.Max(MinHandleSize, Math.Min(1f, Viewport / Content));

        /// <summary>
        /// The content or the viewport changed size. Following keeps the bottom in view; otherwise the
        /// view stays on the same content, moved up by what was dropped above it.
        /// </summary>
        /// <param name="content">The new content height.</param>
        /// <param name="viewport">The new viewport height.</param>
        /// <param name="removedAbove">Content height dropped from the top since the last call.</param>
        public void Resize(float content, float viewport, float removedAbove = 0f)
        {
            Content = Math.Max(0f, Finite(content));
            Viewport = Math.Max(0f, Finite(viewport));

            if (Following)
            {
                Top = MaxTop;
                return;
            }

            // A view the player scrolled up only follows again when they scroll to the bottom (or
            // ToEnd is called), even if shrinking content leaves it at the bottom now.
            Top = Clamp(Top - Math.Max(0f, Finite(removedAbove)));
        }

        /// <summary>Scrolls by an amount: positive moves the view down (towards newer content).</summary>
        /// <param name="delta">Content units.</param>
        public void ScrollBy(float delta)
        {
            Top = Clamp(Top + Finite(delta));
            Following = AtBottom();
        }

        /// <summary>Jumps to the bottom and follows again.</summary>
        public void ToEnd()
        {
            Following = true;
            Top = MaxTop;
        }

        /// <summary>Jumps to the top and stops following.</summary>
        public void ToStart()
        {
            Following = false;
            Top = 0f;
        }

        /// <summary>Scrolls just enough to show a span of the content (e.g. one list row).</summary>
        /// <param name="spanTop">The span's top in content units.</param>
        /// <param name="spanHeight">The span's height.</param>
        public void Reveal(float spanTop, float spanHeight)
        {
            var top = Finite(spanTop);
            var bottom = top + Math.Max(0f, Finite(spanHeight));
            if (top < Top)
            {
                Top = Clamp(top);
            }
            else if (bottom > Top + Viewport)
            {
                Top = Clamp(bottom - Viewport);
            }

            Following = AtBottom() && Following;
        }

        /// <summary>The player dragged the scrollbar.</summary>
        /// <param name="value">Scrollbar value, 0 (bottom) to 1 (top).</param>
        public void FromScrollbar(float value)
        {
            var clamped = float.IsNaN(value) ? 0f : Math.Max(0f, Math.Min(1f, value));
            Top = Clamp(MaxTop * (1f - clamped));
            Following = AtBottom();
        }

        private bool AtBottom() => Top >= MaxTop - BottomSlack;

        private float Clamp(float top) => Math.Max(0f, Math.Min(MaxTop, top));

        private static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
    }
}
