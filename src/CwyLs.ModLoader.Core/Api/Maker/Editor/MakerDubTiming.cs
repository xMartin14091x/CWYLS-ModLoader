using System;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>
    /// "A dub is never cut off" (DS-03 Must 7, 2026-11-07): the timing rules the adapter's dub hold
    /// (<c>MakerDubHold</c>) applies, pure so they are unit-tested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The hold.</b> When the game is about to move on from a line (<c>ScenarioReader.AutoNext</c>)
    /// while a clip that is not the game's own is still playing - a language set's take, a dub Pack
    /// Maker swapped in, a pack's global replacement, a mod's clip - it waits until that clip has
    /// played plus <see cref="ReadingTime.VoiceTailSeconds"/>. This is the same
    /// <c>clip + 0.3 s</c> part of the subtitle hold's rule (<c>max(2.5 s, characters / 15, clip + 0.3 s)</c>),
    /// applied in every language and to every row, voiced or wordless, because the next voiced row
    /// stops the voice and the end of a scenario closes the box. It matters where the game stops
    /// waiting for the voice by itself: the 30 s timeout of its <c>VoiceText</c> and
    /// <c>PomodoroVoiceText</c> rows.
    /// </para>
    /// <para>
    /// <b>Stopped clips.</b> When the game already reports the voice finished (the clip ended, or a
    /// click or a skip stopped it), at most the 0.3 s tail is added, so a clip stopped early never
    /// freezes the box for the rest of its length.
    /// </para>
    /// <para>
    /// <b>The game's own caps.</b> Two lines have a hard limit outside the scenario reader: the
    /// greeting at game start (20 s, then the story is ended) and the talk when a timer ends (15 s).
    /// While dubs can play those limits are multiplied by <see cref="MaxDubRatio"/>, the longest
    /// dub-to-original ratio seen in the wild; both scenarios still end by themselves first.
    /// </para>
    /// </remarks>
    public static class MakerDubTiming
    {
        /// <summary>The longest dub seen relative to the game's clip of the same line (creators' takes: median 1.48x, up to 6x).</summary>
        public const double MaxDubRatio = 6d;

        /// <summary>The <c>VoicePlayback</c> choice of the game's own clip (anything else is a replacement).</summary>
        public const string GameChoice = "Game";

        /// <summary>
        /// True when the clip that played is not the game's own: a language set's (<c>Language</c>,
        /// which includes Pack Maker's live swaps and its Play pin), a pack's global replacement
        /// (<c>Pack</c>) or a mod's (<c>Runtime</c>). Only those can outlast the game's own timing.
        /// </summary>
        /// <param name="choice">The <c>VoicePlayback</c> choice.</param>
        public static bool IsReplacement(string? choice) =>
            !string.IsNullOrEmpty(choice) && !string.Equals(choice, GameChoice, StringComparison.Ordinal);

        /// <summary>
        /// How long the game must still wait before moving on so a replacement clip plays out plus the
        /// tail: <c>clip + 0.3 s - time since it started</c> while it still plays, at most the tail once
        /// the game reports it finished, and 0 when it is over or has no usable length.
        /// </summary>
        /// <param name="sinceClipStarted">Seconds since the clip was handed to the player.</param>
        /// <param name="clipSeconds">The clip's length.</param>
        /// <param name="stillPlaying">True while the game reports the voice as not finished.</param>
        /// <returns>Seconds to wait; 0 for none.</returns>
        public static double HoldSeconds(double sinceClipStarted, double clipSeconds, bool stillPlaying)
        {
            if (!ReadingTime.IsUsableClip(clipSeconds) || double.IsNaN(sinceClipStarted) || double.IsInfinity(sinceClipStarted)
                || sinceClipStarted < 0d)
            {
                return 0d;
            }

            var left = clipSeconds + ReadingTime.VoiceTailSeconds - sinceClipStarted;
            if (left <= 0d)
            {
                return 0d;
            }

            return stillPlaying ? left : Math.Min(left, ReadingTime.VoiceTailSeconds);
        }

        /// <summary>
        /// The wait the game should use: its own, raised to <see cref="HoldSeconds"/> when that is longer.
        /// Never lowers the game's wait.
        /// </summary>
        /// <param name="gameWait">The game's wait, in seconds.</param>
        /// <param name="hold">What the clip still needs.</param>
        public static double Raise(double gameWait, double hold) =>
            double.IsNaN(hold) || hold <= gameWait ? gameWait : hold;

        /// <summary>
        /// One of the game's hard limits on a talk, scaled while dubs can play: <c>cap x</c>
        /// <see cref="MaxDubRatio"/>; unchanged otherwise or for a cap that is not positive.
        /// </summary>
        /// <param name="gameCapSeconds">The game's limit.</param>
        /// <param name="dubsInForce">True while a dub can play (a language set or a Pack Maker take is in force).</param>
        public static double ScaledCap(double gameCapSeconds, bool dubsInForce) =>
            dubsInForce && gameCapSeconds > 0d && !double.IsInfinity(gameCapSeconds) ? gameCapSeconds * MaxDubRatio : gameCapSeconds;

        /// <summary>The whole-second form of <see cref="ScaledCap(double, bool)"/>, for a cap the game passes as an integer.</summary>
        /// <param name="gameCapSeconds">The game's limit.</param>
        /// <param name="dubsInForce">True while a dub can play.</param>
        public static int ScaledCap(int gameCapSeconds, bool dubsInForce) =>
            dubsInForce && gameCapSeconds > 0 ? (int)Math.Min(int.MaxValue, gameCapSeconds * MaxDubRatio) : gameCapSeconds;
    }
}
