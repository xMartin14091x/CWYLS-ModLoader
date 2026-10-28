using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Packs;
using CwyLs.ModLoader.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>How the loader reads a mod assembly.</summary>
    public enum ModAssemblyLoadMode
    {
        /// <summary><c>Assembly.LoadFrom(path)</c>: the default; the file keeps its location for stack traces.</summary>
        LoadFrom = 0,

        /// <summary>
        /// <c>Assembly.Load(bytes, pdb)</c>: a developer switch for hot-swap experiments. Nothing is
        /// locked or memory-mapped, but a second load is a second assembly.
        /// </summary>
        Bytes = 1,
    }

    /// <summary>
    /// The code-mod loader: after the packs resolve, it loads every pack with an
    /// <c>entryDll</c> in dependency order, creates its one <see cref="CwyMod"/> subclass and calls
    /// <see cref="CwyMod.Entry"/>, then collects the mods' APIs and raises the mods-ready signal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>When.</b> On the first frame of the shared main-thread host, i.e. after every BepInEx
    /// plugin's <c>Awake</c> has run, so the game adapter has registered the game-specific
    /// facets (<see cref="ModBackends"/>). The plugin component itself is gone by then in this
    /// game; the host is a hidden <c>DontDestroyOnLoad</c> object.
    /// </para>
    /// <para>
    /// <b>Isolation.</b> Every step that touches mod code is wrapped: a refused mod is
    /// <see cref="ModState.Skipped"/> with a reason, a mod whose constructor or <c>Entry</c> throws
    /// is <see cref="ModState.Failed"/> (one <c>MODS</c> line, full stack in the trace file) and
    /// everything it registered is taken back - its helper registrations are disposed, and its
    /// Harmony patches removed (<c>UnpatchSelf()</c> for the mod id plus every patch whose patch
    /// method is declared in one of the mod's assemblies; never an id-less <c>UnpatchAll</c>) - and
    /// mods that require it are skipped as <see cref="ModSkipReason.DependencyFailed"/>. An
    /// unexpected failure of the loader itself skips only that mod
    /// (<see cref="ModSkipReason.AssemblyLoadFailed"/>). The rest keep loading.
    /// </para>
    /// <para>
    /// <b>No unload.</b> Mono cannot unload an assembly, so a changed mod DLL needs a game restart;
    /// <c>packs rescan</c> does not reload code. A DLL loaded from a file stays memory-mapped: never
    /// overwrite a mod DLL while the game runs (deploy with the game closed).
    /// </para>
    /// <para>
    /// <b>Binding</b>: one <c>AssemblyResolve</c> handler,
    /// installed before any mod type is touched, answers in the order of
    /// <see cref="ModAssemblyResolver"/>: an already-loaded assembly of that name for anyone; for a
    /// mod's assembly, its own folder and then the other mod folders; for an unknown requester, a
    /// mod folder only when exactly one has the name; for the game, a plugin or the framework,
    /// never a mod folder. Every DLL it loads from a mod folder is mapped to that mod. Never a
    /// framework, game, BepInEx or Harmony name, nor another mod's entry DLL, from a mod folder. A
    /// second copy of an assembly (same name, or same build) is refused, because HarmonyX would
    /// cross-wire its patches. Deciding and loading happen under one lock.
    /// </para>
    /// </remarks>
    public static class ModLoader
    {
        /// <summary>Config section of the loader settings (in the framework's .cfg).</summary>
        public const string ConfigSection = "Mods";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the assembly load mode (troubleshooting switch).</summary>
        public const string ConfigKeyAssemblyLoad = "AssemblyLoad";

        private static readonly object Gate = new object();

        // Held while the resolver decides and loads (re-entrant: a load may resolve again on the
        // same thread). Taken before Gate, never after it.
        private static readonly object ResolveGate = new object();
        private static readonly List<string> ModFolders = new List<string>();
        private static readonly Dictionary<string, string> ModFolderById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ModIdByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ReservedFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ReservedOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> WarnedIgnoredCopies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string>? _sharedFileNames;

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<ModAssemblyLoadMode>? _loadMode;
        private static bool _initialized;
        private static bool _resolverInstalled;
        private static bool _shutDown;

        /// <summary>True when code mods are loaded at all (<c>[Mods] Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>How mod assemblies are read (<c>[Mods] AssemblyLoad</c>, default LoadFrom).</summary>
        public static ModAssemblyLoadMode AssemblyLoadMode => _loadMode?.Value ?? ModAssemblyLoadMode.LoadFrom;

        /// <summary>True once the first-frame load has run.</summary>
        public static bool HasLoaded { get; private set; }

        /// <summary>The primary column of the last <c>MODS</c> summary line; empty before loading.</summary>
        public static string LastSummary { get; private set; } = string.Empty;

        /// <summary>Every code mod's outcome, in load order. Empty before loading.</summary>
        public static IReadOnlyList<ModStatus> Statuses
        {
            get
            {
                var statuses = new List<ModStatus>();
                foreach (var entry in ModRuntime.All)
                {
                    if (entry.IsCodeMod)
                    {
                        statuses.Add(entry.Status);
                    }
                }

                return statuses;
            }
        }

        /// <summary>
        /// Binds <c>[Mods]</c>, installs the console command and smoke checks, and schedules the load
        /// for the first frame. Called once from <c>ModLoaderPlugin.Awake</c>, after the pack scan and
        /// after <see cref="MainThread.Arm"/>.
        /// </summary>
        /// <param name="config">The framework's config file.</param>
        internal static void Initialize(ConfigFile config)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;
            }

            _enabled = config.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Load code mods (packs in CWYLS Mods whose manifest has an entryDll). Off still loads their content.");
            _loadMode = config.Bind(
                ConfigSection,
                ConfigKeyAssemblyLoad,
                ModAssemblyLoadMode.LoadFrom,
                "Developer switch. LoadFrom (default) loads a mod DLL from its file. Bytes loads it into memory with " +
                "its .pdb (Assembly.Load(byte[], byte[])) so the file is neither locked nor mapped, for hot-swap " +
                "experiments. Needs a restart.");

            // Before any mod type can be touched or JIT-compiled.
            InstallResolver();
            Threading.MainThread.ModFailureRoute = ModFailures.RouteMainThread;
            ModCommands.Register();
            ModSmokeChecks.RegisterAll();
            Application.quitting += Shutdown;

            if (!Threading.MainThread.Post("mods.load", LoadAll))
            {
                CwyLsModLoader.Log.LogError("MODS the load could not be scheduled; no code mod will run");
            }

            ConsoleEvents.Setup(
                "mods",
                "code-mod loader armed; mods load on the first frame",
                ("api", CwyApi.VersionText),
                ("enabled", Enabled),
                ("assemblyLoad", AssemblyLoadMode.ToString()));
        }

        /// <summary>
        /// Loads every planned code mod. Runs once, on the main thread. Each mod is isolated: an
        /// unexpected loader failure skips that mod, and the ready signals and the
        /// summary are raised whatever happened.
        /// </summary>
        private static void LoadAll()
        {
            if (HasLoaded)
            {
                return;
            }

            HasLoaded = true;
            var codeMods = new List<ModEntry>();
            try
            {
                var plan = ModLoadPlanner.Plan(PackManager.LastResolution);
                ModRuntime.Fill(PackManager.Loaded, plan);

                foreach (var entry in ModRuntime.All)
                {
                    if (entry.Candidate != null)
                    {
                        codeMods.Add(entry);
                    }
                }

                ReserveEntryNames(codeMods);

                if (codeMods.Count > 0 && Enabled)
                {
                    var statuses = new Dictionary<string, ModStatus>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in codeMods)
                    {
                        LoadIsolated(entry, statuses);
                        statuses[entry.Id] = entry.Status;
                    }

                    CollectApis(codeMods);
                }
                else
                {
                    foreach (var entry in codeMods)
                    {
                        Skip(entry, ModSkipReason.Disabled, ConfigSection + "." + ConfigKeyEnabled + "=false; its content still loaded");
                    }
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("MODS the loader failed before every mod was handled: " + ex.GetType().Name + ": " + ex.Message);
                ConsoleEvents.File(ModConsoleLines.LoaderSystem, "loaderFailed", ("stack", ex.ToString()));
            }
            finally
            {
                Finish(codeMods);
            }
        }

        /// <summary>One mod, with a catch-all: whatever the loader itself throws becomes a skip of this mod.</summary>
        private static void LoadIsolated(ModEntry entry, Dictionary<string, ModStatus> statuses)
        {
            try
            {
                if (ModLoadGate.CanLoad(entry.Candidate!, statuses, out var why))
                {
                    Load(entry);
                }
                else
                {
                    Skip(entry, ModSkipReason.DependencyFailed, why);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    entry.Helper?.Deactivate("the loader failed while loading it");
                    Skip(entry, ModSkipReason.AssemblyLoadFailed,
                        "the loader failed unexpectedly: " + ex.GetType().Name + ": " + ex.Message, ex);
                }
                catch (Exception reportError)
                {
                    entry.Status = new ModStatus(entry.Id, entry.Pack.Manifest.Version, ModState.Skipped,
                        ModSkipReason.AssemblyLoadFailed, detail: ex.GetType().Name + ": " + ex.Message);
                    CwyLsModLoader.Log.LogError(
                        "MODS " + entry.Id + " skipped: the loader failed (" + ex.GetType().Name + ": " + ex.Message +
                        ") and so did its report (" + reportError.GetType().Name + ": " + reportError.Message + ")");
                }
            }
        }

        /// <summary>APIs ready, mods ready, each loaded mod's ready handlers, then the summary line. Never throws.</summary>
        private static void Finish(List<ModEntry> codeMods)
        {
            ModRuntime.MarkApisReady();
            ModRuntime.MarkModsReady();
            foreach (var entry in codeMods)
            {
                if (entry.State != ModState.Loaded || entry.Helper is null)
                {
                    continue;
                }

                try
                {
                    entry.Helper.RaiseModsReady();
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError("MODS " + entry.Id + ": mods-ready could not be raised: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            try
            {
                var all = Statuses;
                LastSummary = ModConsoleLines.Summary(all);
                ConsoleEvents.Emit(
                    ModConsoleLines.LoaderSystem,
                    "summary",
                    ModConsoleLines.ModsTag,
                    LastSummary,
                    "api,backends",
                    ("api", CwyApi.VersionText),
                    ("backends", ModBackends.Describe()),
                    ("codeMods", all.Count),
                    ("packs", ModRuntime.All.Count));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("MODS the summary could not be written: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Reserves every planned code mod's entry assembly name to that mod's own folder (review
        /// #15): the resolver never serves such a name from another folder, so a mod that bundles
        /// another mod's DLL cannot get the real one refused as already loaded.
        /// </summary>
        private static void ReserveEntryNames(List<ModEntry> codeMods)
        {
            lock (Gate)
            {
                ReservedFolders.Clear();
                ReservedOwners.Clear();
                foreach (var entry in codeMods)
                {
                    var name = EntryDllPath.SimpleName(entry.Candidate!.EntryDll);
                    if (string.IsNullOrEmpty(name) || ReservedOwners.ContainsKey(name))
                    {
                        continue;
                    }

                    string? folder;
                    try
                    {
                        folder = Path.GetDirectoryName(entry.Pack.FullPath(entry.Candidate.EntryDll));
                    }
                    catch (ArgumentException ex)
                    {
                        // Load() refuses this mod with the same message; nothing to reserve.
                        ConsoleEvents.File(ModConsoleLines.LoaderSystem, "reserve", ("mod", entry.Id), ("error", ex.Message));
                        continue;
                    }

                    if (string.IsNullOrEmpty(folder))
                    {
                        continue;
                    }

                    ReservedOwners[name] = entry.Id;
                    ReservedFolders[name] = folder!;
                }
            }
        }

        private static void Load(ModEntry entry)
        {
            var candidate = entry.Candidate!;
            var id = entry.Id;

            string path;
            try
            {
                path = entry.Pack.FullPath(candidate.EntryDll);
            }
            catch (ArgumentException ex)
            {
                Skip(entry, ModSkipReason.AssemblyLoadFailed, "entryDll is not a path inside the mod folder: " + ex.Message);
                return;
            }

            if (!File.Exists(path))
            {
                Skip(entry, ModSkipReason.AssemblyLoadFailed, "entryDll " + candidate.EntryDll + " no longer exists at " + path);
                return;
            }

            var loadedNames = LoadedAssemblyNames();
            var bundled = ModAssemblyRules.FindBundledShared(PackPaths(entry.Pack), candidate.EntryDll, SharedNames(loadedNames));
            if (bundled.Count > 0)
            {
                // Observed in testing: a stale copy is simply never used, so this is a warning,
                // not a refusal. The resolver below never serves a shared name from a mod folder.
                ConsoleEvents.EmitAt(
                    ModConsoleLines.LoaderSystem, "bundled", ModConsoleLines.ModsTag, ConsoleLevel.Warn,
                    id + ": ignoring its own copy of " + string.Join(", ", bundled) +
                    " (the copy the game, BepInEx, CWY:LS Mod Loader or an earlier mod provides is used); delete them and reference them with Private=false",
                    null,
                    ("mod", id),
                    ("files", string.Join(",", bundled)));
            }

            WarnBundledReserved(entry);

            string? simpleName;
            try
            {
                simpleName = AssemblyName.GetAssemblyName(path).Name;
            }
            catch (Exception ex)
            {
                Skip(entry, ModSkipReason.AssemblyLoadFailed,
                    "cannot read " + candidate.EntryDll + " as a .NET assembly: " + ex.GetType().Name + ": " + ex.Message, ex);
                return;
            }

            if (ModAssemblyRules.IsAlreadyLoaded(simpleName, loadedNames))
            {
                Skip(entry, ModSkipReason.AssemblyAlreadyLoaded,
                    "an assembly named " + simpleName + " is already loaded" + LocationOf(simpleName) +
                    "; is the mod installed twice, or also in BepInEx/plugins?");
                return;
            }

            Assembly assembly;
            try
            {
                assembly = LoadAssembly(path);
            }
            catch (Exception ex)
            {
                Skip(entry, ModSkipReason.AssemblyLoadFailed,
                    candidate.EntryDll + " did not load: " + ex.GetType().Name + ": " + ex.Message, ex);
                return;
            }

            var sameBuild = ModAssemblyRules.FindSameBuild(Mvid(assembly), LoadedMvids(assembly));
            if (sameBuild != null)
            {
                // Loaded, but never used: no type is touched, so no Harmony patch of it exists.
                Skip(entry, ModSkipReason.AssemblyAlreadyLoaded,
                    candidate.EntryDll + " is the same build as the already-loaded " + sameBuild +
                    "; two copies of one build would cross-wire their Harmony patches");
                return;
            }

            entry.Assembly = assembly;
            ModRuntime.MapAssembly(assembly, id);
            RegisterModFolder(id, Path.GetDirectoryName(path));

            var types = LoadableTypes(assembly, entry);
            var infos = new List<ModTypeInfo>(types.Count);
            foreach (var type in types)
            {
                infos.Add(Describe(type, id));
            }

            var selection = ModEntryTypeSelector.Select(infos, Path.GetFileName(path));
            if (!selection.Found)
            {
                Skip(entry, selection.Reason!.Value, selection.Detail + ForeignBaseHint(types));
                return;
            }

            var entryType = types[selection.Index];
            entry.EntryTypeName = entryType.FullName ?? entryType.Name;

            CwyMod mod;
            try
            {
                mod = (CwyMod)Activator.CreateInstance(entryType, nonPublic: true);
            }
            catch (Exception ex)
            {
                Fail(entry, ModStage.Construct, ex);
                return;
            }

            var helper = new ModHelper(entry);
            entry.Helper = helper;
            entry.Instance = mod;
            mod.Attach(helper);

            var clock = Stopwatch.StartNew();
            try
            {
                mod.Entry(helper);
            }
            catch (Exception ex)
            {
                Fail(entry, ModStage.Entry, ex);
                helper.Deactivate("failed in Entry");
                return;
            }

            clock.Stop();
            entry.EntryMs = clock.Elapsed.TotalMilliseconds;
            entry.Status = new ModStatus(id, entry.Pack.Manifest.Version, ModState.Loaded, detail: entry.EntryTypeName);

            ConsoleEvents.Emit(
                ModConsoleLines.LoaderSystem,
                "loaded",
                ModConsoleLines.ModsTag,
                entry.Pack.Manifest + " loaded",
                "type,ms",
                ("mod", id),
                ("type", entry.EntryTypeName),
                ("ms", Math.Round(entry.EntryMs, 1)),
                ("assembly", assembly.FullName),
                ("mode", AssemblyLoadMode.ToString()),
                ("path", path));
        }

        /// <summary>
        /// One warning when a mod ships another mod's entry DLL: that copy is never
        /// loaded, and the mod should declare the other one in <c>dependsOn</c> instead.
        /// </summary>
        private static void WarnBundledReserved(ModEntry entry)
        {
            IReadOnlyList<KeyValuePair<string, string>> copies;
            lock (Gate)
            {
                copies = ModAssemblyRules.FindBundledReserved(PackPaths(entry.Pack), entry.Candidate!.EntryDll, entry.Id, ReservedOwners);
            }

            if (copies.Count == 0)
            {
                return;
            }

            var described = new List<string>(copies.Count);
            var owners = new List<string>(copies.Count);
            foreach (var copy in copies)
            {
                described.Add(copy.Key + " (the entry DLL of " + copy.Value + ")");
                if (!owners.Contains(copy.Value))
                {
                    owners.Add(copy.Value);
                }
            }

            ConsoleEvents.EmitAt(
                ModConsoleLines.LoaderSystem, "bundledMod", ModConsoleLines.ModsTag, ConsoleLevel.Warn,
                entry.Id + ": ignoring its copy of " + string.Join(", ", described) +
                "; declare " + string.Join(", ", owners) + " in dependsOn and reference it with Private=false",
                null,
                ("mod", entry.Id),
                ("files", string.Join(",", described)));
        }

        /// <summary>Calls every loaded mod's <c>GetApi</c>, isolated, after all of them entered.</summary>
        private static void CollectApis(List<ModEntry> codeMods)
        {
            foreach (var entry in codeMods)
            {
                if (entry.State != ModState.Loaded || entry.Instance is null)
                {
                    continue;
                }

                object? api = null;
                if (ModFailures.Guard(entry.Id, ModStage.GetApi, null, () => api = entry.Instance.GetApi()) && api != null)
                {
                    entry.Api = api;
                    ConsoleEvents.File(
                        ModConsoleLines.LoaderSystem, "api",
                        ("mod", entry.Id),
                        ("type", api.GetType().FullName));
                }
            }
        }

        private static void Skip(ModEntry entry, ModSkipReason reason, string detail, Exception? error = null)
        {
            entry.Status = new ModStatus(entry.Id, entry.Pack.Manifest.Version, ModState.Skipped, reason, detail: detail);
            ConsoleEvents.EmitAt(
                ModConsoleLines.LoaderSystem,
                "skipped",
                ModConsoleLines.ModsTag,
                ConsoleLevel.Warn,
                entry.Id + " skipped: " + reason + " - " + ModConsoleLines.OneLine(detail),
                null,
                ("mod", entry.Id),
                ("reason", reason.ToString()),
                ("detail", detail),
                ("stack", error?.ToString()));
        }

        private static void Fail(ModEntry entry, ModStage stage, Exception error)
        {
            var ex = ModFailures.Unwrap(error);
            ModFailures.Report(entry.Id, stage, entry.EntryTypeName, ex);
            entry.Status = new ModStatus(
                entry.Id, entry.Pack.Manifest.Version, ModState.Failed,
                stage: stage, detail: ex.GetType().Name + ": " + ModConsoleLines.OneLine(ex.Message));
        }

        private static Assembly LoadAssembly(string path)
        {
            if (AssemblyLoadMode != ModAssemblyLoadMode.Bytes)
            {
                return Assembly.LoadFrom(path);
            }

            var bytes = File.ReadAllBytes(path);
            var symbols = Path.ChangeExtension(path, ".pdb");
            return File.Exists(symbols) ? Assembly.Load(bytes, File.ReadAllBytes(symbols)) : Assembly.Load(bytes);
        }

        private static List<Type> LoadableTypes(Assembly assembly, ModEntry entry)
        {
            var modId = entry.Id;
            try
            {
                return new List<Type>(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaded = new List<Type>();
                foreach (var type in ex.Types)
                {
                    if (type != null)
                    {
                        loaded.Add(type);
                    }
                }

                var first = ex.LoaderExceptions.Length > 0 && ex.LoaderExceptions[0] != null
                    ? ex.LoaderExceptions[0]!.GetType().Name + ": " + ex.LoaderExceptions[0]!.Message
                    : "unknown";

                // Name the likely cause: an optional dependency that is not running is
                // far more common than a framework mismatch.
                var hint = ModExceptions.TypeLoadHint(
                    ModExceptions.MissingAssemblies(ex.LoaderExceptions),
                    OptionalDependenciesNotRunning(entry));
                ConsoleEvents.EmitAt(
                    ModConsoleLines.LoaderSystem, "typeLoad", ModConsoleLines.ModsTag, ConsoleLevel.Warn,
                    modId + ": " + (ex.Types.Length - loaded.Count) + " type(s) could not load (" +
                    ModConsoleLines.OneLine(first) + "); " + hint,
                    null,
                    ("mod", modId),
                    ("stack", ex.ToString()));
                return loaded;
            }
        }

        /// <summary>Ids of the mod's optional dependencies that are not installed, or are code mods that did not load.</summary>
        private static List<string> OptionalDependenciesNotRunning(ModEntry entry)
        {
            var ids = new List<string>();
            foreach (var dependency in entry.Pack.Manifest.DependsOn)
            {
                if (!dependency.IsOptional)
                {
                    continue;
                }

                var other = ModRuntime.Find(dependency.Id);
                if (other is null || (other.IsCodeMod && other.State != ModState.Loaded))
                {
                    ids.Add(dependency.Id);
                }
            }

            return ids;
        }

        private static ModTypeInfo Describe(Type type, string modId)
        {
            try
            {
                var isMod = type != typeof(CwyMod) && typeof(CwyMod).IsAssignableFrom(type);
                var hasConstructor = type.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null;
                return new ModTypeInfo(type.FullName ?? type.Name, isMod, type.IsAbstract, type.IsGenericTypeDefinition, hasConstructor);
            }
            catch (Exception ex)
            {
                // A type whose base or members cannot resolve: it cannot be the entry, and the
                // trace file says why.
                ConsoleEvents.File(
                    ModConsoleLines.LoaderSystem, "typeSkipped",
                    ("mod", modId), ("type", type.FullName), ("error", ex.GetType().Name + ": " + ex.Message));
                return new ModTypeInfo(type.FullName ?? type.Name, false, false, false, false);
            }
        }

        /// <summary>
        /// The classic mistake, named: a class that derives from a <c>CwyMod</c> of another copy
        /// of CwyLs.ModLoader.dll (the mod shipped or bound to its own copy).
        /// </summary>
        private static string ForeignBaseHint(List<Type> types)
        {
            foreach (var type in types)
            {
                try
                {
                    for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
                    {
                        if (baseType.FullName == typeof(CwyMod).FullName && baseType != typeof(CwyMod))
                        {
                            return "; " + type.FullName + " derives from a CwyMod in another copy of CwyLs.ModLoader (" +
                                   baseType.Assembly.Location + "), so it cannot be the entry";
                        }
                    }
                }
                catch (Exception ex)
                {
                    ConsoleEvents.File(
                        ModConsoleLines.LoaderSystem, "typeSkipped",
                        ("type", type.FullName), ("error", ex.GetType().Name + ": " + ex.Message));
                }
            }

            return string.Empty;
        }

        private static List<string> PackPaths(LoadedPack pack)
        {
            var paths = new List<string>(pack.Index.Files.Count);
            foreach (var file in pack.Index.Files)
            {
                paths.Add(file.Path);
            }

            return paths;
        }

        private static List<string> LoadedAssemblyNames()
        {
            var names = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var name = assembly.GetName().Name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    ConsoleEvents.File(ModConsoleLines.LoaderSystem, "assemblyName", ("error", ex.GetType().Name + ": " + ex.Message));
                }
            }

            return names;
        }

        private static string LocationOf(string? simpleName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return string.IsNullOrEmpty(assembly.Location) ? " (in memory)" : " from " + assembly.Location;
                    }
                }
                catch (Exception ex)
                {
                    ConsoleEvents.File(ModConsoleLines.LoaderSystem, "assemblyName", ("error", ex.GetType().Name + ": " + ex.Message));
                }
            }

            return string.Empty;
        }

        private static void RegisterModFolder(string modId, string? folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            lock (Gate)
            {
                ModFolderById[modId] = folder!;
                ModIdByFolder[folder!] = modId;
                if (!ModFolders.Contains(folder!))
                {
                    ModFolders.Add(folder!);
                }
            }
        }

        private static Guid Mvid(Assembly assembly)
        {
            try
            {
                return assembly.ManifestModule.ModuleVersionId;
            }
            catch (Exception ex)
            {
                ConsoleEvents.File(ModConsoleLines.LoaderSystem, "mvid", ("assembly", assembly.FullName), ("error", ex.GetType().Name + ": " + ex.Message));
                return Guid.Empty;
            }
        }

        private static List<KeyValuePair<string, Guid>> LoadedMvids(Assembly except)
        {
            var mvids = new List<KeyValuePair<string, Guid>>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ReferenceEquals(assembly, except))
                {
                    continue;
                }

                try
                {
                    if (assembly.IsDynamic)
                    {
                        continue;
                    }

                    mvids.Add(new KeyValuePair<string, Guid>(assembly.GetName().Name ?? "?", assembly.ManifestModule.ModuleVersionId));
                }
                catch (Exception ex)
                {
                    ConsoleEvents.File(ModConsoleLines.LoaderSystem, "mvid", ("error", ex.GetType().Name + ": " + ex.Message));
                }
            }

            return mvids;
        }

        /// <summary>
        /// Names that must never come from a mod folder beyond the fixed families: everything loaded
        /// now, plus every DLL the game's Managed folder, BepInEx's core folder and the plugins
        /// folder ship (listed once).
        /// </summary>
        private static HashSet<string> SharedNames(IEnumerable<string> loadedNames)
        {
            HashSet<string> files;
            lock (Gate)
            {
                files = _sharedFileNames ??= ListSharedFiles();
            }

            var names = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            foreach (var name in loadedNames)
            {
                names.Add(name);
            }

            return names;
        }

        private static HashSet<string> ListSharedFiles()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDlls(names, BepInEx.Paths.ManagedPath, SearchOption.TopDirectoryOnly);
            AddDlls(names, BepInEx.Paths.BepInExAssemblyDirectory, SearchOption.TopDirectoryOnly);
            AddDlls(names, BepInEx.Paths.PluginPath, SearchOption.AllDirectories);
            return names;
        }

        private static void AddDlls(HashSet<string> names, string? folder, SearchOption scope)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            try
            {
                foreach (var file in Directory.GetFiles(folder, "*.dll", scope))
                {
                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch (Exception ex)
            {
                ConsoleEvents.File(ModConsoleLines.LoaderSystem, "sharedList", ("folder", folder), ("error", ex.GetType().Name + ": " + ex.Message));
            }
        }

        private static void InstallResolver()
        {
            lock (Gate)
            {
                if (_resolverInstalled)
                {
                    return;
                }

                _resolverInstalled = true;
            }

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        /// <summary>
        /// Resolves a reference the runtime could not find, in <see cref="ModAssemblyResolver"/>'s
        /// order (see the class remarks). A DLL loaded from a mod folder is mapped to that mod, so
        /// its own references resolve from the same folder. Copies that are deliberately not served,
        /// and requests with several candidates and no known requester, get one WARN each. Deciding
        /// and loading run under one re-entrant lock, so two threads cannot load one name twice.
        /// Never throws.
        /// </summary>
        private static Assembly? Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = ModAssemblyResolver.SimpleName(args.Name);
                if (name.Length == 0 || name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                lock (ResolveGate)
                {
                    return ResolveLocked(args);
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("MODS could not resolve " + args.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static Assembly? ResolveLocked(ResolveEventArgs args)
        {
            var loadedNames = LoadedAssemblyNames();
            var requester = args.RequestingAssembly;
            var kind = requester is null ? ModAssemblyRequester.Unknown : ModAssemblyRequester.NotAMod;
            string? requesterFolder = null;
            string[] folders;
            Dictionary<string, string> reserved;
            lock (Gate)
            {
                folders = ModFolders.ToArray();
                reserved = new Dictionary<string, string>(ReservedFolders, StringComparer.OrdinalIgnoreCase);
                if (ModRuntime.TryFindByAssembly(requester, out var requesterMod))
                {
                    kind = ModAssemblyRequester.Mod;
                    ModFolderById.TryGetValue(requesterMod, out requesterFolder);
                }
            }

            var environment = new ModAssemblyEnvironment(
                loadedNames,
                folders,
                File.Exists,
                folders.Length == 0 || kind == ModAssemblyRequester.NotAMod ? null : SharedNames(loadedNames),
                reserved);
            var decision = ModAssemblyResolver.Decide(args.Name, kind, requesterFolder, environment);

            foreach (var ignored in decision.IgnoredSharedCopies)
            {
                WarnOnce(
                    ignored, "resolveIgnored", args.Name,
                    "not loading " + ignored + ": " + decision.Name + " is a framework, game or BepInEx assembly and is never taken from a mod folder");
            }

            foreach (var ignored in decision.IgnoredReservedCopies)
            {
                WarnOnce(
                    ignored, "resolveReserved", args.Name,
                    "not loading " + ignored + ": " + decision.Name + " is the entry DLL of " + ReservedOwnerOf(decision.Name) +
                    " and is only ever loaded from that mod's folder; declare it in dependsOn instead of shipping a copy");
            }

            if (decision.AmbiguousCandidates.Count > 0)
            {
                WarnOnce(
                    "ambiguous:" + decision.Name, "resolveAmbiguous", args.Name,
                    "not loading " + decision.Name + " for an unknown requester: " + decision.AmbiguousCandidates.Count +
                    " mod folders have it (" + string.Join(", ", decision.AmbiguousCandidates) + ")");
            }

            switch (decision.Source)
            {
                case ModAssemblySource.Loaded:
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(SafeName(assembly), decision.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            ConsoleEvents.File(ModConsoleLines.LoaderSystem, "resolve", ("name", args.Name), ("to", "loaded"));
                            return assembly;
                        }
                    }

                    return null;

                case ModAssemblySource.ModFolder:
                    var loaded = LoadAssembly(decision.Path!);
                    var owner = OwnerOfFolder(Path.GetDirectoryName(decision.Path!));
                    if (owner != null)
                    {
                        // Its own references resolve from the same folder, and a deprecation it
                        // triggers is attributed to the mod.
                        ModRuntime.MapAssembly(loaded, owner);
                    }

                    ConsoleEvents.File(
                        ModConsoleLines.LoaderSystem, "resolve",
                        ("name", args.Name), ("to", decision.Path), ("mod", owner),
                        ("requester", SafeName(requester)), ("requesterKind", kind.ToString()));
                    return loaded;

                default:
                    return null;
            }
        }

        private static string ReservedOwnerOf(string name)
        {
            lock (Gate)
            {
                return ReservedOwners.TryGetValue(name, out var modId) ? modId : "another mod";
            }
        }

        private static string? OwnerOfFolder(string? folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return null;
            }

            lock (Gate)
            {
                return ModIdByFolder.TryGetValue(folder!, out var modId) ? modId : null;
            }
        }

        private static void WarnOnce(string key, string kind, string requestedName, string message)
        {
            bool first;
            lock (Gate)
            {
                first = WarnedIgnoredCopies.Add(key);
            }

            if (first)
            {
                ConsoleEvents.EmitAt(
                    ModConsoleLines.LoaderSystem, kind, ModConsoleLines.ModsTag, ConsoleLevel.Warn,
                    message, null, ("name", requestedName), ("key", key));
            }
        }

        private static string? SafeName(Assembly? assembly)
        {
            if (assembly is null)
            {
                return null;
            }

            try
            {
                return assembly.GetName().Name;
            }
            catch (Exception ex)
            {
                ConsoleEvents.File(ModConsoleLines.LoaderSystem, "assemblyName", ("error", ex.GetType().Name + ": " + ex.Message));
                return null;
            }
        }

        /// <summary>
        /// Calls every loaded mod's <c>OnShutdown</c> in reverse load order and takes back its
        /// registrations. On <c>Application.quitting</c>; never throws.
        /// </summary>
        private static void Shutdown()
        {
            lock (Gate)
            {
                if (_shutDown)
                {
                    return;
                }

                _shutDown = true;
            }

            var entries = ModRuntime.All;
            var count = 0;
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry.State != ModState.Loaded || entry.Instance is null || entry.Helper is null)
                {
                    continue;
                }

                entry.Helper.Guard(ModStage.Shutdown, null, entry.Instance.OnShutdown);
                entry.Helper.Deactivate("the game is quitting");
                entry.Status = new ModStatus(entry.Id, entry.Pack.Manifest.Version, ModState.ShutDown, detail: entry.EntryTypeName);
                count++;
            }

            ConsoleEvents.File(ModConsoleLines.LoaderSystem, "shutdown", ("mods", count));
        }
    }
}
