namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// Which material a rule writes.
    /// <para>
    /// This exists because of what the first in-game run of texture replacement found: writing the renderer's
    /// <em>instance</em> material is correct and provably applied, and still changes nothing on
    /// screen when the renderer we found is not the one being drawn.
    /// <c>Bulbul.CostumeChangeService</c> deactivates the five default costume objects
    /// (<c>Body</c>, <c>Bottom1</c>, <c>Paka1</c>, <c>Socks</c>, <c>Top1</c>) and instantiates a
    /// costume prefab whose renderers have different names (<c>Body1</c>, <c>Top3</c>,
    /// <c>Bottom2</c>) but share the same material assets - <c>Body1</c> uses <c>mt_Body</c>, the
    /// very material <c>Body</c> uses.
    /// </para>
    /// <para>
    /// So the shared material is the thing both renderers actually have in common, and writing it
    /// reaches the costume the player is wearing. It is an in-memory write to a loaded asset: no
    /// game file is touched, and <c>TextureFeature.RestoreAll</c> undoes it like any other.
    /// </para>
    /// </summary>
    public enum TextureWriteMode
    {
        /// <summary>
        /// Write <c>renderer.materials[i]</c> only. Affects exactly this renderer, and nothing
        /// else that happens to use the same material - but misses a renderer we never found.
        /// </summary>
        Instance = 0,

        /// <summary>
        /// Write <c>renderer.sharedMaterials[i]</c> only. Reaches every renderer using that
        /// material, including ones the name lookup cannot see, and survives a costume swap that
        /// re-assigns <c>sharedMaterial</c>.
        /// </summary>
        Shared,

        /// <summary>Write both. The default while the costume interaction is being characterised.</summary>
        Both,
    }
}
