using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>What the <c>trace</c> console command should do, decided by <see cref="TraceToggle.Plan"/>.</summary>
    public sealed class TraceTogglePlan
    {
        internal TraceTogglePlan(TraceLevel? save, bool stopNow, string message)
        {
            Save = save;
            StopNow = stopNow;
            Message = message;
        }

        /// <summary>The level to write to <c>[Trace] Level</c>, or null to leave it as it is.</summary>
        public TraceLevel? Save { get; }

        /// <summary>True when the running trace must stop recording right away.</summary>
        public bool StopNow { get; }

        /// <summary>The reply for the console.</summary>
        public string Message { get; }
    }

    /// <summary>
    /// RP-1: the diagnostic trace is off unless a player turns it on, and turning it on or off is one
    /// short command. The trace patches the game at startup, so <c>on</c> and <c>full</c> start at the next
    /// launch; <c>off</c> also stops a running trace at once (its hooks stay in place but write nothing).
    /// </summary>
    public static class TraceToggle
    {
        /// <summary>The console usage line (also the Tab completions).</summary>
        public const string Usage = "trace on|off|full|status";

        /// <summary>Where the trace files go, relative to the game folder.</summary>
        public const string Folder = "BepInEx/trace";

        /// <summary>Decides what <c>trace &lt;args&gt;</c> does.</summary>
        /// <param name="args">The words after <c>trace</c>.</param>
        /// <param name="saved">The level in the config file (what the next launch uses).</param>
        /// <param name="running">What this session is recording: Off when the trace never started or was stopped.</param>
        public static TraceTogglePlan Plan(IReadOnlyList<string>? args, TraceLevel saved, TraceLevel running)
        {
            var word = args is null || args.Count == 0 ? "status" : args[0].Trim().ToLowerInvariant();

            // The old developer form, kept: trace level [off|events|trace].
            if (word == "level")
            {
                if (args!.Count < 2)
                {
                    return new TraceTogglePlan(null, false, Status(saved, running));
                }

                if (!Enum.TryParse<TraceLevel>(args[1].Trim(), true, out var level) || !Enum.IsDefined(typeof(TraceLevel), level))
                {
                    return new TraceTogglePlan(null, false, "'" + args[1] + "' is not a level; use off, events or trace");
                }

                word = level == TraceLevel.Off ? "off" : level == TraceLevel.Events ? "on" : "full";
            }

            switch (word)
            {
                case "status":
                    return new TraceTogglePlan(null, false, Status(saved, running));

                case "on":
                case "events":
                    return TurnOn(TraceLevel.Events, saved, running);

                case "full":
                case "trace":
                    return TurnOn(TraceLevel.Trace, saved, running);

                case "off":
                    if (running != TraceLevel.Off)
                    {
                        return new TraceTogglePlan(
                            TraceLevel.Off, true,
                            "trace off: stopped recording now, and it stays off from the next launch");
                    }

                    return new TraceTogglePlan(
                        TraceLevel.Off, false,
                        saved == TraceLevel.Off ? "trace is already off" : "trace off: it will not start at the next launch");

                default:
                    return new TraceTogglePlan(null, false, "usage: " + Usage);
            }
        }

        /// <summary>A short name for a level, as players see it.</summary>
        public static string Name(TraceLevel level) =>
            level == TraceLevel.Off ? "off" : level == TraceLevel.Events ? "on" : "full";

        private static TraceTogglePlan TurnOn(TraceLevel level, TraceLevel saved, TraceLevel running)
        {
            if (saved == level && running == level)
            {
                return new TraceTogglePlan(null, false, "trace is already " + Name(level) + "; files go to " + Folder);
            }

            var what = level == TraceLevel.Events
                ? "trace on (key events only; small files)"
                : "trace full (every game method; large files, slower game; for developers)";
            var when = running == level
                ? "; recording now, and it stays " + Name(level) + " from the next launch"
                : ": saved, starts at the next launch. Restart the game, repeat the problem, then send the newest file in " + Folder;
            return new TraceTogglePlan(level, false, what + when);
        }

        private static string Status(TraceLevel saved, TraceLevel running)
        {
            var now = running == TraceLevel.Off
                ? "trace is not recording"
                : "trace is recording (" + Name(running) + ") to " + Folder;
            var next = saved == running
                ? string.Empty
                : "; next launch: " + Name(saved);
            return now + next + ". Commands: " + Usage;
        }
    }
}
