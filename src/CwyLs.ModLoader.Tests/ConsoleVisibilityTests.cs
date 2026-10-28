using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI: an open console fades with the game's Hide UI, and the toggle key always ends with the
    /// console visible or closed - never open and invisible.
    /// </summary>
    public class ConsoleVisibilityTests
    {
        private const float Shown = 1f;
        private const float Hidden = 0f;

        [Fact]
        public void An_open_console_follows_the_games_ui_opacity()
        {
            var rule = new ConsoleVisibility();

            Assert.Equal(1f, rule.TargetAlpha(Shown));
            Assert.Equal(0.4f, rule.TargetAlpha(0.4f), 4);
            Assert.Equal(0f, rule.TargetAlpha(Hidden));
        }

        [Fact]
        public void With_follow_off_the_console_ignores_hide_ui()
        {
            var rule = new ConsoleVisibility { FollowGameUi = false };

            Assert.Equal(1f, rule.TargetAlpha(Hidden));
            Assert.Equal(ConsoleToggleIntent.Close, rule.OnToggle(isOpen: true, gameUiAlpha: Hidden));
        }

        [Fact]
        public void Toggling_a_console_hidden_by_the_game_reveals_it_instead_of_closing_it()
        {
            var rule = new ConsoleVisibility();

            Assert.Equal(ConsoleToggleIntent.Reveal, rule.OnToggle(isOpen: true, gameUiAlpha: Hidden));
            Assert.True(rule.Revealed);
            Assert.Equal(1f, rule.TargetAlpha(Hidden));

            Assert.Equal(ConsoleToggleIntent.Close, rule.OnToggle(isOpen: true, gameUiAlpha: Hidden));
            Assert.False(rule.Revealed);
        }

        [Fact]
        public void Opening_while_the_ui_is_hidden_shows_the_console_at_once()
        {
            var rule = new ConsoleVisibility();

            Assert.Equal(ConsoleToggleIntent.Open, rule.OnToggle(isOpen: false, gameUiAlpha: Hidden));
            Assert.Equal(1f, rule.TargetAlpha(Hidden));
        }

        [Fact]
        public void A_reveal_ends_when_the_games_ui_comes_back()
        {
            var rule = new ConsoleVisibility();
            rule.OnToggle(isOpen: true, gameUiAlpha: Hidden);

            rule.Observe(Hidden);
            Assert.True(rule.Revealed);

            rule.Observe(Shown);
            Assert.False(rule.Revealed);
            Assert.Equal(0f, rule.TargetAlpha(Hidden));
        }

        [Fact]
        public void With_the_ui_shown_the_toggle_opens_and_closes()
        {
            var rule = new ConsoleVisibility();

            Assert.Equal(ConsoleToggleIntent.Open, rule.OnToggle(isOpen: false, gameUiAlpha: Shown));
            Assert.False(rule.Revealed);
            Assert.Equal(ConsoleToggleIntent.Close, rule.OnToggle(isOpen: true, gameUiAlpha: Shown));
        }

        [Theory]
        [InlineData(0f, true)]
        [InlineData(0.01f, true)]
        [InlineData(0.02f, false)]
        [InlineData(1f, false)]
        [InlineData(float.NaN, true)]
        public void Hidden_means_faded_all_the_way(float alpha, bool hidden)
        {
            Assert.Equal(hidden, ConsoleVisibility.IsHidden(alpha));
        }

        [Fact]
        public void An_unknown_opacity_never_hides_an_open_console()
        {
            Assert.Equal(1f, new ConsoleVisibility().TargetAlpha(float.NaN));
        }
    }
}
