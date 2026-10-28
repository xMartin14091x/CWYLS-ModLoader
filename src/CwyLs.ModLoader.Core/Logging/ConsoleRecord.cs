using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Logging
{
    /// <summary>How loud one console record is. The tag carries the same information for a reader;
    /// the level is what decides which BepInEx channel the line is written on.</summary>
    public enum ConsoleLevel
    {
        /// <summary>Normal event line.</summary>
        Info,

        /// <summary>Something the user should notice but that did not break anything.</summary>
        Warn,

        /// <summary>Something failed.</summary>
        Error
    }

    /// <summary>One <c>k=v</c> pair of a console record. Ordered: the producer decides the order.</summary>
    public readonly struct ConsoleField
    {
        /// <summary>Field name, e.g. <c>dur</c>. Lower camel case, no spaces.</summary>
        public string Key { get; }

        /// <summary>
        /// Field value. Null or empty is written as <see cref="ConsoleRecord.Missing"/>, so a
        /// column is never silently lost.
        /// </summary>
        public string? Value { get; }

        /// <summary>Creates a field.</summary>
        /// <param name="key">Field name. Null becomes <c>?</c>.</param>
        /// <param name="value">Field value; null is allowed and prints as <c>-</c>.</param>
        public ConsoleField(string? key, string? value)
        {
            Key = string.IsNullOrEmpty(key) ? "?" : key!;
            Value = value;
        }
    }

    /// <summary>
    /// One canonical console line: the shape every CWY console line has.
    /// <para>
    /// Rendered by <see cref="ConsoleRecordFormatter"/> as
    /// <c>HH:mm:ss.fff␠␠TAG␠␠&lt;primary&gt;␠␠k=v k="quoted string"</c> and read back by
    /// <see cref="ConsoleRecordParser"/>. Two spaces separate the three columns, which is what
    /// makes the line parseable while single spaces stay free inside the primary.
    /// </para>
    /// <para>
    /// Not called <c>ConsoleLine</c>: that name belongs to the in-game console's scrollback
    /// (<c>Core/Console/ConsoleBuffer.cs</c>).
    /// </para>
    /// </summary>
    public sealed class ConsoleRecord
    {
        /// <summary>What a missing value prints as, so the layout never loses a column.</summary>
        public const string Missing = "-";

        /// <summary>Longest primary written before it is cut with an ellipsis.</summary>
        public const int PrimaryLimit = 400;

        /// <summary>What a newline inside a value becomes: one physical line per event, always.</summary>
        public const char NewlineGlyph = '⏎';

        private static readonly IReadOnlyList<ConsoleField> NoFields = Array.Empty<ConsoleField>();

        /// <summary>Creates a record.</summary>
        /// <param name="time">Event time - when it happened, not when it was printed.</param>
        /// <param name="tag">One upper-case word, e.g. <c>SPEECH</c>. See <see cref="ConsoleTags"/>.</param>
        /// <param name="primary">The human-readable subject of the line. Null prints as <c>-</c>.</param>
        /// <param name="fields">Ordered <c>k=v</c> details. Null means none.</param>
        /// <param name="level">Which channel the line belongs on. Defaults to <see cref="ConsoleLevel.Info"/>.</param>
        public ConsoleRecord(
            DateTimeOffset time,
            string tag,
            string? primary,
            IReadOnlyList<ConsoleField>? fields = null,
            ConsoleLevel level = ConsoleLevel.Info)
        {
            Time = time;
            Tag = NormalizeTag(tag);
            Primary = primary;
            Fields = fields ?? NoFields;
            Level = level;
        }

        /// <summary>When the event happened.</summary>
        public DateTimeOffset Time { get; }

        /// <summary>The one-word upper-case tag.</summary>
        public string Tag { get; }

        /// <summary>The subject of the line; null prints as <c>-</c>.</summary>
        public string? Primary { get; }

        /// <summary>Ordered details.</summary>
        public IReadOnlyList<ConsoleField> Fields { get; }

        /// <summary>Which BepInEx channel the line belongs on.</summary>
        public ConsoleLevel Level { get; }

        /// <summary>The value of a field by name, or null when the record does not carry it.</summary>
        /// <param name="key">Field name, compared ordinally.</param>
        public string? Field(string key)
        {
            for (var i = 0; i < Fields.Count; i++)
            {
                if (string.Equals(Fields[i].Key, key, StringComparison.Ordinal))
                {
                    return Fields[i].Value;
                }
            }

            return null;
        }

        /// <summary>The formatted line. Same as <see cref="ConsoleRecordFormatter.Format"/>.</summary>
        public override string ToString() => ConsoleRecordFormatter.Format(this);

        /// <summary>
        /// A tag is one upper-case word. Anything else is coerced rather than rejected: a log line
        /// with an odd tag is still worth printing, and the viewer prints an unknown tag dim.
        /// </summary>
        private static string NormalizeTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return ConsoleTags.Unknown;
            }

            var trimmed = tag!.Trim();
            var buffer = new char[trimmed.Length];
            var length = 0;

            foreach (var ch in trimmed)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer[length++] = char.ToUpperInvariant(ch);
                }
            }

            return length == 0 ? ConsoleTags.Unknown : new string(buffer, 0, length);
        }
    }
}
