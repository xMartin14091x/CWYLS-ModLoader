using System;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Trace;

namespace CwyLs.ModLoader.Logging
{
    /// <summary>
    /// RP-1: the handle the <c>trace</c> console command uses to read and change the trace. The trace
    /// plugin registers it in its <c>Awake</c>; the command used to look the plugin up through its
    /// component, which the game destroys about 0.2 s after boot.
    /// </summary>
    public static class TraceSwitch
    {
        private static ConfigEntry<TraceLevel>? _level;
        private static Func<TraceLevel>? _running;
        private static Action? _stopNow;

        /// <summary>Called once by the trace plugin.</summary>
        /// <param name="level">The bound <c>[Trace] Level</c> entry.</param>
        /// <param name="running">What this session is recording right now.</param>
        /// <param name="stopNow">Stops a running trace at once.</param>
        public static void Register(ConfigEntry<TraceLevel> level, Func<TraceLevel> running, Action stopNow)
        {
            _level = level;
            _running = running;
            _stopNow = stopNow;
        }

        /// <summary>Runs <c>trace &lt;args&gt;</c> and returns the reply.</summary>
        internal static string Run(System.Collections.Generic.IReadOnlyList<string> args)
        {
            if (_level is null || _running is null || _stopNow is null)
            {
                return "the trace plugin is not installed";
            }

            var plan = TraceToggle.Plan(args, _level.Value, _running());
            if (plan.StopNow)
            {
                _stopNow();
            }

            if (plan.Save is TraceLevel save && save != _level.Value)
            {
                _level.Value = save;
            }

            return plan.Message;
        }
    }
}
