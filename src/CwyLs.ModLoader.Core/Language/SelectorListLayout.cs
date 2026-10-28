using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// The game's language selector list with pack entries added: what the open pulldown shows, how
    /// far the list can scroll, and where each row sits. Produced by <see cref="SelectorListLayout.Plan"/>.
    /// </summary>
    /// <remarks>
    /// All lengths are canvas units. Offsets and row positions are measured downwards from the top
    /// of the list (the top of the first row's padding); an offset of 0 shows the list from its
    /// start, exactly as the game does with its own seven entries.
    /// </remarks>
    public sealed class SelectorListPlan
    {
        private readonly float[] _rowTops;
        private readonly float[] _rowHeights;

        internal SelectorListPlan(
            float openHeight,
            float listTop,
            float viewportHeight,
            float contentHeight,
            int gameRows,
            float[] rowTops,
            float[] rowHeights,
            string? problem)
        {
            OpenHeight = openHeight;
            ListTop = listTop;
            ViewportHeight = viewportHeight;
            ContentHeight = contentHeight;
            GameRows = gameRows;
            _rowTops = rowTops;
            _rowHeights = rowHeights;
            Problem = problem;
            MaxOffset = problem is null ? Math.Max(0f, contentHeight - viewportHeight) : 0f;
            VisibleRows = problem is null ? CountVisibleFromTop() : 0;
        }

        /// <summary>
        /// The pulldown's open height. Always the game's own value: the list scrolls inside it, so
        /// the settings page around the pulldown looks exactly as it does without the framework.
        /// </summary>
        public float OpenHeight { get; }

        /// <summary>Distance from the pulldown's top edge to the top of the list.</summary>
        public float ListTop { get; }

        /// <summary>
        /// Height of the visible list area. Every row when they all fit in the room the open
        /// pulldown has below <see cref="ListTop"/>; otherwise the game's own rows stacked (the list
        /// the game sized its pulldown for), never more than that room.
        /// </summary>
        public float ViewportHeight { get; }

        /// <summary>Height of every row stacked, with the layout's padding and spacing.</summary>
        public float ContentHeight { get; }

        /// <summary>The largest scroll offset: the last row's bottom at the viewport's bottom. 0 when everything fits.</summary>
        public float MaxOffset { get; }

        /// <summary>True when the rows do not all fit and the list has to scroll.</summary>
        public bool Scrolls => MaxOffset > SelectorListLayout.FitTolerance;

        /// <summary>Rows in the list: the game's plus the pack entries.</summary>
        public int TotalRows => _rowHeights.Length;

        /// <summary>The game's own rows, which come first.</summary>
        public int GameRows { get; }

        /// <summary>Rows wholly visible at offset 0, counted from the first.</summary>
        public int VisibleRows { get; }

        /// <summary>Why the measured layout cannot be used, or null when it can.</summary>
        public string? Problem { get; }

        /// <summary>True when <see cref="Problem"/> is null.</summary>
        public bool IsUsable => Problem is null;

        /// <summary>Top of row <paramref name="index"/>, from the top of the list.</summary>
        /// <param name="index">Row index, 0 = the first row.</param>
        /// <exception cref="ArgumentOutOfRangeException">The index is not a row.</exception>
        public float RowTop(int index)
        {
            CheckIndex(index);
            return _rowTops[index];
        }

        /// <summary>Bottom of row <paramref name="index"/>, from the top of the list.</summary>
        /// <param name="index">Row index, 0 = the first row.</param>
        /// <exception cref="ArgumentOutOfRangeException">The index is not a row.</exception>
        public float RowBottom(int index)
        {
            CheckIndex(index);
            return _rowTops[index] + _rowHeights[index];
        }

        /// <summary>A scroll offset limited to [0, <see cref="MaxOffset"/>]; NaN becomes 0.</summary>
        /// <param name="offset">Wanted offset.</param>
        public float ClampOffset(float offset)
        {
            if (float.IsNaN(offset) || offset <= 0f)
            {
                return 0f;
            }

            return offset >= MaxOffset ? MaxOffset : offset;
        }

        /// <summary>
        /// The smallest change to <paramref name="currentOffset"/> that shows row
        /// <paramref name="index"/> whole: unchanged when it already is, otherwise the row's top at
        /// the viewport's top (row above) or its bottom at the viewport's bottom (row below). A row
        /// taller than the viewport shows its top. An index that is not a row keeps the offset.
        /// </summary>
        /// <param name="index">The row to show, e.g. the selected language's.</param>
        /// <param name="currentOffset">The offset the list has now (0 for a list just opened).</param>
        /// <returns>The offset to scroll to, within [0, <see cref="MaxOffset"/>].</returns>
        public float Reveal(int index, float currentOffset)
        {
            var offset = ClampOffset(currentOffset);
            if (index < 0 || index >= TotalRows || Problem != null)
            {
                return offset;
            }

            var top = _rowTops[index];
            var bottom = top + _rowHeights[index];

            if (bottom - top > ViewportHeight || top < offset - SelectorListLayout.FitTolerance)
            {
                return ClampOffset(top);
            }

            if (bottom > offset + ViewportHeight + SelectorListLayout.FitTolerance)
            {
                return ClampOffset(bottom - ViewportHeight);
            }

            return offset;
        }

        /// <summary>True when row <paramref name="index"/> lies wholly inside the viewport at <paramref name="offset"/>.</summary>
        /// <param name="index">Row index.</param>
        /// <param name="offset">Scroll offset.</param>
        public bool IsWhollyVisible(int index, float offset)
        {
            if (index < 0 || index >= TotalRows || Problem != null)
            {
                return false;
            }

            var top = _rowTops[index] - offset;
            var bottom = top + _rowHeights[index];
            return top >= -SelectorListLayout.FitTolerance && bottom <= ViewportHeight + SelectorListLayout.FitTolerance;
        }

        /// <summary><c>7/8 rows, viewport 281.6 of 321.9, max offset 40.3</c>.</summary>
        public override string ToString() =>
            Problem != null
                ? "unusable: " + Problem
                : VisibleRows + "/" + TotalRows + " rows, viewport " + Round(ViewportHeight) + " of " + Round(ContentHeight)
                    + ", max offset " + Round(MaxOffset);

        private static string Round(float value) =>
            Math.Round(value, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        private int CountVisibleFromTop()
        {
            var count = 0;
            for (var i = 0; i < _rowHeights.Length; i++)
            {
                if (_rowTops[i] + _rowHeights[i] > ViewportHeight + SelectorListLayout.FitTolerance)
                {
                    break;
                }

                count++;
            }

            return count;
        }

        private void CheckIndex(int index)
        {
            if (index < 0 || index >= _rowHeights.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "the list has " + _rowHeights.Length + " rows");
            }
        }
    }

    /// <summary>
    /// Layout maths for the game's language selector once pack entries are added: the
    /// pulldown keeps the game's open height and the list scrolls inside it. Pure, so it is tested
    /// without an engine; <c>LanguageSelectorList</c> in the adapter measures the scene and applies it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game sized the pulldown for its seven entries (<c>PulldownListUI._openPullDownSizeDeltaY</c>
    /// = 338.4 in the game's RoomScene) and the settings page's scroll view clips anything below that
    /// (its <c>Viewport</c> has a <c>Mask</c>). The list's rows are placed by a
    /// <c>VerticalLayoutGroup</c> that does not control child height: row i starts at
    /// <c>paddingTop + sum(height[j] + spacing, j &lt; i)</c> and the list is
    /// <c>paddingTop + sum(height) + spacing * (n - 1) + paddingBottom</c> tall (the
    /// <c>ContentSizeFitter</c> uses that preferred height), which is what the plan reproduces.
    /// </para>
    /// </remarks>
    public static class SelectorListLayout
    {
        /// <summary>How far a row may overshoot the viewport and still count as wholly visible (float rounding).</summary>
        public const float FitTolerance = 0.5f;

        /// <summary>
        /// Width of the list's scrollbar: the game's own pulldown scrollbar (the to-do list picker,
        /// <c>UI_FacilityTodo/TodoGroupSelect/PullDown/Scroll View/Scrollbar Vertical</c> in RoomScene),
        /// which is also the width of every other scrollbar in the game. Narrower would squash the
        /// round ends of its 16-pixel pill sprite (<c>t_ui_scrollbar</c>, borders 8 top and bottom).
        /// </summary>
        public const float ScrollbarWidth = 14.5f;

        /// <summary>Gap between the scrollbar and the pulldown's right edge, as in the game's to-do list picker.</summary>
        public const float ScrollbarRightInset = 8.2f;

        /// <summary>
        /// Height of the first <paramref name="count"/> rows stacked with the layout's spacing and padding.
        /// </summary>
        /// <param name="heights">Row heights in order.</param>
        /// <param name="count">How many rows from the first; clamped to the list.</param>
        /// <param name="spacing">Gap between consecutive rows.</param>
        /// <param name="paddingTop">Space above the first row.</param>
        /// <param name="paddingBottom">Space below the last row.</param>
        /// <exception cref="ArgumentNullException"><paramref name="heights"/> is null.</exception>
        public static float StackHeight(IReadOnlyList<float> heights, int count, float spacing, float paddingTop, float paddingBottom)
        {
            if (heights is null)
            {
                throw new ArgumentNullException(nameof(heights));
            }

            var n = Math.Max(0, Math.Min(count, heights.Count));
            var height = paddingTop + paddingBottom;
            for (var i = 0; i < n; i++)
            {
                height += heights[i] + (i > 0 ? spacing : 0f);
            }

            return height;
        }

        /// <summary>
        /// Plans the list: the content is every row and the pulldown's open height stays the game's.
        /// When the rows do not all fit in the room the open pulldown has below
        /// <paramref name="listTop"/>, the viewport is the game's own rows (as tall as the list the
        /// game sized its pulldown for, capped by that room) and the rest scrolls into it.
        /// </summary>
        /// <param name="openHeight">The game's open height of the pulldown.</param>
        /// <param name="listTop">Distance from the pulldown's top edge to the top of the list.</param>
        /// <param name="rowHeights">Every row's height in layout order: the game's rows first, then the pack entries.</param>
        /// <param name="gameRows">How many of the rows are the game's own (the first ones).</param>
        /// <param name="spacing">The layout's spacing between rows.</param>
        /// <param name="paddingTop">The layout's top padding.</param>
        /// <param name="paddingBottom">The layout's bottom padding.</param>
        /// <returns>The plan; <see cref="SelectorListPlan.Problem"/> says why a measurement cannot be used.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="rowHeights"/> is null.</exception>
        public static SelectorListPlan Plan(
            float openHeight,
            float listTop,
            IReadOnlyList<float> rowHeights,
            int gameRows,
            float spacing,
            float paddingTop,
            float paddingBottom)
        {
            if (rowHeights is null)
            {
                throw new ArgumentNullException(nameof(rowHeights));
            }

            var heights = new float[rowHeights.Count];
            for (var i = 0; i < heights.Length; i++)
            {
                heights[i] = rowHeights[i];
            }

            var problem = Check(openHeight, listTop, heights, gameRows, spacing, paddingTop, paddingBottom);
            if (problem != null)
            {
                return new SelectorListPlan(openHeight, listTop, 0f, 0f, gameRows, new float[heights.Length], heights, problem);
            }

            var tops = new float[heights.Length];
            var y = paddingTop;
            for (var i = 0; i < heights.Length; i++)
            {
                tops[i] = y;
                y += heights[i] + spacing;
            }

            // Everything fits in the room the game gave its pulldown: nothing scrolls. Otherwise the
            // visible area is exactly the game's own list, so the open pulldown looks as it does
            // without pack entries (the gap under the last visible row stays empty, no half row).
            var room = openHeight - listTop;
            var content = StackHeight(heights, heights.Length, spacing, paddingTop, paddingBottom);
            var viewport = content <= room + FitTolerance
                ? content
                : Math.Min(StackHeight(heights, gameRows, spacing, paddingTop, paddingBottom), room);
            return new SelectorListPlan(openHeight, listTop, viewport, content, gameRows, tops, heights, null);
        }

        /// <summary>
        /// The open height that shows <paramref name="addedRows"/> more rows without scrolling: the
        /// fallback when the list cannot be made to scroll (the game's settings page may then clip
        /// the last rows, which is why it is only the fallback).
        /// </summary>
        /// <param name="openHeight">The game's open height.</param>
        /// <param name="addedRows">Rows added below the game's.</param>
        /// <param name="rowHeight">Height of one added row.</param>
        /// <param name="spacing">The layout's spacing.</param>
        public static float GrownOpenHeight(float openHeight, int addedRows, float rowHeight, float spacing) =>
            openHeight + (Math.Max(0, addedRows) * (rowHeight + spacing));

        private static string? Check(
            float openHeight,
            float listTop,
            float[] heights,
            int gameRows,
            float spacing,
            float paddingTop,
            float paddingBottom)
        {
            if (!IsFinite(openHeight) || openHeight <= 0f)
            {
                return "the pulldown's open height is " + openHeight;
            }

            if (!IsFinite(listTop) || listTop < 0f)
            {
                return "the list starts " + listTop + " below the pulldown's top";
            }

            if (!IsFinite(spacing) || !IsFinite(paddingTop) || !IsFinite(paddingBottom) || paddingTop < 0f || paddingBottom < 0f)
            {
                return "the list's layout spacing/padding is not usable";
            }

            if (gameRows <= 0 || gameRows > heights.Length)
            {
                return "the list has " + heights.Length + " rows of which " + gameRows + " are the game's";
            }

            for (var i = 0; i < heights.Length; i++)
            {
                if (!IsFinite(heights[i]) || heights[i] <= 0f)
                {
                    return "row " + i + " is " + heights[i] + " tall";
                }
            }

            if (openHeight - listTop < paddingTop + heights[0] - FitTolerance)
            {
                return "the open pulldown has " + (openHeight - listTop) + " units below the list's top, less than one row";
            }

            return null;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
