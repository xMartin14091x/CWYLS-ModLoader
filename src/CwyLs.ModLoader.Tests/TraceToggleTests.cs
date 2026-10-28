using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>RP-1: the diagnostic trace is off by default and one short command turns it on or off.</summary>
    public class TraceToggleTests
    {
        private static TraceTogglePlan Run(string line, TraceLevel saved, TraceLevel running) =>
            TraceToggle.Plan(line.Length == 0 ? new string[0] : line.Split(' '), saved, running);

        [Fact]
        public void On_saves_events_and_says_it_starts_at_the_next_launch()
        {
            var plan = Run("on", TraceLevel.Off, TraceLevel.Off);
            Assert.Equal(TraceLevel.Events, plan.Save);
            Assert.False(plan.StopNow);
            Assert.Contains("next launch", plan.Message);
            Assert.Contains(TraceToggle.Folder, plan.Message);
        }

        [Fact]
        public void Full_saves_the_every_method_level_and_warns_about_size()
        {
            var plan = Run("full", TraceLevel.Off, TraceLevel.Off);
            Assert.Equal(TraceLevel.Trace, plan.Save);
            Assert.Contains("large files", plan.Message);
        }

        [Fact]
        public void Off_while_recording_stops_now_and_saves_off()
        {
            var plan = Run("off", TraceLevel.Trace, TraceLevel.Trace);
            Assert.Equal(TraceLevel.Off, plan.Save);
            Assert.True(plan.StopNow);
            Assert.Contains("stopped recording now", plan.Message);
        }

        [Fact]
        public void Off_when_only_saved_on_does_not_try_to_stop_anything()
        {
            var plan = Run("off", TraceLevel.Events, TraceLevel.Off);
            Assert.Equal(TraceLevel.Off, plan.Save);
            Assert.False(plan.StopNow);
            Assert.Contains("next launch", plan.Message);
        }

        [Fact]
        public void Off_twice_says_so()
        {
            Assert.Equal("trace is already off", Run("off", TraceLevel.Off, TraceLevel.Off).Message);
        }

        [Fact]
        public void On_when_already_recording_at_that_level_changes_nothing()
        {
            var plan = Run("on", TraceLevel.Events, TraceLevel.Events);
            Assert.Null(plan.Save);
            Assert.Contains("already on", plan.Message);
        }

        [Fact]
        public void On_after_off_in_the_same_session_says_recording_is_still_stopped_until_restart()
        {
            // 'trace off' stopped a running trace (running is now Off); 'trace on' cannot restart it live.
            var plan = Run("on", TraceLevel.Off, TraceLevel.Off);
            Assert.Equal(TraceLevel.Events, plan.Save);
            Assert.Contains("starts at the next launch", plan.Message);
        }

        [Theory]
        [InlineData("", TraceLevel.Off, TraceLevel.Off, "trace is not recording. Commands: trace on|off|full|status")]
        [InlineData("status", TraceLevel.Events, TraceLevel.Off, "trace is not recording; next launch: on. Commands: trace on|off|full|status")]
        [InlineData("status", TraceLevel.Off, TraceLevel.Trace, "trace is recording (full) to BepInEx/trace; next launch: off. Commands: trace on|off|full|status")]
        public void Status_says_what_is_recording_now_and_what_the_next_launch_does(
            string line, TraceLevel saved, TraceLevel running, string expected)
        {
            var plan = Run(line, saved, running);
            Assert.Null(plan.Save);
            Assert.Equal(expected, plan.Message);
        }

        [Theory]
        [InlineData("level off", TraceLevel.Off)]
        [InlineData("level events", TraceLevel.Events)]
        [InlineData("level Trace", TraceLevel.Trace)]
        public void The_old_level_form_still_works(string line, TraceLevel expected) =>
            Assert.Equal(expected, Run(line, TraceLevel.Events, TraceLevel.Off).Save ?? TraceLevel.Events);

        [Fact]
        public void A_bad_level_or_word_changes_nothing()
        {
            Assert.Null(Run("level loud", TraceLevel.Off, TraceLevel.Off).Save);
            Assert.Null(Run("level 7", TraceLevel.Off, TraceLevel.Off).Save);
            Assert.StartsWith("usage:", Run("loud", TraceLevel.Off, TraceLevel.Off).Message);
        }
    }
}
