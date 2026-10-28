using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// A parsed Satone table: every expression, body motion and AI state the game knows, with the
    /// name index that turns <c>"smile"</c> or <c>"10"</c> into the integer the game takes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Immutable and Unity-free, so the whole naming contract is unit-testable off-engine.
    /// <see cref="SatoneTable"/> holds the one instance built from the embedded
    /// <c>satone-table.json</c>; <see cref="Parse"/> exists so a test (or a future pack override)
    /// can supply its own document.
    /// </para>
    /// <para>
    /// A lookup tries three things, in order: the name or alias exactly (ordinal, case-insensitive),
    /// then the query as an integer id, then the name with separators removed
    /// (<see cref="LooseKey"/>) so the game's own <c>Wild001_Motion2_StretchShoulder</c> resolves
    /// as readily as the table's <c>wild001_motion2_stretch_shoulder</c>. A query that parses as an
    /// integer is never treated as a name - no row is named with digits alone.
    /// </para>
    /// </remarks>
    public sealed class SatoneCatalog
    {
        /// <summary>Schema version this code understands; a document that declares more is refused.</summary>
        public const int SupportedSchema = 1;

        /// <summary>Default number of names <see cref="Suggest"/> returns.</summary>
        public const int DefaultSuggestions = 5;

        /// <summary>Longest edit distance a name has to be within to be suggested at all.</summary>
        public const int MaxSuggestDistance = 3;

        private readonly Dictionary<string, SatoneFacial> _facialsByName;
        private readonly Dictionary<string, SatoneFacial> _facialsByLooseName;
        private readonly Dictionary<int, SatoneFacial> _facialsById;
        private readonly Dictionary<string, SatoneMotion> _motionsByName;
        private readonly Dictionary<string, SatoneMotion> _motionsByLooseName;
        private readonly Dictionary<int, SatoneMotion> _motionsById;
        private readonly Dictionary<int, SatoneState> _statesById;
        private readonly string[] _searchable;

        private SatoneCatalog(
            int schema,
            IReadOnlyList<SatoneFacial> facials,
            IReadOnlyList<SatoneMotion> motions,
            IReadOnlyList<SatoneState> states)
        {
            Schema = schema;
            Facials = facials;
            Motions = motions;
            States = states;

            _facialsById = new Dictionary<int, SatoneFacial>(facials.Count);
            _facialsByName = new Dictionary<string, SatoneFacial>(StringComparer.OrdinalIgnoreCase);
            _facialsByLooseName = new Dictionary<string, SatoneFacial>(StringComparer.Ordinal);
            _motionsById = new Dictionary<int, SatoneMotion>(motions.Count);
            _motionsByName = new Dictionary<string, SatoneMotion>(StringComparer.OrdinalIgnoreCase);
            _motionsByLooseName = new Dictionary<string, SatoneMotion>(StringComparer.Ordinal);
            _statesById = new Dictionary<int, SatoneState>(states.Count);

            var searchable = new List<string>(facials.Count + motions.Count);

            foreach (var facial in facials)
            {
                _facialsById[facial.Id] = facial;
                Index(_facialsByName, _facialsByLooseName, facial.Name, facial);
                foreach (var alias in facial.Aliases)
                {
                    Index(_facialsByName, _facialsByLooseName, alias, facial);
                }

                searchable.Add(facial.Name);
            }

            foreach (var motion in motions)
            {
                _motionsById[motion.Id] = motion;
                Index(_motionsByName, _motionsByLooseName, motion.Name, motion);
                foreach (var alias in motion.Aliases)
                {
                    Index(_motionsByName, _motionsByLooseName, alias, motion);
                }

                searchable.Add(motion.Name);
            }

            foreach (var state in states)
            {
                _statesById[state.Id] = state;
            }

            _searchable = searchable.ToArray();
        }

        /// <summary>Schema version the document declared.</summary>
        public int Schema { get; }

        /// <summary>Every expression, in id order.</summary>
        public IReadOnlyList<SatoneFacial> Facials { get; }

        /// <summary>Every body motion, in id order.</summary>
        public IReadOnlyList<SatoneMotion> Motions { get; }

        /// <summary>Every <c>HeroineAI.ActionStateType</c> member, in id order.</summary>
        public IReadOnlyList<SatoneState> States { get; }

        /// <summary>
        /// Parses a table document.
        /// </summary>
        /// <param name="json">The document, in the shape of the embedded <c>satone-table.json</c>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        /// <exception cref="JsonParseException">The document is not well-formed JSON.</exception>
        /// <exception cref="InvalidOperationException">
        /// The document is valid JSON but not a usable table - wrong schema, or a required member
        /// missing.
        /// </exception>
        public static SatoneCatalog Parse(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var root = JsonReader.ParseObject(json);
            var schema = (int)Integer(root, "schema", 0);
            if (schema <= 0 || schema > SupportedSchema)
            {
                throw new InvalidOperationException(
                    $"satone table schema {schema} is not supported (this build reads up to {SupportedSchema})");
            }

            return new SatoneCatalog(
                schema,
                ReadFacials(Array(root, "facials")),
                ReadMotions(Array(root, "motions")),
                ReadStates(Array(root, "states")));
        }

        /// <summary>Looks up an expression by name, alias or id.</summary>
        /// <param name="nameOrId">e.g. <c>smile</c>, <c>shy_rest</c>, <c>anim_face_shy_joy</c>, <c>Shy_Joy</c> or <c>10</c>.</param>
        /// <param name="facial">The row, or null.</param>
        /// <returns>True when a row was found.</returns>
        public bool TryFacial(string? nameOrId, out SatoneFacial? facial) =>
            TryLookup(nameOrId, _facialsByName, _facialsByLooseName, _facialsById, out facial);

        /// <summary>Looks up a body motion by name, alias or id.</summary>
        /// <param name="nameOrId">e.g. <c>wild001_stretch_shoulder</c>, <c>stretch_shoulder</c>, <c>Wild001_Motion2_StretchShoulder</c> or <c>51</c>.</param>
        /// <param name="motion">The row, or null.</param>
        /// <returns>True when a row was found.</returns>
        public bool TryMotion(string? nameOrId, out SatoneMotion? motion) =>
            TryLookup(nameOrId, _motionsByName, _motionsByLooseName, _motionsById, out motion);

        /// <summary>Looks up an AI state by its <c>ActionStateType</c> value.</summary>
        /// <param name="id">The enum value.</param>
        /// <param name="state">The row, or null.</param>
        /// <returns>True when the value is a known state.</returns>
        public bool TryState(int id, out SatoneState? state)
        {
            var found = _statesById.TryGetValue(id, out var hit);
            state = found ? hit : null;
            return found;
        }

        /// <summary>
        /// Canonical names close to <paramref name="name"/>, best first, for a "did you mean" line.
        /// </summary>
        /// <param name="name">What the caller typed. Null or blank returns an empty list.</param>
        /// <param name="max">How many names at most. Values below 1 return an empty list.</param>
        /// <remarks>
        /// Ranked: a name that starts with the query, then one that contains it, then one within
        /// <see cref="MaxSuggestDistance"/> edits. Ties break on the shorter name and then
        /// alphabetically, so the same query always produces the same list.
        /// </remarks>
        public IReadOnlyList<string> Suggest(string? name, int max = DefaultSuggestions)
        {
            if (string.IsNullOrWhiteSpace(name) || max < 1)
            {
                return new string[0];
            }

            var query = name!.Trim().ToLowerInvariant();
            var scored = new List<(int Rank, int Distance, int Length, string Name)>();

            foreach (var candidate in _searchable)
            {
                var lower = candidate.ToLowerInvariant();
                int rank;
                var distance = Distance(query, lower, MaxSuggestDistance);

                if (lower.StartsWith(query, StringComparison.Ordinal))
                {
                    rank = 0;
                }
                else if (lower.IndexOf(query, StringComparison.Ordinal) >= 0 || query.IndexOf(lower, StringComparison.Ordinal) >= 0)
                {
                    rank = 1;
                }
                else if (distance <= MaxSuggestDistance)
                {
                    rank = 2;
                }
                else
                {
                    continue;
                }

                scored.Add((rank, distance, candidate.Length, candidate));
            }

            scored.Sort((a, b) =>
            {
                var byRank = a.Rank.CompareTo(b.Rank);
                if (byRank != 0) return byRank;
                var byDistance = a.Distance.CompareTo(b.Distance);
                if (byDistance != 0) return byDistance;
                var byLength = a.Length.CompareTo(b.Length);
                return byLength != 0 ? byLength : string.CompareOrdinal(a.Name, b.Name);
            });

            var take = Math.Min(max, scored.Count);
            var result = new string[take];
            for (var i = 0; i < take; i++)
            {
                result[i] = scored[i].Name;
            }

            return result;
        }

        /// <summary>
        /// Lower-cases a name and drops everything that is not a letter or digit, so the one name
        /// a modder is most likely to copy - the game's own enum member,
        /// <c>Wild001_Motion2_StretchShoulder</c> - resolves to the table's
        /// <c>wild001_motion2_stretch_shoulder</c>. Verified collision-free over the generated
        /// table by <c>SatoneTableTests.LooseNamesAreCollisionFree</c>.
        /// </summary>
        public static string LooseKey(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var buffer = new char[name.Length];
            var length = 0;
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    buffer[length++] = char.ToLowerInvariant(c);
                }
            }

            return new string(buffer, 0, length);
        }

        private static void Index<T>(
            Dictionary<string, T> byName,
            Dictionary<string, T> byLooseName,
            string key,
            T value)
        {
            // The generator already guarantees uniqueness; first-wins here so a hand-edited table
            // degrades into "the earlier row keeps the name" instead of throwing at startup.
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (!byName.ContainsKey(key))
            {
                byName[key] = value;
            }

            var loose = LooseKey(key);
            if (loose.Length > 0 && !byLooseName.ContainsKey(loose))
            {
                byLooseName[loose] = value;
            }
        }

        private static bool TryLookup<T>(
            string? nameOrId,
            Dictionary<string, T> byName,
            Dictionary<string, T> byLooseName,
            Dictionary<int, T> byId,
            out T? found)
            where T : class
        {
            found = null;
            if (string.IsNullOrWhiteSpace(nameOrId))
            {
                return false;
            }

            var key = nameOrId!.Trim();
            if (byName.TryGetValue(key, out var hit))
            {
                found = hit;
                return true;
            }

            if (int.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
                && byId.TryGetValue(id, out var byNumber))
            {
                found = byNumber;
                return true;
            }

            if (byLooseName.TryGetValue(LooseKey(key), out var loose))
            {
                found = loose;
                return true;
            }

            return false;
        }

        /// <summary>Levenshtein distance, abandoned once it exceeds <paramref name="limit"/>.</summary>
        private static int Distance(string a, string b, int limit)
        {
            if (Math.Abs(a.Length - b.Length) > limit)
            {
                return limit + 1;
            }

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                var best = current[0];
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                    if (current[j] < best)
                    {
                        best = current[j];
                    }
                }

                if (best > limit)
                {
                    return limit + 1;
                }

                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }

        // ---- document readers -------------------------------------------------------------

        private static IReadOnlyList<SatoneFacial> ReadFacials(List<object?> rows)
        {
            var result = new List<SatoneFacial>(rows.Count);
            foreach (var row in rows)
            {
                var item = Row(row, "facials");
                result.Add(new SatoneFacial(
                    (int)Integer(item, "id", 0),
                    Text(item, "name") ?? throw Missing("facials", "name"),
                    Text(item, "facialType"),
                    Text(item, "state"),
                    Text(item, "clip"),
                    Flag(item, "loopable"),
                    ReadMeshes(item),
                    (int)Integer(item, "usedByLines", 0),
                    Strings(item, "aliases")));
            }

            return result;
        }

        private static IReadOnlyList<SatoneMotion> ReadMotions(List<object?> rows)
        {
            var result = new List<SatoneMotion>(rows.Count);
            foreach (var row in rows)
            {
                var item = Row(row, "motions");
                result.Add(new SatoneMotion(
                    (int)Integer(item, "id", 0),
                    Text(item, "name") ?? throw Missing("motions", "name"),
                    Text(item, "animationType"),
                    Text(item, "category"),
                    Text(item, "state"),
                    Text(item, "clip"),
                    Flag(item, "loopable"),
                    Flag(item, "trigger") ?? false,
                    Flag(item, "gameSetsTrigger") ?? false,
                    (int)Integer(item, "usedByLines", 0),
                    Strings(item, "aliases")));
            }

            return result;
        }

        private static IReadOnlyList<SatoneState> ReadStates(List<object?> rows)
        {
            var result = new List<SatoneState>(rows.Count);
            foreach (var row in rows)
            {
                var item = Row(row, "states");
                result.Add(new SatoneState(
                    (int)Integer(item, "id", 0),
                    Text(item, "name") ?? throw Missing("states", "name"),
                    Text(item, "actionStateType") ?? throw Missing("states", "actionStateType")));
            }

            return result;
        }

        private static SatoneFaceMeshes ReadMeshes(JsonObject row)
        {
            if (!row.TryGetValue("meshes", out var raw) || raw is not JsonObject meshes)
            {
                return SatoneFaceMeshes.Empty;
            }

            return new SatoneFaceMeshes(
                Strings(meshes, "eye"),
                Strings(meshes, "brow"),
                Strings(meshes, "mouth"));
        }

        private static JsonObject Row(object? value, string member) =>
            value as JsonObject ?? throw new InvalidOperationException(
                $"satone table: '{member}' contains {JsonReader.TypeName(value)} where an object was expected");

        private static List<object?> Array(JsonObject root, string member)
        {
            if (!root.TryGetValue(member, out var raw) || raw is not List<object?> list)
            {
                throw Missing("satone table", member);
            }

            return list;
        }

        private static string? Text(JsonObject row, string member) =>
            row.TryGetValue(member, out var raw) ? raw as string : null;

        private static long Integer(JsonObject row, string member, long fallback) =>
            row.TryGetValue(member, out var raw) && raw is long number ? number : fallback;

        private static bool? Flag(JsonObject row, string member) =>
            row.TryGetValue(member, out var raw) && raw is bool flag ? flag : (bool?)null;

        private static IReadOnlyList<string> Strings(JsonObject row, string member)
        {
            if (!row.TryGetValue(member, out var raw) || raw is not List<object?> list)
            {
                return new string[0];
            }

            var result = new List<string>(list.Count);
            foreach (var item in list)
            {
                if (item is string text)
                {
                    result.Add(text);
                }
            }

            return result;
        }

        private static InvalidOperationException Missing(string owner, string member) =>
            new InvalidOperationException($"satone table: '{owner}' is missing '{member}'");
    }
}
