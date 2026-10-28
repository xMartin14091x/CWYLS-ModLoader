using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Subtitles
{
    /// <summary>
    /// How long a line the framework puts in the game's text box must stay readable:
    /// <c>max(2.5 s, characters / 15 per second)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 15 characters per second is a common subtitling reading speed; 2.5 s keeps a one-word line
    /// from flashing past. "Characters" means what a reader sees: combining marks (Thai vowels and
    /// tone marks above and below a letter, accents), zero-width and control characters, and
    /// TextMeshPro rich-text tags such as <c>&lt;color=#fff&gt;</c> do not count; a surrogate pair
    /// counts once; spaces count.
    /// </para>
    /// <para>
    /// Pure and clock-free: the adapter passes the time a line reached the box and the time now,
    /// on the same clock, and gets back how much longer to hold it.
    /// </para>
    /// <para>
    /// Voiced lines: when the line has a voice clip, the box also stays up until that clip has played
    /// plus <see cref="VoiceTailSeconds"/>, measured from when the clip started - so the rule is
    /// <c>max(2.5 s, characters / 15, clip length + 0.3 s)</c>. A clip "belongs" to the line when it
    /// started between <see cref="ClipLeadSeconds"/> before and <see cref="ClipLagSeconds"/> after
    /// the text reached the box (<see cref="ClipBelongsToLine"/>); the game asks for the clip a
    /// frame after it shows the text, so an older clip is the previous line's.
    /// </para>
    /// </remarks>
    public static class ReadingTime
    {
        /// <summary>The shortest time any line stays up, in seconds.</summary>
        public const double MinimumSeconds = 2.5d;

        /// <summary>Reading speed, in visible characters per second.</summary>
        public const double CharactersPerSecond = 15d;

        /// <summary>How long the text stays up after its voice clip ends, in seconds.</summary>
        public const double VoiceTailSeconds = 0.3d;

        /// <summary>How long before the text a clip may start and still belong to the line, in seconds.</summary>
        public const double ClipLeadSeconds = 0.5d;

        /// <summary>How long after the text a clip may start and still belong to the line, in seconds.</summary>
        public const double ClipLagSeconds = 2d;

        /// <summary>The longest clip the hold follows, in seconds; a longer or broken length is ignored.</summary>
        public const double MaxClipSeconds = 120d;

        /// <summary>The longest rich-text tag skipped when counting; anything longer is text.</summary>
        private const int MaxTagLength = 64;

        /// <summary>How many characters a reader sees in <paramref name="text"/>.</summary>
        /// <param name="text">The line as it is displayed; null counts as zero.</param>
        public static int CountVisible(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < text!.Length; i++)
            {
                var c = text[i];

                if (c == '<')
                {
                    var close = text.IndexOf('>', i + 1);
                    if (close > i + 1 && close - i <= MaxTagLength && text.IndexOf('<', i + 1, close - i - 1) < 0)
                    {
                        i = close;
                        continue;
                    }
                }

                if (char.IsLowSurrogate(c))
                {
                    continue;
                }

                switch (CharUnicodeInfo.GetUnicodeCategory(c))
                {
                    case UnicodeCategory.NonSpacingMark:
                    case UnicodeCategory.EnclosingMark:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Control:
                        continue;
                }

                count++;
            }

            return count;
        }

        /// <summary>The minimum time <paramref name="text"/> stays readable, in seconds.</summary>
        /// <param name="text">The line as it is displayed.</param>
        public static double MinimumFor(string? text) =>
            Math.Max(MinimumSeconds, CountVisible(text) / CharactersPerSecond);

        /// <summary>
        /// How much longer a line must stay up, given when it reached the box and what time it is.
        /// </summary>
        /// <param name="text">The line as it is displayed.</param>
        /// <param name="shownAtSeconds">When the line reached the box.</param>
        /// <param name="nowSeconds">The current time, on the same clock.</param>
        /// <returns>Seconds still to wait; 0 when the line has already been up long enough.</returns>
        public static double RemainingFor(string? text, double shownAtSeconds, double nowSeconds)
        {
            var elapsed = nowSeconds - shownAtSeconds;
            if (double.IsNaN(elapsed) || elapsed < 0d)
            {
                elapsed = 0d;
            }

            var remaining = MinimumFor(text) - elapsed;
            return remaining > 0d ? remaining : 0d;
        }

        /// <summary>
        /// The minimum time a voiced line stays up, measured from the text:
        /// <c>max(2.5 s, characters / 15, clip length + 0.3 s)</c>.
        /// </summary>
        /// <param name="text">The line as it is displayed.</param>
        /// <param name="clipSeconds">The playing clip's length; zero or an unusable value means no clip.</param>
        public static double MinimumFor(string? text, double clipSeconds)
        {
            var reading = MinimumFor(text);
            return IsUsableClip(clipSeconds) ? Math.Max(reading, clipSeconds + VoiceTailSeconds) : reading;
        }

        /// <summary>
        /// True when a clip that started at <paramref name="clipStartedAtSeconds"/> is the voice of the
        /// line that reached the box at <paramref name="shownAtSeconds"/> (same clock).
        /// </summary>
        /// <param name="shownAtSeconds">When the line reached the box.</param>
        /// <param name="clipStartedAtSeconds">When the clip was handed to the player.</param>
        public static bool ClipBelongsToLine(double shownAtSeconds, double clipStartedAtSeconds)
        {
            if (double.IsNaN(shownAtSeconds) || double.IsNaN(clipStartedAtSeconds)
                || double.IsInfinity(shownAtSeconds) || double.IsInfinity(clipStartedAtSeconds))
            {
                return false;
            }

            var offset = clipStartedAtSeconds - shownAtSeconds;
            return offset >= -ClipLeadSeconds && offset <= ClipLagSeconds;
        }

        /// <summary>
        /// How much longer a voiced line must stay up: until both its reading time (from the text) and
        /// its clip plus <see cref="VoiceTailSeconds"/> (from the clip's start) have passed. A clip that
        /// does not belong to the line (<see cref="ClipBelongsToLine"/>) or has no usable length is
        /// ignored, which leaves exactly <see cref="RemainingFor(string, double, double)"/>.
        /// </summary>
        /// <param name="text">The line as it is displayed.</param>
        /// <param name="shownAtSeconds">When the line reached the box.</param>
        /// <param name="nowSeconds">The current time, on the same clock.</param>
        /// <param name="clipStartedAtSeconds">When the clip was handed to the player, on the same clock.</param>
        /// <param name="clipSeconds">The clip's length.</param>
        /// <returns>Seconds still to wait; 0 when both have passed.</returns>
        public static double RemainingFor(
            string? text, double shownAtSeconds, double nowSeconds, double clipStartedAtSeconds, double clipSeconds)
        {
            var reading = RemainingFor(text, shownAtSeconds, nowSeconds);
            if (!IsUsableClip(clipSeconds) || !ClipBelongsToLine(shownAtSeconds, clipStartedAtSeconds))
            {
                return reading;
            }

            var voice = clipStartedAtSeconds + clipSeconds + VoiceTailSeconds - nowSeconds;
            if (double.IsNaN(voice) || voice < 0d)
            {
                voice = 0d;
            }

            return Math.Max(reading, voice);
        }

        /// <summary>True when a clip length is one the hold follows.</summary>
        /// <param name="clipSeconds">The length, in seconds.</param>
        public static bool IsUsableClip(double clipSeconds) =>
            clipSeconds > 0d && clipSeconds <= MaxClipSeconds && !double.IsNaN(clipSeconds);
    }
}
