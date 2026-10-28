using System;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>The keys the console reacts to, as they went down this frame.</summary>
    [Flags]
    public enum ConsoleKeyPress
    {
        /// <summary>Nothing the console cares about.</summary>
        None = 0,

        /// <summary>Either chord of the <c>console.toggle</c> hotkey (Ctrl+Shift+C | F10 by default).</summary>
        ToggleChord = 1 << 0,

        /// <summary>Escape.</summary>
        Escape = 1 << 1,

        /// <summary>Tab.</summary>
        Tab = 1 << 2,

        /// <summary>Up arrow.</summary>
        Up = 1 << 3,

        /// <summary>Down arrow.</summary>
        Down = 1 << 4,

        /// <summary>Page Up.</summary>
        PageUp = 1 << 5,

        /// <summary>Page Down.</summary>
        PageDown = 1 << 6
    }

    /// <summary>What the console does with this frame's keys.</summary>
    public enum ConsoleKeyAction
    {
        /// <summary>Nothing: the key belongs to someone else, or to the text field itself.</summary>
        None,

        /// <summary>Run the console toggle.</summary>
        Toggle,

        /// <summary>Close the console.</summary>
        Close,

        /// <summary>Tab-complete the input line.</summary>
        Complete,

        /// <summary>Put the previous history entry in the input line.</summary>
        HistoryPrevious,

        /// <summary>Put the next history entry (or the half-typed line) in the input line.</summary>
        HistoryNext,

        /// <summary>Scroll the output up a page.</summary>
        PageUp,

        /// <summary>Scroll the output down a page.</summary>
        PageDown
    }

    /// <summary>What a character typed into the console input becomes.</summary>
    public enum ConsoleTypedChar
    {
        /// <summary>Insert it.</summary>
        Accept,

        /// <summary>Drop it: a control character, or a letter that is really part of a chord.</summary>
        Reject,

        /// <summary>Drop it and run the line: this was Return.</summary>
        Submit
    }

    /// <summary>Who has the keyboard this frame, as far as the console can tell.</summary>
    public readonly struct ConsoleFocus
    {
        /// <summary>Creates a focus snapshot.</summary>
        /// <param name="inputSelected">The console's input field is the UI's selected object.</param>
        /// <param name="inputActive">The console's input field is accepting typing.</param>
        /// <param name="otherTextFieldSelected">Some other text field (the game's note, a to-do) is selected.</param>
        /// <param name="composing">The OS input method has an unfinished composition.</param>
        /// <param name="onInputTab">The tab with the command line is showing.</param>
        public ConsoleFocus(
            bool inputSelected,
            bool inputActive,
            bool otherTextFieldSelected,
            bool composing,
            bool onInputTab)
        {
            InputSelected = inputSelected;
            InputActive = inputActive;
            OtherTextFieldSelected = otherTextFieldSelected;
            Composing = composing;
            OnInputTab = onInputTab;
        }

        /// <summary>The console's input field is the UI's selected object.</summary>
        public bool InputSelected { get; }

        /// <summary>The console's input field is accepting typing.</summary>
        public bool InputActive { get; }

        /// <summary>Some other text field is selected.</summary>
        public bool OtherTextFieldSelected { get; }

        /// <summary>The OS input method has an unfinished composition.</summary>
        public bool Composing { get; }

        /// <summary>The tab with the command line is showing.</summary>
        public bool OnInputTab { get; }
    }

    /// <summary>
    /// Decides who owns a key while the console is on screen: the console, the text field, the
    /// input method, the hotkey registry or the game. Pure, so every rule is unit-tested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The console's input is a real UI text field, so while it is selected the framework's hotkey
    /// registry stands down on its own - it ignores every chord while any text field has focus
    /// (<c>Hotkeys.IsTyping</c>), which is also why a typed letter never reaches the game's own
    /// shortcuts. That leaves the console two keys to handle itself: the toggle chord (the registry
    /// is not listening while our field is selected) and Escape.
    /// </para>
    /// <para>
    /// The toggle is claimed only when our field is selected, the exact condition under which the
    /// registry is silent, so exactly one of the two acts on a press. The console toggle is also
    /// debounced per frame, which covers the frame on which closing deselects the field.
    /// </para>
    /// <para>
    /// While the input method is composing (Japanese today, Thai later), Escape, Tab, the arrows and
    /// the page keys belong to it: they pick candidates and cancel the composition.
    /// </para>
    /// </remarks>
    public static class ConsoleKeyRouter
    {
        /// <summary>The single action for this frame's keys, in priority order.</summary>
        /// <param name="keys">Keys that went down this frame.</param>
        /// <param name="focus">Who has the keyboard.</param>
        public static ConsoleKeyAction Route(ConsoleKeyPress keys, ConsoleFocus focus)
        {
            if (keys == ConsoleKeyPress.None)
            {
                return ConsoleKeyAction.None;
            }

            // The registry ignores chords while a text field is selected; ours is one.
            if (Has(keys, ConsoleKeyPress.ToggleChord) && focus.InputSelected)
            {
                return ConsoleKeyAction.Toggle;
            }

            if (focus.Composing)
            {
                return ConsoleKeyAction.None;
            }

            // Another text field has the keyboard: the game is being typed into, and its keys
            // (Escape included) are the game's.
            if (focus.OtherTextFieldSelected && !focus.InputSelected)
            {
                return ConsoleKeyAction.None;
            }

            if (Has(keys, ConsoleKeyPress.Escape))
            {
                return ConsoleKeyAction.Close;
            }

            var lineKeys = focus.InputActive && focus.OnInputTab;

            if (Has(keys, ConsoleKeyPress.Tab) && lineKeys)
            {
                return ConsoleKeyAction.Complete;
            }

            if (Has(keys, ConsoleKeyPress.Up) && lineKeys)
            {
                return ConsoleKeyAction.HistoryPrevious;
            }

            if (Has(keys, ConsoleKeyPress.Down) && lineKeys)
            {
                return ConsoleKeyAction.HistoryNext;
            }

            if (Has(keys, ConsoleKeyPress.PageUp))
            {
                return ConsoleKeyAction.PageUp;
            }

            return Has(keys, ConsoleKeyPress.PageDown) ? ConsoleKeyAction.PageDown : ConsoleKeyAction.None;
        }

        /// <summary>
        /// What a character the text field is about to insert becomes. Return is a submit;
        /// control characters are dropped; so is any character typed with Ctrl (not AltGr, which
        /// Windows reports as Ctrl+Alt and which types real characters on many layouts) or with
        /// Command held - a chord such as Ctrl+Shift+C must not leave a letter behind. Everything
        /// else is kept, including non-ASCII: commands are ASCII today, but a Thai subtitle or an
        /// input-method commit must pass through untouched.
        /// </summary>
        /// <param name="c">The character.</param>
        /// <param name="control">A Control key is held.</param>
        /// <param name="command">A Command (macOS) key is held.</param>
        /// <param name="alt">An Alt / Option key is held.</param>
        /// <param name="pasting">
        /// The character is part of a paste (Command/Ctrl+V is down; the field appends the clipboard
        /// one character at a time with the chord held). Printable characters are kept; line breaks
        /// are dropped, so a pasted line is never submitted on its own.
        /// </param>
        public static ConsoleTypedChar ClassifyTypedChar(char c, bool control, bool command, bool alt, bool pasting = false)
        {
            if (pasting)
            {
                return c < ' ' || c == '\u007f' ? ConsoleTypedChar.Reject : ConsoleTypedChar.Accept;
            }

            var chord = command || (control && !alt);

            if (c == '\n' || c == '\r' || c == '\v')
            {
                // macOS reports Ctrl+C (and so Ctrl+Shift+C) as U+0003, which the text field turns
                // into '\n' before asking us: with a chord held it is the toggle, not Return.
                return chord ? ConsoleTypedChar.Reject : ConsoleTypedChar.Submit;
            }

            if (c < ' ' || c == '\u007f')
            {
                return ConsoleTypedChar.Reject;
            }

            return chord ? ConsoleTypedChar.Reject : ConsoleTypedChar.Accept;
        }

        private static bool Has(ConsoleKeyPress keys, ConsoleKeyPress key) => (keys & key) == key;
    }
}
