using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.PackLines;
using CwyLs.ModLoader.Core.Subtitles;
using HarmonyLib;

namespace CwyLs.ModLoader.Bulbul.PackLines
{
    /// <summary>
    /// Pack lines: new voiced Satone lines from content packs that the game offers only while one of
    /// their languages is shown - clicks, self-talk, timer lines, the after-pomodoro talk, small talk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Data.</b> A pack marks a scenario group as a pack line with a <c>Languages</c> member in
    /// <c>data/scenario_groups.json</c> (format: <c>README.md</c> next to this file). The master-data
    /// merge screens those groups (<see cref="PackLineScreen"/>) and injects them only while this
    /// feature is active (<see cref="MasterDataFeature.AdmitPackLines"/>).
    /// </para>
    /// <para>
    /// <b>Gate at pick time.</b> The game's pools are hard-coded <c>RandomList</c> ranges (and one
    /// data-built small-talk cache). Harmony patches widen each one with the pool's pack episodes at
    /// the moment the game picks, and only with those whose language is shown and whose clips would
    /// make a sound: a language switch takes effect on the next pick, and in any other language the
    /// pool is the game's own, with the game's own odds and random draws.
    /// </para>
    /// <para>
    /// <b>Save.</b> Pack-line ids are kept out of the one save list they can reach
    /// (<see cref="PackLineSaveFilter"/>).
    /// </para>
    /// <para>
    /// <b>Fail soft.</b> A malformed line: one WARN naming the row, the line is left out. A missing
    /// seam: the feature stays off with one WARN, and no pack line reaches the game.
    /// </para>
    /// <para>
    /// Static and initialised once from the adapter's Awake (after master data and language), because
    /// the game destroys plugin components about 0.2 s after boot.
    /// </para>
    /// </remarks>
    public static class PackLinesFeature
    {
        /// <summary>Config section.</summary>
        public const string ConfigSection = "PackLines";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>The <c>src</c> of the SETUP line and the trace <c>sys</c>.</summary>
        public const string TraceSys = "packlines";

        private static readonly object Gate = new object();
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        private static ConfigEntry<bool>? _enabled;
        private static ConfigFile? _ownConfig;
        private static Harmony? _harmony;
        private static bool _initialized;
        private static bool _setupEmitted;

        /// <summary>The master switch (<c>[PackLines] Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>True when every seam verified and every patch is in place: pack lines may reach the game.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>Why the feature is off, or empty when it is active.</summary>
        public static string InactiveReason { get; private set; } = "not initialised";

        /// <summary>The accepted lines of the current injection. Never null.</summary>
        public static PackLineRegistry Registry => PackLinesRuntime.Registry;

        /// <summary>The language-exclusive groups the last injection refused. Never null.</summary>
        public static IReadOnlyList<PackLineRejection> Rejected { get; private set; } = new PackLineRejection[0];

        /// <summary>The console's armed timer test line.</summary>
        public static PackLineTestArm TestArm { get; } = new PackLineTestArm();

        /// <summary>How the seams checked: <c>targets: N ok, M missing</c>, or empty before the check.</summary>
        public static string TargetSummary { get; private set; } = string.Empty;

        /// <summary>
        /// Binds <c>[PackLines] Enabled</c>, registers the smoke checks and the <c>lines</c> command,
        /// verifies the seams, applies the patches and lets language-exclusive groups into the master
        /// data. Call once from the adapter plugin's Awake, after <c>MasterDataFeature.Initialize</c>
        /// and <c>LanguageFeature.Initialize</c>; a second call is ignored.
        /// </summary>
        /// <param name="config">The plugin's config; when omitted the feature binds its own <c>.packlines.cfg</c>.</param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug(PackLineRejection.LogPrefix + " already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
                BindConfig(config);
            }

            PackLinesSmokeChecks.RegisterAll();
            PackLinesCommands.Register();

            if (!Enabled)
            {
                InactiveReason = ConfigSection + "." + ConfigKeyEnabled + "=false";
                ConsoleEvents.Setup(TraceSys, "disabled (" + InactiveReason + "); language-exclusive lines stay out of the game");
                return;
            }

            if (!MasterDataFeature.Enabled)
            {
                InactiveReason = "master data is off (" + MasterDataFeature.ConfigSection + "." + MasterDataFeature.ConfigKeyEnabled + "=false)";
                ConsoleEvents.Setup(TraceSys, "off: " + InactiveReason);
                return;
            }

