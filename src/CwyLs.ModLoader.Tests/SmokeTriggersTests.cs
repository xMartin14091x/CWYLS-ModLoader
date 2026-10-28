using CwyLs.ModLoader.Core.Smoke;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-9a. The framework used to verify itself twice a session without being asked, and each
    /// pass played the test clip and stripped and re-applied Satone's textures. These fix the new
    /// contract: nothing runs unless someone asks, and the loud checks need an explicit ask.
    /// <para>
    /// F4 tightened it again: the key and the trigger file ask for a QUIET run; only
    /// <c>smoke run loud</c> and the <c>smoke.loud</c> file are loud.
    /// </para>
    /// </summary>
    [Collection(SmokeRegistryCollection.Name)]
    public class SmokeTriggersTests
    {
        [Fact]
        public void Nothing_runs_on_its_own_by_default()
        {
            Assert.False(SmokeTriggers.DefaultAutoRunOnRoomScene);
            Assert.Equal(0f, SmokePassSchedule.DefaultSecondPassSeconds);
        }

        [Fact]
        public void The_trigger_file_and_the_hotkey_are_named_the_same_thing()
        {
            // The user types one of them and creates the other; one name is one thing to remember.
            Assert.Equal("smoke.run", SmokeTriggers.RunHotkeyId);
            Assert.Equal("smoke.run", SmokeTriggers.TriggerFileName);
            Assert.Equal(0.5f, SmokeTriggers.TriggerFilePollSeconds);
        }

        /// <summary>F4: the loud run has its own file, so the quiet one can stay the default.</summary>
        [Fact]
        public void The_loud_run_has_its_own_trigger_file_and_command()
        {
            Assert.Equal("smoke.loud", SmokeTriggers.LoudTriggerFileName);
            Assert.NotEqual(SmokeTriggers.TriggerFileName, SmokeTriggers.LoudTriggerFileName);
            Assert.Equal("smoke run loud", SmokeTriggers.LoudCommand);
            Assert.True(SmokeTriggers.DefaultHotkeyEnabled);
        }

        [Theory]
        [InlineData(false, SmokeRunMode.Automatic, true)]
        [InlineData(false, SmokeRunMode.Quiet, true)]
        [InlineData(false, SmokeRunMode.Loud, true)]
        [InlineData(false, SmokeRunMode.Explicit, true)]
        [InlineData(true, SmokeRunMode.Automatic, false)]
        [InlineData(true, SmokeRunMode.Quiet, false)]
        [InlineData(true, SmokeRunMode.Loud, true)]
        // The pre-F4 spelling of Loud still means loud, so code not yet migrated keeps working.
        [InlineData(true, SmokeRunMode.Explicit, true)]
        public void A_loud_check_needs_someone_to_ask(bool loud, SmokeRunMode mode, bool included) =>
            Assert.Equal(included, SmokeTriggers.Includes(loud, mode));

        /// <summary>
        /// The trigger-to-mode table F4 exists for: the key and the file are quiet, the console
        /// command and the loud file are loud, the scene is automatic. The runner picks the mode
        /// per trigger; these pin what each mode then does and what it is called in the log line.
        /// </summary>
        [Theory]
        [InlineData(SmokeRunMode.Automatic, "auto", false)]
        [InlineData(SmokeRunMode.Quiet, "quiet", false)]
        [InlineData(SmokeRunMode.Loud, "loud", true)]
        [InlineData(SmokeRunMode.Explicit, "loud", true)]
        public void Every_mode_has_a_name_for_the_log_line(SmokeRunMode mode, string label, bool isLoud)
        {
            Assert.Equal(label, SmokeTriggers.Label(mode));
            Assert.Equal(isLoud, SmokeTriggers.IsLoudRun(mode));
        }

        /// <summary>
        /// A quiet run says how to get the loud one; the automatic run says the same thing in its
        /// own words. Neither advertises a key any more - F9 is the alternate now, and pressing it
        /// would only produce another quiet run.
        /// </summary>
        [Fact]
        public void A_skipped_check_says_which_run_would_have_included_it()
        {
            Assert.Equal(
                "skipped (quiet run; use `smoke run loud`)",
                SmokeTriggers.SkipDetail(SmokeRunMode.Quiet));
            Assert.Equal(SmokeTriggers.LoudSkipDetail, SmokeTriggers.SkipDetail(SmokeRunMode.Automatic));

            Assert.Contains(SmokeTriggers.LoudCommand, SmokeTriggers.QuietSkipDetail);
            Assert.Contains(SmokeTriggers.LoudCommand, SmokeTriggers.LoudSkipDetail);
            Assert.DoesNotContain("F9", SmokeTriggers.QuietSkipDetail);
            Assert.DoesNotContain("F9", SmokeTriggers.LoudSkipDetail);
        }

        [Fact]
        public void Every_trigger_has_a_label_for_the_report()
        {
            Assert.Equal("key", SmokeTriggers.KeyTrigger);
            Assert.Equal("file", SmokeTriggers.FileTrigger);
            Assert.Equal("console", SmokeTriggers.ConsoleTrigger);
            Assert.Equal("sceneLoaded", SmokeTriggers.SceneTrigger);
        }

        [Fact]
        public void A_check_is_quiet_unless_it_says_otherwise()
        {
            var check = new SmokeCheck("p1.example", "P1", () => SmokeCheckResult.Pass("p1.example"));
            var loud = check.AsLoud();

            Assert.False(check.Loud);
            Assert.True(loud.Loud);

            // AsLoud keeps everything else, so the registry can mark a check it did not build.
            Assert.Equal(check.Id, loud.Id);
            Assert.Equal(check.Phase, loud.Phase);
            Assert.Same(check.Run, loud.Run);

            // Already loud: the same instance, so marking twice costs nothing.
            Assert.Same(loud, loud.AsLoud());
        }

        [Fact]
        public void Registering_loud_marks_the_check_the_registry_keeps()
        {
            SmokeRegistry.Clear();
            try
            {
                SmokeRegistry.Register(
                    new SmokeCheck("p1.voice.play", "P1", () => SmokeCheckResult.Pass("p1.voice.play")),
                    loud: true);

                var stored = Assert.Single(SmokeRegistry.Snapshot());
                Assert.True(stored.Loud);
            }
            finally
            {
                SmokeRegistry.Clear();
            }
        }

        [Fact]
        public void A_check_built_loud_stays_loud_through_the_plain_overload()
        {
            SmokeRegistry.Clear();
            try
            {
                SmokeRegistry.Register(new SmokeCheck(
                    "p1.textures.restore", "P1",
                    () => SmokeCheckResult.Pass("p1.textures.restore"),
                    loud: true));

                Assert.True(Assert.Single(SmokeRegistry.Snapshot()).Loud);
            }
            finally
            {
                SmokeRegistry.Clear();
            }
        }

        /// <summary>
        /// P1-9a follow-up: half the P1 checks report what has been OBSERVED this session, so
        /// three seconds into a launch they are red for the only possible reason. The runner
        /// logged them at Error and the console viewer painted a healthy session red.
        /// </summary>
        [Fact]
        public void Not_yet_observed_is_a_warning_not_an_error()
        {
            var waiting = SmokeCheckResult.NotYetObserved(
                "p1.events.satone_state", "not yet observed; press Ctrl+Shift+S later");
            var broken = SmokeCheckResult.Fail("p1.events.ready", "the bus never started");

            Assert.False(waiting.Ok);
            Assert.True(waiting.Informational);

            Assert.False(broken.Ok);
            Assert.False(broken.Informational);

            // A pass is never informational, whichever factory built it.
            Assert.False(SmokeCheckResult.Pass("p1.ok").Informational);
            Assert.False(SmokeCheckResult.Skipped("p1.voice.play", "skipped").Informational);
        }

        [Fact]
        public void The_informational_flag_survives_the_runners_restamping()
        {
            var waiting = SmokeCheckResult.NotYetObserved("a", "not yet observed");

            Assert.True(waiting.WithId("b").Informational);
            Assert.True(waiting.WithElapsed(12).Informational);
            Assert.Equal("b", waiting.WithId("b").Id);
            Assert.Equal(12, waiting.WithElapsed(12).ElapsedMs);
        }

        /// <summary>
        /// An informational failure is still a failure: the check proved nothing, so the run is
        /// not ok. Only the level it is announced at changes.
        /// </summary>
        [Fact]
        public void An_informational_failure_still_keeps_the_report_red()
        {
            var report = new SmokeReport(
                "s", "P0-2", "u", "g", System.DateTimeOffset.Now,
                new[]
                {
                    SmokeCheckResult.Pass("a"),
                    SmokeCheckResult.NotYetObserved("b", "not yet observed"),
                });

            Assert.False(report.Ok);
            Assert.Equal(1, report.Passed);
        }

        /// <summary>
        /// A skipped check is reported ok: it has not failed, nobody asked it to run. The report
        /// says which trigger would have included it, so the file explains itself.
        /// </summary>
        [Fact]
        public void A_skipped_check_does_not_turn_the_report_red()
        {
            var result = SmokeCheckResult.Skipped("p1.voice.play", SmokeTriggers.QuietSkipDetail);

            Assert.True(result.Ok);
            Assert.Equal("skipped (quiet run; use `smoke run loud`)", result.Detail);
        }
    }
}
