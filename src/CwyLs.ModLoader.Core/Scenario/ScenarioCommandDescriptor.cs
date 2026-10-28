using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>
    /// What one of a script command's four arguments means (the game's row has
    /// <c>Arg1</c>, <c>Arg2</c> (a float), <c>Arg3</c>, <c>Arg4</c>). For lint and the author's guide. Immutable.
    /// </summary>
    public sealed class ScenarioCommandArg
    {
        /// <summary>Creates an argument description.</summary>
        /// <param name="slot">1 to 4: which <c>ArgN</c> column. Slot 2 is a number in the game's data.</param>
        /// <param name="name">A short name, e.g. <c>action</c>.</param>
        /// <param name="description">One sentence.</param>
        /// <param name="required">True when a row without it is a lint error.</param>
        /// <exception cref="ArgumentException">The slot is not 1-4, or a text is blank.</exception>
        public ScenarioCommandArg(int slot, string name, string description, bool required = false)
        {
            if (slot < 1 || slot > 4)
            {
                throw new ArgumentException("a script row has Arg1 to Arg4; slot " + slot + " does not exist", nameof(slot));
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
            {
                throw new ArgumentException("an argument needs a name and a description");
            }

            Slot = slot;
            Name = name.Trim();
            Description = description.Trim();
            Required = required;
        }

        /// <summary>Which <c>ArgN</c> column, 1 to 4.</summary>
        public int Slot { get; }

        /// <summary>A short name.</summary>
        public string Name { get; }

        /// <summary>One sentence.</summary>
        public string Description { get; }

        /// <summary>True when a row without it is a lint error.</summary>
        public bool Required { get; }

        /// <summary><c>Arg1 action (required)</c>.</summary>
        public override string ToString() => "Arg" + Slot + " " + Name + (Required ? " (required)" : string.Empty);
    }

    /// <summary>What a script command is: its name, what it does and what its arguments mean. Immutable.</summary>
    public sealed class ScenarioCommandDescriptor
    {
        /// <summary>
        /// The pattern of a command name: two or more dot-separated lower-case segments. The
        /// framework's are <c>cwy.*</c>; a mod's start with its id (<c>my.mod.wave</c>). Game command
        /// names (<c>VoiceText</c>) never contain a dot, so the two cannot collide.
        /// </summary>
        public const string NamePattern = "^[a-z0-9][a-z0-9_-]*(\\.[a-z0-9][a-z0-9_-]*)+$";

        /// <summary>Longest accepted name.</summary>
        public const int MaxNameLength = 96;

        private static readonly Regex NameRegex =
            new Regex(@"\A[a-z0-9][a-z0-9_\-]*(\.[a-z0-9][a-z0-9_\-]*)+\z", RegexOptions.CultureInvariant);

        /// <summary>Creates a descriptor.</summary>
        /// <param name="name">The command name (<see cref="NamePattern"/>).</param>
        /// <param name="description">One sentence.</param>
        /// <param name="args">What its arguments mean; null for none. Slots must be distinct.</param>
        /// <exception cref="ArgumentException">The name or description is unusable, or a slot repeats.</exception>
        public ScenarioCommandDescriptor(string name, string description, IEnumerable<ScenarioCommandArg>? args = null)
        {
            if (!IsValidName(name))
            {
                throw new ArgumentException(
                    "\"" + name + "\" is not a script command name: two or more lower-case dot-separated segments, like my.mod.wave",
                    nameof(name));
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                throw new ArgumentException("a script command needs a one-sentence description", nameof(description));
            }

            var list = new List<ScenarioCommandArg>();
            var slots = new HashSet<int>();
            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (arg is null || !slots.Add(arg.Slot))
                    {
                        throw new ArgumentException("argument slots must be distinct and not null", nameof(args));
                    }

                    list.Add(arg);
                }
            }

            list.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            Name = name;
            Description = description.Trim();
            Args = list;
        }

        /// <summary>The command name.</summary>
        public string Name { get; }

        /// <summary>One sentence.</summary>
        public string Description { get; }

        /// <summary>What its arguments mean, by slot.</summary>
        public IReadOnlyList<ScenarioCommandArg> Args { get; }

        /// <summary>True when <paramref name="name"/> is a usable command name.</summary>
        /// <param name="name">A candidate.</param>
        public static bool IsValidName(string? name) =>
            name != null && name.Length <= MaxNameLength && NameRegex.IsMatch(name);

        /// <inheritdoc />
        public override string ToString() => Name;
    }
}
