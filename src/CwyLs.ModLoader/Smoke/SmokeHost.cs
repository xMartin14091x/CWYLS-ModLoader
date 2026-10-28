using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Input;
using UnityEngine;

namespace CwyLs.ModLoader.Smoke
{
    /// <summary>
    /// Owns the single <see cref="SmokeRunner"/> for the process. Any plugin can call
    /// <see cref="EnsureRunner"/> on Awake without knowing whether another one already did;
    /// checks themselves go to <see cref="Core.Smoke.SmokeRegistry"/>, not here.
    /// <para>
    /// The first caller wins: it creates the runner and its <c>Config</c> is where the
    /// <c>Smoke.*</c> entries and the <c>smoke.run</c> hotkey are bound.
    /// <c>ModLoaderPlugin.Awake</c> is that caller, and every other plugin here hard-depends on it,
    /// so the settings land in one .cfg (<c>cwyls.modloader.cfg</c>) rather than
    /// wherever the load order happened to put them.
    /// </para>
    /// <para>
    /// A run is something someone asks for. Nothing here starts one unless
    /// <c>Smoke.AutoRunOnRoomScene</c> is turned on - the checks play the test clip and repaint
    /// the character, and doing that unannounced three seconds into every session reads as the
    /// game misbehaving.
    /// </para>
    /// <para>
    /// Asking is not the same as asking for a show. The <c>smoke.run</c> key and the
    /// <c>smoke.run</c> file produce a <see cref="SmokeRunMode.Quiet"/> run; only
    /// <c>smoke run loud</c> in the console and the <c>smoke.loud</c> file run the loud checks.
    /// </para>
    /// </summary>
    public static class SmokeHost
    {
        /// <summary>The scene whose load starts a run when automatic runs are enabled. Matches the game's <c>EntryBehavior</c> (game 1.17.x).</summary>
        public const string SceneName = "RoomScene";

        /// <summary>Seconds of real time to let the scene settle before checking it.</summary>
        public const float SettleSeconds = 3f;

        /// <summary>Config section for the smoke settings.</summary>
        public const string ConfigSection = "Smoke";

        /// <summary>Config key of the automatic run on <see cref="SceneName"/>.</summary>
        public const string ConfigKeyAutoRun = "AutoRunOnRoomScene";

        /// <summary>Config key of the automatic second pass.</summary>
        public const string ConfigKeySecondPassSeconds = "SecondPassSeconds";

        /// <summary>Config key of the <c>smoke.run</c> hotkey's master switch.</summary>
        public const string ConfigKeyHotkeyEnabled = "HotkeyEnabled";

        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        public const string RunnerObjectName = "CWY.SmokeRunner";

        /// <summary>
        /// Default primary chord of <c>smoke.run</c>: Ctrl+Shift+S. The chord is primary on
        /// macOS and Windows, because on macOS an F-key is a media key unless Fn is held.
        /// </summary>
        public static readonly KeyboardShortcut RunKeyDefault =
            new KeyboardShortcut(KeyCode.S, KeyCode.LeftControl, KeyCode.LeftShift);

        /// <summary>Default alternate chord of <c>smoke.run</c>: F9, which works on Windows.</summary>
        public static readonly KeyboardShortcut RunKeyAlternateDefault = new KeyboardShortcut(KeyCode.F9);

        /// <summary>The primary default older builds wrote; see <see cref="Core.Input.HotkeyDefaults"/>.</summary>
        public static readonly KeyboardShortcut RunKeyLegacyDefault = new KeyboardShortcut(KeyCode.F9);

        /// <summary>The alternate default older builds wrote.</summary>
        public static readonly KeyboardShortcut RunKeyLegacyAlternateDefault =
            new KeyboardShortcut(KeyCode.S, KeyCode.LeftControl, KeyCode.LeftShift);

        private static readonly object Gate = new object();

        private static SmokeRunner? _runner;
        private static ConfigFile? _ownConfig;

