using System;

namespace CwyLs.ModLoader.Core.Conditions
{
    /// <summary>What evaluating a <see cref="ConditionSet"/> found.</summary>
    public enum ConditionOutcome
    {
        /// <summary>Every clause holds.</summary>
        Met = 0,

        /// <summary>At least one clause does not hold.</summary>
        NotMet = 1,

        /// <summary>
        /// No evaluator is installed, or a token's provider is not running yet. Consumers treat
        /// this as "not met": content behind a condition stays off until it can be checked.
        /// </summary>
        Unavailable = 2,

        /// <summary>
        /// The set cannot be evaluated: an unknown token, a missing argument, a value the token
        /// cannot compare, or an evaluator failure. Also "not met"; lint reports it.
        /// </summary>
        Invalid = 3,
    }

    /// <summary>The answer for one <see cref="ConditionSet"/>, with the reason in words. Immutable.</summary>
    public sealed class ConditionResult
    {
        private ConditionResult(ConditionOutcome outcome, string reason, string? clause)
        {
            Outcome = outcome;
            Reason = reason ?? string.Empty;
            Clause = clause;
        }

        /// <summary>The cached "every clause holds" result, with no reason text.</summary>
        public static ConditionResult Met { get; } = new ConditionResult(ConditionOutcome.Met, string.Empty, null);

        /// <summary>What was found.</summary>
        public ConditionOutcome Outcome { get; }

        /// <summary>True only for <see cref="ConditionOutcome.Met"/>; everything else means "do not apply".</summary>
        public bool IsMet => Outcome == ConditionOutcome.Met;

        /// <summary>Why, in words (<c>weekday is Mon; wanted Sat, Sun</c>). Empty for <see cref="Met"/>.</summary>
        public string Reason { get; }

        /// <summary>The key of the clause that decided it (<c>weekday</c>), or null.</summary>
        public string? Clause { get; }

        /// <summary>A clause does not hold.</summary>
        /// <param name="reason">Why, in words.</param>
        /// <param name="clause">The deciding clause's key, or null.</param>
        public static ConditionResult NotMet(string reason, string? clause = null) =>
            new ConditionResult(ConditionOutcome.NotMet, Require(reason), clause);

        /// <summary>Conditions cannot be checked right now.</summary>
        /// <param name="reason">Why, in words.</param>
        /// <param name="clause">The clause that could not be checked, or null.</param>
        public static ConditionResult Unavailable(string reason, string? clause = null) =>
            new ConditionResult(ConditionOutcome.Unavailable, Require(reason), clause);

        /// <summary>The set cannot be evaluated.</summary>
        /// <param name="reason">Why, in words.</param>
        /// <param name="clause">The offending clause's key, or null.</param>
        public static ConditionResult Invalid(string reason, string? clause = null) =>
            new ConditionResult(ConditionOutcome.Invalid, Require(reason), clause);

        /// <summary><c>NotMet: weekday is Mon; wanted Sat, Sun</c>.</summary>
        public override string ToString() => Reason.Length == 0 ? Outcome.ToString() : Outcome + ": " + Reason;

        private static string Require(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("a result other than Met needs a reason", nameof(reason));
            }

            return reason;
        }
    }
}
