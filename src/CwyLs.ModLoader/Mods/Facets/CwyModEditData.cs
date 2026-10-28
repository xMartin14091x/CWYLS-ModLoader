namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// EditData: Content-Patcher-style edits to any master table from packs (<c>data/edit/*.json</c>).
    /// A slot until an EditData backend is installed with <c>ModFacetBackends.Use&lt;ICwyModEditData&gt;</c>
    /// and members are added here; only the framework implements this interface.
    /// </summary>
    public interface ICwyModEditData : ICwyModFacet
    {
    }

    /// <summary>The per-mod EditData facet.</summary>
    internal sealed class ModEditData : ModFacetProxy<ICwyModEditData>, ICwyModEditData
    {
        /// <summary>The status without a backend.</summary>
        internal const string NotAvailable = "EditData is not available: no EditData backend is installed";

        internal ModEditData(ModHelper helper)
            : base(helper, "editData", NotAvailable)
        {
        }
    }
}
