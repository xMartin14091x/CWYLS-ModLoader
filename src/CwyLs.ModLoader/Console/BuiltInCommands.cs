using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Packs;
using CwyLs.ModLoader.Smoke;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// The commands the framework itself provides. Everything here is game-agnostic: packs, the
    /// smoke runner, the patch-target table, the key registry, the diagnostic bus and the console
    /// itself. Game commands come from the adapter (see <c>CwyLs.ModLoader.Bulbul.Console</c>).
    /// </summary>
    internal static class BuiltInCommands
    {
        /// <summary>Default file <c>dump</c> writes the scrollback to.</summary>
        internal const string DumpFileName = "cwy-console-dump.txt";

        /// <summary>Registers every built-in command. Idempotent: a name registered twice replaces itself.</summary>
        internal static void RegisterAll()
        {
            var registry = ConsoleFeature.Commands;
            var group = ConsoleFeature.FrameworkGroup;

            registry.Register(
                "help", "Lists the commands, or explains one.", "help [command]", group, Help);

            registry.Register(
                "commands", "Every command name on one line.", "commands", group, Commands);

            registry.Register(
                "version", "Framework, Unity and game versions, and what is loaded.", "version", group,
                _ => ConsoleViews.Version());

            registry.Register(
                "paths", "Where packs, traces, data and the smoke report live.", "paths", group,
                _ => ConsoleViews.Paths());

            registry.Register(
                "stats", "Frame rate, screen, scrollback and diagnostic-bus counters.", "stats", group,
                _ => ConsoleViews.Stats());

            registry.Register(
                "packs", "Lists the content packs, or scans the folder again.", "packs list|rescan",
                group, Packs);

            registry.Register(
                "smoke", "Runs the smoke checks quietly, loudly, or shows the last report.",
                "smoke run [quiet|loud]|report|list", group, Smoke);

            registry.Register(
                "targets", "The patch targets the framework depends on.", "targets verify", group,
                Targets);

            registry.Register(
                "keys", "Every bound hotkey and its two chords.", "keys", group,
                _ => ConsoleViews.Keys());

            registry.Register(
                "panels", "Lists the extra tabs, or prints one here.", "panels [name]", group, Panels);

            registry.Register(
                "trace", "Turns the diagnostic trace on or off (off by default).", Core.Trace.TraceToggle.Usage,
                group, Logging.TraceSwitch.Run);

            registry.Register(
                "bus", "Publishes an event on the diagnostic bus, or shows its counters.",
                "bus emit <sys> <name> [k=v ...] | bus stats", group, Bus);

            registry.Register(
                "log", "Writes a line to the BepInEx log (and back into this console).",
                "log <text>", group, Log);

            registry.Register(
                "dump", "Writes the scrollback to a file.", "dump [path]", group, Dump);

            registry.Register(
                "clear", "Empties the scrollback.", "clear", group, Clear);
        }

        private static string Help(IReadOnlyList<string> args) =>
            ConsoleFeature.Commands.Help(args.Count > 0 ? args[0] : null);

        private static string Commands(IReadOnlyList<string> args) =>
            string.Join(" ", new List<string>(ConsoleFeature.Commands.Names).ToArray());

        private static string Packs(IReadOnlyList<string> args)
        {
            var what = args.Count > 0 ? args[0] : "list";

            if (string.Equals(what, "rescan", StringComparison.OrdinalIgnoreCase))
            {
                var ok = PackManager.Rescan();
                return (ok ? "rescanned" : "rescan failed: " + PackManager.LastScanError)
                    + "\n" + ConsoleViews.Packs();
            }

            if (string.Equals(what, "list", StringComparison.OrdinalIgnoreCase))
            {
                return ConsoleViews.Packs();
            }

            return "packs list|rescan";
        }

        private static string Smoke(IReadOnlyList<string> args)
        {
            var what = args.Count > 0 ? args[0] : "run";

            if (string.Equals(what, "report", StringComparison.OrdinalIgnoreCase))
            {
                return ConsoleViews.Smoke();
            }

            if (string.Equals(what, "list", StringComparison.OrdinalIgnoreCase))
            {
                var checks = Core.Smoke.SmokeRegistry.Snapshot();
                var sb = new StringBuilder(checks.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" check(s)");
                foreach (var check in checks)
                {
                    sb.Append("\n  ").Append(check.Phase).Append("  ").Append(check.Id)
                        .Append(check.Loud ? "  (loud)" : string.Empty);
                }

                return sb.ToString();
            }

            if (!string.Equals(what, "run", StringComparison.OrdinalIgnoreCase))
            {
                return "smoke run [quiet|loud]  |  smoke report  |  smoke list";
            }

            var how = args.Count > 1 ? args[1] : Core.Smoke.SmokeTriggers.QuietLabel;
            var loud = string.Equals(how, Core.Smoke.SmokeTriggers.LoudLabel, StringComparison.OrdinalIgnoreCase);

            if (!loud && !string.Equals(how, Core.Smoke.SmokeTriggers.QuietLabel, StringComparison.OrdinalIgnoreCase))
            {
                return "'" + how + "' is not a mode; use 'smoke run quiet' (the default) or 'smoke run loud'";
            }

            var runner = SmokeHost.EnsureRunner();
            if (runner == null)
            {
                return "there is no smoke runner, so nothing can be run (see the startup log)";
            }

            // 'smoke run' is quiet, like the key and the trigger file. 'smoke run loud' is the
            // one path that plays a clip and moves the character - and every loud check puts back
            // what it touched afterwards.
            var mode = loud ? Core.Smoke.SmokeRunMode.Loud : Core.Smoke.SmokeRunMode.Quiet;
            runner.RunNow(Core.Smoke.SmokeTriggers.ConsoleTrigger, null, mode);

            return "smoke run (" + Core.Smoke.SmokeTriggers.Label(mode)
                + ") requested; the result is in the log and in " + SmokeHost.ReportPath;
        }

        private static string Targets(IReadOnlyList<string> args) =>
            ConsoleViews.Targets(args.Count > 0
                && string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase));

        private static string Panels(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
            {
                var names = ConsolePanels.Names;
                return names.Count == 0
                    ? "no panels are registered"
                    : names.Count + " panel(s): " + string.Join(", ", new List<string>(names).ToArray());
            }

            ConsolePanels.TryRender(args[0], out var text);
            return text;
        }

        private static string Bus(IReadOnlyList<string> args)
        {
            var what = args.Count > 0 ? args[0] : "stats";

            if (string.Equals(what, "stats", StringComparison.OrdinalIgnoreCase))
            {
                return "bus emitted=" + TraceBus.Emitted
                    + " buffered=" + TraceBus.Buffered
                    + " dropped=" + TraceBus.Dropped
                    + " consumer=" + (TraceBus.HasConsumer ? "attached" : "none");
            }

            if (!string.Equals(what, "emit", StringComparison.OrdinalIgnoreCase) || args.Count < 3)
            {
                return "bus emit <sys> <name> [k=v ...]  |  bus stats";
            }

            var fields = new List<(string Key, object? Value)>();
            var rejected = new List<string>();

            for (var i = 3; i < args.Count; i++)
            {
                var at = args[i].IndexOf('=');
                if (at <= 0)
                {
                    rejected.Add(args[i]);
                    continue;
                }

                fields.Add((args[i].Substring(0, at), Value(args[i].Substring(at + 1))));
            }

            TraceBus.EmitFields(args[1], args[2], fields.ToArray());

            var line = "emitted " + args[1] + "/" + args[2] + " with " + fields.Count + " field(s)";
            return rejected.Count == 0
                ? line
                : line + "; ignored (not k=v): " + string.Join(", ", rejected.ToArray());
        }

        private static string Log(IReadOnlyList<string> args)
        {
            var text = CommandLineParser.Join(args);
            if (text.Length == 0)
            {
                return "log <text>";
            }

            // No echo of our own: the log listener mirrors this straight back into the scrollback,
            // which is also the proof that the mirror is working. It goes through the one sink, so the
            // echo is a record in the canonical grammar like everything else.
            Logging.ConsoleMirror.Print(
                new ConsoleRecord(DateTimeOffset.Now, ConsoleTags.Console, text));
            return string.Empty;
        }

        private static string Dump(IReadOnlyList<string> args)
        {
            var path = args.Count > 0
                ? CommandLineParser.Join(args)
                : Path.Combine(BepInEx.Paths.BepInExRootPath, DumpFileName);

            try
            {
                File.WriteAllText(path, ConsoleFeature.Output.ToText());
                return "wrote " + ConsoleFeature.Output.Count + " line(s) to " + path;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{ConsoleFeature.LogPrefix} dump to '{path}' failed: {ex.GetType().Name}: {ex.Message}");
                return "could not write " + path + ": " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static string Clear(IReadOnlyList<string> args)
        {
            ConsoleFeature.Output.Clear();
            return string.Empty;
        }

        private static object? Value(string raw)
        {
            if (bool.TryParse(raw, out var flag))
            {
                return flag;
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
            {
                return whole;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }

            return raw;
        }
    }
}
