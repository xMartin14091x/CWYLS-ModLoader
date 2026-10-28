using BepInEx;

namespace CwyLs.ModLoader.Api
{
    /// <summary>
    /// The local game API plugin. Thin: <see cref="ApiFeature.Initialize"/> binds the
    /// <c>[Api]</c> config, registers the <c>api</c> console command, the kill-switch key, the
    /// mod facet backend and the smoke check, and starts the API only when <c>[Api] Enabled=true</c>.
    /// Nothing lives on this component (the game destroys plugin components ~0.2 s after boot);
    /// per-frame work is a ticker on the shared main-thread dispatcher.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, CwyLsModLoader.PluginVersion)]
    [BepInDependency(CwyLsModLoader.PluginGuid)]
    [BepInDependency(BulbulGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(DataGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ApiPlugin : BaseUnityPlugin
    {
        /// <summary>BepInEx GUID of this plugin; its config file is <c>BepInEx/config/&lt;GUID&gt;.cfg</c>.</summary>
        public const string PluginGuid = "cwyls.modloader.api";

        /// <summary>Display name of this plugin.</summary>
        public const string PluginName = "CWYLS API";

        // Soft dependencies only order the Awake calls, so the game adapters register their
        // event and data backends before the API looks for them. Literals: this plugin must not
        // reference the game adapter assemblies.
        private const string BulbulGuid = "cwyls.modloader.bulbul";
        private const string DataGuid = "cwyls.modloader.bulbul.data";

        private void Awake()
        {
            // Demo lock: full game only (ModLoaderPlugin has already logged why).
            if (DemoLock.IsLocked)
            {
                enabled = false;
                return;
            }

            ApiFeature.Initialize(Config);
        }
    }
}
