namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// One row of <c>data/localization.json</c>: a line of text in every language the game ships,
    /// plus the voice clip that speaks it.
    /// <para>
    /// Mirrors <c>Bulbul.MasterData.LocalizationData</c>
    /// field for field, but as a
    /// plain type so that parsing and merging can be tested without the game. The adapter builds
    /// the real <c>LocalizationData</c> from this.
    /// </para>
    /// </summary>
    public sealed class LocalizationRow
    {
        /// <summary>Row id; the key the game's <c>LocalizationList</c> dictionary uses. Required.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Japanese text. The column every game row is authored in.</summary>
        public string Ja { get; init; } = string.Empty;

        /// <summary>English text.</summary>
        public string En { get; init; } = string.Empty;

        /// <summary>Simplified Chinese text.</summary>
        public string ZhHans { get; init; } = string.Empty;

        /// <summary>Traditional Chinese text.</summary>
        public string ZhHant { get; init; } = string.Empty;

        /// <summary>Brazilian Portuguese text.</summary>
        public string Pt { get; init; } = string.Empty;

        /// <summary>Korean text.</summary>
        public string Ko { get; init; } = string.Empty;

        /// <summary>Russian text.</summary>
        public string Ru { get; init; } = string.Empty;

        /// <summary>
        /// Voice clip name, e.g. <c>Voice_CWY_Test</c>. Empty means the line is text only.
        /// A name registered by <c>VoiceFeature</c> works here exactly like a shipped one.
        /// </summary>
        public string Voice { get; init; } = string.Empty;

        /// <summary><c>id</c>.</summary>
        public override string ToString() => Id;
    }
}
