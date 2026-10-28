using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// Serves a <c>Satone.Say</c> line's own translations to the game's text box.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Say line has a real localization row in the game's master data, whose seven columns hold
    /// the line in the caller's language. When the game is in a pack language (Thai, say), the text
    /// seam asks every registered <see cref="ISubtitleSource"/> for that row in that language before
    /// falling back along the language's chain - and this source is what turns the row id back into
    /// the caller's own text for that language (<c>SayRequest.Texts</c>), so the line appears in the
    /// game's own box, in the chosen language, like any story line.
    /// </para>
    /// <para>
    /// Registered once from <see cref="SatoneSayFeature.Initialize"/>. Sources are asked in
    /// registration order and the ids here are all <c>cwy_say_*</c>, so this can never shadow a
    /// content pack's table.
    /// </para>
    /// </remarks>
    internal sealed class SatoneSaySubtitles : ISubtitleSource
    {
        /// <inheritdoc />
        public bool TryGet(string localizationId, string lang, out string text) =>
            SatoneSayFeature.TryText(localizationId, lang, out text);
    }
}
