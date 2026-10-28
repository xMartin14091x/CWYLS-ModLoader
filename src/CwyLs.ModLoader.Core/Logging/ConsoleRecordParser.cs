using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Logging
{
    /// <summary>
    /// Reads a console line back into a <see cref="ConsoleRecord"/> - the same grammar
    /// <see cref="ConsoleRecordFormatter"/> writes, so the sink, the in-game console and
    /// <c>tools/mac/watch-log.py</c> can never drift apart.
    /// </summary>
    /// <remarks>
    /// The C# parser and the Python one in the viewer are kept honest by a shared golden corpus:
    /// the unit tests write <c>tools/mac/tests/console-golden.txt</c> and the viewer is run over it
    /// with <c>--strict</c>.
    /// </remarks>
    public static class ConsoleRecordParser
    {
        private const int StampLength = 12;   // HH:mm:ss.fff

        /// <summary>Reads one line. Returns false for anything that is not a console record.</summary>
        /// <param name="line">The physical line, with or without a trailing newline.</param>
        /// <param name="record">The parsed record, or null.</param>
        public static bool TryParse(string? line, out ConsoleRecord? record) =>
            TryParse(line, DateTimeOffset.Now, out record);

        /// <summary>
        /// Reads one line, dating it from <paramref name="reference"/>. The grammar carries a time
        /// of day and no date, so the caller decides which day the line belongs to (the sink and
        /// the viewer both read a live file, so "today" is right for them).
        /// </summary>
        /// <param name="line">The physical line.</param>
        /// <param name="reference">Supplies the date and the UTC offset of the parsed time.</param>
        /// <param name="record">The parsed record, or null.</param>
        public static bool TryParse(string? line, DateTimeOffset reference, out ConsoleRecord? record)
        {
            record = null;

            if (line is null)
            {
                return false;
            }

            var text = line.TrimEnd('\r', '\n');
            if (text.Length < StampLength + 2)
            {
                return false;
            }

            if (!TryTime(text.Substring(0, StampLength), reference, out var time))
            {
                return false;
            }

            if (text[StampLength] != ' ' || text[StampLength + 1] != ' ')
            {
                return false;
            }

            var rest = text.Substring(StampLength + 2);
            var tagEnd = rest.IndexOf(ConsoleRecordFormatter.ColumnSeparator, StringComparison.Ordinal);
            if (tagEnd <= 0)
            {
                return false;
            }

            var tag = rest.Substring(0, tagEnd);
            if (!IsTag(tag))
            {
                return false;
            }

            var body = rest.Substring(tagEnd + 2);
            var split = body.IndexOf(ConsoleRecordFormatter.ColumnSeparator, StringComparison.Ordinal);
            var primary = split < 0 ? body : body.Substring(0, split);
            var fields = split < 0
                ? Array.Empty<ConsoleField>()
                : ParseFields(body.Substring(split + 2));

            record = new ConsoleRecord(
                time,
                tag,
                string.Equals(primary, ConsoleRecord.Missing, StringComparison.Ordinal) ? null : primary,
                fields,
                LevelOf(tag));

            return true;
        }

        /// <summary>The level a tag implies: <c>WARN</c> and <c>ERROR</c> name their own.</summary>
        /// <param name="tag">A console tag.</param>
        public static ConsoleLevel LevelOf(string? tag)
        {
            if (string.Equals(tag, ConsoleTags.Error, StringComparison.Ordinal))
            {
                return ConsoleLevel.Error;
            }

            return string.Equals(tag, ConsoleTags.Warn, StringComparison.Ordinal)
                ? ConsoleLevel.Warn
                : ConsoleLevel.Info;
        }

        /// <summary>Splits a <c>k=v k="v v"</c> run into ordered fields. Never throws.</summary>
        /// <param name="text">The field column of a line.</param>
        public static IReadOnlyList<ConsoleField> ParseFields(string? text)
        {
            var fields = new List<ConsoleField>();
            if (string.IsNullOrEmpty(text))
            {
                return fields;
            }

            var i = 0;
            var span = text!;

            while (i < span.Length)
            {
                while (i < span.Length && span[i] == ' ')
                {
                    i++;
                }

                var keyStart = i;
                while (i < span.Length && span[i] != '=' && span[i] != ' ')
                {
                    i++;
                }

                if (i >= span.Length || span[i] != '=' || i == keyStart)
                {
                    // Not a k=v pair: the rest is not parseable as fields, and dropping it silently
                    // would hide a grammar break. Stop here; the caller sees the fields so far.
                    break;
                }

                var key = span.Substring(keyStart, i - keyStart);
                i++;   // past '='

                string? value;
                if (i < span.Length && span[i] == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < span.Length && span[i] != '"')
                    {
                        if (span[i] == '\\' && i + 1 < span.Length)
                        {
                            i++;
                        }

                        sb.Append(span[i]);
                        i++;
                    }

                    i++;   // past the closing quote
                    value = sb.ToString();
                }
                else
                {
                    var start = i;
                    while (i < span.Length && span[i] != ' ')
                    {
                        i++;
                    }

                    var bare = span.Substring(start, i - start);
                    value = string.Equals(bare, ConsoleRecord.Missing, StringComparison.Ordinal) ? null : bare;
                }

                fields.Add(new ConsoleField(key, value));
            }

            return fields;
        }

        private static bool IsTag(string tag)
        {
            if (tag.Length < 2 || tag.Length > 12)
            {
                return false;
            }

            foreach (var ch in tag)
            {
                if ((ch < 'A' || ch > 'Z') && (ch < '0' || ch > '9'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryTime(string stamp, DateTimeOffset reference, out DateTimeOffset time)
        {
            time = default;

            if (!DateTime.TryParseExact(
                    stamp,
                    ConsoleRecordFormatter.TimeFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                return false;
            }

            time = new DateTimeOffset(
                reference.Year, reference.Month, reference.Day,
                parsed.Hour, parsed.Minute, parsed.Second, parsed.Millisecond,
                reference.Offset);

            return true;
        }
    }
}
