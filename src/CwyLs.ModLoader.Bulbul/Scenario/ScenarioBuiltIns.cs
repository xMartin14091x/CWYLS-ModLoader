using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Scenario;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// The framework's four script commands and the one action the script feature offers:
    /// <list type="bullet">
    /// <item><c>cwy.do &lt;action&gt; [name=value ...]</c> (5000): runs the action through
    /// <see cref="ActionServices.RunAsync"/> (caller <see cref="ActionCaller.Script"/>, caller id = the
    /// scene's group) and holds the scene until it answers (at most <see cref="ScenarioRuntime.DoTimeout"/>).</item>
    /// <item><c>cwy.defer</c> (5001): queues the same request; it runs after the scene ends and the room is quiet.</item>
    /// <item><c>cwy.wait &lt;seconds&gt;</c> (5002): holds the scene; a skipped or ended scene stops waiting.</item>
    /// <item><c>cwy.flag set|clear &lt;name&gt;</c> (5003): <see cref="ScenarioFlags.Shared"/>.</item>
    /// <item>action <c>scene.play group=&lt;id&gt;</c> (tier Act): plays a pack scene (<see cref="ScenarioPlayer"/>),
    /// so <c>cwy.defer scene.play …</c> can chain scenes and the API can start one.</item>
    /// </list>
    /// A row a handler cannot use warns and the scene goes on: a broken row never stalls a scene.
    /// </summary>
    internal static class ScenarioBuiltIns
    {
        /// <summary>The action that plays a pack scene.</summary>
        internal const string PlayActionName = "scene.play";

        private static readonly List<IDisposable> Registrations = new List<IDisposable>();

        /// <summary>The command names and the action actually registered (for the smoke check).</summary>
        internal static IReadOnlyList<string> Registered { get; private set; } = Array.Empty<string>();

        /// <summary>Registers the four commands and <c>scene.play</c>. Idempotent.</summary>
        internal static void Register()
        {
            if (Registrations.Count > 0)
            {
                return;
            }

            var names = new List<string>();
            Command(ScenarioBuiltInArgs.Do, Do, names);
            Command(ScenarioBuiltInArgs.Defer, Defer, names);
            Command(ScenarioBuiltInArgs.Wait, Wait, names);
            Command(ScenarioBuiltInArgs.Flag, Flag, names);

            var play = new ActionDescriptor(
                PlayActionName,
                ActionTier.Act,
                "Plays a pack scene (one a content pack added; scene list shows them) through the game's own pipeline when the room is idle.",
                new[] { new ActionParameter("group", ActionValueKind.String, true, "The pack scene's group id, e.g. cwy_demo_script_02.") });
            if (ActionRegistry.Shared.TryRegister(ActionRegistry.FrameworkOwner, play, PlayAction, out var registration, out var problem))
            {
                Registrations.Add(registration!);
                names.Add(PlayActionName);
            }
            else
            {
                ScenarioRuntime.WarnAlways("the action " + PlayActionName + " was not registered: " + problem);
            }

            Registered = names;
        }

        /// <summary>Runs one released deferred action (called by the runtime's tick).</summary>
        internal static void RunDeferred(DeferredAction due)
        {
            ConsoleEvents.File(ScenarioRuntime.Owner, "deferRun", ("action", due.Line.Name), ("group", due.GroupId), ("row", due.RowId));
            Report(ScenarioCommandIds.DeferName, due.Line, due.GroupId, due.RowId, ActionServices.Shared.RunAsync(due.Line.ToRequest(due.GroupId)));
        }

        private static void Command(ScenarioCommandDescriptor descriptor, ScenarioCommandHandler handler, List<string> names)
        {
            if (ScenarioCommandRegistry.Shared.TryRegister(ScenarioCommandRegistry.FrameworkOwner, descriptor, handler, out var registration, out var problem))
            {
                Registrations.Add(registration!);
                names.Add(descriptor.Name);
            }
            else
            {
                ScenarioRuntime.WarnAlways("the script command " + descriptor.Name + " was not registered: " + problem);
            }
        }

        private static void Do(IScenarioCommandContext context)
        {
            var parse = ScriptActionLine.Parse(context.Row.Arg1);
            if (!parse.IsValid)
            {
                ScenarioRuntime.Warn(context.Row + ": " + parse.Problem + "; going on with the next row");
                return;
            }

            var line = parse.Line!;
            var task = ActionServices.Shared.RunAsync(line.ToRequest(context.GroupId));
            Report(ScenarioCommandIds.DoName, line, context.GroupId, context.Row.Id, task);
            if (!task.IsCompleted)
            {
                context.WaitFor(() => task.IsCompleted, ScenarioRuntime.DoTimeout);
            }
        }

        private static void Defer(IScenarioCommandContext context)
        {
            var parse = ScriptActionLine.Parse(context.Row.Arg1);
            if (!parse.IsValid)
            {
                ScenarioRuntime.Warn(context.Row + ": " + parse.Problem + "; nothing deferred");
                return;
            }

            var result = ScenarioRuntime.Defers.Enqueue(context.GroupId, context.Row.Id, parse.Line!, MainThread.Now);
            if (!result.IsOk)
            {
                ScenarioRuntime.WarnAlways(context.Row + ": " + result.Message);
                return;
            }

            ConsoleEvents.Emit(
                ScenarioRuntime.Owner,
                "defer",
                ConsoleTags.Script,
                "cwy.defer " + parse.Line!.Name + " queued; it runs when the scene has ended and the room is idle",
                "group",
                ("group", context.GroupId),
                ("row", context.Row.Id));
        }

        private static void Wait(IScenarioCommandContext context)
        {
            if (!ScenarioBuiltInArgs.TryParseWait(context.Row.Arg1, out var seconds, out var problem))
            {
                ScenarioRuntime.Warn(context.Row + ": " + problem + "; going on with the next row");
                return;
            }

            var until = MainThread.Now + seconds;
            context.WaitFor(() => MainThread.Now >= until, TimeSpan.FromSeconds(seconds + 1d));
        }

        private static void Flag(IScenarioCommandContext context)
        {
            if (!ScenarioBuiltInArgs.TryParseFlag(context.Row.Arg1, out var value, out var name, out var problem))
            {
                ScenarioRuntime.Warn(context.Row + ": " + problem + "; going on with the next row");
                return;
            }

            var result = ScenarioFlags.Shared.Set(name, value, context.GroupId);
            if (!result.IsOk)
            {
                ScenarioRuntime.Warn(context.Row + ": " + result.Message);
            }
        }

        private static ActionResult PlayAction(ActionInvocation invocation)
        {
            var group = invocation.Args.GetString("group");
            var result = ScenarioPlayer.Play(group, "action:" + invocation.Request.CallerId);
            return result.IsOk ? ActionResult.Ok(result.Message) : ActionResult.Failed(result.Message);
        }

        /// <summary>One SCRIPT line per action answer (a WARN when it is not Ok), posted to the main thread.</summary>
        private static void Report(string command, ScriptActionLine line, string groupId, string rowId, Task<ActionResult> task)
        {
            if (task.IsCompleted)
            {
                Log(command, line, groupId, rowId, task);
                return;
            }

            task.ContinueWith(
                done =>
                {
                    if (!MainThread.Post("script " + command + " answer", () => Log(command, line, groupId, rowId, done), ScenarioRuntime.Owner))
                    {
                        ConsoleEvents.File(ScenarioRuntime.Owner, "answerDropped", ("action", line.Name), ("group", groupId));
                    }
                },
                TaskScheduler.Default);
        }

        private static void Log(string command, ScriptActionLine line, string groupId, string rowId, Task<ActionResult> task)
        {
            // ActionServices.RunAsync never faults (by contract); a faulted task here is a broken runner.
            var result = task.Status == TaskStatus.RanToCompletion && task.Result != null
                ? task.Result
                : ActionResult.Failed("the action runner ended without an answer (" + task.Status + ")");
            var primary = command + " " + line.Name + " -> " + result;
            var fields = new (string Key, object? Value)[]
            {
                ("group", groupId),
                ("row", rowId),
                ("status", result.Status.ToString()),
                ("path", result.Path.ToString()),
            };

            if (result.IsOk)
            {
                ConsoleEvents.Emit(ScenarioRuntime.Owner, "action", ConsoleTags.Script, primary, "group", fields);
            }
            else
            {
                ConsoleEvents.EmitAt(ScenarioRuntime.Owner, "action", ConsoleTags.Script, ConsoleLevel.Warn, primary, "group", fields);
            }
        }
    }
}
