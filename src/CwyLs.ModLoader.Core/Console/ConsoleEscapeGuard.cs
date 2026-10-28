namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>Who an Escape press belongs to while the console may be on screen.</summary>
    public enum EscapeOwner
    {
        /// <summary>The game: it handles Escape as it always does.</summary>
        Game,

        /// <summary>The CWY console: Escape closes it (or cancels a composition in its input).</summary>
        Console
    }

    /// <summary>
    /// Keeps the game from acting on an Escape the CWY console already used. The game reads Escape
    /// itself every frame and switches a fullscreen window to windowed mode
    /// (<c>RoomGameManager.Update</c> → <c>SettingService.ChangeWindowMode(Window)</c>); with the
    /// console open, the same press must only close the console.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity does not order two components' <c>Update</c> calls, so the game may read Escape
    /// before or after the console handles it in the same frame. Both orders are covered:
    /// </para>
    /// <list type="bullet">
    /// <item>The console ran first: it called <see cref="Claim"/> for this frame before closing.</item>
    /// <item>The game ran first: the console is still open, and its latest word on who owns Escape
    /// (<see cref="Observe"/>, made every frame it is on screen) says the console, and that word is
    /// from this frame or the one before.</item>
    /// </list>
    /// <para>
    /// Only a switch <em>to windowed</em> in a frame where Escape went down is ever blocked, so the
    /// settings window's own Window button (a click) is never affected. Main thread only; the
    /// state is two frame numbers.
    /// </para>
    /// </remarks>
    public sealed class ConsoleEscapeGuard
    {
        /// <summary>Frame number meaning "never".</summary>
        public const int NoFrame = int.MinValue;

        private int _observedFrame = NoFrame;
        private EscapeOwner _observedOwner = EscapeOwner.Game;
        private int _claimedFrame = NoFrame;
        private long _blocked;

        /// <summary>The frame of the latest <see cref="Observe"/>, or <see cref="NoFrame"/>.</summary>
        public int ObservedFrame => _observedFrame;

        /// <summary>The owner the latest <see cref="Observe"/> reported.</summary>
        public EscapeOwner ObservedOwner => _observedOwner;

        /// <summary>The frame of the latest <see cref="Claim"/>, or <see cref="NoFrame"/>.</summary>
        public int ClaimedFrame => _claimedFrame;

        /// <summary>How many window switches <see cref="ShouldBlockWindowedSwitch"/> has blocked.</summary>
        public long BlockedCount => _blocked;

        /// <summary>
        /// Who owns Escape right now, by the console's own key routing
        /// (<see cref="ConsoleKeyRouter.Route"/>): the console when it is on screen, interactable,
        /// and Escape would close it; also the console while its own input is composing (Escape
        /// then cancels the composition inside the console). The game otherwise - including while
        /// the player types into one of the game's own text fields.
        /// </summary>
        /// <param name="consoleOpen">The console is open.</param>
        /// <param name="interactable">The console takes input (not faded out with the game's hidden UI).</param>
        /// <param name="focus">Who has the keyboard.</param>
        public static EscapeOwner OwnerOf(bool consoleOpen, bool interactable, ConsoleFocus focus)
        {
            if (!consoleOpen || !interactable)
            {
                return EscapeOwner.Game;
            }

            if (focus.Composing && focus.InputSelected)
            {
                return EscapeOwner.Console;
            }

            return ConsoleKeyRouter.Route(ConsoleKeyPress.Escape, focus) == ConsoleKeyAction.Close
                ? EscapeOwner.Console
                : EscapeOwner.Game;
        }

        /// <summary>Records who owns Escape this frame. Call once a frame while the console is on screen.</summary>
        /// <param name="frame">Unity's frame number.</param>
        /// <param name="owner">The owner (<see cref="OwnerOf"/>).</param>
        public void Observe(int frame, EscapeOwner owner)
        {
            _observedFrame = frame;
            _observedOwner = owner;
        }

        /// <summary>Records that the console used this frame's Escape (it is about to close).</summary>
        /// <param name="frame">Unity's frame number.</param>
        public void Claim(int frame) => _claimedFrame = frame;

        /// <summary>
        /// True when the game's switch to windowed mode must be skipped because the console used
        /// this frame's Escape. Counts every block in <see cref="BlockedCount"/>.
        /// </summary>
        /// <param name="frame">Unity's frame number.</param>
        /// <param name="toWindowed">The game asked for windowed mode.</param>
        /// <param name="escapeDown">Escape went down this frame.</param>
        /// <param name="consoleOpen">The console is open right now.</param>
        public bool ShouldBlockWindowedSwitch(int frame, bool toWindowed, bool escapeDown, bool consoleOpen)
        {
            if (!Decide(frame, toWindowed, escapeDown, consoleOpen))
            {
                return false;
            }

            _blocked++;
            return true;
        }

        /// <summary>Forgets every observation and claim. For tests and a console rebuild.</summary>
        public void Reset()
        {
            _observedFrame = NoFrame;
            _observedOwner = EscapeOwner.Game;
            _claimedFrame = NoFrame;
        }

        private bool Decide(int frame, bool toWindowed, bool escapeDown, bool consoleOpen)
        {
            if (!toWindowed || !escapeDown)
            {
                return false;
            }

            // The console's Update ran first this frame and closed on this very Escape.
            if (_claimedFrame != NoFrame && _claimedFrame == frame)
            {
                return true;
            }

            // The game's Update ran first: the console has not seen this Escape yet. Its latest
            // observation decides, but only a fresh one (this frame or the previous): a stale one
            // means the console has not been on screen since.
            return consoleOpen
                && _observedOwner == EscapeOwner.Console
                && _observedFrame != NoFrame
                && _observedFrame <= frame
                && (long)frame - _observedFrame <= 1L;
        }
    }
}
