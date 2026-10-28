using System;
using System.Collections.Generic;
using System.Threading;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>One registered script command: its number, owner, description and handler.</summary>
    public sealed class ScenarioCommandBinding
    {
        internal ScenarioCommandBinding(int id, string owner, ScenarioCommandDescriptor descriptor, ScenarioCommandHandler handler)
        {
            Id = id;
            Owner = owner;
            Descriptor = descriptor;
            Handler = handler;
        }

        /// <summary>The command number, 5000-5999.</summary>
        public int Id { get; }

        /// <summary><see cref="ScenarioCommandRegistry.FrameworkOwner"/> or the registering mod's id.</summary>
        public string Owner { get; }

        /// <summary>What the command is.</summary>
        public ScenarioCommandDescriptor Descriptor { get; }

        /// <summary>What runs a row of it.</summary>
        public ScenarioCommandHandler Handler { get; }
    }

    /// <summary>
    /// Script commands by name and number: pack rows name a
    /// command (<c>"Command": "cwy.do"</c>), the pack parser turns the name into a number in
    /// 5000-5999 (<see cref="TryResolve(string, out int)"/>), and the script-command runtime looks the number
    /// up when the game's reader reaches the row. An ordinary object so it can be tested;
    /// <see cref="Shared"/> is the one the game uses. Thread-safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Numbers.</b> The framework's commands have fixed numbers (<see cref="ScenarioCommandIds.BuiltIns"/>,
    /// 5000-5099). Any other valid name gets the next free number from 5100 on the first time it is
    /// resolved or registered, and keeps it for the session even if its handler goes away, so rows
    /// parsed before a mod loaded still point at the right command. A number outside 5000-5999 is
    /// never handed out.
    /// </para>
    /// <para>
    /// <b>Owners.</b> The framework (<see cref="FrameworkOwner"/>) registers <c>cwy.*</c> names; a mod
    /// registers only names that start with <c>&lt;its id&gt;.</c> and never a built-in name. A number
    /// has at most one handler: a second registration is refused until the first is disposed.
    /// </para>
    /// </remarks>
    public sealed class ScenarioCommandRegistry
    {
        /// <summary>The owner name of the framework's own commands.</summary>
        public const string FrameworkOwner = "cwy";

        /// <summary>The prefix every framework command name has.</summary>
        public const string FrameworkPrefix = "cwy.";

        /// <summary>The detail reported before the runtime says anything.</summary>
        public const string NoRuntimeDetail = "the scenario-command seam is not installed (rows 5000-5999 would stall the game's reader)";

        private static readonly ScenarioCommandRegistry SharedInstance = new ScenarioCommandRegistry();

        private readonly object _gate = new object();
        private readonly Dictionary<string, int> _idByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> _nameById = new Dictionary<int, string>();
        private readonly Dictionary<int, ScenarioCommandBinding> _bindings = new Dictionary<int, ScenarioCommandBinding>();
        private int _nextDynamic = ScenarioCommandIds.DynamicFirst;
        private bool _runtimeAvailable;
        private string _runtimeDetail = NoRuntimeDetail;

        /// <summary>Creates a registry that knows only the framework's fixed numbers.</summary>
        public ScenarioCommandRegistry()
        {
            foreach (var (name, id) in ScenarioCommandIds.BuiltIns)
            {
                _idByName.Add(name, id);
                _nameById.Add(id, name);
            }
        }

        /// <summary>The registry the running game uses.</summary>
        public static ScenarioCommandRegistry Shared => SharedInstance;

        /// <summary>True once the script-command runtime is installed and rows 5000-5999 run their handlers.</summary>
        public bool RuntimeAvailable
        {
            get { lock (_gate) { return _runtimeAvailable; } }
        }

        /// <summary>What the runtime last said about itself (the seam, or why it is missing).</summary>
        public string RuntimeDetail
        {
            get { lock (_gate) { return _runtimeDetail; } }
        }

        /// <summary>How many numbers from 5100 on have been handed out.</summary>
        public int DynamicCount
        {
            get { lock (_gate) { return _nextDynamic - ScenarioCommandIds.DynamicFirst; } }
        }

        /// <summary>Every command with a handler, by number.</summary>
        public IReadOnlyList<ScenarioCommandBinding> Bindings
        {
            get
            {
                List<ScenarioCommandBinding> list;
                lock (_gate)
                {
                    list = new List<ScenarioCommandBinding>(_bindings.Values);
                }

                list.Sort((a, b) => a.Id.CompareTo(b.Id));
                return list;
            }
        }

        /// <summary>Called by the script-command runtime when its seam is (or is no longer) installed.</summary>
        /// <param name="available">True when rows 5000-5999 run their handlers.</param>
        /// <param name="detail">The seam, or why it is missing.</param>
        public void SetRuntime(bool available, string detail)
        {
            lock (_gate)
            {
                _runtimeAvailable = available;
                _runtimeDetail = string.IsNullOrWhiteSpace(detail) ? (available ? "installed" : NoRuntimeDetail) : detail;
            }
        }

        /// <summary>The number of a command name, handing out the next free one for a name seen first.</summary>
        /// <param name="name">A command name (<see cref="ScenarioCommandDescriptor.NamePattern"/>).</param>
        /// <param name="id">The number, or 0.</param>
        /// <returns>False for an unusable name, or when 5100-5999 are all in use.</returns>
        public bool TryResolve(string? name, out int id) => TryResolve(name, out id, out _);

        /// <summary>As <see cref="TryResolve(string, out int)"/>, saying why a name has no number.</summary>
        /// <param name="name">A command name.</param>
        /// <param name="id">The number, or 0.</param>
        /// <param name="problem">Why not, or empty.</param>
        public bool TryResolve(string? name, out int id, out string problem)
        {
            id = 0;
            var text = name?.Trim().ToLowerInvariant();
            if (!ScenarioCommandDescriptor.IsValidName(text))
            {
                problem = "\"" + name + "\" is not a script command name (two or more lower-case dot-separated segments)";
                return false;
            }

            lock (_gate)
            {
                if (_idByName.TryGetValue(text!, out id))
                {
                    problem = string.Empty;
                    return true;
                }

                if (_nextDynamic > ScenarioCommandIds.Last)
                {
                    problem = "every script command number from " + ScenarioCommandIds.DynamicFirst + " to " +
                              ScenarioCommandIds.Last + " is in use; \"" + text + "\" gets none";
                    return false;
                }

                id = _nextDynamic++;
                _idByName.Add(text!, id);
                _nameById.Add(id, text!);
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>The name a number was handed out to (or a built-in's name).</summary>
        /// <param name="id">A command number.</param>
        /// <param name="name">The name, or null.</param>
        public bool TryGetName(int id, out string? name)
        {
            name = null;
            if (!ScenarioCommandIds.IsInRange(id))
            {
                return false;
            }

            lock (_gate)
            {
                return _nameById.TryGetValue(id, out name);
            }
        }

        /// <summary>True when the number is a built-in or was handed out to a name.</summary>
        /// <param name="id">A command number.</param>
        public bool IsKnownId(int id) => TryGetName(id, out _);

        /// <summary>Registers a handler for a command. Refuses, never replaces.</summary>
        /// <param name="owner"><see cref="FrameworkOwner"/> or a mod id.</param>
        /// <param name="descriptor">What the command is.</param>
        /// <param name="handler">What runs a row of it.</param>
        /// <param name="registration">Dispose to remove the handler (the number stays reserved); null on refusal.</param>
        /// <param name="problem">Why it was refused, or empty.</param>
        /// <returns>True when registered.</returns>
        public bool TryRegister(
            string owner,
            ScenarioCommandDescriptor descriptor,
            ScenarioCommandHandler handler,
            out IDisposable? registration,
            out string problem)
        {
            registration = null;
            if (descriptor is null || handler is null)
            {
                problem = "a script command needs its description and a handler";
                return false;
            }

            var name = descriptor.Name;
            var framework = string.Equals(owner, FrameworkOwner, StringComparison.OrdinalIgnoreCase);
            if (framework)
            {
                if (!name.StartsWith(FrameworkPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    problem = "a framework script command is named \"" + FrameworkPrefix + "<name>\", not \"" + name + "\"";
                    return false;
                }
            }
            else
            {
                if (!PackManifest.IsValidId(owner))
                {
                    problem = "\"" + owner + "\" is not an owner: use \"" + FrameworkOwner + "\" or a mod id";
                    return false;
                }

                if (!name.StartsWith(owner + ".", StringComparison.OrdinalIgnoreCase))
                {
                    problem = "a mod's script command is named \"" + owner + ".<name>\", not \"" + name + "\"";
                    return false;
                }

                if (ScenarioCommandIds.TryGetBuiltIn(name, out _))
                {
                    problem = "\"" + name + "\" is a framework command";
                    return false;
                }
            }

            if (!TryResolve(name, out var id, out problem))
            {
                return false;
            }

            var binding = new ScenarioCommandBinding(id, framework ? FrameworkOwner : owner, descriptor, handler);
            lock (_gate)
            {
                if (_bindings.TryGetValue(id, out var existing))
                {
                    problem = "the script command \"" + name + "\" (" + id + ") already has a handler from " + existing.Owner;
                    return false;
                }

                _bindings.Add(id, binding);
            }

            problem = string.Empty;
            registration = new Registration(this, binding);
            return true;
        }

        /// <summary>Registers a handler, throwing on refusal.</summary>
        /// <param name="owner"><see cref="FrameworkOwner"/> or a mod id.</param>
        /// <param name="descriptor">What the command is.</param>
        /// <param name="handler">What runs a row of it.</param>
        /// <returns>Dispose to remove the handler.</returns>
        /// <exception cref="InvalidOperationException">The registration was refused; the message says why.</exception>
        public IDisposable Register(string owner, ScenarioCommandDescriptor descriptor, ScenarioCommandHandler handler)
        {
            if (TryRegister(owner, descriptor, handler, out var registration, out var problem))
            {
                return registration!;
            }

            throw new InvalidOperationException(problem);
        }

        /// <summary>The handler of a command number.</summary>
        /// <param name="id">A command number.</param>
        /// <param name="binding">The registration, or null (an unknown number, or one nobody handles).</param>
        public bool TryGetBinding(int id, out ScenarioCommandBinding? binding)
        {
            lock (_gate)
            {
                return _bindings.TryGetValue(id, out binding);
            }
        }

        /// <summary>The handler of a command name (case-insensitive).</summary>
        /// <param name="name">A command name.</param>
        /// <param name="binding">The registration, or null.</param>
        public bool TryGetBinding(string? name, out ScenarioCommandBinding? binding)
        {
            binding = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (_gate)
            {
                return _idByName.TryGetValue(name!.Trim(), out var id) && _bindings.TryGetValue(id, out binding);
            }
        }

        private void Remove(ScenarioCommandBinding binding)
        {
            lock (_gate)
            {
                if (_bindings.TryGetValue(binding.Id, out var current) && ReferenceEquals(current, binding))
                {
                    _bindings.Remove(binding.Id);
                }
            }
        }

        private sealed class Registration : IDisposable
        {
            private ScenarioCommandRegistry? _registry;
            private readonly ScenarioCommandBinding _binding;

            internal Registration(ScenarioCommandRegistry registry, ScenarioCommandBinding binding)
            {
                _registry = registry;
                _binding = binding;
            }

            public void Dispose() => Interlocked.Exchange(ref _registry, null)?.Remove(_binding);
        }
    }
}
