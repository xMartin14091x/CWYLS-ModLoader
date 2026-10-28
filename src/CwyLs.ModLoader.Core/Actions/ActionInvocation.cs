using System;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// Runs one action on the Unity main thread and says how it went. Must not block for long
    /// (the dispatcher's frame budget is 4 ms); a handler that throws is reported by the runner
    /// as <see cref="ActionStatus.Failed"/>.
    /// </summary>
    /// <param name="invocation">The validated, gated call.</param>
    /// <returns>The outcome; never null.</returns>
    public delegate ActionResult ActionHandler(ActionInvocation invocation);

    /// <summary>A request to run an action, before validation and gating (what the API, console, scripts and mods hand the runner). Immutable.</summary>
    public sealed class ActionRequest
    {
        /// <summary>Creates a request.</summary>
        /// <param name="name">The action name.</param>
        /// <param name="args">Its arguments; null means none.</param>
        /// <param name="caller">Who asks.</param>
        /// <param name="callerId">A name for the audit line: the mod id, <c>console</c>, the scene's group id, the API client name.</param>
        /// <param name="requestId">An idempotency key (API), or null.</param>
        /// <param name="timeout">How long the caller waits for the main thread; null means the runner's default (2 s).</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="callerId"/> is blank.</exception>
        public ActionRequest(
            string name,
            ActionArgs? args,
            ActionCaller caller,
            string callerId,
            string? requestId = null,
            TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("an action request needs a name", nameof(name));
            }

            if (string.IsNullOrWhiteSpace(callerId))
            {
                throw new ArgumentException("an action request needs a caller id for the audit line", nameof(callerId));
            }

            if (!Enum.IsDefined(typeof(ActionCaller), caller))
            {
                throw new ArgumentException("unknown caller " + (int)caller, nameof(caller));
            }

            Name = name.Trim();
            Args = args ?? ActionArgs.Empty;
            Caller = caller;
            CallerId = callerId;
            RequestId = requestId;
            Timeout = timeout;
        }

        /// <summary>The action name as asked (matched case-insensitively).</summary>
        public string Name { get; }

        /// <summary>The arguments.</summary>
        public ActionArgs Args { get; }

        /// <summary>Who asks.</summary>
        public ActionCaller Caller { get; }

        /// <summary>A name for the audit line.</summary>
        public string CallerId { get; }

        /// <summary>An idempotency key, or null.</summary>
        public string? RequestId { get; }

        /// <summary>How long the caller waits, or null for the runner's default.</summary>
        public TimeSpan? Timeout { get; }

        /// <summary><c>music.play by Api(my-client)</c>.</summary>
        public override string ToString() => Name + " by " + Caller + "(" + CallerId + ")";
    }

    /// <summary>What a handler receives: the request after validation, gating and (if needed) confirmation. Immutable.</summary>
    public sealed class ActionInvocation
    {
        /// <summary>Creates an invocation. Called by the runner.</summary>
        /// <param name="descriptor">The action.</param>
        /// <param name="request">The request.</param>
        /// <param name="confirmed">True when the player approved it in the in-game confirmation.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public ActionInvocation(ActionDescriptor descriptor, ActionRequest request, bool confirmed)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Confirmed = confirmed;
        }

        /// <summary>The action.</summary>
        public ActionDescriptor Descriptor { get; }

        /// <summary>The request as made.</summary>
        public ActionRequest Request { get; }

        /// <summary>The arguments (shorthand for <c>Request.Args</c>).</summary>
        public ActionArgs Args => Request.Args;

        /// <summary>Who asks (shorthand for <c>Request.Caller</c>).</summary>
        public ActionCaller Caller => Request.Caller;

        /// <summary>True when the player approved it in the in-game confirmation.</summary>
        public bool Confirmed { get; }
    }
}
