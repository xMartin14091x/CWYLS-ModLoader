using BepInEx.Logging;
using CwyLs.ModLoader.Core.Mods;

namespace CwyLs.ModLoader
{
    /// <summary>Framework-wide identity and the shared log source.</summary>
    public static class CwyLsModLoader
    {
        /// <summary>BepInEx GUID of the framework plugin; every other CWY plugin depends on it.</summary>
        public const string PluginGuid = "cwyls.modloader";

        /// <summary>Human-readable plugin name.</summary>
        public const string PluginName = "CWYLS Mod Loader";

        /// <summary>
        /// The framework version: the mod API version (<see cref="CwyApi.PluginVersionText"/>),
        /// so the startup line, BepInEx and the manifests' <c>loaderVersion</c> check agree.
        /// Was 0.1.0 before the mod API.
        /// </summary>
        public const string PluginVersion = CwyApi.PluginVersionText;

        private static ManualLogSource? _log;

        /// <summary>
        /// Shared logger. Normally set by <see cref="ModLoaderPlugin"/>.Awake; falls back to its own
        /// source so a dependent plugin that somehow loads first still logs instead of throwing.
        /// </summary>
        public static ManualLogSource Log => _log ??= Logger.CreateLogSource(PluginName);

        internal static void UseLog(ManualLogSource log) => _log = log;
    }
}
