namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>What <see cref="VoiceRegistry.Register"/> did with a registration.</summary>
    public enum VoiceRegistrationOutcome
    {
        /// <summary>The name was free and the entry now owns it.</summary>
        Added = 0,

        /// <summary>The entry took the name from a previous one, which is reported as the previous entry.</summary>
        Replaced,

        /// <summary>
        /// The registration was refused because the name is already held by an entry that outranks
        /// it - a runtime clip a pack tried to overwrite, or a pack clip a caller asked to keep.
        /// </summary>
        KeptExisting,

        /// <summary>The name does not satisfy <see cref="VoiceNameRules"/>; nothing was changed.</summary>
        Invalid,
    }
}
