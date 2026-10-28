using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Packs;
using UnityEngine;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Packs
{
    /// <summary>
    /// Discovers the content packs installed in <see cref="ModLoaderPaths.PacksRoot"/>, resolves them
    /// with <see cref="PackResolver"/>, and holds the result for the rest of the framework.
    /// <para>
    /// Static because packs are process-wide: CwyLs.ModLoader.Bulbul, the smoke plugin and any third
    /// party mod all need the same list, and they are separate BepInEx assemblies with no
    /// reference to one another.
    /// </para>
    /// <para>
    /// <b>Adding a pack needs a game restart.</b> Packs are read once, during
    /// <see cref="ModLoaderPlugin"/>'s Awake, before the game has loaded anything they might patch.
    /// <see cref="Rescan"/> exists for the smoke checks and for development; it refreshes this
    /// list but does <em>not</em> undo or re-apply anything a previous scan's packs already did.
    /// </para>
    /// <para>
    /// Every scan ends by raising <see cref="PacksResolved"/> with its <see cref="PackResolution"/>
    /// and then <see cref="PacksChanged"/>, the argument-free form kept for existing subscribers.
    /// </para>
    /// </summary>
    public static class PackManager
    {
        /// <summary>Config section holding the pack settings.</summary>
        public const string ConfigSection = "Packs";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the game-version override (ignores <c>minGameVersion</c>/<c>maxGameVersion</c>).</summary>
        public const string ConfigKeyIgnoreGameVersion = "IgnoreGameVersion";

        private static readonly object Gate = new object();
        private static readonly LoadedPack[] NoPacks = new LoadedPack[0];
        private static readonly SkippedPack[] NoSkips = new SkippedPack[0];

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<bool>? _ignoreGameVersion;
        private static ConfigFile? _ownConfig;

        private static IReadOnlyList<LoadedPack> _loaded = NoPacks;
        private static IReadOnlyList<SkippedPack> _skipped = NoSkips;
        private static PackResolution _resolution = PackResolution.Empty;

        /// <summary>
        /// Raised after every completed scan, including the first, with the resolution that scan
        /// produced - what loaded, in what order, what was refused and why, and the one-line
        /// <see cref="PackResolution.Summary"/>. Handlers run on the thread that scanned (the Unity
        /// main thread in practice) and a handler that throws is logged and skipped, never allowed
        /// to abort the scan or the handlers after it.
        /// <para>
        /// A scan that failed as a whole re-raises the previous resolution rather than an empty
        /// one, matching <see cref="Loaded"/>: a failed rescan loses nothing that already loaded.
        /// </para>
        /// </summary>
        public static event Action<PackResolution>? PacksResolved;

        /// <summary>
        /// The argument-free form of <see cref="PacksResolved"/>, raised immediately after it for
        /// subscribers that only need to know that something moved. Prefer
        /// <see cref="PacksResolved"/> in new code: it hands you the same resolution the framework
        /// logged, instead of making you re-read <see cref="Loaded"/> and hope nothing scanned in
        /// between.
        /// </summary>
        public static event Action? PacksChanged;

        /// <summary>
        /// The packs that loaded, in dependency order: a pack always appears after the packs it
        /// depends on. Never null; empty before the first scan.
        /// </summary>
        public static IReadOnlyList<LoadedPack> Loaded
        {
            get { lock (Gate) { return _loaded; } }
        }

        /// <summary>Packs that were found but refused, with reasons. Never null.</summary>
        public static IReadOnlyList<SkippedPack> Skipped
        {
            get { lock (Gate) { return _skipped; } }
        }

        /// <summary>
        /// Packs that loaded with a warning in the last scan (e.g. made for another game version),
        /// one entry per pack. Never null.
        /// </summary>
        public static IReadOnlyList<PackWarning> Warnings => LastResolution.Warnings;

        /// <summary>
        /// The resolution of the last completed scan - the same object the last
        /// <see cref="PacksResolved"/> carried. Never null; <see cref="PackResolution.Empty"/>
        /// before the first scan and while pack loading is switched off.
        /// </summary>
        public static PackResolution LastResolution
        {
            get { lock (Gate) { return _resolution; } }
        }

        /// <summary>True once a scan has completed, successfully or not.</summary>
        public static bool HasScanned { get; private set; }

        /// <summary>
        /// The failure of the last scan, or null when it completed. A scan only fails as a whole
        /// for something outside any one pack - an unreadable <c>CWYLS Mods</c> folder, say; a single
        /// bad pack is a <see cref="SkippedPack"/>, not a scan failure.
        /// </summary>
        public static string? LastScanError { get; private set; }

        /// <summary>
        /// This framework's API version (<c>CwyApi.Version</c>), as compared against a
        /// pack's or code mod's <c>loaderVersion</c>.
        /// </summary>
        public static SemVer? CoreVersion { get; } = Core.Mods.CwyApi.Version;

        /// <summary>True when pack loading is switched on (<c>Packs.Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>
        /// True when a pack is loaded even though the running game is outside its
        /// <c>minGameVersion</c>/<c>maxGameVersion</c> range (<c>Packs.IgnoreGameVersion</c>,
        /// default false); it then logs a warning instead of being skipped. A different
        /// <c>gameVersion</c> alone never skips a pack, with or without this switch.
        /// </summary>
        public static bool IgnoreGameVersion => _ignoreGameVersion?.Value ?? false;

        /// <summary>
        /// Binds the config, registers the P1 smoke checks and performs the first scan. Called once
        /// from <see cref="ModLoaderPlugin"/>'s Awake; calling it again simply rescans.
        /// </summary>
        /// <param name="config">
        /// Config file the <c>Packs.*</c> entries are bound in. Pass the calling plugin's
        /// <c>Config</c>; when omitted the framework binds them in its own .cfg.
        /// </param>
        public static void DiscoverAndLoad(ConfigFile? config = null)
        {
            BindConfig(config);
            P1PackChecks.RegisterAll();
            Scan("startup");
        }

        /// <summary>
        /// Repeats the scan and refreshes <see cref="Loaded"/> and <see cref="Skipped"/>. For the
        /// smoke checks and for development only - see the note on the class about restarting.
        /// </summary>
        /// <returns>True when the scan completed; false when it failed (see <see cref="LastScanError"/>).</returns>
        public static bool Rescan() => Scan("rescan", quiet: false);

        /// <summary>
        /// Repeats the scan, optionally without warning about the packs it skips.
        /// </summary>
        /// <param name="quiet">
        /// True to report skipped packs at Info instead of Warning. For a caller that planted the
        /// broken pack on purpose: <c>p1.packs.bad_manifest_skipped</c> writes a truncated
        /// manifest to prove the resolver refuses it, and the resulting
        /// <c>WARN pack skipped: _smoke_bad (InvalidManifest)</c> was the check working, read as
        /// the framework failing. A real startup scan stays loud.
        /// </param>
        /// <returns>True when the scan completed; false when it failed (see <see cref="LastScanError"/>).</returns>
        public static bool Rescan(bool quiet) => Scan("rescan", quiet);

        /// <summary>The loaded pack with this id, or null when it is not loaded.</summary>
        /// <param name="id">A pack id, e.g. <c>cwy.example</c>.</param>
        public static LoadedPack? Find(string? id)
        {
            if (id is null)
            {
                return null;
            }

            foreach (var pack in Loaded)
            {
                if (string.Equals(pack.Manifest.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return pack;
                }
            }

            return null;
        }

        /// <summary>The skip record for this id or folder name, or null when it was not skipped.</summary>
        /// <param name="id">A pack id, or the folder name of a pack whose manifest never parsed.</param>
        public static SkippedPack? FindSkipped(string? id)
        {
            if (id is null)
            {
                return null;
            }

            foreach (var skip in Skipped)
            {
                if (string.Equals(skip.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return skip;
                }
            }

            return null;
        }

        private static void BindConfig(ConfigFile? config)
        {
            lock (Gate)
            {
                if (_enabled != null)
                {
                    return;
                }

                var file = config ?? OwnConfig();

                _enabled = file.Bind(
                    ConfigSection,
                    ConfigKeyEnabled,
                    true,
                    "Load content packs from <game root>/CWYLS Mods. Off loads none of them.");

                _ignoreGameVersion = file.Bind(
                    ConfigSection,
                    ConfigKeyIgnoreGameVersion,
                    false,
                    "Load packs even when the running game is outside their minGameVersion/maxGameVersion " +
                    "range (they log a warning instead of being skipped). A different gameVersion never " +
                    "skips a pack; it only warns. For testing a pack on a game build it does not claim " +
                    "to support; it may patch the wrong thing.");
            }
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(BepInEx.Paths.ConfigPath, CwyLsModLoader.PluginGuid + ".packs.cfg"), saveOnInit: true);

        private static bool Scan(string trigger) => Scan(trigger, quiet: false);

        private static bool Scan(string trigger, bool quiet)
        {
            var ok = true;
            PackResolution resolution;

            try
            {
                lock (Gate)
                {
                    if (!Enabled)
                    {
                        resolution = PackResolution.Empty;
                        _loaded = NoPacks;
                        _skipped = NoSkips;
                        LastScanError = null;
                        ConsoleEvents.Emit(
                            "packs", "scan", ConsoleTags.Packs,
                            $"disabled ({ConfigSection}.{ConfigKeyEnabled}=false)", null);
                    }
                    else
                    {
                        resolution = PackResolver.Resolve(
                            ReadSources(), Application.version, CoreVersion, IgnoreGameVersion);

                        _loaded = ToLoadedPacks(resolution);
                        _skipped = resolution.Skipped;
                        LastScanError = null;

                        ConsoleEvents.Emit(
                            "packs",
                            "scan",
                            ConsoleTags.Packs,
                            resolution.Summary,
                            "loaded,skipped",
                            ("loaded", _loaded.Count),
                            ("skipped", resolution.Skipped.Count),
                            ("warned", resolution.Warnings.Count));
                        ReportWarnings(resolution, quiet);
                        foreach (var skip in resolution.Skipped)
                        {
                            var line = $"pack skipped: {skip.Id} ({skip.Reason}) - {skip.Detail}";

                            // Still reported either way; a caller that planted the bad pack does
                            // not get to hide it, only to stop it reading as a real problem.
                            if (quiet)
                            {
                                CwyLsModLoader.Log.LogDebug(line + " [expected: rescan asked for quietly]");
                            }
                            else
                            {
                                CwyLsModLoader.Log.LogWarning(line);
                            }
                        }
                    }

                    _resolution = resolution;
                }
            }
            catch (Exception ex)
            {
                // A scan that dies must leave the framework usable: the previous list is kept and
                // the failure is both logged and readable from LastScanError (the p1.packs.scan_ok
                // smoke check reads it).
                ok = false;
                LastScanError = ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError($"pack scan failed ({trigger}): {LastScanError}");

                // Subscribers are told what the framework still holds, not an empty list that would
                // read as "every pack went away".
                resolution = LastResolution;
            }

            HasScanned = true;
            RaisePacksChanged(trigger, resolution);
            return ok;
        }

        /// <summary>
        /// One <c>PACKS</c> line at warning level per loaded pack that has something to say, e.g.
        /// <c>my.pack: made for game 1.17.2, running 1.17.3 (newer patch version); loaded anyway</c>.
        /// A quiet rescan (the smoke check that plants a broken pack) keeps them in the trace file
        /// only, so every smoke run does not repeat the startup warnings on the console.
        /// </summary>
        private static void ReportWarnings(PackResolution resolution, bool quiet)
        {
            foreach (var warning in resolution.Warnings)
            {
                var check = resolution.Find(warning.Id)?.GameVersionCheck;
                var fields = new (string Key, object? Value)[]
                {
                    ("id", warning.Id),
                    ("folder", warning.FolderName),
                    ("status", check?.Status.ToString()),
                    ("difference", check?.Difference?.ToString()),
                    ("gameVersion", check?.MadeFor),
                    ("running", check?.Running),
                    ("detail", warning.Message),
                };

                if (quiet)
                {
                    ConsoleEvents.File("packs", "warning", fields);
                }
                else
                {
                    ConsoleEvents.EmitAt(
                        "packs", "warning", ConsoleTags.Packs, ConsoleLevel.Warn, warning.ToString(), null, fields);
                }
            }
        }

        private static List<IPackSource> ReadSources()
        {
            var root = ModLoaderPaths.PacksRoot;
            var sources = new List<IPackSource>();

            if (!Directory.Exists(root))
            {
                return sources;
            }

            var folders = Directory.GetDirectories(root);
            Array.Sort(folders, StringComparer.Ordinal);

            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);

                // Hidden folders are the OS's business, not a pack.
                if (string.IsNullOrEmpty(name) || name[0] == '.')
                {
                    continue;
                }

                sources.Add(DirectoryPackSource.Read(folder));
            }

            return sources;
        }

        private static IReadOnlyList<LoadedPack> ToLoadedPacks(PackResolution resolution)
        {
            if (resolution.Loaded.Count == 0)
            {
                return NoPacks;
            }

            var packs = new List<LoadedPack>(resolution.Loaded.Count);
            foreach (var resolved in resolution.Loaded)
            {
                var rootPath = resolved.Source is DirectoryPackSource directory
                    ? directory.RootPath
                    : Path.Combine(ModLoaderPaths.PacksRoot, resolved.Source.FolderName);

                packs.Add(new LoadedPack(resolved.Manifest, rootPath, resolved.Index));
            }

            return packs;
        }

        private static void RaisePacksChanged(string trigger, PackResolution resolution)
        {
            var resolved = PacksResolved;
            if (resolved != null)
            {
                foreach (var handler in resolved.GetInvocationList())
                {
                    try
                    {
                        ((Action<PackResolution>)handler)(resolution);
                    }
                    catch (Exception ex)
                    {
                        CwyLsModLoader.Log.LogError(
                            $"a PacksResolved handler threw after {trigger}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            var changed = PacksChanged;
            if (changed is null)
            {
                return;
            }

            foreach (var handler in changed.GetInvocationList())
            {
                try
                {
                    ((Action)handler)();
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        $"a PacksChanged handler threw after {trigger}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
