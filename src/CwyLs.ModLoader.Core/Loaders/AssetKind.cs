namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// Coarse kind of a pack asset: what the framework turns the file into, not how it is encoded.
    /// The encoding is <see cref="AssetFormat"/>.
    /// </summary>
    public enum AssetKind
    {
        /// <summary>Not decided - a file rejected before its kind could be established.</summary>
        Unknown = 0,

        /// <summary>An image that becomes a <c>Texture2D</c> (texture replacement).</summary>
        Texture = 1,

        /// <summary>A sound that becomes an <c>AudioClip</c> (voice replacement).</summary>
        Audio = 2,
    }
}
