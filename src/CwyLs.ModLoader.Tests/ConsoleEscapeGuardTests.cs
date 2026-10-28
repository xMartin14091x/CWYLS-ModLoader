using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C5: Escape that closes the CWY console must not also switch a
    /// fullscreen game to windowed. The game reads Escape in its own <c>Update</c>, before or after
    /// the console's in the same frame; both orders are exercised here.
    /// </summary>
    public class ConsoleEscapeGuardTests
    {
        private const int Frame = 1000;

        /// <summary>Our input selected and typing, Console tab showing: the usual open console.</summary>
        private static readonly ConsoleFocus Typing = new ConsoleFocus(true, true, false, false, true);

        /// <summary>On a view tab (Packs, Smoke, ...): no input, nothing selected.</summary>
        private static readonly ConsoleFocus ViewTab = new ConsoleFocus(false, false, false, false, false);

        /// <summary>The player is typing in one of the game's own fields while the console is open.</summary>
        private static readonly ConsoleFocus GameField = new ConsoleFocus(false, false, true, false, true);

        /// <summary>Our input has an unfinished input-method composition.</summary>
        private static readonly ConsoleFocus Composing = new ConsoleFocus(true, true, false, true, true);

        /// <summary>A game field is composing (Japanese in a note) while the console is open.</summary>
        private static readonly ConsoleFocus GameFieldComposing = new ConsoleFocus(false, false, true, true, true);

        // ---- who owns Escape ---------------------------------------------------------------------

        [Fact]
        public void An_open_interactable_console_owns_Escape()
        {
            Assert.Equal(EscapeOwner.Console, ConsoleEscapeGuard.OwnerOf(true, true, Typing));
            Assert.Equal(EscapeOwner.Console, ConsoleEscapeGuard.OwnerOf(true, true, ViewTab));
        }

        [Fact]
        public void A_closed_or_faded_console_leaves_Escape_to_the_game()
        {
            Assert.Equal(EscapeOwner.Game, ConsoleEscapeGuard.OwnerOf(false, true, Typing));
            Assert.Equal(EscapeOwner.Game, ConsoleEscapeGuard.OwnerOf(true, false, Typing));
        }

        [Fact]
        public void Escape_in_one_of_the_games_own_fields_is_the_games()
        {
            Assert.Equal(EscapeOwner.Game, ConsoleEscapeGuard.OwnerOf(true, true, GameField));
            Assert.Equal(EscapeOwner.Game, ConsoleEscapeGuard.OwnerOf(true, true, GameFieldComposing));
        }

        [Fact]
        public void Escape_that_cancels_a_composition_in_our_input_is_ours()
        {
            Assert.Equal(EscapeOwner.Console, ConsoleEscapeGuard.OwnerOf(true, true, Composing));
        }

        [Fact]
        public void The_owner_agrees_with_the_key_router_whenever_the_router_closes()
        {
            foreach (var focus in new[] { Typing, ViewTab, GameField, Composing, GameFieldComposing })
            {
                if (ConsoleKeyRouter.Route(ConsoleKeyPress.Escape, focus) == ConsoleKeyAction.Close)
                {
                    Assert.Equal(EscapeOwner.Console, ConsoleEscapeGuard.OwnerOf(true, true, focus));
                }
            }
        }

        // ---- the window switch -------------------------------------------------------------------

        [Fact]
        public void Console_ran_first_and_claimed_Escape_the_switch_is_blocked()
        {
            var guard = new ConsoleEscapeGuard();
            guard.Observe(Frame, EscapeOwner.Console);
            guard.Claim(Frame);

            // The console has already closed by the time the game's Update runs.
            Assert.True(guard.ShouldBlockWindowedSwitch(Frame, toWindowed: true, escapeDown: true, consoleOpen: false));
            Assert.Equal(1L, guard.BlockedCount);
        }

        [Fact]
        public void Game_ran_first_while_the_open_console_owned_Escape_last_frame_the_switch_is_blocked()
        {
            var guard = new ConsoleEscapeGuard();
            guard.Observe(Frame - 1, EscapeOwner.Console);

            Assert.True(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: true));
        }

        [Fact]
        public void Game_ran_first_in_the_same_frame_as_the_report_the_switch_is_blocked()
        {
            var guard = new ConsoleEscapeGuard();
            guard.Observe(Frame, EscapeOwner.Console);

            Assert.True(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: true));
        }

        [Fact]
        public void The_imgui_overlay_path_blocks_through_its_per_frame_report()
        {
            // OnGUI runs after every Update: the report is always from the previous frame, the
            // claim always comes after the game's call.
            var guard = new ConsoleEscapeGuard();
            guard.Observe(Frame - 1, EscapeOwner.Console);

            Assert.True(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: true));
            guard.Claim(Frame);
            Assert.Equal(1L, guard.BlockedCount);
        }

        [Fact]
        public void Escape_with_the_console_closed_switches_as_the_game_always_did()
        {
            var guard = new ConsoleEscapeGuard();
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: false));

            // Closed some frames ago: its last report is stale and no claim is fresh.
            guard.Observe(Frame - 30, EscapeOwner.Console);
            guard.Claim(Frame - 30);
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: false));
            Assert.Equal(0L, guard.BlockedCount);
        }

        [Fact]
        public void Escape_in_a_game_field_while_the_console_is_open_is_left_to_the_game()
        {
            var guard = new ConsoleEscapeGuard();
            guard.Observe(Frame - 1, ConsoleEscapeGuard.OwnerOf(true, true, GameField));

            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: true));
        }

        [Fact]
        public void A_faded_console_leaves_Escape_to_the_game()
        {
            var guard = new ConsoleEscapeGuard();
            guard.Observe(Frame - 1, ConsoleEscapeGuard.OwnerOf(true, false, Typing));

            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: true));
        }

        [Fact]
        public void A_stale_report_does_not_block()
        {
            var guard = new ConsoleEscapeGuard();
            guard.Observe(Frame - 2, EscapeOwner.Console);

            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: true));
        }

        [Fact]
        public void A_report_from_the_future_does_not_block()
        {
            var guard = new ConsoleEscapeGuard();
            guard.Observe(Frame + 5, EscapeOwner.Console);

            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: true));
        }

        [Fact]
        public void The_settings_windows_own_buttons_are_never_blocked()
        {
            var guard = new ConsoleEscapeGuard();
            guard.Observe(Frame, EscapeOwner.Console);
            guard.Claim(Frame);

            // A click on "Window" while the console is open: no Escape this frame.
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, toWindowed: true, escapeDown: false, consoleOpen: true));

            // Back to fullscreen is never blocked, Escape or not.
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, toWindowed: false, escapeDown: true, consoleOpen: true));
            Assert.Equal(0L, guard.BlockedCount);
        }

        [Fact]
        public void A_claim_from_the_previous_frame_does_not_block_this_one()
        {
            var guard = new ConsoleEscapeGuard();
            guard.Claim(Frame - 1);

            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: false));
        }

        [Fact]
        public void A_fresh_guard_blocks_nothing_and_reset_forgets()
        {
            var guard = new ConsoleEscapeGuard();
            Assert.Equal(ConsoleEscapeGuard.NoFrame, guard.ObservedFrame);
            Assert.Equal(ConsoleEscapeGuard.NoFrame, guard.ClaimedFrame);
            Assert.Equal(EscapeOwner.Game, guard.ObservedOwner);

            guard.Observe(Frame, EscapeOwner.Console);
            guard.Claim(Frame);
            guard.Reset();

            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true, consoleOpen: true));
        }

        [Fact]
        public void The_first_frames_of_a_session_are_safe()
        {
            // Unity's frameCount starts at 0; NoFrame must never look fresh.
            var guard = new ConsoleEscapeGuard();
            Assert.False(guard.ShouldBlockWindowedSwitch(0, true, true, consoleOpen: true));
            Assert.False(guard.ShouldBlockWindowedSwitch(int.MinValue + 1, true, true, consoleOpen: true));

            guard.Observe(0, EscapeOwner.Console);
            Assert.True(guard.ShouldBlockWindowedSwitch(1, true, true, consoleOpen: true));
        }
    }
}
