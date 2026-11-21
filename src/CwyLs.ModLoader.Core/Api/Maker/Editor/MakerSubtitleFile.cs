using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>What <see cref="MakerSubtitleFile.Apply"/> did to a subtitle table.</summary>
    public sealed class MakerSubtitleEdit
    {
        private MakerSubtitleEdit(bool ok, string? text, bool changed, string? previous, string? error)
        {
            Ok = ok;
            Text = text;
            Changed = changed;
            Previous = previous;
            Error = error;
        }

        /// <summary>True when the edit could be made.</summary>
        public bool Ok { get; }

        /// <summary>The whole new file, or null when the file should not exist (nothing to write) or on failure.</summary>
        public string? Text { get; }

        /// <summary>True when the file has to be written: the content differs from what was there.</summary>
        public bool Changed { get; }

        /// <summary>The entry's text before the edit, or null when it had none.</summary>
        public string? Previous { get; }

        /// <summary>Why the edit could not be made, or null.</summary>
        public string? Error { get; }

        internal static MakerSubtitleEdit Done(string? text, bool changed, string? previous) =>
            new MakerSubtitleEdit(true, text, changed, previous, null);

        internal static MakerSubtitleEdit Failed(string error) => new MakerSubtitleEdit(false, null, false, null, error);
    }

    /// <summary>
    /// Sets or removes one entry of a flat JSON object - a pack's <c>data/subtitles/&lt;lang&gt;.json</c> -
    /// by editing the text, not by re-serialising it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A translator's table is hand-written and diffed line by line, so the edit touches only the one
    /// member: every other key keeps its place, its spelling and its escapes, and the file keeps its
    /// indentation, its line endings and its trailing newline. A new key goes after the last one,
    /// written in the style the file already uses (the separator before the last member, the
    /// <c>": "</c> after its key, and <c>\uXXXX</c> escapes when the file holds no raw non-ASCII
    /// text). An empty or missing file gets two-space indentation.
    /// </para>
    /// <para>
    /// A file that is not a strict JSON object (<see cref="JsonReader"/>: no comments, no trailing
    /// commas, no duplicate keys) is refused rather than "repaired": the creator's file is never
    /// rewritten from a guess. The result is parsed again before it is returned, so a bug here cannot
    /// write a broken table.
    /// </para>
    /// <para>The BOM is the caller's business: pass the text without it and write it back as it was.</para>
    /// </remarks>
    public static class MakerSubtitleFile
    {
        private const string DefaultIndent = "  ";

        /// <summary>Sets (<paramref name="text"/>) or removes (null) the entry <paramref name="id"/>.</summary>
        /// <param name="existing">The file's current text without a BOM; null or blank when there is no file.</param>
        /// <param name="id">The key; required.</param>
        /// <param name="text">The new value, or null to remove the key.</param>
        /// <returns>The new text, or why the edit is refused. Never null.</returns>
        public static MakerSubtitleEdit Apply(string? existing, string id, string? text)
        {
            if (string.IsNullOrEmpty(id))
            {
                return MakerSubtitleEdit.Failed("a localisation id is required");
            }

            if (string.IsNullOrWhiteSpace(existing))
            {
                // No file (or an empty one): removing is a no-op; setting creates the file.
                if (text is null)
                {
                    return MakerSubtitleEdit.Done(null, false, null);
                }

                var created = "{\n" + DefaultIndent + Literal(id, false) + ": " + Literal(text, false) + "\n}\n";
                return Verified(created, id, text, null);
            }

            if (!JsonReader.TryParse(existing!, out var parsed, out var parseError))
            {
                return MakerSubtitleEdit.Failed("it is not valid JSON (" + parseError + "); fix it by hand first");
            }

            if (!(parsed is JsonObject table))
            {
                return MakerSubtitleEdit.Failed(
                    "its top-level value is " + JsonReader.TypeName(parsed) + ", not an object of id -> text");
            }

            string? previous = null;
            if (table.TryGetValue(id, out var current))
            {
                if (!(current is string currentText))
                {
                    return MakerSubtitleEdit.Failed(
                        "its entry '" + id + "' is " + JsonReader.TypeName(current) + ", not text; fix it by hand first");
                }

                previous = currentText;
            }

            if (text is null && previous is null)
            {
                return MakerSubtitleEdit.Done(existing, false, null);
            }

            if (text != null && string.Equals(text, previous, StringComparison.Ordinal))
            {
                return MakerSubtitleEdit.Done(existing, false, previous);
            }

            var members = Scan(existing!, out var open, out var close);
            var asciiEscapes = UsesAsciiEscapes(existing!);
            string result;

            if (text is null)
            {
                result = Remove(existing!, members, IndexOf(members, existing!, id), open, close);
            }
            else if (previous != null)
            {
                var member = members[IndexOf(members, existing!, id)];
                result = existing!.Substring(0, member.ValueStart)
                         + Literal(text, asciiEscapes)
                         + existing.Substring(member.ValueEnd);
            }
            else
            {
                result = Add(existing!, members, open, close, id, text, asciiEscapes);
            }

            return Verified(result, id, text, previous);
        }

        /// <summary>The text of one entry of a table, or null when it has none (or the table is not valid).</summary>
        /// <param name="json">A table's text; null or blank means no table.</param>
        /// <param name="id">The key.</param>
        public static string? Read(string? json, string id)
        {
            if (string.IsNullOrWhiteSpace(json) || !JsonReader.TryParse(json!, out var parsed, out _))
            {
                return null;
            }

            return parsed is JsonObject table && table.TryGetValue(id, out var value) ? value as string : null;
        }

        /// <summary>A JSON string literal; non-ASCII as <c>\uXXXX</c> when <paramref name="asciiEscapes"/>.</summary>
        private static string Literal(string value, bool asciiEscapes)
        {
            var escaped = MiniJson.Escape(value);
            if (!asciiEscapes)
            {
                return "\"" + escaped + "\"";
            }

            var sb = new StringBuilder(escaped.Length + 16).Append('"');
            foreach (var c in escaped)
            {
                if (c > 0x7E)
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.Append('"').ToString();
        }

        /// <summary>
        /// True when the file escapes its non-ASCII text (<c>ส</c>, as Python's <c>json.dump</c>
        /// writes by default) and holds none raw, so a new value should be written the same way.
        /// </summary>
        private static bool UsesAsciiEscapes(string json)
        {
            var escapedNonAscii = false;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (c > 0x7E && c != '﻿')
                {
                    return false;
                }

                if (c == '\\' && i + 5 < json.Length && json[i + 1] == 'u'
                    && int.TryParse(json.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                    && code > 0x7E)
                {
                    escapedNonAscii = true;
                }
                else if (c == '\\')
                {
                    // Skip the escaped character, so "\\u0e2a" (a literal backslash) is not misread.
                    i++;
                }
            }

            return escapedNonAscii;
        }

        private static string Add(
            string json, List<Member> members, int open, int close, string id, string text, bool asciiEscapes)
        {
            var newline = json.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var keyLiteral = Literal(id, asciiEscapes);
            var valueLiteral = Literal(text, asciiEscapes);

            if (members.Count == 0)
            {
                // "{}" or "{ }": replace the inside with one member, two-space indented.
                return json.Substring(0, open + 1)
                       + newline + DefaultIndent + keyLiteral + ": " + valueLiteral + newline
                       + json.Substring(close);
            }

            var last = members[members.Count - 1];
            var colon = json.Substring(last.KeyEnd, last.ValueStart - last.KeyEnd);

            // The separator in front of the last member ("," + newline + indent), or for a single
            // member the whitespace after '{' with a comma in front.
            string separator;
            if (members.Count >= 2)
            {
                var before = members[members.Count - 2];
                separator = json.Substring(before.ValueEnd, last.KeyStart - before.ValueEnd);
            }
            else
            {
                separator = "," + json.Substring(open + 1, last.KeyStart - open - 1);
            }

            return json.Substring(0, last.ValueEnd)
                   + separator + keyLiteral + colon + valueLiteral
                   + json.Substring(last.ValueEnd);
        }

        private static string Remove(string json, List<Member> members, int index, int open, int close)
        {
            if (members.Count == 1)
            {
                return json.Substring(0, open + 1) + json.Substring(close);
            }

            if (index < members.Count - 1)
            {
                // Key, value, comma and the next member's indentation; this member's own
                // indentation stays and now serves the next one.
                return json.Substring(0, members[index].KeyStart) + json.Substring(members[index + 1].KeyStart);
            }

            // The last member: from the end of the one before (its comma included).
            return json.Substring(0, members[index - 1].ValueEnd) + json.Substring(members[index].ValueEnd);
        }

        private static MakerSubtitleEdit Verified(string result, string id, string? text, string? previous)
        {
            // Belt and braces: the edit must parse, and say exactly what was asked.
            if (!JsonReader.TryParse(result, out var parsed, out var error) || !(parsed is JsonObject table))
            {
                return MakerSubtitleEdit.Failed("the edited table did not parse (" + error + "); nothing was written");
            }

            var has = table.TryGetValue(id, out var value);
            if (text is null ? has : !(has && value is string s && string.Equals(s, text, StringComparison.Ordinal)))
            {
                return MakerSubtitleEdit.Failed("the edited table does not hold the change; nothing was written");
            }

            return MakerSubtitleEdit.Done(result, true, previous);
        }

        private static int IndexOf(List<Member> members, string json, string id)
        {
            for (var i = 0; i < members.Count; i++)
            {
                if (string.Equals(members[i].Key, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            throw new InvalidOperationException("the scanner did not find '" + id + "', which the parser did");
        }

        /// <summary>
        /// The top-level members of a document already known to be a valid JSON object: where each
        /// key and value starts and ends, and where the braces are.
        /// </summary>
        private static List<Member> Scan(string json, out int open, out int close)
        {
            var members = new List<Member>();
            var i = SkipWhitespace(json, 0);
            open = i;
            i = SkipWhitespace(json, i + 1);

            while (i < json.Length && json[i] != '}')
            {
                var keyStart = i;
                var keyEnd = SkipString(json, i);
                var key = (string)JsonReader.Parse(json.Substring(keyStart, keyEnd - keyStart))!;

                i = SkipWhitespace(json, keyEnd);
                i = SkipWhitespace(json, i + 1); // the ':'
                var valueStart = i;
                var valueEnd = SkipValue(json, i);
                members.Add(new Member(key, keyStart, keyEnd, valueStart, valueEnd));

                i = SkipWhitespace(json, valueEnd);
                if (i < json.Length && json[i] == ',')
                {
                    i = SkipWhitespace(json, i + 1);
                }
            }

            close = i;
            return members;
        }

        private static int SkipWhitespace(string json, int i)
        {
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r' || json[i] == '﻿'))
            {
                i++;
            }

            return i;
        }

        /// <summary>The index just past the string literal starting at <paramref name="i"/>.</summary>
        private static int SkipString(string json, int i)
        {
            i++;
            while (i < json.Length)
            {
                var c = json[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                i++;
                if (c == '"')
                {
                    return i;
                }
            }

            return i;
        }

        /// <summary>The index just past the value starting at <paramref name="i"/> (any JSON value).</summary>
        private static int SkipValue(string json, int i)
        {
            if (json[i] == '"')
            {
                return SkipString(json, i);
            }

            if (json[i] == '{' || json[i] == '[')
            {
                var depth = 0;
                while (i < json.Length)
                {
                    var c = json[i];
                    if (c == '"')
                    {
                        i = SkipString(json, i);
                        continue;
                    }

                    if (c == '{' || c == '[')
                    {
                        depth++;
                    }
                    else if (c == '}' || c == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return i + 1;
                        }
                    }

                    i++;
                }

                return i;
            }

            // A number, true, false or null: up to the next separator.
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']'
                   && json[i] != ' ' && json[i] != '\t' && json[i] != '\n' && json[i] != '\r')
            {
                i++;
            }

            return i;
        }

        private readonly struct Member
        {
            public Member(string key, int keyStart, int keyEnd, int valueStart, int valueEnd)
            {
                Key = key;
                KeyStart = keyStart;
                KeyEnd = keyEnd;
                ValueStart = valueStart;
                ValueEnd = valueEnd;
            }

            public string Key { get; }

            public int KeyStart { get; }

            public int KeyEnd { get; }

            public int ValueStart { get; }

            public int ValueEnd { get; }
        }
    }
}
