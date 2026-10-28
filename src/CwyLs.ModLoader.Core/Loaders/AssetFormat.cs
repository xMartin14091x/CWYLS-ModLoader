namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// File encoding as recognised by <see cref="FileKindDetector"/>.
    /// Only <see cref="Png"/>, <see cref="Wav"/> and <see cref="OggVorbis"/> can be loaded;
    /// <see cref="Jpeg"/> exists solely so a JPEG can be rejected by name instead of as
    /// "unrecognised header", which is the single most likely mistake in a hand-made pack.
    /// </summary>
    public enum AssetFormat
    {
        /// <summary>Header (or extension) matched nothing known.</summary>
        Unknown = 0,

        /// <summary>PNG image. Loadable.</summary>
        Png = 1,

        /// <summary>RIFF/WAVE audio. Loadable (16-bit PCM only, see <see cref="WavParser"/>).</summary>
        Wav = 2,

        /// <summary>Ogg container, assumed Vorbis. Loadable through the engine's decoder only.</summary>
        OggVorbis = 3,

        /// <summary>JPEG image. Recognised, never loaded.</summary>
        Jpeg = 4,
    }
}
