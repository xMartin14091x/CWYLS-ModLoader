using System;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// Decides which row a voice clip that just started belongs to: the row the scenario reader is
    /// reading, or the reverse voice-name index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework used to answer this with the voice-name index alone, which is wrong twice
    /// over: two master-data rows may name the same clip (last writer wins), and the index is
    /// rebuilt on a slow tick, so a row written moments ago is not in it yet. The game itself never
    /// has that problem - <c>ScenarioReader.CommandText</c> and friends already hold the exact
    /// <c>NovelData</c> and look their <c>LocalizationData</c> up by id
    /// - so the adapter captures that row in a prefix
    /// and this class decides whether it may be used.
    /// </para>
    /// <para>
    /// The gate is the voice name. Between the reader's command and the clip actually starting
    /// there is a deliberate one-frame gap (<c>HeroineVoiceController</c> awaits
    /// <c>UniTask.NextFrame</c>), and any other clip may start in that window - a mod's
    /// <c>VoiceManager.Play</c>, an idle line, a clip a mod generated. Requiring the captured row to name the
    /// very clip that started makes those cases fall back to the index automatically instead of
    /// stealing the scenario's id.
    /// </para>
    /// </remarks>
    public static class SpeechAttribution
    {
        /// <summary>
        /// Which source should name the row behind <paramref name="playingVoice"/>.
        /// </summary>
        /// <param name="hint">What the scenario reader last said it was reading; <see cref="ScenarioRowHint.None"/> when nothing is.</param>
        /// <param name="playingVoice">The clip name the game was asked to play.</param>
        /// <returns>
        /// <see cref="LineAttribution.Row"/> when the captured row names this very clip, otherwise
        /// <see cref="LineAttribution.VoiceIndex"/>. It never returns
        /// <see cref="LineAttribution.None"/>: that is the caller's answer when the chosen source
        /// turns out to hold no row.
        /// </returns>
        public static string Decide(ScenarioRowHint hint, string? playingVoice)
        {
            if (!hint.IsSet)
            {
                return LineAttribution.VoiceIndex;
            }

            return VoiceMatches(hint.Voice, playingVoice)
                ? LineAttribution.Row
                : LineAttribution.VoiceIndex;
        }

        /// <summary>
        /// Whether a master-data row's voice column names the clip that started. Two empty values
        /// match: a text-only row read while no clip is playing is still that row.
        /// </summary>
        /// <param name="rowVoice">The <c>LocalizationData.Voice</c> column, possibly null or empty.</param>
        /// <param name="playingVoice">The clip name, possibly null or empty.</param>
        public static bool VoiceMatches(string? rowVoice, string? playingVoice)
        {
            var row = string.IsNullOrEmpty(rowVoice) ? null : rowVoice;
            var playing = string.IsNullOrEmpty(playingVoice) ? null : playingVoice;

            if (row is null && playing is null)
            {
                return true;
            }

            return row != null && playing != null && string.Equals(row, playing, StringComparison.Ordinal);
        }
    }
}
