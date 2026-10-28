using BepInEx;
using CwyLs.ModLoader.Core.Smoke;
using UnityEngine;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Smoke
{
    /// <summary>
    /// Registers the Phase-0 checks, so a launch can be verified from
    /// <c>BepInEx/smoke-report.json</c> instead of a human reading LogOutput.log. Deliberately
    /// thin: the runner, its triggers and the reporting live in CwyLs.ModLoader (<c>ModLoaderPlugin</c>
    /// arms the one runner for the process), the aggregation in CwyLs.ModLoader.Core, and the checks
    /// themselves in <see cref="P0Checks"/>.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CwyLsModLoader.PluginGuid)]
    [BepInDependency(CwyLs.ModLoader.Bulbul.BulbulPlugin.PluginGuid)]
    public class SmokePlugin : BaseUnityPlugin
    {
        /// <summary>BepInEx GUID of this plugin.</summary>
        public const string PluginGuid = "cwyls.modloader.smoke";

        /// <summary>Display name of this plugin.</summary>
        public const string PluginName = "CWYLS Smoke";

        /// <summary>Version of this plugin.</summary>
        public const string PluginVersion = "0.2.0";

        private void Awake()
        {
            // Demo lock: full game only (ModLoaderPlugin has already logged why).
            if (DemoLock.IsLocked)
            {
                enabled = false;
                return;
            }

            P0Checks.RegisterAll();

            // The runner is not created here. ModLoaderPlugin owns it, along with the Smoke.* config
            // and the key binding, and this plugin hard-depends on it, so it is already armed by
            // the time this Awake runs; creating a second one here would bind the same settings in
            // a second .cfg.
            ConsoleEvents.Setup(
                "smoke.checks",
                $"{PluginName} v{PluginVersion}",
                ("registered", SmokeRegistry.Count));
        }
    }
}
