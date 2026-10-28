namespace CwyLs.ModLoader.Core.Input
{
    /// <summary>
    /// Fires a hotkey at most once per frame. Pure, so the rule can be tested without an engine.
    /// <para>
    /// Every function is bound to two chords (an F-key and a Mac-friendly Ctrl+Shift chord) and
    /// both are polled in the same <c>Update</c>. Without this, a user who happens to satisfy both
    /// — or a second poll in the same frame after the host was rebuilt — would run the action
    /// twice. Release detection is BepInEx's job (<c>KeyboardShortcut.IsDown</c> is
    /// <c>GetKeyDown</c>); this only collapses the same frame.
    /// </para>
    /// </summary>
    public sealed class HotkeyDebounce
    {
        /// <summary>Frame number that means "nothing has fired yet".</summary>
        public const int NeverFiredFrame = int.MinValue;

        private int _lastFiredFrame = NeverFiredFrame;

        /// <summary>The frame this hotkey last fired on, or <see cref="NeverFiredFrame"/>.</summary>
        public int LastFiredFrame => _lastFiredFrame;

        /// <summary>
        /// Claims the right to run the action on <paramref name="frame"/>.
        /// </summary>
        /// <param name="frame">Unity's frame counter.</param>
        /// <returns>
        /// True the first time it is called for a given frame, false for any further call in the
        /// same frame. A frame number lower than the last one (a counter reset) is accepted, so a
        /// wrapped or restarted counter cannot deadlock the key.
        /// </returns>
        public bool TryFire(int frame)
        {
            if (_lastFiredFrame == frame)
            {
                return false;
            }

            _lastFiredFrame = frame;
            return true;
        }

        /// <summary>Forgets the last frame, so the next <see cref="TryFire"/> always succeeds.</summary>
        public void Reset() => _lastFiredFrame = NeverFiredFrame;
    }
}
