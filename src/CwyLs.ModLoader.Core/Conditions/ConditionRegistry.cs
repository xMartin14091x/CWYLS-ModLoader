using System;
using System.Collections.Generic;
using System.Threading;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Conditions
{
    /// <summary>One registered token: who owns it, what it is, and where its values come from.</summary>
    public sealed class ConditionTokenRegistration
    {
        internal ConditionTokenRegistration(string owner, ConditionTokenInfo info, IConditionSource source)
        {
            Owner = owner;
            Info = info;
            Source = source;
        }

        /// <summary><see cref="ConditionRegistry.FrameworkOwner"/> or the registering mod's id.</summary>
        public string Owner { get; }

        /// <summary>The token.</summary>
        public ConditionTokenInfo Info { get; }

        /// <summary>Where its values come from.</summary>
        public IConditionSource Source { get; }
    }

    /// <summary>
    /// The condition tokens that exist, the evaluator that answers a <see cref="ConditionSet"/>,
    /// and the <see cref="ConditionsChanged"/> channel. An ordinary object so it can be tested in
    /// isolation; <see cref="Shared"/> is the one the game uses (the plugins that contribute tokens
    /// are separate assemblies, as with <c>CommandRegistry.Shared</c>). Thread-safe.
    /// </summary>
    /// <remarks>
    /// Ownership rules: the framework (<see cref="FrameworkOwner"/>) registers bare names (the
    /// built-ins in <see cref="ConditionTokens"/>); a mod registers only
    /// <c>&lt;its id&gt;/&lt;name&gt;</c>. A name is never replaced: a second registration is refused
    /// until the first is disposed.
    /// </remarks>
    public sealed class ConditionRegistry
    {
        /// <summary>The owner name of the framework's own tokens.</summary>
        public const string FrameworkOwner = "cwy";

        private static readonly ConditionRegistry SharedInstance = new ConditionRegistry();

        private readonly object _gate = new object();
        private readonly Dictionary<string, ConditionTokenRegistration> _byName =
            new Dictionary<string, ConditionTokenRegistration>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ConditionTokenRegistration> _order = new List<ConditionTokenRegistration>();
        private readonly EventDispatcher<ConditionsChanged> _changed;
        private IConditionEvaluator _evaluator = UnavailableConditionEvaluator.Instance;

        /// <summary>Creates an empty registry with the unavailable evaluator.</summary>
        public ConditionRegistry()
        {
            _changed = new EventDispatcher<ConditionsChanged>(
                nameof(ConditionsChanged),
                failure => ErrorSink?.Invoke(
                    "a ConditionsChanged handler (" + failure.Subscriber + ") threw " + failure.Error.GetType().Name + ": " + failure.Error.Message));
        }

        /// <summary>The registry the running game uses.</summary>
        public static ConditionRegistry Shared => SharedInstance;

        /// <summary>
        /// Where evaluator and handler failures are reported (the framework sets the BepInEx log).
        /// Null means they are only turned into <see cref="ConditionOutcome.Invalid"/> results.
        /// </summary>
        public Action<string>? ErrorSink { get; set; }

        /// <summary>The installed evaluator; <see cref="UnavailableConditionEvaluator"/> until one is installed. Never null.</summary>
        public IConditionEvaluator Evaluator => Volatile.Read(ref _evaluator);

        /// <summary>True once a real evaluator is installed.</summary>
        public bool HasEvaluator => !(Evaluator is UnavailableConditionEvaluator);

        /// <summary>Every registered token, in registration order.</summary>
        public IReadOnlyList<ConditionTokenRegistration> Tokens
        {
            get { lock (_gate) { return _order.ToArray(); } }
        }

        /// <summary>Installs the evaluator; a later call replaces it.</summary>
        /// <param name="evaluator">The evaluator.</param>
        /// <exception cref="ArgumentNullException"><paramref name="evaluator"/> is null.</exception>
        public void UseEvaluator(IConditionEvaluator evaluator) =>
            Volatile.Write(ref _evaluator, evaluator ?? throw new ArgumentNullException(nameof(evaluator)));

        /// <summary>The registered token with this name (case-insensitive, without argument).</summary>
        /// <param name="name">The token key, e.g. <c>weekday</c>.</param>
        /// <param name="registration">The registration, or null.</param>
        public bool TryGetToken(string? name, out ConditionTokenRegistration? registration)
        {
            registration = null;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            lock (_gate)
            {
                return _byName.TryGetValue(name!, out registration);
            }
        }

        /// <summary>Registers a token. Refuses, never replaces.</summary>
        /// <param name="owner"><see cref="FrameworkOwner"/> or a mod id.</param>
        /// <param name="info">The token.</param>
        /// <param name="source">Where its values come from.</param>
        /// <param name="registration">Dispose to remove the token; null on refusal.</param>
        /// <param name="problem">Why it was refused, or empty.</param>
        /// <returns>True when registered.</returns>
        public bool TryRegister(
            string owner,
            ConditionTokenInfo info,
            IConditionSource source,
            out IDisposable? registration,
            out string problem)
        {
            registration = null;
            if (info is null || source is null)
            {
                problem = "a token needs its description and a value source";
                return false;
            }

            if (!IsFramework(owner) && !PackManifest.IsValidId(owner))
            {
                problem = "\"" + owner + "\" is not an owner: use \"" + FrameworkOwner + "\" or a mod id";
                return false;
            }

            if (IsFramework(owner) && info.Namespace != null)
            {
                problem = "framework tokens have no \"<modId>/\" prefix: \"" + info.Name + "\"";
                return false;
            }

            if (!IsFramework(owner) && !string.Equals(info.Namespace, owner, StringComparison.OrdinalIgnoreCase))
            {
                problem = "a mod's token is named \"" + owner + "/<name>\", not \"" + info.Name + "\"";
                return false;
            }

            var entry = new ConditionTokenRegistration(IsFramework(owner) ? FrameworkOwner : owner, info, source);
            lock (_gate)
            {
                if (_byName.TryGetValue(info.Name, out var existing))
                {
                    problem = "the token \"" + info.Name + "\" is already registered by " + existing.Owner;
                    return false;
                }

                _byName.Add(info.Name, entry);
                _order.Add(entry);
            }

            problem = string.Empty;
            registration = new Registration(this, entry);
            return true;
        }

        /// <summary>Registers a token, throwing on refusal.</summary>
        /// <param name="owner"><see cref="FrameworkOwner"/> or a mod id.</param>
        /// <param name="info">The token.</param>
        /// <param name="source">Where its values come from.</param>
        /// <returns>Dispose to remove the token.</returns>
        /// <exception cref="InvalidOperationException">The registration was refused; the message says why.</exception>
        public IDisposable Register(string owner, ConditionTokenInfo info, IConditionSource source)
        {
            if (TryRegister(owner, info, source, out var registration, out var problem))
            {
                return registration!;
            }

            throw new InvalidOperationException(problem);
        }

        /// <summary>
        /// The points at which <paramref name="conditions"/> can change its answer: the union of its
        /// tokens' points. An unregistered token counts as <see cref="UpdatePoint.All"/>; an empty
        /// set is <see cref="UpdatePoint.None"/>.
        /// </summary>
        /// <param name="conditions">The set.</param>
        public UpdatePoint UpdatePointsOf(ConditionSet? conditions)
        {
            var points = UpdatePoint.None;
            if (conditions is null)
            {
                return points;
            }

            foreach (var clause in conditions.Clauses)
            {
                points |= TryGetToken(clause.Token, out var token) ? token!.Info.UpdatePoints : UpdatePoint.All;
            }

            return points;
        }

        /// <summary>
        /// Checks a parsed set against the tokens registered now: unknown tokens and argument
        /// rules. Values are the evaluator's business. For lint and load-time reports.
        /// </summary>
        /// <param name="conditions">The set.</param>
        /// <returns>One line per problem; empty when the set is usable.</returns>
        public IReadOnlyList<string> Validate(ConditionSet? conditions)
        {
            var problems = new List<string>();
            if (conditions is null)
            {
                return problems;
            }

            foreach (var clause in conditions.Clauses)
            {
                if (!TryGetToken(clause.Token, out var token))
                {
                    problems.Add("\"" + clause.Key + "\": no token named \"" + clause.Token + "\" is registered");
                    continue;
                }

                var rule = token!.Info.Argument;
                if (rule == ConditionArgumentRule.Required && clause.Argument is null)
                {
                    problems.Add("\"" + clause.Key + "\": " + clause.Token + " needs an argument, like " + clause.Token + ":<name>");
                }
                else if (rule == ConditionArgumentRule.None && clause.Argument != null)
                {
                    problems.Add("\"" + clause.Key + "\": " + clause.Token + " takes no argument");
                }
            }

            return problems;
        }

        /// <summary>
        /// Evaluates a set with the installed evaluator. Never throws: an evaluator failure is
        /// reported to <see cref="ErrorSink"/> and answered <see cref="ConditionOutcome.Invalid"/>.
        /// </summary>
        /// <param name="conditions">The set; null counts as empty.</param>
        public ConditionResult Evaluate(ConditionSet? conditions)
        {
            var set = conditions ?? ConditionSet.Empty;
            try
            {
                return Evaluator.Evaluate(set) ?? ConditionResult.Invalid("the condition evaluator returned no answer");
            }
            catch (Exception ex)
            {
                var reason = "the condition evaluator failed: " + ex.GetType().Name + ": " + ex.Message;
                ErrorSink?.Invoke(reason + " (conditions: " + set + ")");
                return ConditionResult.Invalid(reason);
            }
        }

        /// <summary>Subscribes to <see cref="ConditionsChanged"/>. A throwing handler is reported and the others still run.</summary>
        /// <param name="handler">The callback.</param>
        /// <returns>Dispose to unsubscribe.</returns>
        public IDisposable Subscribe(Action<ConditionsChanged> handler) => _changed.Subscribe(handler);

        /// <summary>Tells every subscriber that conditions may answer differently. Never throws.</summary>
        /// <param name="change">What changed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
        public void RaiseChanged(ConditionsChanged change) =>
            _changed.Publish(change ?? throw new ArgumentNullException(nameof(change)));

        private static bool IsFramework(string? owner) =>
            string.Equals(owner, FrameworkOwner, StringComparison.OrdinalIgnoreCase);

        private void Remove(ConditionTokenRegistration entry)
        {
            lock (_gate)
            {
                if (_byName.TryGetValue(entry.Info.Name, out var current) && ReferenceEquals(current, entry))
                {
                    _byName.Remove(entry.Info.Name);
                    _order.Remove(entry);
                }
            }
        }

        private sealed class Registration : IDisposable
        {
            private ConditionRegistry? _registry;
            private readonly ConditionTokenRegistration _entry;

            internal Registration(ConditionRegistry registry, ConditionTokenRegistration entry)
            {
                _registry = registry;
                _entry = entry;
            }

            public void Dispose() => Interlocked.Exchange(ref _registry, null)?.Remove(_entry);
        }
    }
}
