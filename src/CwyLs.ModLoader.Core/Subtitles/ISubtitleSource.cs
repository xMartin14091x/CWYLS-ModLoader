namespace CwyLs.ModLoader.Core.Subtitles
{
    /// <summary>
    /// A table of subtitle texts keyed by the game's localization id, in one or more languages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how text in a pack language reaches the game's own text box: the packs'
    /// <c>data/subtitles/&lt;code&gt;.json</c> tables and <c>Satone.Say</c>'s lines are each published
    /// as a source, <see cref="SubtitleSources.Register"/> puts them on the list, and the
    /// <c>LocalizationMasterWrapper.Get</c> seam asks them (through
    /// <c>CwyLs.ModLoader.Core.Language.LanguageText</c>) before it falls back to the game's own columns.
    /// </para>
    /// <para>
    /// Implementations must be safe to call from any thread and must not throw: the lookup runs
    /// inside the game's own text binding, for every localized string.
    /// </para>
    /// </remarks>
    public interface ISubtitleSource
    {
        /// <summary>
        /// Looks up one line.
        /// </summary>
        /// <param name="localizationId">
        /// The game's master-data row id, e.g. <c>Tutorial_1_001</c>. Never null or empty.
        /// </param>
        /// <param name="lang">
        /// A normalised language code (<see cref="SubtitleLanguages.Normalize"/>), e.g. <c>th</c>.
        /// </param>
        /// <param name="text">
        /// The translated line on success; <see cref="string.Empty"/> when this source has nothing
        /// for that id and language. Never null.
        /// </param>
        /// <returns>True when <paramref name="text"/> holds a usable line.</returns>
        bool TryGet(string localizationId, string lang, out string text);
    }
}
