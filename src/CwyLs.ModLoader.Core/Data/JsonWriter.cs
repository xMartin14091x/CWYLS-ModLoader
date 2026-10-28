using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// A small reflective object-to-JSON writer, built on <see cref="MiniJson"/>'s escaping and
    /// number formatting. It exists because the snapshot has to be emitted from a Unity Mono
    /// plugin (no System.Text.Json) and from a net10.0 unit test with identical output, and because
    /// <see cref="IGameDataProvider"/> lets mods hand us arbitrary objects we cannot type ahead.
    /// </summary>
    /// <remarks>
    /// <para>Rules, all of which the unit tests pin down:</para>
    /// <list type="bullet">
    /// <item><description><c>null</c> becomes <c>null</c>.</description></item>
    /// <item><description>
    /// <see cref="ulong"/> becomes a JSON <em>string</em>. The game mints ids from
    /// <c>DateTime.Now.Ticks</c> (~6.4e17), far above the 2^53 a JavaScript or JSON-float reader
    /// can hold exactly, so emitting them as numbers would silently corrupt every todo and note id.
    /// </description></item>
    /// <item><description>
    /// <see cref="DateTime"/> and <see cref="DateTimeOffset"/> become ISO-8601 with millisecond
    /// precision; a UTC <c>DateTime</c> gets <c>Z</c>, a local one its offset, an unspecified one
    /// neither.
    /// </description></item>
    /// <item><description>Enums become their name; <see cref="Guid"/> and <see cref="TimeSpan"/> become strings.</description></item>
    /// <item><description>Non-finite floats become <c>null</c>, because <c>NaN</c> is not JSON.</description></item>
    /// <item><description>
    /// Anything else is walked reflectively: public readable instance properties and public
    /// instance fields, keys camel-cased. Key order follows reflection order and is not part of
    /// the contract.
    /// </description></item>
    /// </list>
    /// <para>
    /// Depth is capped at <see cref="MaxDepth"/> and objects already on the current path are
    /// replaced by <see cref="CycleMarker"/>, so a mod handing back a live, self-referencing game
    /// object cannot hang the writer.
    /// </para>
    /// </remarks>
    public static class JsonWriter
    {
        /// <summary>How deep the walker nests before giving up.</summary>
        public const int MaxDepth = 12;

        /// <summary>Emitted in place of a value that refers back into its own path.</summary>
        public const string CycleMarker = "<cycle>";

        /// <summary>Emitted in place of a value nested deeper than <see cref="MaxDepth"/>.</summary>
        public const string DepthMarker = "<max-depth>";

        private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff";

        /// <summary>
        /// Serialises <paramref name="value"/> to a compact JSON document.
        /// </summary>
        /// <param name="value">Anything; see the type's remarks for how each shape is written.</param>
        /// <param name="onError">
        /// Called with a one-line description whenever a member cannot be read (a property getter
        /// threw). The member is then omitted. Pass the plugin's logger so a broken provider is
        /// visible in LogOutput instead of silently producing a short document.
        /// </param>
        public static string Write(object? value, Action<string>? onError = null)
        {
            var sb = new StringBuilder(4096);
            WriteValue(sb, value, 0, new List<object>(MaxDepth), onError);
            return sb.ToString();
        }

        private static void WriteValue(
            StringBuilder sb, object? value, int depth, List<object> path, Action<string>? onError)
        {
            if (value is null)
            {
                sb.Append("null");
                return;
            }

            if (TryWriteScalar(sb, value))
            {
                return;
            }

            if (depth >= MaxDepth)
            {
                sb.Append(MiniJson.String(DepthMarker));
                return;
            }

            // Reference identity, not Equals: two equal-but-distinct items are not a cycle, and a
            // type with a throwing Equals must not be able to break serialisation.
            foreach (var seen in path)
            {
                if (ReferenceEquals(seen, value))
                {
                    sb.Append(MiniJson.String(CycleMarker));
                    return;
                }
            }

            path.Add(value);
            try
            {
                switch (value)
                {
                    case IDictionary dictionary:
                        WriteDictionary(sb, dictionary, depth, path, onError);
                        break;
                    case IEnumerable sequence:
                        WriteArray(sb, sequence, depth, path, onError);
                        break;
                    default:
                        WriteObject(sb, value, depth, path, onError);
                        break;
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        private static bool TryWriteScalar(StringBuilder sb, object value)
        {
            switch (value)
            {
                case string s:
                    sb.Append(MiniJson.String(s));
                    return true;
                case char c:
                    sb.Append(MiniJson.String(c.ToString()));
                    return true;
                case bool b:
                    sb.Append(MiniJson.Bool(b));
                    return true;

                // ulong is deliberately a string; see the remarks on this class.
                case ulong u:
                    sb.Append(MiniJson.String(u.ToString(CultureInfo.InvariantCulture)));
                    return true;

                case sbyte or byte or short or ushort or int or uint or long:
                    sb.Append(MiniJson.Number(Convert.ToInt64(value, CultureInfo.InvariantCulture)));
                    return true;

                case float f:
                    sb.Append(float.IsNaN(f) || float.IsInfinity(f)
                        ? "null"
                        : f.ToString("R", CultureInfo.InvariantCulture));
                    return true;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : MiniJson.Number(d));
                    return true;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return true;

                case DateTime dt:
                    sb.Append(MiniJson.String(FormatDateTime(dt)));
                    return true;
                case DateTimeOffset dto:
                    sb.Append(MiniJson.String(MiniJson.Iso8601(dto)));
                    return true;
                case TimeSpan ts:
                    sb.Append(MiniJson.String(ts.ToString("c", CultureInfo.InvariantCulture)));
                    return true;
                case Guid g:
                    sb.Append(MiniJson.String(g.ToString("D", CultureInfo.InvariantCulture)));
                    return true;

                case Enum e:
                    sb.Append(MiniJson.String(e.ToString()));
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>ISO-8601 to the millisecond, carrying whatever the value's Kind says.</summary>
        private static string FormatDateTime(DateTime value)
        {
            var body = value.ToString(DateFormat, CultureInfo.InvariantCulture);

            return value.Kind switch
            {
                DateTimeKind.Utc => body + "Z",
                DateTimeKind.Local => body + new DateTimeOffset(value).ToString("zzz", CultureInfo.InvariantCulture),
                _ => body,
            };
        }

        private static void WriteDictionary(
            StringBuilder sb, IDictionary dictionary, int depth, List<object> path, Action<string>? onError)
        {
            sb.Append('{');
            var first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;

                // JSON keys are strings; a ulong key therefore keeps its full precision for free.
                sb.Append(MiniJson.String(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty))
                    .Append(':');
                WriteValue(sb, entry.Value, depth + 1, path, onError);
            }

            sb.Append('}');
        }

        private static void WriteArray(
            StringBuilder sb, IEnumerable sequence, int depth, List<object> path, Action<string>? onError)
        {
            sb.Append('[');
            var first = true;
            foreach (var item in sequence)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                WriteValue(sb, item, depth + 1, path, onError);
            }

            sb.Append(']');
        }

        private static void WriteObject(
            StringBuilder sb, object value, int depth, List<object> path, Action<string>? onError)
        {
            var type = value.GetType();
            sb.Append('{');
            var first = true;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object? member;
                try
                {
                    member = property.GetValue(value, null);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(
                        $"json: {type.Name}.{property.Name} getter threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                AppendMember(sb, ref first, property.Name, member, depth, path, onError);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object? member;
                try
                {
                    member = field.GetValue(value);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(
                        $"json: {type.Name}.{field.Name} field read threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                AppendMember(sb, ref first, field.Name, member, depth, path, onError);
            }

            sb.Append('}');
        }

        private static void AppendMember(
            StringBuilder sb,
            ref bool first,
            string name,
            object? member,
            int depth,
            List<object> path,
            Action<string>? onError)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(MiniJson.String(CamelCase(name))).Append(':');
            WriteValue(sb, member, depth + 1, path, onError);
        }

        /// <summary>
        /// <c>SchemaVersion</c> becomes <c>schemaVersion</c>. Only the first character changes,
        /// which is enough for every name in this schema and keeps the mapping reversible.
        /// </summary>
        internal static string CamelCase(string name)
        {
            if (name.Length == 0 || !char.IsUpper(name[0]))
            {
                return name;
            }

            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }
    }
}
