namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>Which clip a voice lookup is answered with.</summary>
    public enum VoiceChoice
    {
        /// <summary>Nobody overrides the name: the game's own clip plays.</summary>
        Game = 0,

        /// <summary>A global pack clip (<c>audio/voice/&lt;Name&gt;.ogg</c>), any language.</summary>
        Pack,

        /// <summary>The language set of the language in force (<c>audio/voice/&lt;lang&gt;/&lt;Name&gt;.ogg</c>).</summary>
        Language,

        /// <summary>A clip a mod registered at runtime (<c>VoiceFeature.Register</c>).</summary>
        Runtime,
    }

    /// <summary>
    /// The precedence between the three kinds of override a voice name can have.
    /// </summary>
    /// <remarks>
    /// Most specific wins, and a mod's explicit choice wins over files on disk:
    /// <list type="number">
    /// <item>a runtime registration (a mod, e.g. a line a mod generated);</item>
    /// <item>the language set of the language in force, when its clip is loaded;</item>
    /// <item>a global pack clip;</item>
    /// <item>the game's own clip.</item>
    /// </list>
    /// A language clip that is still loading or failed is skipped for that one play - the line is
    /// never silent - which is why the language step needs the clip to be loaded, not merely planned.
    /// </remarks>
    public static class LanguageVoiceRules
    {
        /// <summary>Chooses who answers a lookup.</summary>
        /// <param name="registered">What the global registry holds for the name, or null.</param>
        /// <param name="language">What the language set of the language in force says.</param>
        public static VoiceChoice Choose(VoiceEntry? registered, LanguageVoiceLookup language)
        {
            if (registered != null && registered.Priority == VoicePriority.Runtime)
            {
                return VoiceChoice.Runtime;
            }

            if (language.HasClip)
            {
                return VoiceChoice.Language;
            }

            return registered != null ? VoiceChoice.Pack : VoiceChoice.Game;
        }

        /// <summary>
        /// True when the language set should be asked at all: a runtime registration answers
        /// before it, so asking would only produce a misleading fallback trace and a needless load.
        /// </summary>
        /// <param name="registered">What the global registry holds for the name, or null.</param>
        public static bool AsksLanguageSet(VoiceEntry? registered) =>
            registered is null || registered.Priority != VoicePriority.Runtime;
    }
}
