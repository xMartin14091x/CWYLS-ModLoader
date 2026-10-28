namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// Who a <see cref="VoiceEntry"/> came from, and therefore which registration wins when two
    /// of them claim the same name. Higher value wins; equal value means the later registration
    /// wins, which is what makes "a pack listed after another overrides it" fall out of the order
    /// the packs are loaded in.
    /// </summary>
    public enum VoicePriority
    {
        /// <summary>A clip shipped in a content pack's <c>audio/voice/</c> folder.</summary>
        Pack = 0,

        /// <summary>
        /// A clip registered at runtime by a mod - a line a mod generated, say. It
        /// outranks a pack so that a live voice is never silently replaced by a file on disk when
        /// the packs are rescanned.
        /// </summary>
        Runtime = 1,
    }
}
