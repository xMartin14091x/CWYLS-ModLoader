using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core
{
    /// <summary>
    /// A deliberately tiny JSON <em>writer</em>. The plugins run on Unity's Mono profile where
    /// System.Text.Json does not exist; Newtonsoft.Json ships with the game, but pulling a game
    /// assembly in just to emit a status file would tie this logic to the game install and make
    /// it untestable off-Unity. Writing a few dozen bytes of JSON by hand is cheaper than either.
    /// <para>
    /// Reading is the other half and lives in <see cref="Json.JsonReader"/> - also written
    /// here rather than borrowed from the game, for the same reason: nothing in Core may need
    /// Unity or the game install to be unit-tested. Pack manifests are parsed by that reader, not
    /// by Newtonsoft.
    /// </para>
    /// </summary>
    public static class MiniJson
    {
        private const char Delete = (char)0x7F;

        /// <summary>Escapes the body of a JSON string (no surrounding quotes).</summary>
        public static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value!.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == Delete)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>A JSON string literal, quotes included. <c>null</c> becomes the literal <c>null</c>.</summary>
        public static string String(string? value) =>
            value is null ? "null" : "\"" + Escape(value) + "\"";

        public static string Bool(bool value) => value ? "true" : "false";

        public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>ISO-8601 with an explicit offset, e.g. <c>2026-09-22T13:45:01.123+09:00</c>.</summary>
        public static string Iso8601(DateTimeOffset when) =>
            when.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

        /// <summary>
        /// Composes an object from members whose values are already JSON fragments
        /// (build them with <see cref="String"/>, <see cref="Bool"/> or <see cref="Number(long)"/>).
        /// </summary>
        public static string Object(IEnumerable<KeyValuePair<string, string>> members)
        {
            if (members is null)
            {
                throw new ArgumentNullException(nameof(members));
            }

            var sb = new StringBuilder("{");
            var first = true;
            foreach (var member in members)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append(String(member.Key)).Append(':').Append(member.Value);
            }

            return sb.Append('}').ToString();
        }

        public static string Object(params KeyValuePair<string, string>[] members) =>
            Object((IEnumerable<KeyValuePair<string, string>>)members);

        /// <summary>Shorthand for one object member; <paramref name="jsonValue"/> is a JSON fragment.</summary>
        public static KeyValuePair<string, string> Member(string key, string jsonValue) =>
            new KeyValuePair<string, string>(key, jsonValue);
    }
}
