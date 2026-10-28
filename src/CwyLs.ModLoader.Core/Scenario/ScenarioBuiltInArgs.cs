using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>
    /// What the four framework commands read from their row, shared by the runtime's handlers and by
    /// <see cref="ScenarioLint"/> so a row the lint passes is a row the handler accepts. Every
    /// built-in reads its words from <c>Arg1</c>:
    /// <list type="table">
    /// <item><term><c>cwy.do</c> (5000)</term><description><c>&lt;action&gt; [name=value ...]</c> (<see cref="ScriptActionLine"/>); runs it now and holds the scene until it answers.</description></item>
    /// <item><term><c>cwy.defer</c> (5001)</term><description>the same line; runs it after the scene ends and the room is idle.</description></item>
    /// <item><term><c>cwy.wait</c> (5002)</term><description>seconds, invariant (<c>1.5</c>), more than 0 and at most 300.</description></item>
    /// <item><term><c>cwy.flag</c> (5003)</term><description><c>set &lt;name&gt;</c> or <c>clear &lt;name&gt;</c> (<see cref="ScenarioFlags"/>).</description></item>
    /// </list>
    /// </summary>
    public static class ScenarioBuiltInArgs
    {
        /// <summary>Longest <c>cwy.wait</c>, in seconds (the <see cref="ScenarioCommandContext.MaxWait"/> limit).</summary>
        public const double MaxWaitSeconds = 300;

        /// <summary>Descriptor of <c>cwy.do</c>.</summary>
        public static ScenarioCommandDescriptor Do { get; } = new ScenarioCommandDescriptor(
            ScenarioCommandIds.DoName,
            "Runs a game action now and holds the scene until it answers (at most 30 s).",
            new[] { new ScenarioCommandArg(1, "action", "The action and its arguments: music.play title=Rain", required: true) });

        /// <summary>Descriptor of <c>cwy.defer</c>.</summary>
        public static ScenarioCommandDescriptor Defer { get; } = new ScenarioCommandDescriptor(
            ScenarioCommandIds.DeferName,
            "Runs a game action after the scene has ended and the room is idle again.",
            new[] { new ScenarioCommandArg(1, "action", "The action and its arguments: scene.play group=cwy_demo_script_02", required: true) });

        /// <summary>Descriptor of <c>cwy.wait</c>.</summary>
        public static ScenarioCommandDescriptor Wait { get; } = new ScenarioCommandDescriptor(
            ScenarioCommandIds.WaitName,
            "Holds the scene for a number of seconds; a skipped or ended scene stops waiting.",
            new[] { new ScenarioCommandArg(1, "seconds", "More than 0 and at most 300, written like 1.5", required: true) });

        /// <summary>Descriptor of <c>cwy.flag</c>.</summary>
        public static ScenarioCommandDescriptor Flag { get; } = new ScenarioCommandDescriptor(
            ScenarioCommandIds.FlagName,
            "Sets or clears a named flag that the flag:<name> condition reads.",
            new[] { new ScenarioCommandArg(1, "change", "set <name> or clear <name>", required: true) });

        /// <summary>Reads <c>cwy.wait</c>'s seconds.</summary>
        /// <param name="arg1">The row's <c>Arg1</c>.</param>
        /// <param name="seconds">The seconds, or 0.</param>
        /// <param name="problem">Why the value is unusable; empty when usable.</param>
        public static bool TryParseWait(string? arg1, out double seconds, out string problem)
        {
            var text = arg1?.Trim() ?? string.Empty;
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds)
                || double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                seconds = 0;
                problem = "cwy.wait needs seconds in Arg1, written like 1.5 (got \"" + text + "\")";
                return false;
            }

            if (seconds <= 0 || seconds > MaxWaitSeconds)
            {
                problem = "cwy.wait seconds must be more than 0 and at most " +
                          MaxWaitSeconds.ToString(CultureInfo.InvariantCulture) + " (got " + text + ")";
                seconds = 0;
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>Reads <c>cwy.flag</c>'s change.</summary>
        /// <param name="arg1">The row's <c>Arg1</c>: <c>set &lt;name&gt;</c> or <c>clear &lt;name&gt;</c>.</param>
        /// <param name="value">True for set, false for clear.</param>
        /// <param name="name">The flag name, or empty.</param>
        /// <param name="problem">Why the value is unusable; empty when usable.</param>
        public static bool TryParseFlag(string? arg1, out bool value, out string name, out string problem)
        {
            value = false;
            name = string.Empty;
            var words = (arg1 ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 2)
            {
                problem = "cwy.flag needs \"set <name>\" or \"clear <name>\" in Arg1 (got \"" + (arg1 ?? string.Empty).Trim() + "\")";
                return false;
            }

            if (string.Equals(words[0], "set", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
            }
            else if (!string.Equals(words[0], "clear", StringComparison.OrdinalIgnoreCase))
            {
                problem = "cwy.flag: the first word is set or clear, not \"" + words[0] + "\"";
                return false;
            }

            if (!ScenarioFlags.IsValidName(words[1], out problem))
            {
                return false;
            }

            name = words[1];
            return true;
        }

        /// <summary>
        /// Checks a framework command's row the way its handler will (lint). Commands other than the
        /// four built-ins are not checked here.
        /// </summary>
        /// <param name="command">The row's command number.</param>
        /// <param name="arg1">The row's <c>Arg1</c>.</param>
        /// <param name="actionName">For <c>cwy.do</c>/<c>cwy.defer</c>: the parsed action name; otherwise empty.</param>
        /// <returns>Why the row is unusable; empty when it is usable (or not a built-in).</returns>
        public static string Check(int command, string? arg1, out string actionName)
        {
            actionName = string.Empty;
            switch (command)
            {
                case ScenarioCommandIds.Do:
                case ScenarioCommandIds.Defer:
                {
                    var parse = ScriptActionLine.Parse(arg1);
                    if (!parse.IsValid)
                    {
                        return (command == ScenarioCommandIds.Do ? ScenarioCommandIds.DoName : ScenarioCommandIds.DeferName) + ": " + parse.Problem;
                    }

                    actionName = parse.Line!.Name;
                    return string.Empty;
                }

                case ScenarioCommandIds.Wait:
                    return TryParseWait(arg1, out _, out var waitProblem) ? string.Empty : waitProblem;
                case ScenarioCommandIds.Flag:
                    return TryParseFlag(arg1, out _, out _, out var flagProblem) ? string.Empty : flagProblem;
                default:
                    return string.Empty;
            }
        }
    }
}
