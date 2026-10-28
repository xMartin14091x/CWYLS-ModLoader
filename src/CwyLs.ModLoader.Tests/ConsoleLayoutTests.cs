using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI-FIX: every console tab gets its log area from one rule, so the first line sits the same
    /// distance under the tab strip on the Console tab (command line in the bottom row) and on the
    /// view tabs (action buttons and the snapshot/live label in the bottom row).
    /// </summary>
    public class ConsoleLayoutTests
    {
        [Theory]
        [InlineData(true, 0, false)]  // Console: the command line
        [InlineData(false, 2, true)]  // Packs, Smoke, Targets: refresh plus one, "snapshot"
        [InlineData(false, 1, true)]  // Keys, Stream, State: refresh, "snapshot" or "panel, live"
        [InlineData(false, 0, true)]  // a view with only its label
        [InlineData(false, 3, false)] // buttons without a label
        public void Every_tab_with_a_bottom_row_gets_the_same_log_area(bool console, int actions, bool label)
        {
            Assert.True(ConsoleLayout.NeedsBottomRow(console, actions, label));

            ConsoleLayout.LogAreaInsets(true, out var top, out var bottom);
            Assert.Equal(ConsoleLayout.TabsHeight + ConsoleLayout.Gap, top);
            Assert.Equal(ConsoleLayout.RowHeight + ConsoleLayout.Gap, bottom);
        }

        [Fact]
        public void A_tab_with_nothing_for_the_bottom_row_gives_the_log_area_its_height()
        {
            Assert.False(ConsoleLayout.NeedsBottomRow(false, 0, false));

            ConsoleLayout.LogAreaInsets(false, out var top, out var bottom);
            Assert.Equal(ConsoleLayout.TabsHeight + ConsoleLayout.Gap, top);
            Assert.Equal(0f, bottom);
        }

        [Fact]
        public void The_log_area_starts_at_the_same_place_with_or_without_a_bottom_row()
        {
            ConsoleLayout.LogAreaInsets(true, out var withRow, out _);
            ConsoleLayout.LogAreaInsets(false, out var withoutRow, out _);

            Assert.Equal(withRow, withoutRow);
        }

        [Fact]
        public void The_first_line_sits_one_padding_under_the_log_areas_top_edge_on_every_tab()
        {
            // Body top -> tab strip (40) -> gap (12) -> log area edge -> padding (8) -> first line.
            ConsoleLayout.LogAreaInsets(true, out var consoleTop, out _);
            ConsoleLayout.LogAreaInsets(ConsoleLayout.NeedsBottomRow(false, 1, true), out var viewTop, out _);

            Assert.Equal(60f, consoleTop + LineLayout.Padding);
            Assert.Equal(consoleTop + LineLayout.Padding, viewTop + LineLayout.Padding);
        }
    }
}
