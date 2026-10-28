using System;
using System.Collections.Generic;
using System.Threading;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>One registered action: its owner, description and handler.</summary>
    public sealed class ActionBinding
    {
        internal ActionBinding(string owner, ActionDescriptor descriptor, ActionHandler handler)
        {
            Owner = owner;
            Descriptor = descriptor;
            Handler = handler;
        }

        /// <summary><see cref="ActionRegistry.FrameworkOwner"/> or the registering mod's id.</summary>
        public string Owner { get; }

        /// <summary>What the action is.</summary>
        public ActionDescriptor Descriptor { get; }

        /// <summary>What runs it (on the main thread, through the runner).</summary>
        public ActionHandler Handler { get; }
    }

    /// <summary>
    /// Every action the game can be asked to do: the framework's families (music, pomodoro,
    /// panels, room, todos, ...) and mods' own. The console, the local API and scripts all list
    /// and run from here, through the runner in <see cref="ActionServices"/>. An ordinary object
    /// so it can be tested; <see cref="Shared"/> is the one the game uses. Thread-safe.
    /// </summary>
    /// <remarks>
    /// Names are never replaced: a second registration of a name (case-insensitive) is refused
    /// until the first is disposed. The framework (<see cref="FrameworkOwner"/>) registers any
    /// valid name; a mod registers only names that start with <c>&lt;its id&gt;.</c>.
    /// </remarks>
    public sealed class ActionRegistry
    {
        /// <summary>The owner name of the framework's own actions.</summary>
        public const string FrameworkOwner = "cwy";

        private static readonly ActionRegistry SharedInstance = new ActionRegistry();

        private readonly object _gate = new object();
        private readonly Dictionary<string, ActionBinding> _byName =
            new Dictionary<string, ActionBinding>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The registry the running game uses.</summary>
        public static ActionRegistry Shared => SharedInstance;

        /// <summary>How many actions are registered.</summary>
        public int Count
        {
            get { lock (_gate) { return _byName.Count; } }
        }

        /// <summary>Every registered action, sorted by name (the deterministic order the API and MCP need).</summary>
        public IReadOnlyList<ActionBinding> All
        {
            get
            {
                List<ActionBinding> list;
                lock (_gate)
                {
                    list = new List<ActionBinding>(_byName.Values);
                }

                list.Sort((a, b) => string.CompareOrdinal(a.Descriptor.Name, b.Descriptor.Name));
                return list;
            }
        }

        /// <summary>Every registered action's description, sorted by name.</summary>
        public IReadOnlyList<ActionDescriptor> Descriptors
        {
            get
            {
                var all = All;
                var list = new List<ActionDescriptor>(all.Count);
                foreach (var binding in all)
                {
                    list.Add(binding.Descriptor);
                }

                return list;
            }
        }

        /// <summary>The action with this name (case-insensitive).</summary>
        /// <param name="name">An action name.</param>
        /// <param name="binding">The registration, or null.</param>
        public bool TryGet(string? name, out ActionBinding? binding)
        {
            binding = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (_gate)
            {
                return _byName.TryGetValue(name!.Trim(), out binding);
            }
        }

        /// <summary>Registers an action. Refuses, never replaces.</summary>
        /// <param name="owner"><see cref="FrameworkOwner"/> or a mod id.</param>
        /// <param name="descriptor">What the action is.</param>
        /// <param name="handler">What runs it.</param>
        /// <param name="registration">Dispose to remove the action; null on refusal.</param>
        /// <param name="problem">Why it was refused, or empty.</param>
        /// <returns>True when registered.</returns>
        public bool TryRegister(
            string owner,
            ActionDescriptor descriptor,
            ActionHandler handler,
            out IDisposable? registration,
            out string problem)
        {
            registration = null;
            if (descriptor is null || handler is null)
            {
                problem = "an action needs its description and a handler";
                return false;
            }

            var framework = string.Equals(owner, FrameworkOwner, StringComparison.OrdinalIgnoreCase);
            if (!framework && !PackManifest.IsValidId(owner))
            {
                problem = "\"" + owner + "\" is not an owner: use \"" + FrameworkOwner + "\" or a mod id";
                return false;
            }

            if (!framework && !descriptor.Name.StartsWith(owner + ".", StringComparison.OrdinalIgnoreCase))
            {
                problem = "a mod's action is named \"" + owner + ".<verb>\", not \"" + descriptor.Name + "\"";
                return false;
            }

            var binding = new ActionBinding(framework ? FrameworkOwner : owner, descriptor, handler);
            lock (_gate)
            {
                if (_byName.TryGetValue(descriptor.Name, out var existing))
                {
                    problem = "the action \"" + descriptor.Name + "\" is already registered by " + existing.Owner;
                    return false;
                }

                _byName.Add(descriptor.Name, binding);
            }

            problem = string.Empty;
            registration = new Registration(this, binding);
            return true;
        }

        /// <summary>Registers an action, throwing on refusal.</summary>
        /// <param name="owner"><see cref="FrameworkOwner"/> or a mod id.</param>
        /// <param name="descriptor">What the action is.</param>
        /// <param name="handler">What runs it.</param>
        /// <returns>Dispose to remove the action.</returns>
        /// <exception cref="InvalidOperationException">The registration was refused; the message says why.</exception>
        public IDisposable Register(string owner, ActionDescriptor descriptor, ActionHandler handler)
        {
            if (TryRegister(owner, descriptor, handler, out var registration, out var problem))
            {
                return registration!;
            }

            throw new InvalidOperationException(problem);
        }

        private void Remove(ActionBinding binding)
        {
            lock (_gate)
            {
                if (_byName.TryGetValue(binding.Descriptor.Name, out var current) && ReferenceEquals(current, binding))
                {
                    _byName.Remove(binding.Descriptor.Name);
                }
            }
        }

        private sealed class Registration : IDisposable
        {
            private ActionRegistry? _registry;
            private readonly ActionBinding _binding;

            internal Registration(ActionRegistry registry, ActionBinding binding)
            {
                _registry = registry;
                _binding = binding;
            }

            public void Dispose() => Interlocked.Exchange(ref _registry, null)?.Remove(_binding);
        }
    }
}
