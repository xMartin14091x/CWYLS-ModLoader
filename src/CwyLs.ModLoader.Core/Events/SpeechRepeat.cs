using System;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// The guard that keeps the contract "one <see cref="LineSpoken"/> per line spoken": whether a
    /// clip that just started is the same master-data line the framework published a moment ago.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is deliberately narrow. Two <em>different</em> lines that happen to share a clip name are
    /// not a repeat - the game's smoke suite plays the same test clip from two checks in one frame,
    /// once directly through <c>VoiceManager.Play</c> and once through a scenario, and both of
    /// those are real lines that a subscriber must see. So the rule requires the resolved
    /// localization id to be present and identical, not merely the clip name.
    /// </para>
    /// <para>
    /// With attribution resolved from the scenario row, those two smoke plays now carry different
    /// ids and this rule correctly lets both through. What it still catches is a genuine
    /// re-entrant publish of one and the same row: same clip, same row, inside
    /// <see cref="DefaultWindowSeconds"/>. The game cannot produce that on its own - a voice
    /// command awaits <c>IsFinishedVoice</c> before the next one
    /// (<c>ScenarioReader</c>) - so a hit here is a defect and the caller logs it.
    /// </para>
    /// </remarks>
    public static class SpeechRepeat
    {
        /// <summary>
        /// How close two publishes of the same row must be to count as one line. 250 ms is far
        /// longer than the one-frame gap a re-entrant publish would show and far shorter than the
        /// shortest real clip in the game's data.
        /// </summary>
        public const double DefaultWindowSeconds = 0.25d;

        /// <summary>
        /// Whether the line starting now is a republish of the previous one.
        /// </summary>
        /// <param name="previousVoice">Clip name of the last published line, or null if there is none.</param>
        /// <param name="previousLocalizationId">Resolved row id of the last published line, or null.</param>
        /// <param name="previousAtSeconds">When the last line was published, in the caller's clock.</param>
        /// <param name="voice">Clip name starting now.</param>
        /// <param name="localizationId">Resolved row id of the line starting now, or null when unresolved.</param>
        /// <param name="atSeconds">Now, in the same clock.</param>
        /// <param name="windowSeconds">
        /// The window; zero or negative turns the guard off entirely, which is the honest way to
        /// disable it rather than picking a tiny number.
        /// </param>
        /// <returns>
        /// True only when both lines name the same clip, both resolved the same non-empty row id,
        /// and the second is inside the window and not before the first.
        /// </returns>
        public static bool IsRepeat(
            string? previousVoice,
            string? previousLocalizationId,
            double previousAtSeconds,
            string? voice,
            string? localizationId,
            double atSeconds,
            double windowSeconds = DefaultWindowSeconds)
        {
            if (windowSeconds <= 0d)
            {
                return false;
            }

            // An unresolved line says nothing about which row it is, so it can never be proven to
            // be a repeat of anything.
            if (string.IsNullOrEmpty(previousLocalizationId) || string.IsNullOrEmpty(localizationId))
            {
                return false;
            }

            if (string.IsNullOrEmpty(previousVoice) || string.IsNullOrEmpty(voice))
            {
                return false;
            }

            if (!string.Equals(previousLocalizationId, localizationId, StringComparison.Ordinal)
                || !string.Equals(previousVoice, voice, StringComparison.Ordinal))
            {
                return false;
            }

            var elapsed = atSeconds - previousAtSeconds;
            return elapsed >= 0d && elapsed <= windowSeconds;
        }
    }
}
