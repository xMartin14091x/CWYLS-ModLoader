namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Camera: the room camera's presets and story virtual cameras, restored when a scene ends. A
    /// slot until a camera backend is installed with <c>ModFacetBackends.Use&lt;ICwyModCamera&gt;</c> and
    /// members are added here; only the framework implements this interface. When the game's PC build
    /// has no usable story cameras the backend reports <c>IsAvailable = false</c> with the reason.
    /// </summary>
    public interface ICwyModCamera : ICwyModFacet
    {
    }

    /// <summary>The per-mod Camera facet.</summary>
    internal sealed class ModCamera : ModFacetProxy<ICwyModCamera>, ICwyModCamera
    {
        /// <summary>The status without a backend.</summary>
        internal const string NotAvailable = "camera control is not available: no camera backend is installed";

        internal ModCamera(ModHelper helper)
            : base(helper, "camera", NotAvailable)
        {
        }
    }
}