        /// <summary>Where the report is written: <c>&lt;BepInEx&gt;/smoke-report.json</c>.</summary>
        public static string ReportPath => Path.Combine(Paths.BepInExRootPath, "smoke-report.json");

        /// <summary>
        /// The quiet trigger file: <c>&lt;BepInEx&gt;/smoke.run</c>. Creating it asks for a run and
        /// the runner deletes it, so a run can be triggered without the game having keyboard focus.
        /// </summary>
        public static string TriggerFilePath =>
            Path.Combine(Paths.BepInExRootPath, SmokeTriggers.TriggerFileName);

        /// <summary>
        /// The loud trigger file: <c>&lt;BepInEx&gt;/smoke.loud</c>. Same mechanism as
        /// <see cref="TriggerFilePath"/>, but the run includes the checks that play a clip and
        /// move the character - and every one of those puts back what it touched.
        /// </summary>
        public static string LoudTriggerFilePath =>
            Path.Combine(Paths.BepInExRootPath, SmokeTriggers.LoudTriggerFileName);

        /// <summary>
        /// Creates the hidden, scene-surviving runner once and returns it; later calls return the
        /// same instance. Also binds the <c>smoke.run</c> hotkey. Returns null only if the
        /// GameObject could not be created, which is logged - callers may treat null as "no report
        /// will be written".
        /// </summary>
        /// <param name="config">
        /// Config file the <c>Smoke.*</c> entries and the <c>[Keys] smoke.run.*</c> chords are
        /// bound in. Pass the calling plugin's <c>Config</c>; when omitted the framework binds
        /// them in its own .cfg instead.
        /// </param>
        public static SmokeRunner? EnsureRunner(ConfigFile? config = null)
        {
            lock (Gate)
            {
                // Unity's == also catches a runner whose GameObject was destroyed, so a lost
                // runner is rebuilt instead of handing back a dead reference.
                if (_runner != null)
                {
                    return _runner;
                }

                var owner = config ?? OwnConfig();

                try
                {
                    var host = new GameObject(RunnerObjectName);
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;

                    var runner = host.AddComponent<SmokeRunner>();
                    runner.Configure(owner);
                    _runner = runner;

                    if (runner.HotkeyEnabled)
                    {
                        Hotkeys.Register(
                            SmokeTriggers.RunHotkeyId,
                            "Runs every registered smoke check and rewrites " +
                            "BepInEx/smoke-report.json. A quiet run: the checks that play a clip " +
                            "or move the character need `" + SmokeTriggers.LoudCommand + "`.",
                            RunKeyDefault,
                            RunKeyAlternateDefault,
                            () => runner.RunNow(SmokeTriggers.KeyTrigger, null, SmokeRunMode.Quiet),
                            owner,
                            RunKeyLegacyDefault,
                            RunKeyLegacyAlternateDefault);
                    }

                    CwyLsModLoader.Log.LogDebug(
                        $"smoke runner armed: {Describe(runner)} -> {ReportPath}");
                    return runner;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        $"smoke runner could not be created: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>What will and will not start a run, for the one startup line.</summary>
        private static string Describe(SmokeRunner runner)
        {
            var key = runner.HotkeyEnabled
                ? SmokeTriggers.RunHotkeyId + " key"
                : SmokeTriggers.RunHotkeyId + " key off (" + ConfigSection + "." +
                  ConfigKeyHotkeyEnabled + "=false)";

            var triggers =
                $"on demand, quiet ({key}, or touch {TriggerFilePath}); " +
                $"loud (`{SmokeTriggers.LoudCommand}`, or touch {LoudTriggerFilePath})";

            if (!runner.AutoRunOnRoomScene)
            {
                return triggers + "; no automatic run";
            }

            var second = runner.SecondPassSeconds;
            var secondPass = SmokePassSchedule.IsEnabled(second) ? $" + {second:0.#}s" : " (second pass off)";
            return triggers + $"; automatic on '{SceneName}' + {SettleSeconds:0.#}s{secondPass}";
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(Paths.ConfigPath, CwyLsModLoader.PluginGuid + ".cfg"), saveOnInit: true);
    }
}
