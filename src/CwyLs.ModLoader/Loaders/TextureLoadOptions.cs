namespace CwyLs.ModLoader.Loaders
{
    /// <summary>
    /// How a PNG should become a <c>Texture2D</c>. Immutable; use <see cref="Default"/> unless a
    /// caller has a reason not to.
    /// <para>
    /// A class rather than a struct on purpose: <c>default(T)</c> on a struct would silently mean
    /// "no mip maps", which is not the default anyone wants for a character texture.
    /// </para>
    /// </summary>
    public sealed class TextureLoadOptions
    {
        /// <summary>Creates options. The defaults are what the texture feature uses for lilToon albedo slots.</summary>
        /// <param name="linear">
        /// True for data textures (normal, mask, AO), false for colour. lilToon's <c>_MainTex</c>
        /// is colour, so the default is sRGB.
        /// </param>
        /// <param name="mipMaps">
        /// True to build a mip chain. On by default: a character texture is viewed at every
        /// distance and a mip-less one shimmers.
        /// </param>
        /// <param name="readableAfterLoad">
        /// True to keep the pixels on the CPU after upload. Off by default - it doubles the memory
        /// each texture costs, and only a caller that intends to read pixels back (a smoke check,
        /// an atlas build) needs it.
        /// </param>
        public TextureLoadOptions(bool linear = false, bool mipMaps = true, bool readableAfterLoad = false)
        {
            Linear = linear;
            MipMaps = mipMaps;
            ReadableAfterLoad = readableAfterLoad;
        }

        /// <summary>The shared default: sRGB, mip-mapped, not readable.</summary>
        public static TextureLoadOptions Default { get; } = new TextureLoadOptions();

        /// <summary>True to treat the pixels as linear data rather than sRGB colour.</summary>
        public bool Linear { get; }

        /// <summary>True to build a mip chain.</summary>
        public bool MipMaps { get; }

        /// <summary>True to keep the pixels readable from script after upload.</summary>
        public bool ReadableAfterLoad { get; }

        /// <summary>
        /// Distinguishes cache entries built with different options, so a caller that asks for a
        /// readable texture is never handed the non-readable one an earlier caller cached.
        /// </summary>
        public string CacheTag =>
            (Linear ? "L1" : "L0") + (MipMaps ? "M1" : "M0") + (ReadableAfterLoad ? "R1" : "R0");

        /// <summary>The cache tag, which is also the shortest honest description of these options.</summary>
        public override string ToString() => CacheTag;
    }
}
