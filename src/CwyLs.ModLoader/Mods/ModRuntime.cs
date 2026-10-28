using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Packs;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// A manifest as mods see it: a read-only view over the Core <see cref="PackManifest"/>.
    /// <see cref="DependsOn"/> and <see cref="Extensions"/> are this view's own read-only copies,
    /// and the parsed JSON values inside <see cref="Extensions"/> are deep copies, so no mod can
    /// change what the framework or another mod reads (API freeze).
    /// </summary>
    internal sealed class ModManifestView : ICwyModManifest
    {
        private readonly PackManifest _manifest;
        private readonly IReadOnlyList<PackDependency> _dependsOn;
        private readonly Lazy<IReadOnlyDictionary<string, object?>> _extensions;

        internal ModManifestView(PackManifest manifest)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _dependsOn = new ReadOnlyCollection<PackDependency>(new List<PackDependency>(manifest.DependsOn));
            _extensions = new Lazy<IReadOnlyDictionary<string, object?>>(() => CopyExtensions(manifest.Extensions));
        }

        public string Id => _manifest.Id;

        public string Name => _manifest.Name;

        public SemVer Version => _manifest.Version;

        public string? Author => _manifest.Author;

        public string? Description => _manifest.Description;

        public string? GameVersion => _manifest.GameVersion;

        public SemVer? LoaderVersion => _manifest.LoaderVersion;

        public string? EntryDll => _manifest.EntryDll;

        public PackDependency? ContentPackFor => _manifest.ContentPackFor;

        public IReadOnlyList<PackDependency> DependsOn => _dependsOn;

        public IReadOnlyDictionary<string, object?> Extensions => _extensions.Value;

        public override string ToString() => _manifest.ToString();

        private static IReadOnlyDictionary<string, object?> CopyExtensions(IReadOnlyDictionary<string, object?> source)
        {
            var copy = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
            foreach (var pair in source)
            {
                copy[pair.Key] = DeepCopy(pair.Value);
            }

            return new ReadOnlyDictionary<string, object?>(copy);
        }

        /// <summary>A deep copy of a parsed JSON value: objects and arrays are rebuilt, scalars are immutable already.</summary>
        private static object? DeepCopy(object? value)
        {
            switch (value)
            {
                case JsonObject obj:
                    var objectCopy = new JsonObject(obj.Count);
                    foreach (var member in obj)
                    {
                        objectCopy.Add(member.Key, DeepCopy(member.Value));
                    }

                    return objectCopy;

                case IList<object?> array:
                    var arrayCopy = new List<object?>(array.Count);
                    foreach (var item in array)
                    {
                        arrayCopy.Add(DeepCopy(item));
                    }

                    return arrayCopy;

                default:
                    return value;
            }
        }
    }

    /// <summary>One resolved pack or code mod, with everything the loader learned about it.</summary>
    internal sealed class ModEntry : ICwyModInfo
    {
        private readonly object _gate = new object();
        private ModStatus _status;

        internal ModEntry(LoadedPack pack, ModCandidate? candidate)
        {
            Pack = pack ?? throw new ArgumentNullException(nameof(pack));
            Candidate = candidate;
            ManifestView = new ModManifestView(pack.Manifest);
            _status = new ModStatus(pack.Id, pack.Manifest.Version, candidate is null ? ModState.Loaded : ModState.Pending);
        }

        /// <summary>The installed pack.</summary>
        internal LoadedPack Pack { get; }

        /// <summary>The loader's plan entry; null for a content pack.</summary>
        internal ModCandidate? Candidate { get; }

        internal ModManifestView ManifestView { get; }

        /// <summary>The loader's verdict. Content packs are Loaded from the start.</summary>
        internal ModStatus Status
        {
            get { lock (_gate) { return _status; } }
            set { lock (_gate) { _status = value ?? throw new ArgumentNullException(nameof(value)); } }
        }

        internal CwyMod? Instance { get; set; }

        internal ModHelper? Helper { get; set; }

        internal Assembly? Assembly { get; set; }

        internal string EntryTypeName { get; set; } = string.Empty;

        internal double EntryMs { get; set; }

        internal object? Api { get; set; }

        internal int Failures;

        public string Id => Pack.Id;

        public ICwyModManifest Manifest => ManifestView;

        public string DirectoryPath => Pack.RootPath;

        public bool IsCodeMod => Pack.Manifest.IsCodeMod;

        public ModState State => Status.State;

        public string Detail => Status.Detail;

        public override string ToString() => Status.ToString();
    }

    /// <summary>
    /// The process-wide table of resolved packs and code mods for the mod API. Filled once by
    /// <see cref="ModLoader"/> on the first frame; read by every helper's registry view, by the
    /// console and by the deprecation notifier.
    /// </summary>
    internal static class ModRuntime
    {
        private static readonly object Gate = new object();
        private static readonly List<ModEntry> Entries = new List<ModEntry>();
        private static readonly Dictionary<string, ModEntry> ById = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Assembly, string> ByAssembly = new Dictionary<Assembly, string>();

        /// <summary>True once every mod has entered and <c>GetApi</c> has been collected.</summary>
        internal static bool ApisReady { get; private set; }

        /// <summary>True once the mods-ready handlers have run.</summary>
        internal static bool ModsReadyRaised { get; private set; }

        /// <summary>Every resolved pack and code mod, in load order.</summary>
        internal static IReadOnlyList<ModEntry> All
        {
            get { lock (Gate) { return Entries.ToArray(); } }
        }

        /// <summary>Creates the entries from the resolved packs. Called once by the loader.</summary>
        /// <param name="packs">The loaded packs, in dependency order.</param>
        /// <param name="plan">The code mods among them.</param>
        internal static void Fill(IReadOnlyList<LoadedPack> packs, IReadOnlyList<ModCandidate> plan)
        {
            var candidates = new Dictionary<string, ModCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in plan)
            {
                candidates[candidate.Id] = candidate;
            }

            lock (Gate)
            {
                Entries.Clear();
                ById.Clear();
                foreach (var pack in packs)
                {
                    candidates.TryGetValue(pack.Id, out var candidate);
                    var entry = new ModEntry(pack, candidate);
                    Entries.Add(entry);
                    ById[pack.Id] = entry;
                }
            }
        }

        /// <summary>The entry with this id, or null.</summary>
        /// <param name="id">A pack or mod id.</param>
        internal static ModEntry? Find(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            lock (Gate)
            {
                return ById.TryGetValue(id!, out var entry) ? entry : null;
            }
        }

        /// <summary>Remembers which mod an assembly belongs to, for deprecation attribution.</summary>
        internal static void MapAssembly(Assembly assembly, string modId)
        {
            lock (Gate)
            {
                ByAssembly[assembly] = modId;
            }
        }

        /// <summary>The mod an assembly belongs to.</summary>
        internal static bool TryFindByAssembly(Assembly? assembly, out string modId)
        {
            modId = string.Empty;
            if (assembly is null)
            {
                return false;
            }

            lock (Gate)
            {
                if (ByAssembly.TryGetValue(assembly, out var id))
                {
                    modId = id;
                    return true;
                }
            }

            return false;
        }

        internal static void MarkApisReady() => ApisReady = true;

        internal static void MarkModsReady() => ModsReadyRaised = true;

        /// <summary>Counts a failure against a mod (shown by <c>mods list</c>).</summary>
        internal static void NoteFailure(string modId)
        {
            var entry = Find(modId);
            if (entry != null)
            {
                System.Threading.Interlocked.Increment(ref entry.Failures);
            }
        }
    }

    /// <summary>
    /// Every exception a mod throws into the framework ends here: one <c>MODS</c> console line at
    /// error level naming the mod, the call site and the exception; the full stack goes to the
    /// trace file in the same event. Repeats at one site are rate-limited.
    /// </summary>
    internal static class ModFailures
    {
        private static readonly ModFailureLimiter Limiter = new ModFailureLimiter();

        /// <summary>Reports one failure. Never throws.</summary>
        /// <param name="modId">The mod.</param>
        /// <param name="stage">Where.</param>
        /// <param name="label">Which handler, command or work item; null when the stage says it all.</param>
        /// <param name="error">What it threw.</param>
        internal static void Report(string modId, ModStage stage, string? label, Exception error)
        {
            try
            {
                var ex = Unwrap(error);
                ModRuntime.NoteFailure(modId);

                // A bounded key: labels that carry data ("chunk 17") share one site.
                if (!Limiter.ShouldLog(ModFailureLimiter.SiteKey(modId, stage.ToString(), label), MainThread.Now, out var suppressed))
                {
                    return;
                }

                ConsoleEvents.EmitAt(
                    ModConsoleLines.LoaderSystem,
                    "failed",
                    ModConsoleLines.ModsTag,
                    ConsoleLevel.Error,
                    ModConsoleLines.Failure(modId, stage, label, ex.GetType().Name, ex.Message, suppressed),
                    null,
                    ("mod", modId),
                    ("stage", stage.ToString()),
                    ("label", label),
                    ("error", ex.GetType().FullName),
                    ("suppressed", suppressed),
                    ("stack", ex.ToString()));
            }
            catch (Exception reportError)
            {
                // The reporter itself failed (the bus, a formatter). The plain log still works.
                CwyLsModLoader.Log.LogError(
                    "MODS " + modId + " failed in " + stage + " and the report failed too: " +
                    error.GetType().Name + ": " + error.Message + " / " + reportError.GetType().Name + ": " + reportError.Message);
            }
        }

        /// <summary>Runs mod code, reporting any exception against the mod.</summary>
        /// <returns>True when it returned normally.</returns>
        internal static bool Guard(string modId, ModStage stage, string? label, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                Report(modId, stage, label, ex);
                return false;
            }
        }

        /// <summary>
        /// Routes a failure of mod-owned main-thread work (owner <c>mod:&lt;id&gt;</c>). Installed as
        /// <see cref="MainThread.ModFailureRoute"/> by the loader.
        /// </summary>
        /// <returns>True when the owner was a mod and the failure was reported.</returns>
        internal static bool RouteMainThread(Core.Threading.MainThreadFailure failure)
        {
            if (!failure.Owner.StartsWith(MainThread.ModOwnerPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            Report(
                failure.Owner.Substring(MainThread.ModOwnerPrefix.Length),
                failure.IsTicker ? ModStage.Tick : ModStage.MainThread,
                failure.Label,
                failure.Error);
            return true;
        }

        /// <summary>
        /// Reflection wraps constructor and invoke exceptions, and a throwing static constructor
        /// surfaces as <see cref="TypeInitializationException"/>; the author wants the inner one
        /// (<see cref="ModExceptions.Unwrap"/>).
        /// </summary>
        internal static Exception Unwrap(Exception error) => ModExceptions.Unwrap(error);
    }
}
