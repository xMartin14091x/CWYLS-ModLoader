using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI: who owns a key while the game-style console is open. The console input is a real text
    /// field, so the hotkey registry stands down while it is selected; the console then handles
    /// the toggle and Escape itself, and leaves every key to an input method that is composing and
    /// to a game text field that has the keyboard.
    /// </summary>
    public class ConsoleKeyRouterTests
    {
        private static ConsoleFocus Typing(bool composing = false, bool onInputTab = true) =>
            new ConsoleFocus(inputSelected: true, inputActive: true, otherTextFieldSelected: false, composing: composing, onInputTab: onInputTab);

        private static readonly ConsoleFocus Nobody =
            new ConsoleFocus(inputSelected: false, inputActive: false, otherTextFieldSelected: false, composing: false, onInputTab: true);

        private static readonly ConsoleFocus GameField =
            new ConsoleFocus(inputSelected: false, inputActive: false, otherTextFieldSelected: true, composing: false, onInputTab: true);

        [Fact]
        public void No_key_no_action()
        {
            Assert.Equal(ConsoleKeyAction.None, ConsoleKeyRouter.Route(ConsoleKeyPress.None, Typing()));
        }

        [Fact]
        public void The_toggle_is_the_consoles_while_its_input_has_the_keyboard()
        {
            Assert.Equal(ConsoleKeyAction.Toggle, ConsoleKeyRouter.Route(ConsoleKeyPress.ToggleChord, Typing()));
        }

        [Fact]
        public void The_toggle_is_the_registrys_when_the_input_is_not_selected_so_it_fires_once()
        {
            Assert.Equal(ConsoleKeyAction.None, ConsoleKeyRouter.Route(ConsoleKeyPress.ToggleChord, Nobody));
            Assert.Equal(ConsoleKeyAction.None, ConsoleKeyRouter.Route(ConsoleKeyPress.ToggleChord, GameField));
        }

        [Fact]
        public void The_toggle_works_even_while_the_input_method_is_composing()
        {
            Assert.Equal(ConsoleKeyAction.Toggle, ConsoleKeyRouter.Route(ConsoleKeyPress.ToggleChord, Typing(composing: true)));
        }

        [Fact]
        public void Escape_closes_from_the_input_and_from_anywhere_that_is_not_a_game_field()
        {
            Assert.Equal(ConsoleKeyAction.Close, ConsoleKeyRouter.Route(ConsoleKeyPress.Escape, Typing()));
            Assert.Equal(ConsoleKeyAction.Close, ConsoleKeyRouter.Route(ConsoleKeyPress.Escape, Nobody));
        }

        [Fact]
        public void Keys_typed_into_a_game_text_field_are_the_games()
        {
            foreach (var key in new[] { ConsoleKeyPress.Escape, ConsoleKeyPress.Tab, ConsoleKeyPress.Up, ConsoleKeyPress.PageUp })
            {
                Assert.Equal(ConsoleKeyAction.None, ConsoleKeyRouter.Route(key, GameField));
            }
        }

        [Fact]
        public void A_composing_input_method_owns_escape_tab_arrows_and_pages()
        {
            var composing = Typing(composing: true);
            foreach (var key in new[]
                     {
                         ConsoleKeyPress.Escape, ConsoleKeyPress.Tab, ConsoleKeyPress.Up,
                         ConsoleKeyPress.Down, ConsoleKeyPress.PageUp, ConsoleKeyPress.PageDown
                     })
            {
                Assert.Equal(ConsoleKeyAction.None, ConsoleKeyRouter.Route(key, composing));
            }
        }

        [Theory]
        [InlineData(ConsoleKeyPress.Tab, ConsoleKeyAction.Complete)]
        [InlineData(ConsoleKeyPress.Up, ConsoleKeyAction.HistoryPrevious)]
        [InlineData(ConsoleKeyPress.Down, ConsoleKeyAction.HistoryNext)]
        [InlineData(ConsoleKeyPress.PageUp, ConsoleKeyAction.PageUp)]
        [InlineData(ConsoleKeyPress.PageDown, ConsoleKeyAction.PageDown)]
        public void Line_keys_work_while_typing(ConsoleKeyPress key, ConsoleKeyAction expected)
        {
            Assert.Equal(expected, ConsoleKeyRouter.Route(key, Typing()));
        }

        [Fact]
        public void Tab_and_the_arrows_need_the_command_line_page_keys_do_not()
        {
            var viewTab = Typing(onInputTab: false);

            Assert.Equal(ConsoleKeyAction.None, ConsoleKeyRouter.Route(ConsoleKeyPress.Tab, viewTab));
            Assert.Equal(ConsoleKeyAction.None, ConsoleKeyRouter.Route(ConsoleKeyPress.Up, Nobody));
            Assert.Equal(ConsoleKeyAction.PageDown, ConsoleKeyRouter.Route(ConsoleKeyPress.PageDown, Nobody));
        }

        [Fact]
        public void One_action_per_frame_with_the_toggle_first_then_escape()
        {
            Assert.Equal(
                ConsoleKeyAction.Toggle,
                ConsoleKeyRouter.Route(ConsoleKeyPress.ToggleChord | ConsoleKeyPress.Escape | ConsoleKeyPress.Tab, Typing()));
            Assert.Equal(
                ConsoleKeyAction.Close,
                ConsoleKeyRouter.Route(ConsoleKeyPress.Escape | ConsoleKeyPress.Tab, Typing()));
        }

        [Theory]
        [InlineData('a')]
        [InlineData('Z')]
        [InlineData(' ')]
        [InlineData('<')]
        [InlineData('ก')]   // Thai ko kai: non-ASCII passes through for later Thai typing
        [InlineData('あ')]   // Hiragana a: an input-method commit
        public void Printable_characters_are_typed(char c)
        {
            Assert.Equal(ConsoleTypedChar.Accept, ConsoleKeyRouter.ClassifyTypedChar(c, control: false, command: false, alt: false));
        }

        [Theory]
        [InlineData('\n')]
        [InlineData('\r')]
        [InlineData('\v')]
        public void Return_submits(char c)
        {
            Assert.Equal(ConsoleTypedChar.Submit, ConsoleKeyRouter.ClassifyTypedChar(c, control: false, command: false, alt: false));
        }

        [Fact]
        public void Ctrl_shift_c_on_macos_is_not_a_return()
        {
            // macOS reports Ctrl+C as U+0003; the text field turns it into '\n' before asking.
            Assert.Equal(ConsoleTypedChar.Reject, ConsoleKeyRouter.ClassifyTypedChar('\n', control: true, command: false, alt: false));
            Assert.Equal(ConsoleTypedChar.Reject, ConsoleKeyRouter.ClassifyTypedChar('\u0003', control: true, command: false, alt: false));
        }

        [Fact]
        public void A_letter_typed_as_part_of_a_chord_is_dropped()
        {
            Assert.Equal(ConsoleTypedChar.Reject, ConsoleKeyRouter.ClassifyTypedChar('C', control: true, command: false, alt: false));
            Assert.Equal(ConsoleTypedChar.Reject, ConsoleKeyRouter.ClassifyTypedChar('c', control: false, command: true, alt: false));
        }

        [Fact]
        public void AltGr_characters_on_windows_layouts_are_kept()
        {
            // Windows reports AltGr as Ctrl+Alt; '@' on a German layout must still type.
            Assert.Equal(ConsoleTypedChar.Accept, ConsoleKeyRouter.ClassifyTypedChar('@', control: true, command: false, alt: true));
        }

        [Theory]
        [InlineData('\t')]
        [InlineData('\u0000')]
        [InlineData('\u001b')]
        [InlineData('\u007f')]
        public void Control_characters_are_dropped(char c)
        {
            Assert.Equal(ConsoleTypedChar.Reject, ConsoleKeyRouter.ClassifyTypedChar(c, control: false, command: false, alt: false));
        }
    }
}
