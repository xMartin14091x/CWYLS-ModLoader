namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Api: the local game API that programs on this computer (an MCP server, an AI companion,
    /// sidecars) use - state, events and actions over HTTP on the literal loopback address
    /// <c>127.0.0.1:7870</c> only, with a per-launch access key in an owner-only file.
    /// Off by default (<c>[Api] Enabled=false</c>: no
    /// listener, no port, no key file). A mod does not serve requests itself: it publishes actions
    /// through the Actions facet, and the API lists and runs them like the framework's own. The
    /// backend is installed by the <c>CwyLs.ModLoader.Api</c> plugin with
    /// <c>ModFacetBackends.Use&lt;ICwyModApi&gt;</c>; only the framework implements this interface.
    /// Wire reference: <c>src/CwyLs.ModLoader.Core/Api/README.md</c>.
    /// </summary>
    public interface ICwyModApi : ICwyModFacet
    {
        /// <summary>True while the API is listening; false when it is off or the plugin is not installed.</summary>
        bool IsRunning { get; }

        /// <summary>The base address while listening, e.g. <c>http://127.0.0.1:7870/</c>; empty otherwise. The access key is never exposed here.</summary>
        string Endpoint { get; }
    }

    /// <summary>The per-mod Api facet.</summary>
    internal sealed class ModApi : ModFacetProxy<ICwyModApi>, ICwyModApi
    {
        /// <summary>The status without a backend.</summary>
        internal const string NotAvailable =
            "the local game API is not installed (CwyLs.ModLoader.Api); when installed it is off by default " +
            "([Api] Enabled=false) and listens only on 127.0.0.1:7870";

        internal ModApi(ModHelper helper)
            : base(helper, "api", NotAvailable)
        {
        }

        public bool IsRunning => Backend?.IsRunning ?? false;

        public string Endpoint => Backend?.Endpoint ?? string.Empty;
    }
}
