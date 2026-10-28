using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CwyLs.ModLoader.Core.Conditions
{
    /// <summary>A <c>when</c> block that does not follow the grammar; <see cref="Errors"/> lists every problem.</summary>
    public sealed class ConditionSyntaxException : FormatException
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="errors">Every problem found, one line each.</param>
        public ConditionSyntaxException(IReadOnlyList<string> errors)
            : base("invalid conditions: " + string.Join("; ", errors ?? Array.Empty<string>()))
        {
            Errors = errors ?? Array.Empty<string>();
        }

        /// <summary>Every problem found, one line each.</summary>
        public IReadOnlyList<string> Errors { get; }
    }

    /// <summary>The outcome of parsing a <c>when</c> block: the set, or every problem. Immutable.</summary>
    public sealed class ConditionParseResult
    {
        internal ConditionParseResult(ConditionSet conditions, IReadOnlyList<string> errors)
        {
            Conditions = conditions;
            Errors = errors;
        }

        /// <summary>True when the block follows the grammar.</summary>
        public bool IsValid => Errors.Count == 0;

        /// <summary>The parsed set; <see cref="ConditionSet.Empty"/> when invalid.</summary>
        public ConditionSet Conditions { get; }

        /// <summary>Every problem, one line each; empty when valid.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>The set's text, or the problems.</summary>
        public override string ToString() => IsValid ? Conditions.ToString() : string.Join("; ", Errors);
    }

    /// <summary>
    /// Parses the condition grammar every pack file shares (EditData <c>when</c>, triggers,
    /// <c>cwy.if</c>). Syntax only: whether a token exists and what its values mean is the
    /// registry's and the evaluator's business (<see cref="ConditionRegistry.Validate"/>).
    /// </summary>
    /// <remarks>
    /// <para>The grammar (documented for authors in <c>Core/Conditions/README.md</c>):</para>
    /// <code>
    /// when        = JSON object; every member must hold (AND); an empty or absent object always holds
    /// key         = token [ ":" argument ]          token = name | modId "/" name   (case-insensitive)
    /// value       = string | number | boolean | array of those
    /// string      = [ "!" ] alternative { "," alternative }   a leading "!" negates the whole list
    /// array       = [ alternative, ... ]            one alternative per element, no commas split, no "!"
    /// alternative = literal | low ".." high | ("&gt;=" | "&lt;=" | "&gt;" | "&lt;") operand
    /// </code>
    /// <para>
    /// Whitespace around keys and alternatives is ignored. Numbers and booleans become their
    /// invariant text (<c>5</c>, <c>12.5</c>, <c>true</c>). A range may run "backwards"
    /// (<c>22:00..06:00</c>): wrap-around is the evaluator's rule, not a syntax error.
    /// </para>
    /// </remarks>
    public static class ConditionParser
    {
        /// <summary>The range separator: <c>22..23</c>.</summary>
        public const string RangeSeparator = "..";

        /// <summary>Longest accepted argument (<c>flag:&lt;name&gt;</c>).</summary>
        public const int MaxArgumentLength = 64;

        private static readonly Regex ArgumentRegex =
            new Regex(@"\A[A-Za-z0-9_.\-]{1,64}\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses a <c>when</c> value: null (no conditions), a parsed JSON object
        /// (<c>JsonObject</c>) or any string-keyed dictionary.
        /// </summary>
        /// <param name="when">The value.</param>
        /// <param name="conditions">The set, or <see cref="ConditionSet.Empty"/> on failure.</param>
        /// <param name="errors">Every problem, one line each; empty on success.</param>
        /// <returns>True when the whole block parsed.</returns>
        public static bool TryParse(object? when, out ConditionSet conditions, out IReadOnlyList<string> errors)
        {
            conditions = ConditionSet.Empty;
            var problems = new List<string>();
            errors = problems;

            if (when is null)
            {
                return true;
            }

            var members = Members(when);
            if (members is null)
            {
                problems.Add("conditions must be a JSON object like {\"weekday\": \"Sat, Sun\"}, not " + Describe(when));
                return false;
            }

            var clauses = new List<ConditionClause>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members)
            {
                if (!TryParseClause(member.Key, member.Value, out var clause, out var error))
                {
                    problems.Add(error!);
                    continue;
                }

                if (!seen.Add(clause!.Key))
                {
                    problems.Add("\"" + member.Key + "\": the condition " + clause.Key + " appears twice");
                    continue;
                }

                clauses.Add(clause);
            }

            if (problems.Count > 0)
            {
                return false;
            }

            conditions = new ConditionSet(clauses);
            return true;
        }

        /// <summary>Parses a <c>when</c> value into a result object (never throws for bad input).</summary>
        /// <param name="when">The value, as for <see cref="TryParse"/>.</param>
        /// <returns>The set, or every problem.</returns>
        public static ConditionParseResult Read(object? when)
        {
            TryParse(when, out var conditions, out var errors);
            return new ConditionParseResult(conditions, errors);
        }

        /// <summary>Parses a <c>when</c> value, throwing on any problem.</summary>
        /// <param name="when">The value, as for <see cref="TryParse"/>.</param>
        /// <returns>The set.</returns>
        /// <exception cref="ConditionSyntaxException">The block does not follow the grammar.</exception>
        public static ConditionSet Parse(object? when)
        {
            if (TryParse(when, out var conditions, out var errors))
            {
                return conditions;
            }

            throw new ConditionSyntaxException(errors);
        }

        /// <summary>Parses one member of a <c>when</c> object.</summary>
        /// <param name="key">The member name, e.g. <c>weekday</c> or <c>flag:metSatone</c>.</param>
        /// <param name="value">The member value.</param>
        /// <param name="clause">The clause, or null.</param>
        /// <param name="error">One line naming the key and the problem, or null.</param>
        /// <returns>True when the member parsed.</returns>
        public static bool TryParseClause(string? key, object? value, out ConditionClause? clause, out string? error)
        {
            clause = null;
            error = null;

            if (!TryParseKey(key, out var token, out var argument, out var keyError))
            {
                error = "\"" + key + "\": " + keyError;
                return false;
            }

            var alternatives = new List<ConditionAlternative>();
            var negated = false;

            switch (value)
            {
                case null:
                    error = "\"" + key + "\": a condition needs a value";
                    return false;

                case string text:
                    if (!TryParseList(text, out negated, alternatives, out var listError))
                    {
                        error = "\"" + key + "\": " + listError;
                        return false;
                    }

                    break;

                case bool _:
                case long _:
                case int _:
                case double _:
                    alternatives.Add(new ConditionAlternative(ConditionOperator.Equal, Invariant(value)));
                    break;

                case IList _:
                    if (!TryParseArray((IEnumerable)value, alternatives, out var arrayError))
                    {
                        error = "\"" + key + "\": " + arrayError;
                        return false;
                    }

                    break;

                default:
                    error = "\"" + key + "\": a condition value is text, a number, true/false or a list of those, not " + Describe(value);
                    return false;
            }

            clause = new ConditionClause(token, argument, negated, alternatives);
            return true;
        }

        /// <summary>Splits a key into its token and argument.</summary>
        /// <param name="key">The key, e.g. <c>flag:metSatone</c>.</param>
        /// <param name="token">The token key (<c>flag</c>), or empty.</param>
        /// <param name="argument">The argument (<c>metSatone</c>), or null.</param>
        /// <param name="error">Why the key is unusable, or null.</param>
        /// <returns>True when the key is usable.</returns>
        public static bool TryParseKey(string? key, out string token, out string? argument, out string? error)
        {
            token = string.Empty;
            argument = null;
            error = null;

            var text = key?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                error = "a condition needs a token name";
                return false;
            }

            if (text![0] == '!')
            {
                error = "negate a condition in its value, not its name: {\"" + text.Substring(1).Trim() + "\": \"!a, b\"}";
                return false;
            }

            var colon = text.IndexOf(':');
            var name = colon < 0 ? text : text.Substring(0, colon).Trim();
            if (colon >= 0)
            {
                argument = text.Substring(colon + 1).Trim();
                if (!ArgumentRegex.IsMatch(argument))
                {
                    error = "the argument after ':' must be 1 to " + MaxArgumentLength + " letters, digits, '_', '.' or '-'";
                    argument = null;
                    return false;
                }
            }

            if (!ConditionTokenInfo.IsValidKey(name, out _, out _))
            {
                error = "\"" + name + "\" is not a token name (letters, digits and '_', starting with a letter; a mod token is <modId>/<name>)";
                argument = null;
                return false;
            }

            token = name;
            return true;
        }

        /// <summary>Parses one alternative: a literal, a range or a comparison.</summary>
        /// <param name="text">The alternative, e.g. <c>22..23</c> or <c>&gt;=5</c>.</param>
        /// <param name="alternative">The alternative, or null.</param>
        /// <param name="error">Why it is unusable, or null.</param>
        /// <returns>True when it parsed.</returns>
        public static bool TryParseAlternative(string? text, out ConditionAlternative? alternative, out string? error)
        {
            alternative = null;
            error = null;
            var part = text?.Trim() ?? string.Empty;

            if (part.Length == 0)
            {
                error = "an empty alternative (two commas, or a comma at an end)";
                return false;
            }

            if (part[0] == '!')
            {
                error = "'!' negates the whole value and must come first: \"!a, b\" means neither a nor b";
                return false;
            }

            ConditionOperator op;
            string operand;
            if (part.StartsWith(">=", StringComparison.Ordinal)) { op = ConditionOperator.GreaterOrEqual; operand = part.Substring(2); }
            else if (part.StartsWith("<=", StringComparison.Ordinal)) { op = ConditionOperator.LessOrEqual; operand = part.Substring(2); }
            else if (part[0] == '>') { op = ConditionOperator.Greater; operand = part.Substring(1); }
            else if (part[0] == '<') { op = ConditionOperator.Less; operand = part.Substring(1); }
            else
            {
                return TryParseRangeOrLiteral(part, out alternative, out error);
            }

            operand = operand.Trim();
            if (operand.Length == 0)
            {
                error = "\"" + part + "\" compares with nothing";
                return false;
            }

            if (operand.IndexOf(RangeSeparator, StringComparison.Ordinal) >= 0 || operand[0] == '<' || operand[0] == '>' || operand[0] == '=')
            {
                error = "\"" + part + "\" mixes a comparison with a range or a second operator";
                return false;
            }

            alternative = new ConditionAlternative(op, operand);
            return true;
        }

        private static bool TryParseRangeOrLiteral(string part, out ConditionAlternative? alternative, out string? error)
        {
            alternative = null;
            error = null;

            var at = part.IndexOf(RangeSeparator, StringComparison.Ordinal);
            if (at < 0)
            {
                alternative = new ConditionAlternative(ConditionOperator.Equal, part);
                return true;
            }

            var low = part.Substring(0, at).Trim();
            var high = part.Substring(at + RangeSeparator.Length).Trim();
            if (low.Length == 0 || high.Length == 0)
            {
                error = "the range \"" + part + "\" needs both ends, like 22..23";
                return false;
            }

            if (high.IndexOf(RangeSeparator, StringComparison.Ordinal) >= 0)
            {
                error = "\"" + part + "\" has more than one '..'";
                return false;
            }

            alternative = new ConditionAlternative(ConditionOperator.Range, low, high);
            return true;
        }

        private static bool TryParseList(string text, out bool negated, List<ConditionAlternative> into, out string? error)
        {
            negated = false;
            error = null;
            var rest = text.Trim();

            if (rest.Length == 0)
            {
                error = "the value is empty";
                return false;
            }

            if (rest[0] == '!')
            {
                negated = true;
                rest = rest.Substring(1).Trim();
                if (rest.Length == 0)
                {
                    error = "'!' negates nothing";
                    return false;
                }
            }

            foreach (var part in rest.Split(','))
            {
                if (!TryParseAlternative(part, out var alternative, out error))
                {
                    return false;
                }

                into.Add(alternative!);
            }

            return true;
        }

        private static bool TryParseArray(IEnumerable items, List<ConditionAlternative> into, out string? error)
        {
            error = null;
            foreach (var item in items)
            {
                switch (item)
                {
                    case string text:
                        if (!TryParseAlternative(text, out var alternative, out error))
                        {
                            if (text.TrimStart().StartsWith("!", StringComparison.Ordinal))
                            {
                                error = "a list cannot be negated item by item; write the value as text: \"!a, b\"";
                            }

                            return false;
                        }

                        into.Add(alternative!);
                        break;

                    case bool _:
                    case long _:
                    case int _:
                    case double _:
                        into.Add(new ConditionAlternative(ConditionOperator.Equal, Invariant(item)));
                        break;

                    default:
                        error = "a list item is text, a number or true/false, not " + Describe(item);
                        return false;
                }
            }

            if (into.Count == 0)
            {
                error = "the list is empty";
                return false;
            }

            return true;
        }

        private static IEnumerable<KeyValuePair<string, object?>>? Members(object when)
        {
            switch (when)
            {
                case IEnumerable<KeyValuePair<string, object?>> typed:
                    return typed;
                case IDictionary dictionary:
                    var list = new List<KeyValuePair<string, object?>>();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        list.Add(new KeyValuePair<string, object?>(entry.Key as string ?? Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, entry.Value));
                    }

                    return list;
                default:
                    return null;
            }
        }

        private static string Invariant(object value)
        {
            switch (value)
            {
                case bool flag: return flag ? "true" : "false";
                case double real: return real.ToString("R", CultureInfo.InvariantCulture);
                default: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        private static string Describe(object? value)
        {
            switch (value)
            {
                case null: return "null";
                case string _: return "text";
                case IEnumerable<KeyValuePair<string, object?>> _: return "an object";
                case IDictionary _: return "an object";
                case IList _: return "a list";
                default: return value.GetType().Name;
            }
        }
    }
}
