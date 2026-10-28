namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// Where the game-style console puts its parts inside the window body: the tab strip along the
    /// top, the log area under it, and the bottom row (the command line on the Console tab, the
    /// action buttons and the snapshot/live label on the others). Pure, so the rule is tested
    /// without an engine and every tab gets the same log area from one place.
    /// </summary>
    /// <remarks>
    /// Sizes are canvas units at the game's 1920x1080 reference resolution. The text inside the log
    /// area keeps <see cref="LineLayout.Padding"/> from the area's top and bottom edges.
    /// </remarks>
    public static class ConsoleLayout
    {
        /// <summary>Height of the tab strip.</summary>
        public const float TabsHeight = 40f;

        /// <summary>Height of the bottom row.</summary>
        public const float RowHeight = 44f;

        /// <summary>Space between the log area and the strip or row next to it.</summary>
        public const float Gap = 12f;

        /// <summary>Width of a bottom-row button.</summary>
        public const float ButtonWidth = 120f;

        /// <summary>
        /// True when the tab showing needs the bottom row: the Console tab for its command line,
        /// another tab for its buttons or its snapshot/live label.
        /// </summary>
        /// <param name="consoleTab">True on the Console tab.</param>
        /// <param name="actionButtons">How many action buttons the tab offers.</param>
        /// <param name="hasModeLabel">True when the tab shows a snapshot/live label.</param>
        public static bool NeedsBottomRow(bool consoleTab, int actionButtons, bool hasModeLabel) =>
            consoleTab || actionButtons > 0 || hasModeLabel;

        /// <summary>
        /// The log area's distance from the body's top edge (under the tab strip) and bottom edge
        /// (over the bottom row when there is one). The area stretches with the window, so a
        /// resize never changes these, only the area's height.
        /// </summary>
        /// <param name="bottomRow">True when the tab has a bottom row (<see cref="NeedsBottomRow"/>).</param>
        /// <param name="top">Distance from the body's top edge.</param>
        /// <param name="bottom">Distance from the body's bottom edge.</param>
        public static void LogAreaInsets(bool bottomRow, out float top, out float bottom)
        {
            top = TabsHeight + Gap;
            bottom = bottomRow ? RowHeight + Gap : 0f;
        }
    }
}
