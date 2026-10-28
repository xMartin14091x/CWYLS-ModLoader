using CwyLs.ModLoader.Bulbul.Language;

namespace CwyLs.ModLoader.Bulbul.Subtitles
{
    /// <summary>
    /// Subtitles, as a mod sees them: the game's own text box shows every line, in the language
    /// the player chose - one of the game's own, or one a content pack declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thin, stable facade over <see cref="SubtitleFeature"/>. There is no overlay panel: to put a
    /// line in front of the player, make Satone say it (<c>Satone.Say</c>, which goes through the
    /// game's own scenario and text box and gets the reading-time hold); to translate the game's own
    /// lines, ship a pack with a <c>language.json</c> and a <c>data/subtitles/&lt;code&gt;.json</c>.
    /// </para>
    /// <code>
    /// using CwyLs.ModLoader.Bulbul.Subtitles;
    ///
    /// var lang = Subtitles.Language;   // "th" while Thai is chosen, "en", "ja", ...
    /// </code>
    /// </remarks>
    public static class Subtitles
    {
        /// <summary>
        /// True when the framework holds its lines for reading time and mirrors them to the console;
        /// the player's setting is <c>Subtitles.Enabled</c> in the adapter's .cfg.
        /// </summary>
        public static bool Enabled
        {
            get => SubtitleFeature.Enabled;
            set => SubtitleFeature.Enabled = value;
        }

        /// <summary>
        /// The code of the language the game is displaying: a pack language (<c>th</c>) or one of the
        /// game's own (<c>en</c>, <c>ja</c>, ...). Empty before the save has loaded. To change it, the
        /// player uses the game's language selector (or a mod calls <c>LanguageFeature.Select</c>).
        /// </summary>
        public static string Language => LanguageFeature.CurrentCode;

        /// <summary>True once the feature has started up.</summary>
        public static bool IsReady => SubtitleFeature.IsReady;

        /// <summary>
        /// Retired with the overlay panel; always returns false, draws nothing and writes
        /// one trace-file line. Nothing in the framework calls it; it is kept so a mod written
        /// against the old API still compiles. Use <c>Satone.Say</c> to show a line in the game's
        /// text box.
        /// </summary>
        /// <param name="text">The text a caller wanted shown.</param>
        /// <param name="seconds">Ignored.</param>
        /// <param name="lang">The language the caller named, for the trace.</param>
        /// <returns>False.</returns>
        public static bool Show(string? text, double seconds = 0d, string? lang = null) =>
            SubtitleFeature.Show(text, seconds, lang);
    }
}
