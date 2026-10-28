namespace CwyLs.ModLoader.Core.Smoke
{
    /// <summary>Why a smoke run is happening, which decides whether the loud checks take part.</summary>
    /// <remarks>
    /// The run mode splits "someone asked" into <see cref="Quiet"/> and <see cref="Loud"/>. Asking for a run
    /// is not the same as asking the game to play a clip, repaint the character and leave her
    /// smiling at a motion nobody chose: the hotkey and the trigger file now produce a quiet run,
    /// and only the console command <c>smoke run loud</c> or the <c>smoke.loud</c> trigger file
    /// produce a loud one.
    /// </remarks>
    public enum SmokeRunMode
    {
        /// <summary>
        /// Nobody asked: the scene loaded, or the second pass came round. A run like this must be
        /// invisible and inaudible, so <see cref="SmokeCheck.Loud"/> checks are skipped.
        /// </summary>
        Automatic = 0,

        /// <summary>
        /// The old spelling of <see cref="Loud"/>: "someone asked, so run everything". Kept so
        /// code written against the old enum keeps compiling and keeps its old meaning; new code
        /// should say <see cref="Quiet"/> or <see cref="Loud"/>, which say what the player will
        /// notice rather than who asked.
        /// </summary>
        Explicit = 1,

        /// <summary>
        /// Someone asked, but only for the checks the player cannot notice: the <c>smoke.run</c>
        /// hotkey and the <c>BepInEx/smoke.run</c> trigger file. <see cref="SmokeCheck.Loud"/>
        /// checks are reported as skipped (<see cref="SmokeTriggers.QuietSkipDetail"/>).
        /// </summary>
        Quiet = 2,

        /// <summary>
        /// Someone asked for the whole suite, knowing it plays a clip and moves the character:
        /// the console command <c>smoke run loud</c> or the <c>BepInEx/smoke.loud</c> trigger
        /// file. Every loud check runs, and every loud check restores what it touched
        /// (<see cref="SmokeCheck.Restore"/>).
        /// </summary>
        Loud = 3,
    }
}
