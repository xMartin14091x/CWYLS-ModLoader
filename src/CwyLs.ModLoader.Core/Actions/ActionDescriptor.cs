using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// What an action is: its name, tier, arguments and whether it destroys something. The
    /// registry, the console (<c>act list</c>), the API catalog (<c>/v1/actions</c>) and
    /// API clients all read this one description. Immutable.
    /// </summary>
    public sealed class ActionDescriptor
    {
        /// <summary>
        /// The pattern of an action name: two or more dot-separated lower-case segments, e.g.
        /// <c>music.play</c>, <c>todo.add</c>, <c>my.mod.greet</c>. The part before the last
        /// dot is the <see cref="Family"/>.
        /// </summary>
        public const string NamePattern = "^[a-z0-9][a-z0-9_-]*(\\.[a-z0-9][a-z0-9_-]*)+$";

        /// <summary>Longest accepted name.</summary>
        public const int MaxNameLength = 96;

        private static readonly Regex NameRegex =
            new Regex(@"\A[a-z0-9][a-z0-9_\-]*(\.[a-z0-9][a-z0-9_\-]*)+\z", RegexOptions.CultureInvariant);

        private readonly Dictionary<string, ActionParameter> _byName;

        /// <summary>Creates a descriptor.</summary>
        /// <param name="name">The action name (<see cref="NamePattern"/>); lower case only.</param>
        /// <param name="tier">What it may change.</param>
        /// <param name="description">One sentence for people and for MCP.</param>
        /// <param name="parameters">Its arguments; null for none. Names must be distinct (case-insensitive).</param>
        /// <param name="isDestructive">
        /// True when it removes or overwrites something the player cannot get back (a delete).
        /// Always needs confirmation (<see cref="ActionGate"/>); not allowed on <see cref="ActionTier.Read"/>.
        /// </param>
        /// <exception cref="ArgumentException">The name, tier, description or a parameter is unusable.</exception>
        public ActionDescriptor(
            string name,
            ActionTier tier,
            string description,
            IEnumerable<ActionParameter>? parameters = null,
            bool isDestructive = false)
        {
            if (!IsValidName(name))
            {
                throw new ArgumentException(
                    "\"" + name + "\" is not an action name: two or more lower-case dot-separated segments, like music.play (" +
                    NamePattern + ", at most " + MaxNameLength + " characters)",
                    nameof(name));
            }

            if (!Enum.IsDefined(typeof(ActionTier), tier))
            {
                throw new ArgumentException("unknown tier " + (int)tier, nameof(tier));
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                throw new ArgumentException("an action needs a one-sentence description", nameof(description));
            }

            if (isDestructive && tier == ActionTier.Read)
            {
                throw new ArgumentException("a Read action cannot be destructive", nameof(isDestructive));
            }

            var list = new List<ActionParameter>();
            _byName = new Dictionary<string, ActionParameter>(StringComparer.OrdinalIgnoreCase);
            if (parameters != null)
            {
                foreach (var parameter in parameters)
                {
                    if (parameter is null)
                    {
                        throw new ArgumentException("a parameter is null", nameof(parameters));
                    }

                    if (_byName.ContainsKey(parameter.Name))
                    {
                        throw new ArgumentException("the parameter \"" + parameter.Name + "\" appears twice", nameof(parameters));
                    }

                    _byName.Add(parameter.Name, parameter);
                    list.Add(parameter);
                }
            }

            Name = name;
            Tier = tier;
            Description = description.Trim();
            Parameters = list;
            IsDestructive = isDestructive;
            Family = name.Substring(0, name.LastIndexOf('.'));
        }

        /// <summary>The action name, e.g. <c>music.play</c>.</summary>
        public string Name { get; }

        /// <summary>Everything before the last dot: <c>music</c> for <c>music.play</c>, the mod id for a mod's action.</summary>
        public string Family { get; }

        /// <summary>What it may change.</summary>
        public ActionTier Tier { get; }

        /// <summary>One sentence for people and for MCP.</summary>
        public string Description { get; }

        /// <summary>Its arguments, in declaration order.</summary>
        public IReadOnlyList<ActionParameter> Parameters { get; }

        /// <summary>True when it removes or overwrites something the player cannot get back.</summary>
        public bool IsDestructive { get; }

        /// <summary>True when <paramref name="name"/> matches <see cref="NamePattern"/> and <see cref="MaxNameLength"/>.</summary>
        /// <param name="name">A candidate name.</param>
        public static bool IsValidName(string? name) =>
            name != null && name.Length <= MaxNameLength && NameRegex.IsMatch(name);

        /// <summary>The parameter with this name (case-insensitive), or null.</summary>
        /// <param name="name">A parameter name.</param>
        public ActionParameter? FindParameter(string? name) =>
            name != null && _byName.TryGetValue(name, out var parameter) ? parameter : null;

        /// <summary><c>music.play (Act)</c>.</summary>
        public override string ToString() => Name + " (" + Tier + (IsDestructive ? ", destructive" : string.Empty) + ")";
    }
}
