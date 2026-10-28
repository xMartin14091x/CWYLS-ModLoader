using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// The schema step of the action runner: checks one call's arguments against the
    /// action's parameters before anything is gated or run. Immutable result.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Every argument must be a declared parameter (case-insensitive); unknown names are
    /// refused so a misspelt argument never silently does nothing.</item>
    /// <item>A required parameter must be given and not null; an optional one given as null counts
    /// as absent.</item>
    /// <item>Types: a <see cref="ActionValueKind.String"/> takes any value (numbers and booleans are
    /// read as invariant text); <see cref="ActionValueKind.Integer"/> a whole number or text that
    /// parses; <see cref="ActionValueKind.Number"/> any number or text that parses;
    /// <see cref="ActionValueKind.Boolean"/> true/false or the text true, false, 1, 0. Console and
    /// script arguments arrive as text, API arguments as JSON values: both pass the same rules.</item>
    /// <item>Allowed values (case-insensitive) and inclusive bounds are checked.</item>
    /// <item>No problem message repeats a value: arguments may be private (a note's text), and the
    /// message reaches the audit line and the caller.</item>
    /// </list>
    /// </remarks>
    public sealed class ActionArgsValidation
    {
        private static readonly ActionArgsValidation Valid = new ActionArgsValidation(string.Empty, new List<string>());

        private ActionArgsValidation(string actionName, List<string> problems)
        {
            ActionName = actionName;
            Problems = problems;
        }

        /// <summary>The action the arguments were checked against; empty for the shared "valid" answer.</summary>
        public string ActionName { get; }

        /// <summary>Every problem found, in parameter order; empty when the arguments fit.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>True when the arguments fit the parameters.</summary>
        public bool IsValid => Problems.Count == 0;

        /// <summary>All problems in one line, or empty.</summary>
        public string Message => string.Join("; ", Problems);

        /// <summary>Checks <paramref name="args"/> against <paramref name="descriptor"/>.</summary>
        /// <param name="descriptor">The action.</param>
        /// <param name="args">The arguments; null means none.</param>
        /// <returns>The outcome; never null.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
        public static ActionArgsValidation Check(ActionDescriptor descriptor, ActionArgs? args)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var given = args ?? ActionArgs.Empty;
            var problems = new List<string>();

            foreach (var key in given.Keys)
            {
                if (descriptor.FindParameter(key) is null)
                {
                    problems.Add("unknown argument \"" + Shorten(key) + "\" (" + descriptor.Name + " takes " + Names(descriptor) + ")");
                }
            }

            foreach (var parameter in descriptor.Parameters)
            {
                if (!given.TryGetValue(parameter.Name, out var value) || value is null)
                {
                    if (parameter.Required)
                    {
                        problems.Add("missing required argument \"" + parameter.Name + "\" (" + Kind(parameter.Kind) + ")");
                    }

                    continue;
                }

                var problem = CheckValue(parameter, given);
                if (problem != null)
                {
                    problems.Add(problem);
                }
            }

            return problems.Count == 0 ? Valid : new ActionArgsValidation(descriptor.Name, problems);
        }

        /// <summary>The <see cref="ActionStatus.InvalidArgs"/> result for these problems, or null when the arguments fit.</summary>
        public ActionResult? ToResult() =>
            IsValid ? null : ActionResult.InvalidArgs(ActionName + ": " + Message);

        /// <summary>The JSON type name of a value kind, as the API and MCP see it.</summary>
        /// <param name="kind">The kind.</param>
        public static string Kind(ActionValueKind kind)
        {
            switch (kind)
            {
                case ActionValueKind.Integer: return "integer";
                case ActionValueKind.Number: return "number";
                case ActionValueKind.Boolean: return "boolean";
                default: return "string";
            }
        }

        private static string? CheckValue(ActionParameter parameter, ActionArgs args)
        {
            switch (parameter.Kind)
            {
                case ActionValueKind.Integer:
                    if (!args.TryGetInt64(parameter.Name, out var whole))
                    {
                        return "\"" + parameter.Name + "\" must be a whole number";
                    }

                    return CheckBounds(parameter, whole);

                case ActionValueKind.Number:
                    if (!args.TryGetDouble(parameter.Name, out var real))
                    {
                        return "\"" + parameter.Name + "\" must be a number";
                    }

                    return CheckBounds(parameter, real);

                case ActionValueKind.Boolean:
                    return args.TryGetBoolean(parameter.Name, out _)
                        ? null
                        : "\"" + parameter.Name + "\" must be true or false";

                default:
                    if (parameter.AllowedValues.Count == 0)
                    {
                        return null;
                    }

                    var text = args.GetString(parameter.Name);
                    foreach (var allowed in parameter.AllowedValues)
                    {
                        if (string.Equals(allowed, text, StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }
                    }

                    return "\"" + parameter.Name + "\" must be one of " + string.Join(", ", parameter.AllowedValues);
            }
        }

        private static string? CheckBounds(ActionParameter parameter, double value)
        {
            if (parameter.Minimum.HasValue && value < parameter.Minimum.Value)
            {
                return "\"" + parameter.Name + "\" must be at least " + Number(parameter.Minimum.Value);
            }

            if (parameter.Maximum.HasValue && value > parameter.Maximum.Value)
            {
                return "\"" + parameter.Name + "\" must be at most " + Number(parameter.Maximum.Value);
            }

            return null;
        }

        private static string Names(ActionDescriptor descriptor)
        {
            if (descriptor.Parameters.Count == 0)
            {
                return "no arguments";
            }

            var names = new List<string>(descriptor.Parameters.Count);
            foreach (var parameter in descriptor.Parameters)
            {
                names.Add(parameter.Name);
            }

            return string.Join(", ", names);
        }

        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string Shorten(string name) => name.Length <= 32 ? name : name.Substring(0, 32) + "…";
    }
}
