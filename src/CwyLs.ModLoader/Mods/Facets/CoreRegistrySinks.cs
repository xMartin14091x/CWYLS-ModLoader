using System;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Conditions;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Points the Core registries' error sinks at the BepInEx log, so an evaluator, runner or
    /// <c>ConditionsChanged</c> handler failure that Core turns into a result is also written down
    /// (Core has no logger). Called once from <c>ModLoaderPlugin.Awake</c>; calling it again only
    /// re-points the sinks.
    /// </summary>
    public static class CoreRegistrySinks
    {
        /// <summary>Installs the sinks on <see cref="ConditionRegistry.Shared"/> and <see cref="ActionServices.Shared"/>.</summary>
        public static void Install()
        {
            ConditionRegistry.Shared.ErrorSink = message => Warn("COND", message);
            ActionServices.Shared.ErrorSink = message => Warn("ACTION", message);
        }

        private static void Warn(string tag, string message)
        {
            try
            {
                CwyLsModLoader.Log.LogWarning(tag + " " + message);
            }
            catch (Exception ex)
            {
                // The log itself is gone (shutdown); Unity's log is the last place left.
                UnityEngine.Debug.LogWarning("[CWY] " + tag + " " + message + " (BepInEx log failed: " + ex.GetType().Name + ")");
            }
        }
    }
}
