using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI: the game-style console when it can be built, the IMGUI console when it cannot, and
    /// one log line per change of state - never unusable, never a line per open.
    /// </summary>
    public class ConsoleSurfacePolicyTests
    {
        [Fact]
        public void Game_style_is_preferred_when_configured_and_registered()
        {
            var policy = new ConsoleSurfacePolicy();

            Assert.Equal(ConsoleSurfaceKind.GameStyle, policy.Preferred(ConsoleStyle.Game, gameStyleRegistered: true));
        }

        [Fact]
        public void Imgui_when_configured_or_when_no_adapter_registered_a_surface()
        {
            var policy = new ConsoleSurfacePolicy();

            Assert.Equal(ConsoleSurfaceKind.Imgui, policy.Preferred(ConsoleStyle.Imgui, gameStyleRegistered: true));
            Assert.Equal(ConsoleSurfaceKind.Imgui, policy.Preferred(ConsoleStyle.Game, gameStyleRegistered: false));
        }

        [Fact]
        public void The_first_fallback_is_logged_with_its_reason_and_repeats_are_not()
        {
            var policy = new ConsoleSurfacePolicy();

            var first = policy.RecordAttempt(false, "missing t_ui_close");
            var second = policy.RecordAttempt(false, "missing t_ui_close");
            var third = policy.RecordAttempt(false, "missing Base_JP");

            Assert.NotNull(first);
            Assert.Contains("IMGUI", first);
            Assert.Contains("missing t_ui_close", first);
            Assert.Null(second);
            Assert.Null(third);
        }

        [Fact]
        public void A_recovery_is_logged_once_and_a_later_fallback_is_logged_again()
        {
            var policy = new ConsoleSurfacePolicy();

            Assert.NotNull(policy.RecordAttempt(false, "room not loaded"));
            var recovered = policy.RecordAttempt(true, "all game UI assets found");
            Assert.NotNull(recovered);
            Assert.Contains("available again", recovered);
            Assert.Null(policy.RecordAttempt(true, null));
            Assert.NotNull(policy.RecordAttempt(false, "missing t_ui_back_bar"));
        }

        [Fact]
        public void The_first_success_is_logged_once_with_what_was_built()
        {
            var policy = new ConsoleSurfacePolicy();

            var line = policy.RecordAttempt(true, "built without optional t_ui_scrollbar (plain shapes instead)");

            Assert.NotNull(line);
            Assert.StartsWith("game-style console", line);
            Assert.Contains("t_ui_scrollbar", line);
            Assert.Null(policy.RecordAttempt(true, "anything"));
        }

        [Fact]
        public void A_surface_that_failed_while_running_is_off_for_the_session()
        {
            var policy = new ConsoleSurfacePolicy();
            Assert.NotNull(policy.RecordAttempt(true, null));

            var line = policy.RecordFailure("NullReferenceException: boom");

            Assert.NotNull(line);
            Assert.Contains("boom", line);
            Assert.True(policy.Broken);
            Assert.Equal(ConsoleSurfaceKind.Imgui, policy.Preferred(ConsoleStyle.Game, gameStyleRegistered: true));
            Assert.Null(policy.RecordFailure("again"));
            Assert.Equal("NullReferenceException: boom", policy.BrokenReason);
        }

        [Fact]
        public void A_blank_failure_reason_still_breaks_the_surface()
        {
            var policy = new ConsoleSurfacePolicy();

            Assert.NotNull(policy.RecordFailure("  "));
            Assert.True(policy.Broken);
            Assert.Equal("unknown failure", policy.BrokenReason);
        }

        [Fact]
        public void The_configured_imgui_style_is_noted_once()
        {
            var policy = new ConsoleSurfacePolicy();

            Assert.Null(policy.RecordConfiguredStyle(ConsoleStyle.Game));
            Assert.NotNull(policy.RecordConfiguredStyle(ConsoleStyle.Imgui));
            Assert.Null(policy.RecordConfiguredStyle(ConsoleStyle.Imgui));
        }
    }
}
