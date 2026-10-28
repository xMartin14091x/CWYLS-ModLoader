using System;
using System.Text.RegularExpressions;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Conditions
{
    /// <summary>What kind of text a token's values are, so an evaluator knows how to compare them.</summary>
    public enum ConditionValueKind
    {
        /// <summary>Free text, compared case-insensitively (<c>th</c>, <c>work</c>, a pack id).</summary>
        Text = 0,

        /// <summary>A whole number in invariant culture (<c>22</c>).</summary>
        Integer = 1,

        /// <summary>A number in invariant culture (<c>12.5</c>).</summary>
        Number = 2,

        /// <summary>A local clock time <c>HH:mm</c>; a range may wrap past midnight (<c>22:00..06:00</c>).</summary>
        Time = 3,

        /// <summary>A calendar date, <c>MM-dd</c> or <c>yyyy-MM-dd</c>; a range may wrap past New Year.</summary>
        Date = 4,

        /// <summary>A day of the week: <c>Mon</c> … <c>Sun</c> (or the full English name).</summary>
        Weekday = 5,

        /// <summary><c>true</c> or <c>false</c>.</summary>
        Boolean = 6,
    }

    /// <summary>Whether a token takes an argument after a colon, like <c>flag:metSatone</c>.</summary>
    public enum ConditionArgumentRule
    {
        /// <summary>No argument; <c>weekday:x</c> is an error.</summary>
        None = 0,

        /// <summary>An argument is required; bare <c>flag</c> is an error.</summary>
        Required = 1,

        /// <summary>Either form is accepted.</summary>
        Optional = 2,
    }

    /// <summary>
    /// One condition token as the registry knows it: its name, how its values compare, and when
    /// they can change. Immutable.
    /// </summary>
    public sealed class ConditionTokenInfo
    {
        /// <summary>The pattern of a token name: a letter, then letters, digits or <c>_</c>, up to 32.</summary>
        public const string NamePattern = "^[A-Za-z][A-Za-z0-9_]{0,31}$";

        private static readonly Regex NameRegex =
            new Regex(@"\A[A-Za-z][A-Za-z0-9_]{0,31}\z", RegexOptions.CultureInvariant);

        /// <summary>Creates a token description.</summary>
        /// <param name="name">
        /// The key an author writes: a built-in name such as <c>weekday</c>, or a mod token
        /// <c>&lt;modId&gt;/&lt;name&gt;</c>. Compared case-insensitively.
        /// </param>
        /// <param name="kind">How its values compare.</param>
        /// <param name="updatePoints">When its value can change; <see cref="UpdatePoint.None"/> for a constant.</param>
        /// <param name="description">One sentence for <c>cond tokens</c> and the author's guide.</param>
        /// <param name="argument">Whether it takes an argument (<c>flag:&lt;name&gt;</c>).</param>
        /// <exception cref="ArgumentException">The name or description is unusable.</exception>
        public ConditionTokenInfo(
            string name,
            ConditionValueKind kind,
            UpdatePoint updatePoints,
            string description,
            ConditionArgumentRule argument = ConditionArgumentRule.None)
        {
            if (!IsValidKey(name, out var ns, out var bare))
            {
                throw new ArgumentException(
                    "\"" + name + "\" is not a token name: use " + NamePattern + ", or <modId>/<name> for a mod token",
                    nameof(name));
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                throw new ArgumentException("a token needs a one-sentence description", nameof(description));
            }

            if (!Enum.IsDefined(typeof(ConditionValueKind), kind))
            {
                throw new ArgumentException("unknown value kind " + (int)kind, nameof(kind));
            }

            if (!Enum.IsDefined(typeof(ConditionArgumentRule), argument))
            {
                throw new ArgumentException("unknown argument rule " + (int)argument, nameof(argument));
            }

            Name = name;
            Namespace = ns;
            BareName = bare;
            Kind = kind;
            UpdatePoints = updatePoints & UpdatePoint.All;
            Description = description.Trim();
            Argument = argument;
        }

        /// <summary>The key as registered, e.g. <c>weekday</c> or <c>my.mod/mood</c>.</summary>
        public string Name { get; }

        /// <summary>The mod id before the slash, or null for a built-in token.</summary>
        public string? Namespace { get; }

        /// <summary>The name after the slash (or the whole name for a built-in token).</summary>
        public string BareName { get; }

        /// <summary>How its values compare.</summary>
        public ConditionValueKind Kind { get; }

        /// <summary>When its value can change.</summary>
        public UpdatePoint UpdatePoints { get; }

        /// <summary>One sentence for people.</summary>
        public string Description { get; }

        /// <summary>Whether it takes an argument.</summary>
        public ConditionArgumentRule Argument { get; }

        /// <summary>
        /// True when <paramref name="key"/> is a usable token key (without an argument): a bare name
        /// matching <see cref="NamePattern"/>, or <c>&lt;modId&gt;/&lt;name&gt;</c> where the mod id
        /// matches <see cref="PackManifest.IdPattern"/>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="ns">The mod id, or null.</param>
        /// <param name="name">The bare name, or empty.</param>
        public static bool IsValidKey(string? key, out string? ns, out string name)
        {
            ns = null;
            name = string.Empty;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            var slash = key!.IndexOf('/');
            if (slash < 0)
            {
                if (!NameRegex.IsMatch(key))
                {
                    return false;
                }

                name = key;
                return true;
            }

            var prefix = key.Substring(0, slash);
            var rest = key.Substring(slash + 1);
            if (!PackManifest.IsValidId(prefix) || !NameRegex.IsMatch(rest))
            {
                return false;
            }

            ns = prefix;
            name = rest;
            return true;
        }

        /// <summary><c>weekday (Weekday; DayStarted)</c>.</summary>
        public override string ToString() => Name + " (" + Kind + "; " + UpdatePoints + ")";
    }
}
