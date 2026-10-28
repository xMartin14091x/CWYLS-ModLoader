using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Packs;
using HarmonyLib;
using Game = global::Bulbul;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul.MasterData
{
    /// <summary>
    /// Adds and overrides the game's dialogue rows, scenario commands and scenario groups
    /// from content packs, and carries the text tables for languages the game does not have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pack ships <c>data/localization.json</c>, <c>data/novel.json</c>,
    /// <c>data/scenario_groups.json</c> and any number of <c>data/subtitles/&lt;lang&gt;.json</c>
    /// tables. They are merged in pack order and written into <c>Bulbul.MasterDataLoader</c>'s three
    /// public collections, as the game's own row types, so <c>LocalizationMasterWrapper</c>,
    /// <c>ScenarioReader</c> and the story UI read them without knowing they came from a pack.
    /// </para>
    /// <para>
    /// <b>When the injection happens.</b> <c>MasterDataLoader.Load</c> is <c>async UniTask</c>, so
    /// a Harmony postfix on it runs at its first <c>await</c> - before the collections exist. The
    /// postfix (<see cref="MasterDataPatches"/>) therefore only captures the loader and notes that
    /// a load has begun; the injection itself waits for the loader's own <c>IsLoaded</c> flag
    /// (set only after all three collections are assigned), watched once a frame
    /// by <see cref="MasterDataHost"/>. Re-running <c>PacksResolved</c> re-injects from the same
    /// baseline, so nothing accumulates.
    /// </para>
    /// <para>
    /// Static throughout and initialised from the plugin's Awake, because this game destroys
    /// BepInEx plugin components about 0.2 s after boot.
    /// </para>
    /// </remarks>
    public static class MasterDataFeature
    {
        /// <summary>Config section holding the master-data settings.</summary>
        public const string ConfigSection = "MasterData";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the per-row log switch.</summary>
        public const string ConfigKeyLogEachRow = "LogEachRow";

        /// <summary>The <c>sys</c> tag this feature's diagnostic events are published under.</summary>
        public const string TraceSys = "data";

        /// <summary>The event name this feature's diagnostic events carry.</summary>
        public const string TraceName = "masterdata";

        /// <summary>How many rejections a single log pass prints in full.</summary>
        private const int MaxLoggedRejections = 20;

        private static readonly object Gate = new object();

        private static Harmony? _harmony;
        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<bool>? _logEachRow;
        private static ConfigFile? _ownConfig;
        private static bool _initialized;
        private static List<MasterDataPackInput> _inputs = new List<MasterDataPackInput>();
        private static string _injectedFingerprint = string.Empty;

        /// <summary>True when the feature is switched on (<c>MasterData.Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>True when every injected row is logged (<c>MasterData.LogEachRow</c>, default false).</summary>
        public static bool LogEachRow => _logEachRow?.Value ?? false;

        /// <summary>True once the packs' rows are in the game's master data.</summary>
        public static bool IsInjected { get; private set; }

        /// <summary>
        /// Raised after every successful injection into the game's master data - the first one
        /// after the game's load, and every re-injection after a rescan or a new load (the audio
        /// feature re-applies its clips here). The argument is the trigger (<c>loaded</c>,
        /// <c>packsResolved</c>, ...), as in the <c>MASTERDATA</c> line. Raised on the thread that
        /// injected, which is the main thread (the load watch or a rescan). Each handler runs in its
        /// own try/catch: a throwing handler is logged and the others still run.
        /// </summary>
        public static event Action<string>? Injected;

        /// <summary>
        /// Text for languages the game has no column for, from the packs'
        /// <c>data/subtitles/&lt;code&gt;.json</c>, merged in pack order. The language feature serves
        /// it through the game's text seam. Never null; empty until the packs have been read.
        /// </summary>
        public static ISubtitleLookup Subtitles { get; private set; } = SubtitleTables.Empty;

        /// <summary>
        /// The merged rows, as last computed. Never null; <see cref="MasterDataSet.Empty"/> before
        /// the packs have been read.
        /// </summary>
        public static MasterDataSet Merged { get; private set; } = MasterDataSet.Empty;

        /// <summary>
        /// Pack lines: true while the pack-lines feature is active, so groups that declare
        /// <c>Languages</c> are screened and injected. False (the default, and whenever the feature is
        /// off, not initialised or missing a seam) holds every such group back: the small-talk pool is
        /// built from the master data and would otherwise offer it in every language. Set through
        /// <see cref="AdmitPackLines"/>.
        /// </summary>
        public static bool PackLinesAdmitted { get; private set; }

        /// <summary>
        /// Pack lines: what the last merge decided about language-exclusive groups - the lines that may
        /// join the game's pools, the ones refused, the ones held back. Never null.
        /// </summary>
        public static PackLineScreenResult LineScreen { get; private set; } = PackLineScreenResult.Empty;

        /// <summary>The last injection's summary line, or empty before one has happened.</summary>
        public static string LastSummary { get; private set; } = string.Empty;

        /// <summary>What triggered the last injection, or empty. For the smoke detail.</summary>
        public static string LastTrigger { get; private set; } = string.Empty;

        /// <summary>
        /// Fingerprint of the pack files the current injection was built from, or empty when
        /// nothing is injected. A rescan whose files fingerprint the same is skipped.
        /// </summary>
        public static string PackFingerprint => _injectedFingerprint;

        /// <summary>True once the postfix on <c>MasterDataLoader.Load</c> is in place.</summary>
        public static bool PatchApplied => MasterDataPatches.Applied;

        /// <summary>What was patched, or why it could not be. Never null.</summary>
        public static string PatchDetail => MasterDataPatches.Detail;

        /// <summary>
        /// Binds the config, applies the Harmony postfix, reads the packs, registers the smoke
        /// checks and arms the load watch. Called once from the adapter plugin's Awake; calling it
        /// again does nothing.
        /// </summary>
        /// <param name="config">
        /// Config file the <c>MasterData.*</c> entries are bound in. Pass the calling plugin's
        /// <c>Config</c>; when omitted the framework binds them in its own .cfg.
        /// </param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug("MASTERDATA already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
                BindConfig(config);
            }

            MasterDataSmokeChecks.RegisterAll();

            if (!Enabled)
            {
                ConsoleEvents.Setup(
                    "masterdata",
                    "disabled (" + ConfigSection + "." + ConfigKeyEnabled
                    + "=false); the game's own rows are untouched");
                return;
            }

            _harmony = new Harmony(BulbulPlugin.PluginGuid + ".masterdata");
            if (MasterDataPatches.Apply(_harmony))
            {
                CwyLsModLoader.Log.LogDebug("MASTERDATA " + MasterDataPatches.Detail);
            }

            PackManager.PacksResolved += OnPacksResolved;

            // Read and merge now, with an empty baseline, so Subtitles and Merged are usable before
            // the game has loaded anything. The counts are re-computed against the game's real ids
            // at injection time, which is what the summary line reports.
            Reload("startup");

            MasterDataHost.EnsureRunner();
        }

        /// <summary>
        /// Re-reads the packs and, if their files have changed and the game's data is loaded,
        /// re-injects. Exposed so a mod or the smoke runner can refresh without restarting.
        /// </summary>
        /// <remarks>
        /// A rescan whose files fingerprint the same as the current injection is a no-op that logs
        /// <c>MASTERDATA: packs unchanged, injection kept</c> at Debug.
        /// </remarks>
        /// <param name="trigger">What asked for it, for the log line.</param>
        /// <returns>True when the rows are in the game's master data afterwards.</returns>
        public static bool Reload(string trigger)
        {
            if (!Enabled)
            {
                return false;
            }

            var reason = string.IsNullOrEmpty(trigger) ? "unknown" : trigger;

            try
            {
                _inputs = MasterDataPackReader.ReadAll();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    "MASTERDATA could not read the packs' data files: " + ex.GetType().Name + ": " + ex.Message);
                _inputs = new List<MasterDataPackInput>();
            }

            // PacksResolved fires on every rescan - the smoke runner and the texture feature both
            // rescan - so without this an untouched pack would be re-merged and re-injected several
            // times a session, each with its own summary line.
            var fingerprint = MasterDataFingerprint.Compute(_inputs);
            if (IsInjected && string.Equals(fingerprint, _injectedFingerprint, StringComparison.Ordinal))
            {
                CwyLsModLoader.Log.LogDebug(
                    "MASTERDATA: packs unchanged, injection kept (" + reason + ", " + fingerprint + ")");
                return true;
            }

            Merge(reason);

            return IsReadyToInject() && ApplyNow(reason);
        }

        /// <summary>
        /// Injects the merged rows into the game's master data now. Always injects - the
        /// "packs unchanged" shortcut lives in <see cref="Reload"/>, so this stays the way to force
        /// a rebuild after something else has written to the loader's collections.
        /// </summary>
        /// <param name="trigger">What asked for it - <c>loaded</c>, <c>packsResolved</c> - for the log.</param>
        /// <returns>True when the loader holds our rows afterwards.</returns>
        public static bool ApplyNow(string trigger)
        {
            var reason = string.IsNullOrEmpty(trigger) ? "unknown" : trigger;

            if (!Enabled)
            {
                return false;
            }

            var loader = ResolveLoader();
            if (!MasterDataHost.IsReady(loader))
            {
                CwyLsModLoader.Log.LogDebug(
                    "MASTERDATA (" + reason + "): the game's master data is not loaded yet; waiting");
                return false;
            }

            try
            {
                if (!MasterDataInjector.Capture(loader!))
                {
                    // Expected and transient while a new load is replacing the collections; the
                    // injector logs the specific reason. The host backs off and tries again.
                    CwyLsModLoader.Log.LogDebug(
                        "MASTERDATA (" + reason + "): no baseline yet; not injecting");
                    return false;
                }

                // Re-merge against the game's real ids: only now can a row be called an override
                // rather than an addition, and only now can an ambiguous novel id be refused.
                Merge(reason);

                var counts = MasterDataInjector.Inject(loader!, Merged, LogEachRow);

                IsInjected = true;
                LastTrigger = reason;
                LastSummary = Merged.Report.Summarize();
                _injectedFingerprint = MasterDataFingerprint.Compute(_inputs);

                // LastSummary stays the smoke report's detail; the console line is the one bus publish
                // below, so the two counts can never disagree.
                CwyLsModLoader.Log.LogDebug(
                    LastSummary + "; loader now holds " + counts.LocalizationTotal + " localization, "
                    + counts.NovelTotal + " novel, " + counts.ScenarioGroupTotal + " scenario group row(s) ("
                    + reason + ")");

                LogRejections();
                Emit(reason, counts);
                RaiseInjected(reason);
                return true;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    "MASTERDATA (" + reason + ") injection failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Pack lines: lets language-exclusive groups in (the pack-lines feature is active) or holds them
        /// back, and re-injects when the game's data is already loaded so the change takes effect now.
        /// Called by <c>PackLinesFeature</c>; a call that changes nothing does nothing.
        /// </summary>
        /// <param name="admit">True while pack lines are active.</param>
        /// <param name="trigger">Who asked, for the log line.</param>
        /// <returns>True when the setting changed.</returns>
        public static bool AdmitPackLines(bool admit, string trigger)
        {
            if (PackLinesAdmitted == admit)
            {
                return false;
            }

            PackLinesAdmitted = admit;
            var reason = string.IsNullOrEmpty(trigger) ? "packLines" : trigger;
            CwyLsModLoader.Log.LogDebug(
                "MASTERDATA language-exclusive groups are now " + (admit ? "screened and injected" : "held back")
                + " (" + reason + ")");

            if (!Enabled)
            {
                return true;
            }

            if (IsReadyToInject())
            {
                ApplyNow(reason);
            }
            else
            {
                Merge(reason);
            }

            return true;
        }

        /// <summary>
        /// The game's master-data loader: the instance the <c>Load</c> postfix captured, or the
        /// project DI scope's, whichever is available.
        /// </summary>
        internal static Game.MasterDataLoader? ResolveLoader()
        {
            var captured = MasterDataPatches.Loader;
            if (captured != null)
            {
                return captured;
            }

            return GameScopes.TryProject<Game.MasterDataLoader>(out var resolved) ? resolved : null;
        }

        /// <summary>True when the game's data is loaded and the rows could go in right now.</summary>
        internal static bool IsReadyToInject() => MasterDataHost.IsReady(ResolveLoader());

        /// <summary>
        /// Called by the <c>Load</c> postfix. The collections we injected into are about to be
        /// replaced, so the baseline and the injected flag are dropped.
        /// </summary>
        /// <param name="loader">The loader that is loading.</param>
        /// <param name="loadsSeen">How many loads have been seen, including this one.</param>
        internal static void OnLoadStarted(Game.MasterDataLoader loader, int loadsSeen)
        {
            if (loadsSeen > 1)
            {
                CwyLsModLoader.Log.LogDebug(
                    "MASTERDATA the game started master-data load #" + loadsSeen + "; will re-inject when it finishes");
            }

            Rearm("load #" + loadsSeen + " started");
        }

        /// <summary>Forgets the injection so the watch injects again once the data is loaded.</summary>
        /// <param name="reason">Why, for the debug log.</param>
        internal static void Rearm(string reason)
        {
            IsInjected = false;
            _injectedFingerprint = string.Empty;
            MasterDataInjector.Forget();
            CwyLsModLoader.Log.LogDebug("MASTERDATA re-armed (" + reason + ")");
        }

        private static void OnPacksResolved(CwyLs.ModLoader.Core.Packs.PackResolution resolution)
        {
            CwyLsModLoader.Log.LogDebug("MASTERDATA packs resolved: " + resolution.Summary);
            Reload("packsResolved");
        }

        private static void Merge(string trigger)
        {
            try
            {
                var baseline = MasterDataInjector.Baseline();

                // Pack lines: groups that declare Languages are screened (admitted) or held back. A set
                // without such groups comes back as the same instance, so ordinary packs inject
                // exactly what they did before.
                var screened = PackLineScreen.Screen(MasterDataMerge.Merge(_inputs, baseline), baseline, PackLinesAdmitted);
                LineScreen = screened;
                Merged = screened.Set;
                Subtitles = Merged.Subtitles;

                if (screened.HeldBack > 0)
                {
                    CwyLsModLoader.Log.LogDebug(
                        "MASTERDATA (" + trigger + ") held back " + screened.HeldBack
                        + " language-exclusive group(s): pack lines are not active");
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    "MASTERDATA (" + trigger + ") merge failed; keeping the previous rows: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Raises <see cref="Injected"/>, one handler at a time, so one failure cannot skip the rest.</summary>
        private static void RaiseInjected(string trigger)
        {
            var handlers = Injected;
            if (handlers is null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string>)handler)(trigger);
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        "MASTERDATA an Injected handler (" + handler.Method.DeclaringType?.Name + "." + handler.Method.Name +
                        ") failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void LogRejections()
        {
            var rejections = Merged.Report.AllRejections;
            if (rejections.Count == 0)
            {
                return;
            }

            var shown = Math.Min(rejections.Count, MaxLoggedRejections);
            for (var i = 0; i < shown; i++)
            {
                CwyLsModLoader.Log.LogWarning("MASTERDATA rejected " + rejections[i]);
            }

            if (rejections.Count > shown)
            {
                CwyLsModLoader.Log.LogWarning(
                    "MASTERDATA rejected " + (rejections.Count - shown) + " more row(s); see the pack's data files");
            }
        }

        private static void Emit(string trigger, MasterDataInjector.InjectionCounts counts)
        {
            var rejected = new List<string>();
            foreach (var rejection in Merged.Report.AllRejections)
            {
                rejected.Add(rejection.ToString());
            }

            var languages = new List<string>();
            foreach (var pair in Merged.Report.SubtitleRowsByLanguage)
            {
                languages.Add(pair.Key + "=" + pair.Value);
            }

            ConsoleEvents.Emit(
                TraceSys,
                TraceName,
                ConsoleTags.MasterData,
                Merged.Report.Summarize(),
                "trigger,packs,rows:localizationTotal,rejected",
                ("trigger", trigger),
                ("packs", _inputs.Count),
                ("localizationAdded", counts.AddedOf(MasterDataMerge.LocalizationKind)),
                ("localizationOverridden", counts.OverriddenOf(MasterDataMerge.LocalizationKind)),
                ("novelAdded", counts.AddedOf(MasterDataMerge.NovelKind)),
                ("novelOverridden", counts.OverriddenOf(MasterDataMerge.NovelKind)),
                ("groupsAdded", counts.AddedOf(MasterDataMerge.ScenarioGroupsKind)),
                ("groupsOverridden", counts.OverriddenOf(MasterDataMerge.ScenarioGroupsKind)),
                ("subtitles", languages.ToArray()),
                ("localizationTotal", counts.LocalizationTotal),
                ("novelTotal", counts.NovelTotal),
                ("scenarioGroupTotal", counts.ScenarioGroupTotal),
                ("rejected", rejected.ToArray()));
        }

        private static void BindConfig(ConfigFile? config)
        {
            var file = config ?? OwnConfig();

            _enabled = file.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Merge the installed packs' data/*.json into the game's master data. Off leaves the "
                + "game's own dialogue, scenario commands and scenario groups completely untouched.");

            _logEachRow = file.Bind(
                ConfigSection,
                ConfigKeyLogEachRow,
                false,
                "Write one log line per injected row. Off keeps only the summary line - a pack with "
                + "a few hundred rows would otherwise fill the log.");
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(BepInEx.Paths.ConfigPath, BulbulPlugin.PluginGuid + ".masterdata.cfg"),
                saveOnInit: true);
    }
}