            // Registered for 'targets verify' and tools/seamcheck; checked here with one WARN for the
            // lot, because a missing seam is not an error the player can do anything about - the
            // feature simply stays off and every pool stays the game's own.
            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (pack lines)", PackLinesPatchTargets.All);
            var report = PatchTargetChecker.Check(PackLinesPatchTargets.All, new ReflectionMemberResolver());
            TargetSummary = report.Summarize();
            if (!report.AllFound)
            {
                var ids = new List<string>();
                foreach (var missing in report.MissingResults)
                {
                    ids.Add(missing.Target.Id);
                }

                TurnOff("missing seam(s): " + string.Join(", ", ids) + " (" + report.MissingResults[0].Detail + ")");
                return;
            }

            if (!PackLinesRuntime.Prepare(out var problem))
            {
                TurnOff(problem);
                return;
            }

            _harmony = new Harmony(PackLinesPatches.HarmonyId);
            if (!PackLinesPatches.Apply(_harmony, out problem))
            {
                TurnOff(problem);
                return;
            }

            PackLinesVoiceCheck.Resolve();

            IsActive = true;
            InactiveReason = string.Empty;
            MasterDataFeature.Injected += OnInjected;

            // Lets the exclusive groups into the master data; when the game's data is already loaded
            // this re-injects at once and OnInjected runs before this returns.
            MasterDataFeature.AdmitPackLines(true, TraceSys);

            ConsoleEvents.File(
                TraceSys,
                "armed",
                ("targets", TargetSummary),
                ("patches", PackLinesPatches.Detail),
                ("voices", PackLinesVoiceCheck.Detail));
        }

        /// <summary>
        /// After every injection: takes the screen's verdict, warns once per refused line, and swaps in
        /// the new lines when they changed.
        /// </summary>
        private static void OnInjected(string trigger)
        {
            try
            {
                var screen = MasterDataFeature.LineScreen;
                Rejected = screen.Rejected;
                foreach (var rejection in screen.Rejected)
                {
                    var text = rejection.ToString();
                    if (Warned.Add(text))
                    {
                        CwyLsModLoader.Log.LogWarning(text);
                    }
                }

                var registry = PackLineRegistry.Build(screen.Lines);
                foreach (var code in registry.Languages)
                {
                    // A typo such as "tj" is not an error the screen can see - the code is well formed -
                    // but those lines could never be offered, so say so once.
                    if (!SubtitleLanguages.IsInGame(code)
                        && !global::CwyLs.ModLoader.Bulbul.Language.LanguageFeature.Registry.TryFromCode(code, out _)
                        && Warned.Add("language:" + code))
                    {
                        CwyLsModLoader.Log.LogWarning(
                            PackLineRejection.LogPrefix + " pack lines name language '" + code + "', which neither the game nor any "
                            + "installed pack declares; they are never offered (" + registry.DescribePools(code) + ")");
                    }
                }

                var changed = !string.Equals(registry.Fingerprint, PackLinesRuntime.Registry.Fingerprint, StringComparison.Ordinal);
                if (changed || !_setupEmitted)
                {
                    PackLinesRuntime.Use(registry);
                    EmitSetup(registry, screen, trigger);
                }
                else
                {
                    // Same lines, new row objects: let the lottery hold the loader's current ones.
                    // The click and timer lists keep their cycle bookkeeping.
                    PackLinesRuntime.RefreshSmallTalk(trigger);
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    PackLineRejection.LogPrefix + " could not take the new pack lines (" + trigger + "): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EmitSetup(PackLineRegistry registry, PackLineScreenResult screen, string trigger)
        {
            _setupEmitted = true;
            var summary = registry.Count == 0
                ? (screen.Rejected.Count == 0 ? "ready; no pack declares a language-exclusive line" : "ready; every pack line was refused")
                : registry + " ready";

            ConsoleEvents.Setup(
                TraceSys,
                summary,
                ("pools", registry.Count == 0 ? "-" : registry.DescribePools()),
                ("languages", registry.Languages.Count == 0 ? "-" : string.Join("/", registry.Languages)),
                ("rejected", screen.Rejected.Count),
                ("patches", PackLinesPatches.Count),
                ("trigger", trigger));
        }

        private static void TurnOff(string reason)
        {
            IsActive = false;
            InactiveReason = reason;
            CwyLsModLoader.Log.LogWarning(
                PackLineRejection.LogPrefix + " off: " + reason
                + "; language-exclusive lines stay out of the game and every pool stays the game's own");
            ConsoleEvents.File(TraceSys, "off", ("reason", reason));
        }

        private static void BindConfig(ConfigFile? config)
        {
            var file = config ?? OwnConfig();
            _enabled = file.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Offer the new Satone lines content packs add for one language (a \"Languages\" member on a "
                + "scenario group) while the game shows that language: clicks, self-talk, timer lines, the "
                + "after-pomodoro talk and small talk. Off keeps every such line out of the game; the game's "
                + "own lines are never affected either way.");
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(BepInEx.Paths.ConfigPath, BulbulPlugin.PluginGuid + ".packlines.cfg"),
                saveOnInit: true);
    }
}
