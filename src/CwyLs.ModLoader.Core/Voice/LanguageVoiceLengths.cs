using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// How a language set's clips compare in length with the game's own clips of the same name - the
    /// number that tells whether a translated take will change a scene's pacing.
    /// </summary>
    /// <remarks>
    /// The game waits for the clip that plays (story text rows wait for the text and the voice), so a
    /// longer take never gets cut; it makes the scene slower, and it shifts the few effects the
    /// game times by the clock (screen effects started with a fixed delay, the 20 s game-start cap).
    /// The window <see cref="ShorterRatio"/>..<see cref="LongerRatio"/> is the one the voice
    /// pipeline aims for outside cutscenes.
    /// </remarks>
    public sealed class LanguageVoiceLengths
    {
        /// <summary>A clip more than this many times the game's length counts as longer.</summary>
        public const double LongerRatio = 1.25d;

        /// <summary>A clip less than this many times the game's length counts as shorter.</summary>
        public const double ShorterRatio = 0.8d;

        /// <summary>How many of the largest overruns are kept for the log.</summary>
        public const int KeptOverruns = 10;

        private readonly List<KeyValuePair<string, double>> _overruns = new List<KeyValuePair<string, double>>();

        /// <summary>Clips compared (both lengths known and positive).</summary>
        public int Compared { get; private set; }

        /// <summary>Clips with no game clip of the same name, or an unreadable length.</summary>
        public int Unmatched { get; private set; }

        /// <summary>Clips longer than <see cref="LongerRatio"/> times the game's.</summary>
        public int Longer { get; private set; }

        /// <summary>Clips shorter than <see cref="ShorterRatio"/> times the game's.</summary>
        public int Shorter { get; private set; }

        /// <summary>Sum of the language clips' lengths, in seconds (compared clips only).</summary>
        public double LanguageSeconds { get; private set; }

        /// <summary>Sum of the game clips' lengths, in seconds (compared clips only).</summary>
        public double GameSeconds { get; private set; }

        /// <summary>The largest positive difference (language minus game), in seconds; 0 when none is longer.</summary>
        public double MaxOverrunSeconds { get; private set; }

        /// <summary>The voice name with <see cref="MaxOverrunSeconds"/>, or empty.</summary>
        public string MaxOverrunName { get; private set; } = string.Empty;

        /// <summary>The largest overruns, largest first, at most <see cref="KeptOverruns"/>.</summary>
        public IReadOnlyList<KeyValuePair<string, double>> TopOverruns => _overruns;

        /// <summary>Records one clip.</summary>
        /// <param name="name">The voice name.</param>
        /// <param name="languageSeconds">The language clip's length.</param>
        /// <param name="gameSeconds">The game clip's length; zero, negative or not finite when unknown.</param>
        public void Add(string? name, double languageSeconds, double gameSeconds)
        {
            if (!IsUsable(languageSeconds) || !IsUsable(gameSeconds))
            {
                Unmatched++;
                return;
            }

            Compared++;
            LanguageSeconds += languageSeconds;
            GameSeconds += gameSeconds;

            var ratio = languageSeconds / gameSeconds;
            if (ratio > LongerRatio)
            {
                Longer++;
            }
            else if (ratio < ShorterRatio)
            {
                Shorter++;
            }

            var over = languageSeconds - gameSeconds;
            if (over <= 0d)
            {
                return;
            }

            if (over > MaxOverrunSeconds)
            {
                MaxOverrunSeconds = over;
                MaxOverrunName = name ?? string.Empty;
            }

            _overruns.Add(new KeyValuePair<string, double>(name ?? string.Empty, over));
            _overruns.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (_overruns.Count > KeptOverruns)
            {
                _overruns.RemoveAt(_overruns.Count - 1);
            }
        }

        /// <summary>
        /// One phrase for a log line, e.g. <c>1356 compared: 41 longer than 1.25x, 3 shorter than 0.8x, max +1.9 s (Voice_X)</c>.
        /// </summary>
        public string Summarize()
        {
            if (Compared == 0)
            {
                return Unmatched == 0
                    ? "no clips compared"
                    : Unmatched.ToString(CultureInfo.InvariantCulture) + " clip(s) without a game clip to compare";
            }

            var text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} compared: {1} longer than {2}x, {3} shorter than {4}x, total {5:0.0} s vs {6:0.0} s",
                Compared,
                Longer,
                LongerRatio,
                Shorter,
                ShorterRatio,
                LanguageSeconds,
                GameSeconds);

            if (MaxOverrunSeconds > 0d)
            {
                text += string.Format(
                    CultureInfo.InvariantCulture, ", max +{0:0.0} s ({1})", MaxOverrunSeconds, MaxOverrunName);
            }

            return text;
        }

        private static bool IsUsable(double seconds) =>
            seconds > 0d && !double.IsNaN(seconds) && !double.IsInfinity(seconds);
    }
}
