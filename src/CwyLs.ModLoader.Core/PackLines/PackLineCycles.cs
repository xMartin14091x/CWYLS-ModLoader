using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>Where one pool's cycle stood when the last pack line in it was dealt.</summary>
    public sealed class PackLineCycleRecord
    {
        /// <summary>Creates a record.</summary>
        /// <param name="beforeLast">The game list's <c>_beforeLast</c> (the cycle's last element), or <see cref="PackLineCycles.Unknown"/>.</param>
        /// <param name="gameLeft">How many of the game's own episodes were still in the list.</param>
        /// <param name="played">Pack episodes already dealt in this cycle.</param>
        public PackLineCycleRecord(int beforeLast, int gameLeft, IEnumerable<int>? played)
        {
            BeforeLast = beforeLast;
            GameLeft = gameLeft;
            Played = new List<int>(played ?? Array.Empty<int>());
            Played.Sort();
        }

        /// <summary>The cycle's last element, or <see cref="PackLineCycles.Unknown"/>.</summary>
        public int BeforeLast { get; }

        /// <summary>The game's own episodes still to be dealt.</summary>
        public int GameLeft { get; }

        /// <summary>Pack episodes already dealt in this cycle, ascending.</summary>
        public List<int> Played { get; }
    }

    /// <summary>
    /// One deck for game lines and pack lines across sessions: game and pack lines are dealt from
    /// one shared pool, one line at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game saves each <c>RandomList</c> with its remaining cycle (<c>[ES3Serializable]
    /// _randomList</c>, <c>_beforeLast</c> on <c>RandomList</c>), so one cycle spans many sessions. The pack
    /// lines' own bookkeeping (<see cref="PackLinePoolState"/>) lived only in memory, so every launch
    /// owed them again and put them back into the remaining deck: a pack line came once per session,
    /// a game line once per cycle. Remembering which pack lines this cycle already dealt keeps them to
    /// once per cycle like every game line.
    /// </para>
    /// <para>
    /// The same cycle is recognised by the list's <c>_beforeLast</c> (fixed for a whole cycle, set by
    /// <c>InitList</c>) and by the game's remaining count, which only shrinks inside a cycle. File
    /// (mod data, never the save):
    /// <c>{"schema":1,"pools":{"click.normal":{"beforeLast":12,"gameLeft":20,"played":[1001,1003]}}}</c>.
    /// </para>
    /// </remarks>
    public static class PackLineCycles
    {
        /// <summary>The file's schema number.</summary>
        public const int Schema = 1;

        /// <summary><c>_beforeLast</c> could not be read; only the remaining count decides then.</summary>
        public const int Unknown = int.MinValue;

        /// <summary>
        /// True when the list is still in the cycle the record was made in: same last element and no
        /// more of the game's own episodes left than then (a new cycle refills the list).
        /// </summary>
        /// <param name="stored">The saved record; null means none.</param>
        /// <param name="beforeLast">The list's <c>_beforeLast</c> now, or <see cref="Unknown"/>.</param>
        /// <param name="gameLeft">The game's own episodes in the list now.</param>
        public static bool IsSameCycle(PackLineCycleRecord? stored, int beforeLast, int gameLeft) =>
            stored != null && stored.BeforeLast == beforeLast && gameLeft <= stored.GameLeft;

        /// <summary>Reads the file. Null or empty text is an empty table; malformed text is empty with a reason.</summary>
        /// <param name="json">The file's text.</param>
        /// <param name="problem">Why nothing was read, or empty.</param>
        public static Dictionary<string, PackLineCycleRecord> Parse(string? json, out string problem)
        {
            var pools = new Dictionary<string, PackLineCycleRecord>(StringComparer.Ordinal);
            problem = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                return pools;
            }

            if (!JsonReader.TryParse(json!, out var root, out var error) || !(root is JsonObject obj)
                || !obj.TryGetValue("pools", out var value) || !(value is JsonObject table))
            {
                problem = "not a cycles file" + (string.IsNullOrEmpty(error) ? string.Empty : ": " + error);
                return pools;
            }

            foreach (var entry in table)
            {
                if (!(entry.Value is JsonObject pool)
                    || !TryInt(pool, "beforeLast", out var beforeLast)
                    || !TryInt(pool, "gameLeft", out var gameLeft))
                {
                    continue;
                }

                var played = new List<int>();
                if (pool.TryGetValue("played", out var list) && list is IList<object?> items)
                {
                    foreach (var item in items)
                    {
                        if (item is long number && number >= int.MinValue && number <= int.MaxValue)
                        {
                            played.Add((int)number);
                        }
                    }
                }

                pools[entry.Key] = new PackLineCycleRecord(beforeLast, gameLeft, played);
            }

            return pools;
        }

        /// <summary>The file's text, pools sorted by id so the file only changes when a record does.</summary>
        /// <param name="pools">The records by pool id.</param>
        public static string ToJson(IReadOnlyDictionary<string, PackLineCycleRecord> pools)
        {
            var ids = new List<string>(pools.Keys);
            ids.Sort(StringComparer.Ordinal);
            var text = new StringBuilder("{\"schema\":").Append(Schema.ToString(CultureInfo.InvariantCulture)).Append(",\"pools\":{");
            for (var i = 0; i < ids.Count; i++)
            {
                var record = pools[ids[i]];
                text.Append(i == 0 ? "\n  \"" : ",\n  \"").Append(ids[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\":{\"beforeLast\":")
                    .Append(record.BeforeLast.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"gameLeft\":").Append(record.GameLeft.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"played\":[");
                for (var j = 0; j < record.Played.Count; j++)
                {
                    text.Append(j == 0 ? string.Empty : ",").Append(record.Played[j].ToString(CultureInfo.InvariantCulture));
                }

                text.Append("]}");
            }

            return text.Append(ids.Count == 0 ? "}}\n" : "\n}}\n").ToString();
        }

        private static bool TryInt(JsonObject obj, string key, out int value)
        {
            value = 0;
            if (!obj.TryGetValue(key, out var raw) || !(raw is long number) || number < int.MinValue || number > int.MaxValue)
            {
                return false;
            }

            value = (int)number;
            return true;
        }
    }
}
