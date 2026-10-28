using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>The arguments read from <c>key=value</c> words, or why they could not be read. Immutable.</summary>
    public sealed class ActionWordsResult
    {
        private ActionWordsResult(ActionArgs args, string problem)
        {
            Args = args;
            Problem = problem;
        }

        /// <summary>The arguments (all values are text); <see cref="ActionArgs.Empty"/> on a problem.</summary>
        public ActionArgs Args { get; }

        /// <summary>Why the words could not be read; empty when they could.</summary>
        public string Problem { get; }

        /// <summary>True when <see cref="Args"/> is usable.</summary>
        public bool IsValid => Problem.Length == 0;

        internal static ActionWordsResult Ok(ActionArgs args) => new ActionWordsResult(args, string.Empty);

        internal static ActionWordsResult Fail(string problem) => new ActionWordsResult(ActionArgs.Empty, problem);
    }

    /// <summary>
    /// The text forms of actions shared by the console (<c>act list|run</c>) and scripts
    /// (<c>cwy.do music.play title=Rain</c>): reading <c>key=value</c> words, and listing and
    /// printing actions. Pure.
    /// </summary>
    public static class ActionWords
    {
        /// <summary>The console usage line.</summary>
        public const string Usage = "act [status] | act list [prefix] | act run <name> [key=value ...]";

        /// <summary>
        /// Reads <c>key=value</c> words into arguments, from index <paramref name="start"/>. The first
        /// <c>=</c> splits a word, so a value may contain <c>=</c>; an empty value is the empty text.
        /// Quotes are the tokenizer's job (the console's <c>CommandLineParser</c>), so
        /// <c>title="Rain on glass"</c> arrives as one word. Values stay text: the runner's schema step
        /// reads numbers and booleans from text in invariant culture.
        /// </summary>
        /// <param name="words">The words; null means none.</param>
        /// <param name="start">The first word to read.</param>
        /// <returns>The arguments, or the problem (a word without <c>=</c>, a blank or repeated name).</returns>
        public static ActionWordsResult ParseArgs(IReadOnlyList<string>? words, int start = 0)
        {
            if (words is null || start >= words.Count)
            {
                return ActionWordsResult.Ok(ActionArgs.Empty);
            }

            var pairs = new List<KeyValuePair<string, object?>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = Math.Max(0, start); i < words.Count; i++)
            {
                var word = words[i] ?? string.Empty;
                var equals = word.IndexOf('=');
                if (equals <= 0)
                {
                    return ActionWordsResult.Fail("argument " + (i - start + 1).ToString(CultureInfo.InvariantCulture) + " is not key=value");
                }

                var key = word.Substring(0, equals).Trim();
                if (key.Length == 0)
                {
                    return ActionWordsResult.Fail("argument " + (i - start + 1).ToString(CultureInfo.InvariantCulture) + " has no name before '='");
                }

                if (!seen.Add(key))
                {
                    return ActionWordsResult.Fail("the argument \"" + key + "\" is given twice");
                }

                pairs.Add(new KeyValuePair<string, object?>(key, word.Substring(equals + 1)));
            }

            return ActionWordsResult.Ok(ActionArgs.From(pairs));
        }

        /// <summary>
        /// The action list for <c>act list</c>: one line per action (name, tier, description), then
        /// one indented line per parameter. Never shows a value.
        /// </summary>
        /// <param name="descriptors">The actions, sorted.</param>
        /// <param name="prefix">Only names starting with this (case-insensitive); null or empty for all.</param>
        public static string FormatList(IReadOnlyList<ActionDescriptor>? descriptors, string? prefix)
        {
            var sb = new StringBuilder();
            var shown = 0;
            var total = descriptors?.Count ?? 0;
            if (descriptors != null)
            {
                foreach (var descriptor in descriptors)
                {
                    if (!string.IsNullOrEmpty(prefix) && !descriptor.Name.StartsWith(prefix!, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    shown++;
                    sb.Append('\n').Append("  ").Append(descriptor.Name).Append("  ")
                        .Append(ActionAudit.Word(descriptor.Tier))
                        .Append(descriptor.IsDestructive ? ", destructive" : string.Empty)
                        .Append("  ").Append(descriptor.Description);

                    foreach (var parameter in descriptor.Parameters)
                    {
                        sb.Append('\n').Append("      ").Append(Describe(parameter));
                    }
                }
            }

            var head = string.IsNullOrEmpty(prefix)
                ? "ACTIONS  " + total + " registered"
                : "ACTIONS  " + shown + " of " + total + " match \"" + prefix + "\"";
            return shown == 0 && total > 0 && !string.IsNullOrEmpty(prefix)
                ? head + "\n  (try act list without a prefix)"
                : head + sb;
        }

        /// <summary><c>title (string, required): The track title. [values: a|b] [0..1] [private]</c>.</summary>
        /// <param name="parameter">The parameter.</param>
        public static string Describe(ActionParameter parameter)
        {
            if (parameter is null)
            {
                throw new ArgumentNullException(nameof(parameter));
            }

            var sb = new StringBuilder(parameter.Name).Append(" (").Append(ActionArgsValidation.Kind(parameter.Kind))
                .Append(parameter.Required ? ", required" : string.Empty).Append("): ").Append(parameter.Description);
            if (parameter.AllowedValues.Count > 0)
            {
                sb.Append(" [values: ").Append(string.Join("|", parameter.AllowedValues)).Append(']');
            }

            if (parameter.Minimum.HasValue || parameter.Maximum.HasValue)
            {
                sb.Append(" [")
                    .Append(parameter.Minimum.HasValue ? parameter.Minimum.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty)
                    .Append("..")
                    .Append(parameter.Maximum.HasValue ? parameter.Maximum.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty)
                    .Append(']');
            }

            if (parameter.IsPrivate)
            {
                sb.Append(" [private]");
            }

            return sb.ToString();
        }

        /// <summary>
        /// One result for the console: <c>music.play: Ok via Semantic: playing "Rain"</c>, then one
        /// <c>key = value</c> line per data entry.
        /// </summary>
        /// <param name="name">The action name.</param>
        /// <param name="result">The outcome.</param>
        public static string FormatResult(string name, ActionResult result)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var sb = new StringBuilder(name).Append(": ").Append(result);
            foreach (var pair in result.Data)
            {
                sb.Append('\n').Append("  ").Append(pair.Key).Append(" = ").Append(Text(pair.Value));
            }

            return sb.ToString();
        }

        private static string Text(object? value)
        {
            switch (value)
            {
                case null: return "null";
                case bool flag: return flag ? "true" : "false";
                case double real: return real.ToString("R", CultureInfo.InvariantCulture);
                case float single: return single.ToString("R", CultureInfo.InvariantCulture);
                default: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }
}
