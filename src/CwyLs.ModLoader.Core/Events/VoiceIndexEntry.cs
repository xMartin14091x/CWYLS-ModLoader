namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// One master-data row as the voice index sees it: the row id and the clip it names.
    /// </summary>
    /// <remarks>
    /// The two columns <see cref="VoiceIndexFingerprint"/> and <see cref="VoiceNameCollisions"/>
    /// need, lifted out of <c>Bulbul.MasterData.LocalizationData</c> so both can be tested without
    /// the game.
    /// </remarks>
    public readonly struct VoiceIndexEntry
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="id">The localization row id, e.g. <c>tutorial_01_001</c>.</param>
        /// <param name="voice">The voice clip name, or null/empty for a text-only row.</param>
        public VoiceIndexEntry(string? id, string? voice)
        {
            Id = id;
            Voice = voice;
        }

        /// <summary>The localization row id.</summary>
        public string? Id { get; }

        /// <summary>The voice clip name, or null/empty when the row is text only.</summary>
        public string? Voice { get; }

        /// <summary>True when this row names a clip.</summary>
        public bool HasVoice => !string.IsNullOrEmpty(Voice);
    }
}
