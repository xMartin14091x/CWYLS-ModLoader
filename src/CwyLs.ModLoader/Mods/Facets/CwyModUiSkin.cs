namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// UiSkin: pack sprites for the game's own UI (<c>ui/**.png</c>, <c>ui/_sprites.json</c>). A slot
    /// until a UI-skin backend is installed with <c>ModFacetBackends.Use&lt;ICwyModUiSkin&gt;</c> and
    /// members are added here; only the framework implements this interface. Mod windows are the Ui facet.
    /// </summary>
    public interface ICwyModUiSkin : ICwyModFacet
    {
    }

    /// <summary>The per-mod UiSkin facet.</summary>
    internal sealed class ModUiSkin : ModFacetProxy<ICwyModUiSkin>, ICwyModUiSkin
    {
        /// <summary>The status without a backend.</summary>
        internal const string NotAvailable = "UI reskins are not available: no UI-skin backend is installed";

        internal ModUiSkin(ModHelper helper)
            : base(helper, "uiSkin", NotAvailable)
        {
        }
    }
}
