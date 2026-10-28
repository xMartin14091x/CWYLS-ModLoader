using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// The two array shapes the trace needs on top of <see cref="MiniJson"/>, which only writes
    /// scalars and objects. Kept here rather than in MiniJson so the trace owns its own additions.
    /// </summary>
    public static class TraceJson
    {
        /// <summary>Composes an array from values that are already JSON fragments.</summary>
        public static string Array(IEnumerable<string>? jsonFragments)
        {
            if (jsonFragments is null)
            {
                return "[]";
            }

            var sb = new StringBuilder("[");
            var first = true;
            foreach (var fragment in jsonFragments)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append(fragment);
            }

            return sb.Append(']').ToString();
        }

        /// <summary>How deep a bus payload is followed before a value is written as its type name.</summary>
        public const int MaxValueDepth = 4;

        /// <summary>
        /// Serialises one bus payload value. Producers publish primitives, strings, lists and
        /// nested dictionaries; anything else is described rather than expanded, so a producer
        /// cannot make the trace file unreadable by putting an object graph on the bus.
        /// </summary>
        public static string Value(object? value, int depth = 0)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string text:
                    return MiniJson.String(text);
                case bool flag:
                    return MiniJson.Bool(flag);
                case float single:
                    return MiniJson.Number(single);
                case double dbl:
                    return MiniJson.Number(dbl);
                case decimal dec:
                    return MiniJson.Number((double)dec);
                case sbyte or byte or short or ushort or int or uint or long:
                    return MiniJson.Number(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                case ulong big:
                    // Past long.MaxValue, so written as raw digits: still a valid JSON number.
                    return big.ToString(CultureInfo.InvariantCulture);
                case Enum enumeration:
                    return MiniJson.String(enumeration.ToString());
                case DateTimeOffset offset:
                    return MiniJson.String(MiniJson.Iso8601(offset));
                case DateTime time:
                    return MiniJson.String(time.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture));
                case TimeSpan span:
                    return MiniJson.Number(span.TotalSeconds);
            }

            if (depth >= MaxValueDepth)
            {
                return MiniJson.String(value.GetType().Name);
            }

            if (value is IDictionary dictionary)
            {
                var members = new List<KeyValuePair<string, string>>(dictionary.Count);
                foreach (DictionaryEntry entry in dictionary)
                {
                    members.Add(MiniJson.Member(entry.Key?.ToString() ?? "?", Value(entry.Value, depth + 1)));
                }

                return MiniJson.Object(members);
            }

            if (value is IEnumerable sequence)
            {
                var items = new List<string>();
                foreach (var item in sequence)
                {
                    items.Add(Value(item, depth + 1));
                }

                return Array(items);
            }

            return MiniJson.String(value.ToString());
        }

        /// <summary>Composes an array of JSON strings from raw text values.</summary>
        public static string Strings(IEnumerable<string?>? values)
        {
            if (values is null)
            {
                return "[]";
            }

            var sb = new StringBuilder("[");
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append(MiniJson.String(value));
            }

            return sb.Append(']').ToString();
        }
    }
}
