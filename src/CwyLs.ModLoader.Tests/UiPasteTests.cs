using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Ui;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Command/Ctrl+V into a mod text box or the console. TextMeshPro appends the clipboard one
    /// character at a time with the chord still held, so the chord rule that keeps Ctrl+Shift+C from
    /// leaving a "C" dropped the whole paste.
    /// </summary>
    public class UiPasteTests
    {
        [Theory]
        [InlineData('ท')]
        [InlineData('่')]
        [InlineData('a')]
        [InlineData(' ')]
        public void A_pasted_character_is_kept_with_command_held(char c)
        {
            Assert.Equal(TextBoxChar.Accept, TextBoxKeys.Classify(c, false, true, false, true, true, false, pasting: true));
            Assert.Equal(ConsoleTypedChar.Accept, ConsoleKeyRouter.ClassifyTypedChar(c, false, true, false, pasting: true));
        }

        [Fact]
        public void A_typed_chord_still_leaves_nothing_behind()
        {
            Assert.Equal(TextBoxChar.Reject, TextBoxKeys.Classify('C', true, false, false, true, true, false));
            Assert.Equal(ConsoleTypedChar.Reject, ConsoleKeyRouter.ClassifyTypedChar('C', false, true, false));
        }

        [Fact]
        public void A_pasted_line_break_is_a_new_line_in_a_multi_line_box_and_never_sends()
        {
            Assert.Equal(TextBoxChar.Newline, TextBoxKeys.Classify('\n', false, true, false, true, true, false, pasting: true));
            Assert.Equal(TextBoxChar.Reject, TextBoxKeys.Classify('\n', false, true, false, false, true, false, pasting: true));
            Assert.Equal(TextBoxChar.Reject, TextBoxKeys.Classify('\r', false, true, false, true, true, false, pasting: true));
            Assert.Equal(ConsoleTypedChar.Reject, ConsoleKeyRouter.ClassifyTypedChar('\n', false, true, false, pasting: true));
        }

        [Fact]
        public void Pasted_control_characters_are_dropped()
        {
            Assert.Equal(TextBoxChar.Reject, TextBoxKeys.Classify('\u0003', false, true, false, true, true, false, pasting: true));
            Assert.Equal(ConsoleTypedChar.Reject, ConsoleKeyRouter.ClassifyTypedChar('\u007f', false, true, false, pasting: true));
        }
    }
}
