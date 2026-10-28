using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Scenario;
using Framework = CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// The console's <c>scene</c> command (script runtime): <c>scene list</c> (pack scenes, commands, runtime
    /// state), <c>scene lint [groupId]</c>, <c>scene play &lt;groupId|rowId&gt;</c>, <c>scene flags</c>.
    /// </summary>
    internal static class ScenarioCommands
    {
        /// <summary>The command name.</summary>
        internal const string Name = "scene";

        /// <summary>Its usage line.</summary>
        internal const string Usage = "scene list | scene lint [groupId] | scene play <groupId|rowId|game click line|game episode> | scene unseen on|off | scene flags";

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
                "Pack scenes and script commands: list them, lint them, play one, show the flags.",
                Usage,
                global::CwyLs.ModLoader.Bulbul.Console.BulbulConsoleCommands.Group,
                Run);
        }

        private static string? Run(IReadOnlyList<string> args)
        {
            var verb = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
            switch (verb)
            {
                case "list":
                    return List();
                case "lint":
                    return LintText(args.Count > 1 ? args[1] : null);
                case "play":
                    return args.Count < 2 ? "usage: " + Usage : ScenarioPlayer.Play(args[1], "console").ToString();
                case "flags":
                    return Flags();
                case "unseen":
                    return Unseen(args.Count > 1 ? args[1].ToLowerInvariant() : "status");
                default:
                    return "usage: " + Usage;
            }
        }

        private static string Unseen(string choice)
        {
            if (choice == "on" || choice == "off")
            {
                ScenarioFeature.AllowUnseenGameStories = choice == "on";
            }
            else if (choice != "status")
            {
                return "usage: scene unseen on|off";
            }

            return ScenarioFeature.AllowUnseenGameStories
                ? "unseen game stories: ON - scene play previews them after a save backup (" + ScenePreviewBackupFolder.Root + "), without recording them as seen"
                : "unseen game stories: off - scene play replays only episodes already seen, as the game's history does";
        }

        private static string List()
        {
            var sb = new StringBuilder();
            sb.Append("script: ").Append(ScenarioFeature.IsActive ? ScenarioFeature.ModeDetail : "off: " + ScenarioFeature.InactiveReason)
              .Append("; guard ").Append(ScenarioPatches.GuardApplied ? "on" : "off")
              .Append(" (").Append(ScenarioSaveGuard.Skipped.ToString(CultureInfo.InvariantCulture)).Append(" kept out of the save)")
              .Append("; rows run ").Append(ScenarioRuntime.RowsRun.ToString(CultureInfo.InvariantCulture))
              .Append("; holding ").Append(ScenarioRuntime.Waits.Count.ToString(CultureInfo.InvariantCulture))
              .Append("; deferred ").Append(ScenarioRuntime.Defers.Count.ToString(CultureInfo.InvariantCulture))
              .Append("; flags ").Append(ScenarioFlags.Shared.Count.ToString(CultureInfo.InvariantCulture));
            if (ScenarioRuntime.Mode == ScenarioRuntimeMode.Fallback)
            {
                sb.Append("; fallback rows ").Append(ScenarioRuntime.FallbackRowCount.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append("\nlast row: ").Append(ScenarioRuntime.LastRow);

            sb.Append("\ncommands:");
            foreach (var binding in ScenarioCommandRegistry.Shared.Bindings)
            {
                sb.Append("\n  ").Append(binding.Id.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(binding.Descriptor.Name)
                  .Append(" (").Append(binding.Owner).Append("): ").Append(binding.Descriptor.Description);
            }

            var lint = ScenarioFeature.Lint;
            sb.Append("\npack scenes (").Append(lint.Summarize()).Append("):");
            foreach (var id in lint.PackScenes)
            {
                var group = ScenarioPlayer.FindPackGroup(id);
                if (group is null || group.Languages != null)
                {
                    continue;
                }

                var findings = lint.For(id);
                var errors = 0;
                foreach (var finding in findings)
                {
                    if (finding.Severity == ScenarioLintSeverity.Error)
                    {
                        errors++;
                    }
                }

                sb.Append("\n  ").Append(id).Append("  ")
                  .Append(MasterDataEnums.Scenario.TryName(group.Scenario, out var type) && type != null ? type : group.Scenario.ToString(CultureInfo.InvariantCulture))
                  .Append(" ep ").Append(group.EpisodeNumber.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(ScenarioLint.IsLongStory(group.Scenario) ? " (long)" : " (talk)")
                  .Append(errors > 0 ? "  " + errors + " error(s): not playable" : findings.Count > 0 ? "  " + findings.Count + " warning(s)" : "  ok");
            }

            if (lint.PackLineCount > 0)
            {
                sb.Append("\n  + ").Append(lint.PackLineCount.ToString(CultureInfo.InvariantCulture)).Append(" pack line(s) (see: lines list)");
            }

            return sb.ToString();
        }

        private static string LintText(string? groupId)
        {
            var report = ScenarioFeature.Relint("console");
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return report.ToString();
            }

            var findings = report.For(groupId!.Trim());
            if (findings.Count == 0)
            {
                return ScenarioPlayer.FindPackGroup(groupId.Trim()) is null
                    ? "no pack scene \"" + groupId.Trim() + "\""
                    : groupId.Trim() + ": no findings";
            }

            var sb = new StringBuilder(groupId.Trim()).Append(": ").Append(findings.Count.ToString(CultureInfo.InvariantCulture)).Append(" finding(s)");
            foreach (var finding in findings)
            {
                sb.Append('\n').Append(finding);
            }

            return sb.ToString();
        }

        private static string Flags()
        {
            var names = ScenarioFlags.Shared.Names;
            return names.Count == 0
                ? "no flags set (" + ScenarioFlagStore.FilePath + ")"
                : names.Count.ToString(CultureInfo.InvariantCulture) + " flag(s): " + string.Join(", ", names) + " (" + ScenarioFlagStore.FilePath + ")";
        }
    }
}
