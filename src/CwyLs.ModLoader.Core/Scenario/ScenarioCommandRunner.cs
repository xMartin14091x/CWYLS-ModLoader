using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>How a script-command row lets the scene go on.</summary>
    public enum ScenarioCommandOutcome
    {
        /// <summary>The next row (the game's <c>ReadNext</c>).</summary>
        Continue = 0,

        /// <summary>A row of the same group, by id (the game's <c>SelectionJump</c>).</summary>
        Jump = 1,

        /// <summary>Hold until a check passes or a timeout, then the next row.</summary>
        Wait = 2,
    }

    /// <summary>What a handler decided for its row. Immutable.</summary>
    public sealed class ScenarioCommandDecision
    {
        private ScenarioCommandDecision(ScenarioCommandOutcome outcome, string jumpTarget, Func<bool>? waitCheck, TimeSpan waitTimeout)
        {
            Outcome = outcome;
            JumpTarget = jumpTarget;
            WaitCheck = waitCheck;
            WaitTimeout = waitTimeout;
        }

        /// <summary>Go on with the next row.</summary>
        public static ScenarioCommandDecision Continue { get; } =
            new ScenarioCommandDecision(ScenarioCommandOutcome.Continue, string.Empty, null, TimeSpan.Zero);

        /// <summary>What to do.</summary>
        public ScenarioCommandOutcome Outcome { get; }

        /// <summary>The row id to go on at (<see cref="ScenarioCommandOutcome.Jump"/>); empty otherwise.</summary>
        public string JumpTarget { get; }

        /// <summary>The check to poll once a frame (<see cref="ScenarioCommandOutcome.Wait"/>); null otherwise.</summary>
        public Func<bool>? WaitCheck { get; }

        /// <summary>The longest hold (<see cref="ScenarioCommandOutcome.Wait"/>), at most <see cref="ScenarioCommandContext.MaxWait"/>.</summary>
        public TimeSpan WaitTimeout { get; }

        /// <summary><c>continue</c>, <c>jump cwy_gn_020</c>, <c>wait 3s</c>.</summary>
        public override string ToString()
        {
            switch (Outcome)
            {
                case ScenarioCommandOutcome.Jump:
                    return "jump " + JumpTarget;
                case ScenarioCommandOutcome.Wait:
                    return "wait " + WaitTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
                default:
                    return "continue";
            }
        }

        internal static ScenarioCommandDecision Jump(string target) =>
            new ScenarioCommandDecision(ScenarioCommandOutcome.Jump, target, null, TimeSpan.Zero);

        internal static ScenarioCommandDecision Wait(Func<bool> check, TimeSpan timeout) =>
            new ScenarioCommandDecision(ScenarioCommandOutcome.Wait, string.Empty, check, timeout);
    }

    /// <summary>
    /// The <see cref="IScenarioCommandContext"/> the runtime hands a handler. It enforces the handler
    /// contract (README "Handler contract"): one decision at most (later calls warn and are ignored),
    /// an unknown jump target warns and continues, a wait is clamped to <see cref="MaxWait"/>.
    /// Not thread-safe: a handler runs on the main thread and decides there.
    /// </summary>
    public sealed class ScenarioCommandContext : IScenarioCommandContext
    {
        /// <summary>The longest <see cref="WaitFor"/> hold.</summary>
        public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

        private readonly Func<string, bool>? _hasRow;
        private readonly Action<string>? _warn;
        private ScenarioCommandDecision? _decision;

        /// <summary>Creates a context for one row. Called by the runtime (and by tests).</summary>
        /// <param name="row">The row being run.</param>
        /// <param name="scenarioType">The group's scene type name.</param>
        /// <param name="hasRow">True when the group has a row with this id (for <see cref="JumpTo"/>); null accepts every id.</param>
        /// <param name="warn">Where contract warnings go; null drops them.</param>
        /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
        public ScenarioCommandContext(ScenarioCommandRow row, string? scenarioType, Func<string, bool>? hasRow, Action<string>? warn)
        {
            Row = row ?? throw new ArgumentNullException(nameof(row));
            ScenarioType = scenarioType ?? string.Empty;
            _hasRow = hasRow;
            _warn = warn;
        }

        /// <inheritdoc />
        public ScenarioCommandRow Row { get; }

        /// <inheritdoc />
        public string GroupId => Row.GroupId;

        /// <inheritdoc />
        public string ScenarioType { get; }

        /// <inheritdoc />
        public bool HasDecided => _decision != null;

        /// <summary>The decision so far; <see cref="ScenarioCommandDecision.Continue"/> when the handler made none.</summary>
        public ScenarioCommandDecision Decision => _decision ?? ScenarioCommandDecision.Continue;

        /// <inheritdoc />
        public void Continue()
        {
            if (Decide("Continue"))
            {
                _decision = ScenarioCommandDecision.Continue;
            }
        }

        /// <inheritdoc />
        public void JumpTo(string rowId)
        {
            if (!Decide("JumpTo"))
            {
                return;
            }

            var target = rowId?.Trim() ?? string.Empty;
            if (target.Length == 0 || (_hasRow != null && !_hasRow(target)))
            {
                Warn("JumpTo(\"" + target + "\"): the group " + Row.GroupId + " has no such row; going on with the next row");
                _decision = ScenarioCommandDecision.Continue;
                return;
            }

            _decision = ScenarioCommandDecision.Jump(target);
        }

        /// <inheritdoc />
        public void WaitFor(Func<bool> isDone, TimeSpan timeout)
        {
            if (!Decide("WaitFor"))
            {
                return;
            }

            if (isDone is null)
            {
                Warn("WaitFor without a check; going on with the next row");
                _decision = ScenarioCommandDecision.Continue;
                return;
            }

            if (timeout > MaxWait)
            {
                Warn("WaitFor(" + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s) is longer than the "
                     + MaxWait.TotalMinutes.ToString(CultureInfo.InvariantCulture) + "-minute limit; holding for the limit");
                timeout = MaxWait;
            }
            else if (timeout < TimeSpan.Zero)
            {
                timeout = TimeSpan.Zero;
            }

            _decision = ScenarioCommandDecision.Wait(isDone, timeout);
        }

        private bool Decide(string call)
        {
            if (_decision is null)
            {
                return true;
            }

            Warn(call + " after the row already chose to " + _decision + "; ignored");
            return false;
        }

        private void Warn(string message) => _warn?.Invoke(Row + ": " + message);
    }

    /// <summary>
    /// Runs one script-command row: finds the handler, calls it with a <see cref="ScenarioCommandContext"/>,
    /// and turns whatever happened into a <see cref="ScenarioCommandDecision"/>. A row with no handler
    /// and a handler that throws both continue with the next row (with a warning): a broken command
    /// never stalls a scene. Pure; the game side applies the decision.
    /// </summary>
    public static class ScenarioCommandRunner
    {
        /// <summary>Runs one row.</summary>
        /// <param name="registry">Where the handler is looked up.</param>
        /// <param name="row">The row.</param>
        /// <param name="scenarioType">The group's scene type name.</param>
        /// <param name="hasRow">True when the group has a row with this id; null accepts every id.</param>
        /// <param name="warn">Where warnings go (no handler, a throw, contract breaches); null drops them.</param>
        /// <returns>The decision; never null.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="row"/> is null.</exception>
        public static ScenarioCommandDecision Run(
            ScenarioCommandRegistry registry,
            ScenarioCommandRow row,
            string? scenarioType,
            Func<string, bool>? hasRow,
            Action<string>? warn)
        {
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (row is null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            if (!registry.TryGetBinding(row.Command, out var binding) || binding is null)
            {
                warn?.Invoke(row + ": no handler is registered for this command; going on with the next row");
                return ScenarioCommandDecision.Continue;
            }

            var context = new ScenarioCommandContext(row, scenarioType, hasRow, warn);
            try
            {
                binding.Handler(context);
            }
            catch (Exception ex)
            {
                warn?.Invoke(row + ": the handler from " + binding.Owner + " threw " + ex.GetType().Name + ": " + ex.Message
                             + "; going on with the next row");
                return ScenarioCommandDecision.Continue;
            }

            return context.Decision;
        }
    }
}
