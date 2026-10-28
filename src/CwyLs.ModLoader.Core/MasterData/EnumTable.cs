using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// One game enum, as data: the member names and their numbers, as declared
    /// by the pinned game build.
    /// <para>
    /// The tables exist because <see cref="CwyLs.ModLoader.Core"/> must not reference
    /// <c>Assembly-CSharp</c>, yet a content pack has to be able to write
    /// <c>"command": "VoiceText"</c> and have that checked before the game ever sees it. Parsing
    /// therefore happens here against a table, and the adapter casts the resulting number to the
    /// real enum.
    /// </para>
    /// </summary>
    public sealed class EnumTable
    {
        private readonly Dictionary<string, int> _byName;
        private readonly Dictionary<int, string> _byValue;
        private readonly List<string> _names;

        /// <summary>Creates a table.</summary>
        /// <param name="typeName">The game type this mirrors, e.g. <c>Bulbul.MasterData.CommandType</c>.</param>
        /// <param name="members">
        /// Member name/number pairs in declaration order. A number may repeat (the game's
        /// <c>ScenarioType</c> has an alias); the first name wins for <see cref="TryName"/>.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="typeName"/> is blank, or a name repeats.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="members"/> is null.</exception>
        public EnumTable(string typeName, params (string Name, int Value)[] members)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new ArgumentException("an enum type name is required", nameof(typeName));
            }

            if (members is null)
            {
                throw new ArgumentNullException(nameof(members));
            }

            TypeName = typeName;
            _byName = new Dictionary<string, int>(members.Length, StringComparer.OrdinalIgnoreCase);
            _byValue = new Dictionary<int, string>(members.Length);
            _names = new List<string>(members.Length);

            foreach (var member in members)
            {
                if (string.IsNullOrWhiteSpace(member.Name))
                {
                    throw new ArgumentException("an enum member name is required", nameof(members));
                }

                if (_byName.ContainsKey(member.Name))
                {
                    throw new ArgumentException(
                        "duplicate member name \"" + member.Name + "\" in " + typeName, nameof(members));
                }

                _byName.Add(member.Name, member.Value);
                _names.Add(member.Name);

                if (!_byValue.ContainsKey(member.Value))
                {
                    _byValue.Add(member.Value, member.Name);
                }
            }
        }

        /// <summary>The game type this table mirrors.</summary>
        public string TypeName { get; }

        /// <summary>Every member name, in declaration order.</summary>
        public IReadOnlyList<string> Names => _names;

        /// <summary>Number of members.</summary>
        public int Count => _names.Count;

        /// <summary>
        /// Resolves a member name, case-insensitively. Names only - a numeric string goes through
        /// <see cref="TryParseText"/> so that the two cases can be reported differently.
        /// </summary>
        /// <param name="name">The member name.</param>
        /// <param name="value">The member's number, or 0.</param>
        /// <returns>True when the name is a member of this enum.</returns>
        public bool TryParseName(string? name, out int value)
        {
            if (string.IsNullOrEmpty(name))
            {
                value = 0;
                return false;
            }

            return _byName.TryGetValue(name!.Trim(), out value);
        }

        /// <summary>True when <paramref name="value"/> is a number this enum declares.</summary>
        /// <param name="value">A candidate number.</param>
        public bool IsDefined(int value) => _byValue.ContainsKey(value);

        /// <summary>The first declared name with this number.</summary>
        /// <param name="value">A member number.</param>
        /// <param name="name">The name, or null.</param>
        /// <returns>True when the number is declared.</returns>
        public bool TryName(int value, out string? name) => _byValue.TryGetValue(value, out name);

        /// <summary>
        /// Resolves a token that is either a member name or the decimal text of a declared number
        /// (a pack may write <c>"VoiceText"</c> or <c>"20"</c>).
        /// </summary>
        /// <param name="token">The token as written in the pack.</param>
        /// <param name="value">The member's number, or 0.</param>
        /// <returns>True when the token names a declared member.</returns>
        public bool TryParseText(string? token, out int value)
        {
            if (TryParseName(token, out value))
            {
                return true;
            }

            if (token != null &&
                int.TryParse(token.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) &&
                IsDefined(number))
            {
                value = number;
                return true;
            }

            value = 0;
            return false;
        }

        /// <summary>
        /// A short "did you mean" list for an error message: the first few member names.
        /// </summary>
        /// <param name="max">How many names to list.</param>
        public string Describe(int max = 6)
        {
            var shown = Math.Min(max, _names.Count);
            var parts = new string[shown];
            for (var i = 0; i < shown; i++)
            {
                parts[i] = _names[i];
            }

            var joined = string.Join(", ", parts);
            return _names.Count > shown ? joined + ", ... (" + _names.Count + " members)" : joined;
        }
    }
}
