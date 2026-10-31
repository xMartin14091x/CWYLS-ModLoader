using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core.PackLines;
using CwyLs.ModLoader.Threading;
using Framework = CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Bulbul.PackLines
{
    /// <summary>
    /// The console's <c>lines</c> command (pack lines): <c>lines list</c> shows every pack line, its
    /// pool and whether the game would offer it now; <c>lines test &lt;group id&gt;</c> plays one through
    /// the game's own pipeline for the final in-game check (a startup greeting only plays at launch,
    /// so for one it says how to hear it); <c>lines test off</c> disarms.
    /// </summary>
    internal static class PackLinesCommands
    {
        /// <summary>The command name.</summary>
        internal const string Name = "lines";

        /// <summary>Its usage line.</summary>
        internal const string Usage = "lines list | lines test <groupId> | lines test off";

        private static bool _registered;

        /// <summary>Registers the command with the framework console. Idempotent.</summary>
        internal static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            Framework.ConsoleFeature.Register(
                Name,
                "Pack lines offered only in their language: list them, or play one through the game's own pipeline.",
                Usage,
                global::CwyLs.ModLoader.Bulbul.Console.BulbulConsoleCommands.Group,
                Run);
        }

        private static string? Run(IReadOnlyList<string> args)
        {
            if (args.Count == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
            {
                return PackLineReport.List(
                    PackLinesFeature.Registry,
                    PackLinesFeature.Rejected,
                    LanguageFeature.CurrentCode,
                    PackLinesFeature.InactiveReason,
                    PackLinesRuntime.Describe,
                    PackLinesFeature.TestArm.Armed)
                    + "\nclip check: " + PackLinesVoiceCheck.Detail;
            }

            if (string.Equals(args[0], "test", StringComparison.OrdinalIgnoreCase))
            {
                return args.Count < 2 ? "usage: " + Usage : Test(args[1]);
            }

            return "usage: " + Usage;
        }

        private static string Test(string id)
        {
            if (string.Equals(id, "off", StringComparison.OrdinalIgnoreCase))
            {
                var was = PackLinesFeature.TestArm.Clear();
                return was is null ? "nothing was armed" : "disarmed " + was;
            }

            if (!PackLinesFeature.IsActive)
            {
                return "pack lines are off: " + PackLinesFeature.InactiveReason;
            }

            // A startup greeting (its group id, or the id a gamestart.any line was written under)
            // only plays at launch: say so rather than play it out of place.
            var startup = PackLinesGameStart.AnswerTest(id, MainThread.IsMainThread);
            if (startup != null)
            {
                return startup;
            }

            if (!MainThread.IsMainThread)
            {
                return "run 'lines test' from the in-game console (it drives the game's click reaction)";
            }

            if (!PackLinesFeature.Registry.TryGetByGroup(id, out var line) || line is null)
            {
                return "no pack line has group id '" + id + "'; 'lines list' shows them";
            }

            var code = LanguageFeature.CurrentCode;
            if (!line.IsOfferedIn(code))
            {
                return line.GroupId + " is offered in " + string.Join("/", line.Languages) + " only and the game shows "
                       + (code.Length == 0 ? "nothing yet" : code) + "; switch the language first - that gate is part of the test";
            }

            var silent = PackLinesVoiceCheck.FirstSilent(line);
            if (silent != null)
            {
                return line.GroupId + ": its clip " + silent + " would not make a sound yet (not loaded, and no game clip to fall back on); try again in a few seconds";
            }

            if (line.Pool.IsClickStyle)
            {
                return PackLinesRuntime.PlayAsClickReaction(line, out var detail)
                    ? "playing " + line + ": " + detail
                    : "not played: " + detail;
            }

            PackLinesFeature.TestArm.Arm(line, DateTime.Now);
            var mode = PackLinesRuntime.CurrentTimerMode();
            var note = mode != line.Pool.TimerMode && mode != PackLineTimerMode.None
                ? " (the timer is in " + mode + " mode now; switch it to " + line.Pool.TimerMode + ")"
                : string.Empty;
            return "armed " + line + ": " + PackLineReport.WhenArmedPlays(line.Pool) + note + "; 'lines test off' disarms";
        }
    }
}
