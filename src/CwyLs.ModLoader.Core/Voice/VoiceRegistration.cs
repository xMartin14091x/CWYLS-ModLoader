using System;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// The result of one <see cref="VoiceRegistry.Register"/> call: what happened, what it
    /// displaced, and a sentence explaining it that is fit for a log line.
    /// </summary>
    public sealed class VoiceRegistration
    {
        private VoiceRegistration(
            VoiceRegistrationOutcome outcome, VoiceEntry entry, VoiceEntry? previous, string reason)
        {
            Outcome = outcome;
            Entry = entry;
            Previous = previous;
            Reason = reason;
        }

        /// <summary>What the registry did.</summary>
        public VoiceRegistrationOutcome Outcome { get; }

        /// <summary>The entry that was offered.</summary>
        public VoiceEntry Entry { get; }

        /// <summary>
        /// The entry that held the name before this call. Set for
        /// <see cref="VoiceRegistrationOutcome.Replaced"/> (the one displaced) and for
        /// <see cref="VoiceRegistrationOutcome.KeptExisting"/> (the one that won); null otherwise.
        /// </summary>
        public VoiceEntry? Previous { get; }

        /// <summary>One sentence saying what happened and why. Never null.</summary>
        public string Reason { get; }

        /// <summary>True when the registry now resolves the name to <see cref="Entry"/>.</summary>
        public bool Accepted =>
            Outcome == VoiceRegistrationOutcome.Added || Outcome == VoiceRegistrationOutcome.Replaced;

        /// <summary>The name was free.</summary>
        /// <param name="entry">The accepted entry.</param>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
        public static VoiceRegistration Added(VoiceEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            return new VoiceRegistration(
                VoiceRegistrationOutcome.Added, entry, null, "registered from " + entry.Source);
        }

        /// <summary>The entry displaced <paramref name="previous"/>.</summary>
        /// <param name="entry">The accepted entry.</param>
        /// <param name="previous">The entry it displaced.</param>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> or <paramref name="previous"/> is null.</exception>
        public static VoiceRegistration Replaced(VoiceEntry entry, VoiceEntry previous)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (previous is null)
            {
                throw new ArgumentNullException(nameof(previous));
            }

            return new VoiceRegistration(
                VoiceRegistrationOutcome.Replaced,
                entry,
                previous,
                "registered from " + entry.Source + ", overriding " + previous.Source);
        }

        /// <summary>The name stayed with <paramref name="winner"/>.</summary>
        /// <param name="entry">The refused entry.</param>
        /// <param name="winner">The entry that keeps the name.</param>
        /// <param name="reason">Why it keeps it.</param>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> or <paramref name="winner"/> is null.</exception>
        public static VoiceRegistration KeptExisting(VoiceEntry entry, VoiceEntry winner, string reason)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (winner is null)
            {
                throw new ArgumentNullException(nameof(winner));
            }

            return new VoiceRegistration(
                VoiceRegistrationOutcome.KeptExisting,
                entry,
                winner,
                string.IsNullOrWhiteSpace(reason) ? "kept " + winner.Source : reason);
        }

        /// <summary>The name is not a usable voice name.</summary>
        /// <param name="entry">The refused entry.</param>
        /// <param name="reason">Why the name was refused.</param>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
        public static VoiceRegistration Invalid(VoiceEntry entry, string reason)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            return new VoiceRegistration(
                VoiceRegistrationOutcome.Invalid,
                entry,
                null,
                string.IsNullOrWhiteSpace(reason) ? VoiceNameRules.CharacterRuleReason : reason);
        }

        /// <inheritdoc />
        public override string ToString() => Entry.Name + ": " + Outcome + " - " + Reason;
    }
}
