using System;
using System.Diagnostics;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// The last voice clip the game's <c>VoiceManager</c> was handed to play - whichever
    /// clip that was (the game's, a pack's, a language set's or a mod's) - with its length and when.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recorded by the <c>GetAudioClip</c> postfix (<see cref="VoicePatches"/>), which sees the final
    /// answer after every prefix, so it is the clip that really plays. The subtitle hold reads it to
    /// keep a voiced line in the box until its clip has played (<c>Subtitles/SubtitleHold</c>).
    /// </para>
    /// <para>
    /// Main thread only (the game plays voices from its main thread). A smoke check that calls the
    /// lookup through reflection wraps itself in <see cref="Suppress"/>, so a probe is never mistaken
    /// for a line.
    /// </para>
    /// </remarks>
    internal static class VoicePlayback
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private static string _name = string.Empty;
        private static string _choice = string.Empty;
        private static double _lengthSeconds;
        private static double _at = double.NegativeInfinity;
        private static int _suppressed;

        /// <summary>How many clips were recorded this session.</summary>
        internal static long Recorded { get; private set; }

        /// <summary>True while a probe is running; nothing is recorded then.</summary>
        internal static bool IsSuppressed => _suppressed > 0;

        /// <summary>Records a clip the voice manager is about to play.</summary>
        /// <param name="name">The voice name.</param>
        /// <param name="lengthSeconds">The clip's length.</param>
        /// <param name="choice">Who answered: <c>Game</c>, <c>Pack</c>, <c>Language</c> or <c>Runtime</c>.</param>
        internal static void Record(string name, double lengthSeconds, string choice)
        {
            if (_suppressed > 0)
            {
                return;
            }

            _name = name ?? string.Empty;
            _choice = choice ?? string.Empty;
            _lengthSeconds = lengthSeconds;
            _at = Clock.Elapsed.TotalSeconds;
            Recorded++;
        }

        /// <summary>The last recorded clip.</summary>
        /// <param name="name">Its voice name.</param>
        /// <param name="lengthSeconds">Its length.</param>
        /// <param name="secondsAgo">How long ago it was handed to the player.</param>
        /// <param name="choice">Who answered the lookup.</param>
        /// <returns>False before any clip was recorded.</returns>
        internal static bool TryLast(out string name, out double lengthSeconds, out double secondsAgo, out string choice)
        {
            name = _name;
            choice = _choice;
            lengthSeconds = _lengthSeconds;

            if (double.IsNegativeInfinity(_at))
            {
                secondsAgo = double.PositiveInfinity;
                return false;
            }

            secondsAgo = Clock.Elapsed.TotalSeconds - _at;
            return true;
        }

        /// <summary>Stops recording until the handle is disposed. Nests.</summary>
        /// <returns>Dispose to record again.</returns>
        internal static IDisposable Suppress()
        {
            _suppressed++;
            return new Release();
        }

        private sealed class Release : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done)
                {
                    return;
                }

                _done = true;
                _suppressed = Math.Max(0, _suppressed - 1);
            }
        }
    }
}
