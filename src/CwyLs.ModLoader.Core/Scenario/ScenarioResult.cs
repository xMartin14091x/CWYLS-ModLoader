using System;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>
    /// The outcome of a scene request that can be refused (play a pack scene, set a flag, queue a
    /// deferred action): whether it was accepted and one line for people. Immutable.
    /// </summary>
    public sealed class ScenarioResult
    {
        private ScenarioResult(bool isOk, string message)
        {
            IsOk = isOk;
            Message = message;
        }

        /// <summary>True when the request was accepted.</summary>
        public bool IsOk { get; }

        /// <summary>What happened (<c>playing cwy_demo_script_01</c>) or why not (<c>a scene is playing</c>). Never null.</summary>
        public string Message { get; }

        /// <summary>An accepted request.</summary>
        /// <param name="message">What happened.</param>
        public static ScenarioResult Ok(string message) => new ScenarioResult(true, message ?? string.Empty);

        /// <summary>A refused request.</summary>
        /// <param name="reason">Why; required.</param>
        /// <exception cref="ArgumentException"><paramref name="reason"/> is blank.</exception>
        public static ScenarioResult Refused(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("a refusal needs a reason", nameof(reason));
            }

            return new ScenarioResult(false, reason);
        }

        /// <summary><c>ok: playing …</c> or <c>refused: …</c>.</summary>
        public override string ToString() => (IsOk ? "ok: " : "refused: ") + Message;
    }
}
