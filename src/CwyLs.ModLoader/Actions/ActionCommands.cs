using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CwyLs.ModLoader.Console;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Console;

namespace CwyLs.ModLoader.Actions
{
    /// <summary>
    /// The <c>act</c> console command (framework group): <c>act</c> / <c>act status</c>,
    /// <c>act list [prefix]</c>, <c>act run &lt;name&gt; [key=value ...]</c>. A run goes through
    /// <see cref="ActionServices.Shared"/> as caller <see cref="ActionCaller.Console"/> (id
    /// <c>console</c>): the same schema, gate, confirmation and audit as every other caller.
    /// Registered by the feature itself, so no shared console file changes.
    /// </summary>
    internal static class ActionCommands
    {
        /// <summary>The command name.</summary>
        internal const string Name = "act";

        /// <summary>The caller id of console runs in the audit line.</summary>
        internal const string CallerId = "console";

        private static bool _registered;

        /// <summary>Registers the command once.</summary>
        internal static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            ConsoleFeature.Register(
                Name,
                "Game actions: the status of the runner, the list, or run one (same gate as scripts and the API).",
                ActionWords.Usage,
                ConsoleFeature.FrameworkGroup,
                Run);
        }

        /// <summary>Runs one <c>act</c> line.</summary>
        /// <param name="args">The words after <c>act</c>.</param>
        /// <returns>What to print now; a run that has to wait for the main thread prints its result when it ends.</returns>
        internal static string? Run(IReadOnlyList<string> args)
        {
            var sub = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
            switch (sub)
            {
                case "status":
                    return ActionsFeature.Status();
                case "list":
                    return ActionWords.FormatList(ActionRegistry.Shared.Descriptors, args.Count > 1 ? args[1] : null);
                case "run":
                    return RunAction(args);
                default:
                    return "usage: " + ActionWords.Usage;
            }
        }

        private static string RunAction(IReadOnlyList<string> args)
        {
            if (args.Count < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                return "usage: act run <name> [key=value ...]";
            }

            var name = args[1].Trim();
            var words = ActionWords.ParseArgs(args, 2);
            if (!words.IsValid)
            {
                return "act run " + name + ": " + words.Problem;
            }

            var request = new ActionRequest(name, words.Args, ActionCaller.Console, CallerId);
            var task = ActionServices.Shared.RunAsync(request);
            if (task.IsCompleted)
            {
                return Format(request.Name, task);
            }

            task.ContinueWith(
                finished => ConsoleFeature.Write(Format(request.Name, finished), KindOf(finished)),
                TaskScheduler.Default);
            return "running " + request.Name + " ...";
        }

        private static string Format(string name, Task<ActionResult> task)
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
            {
                return ActionWords.FormatResult(name, task.Result);
            }

            // ActionServices.RunAsync never faults; this is the belt to its braces.
            var why = task.Exception?.GetBaseException().Message ?? task.Status.ToString();
            CwyLsModLoader.Log.LogWarning("ACTION act run " + name + " ended " + task.Status + ": " + why);
            return name + ": the run ended " + task.Status + " (" + why + ")";
        }

        private static ConsoleLineKind KindOf(Task<ActionResult> task)
        {
            if (task.Status != TaskStatus.RanToCompletion || task.Result is null)
            {
                return ConsoleLineKind.Error;
            }

            var status = task.Result.Status;
            return status == ActionStatus.Failed || status == ActionStatus.TimedOut || status == ActionStatus.NotAvailable
                ? ConsoleLineKind.Error
                : ConsoleLineKind.Output;
        }
    }
}
