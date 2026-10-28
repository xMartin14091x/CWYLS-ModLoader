namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>Where a <see cref="TextureRule"/> came from. Carried so a log line can say so.</summary>
    public enum TextureRuleSource
    {
        /// <summary>Derived from the folder layout <c>textures/&lt;renderer&gt;/&lt;slot&gt;.png</c>.</summary>
        Convention = 0,

        /// <summary>Declared explicitly in the pack's <c>textures/_rules.json</c>.</summary>
        RulesFile,

        /// <summary>Supplied at runtime by a mod through <c>TextureFeature.Set</c>, not by a pack file.</summary>
        Runtime,
    }
}
