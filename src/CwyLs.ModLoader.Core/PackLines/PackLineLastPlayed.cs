using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// Which weighted pack line each pool played last, for the no-repeat rule of
    /// <see cref="PackLineWeights.Roll"/>. Kept in the mod's own data folder (never the save), because
    /// the startup greeting is one pick per launch: without a file, "never twice in a row" could not
    /// hold across launches.
    /// </summary>
    /// <remarks>
    /// File: <c>{"schema":1,"last":{"gamestart":"mymod_hello","click.work":"mymod_click_001"}}</c>, keyed
    /// by <see cref="KeyOf"/>. Unknown or malformed content starts empty (the next pick rewrites it).
    /// </remarks>
    public static class PackLineLastPlayed
    {
        /// <summary>The file's schema number.</summary>
        public const int Schema = 1;

        /// <summary>The one key every startup-greeting pool shares: consecutive launches can be different kinds.</summary>
        public const string GameStartKey = "gamestart";

        /// <summary>The key a pool's last line is kept under: <see cref="GameStartKey"/> for every startup pool, else the pool id.</summary>
        /// <param name="pool">The pool.</param>
        public static string KeyOf(PackLinePool pool)
        {
            if (pool is null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            return pool.Family == PackLineFamily.GameStart ? GameStartKey : pool.Id;
        }

        /// <summary>Reads the file. Null, empty or malformed text gives an empty map and a reason.</summary>
        /// <param name="json">The file's text.</param>
        /// <param name="problem">Why nothing was read, or empty.</param>
        public static Dictionary<string, string> Parse(string? json, out string problem)
        {
            var last = new Dictionary<string, string>(StringComparer.Ordinal);
            problem = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                return last;
            }

            if (!JsonReader.TryParse(json!, out var root, out var error) || !(root is JsonObject obj))
            {
                problem = "not a JSON object" + (string.IsNullOrEmpty(error) ? string.Empty : ": " + error);
                return last;
            }

            if (!obj.TryGetValue("last", out var map) || !(map is JsonObject entries))
            {
                problem = "no \"last\" object";
                return last;
            }

            foreach (var entry in entries)
            {
                if (entry.Key.Length > 0 && entry.Value is string id && id.Length > 0)
                {
                    last[entry.Key] = id;
                }
            }

            return last;
        }

        /// <summary>The file's text, keys sorted so the file only changes when the map does.</summary>
        /// <param name="last">Key to source group id.</param>
        public static string ToJson(IReadOnlyDictionary<string, string>? last)
        {
            var keys = new List<string>();
            if (last != null)
            {
                foreach (var key in last.Keys)
                {
                    keys.Add(key);
                }
            }

            keys.Sort(StringComparer.Ordinal);
            var text = new StringBuilder("{\"schema\":").Append(Schema.ToString(CultureInfo.InvariantCulture)).Append(",\"last\":{");
            for (var i = 0; i < keys.Count; i++)
            {
                text.Append(i == 0 ? "\n  " : ",\n  ").Append(Quote(keys[i])).Append(": ").Append(Quote(last![keys[i]]));
            }

            return text.Append(keys.Count == 0 ? "}}\n" : "\n}}\n").ToString();
        }

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
