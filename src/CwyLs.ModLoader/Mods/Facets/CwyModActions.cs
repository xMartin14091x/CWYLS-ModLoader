using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Mods;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Actions: the semantic game actions (music, pomodoro, panels, room, todos ...) that the console,
    /// scripts and the local API also use, and a mod's own actions for them. Listing and registering
    /// always work; running needs the action runner, which the framework installs while
    /// <c>[Actions] Enabled</c> is true - otherwise <see cref="RunAsync"/> answers
    /// <see cref="ActionStatus.NotAvailable"/>. Every run goes through the same schema check, tier gate
    /// (<see cref="ActionGate"/>), confirmation and <c>ACTION</c> audit line as the API, with this mod as
    /// a <see cref="ActionCaller.Mod"/> caller; the handler runs in this mod's dispatcher lane.
    /// For an LLM tool list, <c>ActionToolSchema.BuildAll(helper.Actions.List())</c> gives each
    /// action's name, description, tier hints and argument schema. Only the framework implements
    /// this interface.
    /// </summary>
    public interface ICwyModActions : ICwyModFacet
    {
        /// <summary>Every registered action, sorted by name.</summary>
        IReadOnlyList<ActionDescriptor> List();

        /// <summary>
        /// Publishes an action of this mod's own to the console (<c>act run</c>), scripts
        /// (<c>cwy.do</c>) and the local API. The name must start with <c>&lt;modId&gt;.</c>. The handler
        /// runs on the main thread with this mod's attribution; if it throws, the call ends as
        /// <see cref="ActionStatus.Failed"/>.
        /// </summary>
        /// <param name="descriptor">What the action is.</param>
        /// <param name="handler">What runs it.</param>
        /// <returns>The handle; refused (with the reason) when the name is unusable or taken.</returns>
        CwyRegistration Register(ActionDescriptor descriptor, ActionHandler handler);

        /// <summary>
        /// Runs an action as this mod. Never faults: every outcome, including "not available", is an
        /// <see cref="ActionResult"/>. The main-thread hop waits at most 2 s. Await it or continue from
        /// it; never wait for it synchronously on the main thread (the hop is queued for a later
        /// frame, so the wait would end as TimedOut).
        /// </summary>
        /// <param name="name">The action name, e.g. <c>music.play</c>.</param>
        /// <param name="args">Its arguments; null for none.</param>
        /// <param name="cancellationToken">Stops waiting.</param>
        Task<ActionResult> RunAsync(string name, ActionArgs? args = null, CancellationToken cancellationToken = default);
    }

    /// <summary>The per-mod Actions facet, over <see cref="ActionRegistry.Shared"/> and <see cref="ActionServices.Shared"/>.</summary>
    internal sealed class ModActions : ICwyModActions
    {
        private readonly ModHelper _helper;

        internal ModActions(ModHelper helper) => _helper = helper;

        public bool IsAvailable => ActionServices.Shared.HasRunner;

        public string Status
        {
            get
            {
                var services = ActionServices.Shared;
                if (!services.HasRunner)
                {
                    return UnavailableActionRunner.Reason + "; " + ActionRegistry.Shared.Count + " registered";
                }

                var settings = services.GateSettings;
                return "actions run by " + services.Runner.GetType().Name + "; " + ActionRegistry.Shared.Count + " registered; writes " +
                       (settings.AllowWrites ? "on" : "off") + "; confirmation " +
                       (services.HasConfirmationGate ? "in game" : "none (deletes are declined)");
            }
        }

        public IReadOnlyList<ActionDescriptor> List() => ActionRegistry.Shared.Descriptors;

        public CwyRegistration Register(ActionDescriptor descriptor, ActionHandler handler)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var name = descriptor.Name;
            if (!_helper.IsActive)
            {
                return CwyRegistration.Refused(name, "the mod " + _helper.ModId + " is not running");
            }

            if (string.Equals(_helper.ModId, ActionRegistry.FrameworkOwner, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(name, "a mod cannot use the framework's owner name \"" + ActionRegistry.FrameworkOwner + "\"");
            }

            ActionHandler wrapped = invocation => Run(name, handler, invocation);
            if (!ActionRegistry.Shared.TryRegister(_helper.ModId, descriptor, wrapped, out var registration, out var problem))
            {
                return Refuse(name, problem);
            }

            return _helper.Track(name, registration!);
        }

        public Task<ActionResult> RunAsync(string name, ActionArgs? args = null, CancellationToken cancellationToken = default)
        {
            if (!_helper.IsActive)
            {
                return Task.FromResult(ActionResult.Denied("the mod " + _helper.ModId + " is not running"));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return Task.FromResult(ActionResult.InvalidArgs("an action name is required"));
            }

            if (!ActionServices.Shared.HasRunner)
            {
                _helper.WarnOnce("actions", UnavailableActionRunner.Reason + "; RunAsync answers NotAvailable");
            }

            var request = new ActionRequest(name, args, ActionCaller.Mod, _helper.ModId);
            return ActionServices.Shared.RunAsync(request, cancellationToken);
        }

        private ActionResult Run(string name, ActionHandler handler, ActionInvocation invocation)
        {
            if (!_helper.IsActive)
            {
                return ActionResult.Failed("the mod " + _helper.ModId + " is not running");
            }

            ActionResult? result = null;
            if (!_helper.Guard(ModStage.Action, "action " + name, () => result = handler(invocation)))
            {
                return ActionResult.Failed(name + " threw in " + _helper.ModId + "; the MODS line has the error");
            }

            return result ?? ActionResult.Failed(name + ": the handler returned no result");
        }

        private CwyRegistration Refuse(string name, string problem)
        {
            _helper.WarnOnce("action:" + name, "action '" + name + "' refused: " + problem);
            return CwyRegistration.Refused(name, problem);
        }
    }
}
