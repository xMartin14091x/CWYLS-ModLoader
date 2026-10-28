using System;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using HarmonyLib;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Harmony: a HarmonyX instance whose id is this mod's id, owned by the framework. When the mod
    /// fails in its constructor or <c>Entry</c>, and when the game quits, the framework removes every
    /// patch made under that id <b>and</b> every patch whose patch method is declared in one of the
    /// mod's own assemblies, under any id - so a failed mod leaves no patch of its behind. It never
    /// calls an id-less <c>UnpatchAll</c>. Only the framework implements this interface.
    /// </summary>
    /// <remarks>
    /// A mod may still create its own <c>new Harmony("some.id")</c>; patch methods declared in its
    /// assemblies are taken back all the same. The framework cannot catch an exception inside a
    /// patch: catch it there and fall back to the original.
    /// </remarks>
    public interface ICwyModHarmony : ICwyModFacet
    {
        /// <summary>The Harmony id: the mod's id.</summary>
        string Id { get; }

        /// <summary>The instance, created on first use. Patch through it: <c>Instance.Patch(original, prefix: ...)</c>.</summary>
        /// <exception cref="InvalidOperationException">The mod is not running (failed or shut down).</exception>
        Harmony Instance { get; }

        /// <summary>
        /// Applies every <c>[HarmonyPatch]</c> class in the mod's own entry assembly (not the caller's,
        /// which through this helper would be the framework). Throws what Harmony throws, so a failure
        /// in <c>Entry</c> fails the mod and the framework removes whatever was already applied.
        /// </summary>
        /// <exception cref="InvalidOperationException">The mod is not running, or its assembly is not known.</exception>
        void PatchAll();

        /// <summary>The original methods patched under this id.</summary>
        IReadOnlyList<MethodBase> GetPatchedMethods();

        /// <summary>Removes every patch made under this id now (the mod keeps running).</summary>
        void UnpatchSelf();
    }

    /// <summary>The per-mod Harmony facet and the take-back that runs when the mod stops.</summary>
    internal sealed class ModHarmony : ICwyModHarmony
    {
        private readonly ModHelper _helper;
        private readonly Func<Assembly?> _entryAssembly;
        private readonly object _gate = new object();
        private Harmony? _instance;

        internal ModHarmony(ModHelper helper, Func<Assembly?> entryAssembly)
        {
            _helper = helper;
            _entryAssembly = entryAssembly;
        }

        public string Id => _helper.ModId;

        public bool IsAvailable => true;

        public string Status
        {
            get
            {
                lock (_gate)
                {
                    return _instance is null
                        ? "id " + Id + "; no instance yet"
                        : "id " + Id + "; " + Count(_instance.GetPatchedMethods()) + " method(s) patched";
                }
            }
        }

        public Harmony Instance
        {
            get
            {
                if (!_helper.IsActive)
                {
                    throw new InvalidOperationException("the mod " + Id + " is not running; its Harmony instance is closed");
                }

                lock (_gate)
                {
                    return _instance ??= new Harmony(Id);
                }
            }
        }

        public void PatchAll()
        {
            var assembly = _entryAssembly();
            if (assembly is null)
            {
                throw new InvalidOperationException("the entry assembly of " + Id + " is not known; call PatchAll from Entry or later");
            }

            Instance.PatchAll(assembly);
        }

        public IReadOnlyList<MethodBase> GetPatchedMethods() => new List<MethodBase>(Instance.GetPatchedMethods());

        public void UnpatchSelf() => Instance.UnpatchSelf();

        /// <summary>
        /// Removes the mod's patches: <c>UnpatchSelf</c> for the mod's id, then every remaining patch
        /// whose patch method's declaring assembly maps to this mod (<c>ModRuntime.TryFindByAssembly</c>).
        /// Main thread; never throws; logs what it removed.
        /// </summary>
        /// <param name="reason">Why, for the log.</param>
        /// <returns>How many patches were removed.</returns>
        internal int TakeBack(string reason)
        {
            var byId = 0;
            var byAssembly = 0;
            var failures = 0;
            try
            {
                var foreign = new List<KeyValuePair<MethodBase, MethodInfo>>();
                foreach (var original in new List<MethodBase>(Harmony.GetAllPatchedMethods()))
                {
                    var info = Harmony.GetPatchInfo(original);
                    if (info is null)
                    {
                        continue;
                    }

                    foreach (var patch in AllPatches(info))
                    {
                        if (string.Equals(patch.owner, Id, StringComparison.Ordinal))
                        {
                            byId++;
                        }
                        else if (BelongsToMod(patch.PatchMethod))
                        {
                            foreign.Add(new KeyValuePair<MethodBase, MethodInfo>(original, patch.PatchMethod));
                        }
                    }
                }

                if (byId == 0 && foreign.Count == 0)
                {
                    return 0;
                }

                Harmony harmony;
                lock (_gate)
                {
                    harmony = _instance ??= new Harmony(Id);
                }

                if (byId > 0)
                {
                    harmony.UnpatchSelf();
                }

                foreach (var pair in foreign)
                {
                    try
                    {
                        harmony.Unpatch(pair.Key, pair.Value);
                        byAssembly++;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        CwyLsModLoader.Log.LogWarning(
                            "MODS " + Id + ": could not remove its patch " + pair.Value.DeclaringType?.FullName + "." + pair.Value.Name +
                            " on " + pair.Key.DeclaringType?.FullName + "." + pair.Key.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                failures++;
                CwyLsModLoader.Log.LogWarning("MODS " + Id + ": removing its Harmony patches failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            var removed = byId + byAssembly;
            if (removed > 0 || failures > 0)
            {
                ConsoleEvents.File(
                    ModConsoleLines.LoaderSystem, "harmony",
                    ("mod", Id), ("reason", reason), ("byId", byId), ("byAssembly", byAssembly), ("failures", failures));
            }

            return removed;
        }

        private bool BelongsToMod(MethodInfo? method)
        {
            var assembly = method?.DeclaringType?.Assembly;
            return assembly != null
                   && ModRuntime.TryFindByAssembly(assembly, out var owner)
                   && string.Equals(owner, Id, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<Patch> AllPatches(Patches info)
        {
            foreach (var patch in info.Prefixes) { yield return patch; }
            foreach (var patch in info.Postfixes) { yield return patch; }
            foreach (var patch in info.Transpilers) { yield return patch; }
            foreach (var patch in info.Finalizers) { yield return patch; }
            foreach (var patch in info.ILManipulators) { yield return patch; }
        }

        private static int Count(IEnumerable<MethodBase> methods)
        {
            var count = 0;
            foreach (var unused in methods)
            {
                count++;
            }

            return count;
        }
    }
}
