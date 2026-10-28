namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>What a character a mod window's text box is about to insert becomes.</summary>
    public enum TextBoxChar
    {
        /// <summary>Insert it as it is.</summary>
        Accept,

        /// <summary>Drop it: a control character, a letter that is part of a chord, or a key the input method used.</summary>
        Reject,

        /// <summary>Drop it and send the text.</summary>
        Submit,

        /// <summary>Insert a line break (<c>'\n'</c>) instead.</summary>
        Newline,
    }

    /// <summary>
    /// The keys of a mod window's text box: Enter sends, Shift+Enter starts a new line, and
    /// nothing a chord or the input method used is inserted. Pure, so every rule is unit-tested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the game's TextMeshPro hands over (<c>TMP_InputField.KeyPressed</c>,
    /// multi-line-newline mode): Return and keypad Enter arrive as <c>'\n'</c> (<c>'\r'</c> and macOS's Ctrl+C, U+0003, are turned into
    /// it), and with Shift held as a vertical tab <c>'\v'</c>.
    /// </para>
    /// <para>
    /// While the operating system's input method is composing (Japanese Kotoeri; Thai layouts never
    /// compose), and on the frame its composition ended, Enter commits the composition: it
    /// must neither send nor break the line.
    /// </para>
    /// <para>
    /// The console's one-line field keeps its own rule (<c>ConsoleKeyRouter.ClassifyTypedChar</c>);
    /// this one adds the second line and the composition guard for mod windows.
    /// </para>
    /// </remarks>
    public static class TextBoxKeys
    {
        /// <summary>Classifies one character.</summary>
        /// <param name="c">The character the text box is about to insert.</param>
        /// <param name="control">A Control key is held.</param>
        /// <param name="command">A Command (macOS) key is held.</param>
        /// <param name="alt">An Alt / Option key is held (AltGr is Ctrl+Alt on Windows and types characters).</param>
        /// <param name="multiLine">The box shows more than one line.</param>
        /// <param name="submitOnEnter">Enter sends (true) or breaks the line (false; then Ctrl/Command+Enter sends).</param>
        /// <param name="composing">The input method is composing now or finished composing this frame or the last.</param>
        /// <param name="pasting">
        /// The character is part of a paste (Command/Ctrl+V is down): TextMeshPro appends the
        /// clipboard one character at a time with the chord still held (<c>TMP_InputField</c>),
        /// so the chord rule would drop all of it. Printable
        /// characters are kept, a line break becomes a new line in a multi-line box and is dropped in
        /// a one-line box - a paste never sends.
        /// </param>
        public static TextBoxChar Classify(char c, bool control, bool command, bool alt, bool multiLine, bool submitOnEnter, bool composing, bool pasting = false)
        {
            if (pasting)
            {
                if (c == '\n' || c == '\v')
                {
                    return multiLine ? TextBoxChar.Newline : TextBoxChar.Reject;
                }

                return c < ' ' || c == '\u007f' ? TextBoxChar.Reject : TextBoxChar.Accept;
            }

            var chord = command || (control && !alt);

            if (c == '\n' || c == '\r' || c == '\v')
            {
                if (composing)
                {
                    return TextBoxChar.Reject;
                }

                var shifted = c == '\v';
                if (chord)
                {
                    // Ctrl/Command+Enter sends where Enter alone breaks the line; otherwise a chord
                    // (macOS Ctrl+C arrives as a newline) is dropped.
                    return multiLine && !submitOnEnter && !shifted ? TextBoxChar.Submit : TextBoxChar.Reject;
                }

                if (!multiLine)
                {
                    return TextBoxChar.Submit;
                }

                if (shifted)
                {
                    return TextBoxChar.Newline;
                }

                return submitOnEnter ? TextBoxChar.Submit : TextBoxChar.Newline;
            }

            if (c < ' ' || c == '\u007f')
            {
                return TextBoxChar.Reject;
            }

            return chord ? TextBoxChar.Reject : TextBoxChar.Accept;
        }

        /// <summary>
        /// True when an Enter now belongs to the input method: it is composing, or it was composing
        /// on this frame or the one before (the frame the commit cleared the composition).
        /// </summary>
        /// <param name="composingNow">The composition string is not empty now.</param>
        /// <param name="frame">The current frame number.</param>
        /// <param name="lastComposingFrame">The last frame the composition was seen, or <see cref="int.MinValue"/> for never.</param>
        public static bool ComposingRecently(bool composingNow, int frame, int lastComposingFrame) =>
            composingNow
            || (lastComposingFrame != int.MinValue && lastComposingFrame <= frame && (long)frame - lastComposingFrame <= 1L);
    }
}
