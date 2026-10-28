using System;
using System.Threading;
using System.Threading.Tasks;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// Asks the player, in the game's own UI and outside any LLM, whether an action may run.
    /// Until an in-game dialog is installed with <see cref="ActionServices.UseConfirmationGate"/>,
    /// <see cref="DenyingConfirmationGate"/> answers and every Confirm verdict ends as Declined.
    /// </summary>
    public interface IConfirmationGate
    {
        /// <summary>
        /// Shows the question and completes when the player answers, the request times out
        /// (<see cref="ConfirmationRequest.Timeout"/>: timeout means no), or the token is cancelled.
        /// Called from any thread; the implementation owns the main-thread hop. Never faults: a
        /// failure to show the dialog is <see cref="ConfirmationOutcome.Unavailable"/>.
        /// </summary>
        /// <param name="request">What to ask.</param>
        /// <param name="cancellationToken">Stops waiting (counts as not approved).</param>
        /// <returns>The answer.</returns>
        Task<ConfirmationResult> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken);
    }

    /// <summary>What the confirmation asks. Immutable.</summary>
    public sealed class ConfirmationRequest
    {
        /// <summary>How long the player has to answer by default before it counts as no.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Creates a request.</summary>
        /// <param name="actionName">The action being confirmed.</param>
        /// <param name="title">The dialog title (short).</param>
        /// <param name="message">What will happen, in plain words, including what is deleted.</param>
        /// <param name="caller">Who asks.</param>
        /// <param name="callerId">The caller's name, shown to the player.</param>
        /// <param name="isDestructive">True for a delete: the dialog says it cannot be undone.</param>
        /// <param name="timeout">How long to wait; null means <see cref="DefaultTimeout"/>.</param>
        /// <param name="arguments">
        /// The call's arguments as given, so the dialog can show the player exactly what
        /// is confirmed (resolve an id to a title, quote the text). Unredacted: never log them;
        /// <paramref name="message"/> is the redacted text. Null means none.
        /// </param>
        /// <exception cref="ArgumentException">A text is blank, or the timeout is not positive.</exception>
        public ConfirmationRequest(
            string actionName,
            string title,
            string message,
            ActionCaller caller,
            string callerId,
            bool isDestructive,
            TimeSpan? timeout = null,
            ActionArgs? arguments = null)
        {
            if (string.IsNullOrWhiteSpace(actionName) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("a confirmation needs the action name, a title and a message");
            }

            if (string.IsNullOrWhiteSpace(callerId))
            {
                throw new ArgumentException("a confirmation names who asks", nameof(callerId));
            }

            var wait = timeout ?? DefaultTimeout;
            if (wait <= TimeSpan.Zero)
            {
                throw new ArgumentException("the timeout must be positive", nameof(timeout));
            }

            ActionName = actionName;
            Title = title;
            Message = message;
            Caller = caller;
            CallerId = callerId;
            IsDestructive = isDestructive;
            Timeout = wait;
            Arguments = arguments ?? ActionArgs.Empty;
        }

        /// <summary>The action being confirmed.</summary>
        public string ActionName { get; }

        /// <summary>The dialog title.</summary>
        public string Title { get; }

        /// <summary>What will happen.</summary>
        public string Message { get; }

        /// <summary>Who asks.</summary>
        public ActionCaller Caller { get; }

        /// <summary>The caller's name, shown to the player.</summary>
        public string CallerId { get; }

        /// <summary>True for a delete.</summary>
        public bool IsDestructive { get; }

        /// <summary>How long to wait before it counts as no.</summary>
        public TimeSpan Timeout { get; }

        /// <summary>The call's arguments, unredacted, for the dialog only (never log them). Never null.</summary>
        public ActionArgs Arguments { get; }
    }

    /// <summary>How the confirmation ended.</summary>
    public enum ConfirmationOutcome
    {
        /// <summary>The player said yes.</summary>
        Approved = 0,

        /// <summary>The player said no (or closed the dialog).</summary>
        Declined = 1,

        /// <summary>Nobody answered in time.</summary>
        TimedOut = 2,

        /// <summary>No dialog could be shown (none installed, the UI is not up, the request was cancelled).</summary>
        Unavailable = 3,
    }

    /// <summary>The confirmation's answer. Immutable.</summary>
    public sealed class ConfirmationResult
    {
        private ConfirmationResult(ConfirmationOutcome outcome, string detail)
        {
            Outcome = outcome;
            Detail = detail ?? string.Empty;
        }

        /// <summary>How it ended.</summary>
        public ConfirmationOutcome Outcome { get; }

        /// <summary>True only for <see cref="ConfirmationOutcome.Approved"/>.</summary>
        public bool IsApproved => Outcome == ConfirmationOutcome.Approved;

        /// <summary>Why, in words; may be empty for an approval.</summary>
        public string Detail { get; }

        /// <summary>The player said yes.</summary>
        public static ConfirmationResult Approved() => new ConfirmationResult(ConfirmationOutcome.Approved, string.Empty);

        /// <summary>The player said no.</summary>
        /// <param name="detail">What the dialog reported.</param>
        public static ConfirmationResult Declined(string detail) => new ConfirmationResult(ConfirmationOutcome.Declined, detail);

        /// <summary>Nobody answered in time.</summary>
        /// <param name="detail">The limit.</param>
        public static ConfirmationResult TimedOut(string detail) => new ConfirmationResult(ConfirmationOutcome.TimedOut, detail);

        /// <summary>No dialog could be shown.</summary>
        /// <param name="detail">Why.</param>
        public static ConfirmationResult Unavailable(string detail) => new ConfirmationResult(ConfirmationOutcome.Unavailable, detail);

        /// <summary><c>Declined: the player said no</c>.</summary>
        public override string ToString() => Detail.Length == 0 ? Outcome.ToString() : Outcome + ": " + Detail;
    }

    /// <summary>
    /// The gate before the in-game dialog exists: it never approves. Every request ends as
    /// <see cref="ConfirmationOutcome.Unavailable"/> at once, so a Confirm verdict can never
    /// turn into a silent yes.
    /// </summary>
    public sealed class DenyingConfirmationGate : IConfirmationGate
    {
        /// <summary>The detail every answer carries.</summary>
        public const string Reason = "no in-game confirmation dialog is installed; the request is refused";

        private DenyingConfirmationGate()
        {
        }

        /// <summary>The one instance.</summary>
        public static DenyingConfirmationGate Instance { get; } = new DenyingConfirmationGate();

        /// <inheritdoc />
        public Task<ConfirmationResult> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ConfirmationResult.Unavailable(Reason));
    }
}
