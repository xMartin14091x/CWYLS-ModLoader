namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// Which loader a voice file needs, decided from its extension alone.
    /// </summary>
    public enum VoiceLoaderKind
    {
        /// <summary>
        /// 16-bit PCM WAV, decoded in managed code by <c>AudioLoader.LoadWav</c>. Synchronous: the
        /// clip exists by the time the call returns.
        /// </summary>
        WavSync = 0,

        /// <summary>
        /// Ogg Vorbis, handed to the engine's own decoder by <c>AudioLoader.LoadAsync</c>. Takes
        /// several frames, so the registration happens in a callback.
        /// </summary>
        EngineAsync,
    }
}
