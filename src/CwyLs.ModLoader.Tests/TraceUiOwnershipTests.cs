using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-LEFTOVERS. Closing the game-style console with Esc wrote a stray <c>TYPED ""</c>: the
    /// trace's UI layer traced our own input line. The rule that keeps the framework's controls out
    /// is a name test on the hierarchy root; walking the hierarchy needs Unity and is checked in game.
    /// </summary>
    public class TraceUiOwnershipTests
    {
        [Theory]
        [InlineData("CWY.GameStyleCanvas")]   // every game-style window (the console, mod windows)
        [InlineData("CWY.TraceHost")]
        [InlineData("CWY.ConsoleHost")]
        [InlineData("CWY.SomeFutureOverlay")]
        public void Roots_with_the_framework_prefix_are_ours(string rootName)
        {
            Assert.True(UiOwnership.IsFrameworkRoot(rootName));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("CWY.")]                  // the bare prefix is not a name
        [InlineData("Canvas")]                // the game's own canvases
        [InlineData("UI")]
        [InlineData("cwy.GameStyleCanvas")]   // ordinal and case-sensitive
        [InlineData("CWYCanvas")]             // the dot is part of the prefix
        [InlineData("Canvas CWY.GameStyleCanvas")]
        public void Anything_else_is_the_games(string? rootName)
        {
            Assert.False(UiOwnership.IsFrameworkRoot(rootName));
        }

        [Fact]
        public void The_prefix_is_the_one_every_framework_root_uses()
        {
            Assert.Equal("CWY.", UiOwnership.FrameworkRootPrefix);
        }
    }
}
