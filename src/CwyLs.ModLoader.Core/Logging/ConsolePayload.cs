using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Logging
{
    /// <summary>
    /// The reserved bus-payload members a producer uses to say what its event looks like on the
    /// console, and the rendering of a payload value as a console field.
    /// </summary>
    /// <remarks>
    /// The contract: a producer publishes its event once, on <c>TraceBus</c>, and names
    /// its own <see cref="TagKey"/>, <see cref="PrimaryKey"/> and field order. The mirror never
    /// parses free text and never invents a layout - it either finds these members or falls back to
    /// the canonical table in <see cref="ConsoleEventMap"/>. Everything else is file-only.
    /// </remarks>
    public static class ConsolePayload
    {
        /// <summary>Payload member holding the one-word console tag (string).</summary>
        public const string TagKey = "tag";

        /// <summary>Payload member holding the primary column (string).</summary>
        public const string PrimaryKey = "primary";

        /// <summary>
        /// Payload member holding the console field order: <c>"a,b,c"</c> or <c>string[]</c>.
        /// An item may rename the column: <c>"dur:durationSec"</c> prints the payload member
        /// <c>durationSec</c> as <c>dur=</c>.
        /// </summary>
        public const string FieldsKey = "consoleFields";

        /// <summary>
        /// Payload member (bool) forcing the decision: false keeps the event in the trace file even
        /// when it carries a tag. Producers set it on <c>phase=</c> and diagnostic events.
        /// </summary>
        public const string MirrorKey = "mirror";

        /// <summary>Payload member (string: <c>info</c>, <c>warn</c>, <c>error</c>) naming the channel.</summary>
        public const string LevelKey = "level";

        /// <summary>True when the payload explicitly forbids the console copy.</summary>
        /// <param name="payload">The bus payload. Null counts as "did not say".</param>
        public static bool IsMuted(IReadOnlyDictionary<string, object?>? payload) =>
            payload != null
            && payload.TryGetValue(MirrorKey, out var value)
            && value is bool flag
            && !flag;

        /// <summary>The producer's tag, or null when it did not name one.</summary>
        /// <param name="payload">The bus payload.</param>
        public static string? Tag(IReadOnlyDictionary<string, object?>? payload) => Text(payload, TagKey);

        /// <summary>The producer's primary column, or null.</summary>
        /// <param name="payload">The bus payload.</param>
        public static string? Primary(IReadOnlyDictionary<string, object?>? payload) => Text(payload, PrimaryKey);

        /// <summary>The level the producer asked for; <see cref="ConsoleLevel.Info"/> by default.</summary>
        /// <param name="payload">The bus payload.</param>
        public static ConsoleLevel Level(IReadOnlyDictionary<string, object?>? payload)
        {
            var level = Text(payload, LevelKey);
            if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
            {
                return ConsoleLevel.Error;
            }

            return string.Equals(level, "warn", StringComparison.OrdinalIgnoreCase)
                || string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase)
                ? ConsoleLevel.Warn
                : ConsoleLevel.Info;
        }

        /// <summary>
        /// The console fields the producer asked for, in the order it asked for them. A member the
        /// payload does not carry still gets its column, printed as <c>-</c>.
        /// </summary>
        /// <param name="payload">The bus payload.</param>
        public static IReadOnlyList<ConsoleField> Fields(IReadOnlyDictionary<string, object?>? payload)
        {
            var fields = new List<ConsoleField>();
            if (payload is null || !payload.TryGetValue(FieldsKey, out var spec) || spec is null)
            {
                return fields;
            }

            foreach (var item in Items(spec))
            {
                var separator = item.IndexOf(':');
                var name = separator < 0 ? item : item.Substring(0, separator);
                var key = separator < 0 ? item : item.Substring(separator + 1);

                if (name.Length == 0 || key.Length == 0)
                {
                    continue;
                }

                payload.TryGetValue(key, out var value);
                fields.Add(new ConsoleField(name, Value(value)));
            }

            return fields;
        }

        /// <summary>A payload member as text, or null when it is absent, null or empty.</summary>
        /// <param name="payload">The bus payload.</param>
        /// <param name="key">Member name.</param>
        public static string? Text(IReadOnlyDictionary<string, object?>? payload, string key)
        {
            if (payload is null || !payload.TryGetValue(key, out var value))
            {
                return null;
            }

            var text = Value(value);
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>
        /// Renders one payload value the way every console field renders it: invariant culture,
        /// <c>true</c>/<c>false</c> for a flag, a comma-joined list for a sequence.
        /// </summary>
        /// <param name="value">Any payload value. Null returns null.</param>
        public static string? Value(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string text:
                    return text;
                case bool flag:
                    return flag ? "true" : "false";
                case double d:
                    return d.ToString("0.###", CultureInfo.InvariantCulture);
                case float f:
                    return f.ToString("0.###", CultureInfo.InvariantCulture);
                case decimal m:
                    return m.ToString("0.###", CultureInfo.InvariantCulture);
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                case IEnumerable sequence:
                    return Join(sequence);
                default:
                    return value.ToString();
            }
        }

        /// <summary>A number the way a duration is always written: one decimal, invariant.</summary>
        /// <param name="payload">The bus payload.</param>
        /// <param name="key">Member holding the seconds.</param>
        public static string? Seconds(IReadOnlyDictionary<string, object?>? payload, string key)
        {
            if (payload is null || !payload.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            switch (value)
            {
                case double d: return d.ToString("0.0", CultureInfo.InvariantCulture) + "s";
                case float f: return f.ToString("0.0", CultureInfo.InvariantCulture) + "s";
                case decimal m: return m.ToString("0.0", CultureInfo.InvariantCulture) + "s";
                case long l: return l.ToString("0.0", CultureInfo.InvariantCulture) + "s";
                case int i: return i.ToString("0.0", CultureInfo.InvariantCulture) + "s";
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    return parsed.ToString("0.0", CultureInfo.InvariantCulture) + "s";
                default:
                    // Matched rather than converted: a producer that published something
                    // non-numeric must not cost the whole line.
                    return null;
            }
        }

        private static IEnumerable<string> Items(object spec)
        {
            if (spec is string text)
            {
                foreach (var part in text.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0)
                    {
                        yield return trimmed;
                    }
                }

                yield break;
            }

            if (spec is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    var trimmed = item?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        yield return trimmed!;
                    }
                }
            }
        }

        private static string Join(IEnumerable sequence)
        {
            var sb = new StringBuilder();
            foreach (var item in sequence)
            {
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(Value(item));
            }

            return sb.ToString();
        }
    }
}
