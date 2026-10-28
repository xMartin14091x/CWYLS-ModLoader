using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Smoke
{
    /// <summary>
    /// The Phase-0 checks: the three plugins loaded, the two seams the whole loader rests on are
    /// still present in this build of the game, and the framework's directories exist.
    /// Every seam is probed by name through reflection rather than by a compile-time reference,
    /// so a renamed type is a failed check with a readable detail instead of a TypeLoadException
    /// while the report is being produced.
    /// </summary>
    internal static class P0Checks
    {
        private const string Phase = "P0";

        private const string HeroineServiceType = "Bulbul.HeroineService, Assembly-CSharp";
        private const string MasterDataLoaderType = "Bulbul.MasterDataLoader, Assembly-CSharp";
        private const string UniTaskReturnType = "Cysharp.Threading.Tasks.UniTask";

        private static readonly string[] RequiredPluginGuids =
        {
            CwyLsModLoader.PluginGuid,
            CwyLs.ModLoader.Bulbul.BulbulPlugin.PluginGuid,
            SmokePlugin.PluginGuid,
        };

        /// <summary>Registers every P0 check. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck("p0.plugins_loaded", Phase, PluginsLoaded));
            SmokeRegistry.Register(new SmokeCheck("p0.seam.HeroineService", Phase, HeroineServiceSeam));
            SmokeRegistry.Register(new SmokeCheck("p0.seam.MasterDataLoader.Load", Phase, MasterDataLoaderLoadSeam));
            SmokeRegistry.Register(new SmokeCheck("p0.paths", Phase, PathsExist));
        }

        private static SmokeCheckResult PluginsLoaded()
        {
            var missing = new List<string>();
            foreach (var guid in RequiredPluginGuids)
            {
                if (!Chainloader.PluginInfos.ContainsKey(guid))
                {
                    missing.Add(guid);
                }
            }

            return missing.Count == 0
                ? SmokeCheckResult.Pass("p0.plugins_loaded", string.Join(", ", RequiredPluginGuids))
                : SmokeCheckResult.Fail("p0.plugins_loaded", "not loaded: " + string.Join(", ", missing.ToArray()));
        }

        private static SmokeCheckResult HeroineServiceSeam()
        {
            var type = Type.GetType(HeroineServiceType, throwOnError: false);

            return type is null
                ? SmokeCheckResult.Fail("p0.seam.HeroineService", "type not found: " + HeroineServiceType)
                : SmokeCheckResult.Pass(
                    "p0.seam.HeroineService", $"{type.FullName} in {type.Assembly.GetName().Name}");
        }

        private static SmokeCheckResult MasterDataLoaderLoadSeam()
        {
            const string id = "p0.seam.MasterDataLoader.Load";

            var type = Type.GetType(MasterDataLoaderType, throwOnError: false);
            if (type is null)
            {
                return SmokeCheckResult.Fail(id, "type not found: " + MasterDataLoaderType);
            }

            // Bound to the no-argument overload on purpose: the class also has
            // LoadCompletionAsync(CancellationToken), and a loose GetMethod would be ambiguous
            // the day another Load overload appears.
            var load = type.GetMethod(
                "Load", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (load is null)
            {
                return SmokeCheckResult.Fail(id, "public instance Load() not found on " + type.FullName);
            }

            var returned = load.ReturnType.FullName ?? load.ReturnType.Name;

            return string.Equals(returned, UniTaskReturnType, StringComparison.Ordinal)
                ? SmokeCheckResult.Pass(id, "Load() -> " + returned)
                : SmokeCheckResult.Fail(id, $"Load() returns {returned}, expected {UniTaskReturnType}");
        }

        private static SmokeCheckResult PathsExist()
        {
            const string id = "p0.paths";

            var missing = new List<string>();
            foreach (var path in new[] { ModLoaderPaths.PacksRoot, ModLoaderPaths.TraceRoot })
            {
                if (!Directory.Exists(path))
                {
                    missing.Add(path);
                }
            }

            return missing.Count == 0
                ? SmokeCheckResult.Pass(id, $"{ModLoaderPaths.PacksRoot}; {ModLoaderPaths.TraceRoot}")
                : SmokeCheckResult.Fail(id, "missing: " + string.Join("; ", missing.ToArray()));
        }
    }
}
