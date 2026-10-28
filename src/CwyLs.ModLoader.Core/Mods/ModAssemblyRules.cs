using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// Assembly-identity rules for code mods: one
    /// copy of every shared assembly per process. A mod may ship private helper DLLs, but a copy of
    /// something the game, BepInEx or the framework provides is never loaded from a mod folder -
    /// two copies of one identity give "<c>MyType</c> cannot be cast to <c>MyType</c>", and HarmonyX
    /// cross-wires the patches of two copies of one build.
    /// </summary>
    public static class ModAssemblyRules
    {
        /// <summary>
        /// Name families that are always shared, whether or not an assembly of that name is loaded
        /// yet: the framework, the game, Unity, BepInEx, Harmony, MonoMod, Cecil and the core
        /// runtime. A name matches a family when it equals it or continues it with <c>.</c> or <c>-</c>.
        /// </summary>
        /// <remarks>
        /// <c>System</c> and <c>Microsoft</c> are not whole families. A provider SDK
        /// needs <c>System.ClientModel</c>, <c>Microsoft.Extensions.*</c> or <c>System.Collections.Immutable</c>,
        /// which the game does not ship; the ones it does ship (<c>System.dll</c>,
        /// <c>System.Text.Json</c>, <c>Microsoft.Bcl.AsyncInterfaces</c>, ...) are shared because they
        /// are in its <c>Managed</c> folder or already loaded - the loader passes those as the
        /// known-shared list.
        /// </remarks>
        public static readonly IReadOnlyList<string> SharedFamilies = new[]
        {
            "CwyLs.ModLoader", "Assembly-CSharp", "UnityEngine", "Unity", "BepInEx", "0Harmony", "HarmonyX",
            "MonoMod", "Mono.Cecil", "mscorlib", "netstandard",
        };

        /// <summary>
        /// True when <paramref name="simpleName"/> is shared: in one of the <see cref="SharedFamilies"/>,
        /// or in <paramref name="knownShared"/> (names loaded now, or shipped in the game's Managed
        /// folder, BepInEx's core folder or the plugins folder).
        /// </summary>
        /// <param name="simpleName">An assembly's simple name, e.g. <c>CwyLs.ModLoader.Core</c>.</param>
        /// <param name="knownShared">Further shared names; null for none.</param>
        public static bool IsShared(string? simpleName, ICollection<string>? knownShared)
        {
            if (string.IsNullOrEmpty(simpleName))
            {
                return false;
            }

            foreach (var family in SharedFamilies)
            {
                if (simpleName!.Length == family.Length
                        ? string.Equals(simpleName, family, StringComparison.OrdinalIgnoreCase)
                        : simpleName.Length > family.Length
                          && simpleName.StartsWith(family, StringComparison.OrdinalIgnoreCase)
                          && (simpleName[family.Length] == '.' || simpleName[family.Length] == '-'))
                {
                    return true;
                }
            }

            if (knownShared != null)
            {
                foreach (var name in knownShared)
                {
                    if (string.Equals(name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The DLLs in a mod folder (other than its entry DLL) that are shared assemblies. They are
        /// never loaded - the resolver serves the process's own copy - so the loader only warns.
        /// </summary>
        /// <param name="packFiles">The pack's files, relative to its root.</param>
        /// <param name="entryDll">The normalised entry DLL path, excluded from the check.</param>
        /// <param name="knownShared">Shared names beyond <see cref="SharedFamilies"/>; null for none.</param>
        /// <returns>The offending relative paths, sorted; empty when the folder is clean. Never null.</returns>
        public static IReadOnlyList<string> FindBundledShared(
            IEnumerable<string>? packFiles,
            string? entryDll,
            ICollection<string>? knownShared)
        {
            var found = new List<string>();
            foreach (var path in OtherDlls(packFiles, entryDll))
            {
                if (IsShared(EntryDllPath.SimpleName(path), knownShared))
                {
                    found.Add(path);
                }
            }

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>
        /// The DLLs in a mod folder that are another code mod's entry DLL. Every
        /// planned mod's entry assembly name is reserved to that mod's own folder, so such a copy is
        /// never loaded - otherwise it would load first and the real mod would be refused as
        /// <see cref="ModSkipReason.AssemblyAlreadyLoaded"/>. The loader warns the bundling mod.
        /// </summary>
        /// <param name="packFiles">The bundling mod's files, relative to its root.</param>
        /// <param name="entryDll">Its own normalised entry DLL path, excluded from the check.</param>
        /// <param name="ownModId">Its id; names it reserves itself are not reported.</param>
        /// <param name="reservedNameOwners">Entry assembly simple name → the id of the mod that owns it. Case-insensitive lookups.</param>
        /// <returns>(relative path, owning mod id) pairs, sorted by path; empty when there are none. Never null.</returns>
        public static IReadOnlyList<KeyValuePair<string, string>> FindBundledReserved(
            IEnumerable<string>? packFiles,
            string? entryDll,
            string? ownModId,
            IReadOnlyDictionary<string, string>? reservedNameOwners)
        {
            var found = new List<KeyValuePair<string, string>>();
            if (reservedNameOwners is null || reservedNameOwners.Count == 0)
            {
                return found;
            }

            foreach (var path in OtherDlls(packFiles, entryDll))
            {
                var owner = Lookup(reservedNameOwners, EntryDllPath.SimpleName(path));
                if (owner != null && !string.Equals(owner, ownModId, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(new KeyValuePair<string, string>(path, owner));
                }
            }

            found.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
            return found;
        }

        /// <summary>
        /// True when an assembly named <paramref name="simpleName"/> is already loaded, which means
        /// loading the entry DLL would hand back that one or load a second copy. Refused as
        /// <see cref="ModSkipReason.AssemblyAlreadyLoaded"/>.
        /// </summary>
        /// <param name="simpleName">The entry DLL's simple name, from its metadata.</param>
        /// <param name="loadedSimpleNames">Simple names of every assembly loaded in the process.</param>
        public static bool IsAlreadyLoaded(string? simpleName, IEnumerable<string>? loadedSimpleNames)
        {
            if (string.IsNullOrEmpty(simpleName) || loadedSimpleNames is null)
            {
                return false;
            }

            foreach (var name in loadedSimpleNames)
            {
                if (string.Equals(name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The name of an already-loaded assembly with the same module version id (the same build,
        /// perhaps renamed), or null. HarmonyX keys patch methods by MVID, so two copies of one build
        /// would run each other's patches.
        /// </summary>
        /// <param name="mvid">The new assembly's manifest-module MVID.</param>
        /// <param name="loaded">Name and MVID of every other loaded assembly.</param>
        public static string? FindSameBuild(Guid mvid, IEnumerable<KeyValuePair<string, Guid>>? loaded)
        {
            if (mvid == Guid.Empty || loaded is null)
            {
                return null;
            }

            foreach (var pair in loaded)
            {
                if (pair.Value == mvid)
                {
                    return pair.Key;
                }
            }

            return null;
        }

        /// <summary>A case-insensitive lookup in a dictionary that may use any comparer.</summary>
        internal static string? Lookup(IReadOnlyDictionary<string, string>? map, string? key)
        {
            if (map is null || string.IsNullOrEmpty(key))
            {
                return null;
            }

            if (map.TryGetValue(key!, out var direct))
            {
                return direct;
            }

            foreach (var pair in map)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        /// <summary>The pack's <c>.dll</c> files other than the entry DLL, normalised.</summary>
        private static IEnumerable<string> OtherDlls(IEnumerable<string>? packFiles, string? entryDll)
        {
            if (packFiles is null)
            {
                yield break;
            }

            foreach (var raw in packFiles)
            {
                var path = PackContentIndex.Normalise(raw);
                if (path is null || !path.EndsWith(EntryDllPath.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entryDll != null && string.Equals(path, entryDll, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    /// <summary>Where the resolver found an assembly the runtime asked for.</summary>
    public enum ModAssemblySource
    {
        /// <summary>Not ours to answer: the runtime keeps looking (and fails if nobody else knows it).</summary>
        NotFound = 0,

        /// <summary>An assembly with that simple name is already loaded: use it.</summary>
        Loaded = 1,

        /// <summary>A mod's private DLL at <see cref="ModAssemblyDecision.Path"/>: load it.</summary>
        ModFolder = 2,
    }

    /// <summary>Who asked the runtime for an assembly it could not find.</summary>
    public enum ModAssemblyRequester
    {
        /// <summary>
        /// The runtime did not say (<c>ResolveEventArgs.RequestingAssembly</c> is null, e.g. a
        /// <c>Type.GetType</c> or <c>Assembly.Load</c> by name). Served from a mod folder only when
        /// exactly one mod folder has the name.
        /// </summary>
        Unknown = 0,

        /// <summary>An assembly of a code mod: its entry DLL or a DLL the resolver loaded from its folder.</summary>
        Mod = 1,

        /// <summary>Anything else - the game, a BepInEx plugin, the framework. Never served from a mod folder.</summary>
        NotAMod = 2,
    }

    /// <summary>What the resolver knows about the process when a request arrives. Immutable.</summary>
    public sealed class ModAssemblyEnvironment
    {
        /// <summary>Creates a snapshot.</summary>
        /// <param name="loadedSimpleNames">Simple names of every loaded assembly.</param>
        /// <param name="modFolders">The folder of every loaded mod's entry DLL, in load order.</param>
        /// <param name="fileExists">Tells whether a path exists.</param>
        /// <param name="knownShared">Shared names beyond <see cref="ModAssemblyRules.SharedFamilies"/>; null for none.</param>
        /// <param name="reservedNames">
        /// Every planned code mod's entry assembly simple name → the folder of that mod's entry DLL.
        /// Such a name is never served from any other folder. Null for none.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="fileExists"/> is null.</exception>
        public ModAssemblyEnvironment(
            ICollection<string>? loadedSimpleNames,
            IReadOnlyList<string>? modFolders,
            Func<string, bool> fileExists,
            ICollection<string>? knownShared = null,
            IReadOnlyDictionary<string, string>? reservedNames = null)
        {
            FileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
            LoadedSimpleNames = loadedSimpleNames ?? Array.Empty<string>();
            ModFolders = modFolders ?? Array.Empty<string>();
            KnownShared = knownShared;
            ReservedNames = reservedNames;
        }

        /// <summary>Simple names of every loaded assembly. Never null.</summary>
        public ICollection<string> LoadedSimpleNames { get; }

        /// <summary>Every loaded mod's entry-DLL folder, in load order. Never null.</summary>
        public IReadOnlyList<string> ModFolders { get; }

        /// <summary>Tells whether a path exists.</summary>
        public Func<string, bool> FileExists { get; }

        /// <summary>Shared names beyond the fixed families, or null.</summary>
        public ICollection<string>? KnownShared { get; }

        /// <summary>Reserved entry names → owning folder, or null.</summary>
        public IReadOnlyDictionary<string, string>? ReservedNames { get; }
    }

    /// <summary>The resolver's answer for one request.</summary>
    public sealed class ModAssemblyDecision
    {
        internal ModAssemblyDecision(
            ModAssemblySource source,
            string name,
            string? path,
            IReadOnlyList<string>? ignoredShared = null,
            IReadOnlyList<string>? ignoredReserved = null,
            IReadOnlyList<string>? ambiguous = null)
        {
            Source = source;
            Name = name;
            Path = path;
            IgnoredSharedCopies = ignoredShared ?? Array.Empty<string>();
            IgnoredReservedCopies = ignoredReserved ?? Array.Empty<string>();
            AmbiguousCandidates = ambiguous ?? Array.Empty<string>();
        }

        /// <summary>Where to take it from.</summary>
        public ModAssemblySource Source { get; }

        /// <summary>The requested simple name; empty when the request was unreadable.</summary>
        public string Name { get; }

        /// <summary>The file to load for <see cref="ModAssemblySource.ModFolder"/>; null otherwise.</summary>
        public string? Path { get; }

        /// <summary>Copies of a shared assembly found in mod folders and deliberately not served. Never null.</summary>
        public IReadOnlyList<string> IgnoredSharedCopies { get; }

        /// <summary>Copies of another mod's entry DLL found outside that mod's folder and not served. Never null.</summary>
        public IReadOnlyList<string> IgnoredReservedCopies { get; }

        /// <summary>
        /// For a request with no known requester: every mod-folder file that could answer it, when
        /// there is more than one - nothing is served then, and the loader names them in one warning.
        /// Never null.
        /// </summary>
        public IReadOnlyList<string> AmbiguousCandidates { get; }
    }

    /// <summary>
    /// The order the framework's <c>AssemblyResolve</c> handler answers in: an
    /// already-loaded assembly of that simple name for anyone; then, for a
    /// requester that is a mod, its own folder and the other mod folders in load order; for an
    /// unknown requester, a mod folder only when exactly one has the name; for anyone else, never a
    /// mod folder. Shared names and other mods' entry names are never served from a mod folder.
    /// Mono does not probe a mod's folder for its private dependencies, so without this they do not
    /// load at all. Pure: the file system is a callback.
    /// </summary>
    public static class ModAssemblyResolver
    {
        /// <summary>Decides where a requested assembly comes from.</summary>
        /// <param name="requestedName">The full name the runtime asked for, e.g. <c>MyLib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null</c>.</param>
        /// <param name="requester">Who asked.</param>
        /// <param name="requesterFolder">For <see cref="ModAssemblyRequester.Mod"/>, the folder of that mod's entry DLL; otherwise ignored.</param>
        /// <param name="environment">What is loaded and where the mod folders are.</param>
        /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
        public static ModAssemblyDecision Decide(
            string? requestedName,
            ModAssemblyRequester requester,
            string? requesterFolder,
            ModAssemblyEnvironment environment)
        {
            if (environment is null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            var name = SimpleName(requestedName);
            if (name.Length == 0 || name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                return new ModAssemblyDecision(ModAssemblySource.NotFound, name, null);
            }

            if (ModAssemblyRules.IsAlreadyLoaded(name, environment.LoadedSimpleNames))
            {
                return new ModAssemblyDecision(ModAssemblySource.Loaded, name, null);
            }

            if (requester == ModAssemblyRequester.NotAMod)
            {
                return new ModAssemblyDecision(ModAssemblySource.NotFound, name, null);
            }

            var folders = new List<string>();
            if (requester == ModAssemblyRequester.Mod && !string.IsNullOrEmpty(requesterFolder))
            {
                folders.Add(requesterFolder!);
            }

            foreach (var folder in environment.ModFolders)
            {
                if (!string.IsNullOrEmpty(folder) && !Contains(folders, folder))
                {
                    folders.Add(folder);
                }
            }

            var shared = ModAssemblyRules.IsShared(name, environment.KnownShared);
            var reservedFolder = ModAssemblyRules.Lookup(environment.ReservedNames, name);
            List<string>? ignoredShared = null;
            List<string>? ignoredReserved = null;
            List<string>? candidates = null;

            foreach (var folder in folders)
            {
                var candidate = System.IO.Path.Combine(folder, name + EntryDllPath.Extension);
                if (!environment.FileExists(candidate))
                {
                    continue;
                }

                if (shared)
                {
                    (ignoredShared ??= new List<string>()).Add(candidate);
                    continue;
                }

                if (reservedFolder != null && !SameFolder(folder, reservedFolder))
                {
                    (ignoredReserved ??= new List<string>()).Add(candidate);
                    continue;
                }

                (candidates ??= new List<string>()).Add(candidate);
                if (requester == ModAssemblyRequester.Mod)
                {
                    break;
                }
            }

            if (candidates != null && (requester == ModAssemblyRequester.Mod || candidates.Count == 1))
            {
                return new ModAssemblyDecision(ModAssemblySource.ModFolder, name, candidates[0], ignoredShared, ignoredReserved);
            }

            return new ModAssemblyDecision(
                ModAssemblySource.NotFound, name, null, ignoredShared, ignoredReserved,
                candidates != null && candidates.Count > 1 ? candidates : null);
        }

        /// <summary>The simple name of a full assembly name: the part before the first comma, trimmed.</summary>
        /// <param name="fullName">A full or simple assembly name.</param>
        public static string SimpleName(string? fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return string.Empty;
            }

            var comma = fullName!.IndexOf(',');
            return (comma < 0 ? fullName : fullName.Substring(0, comma)).Trim();
        }

        /// <summary>True when two folder paths name the same folder (case-insensitive, trailing separators ignored).</summary>
        /// <param name="a">A folder path.</param>
        /// <param name="b">Another folder path.</param>
        public static bool SameFolder(string? a, string? b) =>
            string.Equals(TrimFolder(a), TrimFolder(b), StringComparison.OrdinalIgnoreCase);

        private static string TrimFolder(string? folder) =>
            (folder ?? string.Empty).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        private static bool Contains(List<string> folders, string folder)
        {
            foreach (var existing in folders)
            {
                if (SameFolder(existing, folder))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
