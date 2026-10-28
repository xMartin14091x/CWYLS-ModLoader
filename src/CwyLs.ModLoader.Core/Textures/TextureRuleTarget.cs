namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>What a <see cref="TextureRule"/> names.</summary>
    public enum TextureRuleTarget
    {
        /// <summary>
        /// A renderer's GameObject name, e.g. <c>Body</c>. Written on that renderer's material at
        /// <see cref="TextureRule.MaterialIndex"/>.
        /// </summary>
        Renderer = 0,

        /// <summary>
        /// A material asset's name, e.g. <c>mt_Tee_A</c>. Written on every loaded material with that
        /// name - the asset itself and any instance copied from it - so it reaches renderers no rule
        /// could name, including ones the game has not instantiated yet.
        /// <para>
        /// This is the form that can repaint a costume Satone is actually wearing:
        /// <c>Bulbul.CostumeChangeService</c> instantiates <c>Costume_Tee_001</c>, whose shirt
        /// renderer is called <c>Top2</c> and whose material <c>CostumeSkinChanger</c> reassigns
        /// from <c>CostumeSkinDB</c>. The renderer name is unreachable; the material name is not.
        /// </para>
        /// </summary>
        Material,
    }
}
