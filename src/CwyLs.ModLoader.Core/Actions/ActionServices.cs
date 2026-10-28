using System;
using System.Threading;
using System.Threading.Tasks;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// Runs actions end to end: look up, validate the arguments, gate (<see cref="ActionGate"/>),
    /// confirm when asked, hop to the main thread with the caller's timeout, run the handler,
    /// write the <c>ACTION</c> audit line. The loader's actions feature installs the real one with
    /// <see cref="ActionServices.UseRunner"/>; until then <see cref="UnavailableActionRunner"/> answers.
    /// </summary>
    public interface IActionRunner
    {
        /// <summary>Runs one request. Never faults: every failure is an <see cref="ActionResult"/>.</summary>
        /// <param name="request">What to run and who asks.</param>
        /// <param name="cancellationToken">Stops waiting; work not started by then is skipped, never run late.</param>
        /// <returns>The outcome.</returns>
        Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken);
    }

    /// <summary>The runner before one is installed: every request ends at once as <see cref="ActionStatus.NotAvailable"/>.</summary>
    public sealed class UnavailableActionRunner : IActionRunner
    {
        /// <summary>The message every answer carries.</summary>
        public const string Reason = "actions are not available: no action runner is installed";

        private UnavailableActionRunner()
        {
        }

        /// <summary>The one instance.</summary>
        public static UnavailableActionRunner Instance { get; } = new UnavailableActionRunner();

        /// <inheritdoc />
        public Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.NotAvailable(Reason));
    }

    /// <summary>
    /// Where the action runner, the confirmation gate and the two gate switches are installed. An
    /// ordinary object so it can be tested; <see cref="Shared"/> is the one the game uses. Thread-safe.
    /// </summary>
    /// <remarks>
    /// The switches live with the plugin that owns their config entry, which hands this object a
    /// reader once: the data plugin for <c>[Data] AllowWrites</c> (<see cref="UseWritesSwitch"/>),
    /// the local API for <c>[Api] AllowActions</c> (<see cref="UseApiActionsSwitch"/>). A switch
    /// nobody installed, or whose reader throws, reads as off, so the default is always the safe one.
    /// </remarks>
    public sealed class ActionServices
    {
        private static readonly ActionServices SharedInstance = new ActionServices();

        private IActionRunner _runner = UnavailableActionRunner.Instance;
        private IConfirmationGate _gate = DenyingConfirmationGate.Instance;
        private Func<bool>? _allowWrites;
        private Func<bool>? _allowApiActions;

        /// <summary>The services the running game uses.</summary>
        public static ActionServices Shared => SharedInstance;

        /// <summary>
        /// Where a runner failure caught by <see cref="RunAsync"/> is reported (the framework sets the
        /// BepInEx log). Null means the failure is only in the returned result.
        /// </summary>
        public Action<string>? ErrorSink { get; set; }

        /// <summary>The installed runner; <see cref="UnavailableActionRunner"/> until one is installed. Never null.</summary>
        public IActionRunner Runner => Volatile.Read(ref _runner);

        /// <summary>True once a real runner is installed.</summary>
        public bool HasRunner => !(Runner is UnavailableActionRunner);

        /// <summary>The installed confirmation gate; <see cref="DenyingConfirmationGate"/> until one is installed. Never null.</summary>
        public IConfirmationGate ConfirmationGate => Volatile.Read(ref _gate);

        /// <summary>True once a real confirmation dialog is installed.</summary>
        public bool HasConfirmationGate => !(ConfirmationGate is DenyingConfirmationGate);

        /// <summary>Installs the runner; a later call replaces it.</summary>
        /// <param name="runner">The runner.</param>
        /// <exception cref="ArgumentNullException"><paramref name="runner"/> is null.</exception>
        public void UseRunner(IActionRunner runner) =>
            Volatile.Write(ref _runner, runner ?? throw new ArgumentNullException(nameof(runner)));

        /// <summary>Installs the confirmation gate; a later call replaces it.</summary>
        /// <param name="gate">The gate.</param>
        /// <exception cref="ArgumentNullException"><paramref name="gate"/> is null.</exception>
        public void UseConfirmationGate(IConfirmationGate gate) =>
            Volatile.Write(ref _gate, gate ?? throw new ArgumentNullException(nameof(gate)));

        /// <summary>True once a reader for <c>[Data] AllowWrites</c> is installed.</summary>
        public bool HasWritesSwitch => Volatile.Read(ref _allowWrites) != null;

        /// <summary>True once a reader for <c>[Api] AllowActions</c> is installed.</summary>
        public bool HasApiActionsSwitch => Volatile.Read(ref _allowApiActions) != null;

        /// <summary>
        /// Installs the reader of <c>[Data] AllowWrites</c> (<see cref="ActionGateSettings.AllowWritesKey"/>),
        /// called by the plugin that binds that entry. Read on every action call, so a changed value
        /// applies at once. A later call replaces it.
        /// </summary>
        /// <param name="allowWrites">Returns the switch's current value; must be cheap and thread-safe.</param>
        /// <exception cref="ArgumentNullException"><paramref name="allowWrites"/> is null.</exception>
        public void UseWritesSwitch(Func<bool> allowWrites) =>
            Volatile.Write(ref _allowWrites, allowWrites ?? throw new ArgumentNullException(nameof(allowWrites)));

        /// <summary>
        /// Installs the reader of <c>[Api] AllowActions</c> (<see cref="ActionGateSettings.AllowApiActionsKey"/>),
        /// called by the local API plugin. Read on every action call. A later call replaces it.
        /// </summary>
        /// <param name="allowApiActions">Returns the switch's current value; must be cheap and thread-safe.</param>
        /// <exception cref="ArgumentNullException"><paramref name="allowApiActions"/> is null.</exception>
        public void UseApiActionsSwitch(Func<bool> allowApiActions) =>
            Volatile.Write(ref _allowApiActions, allowApiActions ?? throw new ArgumentNullException(nameof(allowApiActions)));

        /// <summary>
        /// The switches as they are now, for <see cref="ActionGate.Decide"/>. A switch with no reader,
        /// or whose reader throws (reported to <see cref="ErrorSink"/>), is off. A fresh object per read.
        /// </summary>
        public ActionGateSettings GateSettings => new ActionGateSettings
        {
            AllowWrites = Read(Volatile.Read(ref _allowWrites), ActionGateSettings.AllowWritesKey),
            AllowApiActions = Read(Volatile.Read(ref _allowApiActions), ActionGateSettings.AllowApiActionsKey),
        };

        /// <summary>
        /// Runs a request through the installed runner. Never throws and never faults: a runner that
        /// throws or returns nothing becomes <see cref="ActionStatus.Failed"/>.
        /// </summary>
        /// <param name="request">What to run.</param>
        /// <param name="cancellationToken">Stops waiting.</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public async Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            try
            {
                var result = await Runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
                return result ?? ActionResult.Failed("the action runner returned no result for " + request.Name);
            }
            catch (OperationCanceledException)
            {
                var stopped = "the caller stopped waiting for " + request;
                ErrorSink?.Invoke(stopped);
                return ActionResult.TimedOut(stopped);
            }
            catch (Exception ex)
            {
                var failed = "the action runner failed on " + request + ": " + ex.GetType().Name + ": " + ex.Message;
                ErrorSink?.Invoke(failed);
                return ActionResult.Failed(failed);
            }
        }

        private bool Read(Func<bool>? reader, string key)
        {
            if (reader is null)
            {
                return false;
            }

            try
            {
                return reader();
            }
            catch (Exception ex)
            {
                ErrorSink?.Invoke("the reader of " + key + " threw " + ex.GetType().Name + ": " + ex.Message + "; treated as false");
                return false;
            }
        }
    }
}
