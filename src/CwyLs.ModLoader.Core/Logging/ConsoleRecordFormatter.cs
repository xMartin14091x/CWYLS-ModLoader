using System;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Logging
{
    /// <summary>
    /// Renders a <see cref="ConsoleRecord"/> as the one canonical console line, and renders single
    /// values the same way everywhere.
    /// </summary>
    /// <remarks>
    /// Grammar:
    /// <code>
    /// &lt;line&gt;    ::= HH:mm:ss.fff  SP SP  TAG  SP SP  &lt;primary&gt;  [ SP SP &lt;fields&gt; ]
    /// &lt;primary&gt; ::= free text, never two consecutive spaces, never a newline
    /// &lt;fields&gt;  ::= k=v ( SP k=v )*   v = bare token | "quoted" | -
    /// </code>
    /// Rules that make it parseable and stable:
    /// <list type="number">
    /// <item>Two spaces separate the three columns; single spaces are free inside the primary and
    /// inside a quoted value.</item>
    /// <item>A value that is empty, or contains a space, a quote, a backslash or an <c>=</c>, is
    /// double-quoted with <c>\"</c> escaping. A null value is <c>-</c>.</item>
    /// <item>One physical line per event: newlines become <see cref="ConsoleRecord.NewlineGlyph"/>.</item>
    /// <item>The stamp is the event time, not the time the line reached the sink.</item>
    /// </list>
    /// </remarks>
    public static class ConsoleRecordFormatter
    {
        /// <summary>The time format every line starts with.</summary>
        public const string TimeFormat = "HH:mm:ss.fff";

        /// <summary>The column separator: two spaces.</summary>
        public const string ColumnSeparator = "  ";

        /// <summary>Renders the record as one physical line, without a trailing newline.</summary>
        /// <param name="record">The record. Null returns an empty string rather than throwing.</param>
        public static string Format(ConsoleRecord? record)
        {
            if (record is null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(96);
            sb.Append(record.Time.ToString(TimeFormat, CultureInfo.InvariantCulture));
            sb.Append(ColumnSeparator).Append(record.Tag);
            sb.Append(ColumnSeparator).Append(Primary(record.Primary));

            var fields = record.Fields;
            for (var i = 0; i < fields.Count; i++)
            {
                sb.Append(i == 0 ? ColumnSeparator : " ");
                sb.Append(fields[i].Key).Append('=').Append(Value(fields[i].Value));
            }

            return sb.ToString();
        }

        /// <summary>
        /// The primary column: one line, never two consecutive spaces (they would be read as a
        /// column break), capped at <see cref="ConsoleRecord.PrimaryLimit"/>.
        /// </summary>
        /// <param name="primary">The raw primary text; null or blank becomes <c>-</c>.</param>
        public static string Primary(string? primary)
        {
            if (string.IsNullOrWhiteSpace(primary))
            {
                return ConsoleRecord.Missing;
            }

            var single = Flatten(primary!);
            var sb = new StringBuilder(single.Length);
            var lastWasSpace = false;

            foreach (var ch in single)
            {
                if (ch == ' ')
                {
                    if (lastWasSpace)
                    {
                        continue;
                    }

                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }

                sb.Append(ch);
            }

            var text = sb.ToString().Trim();
            if (text.Length == 0)
            {
                return ConsoleRecord.Missing;
            }

            return text.Length <= ConsoleRecord.PrimaryLimit
                ? text
                : text.Substring(0, ConsoleRecord.PrimaryLimit) + "…";
        }

        /// <summary>
        /// One field value: <c>-</c> when missing, quoted when it could not be read back as a bare
        /// token, otherwise as it stands.
        /// </summary>
        /// <param name="value">The raw value; null or empty becomes <c>-</c>.</param>
        public static string Value(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return ConsoleRecord.Missing;
            }

            var single = Flatten(value!);
            if (!NeedsQuotes(single))
            {
                return single;
            }

            var sb = new StringBuilder(single.Length + 2);
            sb.Append('"');
            foreach (var ch in single)
            {
                if (ch == '"' || ch == '\\')
                {
                    sb.Append('\\');
                }

                sb.Append(ch);
            }

            return sb.Append('"').ToString();
        }

        /// <summary>True when the value cannot be written bare.</summary>
        /// <param name="value">An already flattened value.</param>
        private static bool NeedsQuotes(string value)
        {
            // "-" bare would read back as "missing", so a real single dash is quoted.
            if (value.Length == 0 || string.Equals(value, ConsoleRecord.Missing, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var ch in value)
            {
                if (ch == ' ' || ch == '"' || ch == '\\' || char.IsControl(ch))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Collapses a value onto one physical line.</summary>
        /// <param name="text">Any text.</param>
        private static string Flatten(string text)
        {
            if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0 && text.IndexOf('\t') < 0)
            {
                return text;
            }

            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                switch (ch)
                {
                    case '\r':
                        // CRLF is one break, not two.
                        if (i + 1 < text.Length && text[i + 1] == '\n')
                        {
                            i++;
                        }

                        sb.Append(ConsoleRecord.NewlineGlyph);
                        break;
                    case '\n':
                        sb.Append(ConsoleRecord.NewlineGlyph);
                        break;
                    case '\t':
                        sb.Append(' ');
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
