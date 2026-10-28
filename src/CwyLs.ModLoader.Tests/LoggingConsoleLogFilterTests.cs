using CwyLs.ModLoader.Core.Logging;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C5: <c>cwy-console.log</c> and the in-game console's scrollback keep the same lines, through
    /// one predicate. The cases are the rule both listeners implemented separately before.
    /// </summary>
    public class LoggingConsoleLogFilterTests
    {
        [Theory]
        [InlineData("CWYLS Mod Loader", false, false, true)]          // our Info / Message / Warning
        [InlineData("CWYLS Game Adapter", false, false, true)]
        [InlineData("CWYLS Trace", false, true, false)]            // our Debug stays in the trace file
        [InlineData("CWYLS Mod Loader", true, false, true)]           // our Error
        [InlineData("Unity Log", false, false, false)]           // someone else's Info or Warning
        [InlineData("Unity Log", true, false, true)]             // someone else's Error or Fatal
        [InlineData("Unity Log", true, true, true)]              // an error is an error, whatever else is set
        [InlineData(null, false, false, false)]                  // unknown source, not an error
        [InlineData(null, true, false, true)]                    // unknown source, error
        [InlineData("", false, false, false)]
        [InlineData("cwy lowercase", false, false, false)]       // the prefix is case-sensitive, as before
        [InlineData("CWYSomething", false, false, true)]         // a prefix, not a word: as before
        public void Errors_from_anyone_and_our_own_lines_above_debug(
            string? source, bool errorOrFatal, bool debug, bool expected)
        {
            Assert.Equal(expected, ConsoleLogFilter.Admits(source, errorOrFatal, debug));
        }

        [Fact]
        public void The_prefix_is_the_one_every_framework_log_source_starts_with()
        {
            Assert.Equal("CWY", ConsoleLogFilter.CwySourcePrefix);
        }
    }
}
