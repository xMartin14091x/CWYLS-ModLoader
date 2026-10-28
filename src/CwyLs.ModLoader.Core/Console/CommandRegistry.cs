using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// The commands the in-game console knows. A registry is an ordinary object so it can be
    /// unit-tested in isolation; <see cref="Shared"/> is the one the running game uses, because
    /// the plugins that contribute commands are separate BepInEx assemblies with no reference to
    /// one another (the same reason <c>SmokeRegistry</c> is process-wide).
    /// <para>
    /// Every member takes the same lock, so a mod registering a command from its Awake while the
    /// console renders on the main thread is safe.
    /// </para>
    /// </summary>
    public sealed class CommandRegistry
    {
        /// <summary>Prefix of the line printed for a name nobody registered.</summary>
        public const string UnknownPrefix = "unknown command: ";

        private static readonly CommandRegistry SharedInstance = new CommandRegistry();

        private readonly object _gate = new object();
        private readonly Dictionary<string, ConsoleCommand> _byName =
            new Dictionary<string, ConsoleCommand>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ConsoleCommand> _order = new List<ConsoleCommand>();

        /// <summary>
        /// The process-wide registry the in-game console runs. Mods register here (directly, or
        /// through <c>CwyLs.ModLoader.Console.ConsoleFeature.Register</c>, which also logs).
        /// </summary>
        public static CommandRegistry Shared => SharedInstance;

        /// <summary>
        /// Where a handler's exception is reported besides the returned error line. Set by the
        /// console feature to the BepInEx log; null means the console output is the only record.
        /// </summary>
        public Action<string>? ErrorSink { get; set; }

        /// <summary>How many commands are registered.</summary>
        public int Count
        {
            get { lock (_gate) { return _order.Count; } }
        }

        /// <summary>Every command, in registration order. Never null.</summary>
        public IReadOnlyList<ConsoleCommand> All
        {
            get { lock (_gate) { return _order.ToArray(); } }
        }

        /// <summary>Every command name, sorted - what completion offers.</summary>
        public IReadOnlyList<string> Names
        {
            get
            {
                lock (_gate)
                {
                    var names = new List<string>(_order.Count);
                    foreach (var command in _order)
                    {
                        names.Add(command.Name);
                    }

                    names.Sort(StringComparer.OrdinalIgnoreCase);
                    return names;
                }
            }
        }

        /// <summary>Every group that has at least one command, in first-registration order.</summary>
        public IReadOnlyList<string> Groups
        {
            get
            {
                lock (_gate)
                {
                    var groups = new List<string>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var command in _order)
                    {
                        if (seen.Add(command.Group))
                        {
                            groups.Add(command.Group);
                        }
                    }

                    return groups;
                }
            }
        }

        /// <summary>
        /// Registers a command. Registering an existing name replaces it in place, keeping its
        /// position, so a plugin whose Awake runs twice cannot list the same command twice.
        /// </summary>
        /// <param name="command">The command. Required.</param>
        /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
        public void Register(ConsoleCommand command)
        {
            if (command is null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            lock (_gate)
            {
                if (_byName.TryGetValue(command.Name, out var existing))
                {
                    _order[_order.IndexOf(existing)] = command;
                    _byName[command.Name] = command;
                    return;
                }

                _byName.Add(command.Name, command);
                _order.Add(command);
            }
        }

        /// <summary>Registers a command from its parts. See <see cref="ConsoleCommand"/>.</summary>
        /// <param name="name">What the user types.</param>
        /// <param name="description">One short sentence for <c>help</c>.</param>
        /// <param name="usage">The argument shape, e.g. <c>packs list|rescan</c>.</param>
        /// <param name="group">Heading <c>help</c> lists it under.</param>
        /// <param name="handler">The work; receives the arguments after the name.</param>
        public void Register(
            string name,
            string? description,
            string? usage,
            string? group,
            Func<IReadOnlyList<string>, string?> handler) =>
            Register(new ConsoleCommand(name, description, usage, group, handler));

        /// <summary>Removes a command.</summary>
        /// <param name="name">The registered name; matched case-insensitively.</param>
        /// <returns>True when a command was removed.</returns>
        public bool Unregister(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (_gate)
            {
                if (!_byName.TryGetValue(name!, out var existing))
                {
                    return false;
                }

                _byName.Remove(existing.Name);
                _order.Remove(existing);
                return true;
            }
        }

        /// <summary>Looks a command up by name, case-insensitively.</summary>
        /// <param name="name">The name to find.</param>
        /// <param name="command">The command, or null.</param>
        /// <returns>True when it is registered.</returns>
        public bool TryGet(string? name, out ConsoleCommand? command)
        {
            command = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (_gate)
            {
                return _byName.TryGetValue(name!, out command);
            }
        }

        /// <summary>Removes every command. Used by tests; harmless at runtime.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _byName.Clear();
                _order.Clear();
            }
        }

        /// <summary>
        /// Parses a line and runs it. Never throws: a blank line, an unknown name and a handler
        /// that throws all come back as text to print. A handler's exception is also sent to
        /// <see cref="ErrorSink"/>, so a failure survives in the log after the console is closed.
        /// </summary>
        /// <param name="line">The line as typed.</param>
        /// <returns>What to print; empty when there is nothing to say.</returns>
        public string Execute(string? line)
        {
            var parsed = CommandLineParser.Parse(line);
            return parsed is null ? string.Empty : Execute(parsed.Name, parsed.Arguments);
        }

        /// <summary>
        /// Runs an already-split command. Never throws; see <see cref="Execute(string)"/>.
        /// </summary>
        /// <param name="name">The command name.</param>
        /// <param name="arguments">The arguments after it; null is treated as none.</param>
        /// <returns>What to print.</returns>
        public string Execute(string? name, IReadOnlyList<string>? arguments)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            if (!TryGet(name, out var command) || command is null)
            {
                return UnknownPrefix + name + " (try 'help')";
            }

            try
            {
                return command.Handler(arguments ?? Array.Empty<string>()) ?? string.Empty;
            }
            catch (Exception ex)
            {
                // A console command that throws must not take the frame down, and must not
                // disappear either: the text goes to the console, the same text to the log.
                var message = command.Name + " failed: " + ex.GetType().Name + ": " + ex.Message;
                Report(message);
                return message;
            }
        }

        /// <summary>
        /// The <c>help</c> text: every command grouped by <see cref="ConsoleCommand.Group"/>, or
        /// the full entry for one command.
        /// </summary>
        /// <param name="name">
        /// A command name for its own entry; null or blank for the whole list.
        /// </param>
        public string Help(string? name = null)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (!TryGet(name, out var one) || one is null)
                {
                    return UnknownPrefix + name;
                }

                var entry = new StringBuilder(one.Usage);
                if (one.Description.Length > 0)
                {
                    entry.Append('\n').Append("  ").Append(one.Description);
                }

                return entry.Append('\n').Append("  group: ").Append(one.Group).ToString();
            }

            var all = All;
            if (all.Count == 0)
            {
                return "no commands are registered";
            }

            var sb = new StringBuilder();
            sb.Append(all.Count).Append(" command(s). Tab completes, Up/Down repeats.");

            foreach (var group in Groups)
            {
                sb.Append('\n').Append('[').Append(group).Append(']');
                foreach (var command in all)
                {
                    if (string.Equals(command.Group, group, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append('\n').Append("  ").Append(command.Describe());
                    }
                }
            }

            return sb.ToString();
        }

        private void Report(string message)
        {
            var sink = ErrorSink;
            if (sink is null)
            {
                return;
            }

            try
            {
                sink(message);
            }
            catch (Exception ex)
            {
                // The sink is the last resort; there is nowhere left to report to, and throwing
                // here would turn a failed command into a failed frame.
                System.Diagnostics.Debug.WriteLine("console error sink threw: " + ex.Message);
            }
        }
    }
}
