using System;
using System.Globalization;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// What actually happened to one <c>Satone.Say</c> line, as the event bus and the game's text
    /// seam reported it: the clip really started, the text the game had for it, how long it ran,
    /// and - while a pack language was active - whether the seam found the line's own translation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A smoke check cannot answer "did she say it?" in the frame it asks - the clip starts a
    /// frame later and runs for seconds. So the request is recorded here and the verdict is read
    /// back by the next pass, the same way the Satone motion checks report
    /// <c>SatoneMotionHost.LastObservation</c>.
    /// </para>
    /// <para>
    /// Text is shown only by the game's own story box, and only on the <see cref="ViaScenario"/>
    /// path. The line's row holds the caller's text in every game column, so in one of the game's
    /// own languages nothing is asked. While a pack language is active the text seam
    /// (<c>LocalizationMasterWrapper.Get</c>) asks the registered text tables, including this line's
    /// translations (<c>SatoneSaySubtitles</c>); the <c>Subtitle*</c> members record those asks.
    /// </para>
    /// </remarks>
    public sealed class SayObservation
    {
        /// <summary><see cref="Via"/> for a line the game's scenario reader played, in its text box.</summary>
        public const string ViaScenario = "scenario";

        /// <summary><see cref="Via"/> for a line spoken the simple way: voice only, no text box.</summary>
        public const string ViaFallback = "fallback";

        private readonly object _gate = new object();

        /// <summary>Starts watching a line.</summary>
        /// <param name="line">The line that was just handed to the game. Required.</param>
        /// <param name="via">How it was performed: <see cref="ViaScenario"/> or <see cref="ViaFallback"/>.</param>
        /// <param name="startedAt">Seconds on the host's clock.</param>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        public SayObservation(SayLine line, string via, double startedAt)
        {
            Line = line ?? throw new ArgumentNullException(nameof(line));
            Via = via ?? string.Empty;
            StartedAt = startedAt;
        }

        /// <summary>The line being watched.</summary>
        public SayLine Line { get; }

        /// <summary>How it was performed: <see cref="ViaScenario"/> or <see cref="ViaFallback"/>.</summary>
        public string Via { get; }

        /// <summary>Seconds on the host's clock when the request went to the game.</summary>
        public double StartedAt { get; }

        /// <summary>True once the game's voice path published a <see cref="LineSpoken"/> for it.</summary>
        public bool VoiceStarted { get; private set; }

        /// <summary>The text the game had for the line when the clip started, or null.</summary>
        public string? SpokenText { get; private set; }

        /// <summary>The language the game was displaying, or empty.</summary>
        public string SpokenLanguage { get; private set; } = string.Empty;

        /// <summary>How long the clip ran, in seconds; 0 until it has ended.</summary>
        public double SpokenSeconds { get; private set; }

        /// <summary>True once the clip has stopped.</summary>
        public bool VoiceEnded { get; private set; }

        /// <summary>
        /// The pack language the text seam last asked this line's translations for, or empty when
        /// it never asked (the game was in one of its own languages).
        /// </summary>
        public string SubtitleLanguage { get; private set; } = string.Empty;

        /// <summary>True when that ask was answered with the caller's own translation.</summary>
        public bool SubtitleFound { get; private set; }

        /// <summary>How many times the text seam has asked for this line's translation.</summary>
        public int SubtitleAsks { get; private set; }

        /// <summary>
        /// Records that the game started a clip. Ignores anything that is not this line.
        /// </summary>
        /// <param name="spoken">The bus event.</param>
        /// <param name="nowSeconds">Seconds on the host's clock.</param>
        public void NoteSpoken(LineSpoken spoken, double nowSeconds)
        {
            if (spoken is null || !Owns(spoken.LocalizationId, spoken.VoiceName))
            {
                return;
            }

            lock (_gate)
            {
                VoiceStarted = true;
                SpokenText = spoken.Text;
                SpokenLanguage = spoken.Language ?? string.Empty;
                SpokenSeconds = spoken.DurationSec;
                _ = nowSeconds;
            }
        }

        /// <summary>Records that the clip stopped.</summary>
        /// <param name="ended">The bus event.</param>
        /// <param name="nowSeconds">Seconds on the host's clock.</param>
        public void NoteEnded(LineEnded ended, double nowSeconds)
        {
            if (ended is null || !Line.HasVoice
                || !string.Equals(ended.VoiceName, Line.VoiceName, StringComparison.Ordinal))
            {
                return;
            }

            lock (_gate)
            {
                VoiceEnded = true;
                SpokenSeconds = ended.DurationSec > 0d ? ended.DurationSec : Math.Max(0d, nowSeconds - StartedAt);
            }
        }

        /// <summary>
        /// Records that the game's text seam asked for this line in a pack language (it asks only
        /// while one is active). Ignores any other id.
        /// </summary>
        /// <param name="localizationId">The id it asked about.</param>
        /// <param name="lang">The pack language code it wanted.</param>
        /// <param name="found">Whether the line carries a translation for it.</param>
        public void NoteSubtitleAsk(string localizationId, string lang, bool found)
        {
            if (!string.Equals(localizationId, Line.LocalizationId, StringComparison.Ordinal))
            {
                return;
            }

            lock (_gate)
            {
                SubtitleAsks++;
                SubtitleLanguage = SubtitleLanguages.Normalize(lang);
                SubtitleFound = found;
            }
        }

        /// <summary>
        /// True when the clip started and, if the text seam asked for a pack language, the line's
        /// own translation answered. A line with no translation for the active pack language is
        /// unconfirmed: its text box fell back along the language's chain instead.
        /// </summary>
        public bool IsConfirmed => VoiceStarted && (SubtitleAsks == 0 || SubtitleFound);

        private bool Owns(string? localizationId, string? voiceName) =>
            string.Equals(localizationId, Line.LocalizationId, StringComparison.Ordinal)
            || (Line.HasVoice && string.Equals(voiceName, Line.VoiceName, StringComparison.Ordinal));

        /// <summary>A one-line verdict for a smoke detail or a log.</summary>
        public override string ToString()
        {
            var voice = VoiceStarted
                ? "voice started (" + SpokenSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                    + (VoiceEnded ? ", ended" : ", still playing") + ")"
                : "voice never started";

            var subtitle = string.Equals(Via, ViaFallback, StringComparison.Ordinal)
                ? "no text box on the fallback path"
                : SubtitleAsks == 0
                    ? "no pack-language lookup (the text box read the row's own text)"
                    : "the text seam asked for [" + SubtitleLanguage + "] and "
                        + (SubtitleFound ? "got the line's translation" : "found none (the box fell back along the chain)");

            return "#" + Line.Id.ToString(CultureInfo.InvariantCulture) + " via " + Via
                + ": " + voice + "; game text " + Quote(SpokenText)
                + " [" + (SpokenLanguage.Length == 0 ? "?" : SpokenLanguage) + "]; " + subtitle;
        }

        private static string Quote(string? text) =>
            string.IsNullOrEmpty(text) ? "(none)" : "\"" + text + "\"";
    }
}
