using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Conditions
{
    /// <summary>How one alternative of a clause compares the token's value.</summary>
    public enum ConditionOperator
    {
        /// <summary><c>Sat</c>: the token has this value.</summary>
        Equal = 0,

        /// <summary><c>22..23</c>: a value lies between <see cref="ConditionAlternative.Value"/> and <see cref="ConditionAlternative.High"/>, inclusive (the evaluator handles wrap-around).</summary>
        Range = 1,

        /// <summary><c>&gt;=5</c>.</summary>
        GreaterOrEqual = 2,

        /// <summary><c>&gt;5</c>.</summary>
        Greater = 3,

        /// <summary><c>&lt;=5</c>.</summary>
        LessOrEqual = 4,

        /// <summary><c>&lt;5</c>.</summary>
        Less = 5,
    }

    /// <summary>One alternative in a clause's comma list. Immutable; text is kept as written (trimmed).</summary>
    public sealed class ConditionAlternative
    {
        /// <summary>Creates an alternative.</summary>
        /// <param name="op">The comparison.</param>
        /// <param name="value">The literal, the operand, or the low end of a range. Not blank.</param>
        /// <param name="high">The high end of a range; required for <see cref="ConditionOperator.Range"/>, null otherwise.</param>
        /// <exception cref="ArgumentException">A value is blank, or <paramref name="high"/> does not match the operator.</exception>
        public ConditionAlternative(ConditionOperator op, string value, string? high = null)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("an alternative needs a value", nameof(value));
            }

            if (op == ConditionOperator.Range && string.IsNullOrWhiteSpace(high))
            {
                throw new ArgumentException("a range needs a high end", nameof(high));
            }

            if (op != ConditionOperator.Range && high != null)
            {
                throw new ArgumentException("only a range has a high end", nameof(high));
            }

            Operator = op;
            Value = value.Trim();
            High = high?.Trim();
        }

        /// <summary>The comparison.</summary>
        public ConditionOperator Operator { get; }

        /// <summary>The literal, the operand, or the low end of a range.</summary>
        public string Value { get; }

        /// <summary>The high end of a range, or null.</summary>
        public string? High { get; }

        /// <summary>The alternative in the pack syntax: <c>Sat</c>, <c>22..23</c>, <c>&gt;=5</c>.</summary>
        public override string ToString()
        {
            switch (Operator)
            {
                case ConditionOperator.Range: return Value + ConditionParser.RangeSeparator + High;
                case ConditionOperator.GreaterOrEqual: return ">=" + Value;
                case ConditionOperator.Greater: return ">" + Value;
                case ConditionOperator.LessOrEqual: return "<=" + Value;
                case ConditionOperator.Less: return "<" + Value;
                default: return Value;
            }
        }
    }

    /// <summary>
    /// One member of a <c>when</c> object: a token (with its argument) and the alternatives, any
    /// of which may hold (OR); <see cref="Negated"/> turns it into "none of them holds".
    /// Immutable.
    /// </summary>
    public sealed class ConditionClause
    {
        /// <summary>Creates a clause. Use <see cref="ConditionParser"/> for pack text.</summary>
        /// <param name="token">The token key without argument, e.g. <c>weekday</c> or <c>my.mod/mood</c>.</param>
        /// <param name="argument">The argument after the colon, or null.</param>
        /// <param name="negated">True for a leading <c>!</c>.</param>
        /// <param name="alternatives">At least one alternative.</param>
        /// <exception cref="ArgumentException">The token is not a valid key, or there is no alternative.</exception>
        public ConditionClause(string token, string? argument, bool negated, IEnumerable<ConditionAlternative> alternatives)
        {
            if (!ConditionTokenInfo.IsValidKey(token, out var ns, out var bare))
            {
                throw new ArgumentException("\"" + token + "\" is not a token name", nameof(token));
            }

            if (alternatives is null)
            {
                throw new ArgumentNullException(nameof(alternatives));
            }

            var list = new List<ConditionAlternative>();
            foreach (var alternative in alternatives)
            {
                list.Add(alternative ?? throw new ArgumentException("an alternative is null", nameof(alternatives)));
            }

            if (list.Count == 0)
            {
                throw new ArgumentException("a clause needs at least one alternative", nameof(alternatives));
            }

            Token = token;
            Namespace = ns;
            BareName = bare;
            Argument = string.IsNullOrEmpty(argument) ? null : argument;
            Negated = negated;
            Alternatives = list;
        }

        /// <summary>The token key without argument, as written.</summary>
        public string Token { get; }

        /// <summary>The mod id of a mod token, or null.</summary>
        public string? Namespace { get; }

        /// <summary>The token name after any <c>modId/</c>.</summary>
        public string BareName { get; }

        /// <summary>The argument after the colon (<c>flag:metSatone</c> → <c>metSatone</c>), or null.</summary>
        public string? Argument { get; }

        /// <summary>True when the clause holds only if none of the alternatives does.</summary>
        public bool Negated { get; }

        /// <summary>The alternatives (OR). Never empty.</summary>
        public IReadOnlyList<ConditionAlternative> Alternatives { get; }

        /// <summary>The key as an author writes it: <c>flag:metSatone</c>.</summary>
        public string Key => Argument is null ? Token : Token + ":" + Argument;

        /// <summary><c>weekday: !Sat, Sun</c>.</summary>
        public override string ToString()
        {
            var sb = new StringBuilder(Key).Append(": ");
            if (Negated)
            {
                sb.Append('!');
            }

            for (var i = 0; i < Alternatives.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(Alternatives[i]);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// A parsed <c>when</c> block: every clause must hold (AND). An empty set always holds.
    /// Immutable; build one with <see cref="ConditionParser"/>.
    /// </summary>
    public sealed class ConditionSet
    {
        private static readonly ConditionClause[] NoClauses = new ConditionClause[0];

        /// <summary>Creates a set from clauses.</summary>
        /// <param name="clauses">The clauses; null means none. Two clauses with the same key are refused.</param>
        /// <exception cref="ArgumentException">A clause is null or two share a key.</exception>
        public ConditionSet(IEnumerable<ConditionClause>? clauses)
        {
            var list = new List<ConditionClause>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (clauses != null)
            {
                foreach (var clause in clauses)
                {
                    if (clause is null)
                    {
                        throw new ArgumentException("a clause is null", nameof(clauses));
                    }

                    if (!keys.Add(clause.Key))
                    {
                        throw new ArgumentException("\"" + clause.Key + "\" appears twice", nameof(clauses));
                    }

                    list.Add(clause);
                }
            }

            Clauses = list.Count == 0 ? (IReadOnlyList<ConditionClause>)NoClauses : list;
        }

        /// <summary>The set with no clauses: always holds.</summary>
        public static ConditionSet Empty { get; } = new ConditionSet(null);

        /// <summary>The clauses (AND), in document order.</summary>
        public IReadOnlyList<ConditionClause> Clauses { get; }

        /// <summary>True when there is nothing to check.</summary>
        public bool IsEmpty => Clauses.Count == 0;

        /// <summary><c>weekday: Sat, Sun; hour: 22..23</c>, or <c>(always)</c>.</summary>
        public override string ToString()
        {
            if (IsEmpty)
            {
                return "(always)";
            }

            var parts = new string[Clauses.Count];
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = Clauses[i].ToString();
            }

            return string.Join("; ", parts);
        }
    }
}
