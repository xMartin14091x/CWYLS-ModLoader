using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// Which pack small talks the player has already heard, kept in the mod's own data folder so the
    /// game's lottery treats them as seen in later sessions - without the id ever reaching the save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game's small-talk lottery draws one number and asks the <em>unseen</em> scenes first
    /// (<c>Arg1</c> &gt;= the number), and only when none qualifies the seen ones (<c>Arg2</c>,
    /// 4-8 for every game scene) - <c>SmallTalkSelector.LotterySmallTalk</c>. A pack scene is held out of the
    /// save (<see cref="PackLineSaveFilter"/>), so every launch it was unseen again: with <c>Arg1</c> 25 it
    /// won a quarter of all lotteries, and in the other three quarters no seen game scene could qualify
    /// (their <c>Arg2</c> is below any number above 25). In test sessions it played 6
    /// times against 1 game small talk.
    /// </para>
    /// <para>
    /// File: <c>{"schema":1,"seen":["mymod_smalltalk_scene_1"]}</c>. Unknown or malformed content
    /// starts empty (the next play rewrites it); ids no pack declares any more are kept but never put
    /// back into the game's list.
    /// </para>
    /// </remarks>
    public static class PackLineSeen
    {
        /// <summary>The file's schema number.</summary>
        public const int Schema = 1;

        /// <summary>Reads the file's ids. Null, empty or malformed text gives an empty set and a reason.</summary>
        /// <param name="json">The file's text.</param>
        /// <param name="problem">Why nothing was read, or empty.</param>
        public static HashSet<string> Parse(string? json, out string problem)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            problem = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                return seen;
            }

            if (!JsonReader.TryParse(json!, out var root, out var error) || !(root is JsonObject obj))
            {
                problem = "not a JSON object" + (string.IsNullOrEmpty(error) ? string.Empty : ": " + error);
                return seen;
            }

            if (!obj.TryGetValue("seen", out var list) || !(list is IList<object?> items))
            {
                problem = "no \"seen\" array";
                return seen;
            }

            foreach (var item in items)
            {
                if (item is string id && id.Length > 0)
                {
                    seen.Add(id);
                }
            }

            return seen;
        }

        /// <summary>The file's text for a set of ids, sorted so the file only changes when the set does.</summary>
        /// <param name="seen">The ids.</param>
        public static string ToJson(IEnumerable<string> seen)
        {
            var ids = new List<string>(seen ?? Array.Empty<string>());
            ids.Sort(StringComparer.Ordinal);
            var text = new StringBuilder("{\"schema\":").Append(Schema.ToString(CultureInfo.InvariantCulture)).Append(",\"seen\":[");
            for (var i = 0; i < ids.Count; i++)
            {
                text.Append(i == 0 ? "\n  \"" : ",\n  \"").Append(ids[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }

            return text.Append(ids.Count == 0 ? "]}\n" : "\n]}\n").ToString();
        }

        /// <summary>
        /// Puts every remembered id that is a current pack line into the game's in-memory played list,
        /// so the lottery counts it as seen. Returns how many were added.
        /// </summary>
        /// <param name="played">The game's <c>PlayedScenarioGroupIDs</c> (in memory).</param>
        /// <param name="seen">The remembered ids.</param>
        /// <param name="isPackId">True for a group a pack declares now.</param>
        public static int MarkSeen(IList<string>? played, IEnumerable<string>? seen, Func<string, bool> isPackId)
        {
            if (played is null || seen is null || isPackId is null)
            {
                return 0;
            }

            var added = 0;
            foreach (var id in seen)
            {
                if (isPackId(id) && !played.Contains(id))
                {
                    played.Add(id);
                    added++;
                }
            }

            return added;
        }
    }
}
