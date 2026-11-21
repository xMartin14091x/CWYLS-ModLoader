namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>
    /// One change Pack Maker made to a pack, as the editor's <c>MakerEditor.Changed</c> event hands it
    /// to whoever caches pack state (the catalog, DS-02). Plain data, no Unity.
    /// </summary>
    public sealed class MakerEditorChange
    {
        /// <summary><see cref="Kind"/> of a subtitle save or removal.</summary>
        public const string SubtitleKind = "subtitle";

        /// <summary><see cref="Kind"/> of a dub save or removal.</summary>
        public const string VoiceKind = "voice";

        /// <summary><see cref="SubtitleKind"/> or <see cref="VoiceKind"/>.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The pack written to.</summary>
        public string PackId { get; set; } = string.Empty;

        /// <summary>The canonical language code, e.g. <c>th</c>.</summary>
        public string Lang { get; set; } = string.Empty;

        /// <summary>The localisation id (subtitle) or the voice clip name (dub).</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>True when the entry or the dub was removed.</summary>
        public bool Removed { get; set; }

        /// <summary>True when the game already uses the change.</summary>
        public bool Live { get; set; }

        /// <summary>The pack-relative file written or removed, e.g. <c>data/subtitles/th.json</c>.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>The dub's length in seconds; null for a subtitle or a removal.</summary>
        public double? DurationSec { get; set; }

        /// <inheritdoc />
        public override string ToString() =>
            Kind + " " + PackId + "/" + Lang + "/" + Key + (Removed ? " removed" : " saved") + (Live ? " (live)" : string.Empty);
    }
}
