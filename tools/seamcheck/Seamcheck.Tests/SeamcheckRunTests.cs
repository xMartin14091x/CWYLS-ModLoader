using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Trace;
using Xunit;

namespace CWY.Seamcheck.Tests
{
    /// <summary>Options, controls, exit codes, report output, and the full run against the real game folders.</summary>
    public class SeamcheckRunTests
    {
        // ---------- options -------------------------------------------------------------------

        [Fact]
        public void The_default_is_the_macOS_install()
        {
            var options = SeamcheckOptions.Parse(Array.Empty<string>(), "/Users/someone");

            var spec = Assert.Single(options.Platforms);
            Assert.Equal("mac", spec.Key);
            Assert.Equal(TracePlatforms.MacOS, spec.TracePlatform);
            Assert.Equal(
                "/Users/someone/Library/Application Support/Steam/steamapps/common/Chill with You Lo-Fi Story/Chill With You.app/Contents/Resources/Data/Managed",
                spec.ManagedDirectory);
        }

        [Fact]
        public void All_checks_both_platforms_with_the_windows_copy_under_the_repo()
        {
            var options = SeamcheckOptions.Parse(new[] { "--platform", "all", "--repo", "/repo", "--json", "o.json", "--verbose" }, "/h");

            Assert.Equal(new[] { "mac", "win" }, options.Platforms.Select(p => p.Key));
            Assert.Equal("/repo/_ref/win-1.18.0/Managed", options.Platforms[1].ManagedDirectory);
            Assert.Equal(TracePlatforms.Windows, options.Platforms[1].TracePlatform);
            Assert.Equal("o.json", options.JsonPath);
            Assert.True(options.Verbose);
        }

        [Fact]
        public void Managed_overrides_the_folder_of_a_single_platform()
        {
            var options = SeamcheckOptions.Parse(new[] { "--platform", "win", "--managed", "/x/Managed" }, "/h");

            Assert.Equal("/x/Managed", Assert.Single(options.Platforms).ManagedDirectory);
        }

        [Theory]
        [InlineData("--platform", "linux")]
        [InlineData("--bogus")]
        [InlineData("--json")]
        [InlineData("--platform", "all", "--managed", "/x")]
        [InlineData("--platform", "win")]
        public void Bad_command_lines_are_tool_errors(params string[] args)
        {
            Assert.Throws<SeamcheckException>(() => SeamcheckOptions.Parse(args, "/h"));
        }

        [Fact]
        public void Help_is_recognised()
        {
            Assert.True(SeamcheckOptions.Parse(new[] { "--help" }, "/h").ShowHelp);
        }

        // ---------- controls and exit codes ---------------------------------------------------

        private sealed class AnswerEverything : IMemberResolver
        {
            private readonly bool _found;

            public AnswerEverything(bool found) => _found = found;

            public PatchTargetResult Resolve(PatchTarget target) =>
                _found ? PatchTargetResult.Ok(target, "whatever") : PatchTargetResult.Missing(target, "gone");
        }

        [Fact]
        public void A_resolver_that_finds_everything_fails_the_negative_control()
        {
            var controls = Controls.Run(new AnswerEverything(found: true));

            Assert.False(controls.Single(c => c.Id == Controls.Negative.Id).Passed);
            Assert.True(controls.Single(c => c.Id == Controls.Positive.Id).Passed);
        }

        [Fact]
        public void A_resolver_that_finds_nothing_fails_the_positive_control_and_a_vague_miss_fails_the_negative()
        {
            var controls = Controls.Run(new AnswerEverything(found: false));

            Assert.False(controls.Single(c => c.Id == Controls.Positive.Id).Passed);
            Assert.False(controls.Single(c => c.Id == Controls.Negative.Id).Passed);
        }

        private static TracePreflightResult Trace(bool excluded) =>
            new TracePreflightResult(
                10,
                2,
                new[] { new TraceHazard("Game.Window", "precise", new[] { "ProjectLifetimeScope.get_GameObject" }, 3, excluded) },
                Array.Empty<ExclusionCheck>());

