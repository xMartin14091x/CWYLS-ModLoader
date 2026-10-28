using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Json
{
    /// <summary>
    /// A strict, allocation-modest RFC 8259 JSON reader. The counterpart to
    /// <see cref="MiniJson"/>, which only writes.
    /// <para>
    /// It exists for the same reason MiniJson does: the plugins run on Unity's Mono profile, where
    /// <c>System.Text.Json</c> is absent, and borrowing the game's Newtonsoft.Json would tie pack
    /// parsing to the game install and make it untestable off-engine. Pack manifests are small and
    /// the failure mode that matters is a modder's typo, so the reader trades features for exact
    /// <c>line:column</c> diagnostics.
    /// </para>
    /// <para>The object graph is:</para>
    /// <list type="bullet">
    ///   <item><description>object -&gt; <see cref="JsonObject"/> (ordered <c>IDictionary&lt;string, object?&gt;</c>)</description></item>
    ///   <item><description>array -&gt; <c>List&lt;object?&gt;</c></description></item>
    ///   <item><description>string -&gt; <see cref="string"/></description></item>
    ///   <item><description>number -&gt; <see cref="long"/> when it is an integer that fits, otherwise <see cref="double"/></description></item>
    ///   <item><description>true/false -&gt; <see cref="bool"/>; null -&gt; <c>null</c></description></item>
    /// </list>
    /// <para>
    /// Strict means: no comments, no trailing commas, no single quotes, no unquoted keys, no
    /// <c>NaN</c>/<c>Infinity</c>, no leading zeros, and no duplicate keys in one object - a
    /// duplicate key in a manifest is a mistake we would otherwise silently resolve the wrong way.
    /// A leading UTF-8 BOM is skipped.
    /// </para>
    /// </summary>
    public static class JsonReader
    {
        /// <summary>
        /// Maximum nesting of objects and arrays. Deep input is a stack overflow (which no
        /// <c>catch</c> can contain) rather than an exception, so it is refused up front.
        /// </summary>
        public const int MaxDepth = 64;

        private const char Bom = '\uFEFF';
        private const char Delete = '\u007F';

        /// <summary>Parses a complete JSON document.</summary>
        /// <param name="json">The document. Must contain exactly one value.</param>
        /// <returns>The object graph; <c>null</c> for the document <c>null</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        /// <exception cref="JsonParseException">The document is not well-formed JSON.</exception>
        public static object? Parse(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var cursor = new Cursor(json);
            cursor.SkipWhitespace();
            if (cursor.AtEnd)
            {
                throw cursor.Error("the document is empty");
            }

            var value = ParseValue(cursor, 1);

            cursor.SkipWhitespace();
            if (!cursor.AtEnd)
            {
                throw cursor.Error("unexpected " + Describe(cursor.Current) + " after the top-level value");
            }

            return value;
        }

        /// <summary>
        /// Parses a document, reporting failure instead of throwing. <paramref name="error"/> is
        /// the exception message, which already begins with <c>line:column</c>.
        /// </summary>
        /// <param name="json">The document; null is reported as an error, not accepted.</param>
        /// <param name="value">The object graph, or null on failure.</param>
        /// <param name="error">The failure description, or null on success.</param>
        /// <returns>True when the document parsed.</returns>
        public static bool TryParse(string json, out object? value, out string? error)
        {
            try
            {
                value = Parse(json);
                error = null;
                return true;
            }
            catch (JsonParseException ex)
            {
                value = null;
                error = ex.Message;
                return false;
            }
            catch (ArgumentNullException ex)
            {
                // A caller that read a file which vanished mid-scan gets a readable reason rather
                // than a null-reference crash further down.
                value = null;
                error = "no JSON was supplied (" + ex.ParamName + ")";
                return false;
            }
        }

        /// <summary>Parses a document that must be a JSON object.</summary>
        /// <param name="json">The document.</param>
        /// <exception cref="JsonParseException">Not well-formed, or the top-level value is not an object.</exception>
        public static JsonObject ParseObject(string json)
        {
            var value = Parse(json);
            if (value is JsonObject obj)
            {
                return obj;
            }

            throw new JsonParseException("the top-level value must be an object, not " + TypeName(value), 1, 1);
        }

        /// <summary>The JSON type name of a parsed value, for error messages.</summary>
        /// <param name="value">Any node of a parsed graph.</param>
        public static string TypeName(object? value)
        {
            switch (value)
            {
                case null: return "null";
                case JsonObject _: return "an object";
                case IList<object?> _: return "an array";
                case string _: return "a string";
                case bool _: return "a boolean";
                case long _:
                case double _: return "a number";
                default: return value.GetType().Name;
            }
        }

        private static object? ParseValue(Cursor cursor, int depth)
        {
            var c = cursor.Current;
            switch (c)
            {
                case '{': return ParseObject(cursor, depth);
                case '[': return ParseArray(cursor, depth);
                case '"': return ParseString(cursor);
                case 't': return ParseLiteral(cursor, "true", true);
                case 'f': return ParseLiteral(cursor, "false", false);
                case 'n': return ParseLiteral(cursor, "null", null);
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        return ParseNumber(cursor);
                    }

                    throw cursor.Error("expected a value but found " + Describe(c));
            }
        }

        private static JsonObject ParseObject(Cursor cursor, int depth)
        {
            RequireDepth(cursor, depth);
            cursor.Advance(); // '{'

            var result = new JsonObject();
            cursor.SkipWhitespace();
            if (cursor.AtEnd)
            {
                throw cursor.Error("the object is not closed (expected '}')");
            }

            if (cursor.Current == '}')
            {
                cursor.Advance();
                return result;
            }

            while (true)
            {
                cursor.SkipWhitespace();
                if (cursor.AtEnd)
                {
                    throw cursor.Error("the object is not closed (expected a member name)");
                }

                if (cursor.Current != '"')
                {
                    throw cursor.Error("expected a quoted member name but found " + Describe(cursor.Current));
                }

                var nameLine = cursor.Line;
                var nameColumn = cursor.Column;
                var name = ParseString(cursor);

                cursor.SkipWhitespace();
                if (cursor.AtEnd || cursor.Current != ':')
                {
                    throw cursor.Error("expected ':' after the member name \"" + name + "\"");
                }

                cursor.Advance(); // ':'
                cursor.SkipWhitespace();
                if (cursor.AtEnd)
                {
                    throw cursor.Error("expected a value for \"" + name + "\"");
                }

                var value = ParseValue(cursor, depth + 1);

                if (result.ContainsKey(name))
                {
                    throw new JsonParseException("duplicate member name \"" + name + "\"", nameLine, nameColumn);
                }

                result.Add(name, value);

                cursor.SkipWhitespace();
                if (cursor.AtEnd)
                {
                    throw cursor.Error("the object is not closed (expected ',' or '}')");
                }

                if (cursor.Current == ',')
                {
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current == '}')
                {
                    cursor.Advance();
                    return result;
                }

                throw cursor.Error("expected ',' or '}' but found " + Describe(cursor.Current));
            }
        }

        private static List<object?> ParseArray(Cursor cursor, int depth)
        {
            RequireDepth(cursor, depth);
            cursor.Advance(); // '['

            var result = new List<object?>();
            cursor.SkipWhitespace();
            if (cursor.AtEnd)
            {
                throw cursor.Error("the array is not closed (expected ']')");
            }

            if (cursor.Current == ']')
            {
                cursor.Advance();
                return result;
            }

            while (true)
            {
                cursor.SkipWhitespace();
                if (cursor.AtEnd)
                {
                    throw cursor.Error("the array is not closed (expected a value)");
                }

                result.Add(ParseValue(cursor, depth + 1));

                cursor.SkipWhitespace();
                if (cursor.AtEnd)
                {
                    throw cursor.Error("the array is not closed (expected ',' or ']')");
                }

                if (cursor.Current == ',')
                {
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current == ']')
                {
                    cursor.Advance();
                    return result;
                }

                throw cursor.Error("expected ',' or ']' but found " + Describe(cursor.Current));
            }
        }

        private static string ParseString(Cursor cursor)
        {
            cursor.Advance(); // opening quote

            var sb = new StringBuilder();
            while (true)
            {
                if (cursor.AtEnd)
                {
                    throw cursor.Error("the string is not closed (expected '\"')");
                }

                var c = cursor.Current;
                if (c == '"')
                {
                    cursor.Advance();
                    return sb.ToString();
                }

                if (c == '\\')
                {
                    cursor.Advance();
                    sb.Append(ParseEscape(cursor));
                    continue;
                }

                if (c < ' ')
                {
                    throw cursor.Error(
                        "a raw control character (U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) +
                        ") must be escaped inside a string");
                }

                // Characters written literally - including a surrogate pair typed as itself - pass
                // through as the UTF-16 units they already are, which is what a .NET string holds.
                sb.Append(c);
                cursor.Advance();
            }
        }

        private static string ParseEscape(Cursor cursor)
        {
            if (cursor.AtEnd)
            {
                throw cursor.Error("the string ends with an unfinished escape");
            }

            var c = cursor.Current;
            cursor.Advance();
            switch (c)
            {
                case '"': return "\"";
                case '\\': return "\\";
                case '/': return "/";
                case 'b': return "\b";
                case 'f': return "\f";
                case 'n': return "\n";
                case 'r': return "\r";
                case 't': return "\t";
                case 'u': return ParseUnicodeEscape(cursor).ToString();
                default:
                    throw cursor.Error("unknown escape '\\" + c + "'");
            }
        }

        private static char ParseUnicodeEscape(Cursor cursor)
        {
            var value = 0;
            for (var i = 0; i < 4; i++)
            {
                if (cursor.AtEnd)
                {
                    throw cursor.Error("\\u needs four hex digits");
                }

                var digit = HexValue(cursor.Current);
                if (digit < 0)
                {
                    throw cursor.Error("\\u needs four hex digits but found " + Describe(cursor.Current));
                }

                value = (value << 4) | digit;
                cursor.Advance();
            }

            // A surrogate pair is two consecutive \u escapes; appending each UTF-16 unit in turn
            // reproduces the astral character exactly, so no pairing logic is needed here.
            return (char)value;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') { return c - '0'; }
            if (c >= 'a' && c <= 'f') { return c - 'a' + 10; }
            if (c >= 'A' && c <= 'F') { return c - 'A' + 10; }
            return -1;
        }

        private static object? ParseLiteral(Cursor cursor, string literal, object? value)
        {
            for (var i = 0; i < literal.Length; i++)
            {
                if (cursor.AtEnd || cursor.Current != literal[i])
                {
                    throw cursor.Error("expected '" + literal + "'");
                }

                cursor.Advance();
            }

            return value;
        }

        private static object ParseNumber(Cursor cursor)
        {
            var startLine = cursor.Line;
            var startColumn = cursor.Column;
            var start = cursor.Index;

            if (cursor.Current == '-')
            {
                cursor.Advance();
            }

            if (cursor.AtEnd)
            {
                throw cursor.Error("the number has no digits");
            }

            if (cursor.Current == '0')
            {
                cursor.Advance();
                if (!cursor.AtEnd && IsDigit(cursor.Current))
                {
                    throw cursor.Error("a number may not have a leading zero");
                }
            }
            else
            {
                if (!IsDigit(cursor.Current))
                {
                    throw cursor.Error("expected a digit but found " + Describe(cursor.Current));
                }

                while (!cursor.AtEnd && IsDigit(cursor.Current))
                {
                    cursor.Advance();
                }
            }

            var integral = true;

            if (!cursor.AtEnd && cursor.Current == '.')
            {
                integral = false;
                cursor.Advance();
                if (cursor.AtEnd || !IsDigit(cursor.Current))
                {
                    throw cursor.Error("the fraction needs at least one digit");
                }

                while (!cursor.AtEnd && IsDigit(cursor.Current))
                {
                    cursor.Advance();
                }
            }

            if (!cursor.AtEnd && (cursor.Current == 'e' || cursor.Current == 'E'))
            {
                integral = false;
                cursor.Advance();
                if (!cursor.AtEnd && (cursor.Current == '+' || cursor.Current == '-'))
                {
                    cursor.Advance();
                }

                if (cursor.AtEnd || !IsDigit(cursor.Current))
                {
                    throw cursor.Error("the exponent needs at least one digit");
                }

                while (!cursor.AtEnd && IsDigit(cursor.Current))
                {
                    cursor.Advance();
                }
            }

            var text = cursor.Slice(start);

            if (integral &&
                long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var asLong))
            {
                return asLong;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble))
            {
                return asDouble;
            }

            // Reached only by magnitudes double cannot hold. Mono and .NET disagree on whether
            // TryParse saturates to infinity or fails there, so both paths end in this one error.
            throw new JsonParseException("the number " + text + " cannot be represented", startLine, startColumn);
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static void RequireDepth(Cursor cursor, int depth)
        {
            if (depth > MaxDepth)
            {
                throw cursor.Error("nesting deeper than " + MaxDepth + " levels is refused");
            }
        }

        private static string Describe(char c) =>
            c < ' ' || c == Delete
                ? "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
                : "'" + c + "'";

        /// <summary>Position-tracking read head over the document.</summary>
        private sealed class Cursor
        {
            private readonly string _text;

            internal Cursor(string text)
            {
                _text = text;
                Index = text.Length > 0 && text[0] == Bom ? 1 : 0;
                Line = 1;
                Column = 1;
            }

            internal int Index { get; private set; }

            internal int Line { get; private set; }

            internal int Column { get; private set; }

            internal bool AtEnd => Index >= _text.Length;

            /// <summary>The character under the head. Only valid while <see cref="AtEnd"/> is false.</summary>
            internal char Current => _text[Index];

            internal void Advance()
            {
                if (AtEnd)
                {
                    return;
                }

                if (_text[Index] == '\n')
                {
                    Line++;
                    Column = 1;
                }
                else
                {
                    Column++;
                }

                Index++;
            }

            internal void SkipWhitespace()
            {
                while (!AtEnd)
                {
                    var c = _text[Index];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    {
                        Advance();
                        continue;
                    }

                    return;
                }
            }

            internal string Slice(int from) => _text.Substring(from, Index - from);

            /// <summary>An exception pointing at the current position.</summary>
            internal JsonParseException Error(string problem) => new JsonParseException(problem, Line, Column);
        }
    }
}
