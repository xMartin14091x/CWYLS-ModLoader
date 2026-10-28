using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Threading;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>Counters of one runner since it was created. Immutable snapshot.</summary>
    public sealed class ActionRunnerStats
    {
        internal ActionRunnerStats(long requests, long ok, long refused, long failed, long timedOut, long late)
        {
            Requests = requests;
            Ok = ok;
            Refused = refused;
            Failed = failed;
            TimedOut = timedOut;
            Late = late;
        }

        /// <summary>Requests received.</summary>
        public long Requests { get; }

        /// <summary>Requests that ended <see cref="ActionStatus.Ok"/>.</summary>
        public long Ok { get; }

        /// <summary>Requests refused before anything ran: not found, invalid arguments, denied, declined.</summary>
        public long Refused { get; }

        /// <summary>Requests that ended <see cref="ActionStatus.Failed"/> or <see cref="ActionStatus.NotAvailable"/>.</summary>
        public long Failed { get; }

        /// <summary>Requests the caller stopped waiting for (<see cref="ActionStatus.TimedOut"/>).</summary>
        public long TimedOut { get; }

        /// <summary>Handlers that finished after their caller stopped waiting.</summary>
        public long Late { get; }

        /// <summary><c>requests=5 ok=3 refused=1 failed=0 timedOut=1 late=0</c>.</summary>
        public override string ToString() =>
            "requests=" + Requests + " ok=" + Ok + " refused=" + Refused + " failed=" + Failed +
            " timedOut=" + TimedOut + " late=" + Late;
    }

    /// <summary>
    /// The action runner: look up → check the arguments against the schema
    /// (<see cref="ActionArgsValidation"/>) → tier gate (<see cref="ActionGate"/> with the live
    /// switches from <see cref="ActionServices.GateSettings"/>) → in-game confirmation when the gate
    /// asks for one (<see cref="ActionServices.ConfirmationGate"/>) → hop to the main thread through
    /// the shared dispatcher with the caller's timeout → the handler → one <c>ACTION</c> audit record.
    /// Every outcome is an <see cref="ActionResult"/>; the runner never throws and never faults.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Timeouts.</b> <see cref="ActionRequest.Timeout"/> bounds the main-thread hop only
    /// (null → <see cref="DefaultTimeout"/>, more than <see cref="MaxTimeout"/> is cut to it, zero
    /// or less is InvalidArgs). The confirmation has its own limit
    /// (<see cref="ConfirmationRequest.DefaultTimeout"/>; no answer = no).</item>
    /// <item><b>Late handlers.</b> A handler that started before the limit but finished after it is
    /// reported to the caller as TimedOut and gets a second audit record marked late with its real
    /// outcome, so the audit never claims that something which ran did not.</item>
    /// <item><b>Owners.</b> A mod caller's work runs in that mod's dispatcher lane
    /// (<c>mod:&lt;id&gt;</c>, budgeted, dropped when the mod stops); everything else under
    /// <see cref="Owner"/> in the framework lane.</item>
    /// <item><b>Never block on the main thread.</b> The hop is always queued, so a main-thread caller
    /// that waits synchronously for the task stalls until the timeout and gets TimedOut. Await it
    /// or continue from it (the console, scripts and mods do).</item>
    /// </list>
    /// </remarks>
    public sealed class ActionRunner : IActionRunner
    {
        /// <summary>The main-thread hop's limit when the request names none (the dispatcher's default).</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

        /// <summary>The longest main-thread hop a request may ask for; longer ones are cut to this.</summary>
        public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

        /// <summary>The dispatcher owner of every non-mod call.</summary>
        public const string Owner = "actions";

        private readonly ActionRegistry _registry;
        private readonly IActionDispatcher _dispatcher;
        private readonly ActionServices _services;
        private readonly Action<ActionAuditRecord>? _audit;

        private long _requests;
        private long _ok;
        private long _refused;
        private long _failed;
        private long _timedOut;
        private long _late;

        /// <summary>Creates a runner.</summary>
        /// <param name="registry">Where actions are looked up (<see cref="ActionRegistry.Shared"/> in game).</param>
        /// <param name="dispatcher">The main-thread hop.</param>
        /// <param name="services">The gate switches, the confirmation gate and the error sink (<see cref="ActionServices.Shared"/> in game).</param>
        /// <param name="audit">Receives one record per finished call (and one more per late handler); <see cref="ActionAudit.Publish"/> in game. Null for none.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public ActionRunner(ActionRegistry registry, IActionDispatcher dispatcher, ActionServices services, Action<ActionAuditRecord>? audit = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _audit = audit;
        }

        /// <summary>The counters so far.</summary>
        public ActionRunnerStats Stats => new ActionRunnerStats(
            Interlocked.Read(ref _requests),
            Interlocked.Read(ref _ok),
            Interlocked.Read(ref _refused),
            Interlocked.Read(ref _failed),
            Interlocked.Read(ref _timedOut),
            Interlocked.Read(ref _late));

        /// <summary>The dispatcher owner a request runs under: <c>mod:&lt;id&gt;</c> for a mod, <see cref="Owner"/> otherwise.</summary>
        /// <param name="request">The request.</param>
        public static string OwnerFor(ActionRequest request) =>
            request != null && request.Caller == ActionCaller.Mod
                ? MainThreadQueue.ModOwnerPrefix + request.CallerId
                : Owner;

        /// <inheritdoc />
        public async Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken)
        {
            if (request is null)
            {
                _services.ErrorSink?.Invoke("the action runner was handed no request");
                return ActionResult.Failed("no request");
            }

            Interlocked.Increment(ref _requests);
            var watch = Stopwatch.StartNew();
            ActionDescriptor? descriptor = null;
            try
            {
                if (!_registry.TryGet(request.Name, out var binding) || binding is null)
                {
                    return Finish(request, null, ActionResult.NotFound(Cut(request.Name)), watch, false, false, false);
                }

                descriptor = binding.Descriptor;

                var validation = ActionArgsValidation.Check(descriptor, request.Args);
                if (!validation.IsValid)
                {
                    return Finish(request, descriptor, validation.ToResult()!, watch, false, false, false);
                }

                var timeout = request.Timeout ?? DefaultTimeout;
                if (timeout <= TimeSpan.Zero)
                {
                    return Finish(request, descriptor, ActionResult.InvalidArgs(descriptor.Name + ": the timeout must be positive"), watch, false, false, false);
                }

                if (timeout > MaxTimeout)
                {
                    timeout = MaxTimeout;
                }

                var decision = ActionGate.Decide(descriptor, request.Caller, _services.GateSettings);
                if (decision.Verdict == ActionGateVerdict.Deny)
                {
                    return Finish(request, descriptor, ActionResult.Denied(decision.Reason), watch, false, false, false);
                }

                var confirmed = false;
                if (decision.Verdict == ActionGateVerdict.Confirm)
                {
                    var answer = await ConfirmAsync(descriptor, request, cancellationToken).ConfigureAwait(false);
                    if (!answer.IsApproved)
                    {
                        return Finish(request, descriptor, ActionResult.Declined(decision.Reason + "; " + answer), watch, false, false, false);
                    }

                    confirmed = true;
                }

                var invocation = new ActionInvocation(descriptor, request, confirmed);
                var flight = new Flight(this, request, descriptor, confirmed, watch);
                var handler = binding.Handler;
                ActionResult result;
                try
                {
                    result = await _dispatcher.InvokeAsync(
                        OwnerFor(request),
                        "action " + descriptor.Name,
                        () => flight.Run(handler, invocation),
                        timeout,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return CallerGone(flight, request, descriptor, confirmed, watch, timeout);
                }
                catch (OperationCanceledException)
                {
                    return CallerGone(flight, request, descriptor, confirmed, watch, null);
                }
                catch (MainThreadRejectedException rejected)
                {
                    var refusal = rejected.Reason == MainThreadRejectReason.ShuttingDown
                        ? ActionResult.NotAvailable("the game is quitting; " + descriptor.Name + " did not run")
                        : ActionResult.Failed("the main-thread dispatcher refused " + descriptor.Name + " (" + rejected.Reason + ")");
                    return Finish(request, descriptor, refusal, watch, confirmed, false, false);
                }

                return Finish(request, descriptor, result ?? ActionResult.Failed(descriptor.Name + ": no result"), watch, confirmed, true, false);
            }
            catch (Exception ex)
            {
                var failed = "the action runner failed on " + request + ": " + ex.GetType().Name + ": " + ex.Message;
                _services.ErrorSink?.Invoke(failed);
                return Finish(request, descriptor, ActionResult.Failed("the action runner failed: " + ex.GetType().Name), watch, false, false, false);
            }
        }

        private async Task<ConfirmationResult> ConfirmAsync(ActionDescriptor descriptor, ActionRequest request, CancellationToken cancellationToken)
        {
            var args = ActionAudit.RedactArgs(descriptor, request.Args);
            var message = request.CallerId + " (" + ActionAudit.Word(request.Caller) + ") asks to run " + descriptor.Name + ": " +
                          descriptor.Description +
                          (args.Length == 0 ? string.Empty : " Arguments: " + args + ".") +
                          (descriptor.IsDestructive ? " This cannot be undone." : string.Empty);
            var question = new ConfirmationRequest(
                descriptor.Name,
                "Allow " + descriptor.Name + "?",
                message,
                request.Caller,
                request.CallerId,
                descriptor.IsDestructive,
                arguments: request.Args);

            try
            {
                var answer = await _services.ConfirmationGate.ConfirmAsync(question, cancellationToken).ConfigureAwait(false);
                return answer ?? ConfirmationResult.Unavailable("the confirmation gave no answer");
            }
            catch (OperationCanceledException)
            {
                return ConfirmationResult.Unavailable("the caller stopped waiting for the answer");
            }
            catch (Exception ex)
            {
                _services.ErrorSink?.Invoke("the confirmation gate failed on " + descriptor.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                return ConfirmationResult.Unavailable("the confirmation failed (" + ex.GetType().Name + ")");
            }
        }

        /// <summary>The caller stopped waiting: <paramref name="timeout"/> passed, or (null) its token fired.</summary>
        private ActionResult CallerGone(Flight flight, ActionRequest request, ActionDescriptor descriptor, bool confirmed, Stopwatch watch, TimeSpan? timeout)
        {
            var state = flight.Abandon();
            if (state.Finished != null)
            {
                // The handler finished between the timer and this catch: its result is real, so the
                // caller gets it after all.
                return Finish(request, descriptor, state.Finished, watch, confirmed, true, false);
            }

            string reason;
            if (timeout.HasValue)
            {
                reason = state.Started
                    ? "the main thread did not finish " + descriptor.Name + " within " + Seconds(timeout.Value) + "; it is still running and its outcome will be audited as late"
                    : "the main thread did not start " + descriptor.Name + " within " + Seconds(timeout.Value) + "; it will not run";
            }
            else
            {
                reason = state.Started
                    ? "the caller stopped waiting while " + descriptor.Name + " was running; its outcome will be audited as late"
                    : "the caller stopped waiting before " + descriptor.Name + " started; it will not run";
            }

            return Finish(request, descriptor, ActionResult.TimedOut(reason), watch, confirmed, false, false);
        }

        private void AuditLate(ActionRequest request, ActionDescriptor descriptor, ActionResult result, Stopwatch watch, bool confirmed)
        {
            Interlocked.Increment(ref _late);
            Audit(request, descriptor, result, watch, confirmed, true, true);
        }

        private ActionResult Finish(
            ActionRequest request,
            ActionDescriptor? descriptor,
            ActionResult result,
            Stopwatch watch,
            bool confirmed,
            bool fromHandler,
            bool late)
        {
            switch (result.Status)
            {
                case ActionStatus.Ok:
                    Interlocked.Increment(ref _ok);
                    break;
                case ActionStatus.NotFound:
                case ActionStatus.InvalidArgs:
                case ActionStatus.Denied:
                case ActionStatus.Declined:
                    Interlocked.Increment(ref _refused);
                    break;
                case ActionStatus.TimedOut:
                    Interlocked.Increment(ref _timedOut);
                    break;
                default:
                    Interlocked.Increment(ref _failed);
                    break;
            }

            Audit(request, descriptor, result, watch, confirmed, fromHandler, late);
            return result;
        }

        private void Audit(ActionRequest request, ActionDescriptor? descriptor, ActionResult result, Stopwatch watch, bool confirmed, bool fromHandler, bool late)
        {
            if (_audit is null)
            {
                return;
            }

            try
            {
                var record = new ActionAuditRecord(
                    descriptor?.Name ?? Cut(request.Name),
                    descriptor?.Tier,
                    request,
                    result.Status,
                    result.Path,
                    watch.Elapsed.TotalMilliseconds,
                    confirmed,
                    late,
                    ActionAudit.RedactArgs(descriptor, request.Args),
                    ActionAudit.RedactDetail(descriptor?.Tier, result.Message, fromHandler));
                _audit(record);
            }
            catch (Exception ex)
            {
                _services.ErrorSink?.Invoke("the ACTION audit sink failed for " + (descriptor?.Name ?? Cut(request.Name)) + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private ActionResult Call(ActionHandler handler, ActionInvocation invocation)
        {
            var descriptor = invocation.Descriptor;
            try
            {
                return handler(invocation) ?? ActionResult.Failed(descriptor.Name + ": the handler returned no result");
            }
            catch (Exception ex)
            {
                // A Write-tier exception message may quote what was being written: type and stack only.
                var write = descriptor.Tier == ActionTier.Write;
                _services.ErrorSink?.Invoke(
                    "the handler of " + descriptor.Name + " threw " + ex.GetType().Name +
                    (write ? " (message withheld: write tier)\n" + ex.StackTrace : ": " + ex.Message + "\n" + ex.StackTrace));
                return ActionResult.Failed(descriptor.Name + " failed: " + ex.GetType().Name + (write ? string.Empty : ": " + ex.Message));
            }
        }

        private static string Seconds(TimeSpan span) =>
            span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

        private static string Cut(string name) =>
            name.Length <= ActionDescriptor.MaxNameLength ? name : name.Substring(0, ActionDescriptor.MaxNameLength) + "…";

        /// <summary>
        /// One handler call on its way through the dispatcher: knows whether it started and finished,
        /// and whether the caller is still waiting, so exactly one side reports a late outcome.
        /// </summary>
        private sealed class Flight
        {
            private readonly object _gate = new object();
            private readonly ActionRunner _runner;
            private readonly ActionRequest _request;
            private readonly ActionDescriptor _descriptor;
            private readonly bool _confirmed;
            private readonly Stopwatch _watch;
            private bool _started;
            private bool _callerGone;
            private ActionResult? _finished;

            internal Flight(ActionRunner runner, ActionRequest request, ActionDescriptor descriptor, bool confirmed, Stopwatch watch)
            {
                _runner = runner;
                _request = request;
                _descriptor = descriptor;
                _confirmed = confirmed;
                _watch = watch;
            }

            internal ActionResult Run(ActionHandler handler, ActionInvocation invocation)
            {
                lock (_gate)
                {
                    _started = true;
                }

                var result = _runner.Call(handler, invocation);
                bool late;
                lock (_gate)
                {
                    _finished = result;
                    late = _callerGone;
                }

                if (late)
                {
                    _runner.AuditLate(_request, _descriptor, result, _watch, _confirmed);
                }

                return result;
            }

            internal (bool Started, ActionResult? Finished) Abandon()
            {
                lock (_gate)
                {
                    _callerGone = true;
                    return (_started, _finished);
                }
            }
        }
    }
}