        private static SeamcheckResult Result(
            bool missing,
            bool registrationError = false,
            bool platformError = false,
            TracePreflightResult? trace = null)
        {
            var target = new PatchTarget("t", "A", "T", MemberKind.Method, "M");
            var report = new PatchTargetReport(new[] { missing ? PatchTargetResult.Missing(target, "gone") : PatchTargetResult.Ok(target, "void M()") });
            var registrations = new RegistrationSummary(
                Array.Empty<string>(), Array.Empty<LoadedRegistration>(), Array.Empty<RegistrationSite>(),
                new[] { target }, new System.Collections.Generic.Dictionary<string, string>(),
                Array.Empty<string>(), Array.Empty<string>(),
                registrationError ? new[] { "boom" } : Array.Empty<string>());
            var platform = new PlatformResult(
                new PlatformSpec("mac", TracePlatforms.MacOS, "/m"), 1, null, report, Array.Empty<ControlOutcome>(), trace,
                platformError ? new[] { "control failed" } : Array.Empty<string>());
            return new SeamcheckResult(registrations, new[] { platform }, Array.Empty<PlatformDifference>());
        }

        private const string UncoveredGameWindow =
            "TRACE HAZARD Game.Window on macOS: precise .cctor calls ProjectLifetimeScope.get_GameObject; "
            + "the trace would run it at plugin load (3 method(s) patched); "
            + "declare an exclusion in src/CwyLs.ModLoader.Trace/TraceExclusions.cs";

        [Fact]
        public void An_uncovered_trace_hazard_fails_the_run_with_one_line_naming_type_platform_call_and_file()
        {
            var result = Result(missing: false, trace: Trace(excluded: false));
            var writer = new StringWriter();

            ReportWriter.WriteText(result, writer);

            Assert.Equal(1, result.ExitCode);
            var lines = writer.ToString().Split(Environment.NewLine);
            Assert.Equal(UncoveredGameWindow, Assert.Single(lines, l => l.StartsWith(TracePreflight.HazardPrefix, StringComparison.Ordinal)));
            Assert.Contains("seamcheck: exit 1", writer.ToString());
        }

        [Fact]
        public void A_covered_trace_hazard_does_not_fail_the_run()
        {
            var result = Result(missing: false, trace: Trace(excluded: true));
            var writer = new StringWriter();

            ReportWriter.WriteText(result, writer);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(TracePreflight.HazardPrefix, writer.ToString());
            Assert.Contains("  excluded Game.Window", writer.ToString());
        }

        [Fact]
        public void A_tool_error_still_outranks_an_uncovered_hazard()
        {
            Assert.Equal(2, Result(missing: false, platformError: true, trace: Trace(excluded: false)).ExitCode);
        }

        [Fact]
        public void The_hazard_line_is_built_from_the_hazard_and_the_platform()
        {
            var hazard = new TraceHazard("NestopiSystem.ScreenSystem", "precise",
                new[] { "ProjectLifetimeScope.get_GameObject", "ProjectLifetimeScope.Resolve" }, 3, ExcludedHere: false);

            Assert.Equal(
                "TRACE HAZARD NestopiSystem.ScreenSystem on Windows: precise .cctor calls ProjectLifetimeScope.get_GameObject, "
                + "ProjectLifetimeScope.Resolve; the trace would run it at plugin load (3 method(s) patched); "
                + "declare an exclusion in src/CwyLs.ModLoader.Trace/TraceExclusions.cs",
                TracePreflight.UncoveredLine(hazard, TracePlatforms.Windows));
        }

        [ManagedFolderFact("win")]
        public void Removing_the_ScreenSystem_exclusion_fails_the_Windows_run()
        {
            var registrations = SeamcheckRunner.LoadRegistrations(TestAssemblies.PluginAssemblies(), ToolLog.Null);
            var platform = SeamcheckRunner.CheckPlatform(
                new PlatformSpec("win", TracePlatforms.Windows, TestAssemblies.WinManaged),
                registrations.Targets,
                Array.Empty<TraceExclusion>(),
                ToolLog.Null);
            var result = new SeamcheckResult(registrations, new[] { platform }, Array.Empty<PlatformDifference>());
            var writer = new StringWriter();

            ReportWriter.WriteText(result, writer);

            Assert.True(platform.Report.AllFound);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains(
                "TRACE HAZARD NestopiSystem.ScreenSystem on Windows: precise .cctor calls ProjectLifetimeScope.get_GameObject",
                writer.ToString());
        }

        [Fact]
        public void Exit_codes_are_0_ok_1_missing_2_tool_error()
        {
            Assert.Equal(0, Result(missing: false).ExitCode);
            Assert.Equal(1, Result(missing: true).ExitCode);
            Assert.Equal(2, Result(missing: true, registrationError: true).ExitCode);
            Assert.Equal(2, Result(missing: false, platformError: true).ExitCode);
        }

