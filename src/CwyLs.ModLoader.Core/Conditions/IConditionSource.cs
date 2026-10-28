using System.Collections.Generic;
using CwyLs.ModLoader.Core.Events;

namespace CwyLs.ModLoader.Core.Conditions
{
    /// <summary>
    /// Supplies the current values of the tokens it registered: the game-side provider (every
    /// built-in token) or a mod (its <c>&lt;modId&gt;/&lt;name&gt;</c> tokens). Called by the
    /// evaluator on the main thread.
    /// </summary>
    public interface IConditionSource
    {
        /// <summary>The token's current values as invariant text; a token may hold several (<c>scenery</c>, <c>hasPack</c>).</summary>
        /// <param name="token">The registered token.</param>
        /// <param name="argument">The clause's argument (<c>flag:&lt;name&gt;</c>), or null.</param>
        /// <param name="values">The values; an empty list means "no value" (every Equal/Range/compare fails).</param>
        /// <returns>False when the value is not known yet (before the save loads): the result is <see cref="ConditionOutcome.Unavailable"/>.</returns>
        bool TryGetValues(ConditionTokenInfo token, string? argument, out IReadOnlyList<string> values);
    }

    /// <summary>
    /// Answers a <see cref="ConditionSet"/> against the registered token sources. The framework
    /// installs one with <see cref="ConditionRegistry.UseEvaluator"/>; until then
    /// <see cref="UnavailableConditionEvaluator"/> answers.
    /// </summary>
    public interface IConditionEvaluator
    {
        /// <summary>Evaluates every clause (AND). Must not throw; the registry still guards it.</summary>
        /// <param name="conditions">The set.</param>
        /// <returns>The outcome and its reason.</returns>
        ConditionResult Evaluate(ConditionSet conditions);
    }

    /// <summary>
    /// The evaluator before one is installed: an empty set is <see cref="ConditionOutcome.Met"/>
    /// (nothing to check); anything else is <see cref="ConditionOutcome.Unavailable"/>, so
    /// conditional content stays off rather than applying unchecked.
    /// </summary>
    public sealed class UnavailableConditionEvaluator : IConditionEvaluator
    {
        /// <summary>The reason text every unavailable answer carries.</summary>
        public const string Reason = "conditions are not available: no condition evaluator is installed";

        private UnavailableConditionEvaluator()
        {
        }

        /// <summary>The one instance.</summary>
        public static UnavailableConditionEvaluator Instance { get; } = new UnavailableConditionEvaluator();

        /// <inheritdoc />
        public ConditionResult Evaluate(ConditionSet conditions) =>
            conditions is null || conditions.IsEmpty
                ? ConditionResult.Met
                : ConditionResult.Unavailable(Reason, conditions.Clauses[0].Key);
    }

    /// <summary>
    /// Conditions may answer differently now. Raised through <see cref="ConditionRegistry.RaiseChanged"/>
    /// (and mirrored on the game event bus by the provider) with one <see cref="UpdatePoint"/> per change.
    /// </summary>
    /// <param name="Point">What changed.</param>
    /// <param name="Detail">The token or cause, e.g. <c>flag:metSatone</c> or <c>my.mod/mood</c>; null when not specific.</param>
    public sealed record ConditionsChanged(UpdatePoint Point, string? Detail = null) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => Detail is null ? Point.ToString() : Point + " " + Detail;

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["point"] = Point.ToString(),
                ["detail"] = Detail,
            };
    }
}
