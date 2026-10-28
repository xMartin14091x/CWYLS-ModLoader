using System;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>The switches the default gate reads. The runner fills it from config each call.</summary>
    public sealed class ActionGateSettings
    {
        /// <summary>The name of the writes switch, for messages: <c>[Data] AllowWrites</c>.</summary>
        public const string AllowWritesKey = "[Data] AllowWrites";

        /// <summary>The name of the API actions switch, for messages: <c>[Api] AllowActions</c>.</summary>
        public const string AllowApiActionsKey = "[Api] AllowActions";

        /// <summary>
        /// Whether Write-tier actions may run at all (<c>[Data] AllowWrites</c>). Default false
        /// (by design).
        /// </summary>
        public bool AllowWrites { get; set; }

        /// <summary>
        /// Whether the local API may run Act- and Write-tier actions (<c>[Api] AllowActions</c>);
        /// reads are always allowed once the API is on. Default false.
        /// </summary>
        public bool AllowApiActions { get; set; }

        /// <summary>A fresh copy of the defaults: everything off.</summary>
        public static ActionGateSettings Defaults => new ActionGateSettings();
    }

    /// <summary>What the gate decided.</summary>
    public enum ActionGateVerdict
    {
        /// <summary>Run it.</summary>
        Allow = 0,

        /// <summary>Run it only after the player says yes in the in-game confirmation (<see cref="IConfirmationGate"/>).</summary>
        Confirm = 1,

        /// <summary>Do not run it.</summary>
        Deny = 2,
    }

    /// <summary>The gate's verdict and the rule behind it. Immutable.</summary>
    public sealed class ActionGateDecision
    {
        internal ActionGateDecision(ActionGateVerdict verdict, string reason)
        {
            Verdict = verdict;
            Reason = reason;
        }

        /// <summary>What to do.</summary>
        public ActionGateVerdict Verdict { get; }

        /// <summary>The rule, in words, naming a config switch when one decides it.</summary>
        public string Reason { get; }

        /// <summary><c>Deny: writes are off ([Data] AllowWrites=false)</c>.</summary>
        public override string ToString() => Verdict + ": " + Reason;
    }

    /// <summary>
    /// The default tier gating every action goes through before its handler runs. Pure, so
    /// the table below is unit-tested.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    ///   <listheader><term>Rule, first match wins</term><description>Verdict</description></listheader>
    ///   <item><term>Read tier</term><description>Allow, every caller</description></item>
    ///   <item><term>API caller, Act or Write, <c>[Api] AllowActions=false</c></term><description>Deny</description></item>
    ///   <item><term>Write tier, <c>[Data] AllowWrites=false</c></term><description>Deny</description></item>
    ///   <item><term>Destructive (a delete), any caller</term><description>Confirm</description></item>
    ///   <item><term>Write tier from the API</term><description>Confirm</description></item>
    ///   <item><term>everything else</term><description>Allow</description></item>
    /// </list>
    /// A Confirm verdict with no confirmation dialog installed ends as Declined:
    /// <see cref="DenyingConfirmationGate"/> never approves.
    /// </remarks>
    public static class ActionGate
    {
        /// <summary>Decides whether an action may run.</summary>
        /// <param name="descriptor">The action.</param>
        /// <param name="caller">Who asks.</param>
        /// <param name="settings">The switches; null means <see cref="ActionGateSettings.Defaults"/>.</param>
        /// <returns>The verdict and its reason.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
        public static ActionGateDecision Decide(ActionDescriptor descriptor, ActionCaller caller, ActionGateSettings? settings = null)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var s = settings ?? ActionGateSettings.Defaults;

            if (descriptor.Tier == ActionTier.Read)
            {
                return new ActionGateDecision(ActionGateVerdict.Allow, "read-only");
            }

            if (caller == ActionCaller.Api && !s.AllowApiActions)
            {
                return new ActionGateDecision(
                    ActionGateVerdict.Deny,
                    "the game API may only read (" + ActionGateSettings.AllowApiActionsKey + "=false)");
            }

            if (descriptor.Tier == ActionTier.Write && !s.AllowWrites)
            {
                return new ActionGateDecision(
                    ActionGateVerdict.Deny,
                    "writes are off (" + ActionGateSettings.AllowWritesKey + "=false)");
            }

            if (descriptor.IsDestructive)
            {
                return new ActionGateDecision(ActionGateVerdict.Confirm, "it deletes or overwrites something; the player confirms in game");
            }

            if (descriptor.Tier == ActionTier.Write && caller == ActionCaller.Api)
            {
                return new ActionGateDecision(ActionGateVerdict.Confirm, "a write from outside the game; the player confirms in game");
            }

            return new ActionGateDecision(ActionGateVerdict.Allow, descriptor.Tier == ActionTier.Write ? "write allowed" : "allowed");
        }
    }
}