        [Fact]
        public void The_text_report_ends_each_platform_with_the_game_summary_line()
        {
            var writer = new StringWriter();

            ReportWriter.WriteText(Result(missing: true), writer);

            var text = writer.ToString();
            Assert.Contains("PATCH TARGET MISSING t: gone" + Environment.NewLine + "targets: 0 ok, 1 missing", text);
            Assert.Contains("seamcheck: exit 1", text);
        }

        [Fact]
        public void Platform_differences_list_targets_whose_outcome_differs()
        {
            var a = new PatchTarget("a", "A", "T", MemberKind.Method, "M");
            var b = new PatchTarget("b", "A", "T", MemberKind.Method, "N");
            PlatformResult Platform(string key, bool bFound) => new PlatformResult(
                new PlatformSpec(key, TracePlatforms.MacOS, "/m"), 1, null,
                new PatchTargetReport(new[] { PatchTargetResult.Ok(a, "void M()"), bFound ? PatchTargetResult.Ok(b, "void N()") : PatchTargetResult.Missing(b, "gone") }),
                Array.Empty<ControlOutcome>(), null, Array.Empty<string>());

            var differences = SeamcheckRunner.Differences(new[] { Platform("mac", true), Platform("win", false) });

            var difference = Assert.Single(differences);
            Assert.Equal("b", difference.Id);
            Assert.Equal(new[] { "mac", "win" }, difference.Outcomes.Select(o => o.Platform));
        }

        // ---------- the real game folders -------------------------------------------------------

        [ManagedFolderFact("mac")]
        public void Every_registered_target_resolves_on_macOS_and_the_controls_pass()
        {
            AssertPlatformClean("mac", TracePlatforms.MacOS, TestAssemblies.MacManaged);
        }

        [ManagedFolderFact("win")]
        public void Every_registered_target_resolves_on_Windows_and_the_controls_pass()
        {
            AssertPlatformClean("win", TracePlatforms.Windows, TestAssemblies.WinManaged);
        }

        [ManagedFolderFact("win")]
        public void The_json_report_carries_the_schema_summary_and_every_result()
        {
            var registrations = SeamcheckRunner.LoadRegistrations(TestAssemblies.PluginAssemblies(), ToolLog.Null);
            var platform = SeamcheckRunner.CheckPlatform(
                new PlatformSpec("win", TracePlatforms.Windows, TestAssemblies.WinManaged), registrations.Targets, TraceExclusions.Declared, ToolLog.Null);
            var result = new SeamcheckResult(registrations, new[] { platform }, Array.Empty<PlatformDifference>());
            var path = Path.Combine(Path.GetTempPath(), "seamcheck-test-" + Guid.NewGuid().ToString("N") + ".json");

            try
            {
                ReportWriter.WriteJson(result, path);
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var root = json.RootElement;
                Assert.Equal(ReportWriter.Schema, root.GetProperty("schema").GetString());
                Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
                var win = root.GetProperty("platforms")[0];
                Assert.Equal(platform.Report.Summarize(), win.GetProperty("summary").GetString());
                Assert.Equal(registrations.Targets.Count, win.GetProperty("results").GetArrayLength());
                Assert.Equal("needed", win.GetProperty("trace").GetProperty("exclusions")[0].GetProperty("verdict").GetString());
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void AssertPlatformClean(string key, TracePlatforms tracePlatform, string managed)
        {
            var registrations = SeamcheckRunner.LoadRegistrations(TestAssemblies.PluginAssemblies(), ToolLog.Null);

            var result = SeamcheckRunner.CheckPlatform(new PlatformSpec(key, tracePlatform, managed), registrations.Targets, TraceExclusions.Declared, ToolLog.Null);

            Assert.Empty(result.Errors);
            Assert.Empty(result.Trace!.UncoveredHazards);
            Assert.Equal(0, new SeamcheckResult(registrations, new[] { result }, Array.Empty<PlatformDifference>()).ExitCode);
            Assert.All(result.Controls, c => Assert.True(c.Passed, c.Id + ": " + c.Detail));
            Assert.True(result.Report.AllFound, string.Join(Environment.NewLine, result.Report.MissingLines()));
            Assert.Equal("targets: " + registrations.Targets.Count + " ok, 0 missing", result.Report.Summarize());
        }
    }
}
