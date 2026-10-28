using System;
using System.Linq;
using CwyLs.ModLoader.Trace;
using Xunit;

namespace CWY.Seamcheck.Tests
{
    /// <summary>
    /// The Trace plugin's per-platform exclusion (src/CwyLs.ModLoader.Trace/TraceExclusions.cs, compiled
    /// into seamcheck): Windows ScreenSystem is never patched, macOS still is, and the skip is one
    /// INFO line naming the methods and the reason.
    /// </summary>
    public class TraceExclusionsTests
    {
        private const string ScreenSystem = "NestopiSystem.ScreenSystem";

        [Fact]
        public void The_declared_table_excludes_ScreenSystem_on_Windows_only()
        {
            var exclusion = Assert.Single(TraceExclusions.Declared);

            Assert.Equal(ScreenSystem, exclusion.TypeFullName);
            Assert.Equal(TracePlatforms.Windows, exclusion.Platforms);
            Assert.Contains("ProjectLifetimeScope.GameObject", exclusion.Reason);
        }

        [Theory]
        [InlineData((int)TracePlatforms.Windows, true)]
        [InlineData((int)TracePlatforms.MacOS, false)]
        [InlineData((int)TracePlatforms.Linux, false)]
        [InlineData((int)TracePlatforms.None, false)]
        public void Find_applies_the_exclusion_per_platform(int platform, bool excluded)
        {
            var found = TraceExclusions.Find(TraceExclusions.Declared, (TracePlatforms)platform, ScreenSystem);

            Assert.Equal(excluded, found != null);
        }

        [Theory]
        [InlineData("NestopiSystem.screensystem")]
        [InlineData("ScreenSystem")]
        [InlineData("NestopiSystem.ScreenSystem+WndProcDelegate")]
        [InlineData("Bulbul.SettingService")]
        [InlineData(null)]
        public void Find_matches_the_exact_reflection_name_only(string? typeName)
        {
            Assert.Null(TraceExclusions.Find(TraceExclusions.Declared, TracePlatforms.Windows, typeName));
        }

        [Fact]
        public void Find_rejects_a_null_table()
        {
            Assert.Throws<ArgumentNullException>(() => TraceExclusions.Find(null!, TracePlatforms.Windows, ScreenSystem));
        }

        [Theory]
        [InlineData("WindowsPlayer", (int)TracePlatforms.Windows)]
        [InlineData("WindowsEditor", (int)TracePlatforms.Windows)]
        [InlineData("OSXPlayer", (int)TracePlatforms.MacOS)]
        [InlineData("OSXEditor", (int)TracePlatforms.MacOS)]
        [InlineData("LinuxPlayer", (int)TracePlatforms.Linux)]
        [InlineData("Android", (int)TracePlatforms.None)]
        [InlineData("", (int)TracePlatforms.None)]
        [InlineData(null, (int)TracePlatforms.None)]
        public void Unity_platform_names_map_to_trace_platforms(string? runtimePlatform, int expected)
        {
            Assert.Equal((TracePlatforms)expected, TraceExclusions.FromUnityPlatformName(runtimePlatform));
        }

        [Fact]
        public void The_skip_line_names_every_skipped_method_and_the_reason()
        {
            var line = TraceExclusions.FormatSkipped(
                TraceExclusions.WindowsScreenSystem,
                TracePlatforms.Windows,
                new[] { "BringWindowToTopAlways", "ResetWindowOrder", "Initialize" });

            Assert.Equal(
                "skipped NestopiSystem.ScreenSystem.{BringWindowToTopAlways, ResetWindowOrder, Initialize} on Windows: "
                + TraceExclusions.WindowsScreenSystem.Reason,
                line);
        }

        [Fact]
        public void A_single_skipped_method_has_no_braces()
        {
            var line = TraceExclusions.FormatSkipped(TraceExclusions.WindowsScreenSystem, TracePlatforms.Windows, new[] { "Initialize" });

            Assert.StartsWith("skipped NestopiSystem.ScreenSystem.Initialize on Windows: ", line);
        }

        [Fact]
        public void Nothing_skipped_means_no_line()
        {
            Assert.Null(TraceExclusions.FormatSkipped(TraceExclusions.WindowsScreenSystem, TracePlatforms.Windows, Array.Empty<string>()));
        }

        [Fact]
        public void FormatSkipped_rejects_null_arguments()
        {
            Assert.Throws<ArgumentNullException>(() => TraceExclusions.FormatSkipped(null!, TracePlatforms.Windows, new[] { "X" }));
            Assert.Throws<ArgumentNullException>(() => TraceExclusions.FormatSkipped(TraceExclusions.WindowsScreenSystem, TracePlatforms.Windows, null!));
        }

        [Fact]
        public void An_exclusion_must_name_a_type_a_reason_and_a_platform()
        {
            Assert.Throws<ArgumentException>(() => new TraceExclusion(" ", TracePlatforms.Windows, "why"));
            Assert.Throws<ArgumentException>(() => new TraceExclusion("T", TracePlatforms.Windows, ""));
            Assert.Throws<ArgumentException>(() => new TraceExclusion("T", TracePlatforms.None, "why"));
        }

        [Fact]
        public void A_multi_platform_exclusion_applies_on_each_of_its_platforms()
        {
            var both = new TraceExclusion("T", TracePlatforms.Windows | TracePlatforms.MacOS, "why");

            Assert.True(both.AppliesTo(TracePlatforms.Windows));
            Assert.True(both.AppliesTo(TracePlatforms.MacOS));
            Assert.False(both.AppliesTo(TracePlatforms.Linux));
            Assert.False(both.AppliesTo(TracePlatforms.None));
        }

        [Fact]
        public void Platform_names_are_readable()
        {
            Assert.Equal(
                new[] { "Windows", "macOS", "Linux" },
                new[] { TracePlatforms.Windows, TracePlatforms.MacOS, TracePlatforms.Linux }.Select(TraceExclusions.Name));
        }
    }
}
