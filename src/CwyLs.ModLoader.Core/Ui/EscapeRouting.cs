using System;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>What Escape does this frame, as far as mod windows are concerned.</summary>
    public enum EscapeAction
    {
        /// <summary>The game's: it handles Escape as it always does (fullscreen to windowed).</summary>
        Game,

        /// <summary>The CWY console's: it closes (<c>ConsoleEscapeGuard</c>). Mod windows do nothing.</summary>
        Console,

        /// <summary>The input method's: Escape cancels a composition in a mod window's text box. Nothing else happens.</summary>
        InputMethod,

        /// <summary>A mod window closes (<see cref="CwyWindowSpec.CloseOnEscape"/>).</summary>
        CloseWindow,

        /// <summary>A mod window's text box gives the keyboard back; the window stays.</summary>
        ReleaseKeyboard,
    }

    /// <summary>Who has the keyboard this frame, as the mod-window manager sees it.</summary>
    public readonly struct EscapeInputs
    {
        /// <summary>Creates a snapshot.</summary>
        /// <param name="consoleClaimedThisFrame">The console already used this frame's Escape.</param>
        /// <param name="consoleOwnsEscape">The console is open, takes input, and its latest word (this frame or the last) is that Escape is its own.</param>
        /// <param name="keyboardWindow">The mod window whose text box is selected and takes input, or null.</param>
        /// <param name="keyboardComposing">That text box's input method is composing (or just was).</param>
        /// <param name="keyboardWindowClosesOnEscape">That window's <see cref="CwyWindowSpec.CloseOnEscape"/>.</param>
        /// <param name="gameTextFieldSelected">One of the game's own text fields (a note, a to-do) is selected.</param>
        /// <param name="activeWindow">The mod window the player is in (<see cref="WindowStack.Active"/>), shown and taking input, or null.</param>
        /// <param name="activeWindowClosesOnEscape">That window's <see cref="CwyWindowSpec.CloseOnEscape"/>.</param>
        public EscapeInputs(
            bool consoleClaimedThisFrame,
            bool consoleOwnsEscape,
            string? keyboardWindow,
            bool keyboardComposing,
            bool keyboardWindowClosesOnEscape,
            bool gameTextFieldSelected,
            string? activeWindow,
            bool activeWindowClosesOnEscape)
        {
            ConsoleClaimedThisFrame = consoleClaimedThisFrame;
            ConsoleOwnsEscape = consoleOwnsEscape;
            KeyboardWindow = keyboardWindow;
            KeyboardComposing = keyboardComposing;
            KeyboardWindowClosesOnEscape = keyboardWindowClosesOnEscape;
            GameTextFieldSelected = gameTextFieldSelected;
            ActiveWindow = activeWindow;
            ActiveWindowClosesOnEscape = activeWindowClosesOnEscape;
        }

        /// <summary>The console already used this frame's Escape.</summary>
        public bool ConsoleClaimedThisFrame { get; }

        /// <summary>The console would take an Escape now.</summary>
        public bool ConsoleOwnsEscape { get; }

        /// <summary>The mod window whose text box has the keyboard, or null.</summary>
        public string? KeyboardWindow { get; }

        /// <summary>That text box is composing.</summary>
        public bool KeyboardComposing { get; }

        /// <summary>That window closes on Escape.</summary>
        public bool KeyboardWindowClosesOnEscape { get; }

        /// <summary>A game text field is selected.</summary>
        public bool GameTextFieldSelected { get; }

        /// <summary>The mod window the player is in, or null.</summary>
        public string? ActiveWindow { get; }

        /// <summary>That window closes on Escape.</summary>
        public bool ActiveWindowClosesOnEscape { get; }

        /// <summary>The same snapshot without the console's claim (what the next frame would see).</summary>
        public EscapeInputs WithoutClaim() =>
            new EscapeInputs(
                false, ConsoleOwnsEscape, KeyboardWindow, KeyboardComposing, KeyboardWindowClosesOnEscape,
                GameTextFieldSelected, ActiveWindow, ActiveWindowClosesOnEscape);
    }

    /// <summary>The decision: what Escape does, and to which mod window.</summary>
    public readonly struct EscapeDecision : IEquatable<EscapeDecision>
    {
        /// <summary>Creates a decision.</summary>
        /// <param name="action">What happens.</param>
        /// <param name="window">The mod window it happens to, or null.</param>
        public EscapeDecision(EscapeAction action, string? window)
        {
            Action = action;
            Window = window;
        }

        /// <summary>What happens.</summary>
        public EscapeAction Action { get; }

        /// <summary>The mod window it happens to, or null.</summary>
        public string? Window { get; }

        /// <summary>
        /// True when a mod window (or its text box's input method) owns the press: the game's own
        /// Escape handling must then be skipped (<see cref="WindowEscapeGuard"/>).
        /// </summary>
        public bool ModWindowOwns =>
            Action == EscapeAction.CloseWindow || Action == EscapeAction.ReleaseKeyboard || Action == EscapeAction.InputMethod;

        /// <inheritdoc />
        public bool Equals(EscapeDecision other) => Action == other.Action && Window == other.Window;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is EscapeDecision other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => ((int)Action * 397) ^ (Window?.GetHashCode() ?? 0);

        /// <summary><c>CloseWindow my.mod/chat</c>.</summary>
        public override string ToString() => Window is null ? Action.ToString() : Action + " " + Window;
    }

    /// <summary>
    /// Focus isolation for mod windows, generalising the console's Escape rule: who an
    /// Escape belongs to when mod windows, the console and the game's own fields may all be on
    /// screen. Pure, so every rule is unit-tested.
    /// </summary>
    /// <remarks>
    /// <para>In priority order:</para>
    /// <list type="number">
    /// <item>The console already closed on this Escape: nobody else acts.</item>
    /// <item>A mod window's text box has the keyboard: composing → the input method cancels the
    /// composition, nothing else; otherwise the window closes, or with
    /// <see cref="CwyWindowSpec.CloseOnEscape"/> off its text box lets go of the keyboard. The console
    /// stands down by its own rule (another text field is selected), and the game's switch to
    /// windowed mode is skipped.</item>
    /// <item>One of the game's text fields has the keyboard: the game's.</item>
    /// <item>The console is open and would take it: the console's.</item>
    /// <item>The player is in a mod window that closes on Escape: it closes.</item>
    /// <item>Otherwise the game's.</item>
    /// </list>
    /// <para>
    /// Hotkeys need no rule here: the framework's registry ignores every chord while any text field
    /// is selected (<c>Hotkeys.IsTyping</c>), and in the room the game reads no key but Escape
    /// (<c>RoomGameManager</c>; <c>CameraManager</c>/<c>Camera_Controller</c> are not in
    /// <c>RoomScene</c>).
    /// </para>
    /// </remarks>
    public static class EscapeRouting
    {
        /// <summary>Decides who an Escape belongs to.</summary>
        /// <param name="inputs">Who has the keyboard.</param>
        public static EscapeDecision Decide(EscapeInputs inputs)
        {
            if (inputs.ConsoleClaimedThisFrame)
            {
                return new EscapeDecision(EscapeAction.Console, null);
            }

            if (inputs.KeyboardWindow != null)
            {
                if (inputs.KeyboardComposing)
                {
                    return new EscapeDecision(EscapeAction.InputMethod, inputs.KeyboardWindow);
                }

                return new EscapeDecision(
                    inputs.KeyboardWindowClosesOnEscape ? EscapeAction.CloseWindow : EscapeAction.ReleaseKeyboard,
                    inputs.KeyboardWindow);
            }

            if (inputs.GameTextFieldSelected)
            {
                return new EscapeDecision(EscapeAction.Game, null);
            }

            if (inputs.ConsoleOwnsEscape)
            {
                return new EscapeDecision(EscapeAction.Console, null);
            }

            if (inputs.ActiveWindow != null && inputs.ActiveWindowClosesOnEscape)
            {
                return new EscapeDecision(EscapeAction.CloseWindow, inputs.ActiveWindow);
            }

            return new EscapeDecision(EscapeAction.Game, null);
        }
    }

    /// <summary>
    /// Keeps the game from acting on an Escape a mod window used: the game reads Escape itself in
    /// <c>RoomGameManager.Update</c> and calls <c>SettingService.ChangeWindowMode(Window)</c>, which
    /// would drop a fullscreen game to windowed (the same problem the console's
    /// <c>ConsoleEscapeGuard</c> solves).
    /// </summary>
    /// <remarks>
    /// Mod windows decide and act on Escape in <c>LateUpdate</c>, after every <c>Update</c> - so after
    /// the console and the game have read the key - and record there who would own an Escape pressed
    /// on the next frame (<see cref="Observe"/>). The game's switch is skipped when that word is fresh
    /// (this frame or the last), says a mod window, and Escape went down this frame. Main thread only.
    /// </remarks>
    public sealed class WindowEscapeGuard
    {
        /// <summary>Frame number meaning "never".</summary>
        public const int NoFrame = int.MinValue;

        private int _observedFrame = NoFrame;
        private bool _observedOwns;
        private long _blocked;

        /// <summary>The frame of the latest <see cref="Observe"/>.</summary>
        public int ObservedFrame => _observedFrame;

        /// <summary>Whether the latest observation said a mod window owns Escape.</summary>
        public bool ObservedOwns => _observedOwns;

        /// <summary>How many switches to windowed mode were skipped.</summary>
        public long BlockedCount => _blocked;

        /// <summary>Records who would own an Escape now. Call once a frame.</summary>
        /// <param name="frame">Unity's frame number.</param>
        /// <param name="modWindowOwns">True when a mod window would take it (<see cref="EscapeDecision.ModWindowOwns"/>).</param>
        public void Observe(int frame, bool modWindowOwns)
        {
            _observedFrame = frame;
            _observedOwns = modWindowOwns;
        }

        /// <summary>True when the game's switch to windowed mode must be skipped. Counts each skip.</summary>
        /// <param name="frame">Unity's frame number.</param>
        /// <param name="toWindowed">The game asked for windowed mode.</param>
        /// <param name="escapeDown">Escape went down this frame.</param>
        public bool ShouldBlockWindowedSwitch(int frame, bool toWindowed, bool escapeDown)
        {
            if (!toWindowed || !escapeDown || !_observedOwns || _observedFrame == NoFrame)
            {
                return false;
            }

            if (_observedFrame > frame || (long)frame - _observedFrame > 1L)
            {
                return false;
            }

            _blocked++;
            return true;
        }

        /// <summary>Forgets the observation. For tests.</summary>
        public void Reset()
        {
            _observedFrame = NoFrame;
            _observedOwns = false;
        }
    }
}
