using CwyLs.ModLoader.Core.Ui;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// S-UI: a mod window's text box - Enter sends, Shift+Enter breaks the line (TextMeshPro hands
    /// Shift+Enter over as a vertical tab), chords leave nothing behind, an Enter that commits an
    /// input-method composition does neither, and all of Thai is accepted.
    /// </summary>
    public class UiTextBoxKeysTests
    {
        private static TextBoxChar Multi(char c, bool composing = false, bool submitOnEnter = true, bool control = false, bool command = false, bool alt = false) =>
            TextBoxKeys.Classify(c, control, command, alt, multiLine: true, submitOnEnter, composing);

        private static TextBoxChar Single(char c, bool composing = false, bool control = false) =>
            TextBoxKeys.Classify(c, control, false, false, multiLine: false, submitOnEnter: true, composing);

        [Fact]
        public void Enter_sends_and_shift_enter_breaks_the_line()
        {
            Assert.Equal(TextBoxChar.Submit, Multi('\n'));
            Assert.Equal(TextBoxChar.Submit, Multi('\r'));
            Assert.Equal(TextBoxChar.Newline, Multi('\v'));
        }

        [Fact]
        public void With_enter_as_newline_ctrl_or_command_enter_sends()
        {
            Assert.Equal(TextBoxChar.Newline, Multi('\n', submitOnEnter: false));
            Assert.Equal(TextBoxChar.Newline, Multi('\v', submitOnEnter: false));
            Assert.Equal(TextBoxChar.Submit, Multi('\n', submitOnEnter: false, control: true));
            Assert.Equal(TextBoxChar.Submit, Multi('\n', submitOnEnter: false, command: true));
        }

        [Fact]
        public void A_chord_never_leaves_a_newline_or_a_letter()
        {
            // macOS Ctrl+C (and the console's Ctrl+Shift+C) arrives as U+0003 -> '\n' with Ctrl held.
            Assert.Equal(TextBoxChar.Reject, Multi('\n', control: true));
            Assert.Equal(TextBoxChar.Reject, Multi('c', control: true));
            Assert.Equal(TextBoxChar.Reject, Multi('c', command: true));
            Assert.Equal(TextBoxChar.Reject, Single('\n', control: true));
        }

        [Fact]
        public void AltGr_and_option_still_type()
        {
            Assert.Equal(TextBoxChar.Accept, Multi('@', control: true, alt: true));
            Assert.Equal(TextBoxChar.Accept, Multi('é', alt: true));
        }

        [Fact]
        public void An_enter_that_commits_a_composition_neither_sends_nor_breaks()
        {
            Assert.Equal(TextBoxChar.Reject, Multi('\n', composing: true));
            Assert.Equal(TextBoxChar.Reject, Multi('\v', composing: true));
            Assert.Equal(TextBoxChar.Reject, Single('\n', composing: true));

            // Ordinary characters committed by the input method are kept.
            Assert.Equal(TextBoxChar.Accept, Multi('日', composing: true));
        }

        [Fact]
        public void A_one_line_box_sends_on_any_enter()
        {
            Assert.Equal(TextBoxChar.Submit, Single('\n'));
            Assert.Equal(TextBoxChar.Submit, Single('\v'));
        }

        [Fact]
        public void Control_characters_are_dropped()
        {
            Assert.Equal(TextBoxChar.Reject, Multi('\t'));
            Assert.Equal(TextBoxChar.Reject, Multi((char)0x7f));
            Assert.Equal(TextBoxChar.Reject, Multi((char)0x01));
        }

        [Fact]
        public void Every_assigned_Thai_code_point_is_accepted()
        {
            for (var c = 0x0E01; c <= 0x0E5B; c++)
            {
                if (c >= 0x0E3B && c <= 0x0E3E)
                {
                    continue; // unassigned
                }

                Assert.Equal(TextBoxChar.Accept, Multi((char)c));
                Assert.Equal(TextBoxChar.Accept, Single((char)c));
            }

            foreach (var c in ThaiMarkCheck.Sample)
            {
                Assert.Equal(TextBoxChar.Accept, Multi(c));
            }

            Assert.Equal(TextBoxChar.Accept, Multi((char)0x200B));
        }

        [Fact]
        public void Composing_recently_covers_this_frame_and_the_one_before()
        {
            Assert.True(TextBoxKeys.ComposingRecently(true, 10, int.MinValue));
            Assert.True(TextBoxKeys.ComposingRecently(false, 10, 10));
            Assert.True(TextBoxKeys.ComposingRecently(false, 10, 9));
            Assert.False(TextBoxKeys.ComposingRecently(false, 10, 8));
            Assert.False(TextBoxKeys.ComposingRecently(false, 10, int.MinValue));
            Assert.False(TextBoxKeys.ComposingRecently(false, 10, 11));
        }
    }
}
