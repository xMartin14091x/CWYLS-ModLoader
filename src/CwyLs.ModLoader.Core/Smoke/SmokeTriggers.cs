namespace CwyLs.ModLoader.Core.Smoke
{
    /// <summary>
    /// What may start a smoke run, and what a run does with the loud checks. Pure constants and
    /// three decisions, so all of them can be tested off-engine.
    /// <para>
    /// The framework moved from "run itself twice every launch" to "run when asked". The old
    /// behaviour played the test beep and stripped and re-applied Satone's textures three seconds
    /// into every session and again a minute later, which reads as the game doing something
    /// random. Automatic runs are off by default; the hotkey and the trigger file are the two ways
    /// to ask for one.
    /// </para>
    /// <para>
    /// The run mode finished the job: asking for a run is no longer the same as asking for a show. The
    /// hotkey and <c>smoke.run</c> produce a <see cref="SmokeRunMode.Quiet"/> run; the console
    /// command <c>smoke run loud</c> and the <see cref="LoudTriggerFileName"/> file are the only
    /// two <see cref="SmokeRunMode.Loud"/> paths.
    /// </para>
    /// </summary>
    public static class SmokeTriggers
    {
        /// <summary>Registry id of the "run the smoke checks now" hotkey.</summary>
        public const string RunHotkeyId = "smoke.run";

        /// <summary>
        /// File whose existence under the BepInEx folder asks for a quiet run. It is deleted as
        /// soon as the run starts, so the orchestrator can trigger a run without the game having
        /// keyboard focus: <c>touch BepInEx/smoke.run</c>.
        /// </summary>
        public const string TriggerFileName = "smoke.run";

        /// <summary>
        /// File whose existence under the BepInEx folder asks for a <see cref="SmokeRunMode.Loud"/>
        /// run - the one that plays a clip and moves the character. Deleted as soon as the run
        /// starts, exactly like <see cref="TriggerFileName"/>: <c>touch BepInEx/smoke.loud</c>.
        /// </summary>
        public const string LoudTriggerFileName = "smoke.loud";

        /// <summary>How often the runner looks for the trigger files, in seconds.</summary>
        public const float TriggerFilePollSeconds = 0.5f;

        /// <summary>Default of <c>Smoke.AutoRunOnRoomScene</c>: the room loading does NOT run the checks.</summary>
        public const bool DefaultAutoRunOnRoomScene = false;

        /// <summary>Default of <c>Smoke.HotkeyEnabled</c>: the <c>smoke.run</c> key is bound.</summary>
        public const bool DefaultHotkeyEnabled = true;

        /// <summary>Trigger label written by the scene-load run.</summary>
        public const string SceneTrigger = "sceneLoaded";

        /// <summary>Trigger label written by the hotkey run.</summary>
        public const string KeyTrigger = "key";

        /// <summary>Trigger label written by a trigger-file run (either file; the mode says which).</summary>
        public const string FileTrigger = "file";

        /// <summary>Trigger label written by a run asked for in the in-game console.</summary>
        public const string ConsoleTrigger = "console";

        /// <summary>The console command that asks for the loud run, quoted in every skip detail.</summary>
        public const string LoudCommand = "smoke run loud";

        /// <summary>What a loud check reports instead of running, in an automatic run.</summary>
        public const string LoudSkipDetail =
            "skipped (automatic run; use `" + LoudCommand + "` to include)";

        /// <summary>What a loud check reports instead of running, in a quiet run.</summary>
        public const string QuietSkipDetail =
            "skipped (quiet run; use `" + LoudCommand + "`)";

        /// <summary>Mode name used in the <c>smoke run (key, quiet)</c> log line.</summary>
        public const string AutomaticLabel = "auto";

        /// <summary>Mode name used in the <c>smoke run (key, quiet)</c> log line.</summary>
        public const string QuietLabel = "quiet";

        /// <summary>Mode name used in the <c>smoke run (console, loud)</c> log line.</summary>
        public const string LoudLabel = "loud";

        /// <summary>
        /// Whether a check takes part in a run.
        /// </summary>
        /// <param name="loud">Whether the check is <see cref="SmokeCheck.Loud"/>.</param>
        /// <param name="mode">Why the run is happening.</param>
        /// <returns>True for every quiet check, and for a loud check only in a loud run.</returns>
        public static bool Includes(bool loud, SmokeRunMode mode) => !loud || IsLoudRun(mode);

        /// <summary>True when <paramref name="mode"/> is a run that may make noise.</summary>
        /// <param name="mode">Why the run is happening.</param>
        public static bool IsLoudRun(SmokeRunMode mode) =>
            mode == SmokeRunMode.Loud || mode == SmokeRunMode.Explicit;

        /// <summary>The one-word mode name for the log line: <c>auto</c>, <c>quiet</c> or <c>loud</c>.</summary>
        /// <param name="mode">Why the run is happening.</param>
        public static string Label(SmokeRunMode mode) => mode switch
        {
            SmokeRunMode.Automatic => AutomaticLabel,
            SmokeRunMode.Quiet => QuietLabel,
            _ => LoudLabel,
        };

        /// <summary>
        /// What a skipped loud check reports, worded for the run that skipped it.
        /// </summary>
        /// <param name="mode">Why the run is happening; a loud run never skips, so it never asks.</param>
        public static string SkipDetail(SmokeRunMode mode) =>
            mode == SmokeRunMode.Quiet ? QuietSkipDetail : LoudSkipDetail;
    }
}
