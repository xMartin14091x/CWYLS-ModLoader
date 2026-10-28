using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Scenario;
using CwyLs.ModLoader.Mods;
using HarmonyLib;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// Script runtime: custom script commands in pack scenes (5000-5999), the four built-ins
    /// (<c>cwy.do</c>, <c>cwy.defer</c>, <c>cwy.wait</c>, <c>cwy.flag</c>), the save-log guard that keeps
    /// pack scenes out of the game's save, the pack-scene lint, <c>scene play|lint|list|flags</c> and the
    /// Scenes facet's playback. Format and rules: <c>README.md</c> next to this file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Seam.</b> A prefix on the private <c>ScenarioReader.ReadSentence(int)</c> runs rows 5000-5999 and
    /// continues through the reader's own <c>ReadNext</c>/<c>SelectionJump</c>. If that seam is missing
    /// (a game update), the fallback turns script rows into the game's <c>ReadNextForScript</c> rows and
    /// resumes them from a per-frame poll; <c>JumpTo</c> is then unavailable. If neither works the feature
    /// is off with one WARN and <see cref="ScenarioCommandRegistry.RuntimeAvailable"/> stays false, so the
    /// lint reports every script row (they would stall).
    /// </para>
    /// <para>
    /// Static and initialised once from the adapter's Awake, after <c>MasterDataFeature</c> (the game
    /// destroys plugin components about 0.2 s after boot).
    /// </para>
    /// </remarks>
    public static class ScenarioFeature
    {
        /// <summary>Config section.</summary>
        public const string ConfigSection = "Script";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the test switch that runs rows through the fallback even when the seam is available.</summary>
        public const string ConfigKeyForceFallback = "ForceFallback";

        /// <summary>Config key: <c>scene play</c> may preview game stories the player has not seen.</summary>
        public const string ConfigKeyAllowUnseen = "AllowUnseenGameStories";

        private static readonly object Gate = new object();

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<bool>? _forceFallback;
        private static ConfigEntry<bool>? _allowUnseen;

        /// <summary>
        /// <c>[Script] AllowUnseenGameStories</c> (default false; console <c>scene unseen on|off</c>): <c>scene
        /// play</c> also starts game stories the player has not seen - after a save backup, and without
        /// recording them as seen. For testing new or translated episodes.
        /// </summary>
        public static bool AllowUnseenGameStories
        {
            get => _allowUnseen?.Value ?? false;
            set
            {
                if (_allowUnseen != null)
                {
                    _allowUnseen.Value = value;
                }
            }
        }
        private static ConfigFile? _ownConfig;
        private static Harmony? _harmony;
        private static bool _initialized;
        private static string _lintFingerprint = string.Empty;

        /// <summary>The master switch (<c>[Script] Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>
        /// Testing only (<c>[Script] ForceFallback</c>, default false): run script rows through the
        /// <c>ReadNextForScript</c> fallback even when the <c>ReadSentence</c> seam is available, so the
        /// final in-game pass can exercise the fallback.
        /// </summary>
        public static bool ForceFallback => _forceFallback?.Value ?? false;

        /// <summary>True when script rows run (seam or fallback).</summary>
        public static bool IsActive => ScenarioRuntime.Mode != ScenarioRuntimeMode.Off;

        /// <summary>Why the feature is off, or empty when it is active.</summary>
        public static string InactiveReason { get; private set; } = "not initialised";

        /// <summary>How rows run: <c>seam</c>, <c>fallback (…)</c> or <c>off</c>.</summary>
        public static string ModeDetail { get; private set; } = "off";

        /// <summary>How the seams checked: <c>targets: N ok, M missing</c>, or empty before the check.</summary>
        public static string TargetSummary { get; private set; } = string.Empty;

        /// <summary>The last lint of the installed packs. Never null.</summary>
        public static ScenarioLintReport Lint { get; private set; } = ScenarioLintReport.Empty;

        /// <summary>
        /// Binds <c>[Script] Enabled</c>, registers the patch targets, the console command and the smoke
        /// checks, installs the seam (or the fallback) and the save-log guard, registers the built-ins,
        /// the <c>scene.play</c> action and the Scenes facet backend, and lints the packs after every
        /// master-data injection. Call once from the adapter plugin's Awake, after
        /// <c>MasterDataFeature.Initialize</c>; a second call is ignored.
        /// </summary>
        /// <param name="config">The plugin's config; when omitted the feature binds its own <c>.script.cfg</c>.</param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug("SCRIPT already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
                BindConfig(config);
            }

            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (script)", ScenarioPatchTargets.All);
            ScenarioCommands.Register();
            ScenarioSmokeChecks.RegisterAll();
            ModFacetBackends.Use<IScenesFacetBackend>("scenes", new ScenesFacetBackend());

            // The lint runs whatever happens below: switched off or without a seam it is what tells
            // the author which rows would stall.
            MasterDataFeature.Injected += OnInjected;

            if (!Enabled)
            {
                TurnOff(ConfigSection + "." + ConfigKeyEnabled + "=false", warn: false);
                ConsoleEvents.Setup(ScenarioRuntime.Owner, "disabled (" + InactiveReason + "); rows 5000-5999 would stall, scene lint reports them");
                return;
            }

            // Flags belong to the conditions as much as to scripts (mods set them through the Scenes
            // facet), so they load even when no seam could be installed.
            ScenarioFlagStore.Arm();

            var resolver = new ReflectionMemberResolver();
            var seam = PatchTargetChecker.Check(ScenarioPatchTargets.Seam, resolver);
            var fallback = PatchTargetChecker.Check(ScenarioPatchTargets.Fallback, resolver);
            var guard = PatchTargetChecker.Check(ScenarioPatchTargets.SaveGuard, resolver);
            var room = PatchTargetChecker.Check(ScenarioPatchTargets.Room, resolver);
            TargetSummary = "seam " + seam.Summarize() + "; fallback " + fallback.Summarize() + "; guard " + guard.Summarize() + "; room " + room.Summarize();

            _harmony = new Harmony(ScenarioPatches.HarmonyId);
            InstallGuard(guard);
            var mode = InstallRuntime(seam, fallback, out var detail);
            if (mode == ScenarioRuntimeMode.Off)
            {
                TurnOff(detail, warn: true);
            }
            else
            {
                InactiveReason = string.Empty;
                ModeDetail = detail;
                ScenarioRuntime.Start(mode);
                ScenarioCommandRegistry.Shared.SetRuntime(true, detail);
                ScenarioBuiltIns.Register();
                if (!room.AllFound)
                {
                    ScenarioRuntime.WarnAlways("scene play and deferred actions may not work: " + Missing(room));
                }
            }

            if (MasterDataFeature.IsInjected)
            {
                OnInjected("script.init");
            }

            ConsoleEvents.Setup(
                ScenarioRuntime.Owner,
                IsActive ? "script commands ready" : "off: " + InactiveReason,
                ("mode", ModeDetail),
                ("guard", ScenarioPatches.GuardApplied ? "on" : "off"),
                ("commands", string.Join(",", ScenarioBuiltIns.Registered)),
                ("flags", ScenarioFlags.Shared.Count),
                ("actions", ActionServices.Shared.HasRunner ? "runner" : "no runner yet"));
        }

        /// <summary>Lints the installed packs now (the console's <c>scene lint</c> after mods registered commands).</summary>
        internal static ScenarioLintReport Relint(string trigger)
        {
            Lint = ScenarioLint.Lint(MasterDataFeature.Merged, BuildContext());
            ConsoleEvents.File(ScenarioRuntime.Owner, "lint", ("trigger", trigger), ("summary", Lint.Summarize()));
            return Lint;
        }

        private static ScenarioRuntimeMode InstallRuntime(PatchTargetReport seam, PatchTargetReport fallback, out string detail)
        {
            string seamProblem;
            if (ForceFallback)
            {
                seamProblem = ConfigSection + "." + ConfigKeyForceFallback + "=true";
            }
            else if (!seam.AllFound)
            {
                seamProblem = Missing(seam);
            }
            else if (!ScenarioRuntime.PrepareSeam(out seamProblem))
            {
                // seamProblem says why
            }
            else if (ScenarioPatches.ApplySeam(_harmony!, out seamProblem))
            {
                detail = "seam (ScenarioReader.ReadSentence prefix)";
                return ScenarioRuntimeMode.Seam;
            }

            string fallbackProblem;
            if (!fallback.AllFound)
            {
                fallbackProblem = Missing(fallback);
            }
            else if (ScenarioRuntime.PrepareFallback(out fallbackProblem))
            {
                ScenarioRuntime.WarnAlways(
                    "script rows use the fallback (" + seamProblem + "): they wait as ReadNextForScript rows, "
                    + "JumpTo is not available and Satone's click state is re-entered when a row resumes");
                detail = "fallback (ReadNextForScript; " + seamProblem + ")";
                return ScenarioRuntimeMode.Fallback;
            }

            detail = "missing seam(s): " + seamProblem + "; fallback: " + fallbackProblem;
            return ScenarioRuntimeMode.Off;
        }

        private static void InstallGuard(PatchTargetReport guard)
        {
            string problem;
            if (!guard.AllFound)
            {
                problem = Missing(guard);
            }
            else if (!ScenarioSaveGuard.Prepare(out problem))
            {
                // problem says why
            }
            else if (ScenarioPatches.ApplyGuard(_harmony!, out problem))
            {
                return;
            }

            ScenarioRuntime.WarnAlways(
                "the save-log guard is off (" + problem + "): a long pack scene would be logged in the game's save, so scene play refuses long scenes");
        }

        private static void OnInjected(string trigger)
        {
            try
            {
                ScenarioSaveGuard.Refresh();
                var loader = MasterDataFeature.ResolveLoader();
                ScenarioRuntime.ConvertForFallback(loader?.NovelMasterList);

                var report = Relint(trigger);
                var fingerprint = MasterDataFeature.PackFingerprint + "|" + report.Summarize();
                if (string.Equals(fingerprint, _lintFingerprint, StringComparison.Ordinal))
                {
                    return;
                }

                _lintFingerprint = fingerprint;
                var shown = 0;
                foreach (var finding in report.Findings)
                {
                    if (finding.Severity == ScenarioLintSeverity.Error && shown++ < 20)
                    {
                        ScenarioRuntime.WarnAlways("lint " + finding);
                    }
                }

                ConsoleEvents.Emit(
                    ScenarioRuntime.Owner,
                    "lint",
                    ConsoleTags.Script,
                    "lint: " + report.Summarize() + (report.ErrorCount + report.WarningCount > 0 ? "; scene lint shows them" : string.Empty),
                    "trigger",
                    ("trigger", trigger),
                    ("errors", report.ErrorCount),
                    ("warnings", report.WarningCount));
            }
            catch (Exception ex)
            {
                ScenarioRuntime.WarnAlways("linting the pack scenes failed (" + trigger + "): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static ScenarioLintContext BuildContext()
        {
            var baseline = ScenarioSaveGuard.Baseline;
            var games = new List<ScenarioGroupKey>();
            var loader = MasterDataFeature.ResolveLoader();
            if (loader?.ScenarioGroupMasterList != null)
            {
                foreach (var group in loader.ScenarioGroupMasterList)
                {
                    if (group != null && baseline.HasScenarioGroup(group.ID))
                    {
                        games.Add(new ScenarioGroupKey(group.ID, (int)group.Scenario, group.EpisodeNumber));
                    }
                }
            }

            return new ScenarioLintContext
            {
                Baseline = baseline,
                GameDataKnown = baseline.ScenarioGroupCount > 0,
                GameGroups = games,
                Registry = ScenarioCommandRegistry.Shared,
                IsKnownAction = name => ActionRegistry.Shared.TryGet(name, out _),
            };
        }

        private static string Missing(PatchTargetReport report)
        {
            var ids = new List<string>();
            foreach (var missing in report.MissingResults)
            {
                ids.Add(missing.Target.Id);
            }

            return ids.Count == 0 ? report.Summarize() : string.Join(", ", ids) + " (" + report.MissingResults[0].Detail + ")";
        }

        private static void TurnOff(string reason, bool warn)
        {
            InactiveReason = reason;
            ModeDetail = "off";
            ScenarioRuntime.Reset("off");
            ScenarioCommandRegistry.Shared.SetRuntime(false, "script commands are off: " + reason + " (rows 5000-5999 would stall the game's reader)");
            if (warn)
            {
                ScenarioRuntime.WarnAlways("script commands are off: " + reason + "; scenes with rows 5000-5999 would stall, scene lint lists them");
            }
        }

        private static void BindConfig(ConfigFile? config)
        {
            var file = config ?? OwnConfig();
            _enabled = file.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Run script commands in pack scenes (cwy.do, cwy.defer, cwy.wait, cwy.flag and mods' commands, rows 5000-5999), "
                + "keep pack scenes out of the game's save, lint pack scenes and offer the console's scene command. Off: such rows "
                + "would stall the game's reader, so scene lint reports every one of them.");
            _forceFallback = file.Bind(
                ConfigSection,
                ConfigKeyForceFallback,
                false,
                "Testing only: run script rows through the fallback (they wait as the game's ReadNextForScript rows and resume "
                + "from a 4 Hz poll; cwy.if-style jumps do not work) even though the normal seam is available. Leave false.");
            _allowUnseen = file.Bind(
                ConfigSection,
                ConfigKeyAllowUnseen,
                false,
                "Testing: let 'scene play <id>' start game story episodes the player has not seen yet. Each one backs up the save "
                + "folder first (BepInEx/data/backups/save-scene-*, newest 10 kept) and is not recorded as seen. Off: only episodes "
                + "already seen replay, exactly as the game's history screen does. Console: scene unseen on|off.");
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(BepInEx.Paths.ConfigPath, BulbulPlugin.PluginGuid + ".script.cfg"),
                saveOnInit: true);
    }
}
