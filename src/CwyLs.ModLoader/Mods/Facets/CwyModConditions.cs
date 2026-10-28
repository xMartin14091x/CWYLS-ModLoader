using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Conditions;
using CwyLs.ModLoader.Core.Mods;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Conditions: the <c>when</c> grammar packs use (time, weekday, pomodoro, language, flags ...),
    /// for a mod's own decisions, plus tokens a mod adds for packs to use (<c>&lt;modId&gt;/&lt;name&gt;</c>).
    /// Parsing always works; evaluating needs the condition provider, which the framework
    /// installs - until then <see cref="Evaluate(ConditionSet)"/> answers
    /// <see cref="ConditionOutcome.Unavailable"/> for anything but an empty set. Only the framework
    /// implements this interface. Grammar: <c>src/CwyLs.ModLoader.Core/Conditions/README.md</c>.
    /// </summary>
    public interface ICwyModConditions : ICwyModFacet
    {
        /// <summary>Parses a <c>when</c> block (a parsed JSON object, or any string-keyed dictionary; null = no conditions).</summary>
        /// <param name="when">The block.</param>
        /// <returns>The set, or every syntax problem.</returns>
        ConditionParseResult Parse(object? when);

        /// <summary>Evaluates a set now. Main thread. Never throws.</summary>
        /// <param name="conditions">The set.</param>
        /// <returns>Met, NotMet, Unavailable or Invalid, with the reason; only Met means "apply".</returns>
        ConditionResult Evaluate(ConditionSet conditions);

        /// <summary>Parses and evaluates in one call; a syntax problem is <see cref="ConditionOutcome.Invalid"/>.</summary>
        /// <param name="when">The block.</param>
        ConditionResult Evaluate(object? when);

        /// <summary>
        /// Adds a token packs can test as <c>&lt;modId&gt;/&lt;name&gt;</c>. Its values come from
        /// <paramref name="values"/>, called on the main thread with the clause's argument (or null);
        /// return null while the value is not known. Call <see cref="NotifyTokenChanged"/> when it changes.
        /// </summary>
        /// <param name="name">The name after the slash: a letter, then letters, digits or <c>_</c>.</param>
        /// <param name="kind">How its values compare.</param>
        /// <param name="description">One sentence for <c>cond tokens</c>.</param>
        /// <param name="values">The current values for an argument.</param>
        /// <param name="argument">Whether it takes an argument after a colon.</param>
        /// <returns>The handle; refused (with the reason) when the name is unusable or taken.</returns>
        CwyRegistration RegisterToken(
            string name,
            ConditionValueKind kind,
            string description,
            Func<string?, IReadOnlyList<string>?> values,
            ConditionArgumentRule argument = ConditionArgumentRule.None);

        /// <summary>Says one of this mod's tokens changed, so conditions that use it are checked again.</summary>
        /// <param name="name">The name after the slash.</param>
        void NotifyTokenChanged(string name);

        /// <summary>Called whenever conditions may answer differently (<see cref="ConditionsChanged"/>). Main thread.</summary>
        /// <param name="handler">Your callback.</param>
        /// <returns>Dispose to unsubscribe.</returns>
        IDisposable OnChanged(Action<ConditionsChanged> handler);
    }

    /// <summary>The per-mod Conditions facet, over <see cref="ConditionRegistry.Shared"/>.</summary>
    internal sealed class ModConditions : ICwyModConditions
    {
        private readonly ModHelper _helper;

        internal ModConditions(ModHelper helper) => _helper = helper;

        private static ConditionRegistry Registry => ConditionRegistry.Shared;

        public bool IsAvailable => Registry.HasEvaluator;

        public string Status =>
            Registry.HasEvaluator
                ? "conditions evaluated by " + Registry.Evaluator.GetType().Name + "; " + Registry.Tokens.Count + " token(s)"
                : UnavailableConditionEvaluator.Reason;

        public ConditionParseResult Parse(object? when) => ConditionParser.Read(when);

        public ConditionResult Evaluate(ConditionSet conditions)
        {
            if (conditions is null)
            {
                throw new ArgumentNullException(nameof(conditions));
            }

            if (!Registry.HasEvaluator && !conditions.IsEmpty)
            {
                _helper.WarnOnce("conditions", UnavailableConditionEvaluator.Reason + "; Evaluate answers Unavailable");
            }

            return Registry.Evaluate(conditions);
        }

        public ConditionResult Evaluate(object? when)
        {
            var parsed = ConditionParser.Read(when);
            return parsed.IsValid
                ? Evaluate(parsed.Conditions)
                : ConditionResult.Invalid(string.Join("; ", parsed.Errors));
        }

        public CwyRegistration RegisterToken(
            string name,
            ConditionValueKind kind,
            string description,
            Func<string?, IReadOnlyList<string>?> values,
            ConditionArgumentRule argument = ConditionArgumentRule.None)
        {
            if (values is null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            var key = _helper.ModId + "/" + name;
            if (!_helper.IsActive)
            {
                return CwyRegistration.Refused(key, "the mod " + _helper.ModId + " is not running");
            }

            ConditionTokenInfo info;
            try
            {
                info = new ConditionTokenInfo(key, kind, UpdatePoint.ModToken, description, argument);
            }
            catch (ArgumentException ex)
            {
                return Refuse(key, ex.Message);
            }

            var source = new ModTokenSource(_helper, key, values);
            if (!Registry.TryRegister(_helper.ModId, info, source, out var registration, out var problem))
            {
                return Refuse(key, problem);
            }

            return _helper.Track(key, registration!);
        }

        public void NotifyTokenChanged(string name)
        {
            if (!_helper.IsActive)
            {
                return;
            }

            Registry.RaiseChanged(new ConditionsChanged(UpdatePoint.ModToken, _helper.ModId + "/" + name));
        }

        public IDisposable OnChanged(Action<ConditionsChanged> handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            return _helper.Own(Registry.Subscribe(change => _helper.Guard(ModStage.Event, "ConditionsChanged", () => handler(change))));
        }

        private CwyRegistration Refuse(string key, string problem)
        {
            _helper.WarnOnce("token:" + key, "condition token '" + key + "' refused: " + problem);
            return CwyRegistration.Refused(key, problem);
        }

        /// <summary>A mod's token values, read with the mod's attribution; a throwing provider counts as "not known".</summary>
        private sealed class ModTokenSource : IConditionSource
        {
            private readonly ModHelper _helper;
            private readonly string _key;
            private readonly Func<string?, IReadOnlyList<string>?> _values;

            internal ModTokenSource(ModHelper helper, string key, Func<string?, IReadOnlyList<string>?> values)
            {
                _helper = helper;
                _key = key;
                _values = values;
            }

            public bool TryGetValues(ConditionTokenInfo token, string? argument, out IReadOnlyList<string> values)
            {
                IReadOnlyList<string>? result = null;
                _helper.Guard(ModStage.Token, "token " + _key, () => result = _values(argument));
                values = result ?? Array.Empty<string>();
                return result != null;
            }
        }
    }
}
