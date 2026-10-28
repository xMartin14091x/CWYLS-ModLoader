using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Loaders;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;
using CwyLs.ModLoader.Loaders;
using CwyLs.ModLoader.Packs;
using HarmonyLib;
using UnityEngine;
using Game = global::Bulbul;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// Replaces and adds the game's voice lines, from content packs and from other mods.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pack file <c>audio/voice/&lt;VoiceName&gt;.ogg</c> (or <c>.wav</c>) is registered under
    /// its file name without the extension. If the game already has a line by that name, ours is
    /// played instead; if it does not, the name simply becomes a new line that anything able to
    /// call <c>VoiceManager.Play</c> can use. Both come from the same place: a Harmony prefix on
    /// the clip lookup (<see cref="VoicePatches"/>).
    /// </para>
    /// <para>
    /// The runtime half - <see cref="Register"/>, <see cref="Unregister"/>,
    /// <see cref="PlayAsSatone"/> - is the seam for a mod that generates speech: make a clip, give
    /// it a name, ask Satone to say it. Her mouth animation comes from the game's own
    /// <c>HeroineService.PlayVoice</c>, so a line played that way looks like a line the game shipped.
    /// </para>
    /// <para>
    /// Static throughout, and initialised from the plugin's Awake, because this game destroys
    /// BepInEx plugin components about 0.2 s after boot. Nothing here lives on
    /// a component; the Ogg decode uses the framework's own DontDestroyOnLoad coroutine host
    /// (<c>LoaderHost</c>).
    /// </para>
    /// </remarks>
    public static class VoiceFeature
    {
        /// <summary>Config section holding the voice settings.</summary>
        public const string ConfigSection = "Voice";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the pack preload switch.</summary>
        public const string ConfigKeyPreloadPackClips = "PreloadPackClips";

        /// <summary>Config key of the per-clip log switch.</summary>
        public const string ConfigKeyLogEachRegistration = "LogEachRegistration";

        /// <summary>The <c>sys</c> tag this feature's diagnostic events are published under.</summary>
        public const string TraceSys = "voice";

        /// <summary>The <see cref="VoiceEntry.Source"/> a mod's registration gets by default.</summary>
        public const string RuntimeSource = VoiceRegistry.RuntimeSource;

        /// <summary>How many names a log or smoke line lists before saying "and N more".</summary>
        private const int MaxListedNames = 12;

        private static readonly object Gate = new object();
        private static readonly VoiceRegistry Registry = new VoiceRegistry();

        private static Harmony? _harmony;
        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<bool>? _preloadPackClips;
        private static ConfigEntry<bool>? _logEachRegistration;
        private static ConfigEntry<bool>? _languageVoices;
        private static ConfigFile? _ownConfig;
        private static bool _initialized;
        private static Game.HeroineService? _heroine;
        private static string _heroineVia = string.Empty;

        /// <summary>True when the feature is switched on (<c>Voice.Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>True when pack clips are loaded at startup (<c>Voice.PreloadPackClips</c>, default true).</summary>
        public static bool PreloadPackClips => _preloadPackClips?.Value ?? true;

        /// <summary>True when every registration is logged (<c>Voice.LogEachRegistration</c>, default true).</summary>
        public static bool LogEachRegistration => _logEachRegistration?.Value ?? true;

        /// <summary>
        /// True when packs' language voice sets (<c>audio/voice/&lt;lang&gt;/</c>) replace the game's
        /// clips while that language is selected (<c>Voice.LanguageVoices</c>, default true). Read
        /// once at startup; see <see cref="LanguageVoices"/>.
        /// </summary>
        public static bool LanguageVoicesEnabled => _languageVoices?.Value ?? true;

        /// <summary>True once the clip-lookup prefix is in place.</summary>
        public static bool PatchApplied => VoicePatches.Applied;

        /// <summary>What was patched, or why it could not be. Never null.</summary>
        public static string PatchDetail => VoicePatches.Detail;

        /// <summary>How many voice names are registered.</summary>
        public static int Count => Registry.Count;

        /// <summary>The registered voice names, ordinally sorted.</summary>
        public static IReadOnlyList<string> Names => Registry.Names;

        /// <summary>The last pack preload's summary line, or empty before one has finished.</summary>
        public static string LastSummary { get; private set; } = string.Empty;

        /// <summary>
        /// Binds the config, applies the Harmony prefix, registers the smoke checks and loads
        /// the packs' voice clips. Called once from the adapter plugin's Awake; calling it again
        /// does nothing.
        /// </summary>
        /// <param name="config">
        /// Config file the <c>Voice.*</c> entries are bound in. Pass the calling plugin's
        /// <c>Config</c>; when omitted the framework binds them in its own .cfg.
        /// </param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug("VOICE already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
                BindConfig(config);
            }

            VoiceDiagnostics.Sink = message => CwyLsModLoader.Log.LogWarning("VOICE " + message);
            VoiceSmokeChecks.RegisterAll();
            LanguageVoiceSmokeChecks.RegisterAll();

            // The closed instantiation that is actually patched and read, so the startup report
            // covers the real target. The only registration of GetAudioClip and the clip cache.
            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (voice)", BulbulVoiceTargets.All);
            PatchTargetVerifier.VerifyAll(BulbulVoiceTargets.All, new ReflectionMemberResolver());

            if (!Enabled)
            {
                ConsoleEvents.Setup(
                    "voice",
                    $"disabled ({ConfigSection}.{ConfigKeyEnabled}=false); the game's own clips are untouched");
                return;
            }

            _harmony = new Harmony(BulbulPlugin.PluginGuid + ".voice");
            if (VoicePatches.Apply(_harmony))
            {
                CwyLsModLoader.Log.LogDebug("VOICE " + VoicePatches.Detail);
            }

            // PacksResolved, not PacksChanged: it hands us the resolution the scan just logged, so
            // the preload reads exactly the pack list the framework reported. LastResolution covers
            // the normal order, where ModLoaderPlugin scanned before this adapter's Awake ran.
            PackManager.PacksResolved += OnPacksResolved;
            Preload("startup", PackManager.LastResolution);

            // Language-scoped voice sets ride on the same lookup prefix, so they only make sense
            // once it is in place.
            if (VoicePatches.Applied)
            {
                LanguageVoices.Initialize(_harmony!, LanguageVoicesEnabled);
            }
            else
            {
                ConsoleEvents.Setup(
                    LanguageVoices.SetupFeature,
                    "unavailable: the voice lookup is not patched; every language plays the game's clips",
                    ("reason", VoicePatches.Detail));
            }
        }

        /// <summary>
        /// The clip the game's voice lookup would answer with for a name right now, if anything
        /// overrides the game's own clip: a mod's runtime registration, else the loaded clip of the
        /// language voice set in force, else a global pack clip. Free of side effects: nothing
        /// is loaded, traced or recorded. Use it to measure the clip that will really play, e.g. its
        /// <c>length</c>.
        /// </summary>
        /// <param name="name">The voice name; case-sensitive.</param>
        /// <param name="clip">The overriding clip, or null when the game's own clip would play.</param>
        /// <returns>True when an override would answer.</returns>
        public static bool TryGetEffective(string? name, [NotNullWhen(true)] out AudioClip? clip)
        {
            clip = null;
            if (!Enabled || string.IsNullOrEmpty(name))
            {
                return false;
            }

            var registered = TryGet(name!, out var registeredClip) ? EntryOf(name!) : null;
            if (registered != null && registered.IsRuntime)
            {
                clip = registeredClip;
                return clip != null;
            }

            if (LanguageVoices.TryPeek(name, out var languageClip) && languageClip != null)
            {
                clip = languageClip;
                return true;
            }

            clip = registered != null ? registeredClip : null;
            return clip != null;
        }

        /// <summary>
        /// Registers a clip under a voice name, so that anything asking the game for that name gets
        /// this clip. This is how a mod adds a line the game never shipped.
        /// </summary>
        /// <param name="name">
        /// The voice name, e.g. <c>mymod_0001</c>. Must match
        /// <see cref="VoiceNameRules.Pattern"/>, and is matched case-sensitively.
        /// </param>
        /// <param name="clip">The clip to play. Its <c>name</c> is set to <paramref name="name"/>.</param>
        /// <param name="source">Who is registering, for the log and the diagnostics. Defaults to <c>runtime</c>.</param>
        /// <param name="keepPack">
        /// True to leave the name alone when a content pack already provides it. The default,
        /// false, means a mod's clip wins - a synthesised line should not be replaced by a file.
        /// </param>
        /// <returns>True when the registry now resolves <paramref name="name"/> to this clip.</returns>
        public static bool Register(string name, AudioClip clip, string source = RuntimeSource, bool keepPack = false)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                CwyLsModLoader.Log.LogWarning("VOICE Register was given no name; nothing was registered");
                return false;
            }

            // Unity's == also catches a clip that has already been destroyed, which would register
            // a name that resolves to nothing.
            if (clip == null)
            {
                CwyLsModLoader.Log.LogWarning($"VOICE Register('{name}') was given no clip; nothing was registered");
                return false;
            }

            try
            {
                if (!string.Equals(clip.name, name, StringComparison.Ordinal))
                {
                    clip.name = name;
                }
            }
            catch (Exception ex)
            {
                // Naming is cosmetic for the lookup (the registry keys on the name we were given),
                // so a clip that refuses to be renamed is still registered.
                CwyLsModLoader.Log.LogWarning(
                    $"VOICE could not rename the clip for '{name}': {ex.GetType().Name}: {ex.Message}");
            }

            var entry = new VoiceEntry(
                name,
                string.IsNullOrWhiteSpace(source) ? RuntimeSource : source,
                VoicePriority.Runtime,
                path: null,
                clip: clip);

            var result = Registry.Register(entry, keepPack);
            Report(result, "runtime");
            return result.Accepted;
        }

        /// <summary>Removes a voice name, so the game goes back to its own clip (or to nothing).</summary>
        /// <param name="name">The name to remove; case-sensitive.</param>
        /// <returns>True when a registration was removed.</returns>
        public static bool Unregister(string name)
        {
            var removed = Registry.Unregister(name);
            if (removed is null)
            {
                return false;
            }

            ConsoleEvents.Emit(
                TraceSys,
                "unregistered",
                ConsoleTags.Voice,
                "- " + removed.Name,
                "source",
                (ConsolePayload.MirrorKey, LogEachRegistration),
                ("name", removed.Name),
                ("source", removed.Source));
            return true;
        }

        /// <summary>The clip registered under a name.</summary>
        /// <param name="name">The name to look up; case-sensitive.</param>
        /// <param name="clip">The clip, or null when the name is not registered.</param>
        /// <returns>True when a live clip was found.</returns>
        public static bool TryGet(string name, [NotNullWhen(true)] out AudioClip? clip)
        {
            var found = Registry.Resolve(name)?.Clip as AudioClip;

            // A destroyed clip is Unity-null but not reference-null; treat it as absent so the
            // game falls through to its own lookup instead of playing silence.
            if (found is null || found == null)
            {
                clip = null;
                return false;
            }

            clip = found;
            return true;
        }

        /// <summary>Every registration, ordinally sorted by name. A detached copy.</summary>
        public static IReadOnlyList<VoiceEntry> Snapshot() => Registry.Snapshot();

        /// <summary>
        /// Plays a voice name as Satone, through the game's own
        /// <c>HeroineService.PlayVoice</c> - which is what drives her mouth animation, so a
        /// registered clip played this way looks exactly like a line the game shipped.
        /// </summary>
        /// <param name="name">
        /// A registered voice name, or any name the game itself knows. Nothing is checked against
        /// the registry: a mod is allowed to ask for one of the game's own 1381 lines.
        /// </param>
        /// <param name="moveMouth">
        /// True to run the talk animation while the clip plays (the game's <c>isMoveMouse</c>).
        /// </param>
        /// <returns>True when the call reached the game; false when Satone is not in the scene.</returns>
        public static bool PlayAsSatone(string name, bool moveMouth = true)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                CwyLsModLoader.Log.LogWarning("VOICE PlayAsSatone was given no name");
                return false;
            }

            var heroine = ResolveHeroineService();
            if (heroine == null)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"VOICE PlayAsSatone('{name}'): HeroineService is not available yet " +
                    "(it lives in the room scene's DI scope, so this only works once RoomScene is up)");
                return false;
            }

            try
            {
                // isStory: false - a story line is queued by the scenario reader and would cut
                // whatever it is reading. This is a one-off line, like an idle voice.
                heroine!.PlayVoice(name, moveMouth, false).Forget();

                ConsoleEvents.Emit(
                    TraceSys,
                    "play",
                    ConsoleTags.Voice,
                    "play " + name,
                    "via,mouth:moveMouth,registered",
                    ("name", name),
                    ("moveMouth", moveMouth),
                    ("via", _heroineVia),
                    ("registered", Registry.Resolve(name) != null));

                return true;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"VOICE PlayAsSatone('{name}') failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Where a registered name came from, or empty when it is not registered.</summary>
        /// <param name="name">The name to look up.</param>
        internal static string SourceOf(string name) => Registry.Resolve(name)?.Source ?? string.Empty;

        /// <summary>The registry entry for a name (pack or runtime), or null.</summary>
        /// <param name="name">The name to look up; case-sensitive.</param>
        internal static VoiceEntry? EntryOf(string name) => Registry.Resolve(name);

        private static void BindConfig(ConfigFile? config)
        {
            var file = config ?? OwnConfig();

            _enabled = file.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Replace and add voice lines from content packs and from other mods. Off leaves the "
                + "game's own clips completely untouched.");

            _preloadPackClips = file.Bind(
                ConfigSection,
                ConfigKeyPreloadPackClips,
                true,
                "Load every audio/voice/*.ogg|*.wav in the installed packs at startup. Off still "
                + "lets a mod register clips at runtime.");

            _logEachRegistration = file.Bind(
                ConfigSection,
                ConfigKeyLogEachRegistration,
                true,
                "Write one log line per registered clip. Off keeps only the summary line.");

            _languageVoices = file.Bind(
                ConfigSection,
                LanguageVoices.ConfigKey,
                true,
                "Play a pack's audio/voice/<lang>/ clips instead of the game's while that language is "
                + "selected (e.g. Thai voices only while Thai is chosen). Off plays the game's clips in "
                + "every language. Read at startup.");
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(BepInEx.Paths.ConfigPath, BulbulPlugin.PluginGuid + ".voice.cfg"), saveOnInit: true);

        /// <summary>
        /// Re-runs the preload for a completed scan, against that scan's own resolution.
        /// </summary>
        /// <param name="resolution">What the scan loaded and refused.</param>
        private static void OnPacksResolved(PackResolution resolution)
        {
            // PackManager.Rescan is a development tool; re-running the preload keeps the registry
            // in step with it. A clip a mod registered outranks a pack, so nothing a mod did is
            // lost here (see VoiceRegistry's rules).
            Preload("packs resolved", resolution);
            LanguageVoices.OnPacksResolved(resolution);
        }

        private static void Preload(string trigger, PackResolution resolution)
        {
            if (!Enabled)
            {
                return;
            }

            if (!PreloadPackClips)
            {
                ConsoleEvents.Setup(
                    "voice",
                    $"pack clips not loaded ({ConfigSection}.{ConfigKeyPreloadPackClips}=false)");
                return;
            }

            var packs = PacksOf(resolution);
            var plan = VoiceLoadPlan.Build(PackIndexes(packs));
            var run = new PreloadRun(trigger, plan, packs);

            foreach (var skip in plan.Skipped)
            {
                CwyLsModLoader.Log.LogWarning($"VOICE skipped {skip.PackId}/{skip.RelativePath}: {skip.Reason}");
            }

            if (plan.Count == 0)
            {
                Finish(run);
                return;
            }

            CwyLsModLoader.Log.LogDebug($"VOICE preload ({trigger}): {plan.Summarize()}");

            foreach (var step in plan.Steps)
            {
                var absolute = AbsolutePathOf(run, step);
                if (absolute is null)
                {
                    Complete(run, step, null, "the pack is not loaded any more");
                    continue;
                }

                if (step.Loader == VoiceLoaderKind.WavSync)
                {
                    Complete(run, step, AudioLoader.LoadWav(absolute, run.Report), "the WAV did not load");
                }
                else
                {
                    // Ogg has to go through the engine's decoder, which needs several frames; the
                    // callback lands back on the main thread, so the counters stay single-threaded.
                    AudioLoader.LoadAsync(
                        absolute,
                        clip => Complete(run, step, clip, "the Ogg did not decode"),
                        run.Report);
                }
            }
        }

        private static void Complete(PreloadRun run, VoiceLoadStep step, AudioClip? clip, string failureReason)
        {
            try
            {
                if (clip == null)
                {
                    run.Failed++;
                    CwyLsModLoader.Log.LogWarning(
                        $"VOICE {step.PackId}/{step.RelativePath}: {failureReason}; see the loader warning above");
                    return;
                }

                if (!string.Equals(clip!.name, step.Name, StringComparison.Ordinal))
                {
                    clip.name = step.Name;
                }

                var entry = new VoiceEntry(
                    step.Name, step.PackId, VoicePriority.Pack, step.RelativePath, clip);

                var result = Registry.Register(entry);
                Report(result, step.Loader == VoiceLoaderKind.WavSync ? "wav" : "ogg");

                if (result.Accepted)
                {
                    run.Register(step);
                }
                else if (result.Outcome == VoiceRegistrationOutcome.KeptExisting)
                {
                    run.Kept++;
                }
                else
                {
                    run.Failed++;
                }
            }
            catch (Exception ex)
            {
                run.Failed++;
                CwyLsModLoader.Log.LogError(
                    $"VOICE {step.PackId}/{step.RelativePath} could not be registered: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (run.Done())
                {
                    Finish(run);
                }
            }
        }

        private static void Finish(PreloadRun run)
        {
            // LastSummary is the SMOKE REPORT's detail, not a console line. The console
            // line is built from the payload below, so changing one can no longer change the other.
            LastSummary = run.Summarize();
            CwyLsModLoader.Log.LogDebug("VOICE names: " + Join(run.Names));
            CwyLsModLoader.Log.LogDebug("VOICE loader report: " + run.Report.Summarize());

            ConsoleEvents.Emit(
                TraceSys,
                "preload",
                ConsoleTags.Setup,
                run.Summarize(),
                "src,trigger,registered,failed",
                ("src", "voice"),
                ("trigger", run.Trigger),
                ("registered", run.Registered),
                ("ogg", run.Ogg),
                ("wav", run.Wav),
                ("failed", run.Failed),
                ("kept", run.Kept),
                ("skipped", run.Plan.Skipped.Count),
                ("names", run.Names.ToArray()));
        }

        private static void Report(VoiceRegistration result, string via)
        {
            var entry = result.Entry;

            switch (result.Outcome)
            {
                case VoiceRegistrationOutcome.Added:
                    ConsoleEvents.Emit(
                        TraceSys,
                        "registered",
                        ConsoleTags.Voice,
                        "+ " + entry.Name,
                        "via,source",
                        (ConsolePayload.MirrorKey, LogEachRegistration),
                        ("name", entry.Name),
                        ("source", entry.Source),
                        ("via", via),
                        ("path", entry.Path));
                    break;

                case VoiceRegistrationOutcome.Replaced:
                    ConsoleEvents.Emit(
                        TraceSys,
                        "override",
                        ConsoleTags.Voice,
                        "~ " + entry.Name,
                        "via,source,previous",
                        ("name", entry.Name),
                        ("source", entry.Source),
                        ("previous", result.Previous!.Source),
                        ("via", via),
                        ("path", entry.Path));
                    break;

                default:
                    ConsoleEvents.EmitAt(
                        TraceSys,
                        "refused",
                        ConsoleTags.Voice,
                        ConsoleLevel.Warn,
                        "! " + entry.Name,
                        "source,outcome,reason",
                        ("name", entry.Name),
                        ("source", entry.Source),
                        ("outcome", result.Outcome.ToString()),
                        ("reason", result.Reason));
                    break;
            }
        }

        /// <summary>
        /// One resolution's packs as folders on disk, in load order.
        /// </summary>
        /// <param name="resolution">The resolution a scan raised, or null.</param>
        /// <remarks>
        /// Built from the resolution the scan handed us rather than read back from
        /// <c>PackManager.Loaded</c>: a preload holds its own list for as long as its Ogg decodes
        /// take, so a rescan landing mid-run cannot make a step resolve against a different pack
        /// than the one it was planned from.
        /// </remarks>
        internal static List<LoadedPack> PacksOf(PackResolution? resolution)
        {
            var resolved = resolution?.Loaded;
            if (resolved is null || resolved.Count == 0)
            {
                return new List<LoadedPack>(0);
            }

            var packs = new List<LoadedPack>(resolved.Count);
            foreach (var pack in resolved)
            {
                // A directory source knows its real folder; any other source (a test's, a future
                // archive's) can only be named under CWYLS Mods, which is where the scan looked.
                var root = pack.Source is DirectoryPackSource directory
                    ? directory.RootPath
                    : Path.Combine(ModLoaderPaths.PacksRoot, pack.Source.FolderName);

                packs.Add(new LoadedPack(pack.Manifest, root, pack.Index));
            }

            return packs;
        }

        private static List<KeyValuePair<string, PackContentIndex>> PackIndexes(IReadOnlyList<LoadedPack> packs)
        {
            var indexes = new List<KeyValuePair<string, PackContentIndex>>(packs.Count);

            foreach (var pack in packs)
            {
                indexes.Add(new KeyValuePair<string, PackContentIndex>(pack.Id, pack.Index));
            }

            return indexes;
        }

        private static string? AbsolutePathOf(PreloadRun run, VoiceLoadStep step)
        {
            var pack = Find(run.Packs, step.PackId);
            if (pack is null)
            {
                return null;
            }

            try
            {
                return pack.FullPath(step.RelativePath);
            }
            catch (ArgumentException ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"VOICE {step.PackId}/{step.RelativePath} is not a path inside the pack: {ex.Message}");
                return null;
            }
        }

        /// <summary>The pack with this id in one run's list, or null when it is not in it.</summary>
        /// <param name="packs">The run's packs.</param>
        /// <param name="packId">A pack id, matched the way <c>PackManager.Find</c> matches.</param>
        private static LoadedPack? Find(IReadOnlyList<LoadedPack> packs, string packId)
        {
            foreach (var pack in packs)
            {
                if (string.Equals(pack.Id, packId, StringComparison.OrdinalIgnoreCase))
                {
                    return pack;
                }
            }

            return null;
        }

        private static Game.HeroineService? ResolveHeroineService()
        {
            // Unity's == also catches a service whose GameObject was destroyed by a scene change.
            if (_heroine != null)
            {
                return _heroine;
            }

            try
            {
                // The room scene's VContainer scope; throws until that scene exists, which is the
                // normal state for the first seconds of a session.
                _heroine = Game.RoomLifetimeScope.Resolve<Game.HeroineService>();
                if (_heroine != null)
                {
                    _heroineVia = "RoomLifetimeScope";
                    CwyLsModLoader.Log.LogDebug("VOICE HeroineService resolved via RoomLifetimeScope");
                    return _heroine;
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    $"VOICE HeroineService not resolvable from the DI scope yet: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                _heroine = UnityEngine.Object.FindObjectOfType<Game.HeroineService>();
                if (_heroine != null)
                {
                    _heroineVia = "FindObjectOfType";
                    CwyLsModLoader.Log.LogDebug("VOICE HeroineService resolved via FindObjectOfType");
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"VOICE HeroineService could not be found: {ex.GetType().Name}: {ex.Message}");
            }

            return _heroine;
        }

        private static string Join(IReadOnlyList<string> names)
        {
            var shown = Math.Min(names.Count, MaxListedNames);
            var parts = new string[shown];
            for (var i = 0; i < shown; i++)
            {
                parts[i] = names[i];
            }

            var joined = string.Join(", ", parts);
            return names.Count > shown ? joined + " and " + (names.Count - shown) + " more" : joined;
        }

        /// <summary>
        /// One pass over the packs' voice files. Everything on it is touched on Unity's main
        /// thread only: the WAV loader is synchronous and the Ogg loader's callback is delivered
        /// from a coroutine.
        /// </summary>
        private sealed class PreloadRun
        {
            internal PreloadRun(string trigger, VoiceLoadPlan plan, IReadOnlyList<LoadedPack> packs)
            {
                Trigger = trigger;
                Plan = plan;
                Packs = packs;
                Pending = plan.Count;
            }

            internal string Trigger { get; }

            internal VoiceLoadPlan Plan { get; }

            /// <summary>The packs this run was planned from; every step resolves against these.</summary>
            internal IReadOnlyList<LoadedPack> Packs { get; }

            internal LoadReport Report { get; } = new LoadReport();

            internal List<string> Names { get; } = new List<string>();

            internal int Registered { get; private set; }

            internal int Ogg { get; private set; }

            internal int Wav { get; private set; }

            internal int Failed { get; set; }

            internal int Kept { get; set; }

            private int Pending { get; set; }

            internal void Register(VoiceLoadStep step)
            {
                Registered++;
                Names.Add(step.Name);

                if (step.Loader == VoiceLoaderKind.WavSync)
                {
                    Wav++;
                }
                else
                {
                    Ogg++;
                }
            }

            /// <summary>Records one finished file and says whether the run is over.</summary>
            internal bool Done() => --Pending <= 0;

            internal string Summarize()
            {
                var summary =
                    $"VOICE: {Registered} clip(s) registered from packs ({Ogg} ogg, {Wav} wav), {Failed} failed";

                if (Kept > 0)
                {
                    summary += $"; {Kept} kept for a mod";
                }

                if (Plan.Skipped.Count > 0)
                {
                    summary += $"; {Plan.Skipped.Count} skipped";
                }

                return summary;
            }
        }
    }
}
