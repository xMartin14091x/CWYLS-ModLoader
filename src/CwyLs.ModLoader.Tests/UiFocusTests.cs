using CwyLs.ModLoader.Core.Ui;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// S-UI focus isolation: the window order, which window the player is in, who an Escape belongs
    /// to, and the guard that keeps the game's fullscreen-to-windowed switch off an Escape a mod
    /// window used (generalising C5's console rule).
    /// </summary>
    public class UiFocusTests
    {
        private const int Frame = 500;

        private static EscapeInputs In(
            bool claimed = false,
            bool consoleOwns = false,
            string? keyboard = null,
            bool composing = false,
            bool keyboardCloses = true,
            bool gameField = false,
            string? active = null,
            bool activeCloses = true) =>
            new EscapeInputs(claimed, consoleOwns, keyboard, composing, keyboardCloses, gameField, active, activeCloses);

        // ---- window order ------------------------------------------------------------------------

        [Fact]
        public void Showing_a_window_puts_it_on_top_and_makes_it_active()
        {
            var stack = new WindowStack();
            stack.Add("m/a");
            stack.Add("m/b");
            Assert.Null(stack.Active);
            stack.Shown("m/a");
            Assert.Equal("m/a", stack.Top);
            Assert.Equal("m/a", stack.Active);
            Assert.Equal(new[] { "m/b", "m/a" }, stack.Order);
        }

        [Fact]
        public void A_press_outside_every_mod_window_hands_escape_back_to_the_game()
        {
            var stack = new WindowStack();
            stack.Add("m/a");
            stack.Shown("m/a");
            Assert.False(stack.PointerDown(null));
            Assert.Null(stack.Active);
            Assert.Equal("m/a", stack.Top);
        }

        [Fact]
        public void A_press_on_a_lower_window_raises_it()
        {
            var stack = new WindowStack();
            stack.Add("m/a");
            stack.Add("m/b");
            Assert.True(stack.PointerDown("m/a"));
            Assert.Equal("m/a", stack.Active);
            Assert.Equal(new[] { "m/b", "m/a" }, stack.Order);
            Assert.False(stack.PointerDown("m/a"));
        }

        [Fact]
        public void Hiding_or_removing_the_active_window_clears_it()
        {
            var stack = new WindowStack();
            stack.Add("m/a");
            stack.Add("m/b");
            stack.Shown("m/a");
            stack.Hidden("m/b");
            Assert.Equal("m/a", stack.Active);
            stack.Hidden("m/a");
            Assert.Null(stack.Active);

            stack.Shown("m/b");
            Assert.True(stack.Remove("m/b"));
            Assert.Null(stack.Active);
            Assert.Equal(1, stack.Count);
            Assert.False(stack.Remove("m/b"));
        }

        [Fact]
        public void Hit_test_finds_the_top_most_hit()
        {
            var stack = new WindowStack();
            stack.Add("m/a");
            stack.Add("m/b");
            stack.Add("m/c");
            Assert.Equal("m/b", stack.HitTest(key => key != "m/c"));
            Assert.Null(stack.HitTest(_ => false));
            Assert.Equal(2, stack.IndexOf("m/c"));
        }

        [Fact]
        public void Unknown_keys_change_nothing()
        {
            var stack = new WindowStack();
            stack.Add("m/a");
            stack.Add("m/a");
            Assert.Equal(1, stack.Count);
            Assert.False(stack.Activate("x/y"));
            Assert.False(stack.PointerDown("x/y"));
            Assert.Null(stack.Active);
        }

        // ---- who owns Escape ---------------------------------------------------------------------

        [Fact]
        public void Typing_in_a_mod_window_escape_closes_that_window_only()
        {
            var decision = EscapeRouting.Decide(In(keyboard: "m/chat", consoleOwns: true, active: "m/other"));
            Assert.Equal(new EscapeDecision(EscapeAction.CloseWindow, "m/chat"), decision);
            Assert.True(decision.ModWindowOwns);
        }

        [Fact]
        public void A_window_that_stays_open_only_lets_go_of_the_keyboard()
        {
            var decision = EscapeRouting.Decide(In(keyboard: "m/chat", keyboardCloses: false));
            Assert.Equal(new EscapeDecision(EscapeAction.ReleaseKeyboard, "m/chat"), decision);
            Assert.True(decision.ModWindowOwns);
        }

        [Fact]
        public void While_composing_escape_belongs_to_the_input_method_and_the_game_still_does_nothing()
        {
            var decision = EscapeRouting.Decide(In(keyboard: "m/chat", composing: true));
            Assert.Equal(EscapeAction.InputMethod, decision.Action);
            Assert.True(decision.ModWindowOwns);
        }

        [Fact]
        public void An_escape_the_console_already_used_is_nobody_elses()
        {
            var decision = EscapeRouting.Decide(In(claimed: true, keyboard: "m/chat", active: "m/chat"));
            Assert.Equal(EscapeAction.Console, decision.Action);
            Assert.False(decision.ModWindowOwns);
        }

        [Fact]
        public void A_game_text_field_keeps_its_escape()
        {
            var decision = EscapeRouting.Decide(In(gameField: true, active: "m/chat", consoleOwns: true));
            Assert.Equal(EscapeAction.Game, decision.Action);
            Assert.False(decision.ModWindowOwns);
        }

        [Fact]
        public void The_open_console_comes_before_the_active_mod_window()
        {
            Assert.Equal(EscapeAction.Console, EscapeRouting.Decide(In(consoleOwns: true, active: "m/chat")).Action);
        }

        [Fact]
        public void The_active_window_closes_when_nothing_else_wants_escape()
        {
            Assert.Equal(new EscapeDecision(EscapeAction.CloseWindow, "m/list"), EscapeRouting.Decide(In(active: "m/list")));
        }

        [Fact]
        public void A_window_that_does_not_close_on_escape_leaves_it_to_the_game_without_the_keyboard()
        {
            var decision = EscapeRouting.Decide(In(active: "m/chat", activeCloses: false));
            Assert.Equal(EscapeAction.Game, decision.Action);
            Assert.False(decision.ModWindowOwns);
        }

        [Fact]
        public void Nothing_open_is_the_game()
        {
            Assert.Equal(new EscapeDecision(EscapeAction.Game, null), EscapeRouting.Decide(In()));
        }

        [Fact]
        public void WithoutClaim_keeps_everything_else()
        {
            var inputs = In(claimed: true, keyboard: "m/a", composing: true, gameField: true, active: "m/b", activeCloses: false).WithoutClaim();
            Assert.False(inputs.ConsoleClaimedThisFrame);
            Assert.Equal("m/a", inputs.KeyboardWindow);
            Assert.True(inputs.KeyboardComposing);
            Assert.True(inputs.GameTextFieldSelected);
            Assert.Equal("m/b", inputs.ActiveWindow);
            Assert.False(inputs.ActiveWindowClosesOnEscape);
        }

        // ---- the game's windowed switch ----------------------------------------------------------

        [Fact]
        public void The_switch_is_skipped_after_a_fresh_word_that_a_window_owns_escape()
        {
            var guard = new WindowEscapeGuard();
            guard.Observe(Frame - 1, true);
            Assert.True(guard.ShouldBlockWindowedSwitch(Frame, toWindowed: true, escapeDown: true));
            Assert.Equal(1, guard.BlockedCount);

            guard.Observe(Frame, true);
            Assert.True(guard.ShouldBlockWindowedSwitch(Frame, true, true));
        }

        [Fact]
        public void The_switch_runs_otherwise()
        {
            var guard = new WindowEscapeGuard();
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true));

            guard.Observe(Frame - 1, false);
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true));

            guard.Observe(Frame - 1, true);
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, toWindowed: false, escapeDown: true));
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, toWindowed: true, escapeDown: false));

            guard.Observe(Frame - 2, true);
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true));

            guard.Observe(Frame + 1, true);
            Assert.False(guard.ShouldBlockWindowedSwitch(Frame, true, true));
            Assert.Equal(0, guard.BlockedCount);

            guard.Reset();
            Assert.Equal(WindowEscapeGuard.NoFrame, guard.ObservedFrame);
            Assert.False(guard.ObservedOwns);
        }
    }
}
