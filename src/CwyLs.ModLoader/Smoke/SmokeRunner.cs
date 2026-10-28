using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Smoke;
using UnityEngine;
using UnityEngine.SceneManagement;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Smoke
{
    /// <summary>
    /// Runs the registered smoke checks in-game and writes the report. Lives on the hidden
    /// DontDestroyOnLoad object created by <see cref="SmokeHost.EnsureRunner"/>; nothing else
    /// should add it.
    /// <para>
    /// Four triggers ask for a run, split by how loud the answer is:
    /// </para>
    /// <list type="table">
    /// <item><term><c>smoke.run</c> hotkey (Ctrl+Shift+S | F9)</term>
    /// <description><see cref="SmokeRunMode.Quiet"/></description></item>
    /// <item><term><c>BepInEx/smoke.run</c> file</term>
    /// <description><see cref="SmokeRunMode.Quiet"/></description></item>
    /// <item><term><c>smoke run loud</c> in the console, or <c>BepInEx/smoke.loud</c></term>
    /// <description><see cref="SmokeRunMode.Loud"/></description></item>
    /// <item><term>the scene load, only when <c>Smoke.AutoRunOnRoomScene</c> is on</term>
    /// <description><see cref="SmokeRunMode.Automatic"/></description></item>
    /// </list>
    /// <para>
    /// A quiet run reports every <see cref="SmokeCheck.Loud"/> check as skipped instead of playing
    /// a clip and moving the character; a loud run runs them and then runs each check's
    /// <see cref="SmokeCheck.Restore"/> in a <c>finally</c>, so the player is left where she was.
    /// </para>
    /// </summary>
    public sealed class SmokeRunner : MonoBehaviour
    {
        /// <summary>Fixed tag stamped into every report this build writes.</summary>
        public const string Ticket = "P0-2";

        /// <summary>Label the second automatic pass logs itself under.</summary>
        public const string SecondPassLabel = "second pass";

        private readonly string _session = Guid.NewGuid().ToString("N");

        private ConfigEntry<bool>? _autoRun;
        private ConfigEntry<bool>? _hotkeyEnabled;
        private ConfigEntry<float>? _secondPassSeconds;
        private Coroutine? _pending;
        private float _sinceTriggerPoll;
        private bool _triggerFileWarned;

        /// <summary>Id of the game session; the same across re-runs, so re-runs are recognisable.</summary>
        public string Session => _session;

        /// <summary>
        /// Whether a <see cref="SmokeHost.SceneName"/> load starts a run on its own. Default false:
        /// see <see cref="SmokeTriggers"/> for why the framework stopped running itself.
        /// </summary>
        public bool AutoRunOnRoomScene =>
            _autoRun?.Value ?? SmokeTriggers.DefaultAutoRunOnRoomScene;

        /// <summary>
        /// Seconds after the scene load at which the automatic second pass runs; 0 turns it off,
        /// which is the default. Only meaningful when <see cref="AutoRunOnRoomScene"/> is on.
        /// </summary>
        public float SecondPassSeconds =>
            _secondPassSeconds?.Value ?? SmokePassSchedule.DefaultSecondPassSeconds;

        /// <summary>
        /// Whether the <c>smoke.run</c> key is bound at all (<c>Smoke.HotkeyEnabled</c>, default
        /// true). Off leaves the trigger files and the console command, and frees the chord for
        /// the game. Read once, when <see cref="SmokeHost.EnsureRunner"/> binds the key.
        /// </summary>
        public bool HotkeyEnabled =>
            _hotkeyEnabled?.Value ?? SmokeTriggers.DefaultHotkeyEnabled;

        /// <summary>
        /// Binds the automatic-run settings. Called once by <see cref="SmokeHost.EnsureRunner"/>;
        /// until it runs, the defaults apply and only the explicit triggers are live.
        /// </summary>
        internal void Configure(ConfigFile config)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _autoRun = config.Bind(
                SmokeHost.ConfigSection,
                SmokeHost.ConfigKeyAutoRun,
                SmokeTriggers.DefaultAutoRunOnRoomScene,
                "Run every check automatically a few seconds after " + SmokeHost.SceneName +
                " loads. Off by default: a run plays the test clip and repaints the character, " +
                "which is not something the game should do unasked. Press the " +
                SmokeTriggers.RunHotkeyId + " key or create BepInEx/" +
                SmokeTriggers.TriggerFileName + " to run the checks instead. An automatic run " +
                "skips the loud checks.");

            _hotkeyEnabled = config.Bind(
                SmokeHost.ConfigSection,
                SmokeHost.ConfigKeyHotkeyEnabled,
                SmokeTriggers.DefaultHotkeyEnabled,
                "Bind the " + SmokeTriggers.RunHotkeyId + " key. Off means no smoke key at all: " +
                "the trigger files (BepInEx/" + SmokeTriggers.TriggerFileName + ", BepInEx/" +
                SmokeTriggers.LoudTriggerFileName + ") and the console command still work. " +
                "Takes effect at the next launch, since the key is bound at startup.");

            _secondPassSeconds = config.Bind(
                SmokeHost.ConfigSection,
                SmokeHost.ConfigKeySecondPassSeconds,
                SmokePassSchedule.DefaultSecondPassSeconds,
                "Seconds after " + SmokeHost.SceneName + " loads at which an automatic run is " +
                "repeated. Checks that report what has been observed this session cannot pass " +
                "three seconds into a session, so " +
                SmokePassSchedule.SuggestedSecondPassSeconds.ToString("0") +
                " is the value that makes a hands-off launch end green. 0 (the default) turns " +
                "the second pass off; it does nothing unless " + SmokeHost.ConfigKeyAutoRun +
                " is on.");
        }

        private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;

        private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        private void Start()
        {
            // A runner created after the scene is already up would otherwise wait for a sceneLoaded
            // that never comes again.
            if (SceneManager.GetActiveScene().name == SmokeHost.SceneName)
            {
                Schedule("active-scene");
            }
        }

        private void Update()
        {
            // The key is polled by CwyLs.ModLoader.Input.Hotkeys; this is only the file trigger, which
            // is what lets the orchestrator ask for a run without the game having focus.
            _sinceTriggerPoll += Time.unscaledDeltaTime;
            if (_sinceTriggerPoll < SmokeTriggers.TriggerFilePollSeconds)
            {
                return;
            }

            _sinceTriggerPoll = 0f;

            // Quiet first: if both files are there, the loud run is the one that has to happen,
            // and it runs everything the quiet one would have.
            var quiet = TryTakeTriggerFile(SmokeHost.TriggerFilePath);
            var loud = TryTakeTriggerFile(SmokeHost.LoudTriggerFilePath);

            if (loud)
            {
                RunNow(SmokeTriggers.FileTrigger, null, SmokeRunMode.Loud);
            }
            else if (quiet)
            {
                RunNow(SmokeTriggers.FileTrigger, null, SmokeRunMode.Quiet);
            }
        }

        /// <summary>
        /// True when <paramref name="path"/> existed and has been deleted - deleting it before the
        /// run is what stops a check that takes a second from being asked for twice.
        /// </summary>
        private bool TryTakeTriggerFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                // Once: the poll repeats twice a second, and a folder we cannot write to would
                // otherwise fill the log with the same line for the rest of the session.
                if (!_triggerFileWarned)
                {
                    _triggerFileWarned = true;
                    CwyLsModLoader.Log.LogWarning(
                        $"smoke trigger file ({path}) cannot be used: {ex.GetType().Name}: {ex.Message}");
                }

                return false;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == SmokeHost.SceneName)
            {
                Schedule(SmokeTriggers.SceneTrigger);
            }
        }

        private void Schedule(string trigger)
        {
            if (!AutoRunOnRoomScene)
            {
                return;
            }

            // The game loads a Transition scene around RoomScene, so the same load can be seen more
            // than once; one window at a time is enough. The window covers both passes, so a scene
            // load arriving while the second pass is still pending is absorbed by it rather than
            // starting a rival pair of runs - the pending pass re-runs every check anyway.
            if (_pending != null)
            {
                return;
            }

            _pending = StartCoroutine(RunAfterSettle(trigger));
        }

        private IEnumerator RunAfterSettle(string trigger)
        {
            // Real time: the checks must still run if the game is paused with timeScale 0.
            yield return new WaitForSecondsRealtime(SmokeHost.SettleSeconds);

            RunNow(trigger, null, SmokeRunMode.Automatic);

            // The second pass is part of the same scheduled window, so _pending stays set until it
            // is done: a Transition scene load arriving in between must not start a rival window.
            if (SmokePassSchedule.TryPlanSecondPass(
                    SmokeHost.SettleSeconds, SecondPassSeconds, out var delay))
            {
                yield return new WaitForSecondsRealtime(delay);
                RunNow(trigger, SecondPassLabel, SmokeRunMode.Automatic);
            }

            _pending = null;
        }

        /// <summary>
        /// Runs every registered check now as a <see cref="SmokeRunMode.Quiet"/> run and rewrites
        /// the report. Safe to call at any time; failures are reported, never thrown.
        /// <para>
        /// This used to be a loud run. The checks that play a clip and move the character now
        /// need <see cref="SmokeRunMode.Loud"/> - pass it to the three-argument overload, which is
        /// what <c>smoke run loud</c> does.
        /// </para>
        /// </summary>
        /// <param name="trigger">What asked for this run, for the log line.</param>
        public void RunNow(string trigger) => RunNow(trigger, null, SmokeRunMode.Quiet);

        /// <summary>
        /// Runs the registered checks now and rewrites the report, naming the pass in the
        /// <c>SMOKE</c> line. Safe to call at any time; failures are reported, never thrown.
        /// </summary>
        /// <param name="trigger">What asked for this run, for the log line.</param>
        /// <param name="passLabel">
        /// Name of the pass, e.g. <see cref="SecondPassLabel"/>; null for the ordinary first pass.
        /// </param>
        /// <param name="mode">
        /// How loud this run is allowed to be. <see cref="SmokeRunMode.Quiet"/> and
        /// <see cref="SmokeRunMode.Automatic"/> skip the <see cref="SmokeCheck.Loud"/> checks
        /// instead of playing a clip behind the player's back; <see cref="SmokeRunMode.Loud"/>
        /// runs them and restores what they touched.
        /// </param>
        public void RunNow(string trigger, string? passLabel, SmokeRunMode mode)
        {
            var pass = string.IsNullOrEmpty(passLabel) ? string.Empty : " (" + passLabel + ")";

            try
            {
                var checks = SmokeRegistry.Snapshot();
                var results = new List<SmokeCheckResult>(checks.Count);
                var skipped = 0;

                foreach (var check in checks)
                {
                    if (!SmokeTriggers.Includes(check.Loud, mode))
                    {
                        skipped++;
                        results.Add(SmokeCheckResult.Skipped(check.Id, SmokeTriggers.SkipDetail(mode)));
                        continue;
                    }

                    // ExecuteAndRestore() times the check, turns any exception into a failed
                    // result, and runs the check's undo in a finally - which is what stops a loud
                    // run from leaving Satone smiling at a motion nobody chose.
                    results.Add(check.ExecuteAndRestore(OnRestoreFailed));
                }

                var report = new SmokeReport(
                    _session, Ticket, Application.unityVersion, Application.version, DateTimeOffset.Now, results);

                // ONE line per run (it used to be two: a "smoke run (...)" line and a
                // "SMOKE ok=..." line saying the same thing), plus one line per real failure.
                // A not-yet-observed check is the normal state of a fresh session and stays in the
                // file - it painted a healthy session red in the viewer.
                ConsoleEvents.Emit(
                    "smoke",
                    "run",
                    ConsoleTags.Smoke,
                    "ok=" + (report.Ok ? "true" : "false"),
                    "passed,mode,trigger,skipped,session,pass",
                    ("ok", report.Ok),
                    ("passed", report.Passed.ToString(CultureInfo.InvariantCulture)
                        + "/" + report.Total.ToString(CultureInfo.InvariantCulture)),
                    ("mode", SmokeTriggers.Label(mode)),
                    ("trigger", trigger),
                    ("skipped", skipped),
                    ("session", _session),
                    ("pass", string.IsNullOrEmpty(passLabel) ? null : passLabel));

                if (report.Total == 0)
                {
                    CwyLsModLoader.Log.LogWarning($"SMOKE{pass} {report.Detail}");
                }

                foreach (var result in report.Checks)
                {
                    if (result.Ok)
                    {
                        continue;
                    }

                    if (result.Informational)
                    {
                        CwyLsModLoader.Log.LogDebug($"SMOKE not yet {result.Id}: {result.Detail}");
                        continue;
                    }

                    ConsoleEvents.EmitAt(
                        "smoke",
                        "failed",
                        ConsoleTags.Smoke,
                        ConsoleLevel.Error,
                        "FAIL " + result.Id,
                        "detail",
                        ("id", result.Id),
                        ("detail", result.Detail));
                }

                Write(report);
            }
            catch (Exception ex)
            {
                // A broken reporter must never take the game down with it.
                CwyLsModLoader.Log.LogError(
                    $"smoke run failed ({trigger}){pass}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// A check whose undo threw. The run carries on: a restore that fails is worth a line, not
        /// a half-finished report.
        /// </summary>
        private static void OnRestoreFailed(SmokeCheck check, Exception ex) =>
            CwyLsModLoader.Log.LogWarning(
                $"SMOKE restore failed {check.Id}: {ex.GetType().Name}: {ex.Message}");

        private static void Write(SmokeReport report)
        {
            var path = SmokeHost.ReportPath;
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                File.WriteAllText(path, SmokeReportWriter.ToJson(report));
                CwyLsModLoader.Log.LogDebug($"smoke report written: {path}");
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError($"smoke report failed ({path}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
