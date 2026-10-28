using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Json
{
    /// <summary>
    /// Reads one value out of a <see cref="JsonReader"/> graph with a dotted path such as
    /// <c>"dependsOn[0].id"</c>. Deliberately tiny: member names, array indices, and nothing else -
    /// no wildcards, no filters, no root <c>$</c>. It exists so pack code reads a nested value in
    /// one line instead of five casts, not to be a query language.
    /// <para>
    /// A path that does not exist is <em>not</em> an error: <see cref="Get"/> returns null and
    /// <see cref="TryGet"/> returns false. Only a malformed path throws, because that is our bug,
    /// not the modder's.
    /// </para>
    /// </summary>
    public static class JsonPath
    {
        /// <summary>
        /// The value at <paramref name="path"/>, or null when any step is missing. A stored JSON
        /// <c>null</c> is indistinguishable from "missing" here; use <see cref="TryGet"/> when the
        /// difference matters.
        /// </summary>
        /// <param name="root">The graph, normally a <see cref="JsonObject"/>.</param>
        /// <param name="path">Dotted path, e.g. <c>a.b[0].c</c>. An empty path returns <paramref name="root"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="FormatException"><paramref name="path"/> is malformed.</exception>
        public static object? Get(object? root, string path)
        {
            TryGet(root, path, out var value);
            return value;
        }

        /// <summary>
        /// Reads <paramref name="path"/>, distinguishing "absent" (false) from "present and null"
        /// (true with a null <paramref name="value"/>).
        /// </summary>
        /// <param name="root">The graph, normally a <see cref="JsonObject"/>.</param>
        /// <param name="path">Dotted path, e.g. <c>a.b[0].c</c>.</param>
        /// <param name="value">The value found, or null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="FormatException"><paramref name="path"/> is malformed.</exception>
        public static bool TryGet(object? root, string path, out object? value)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            value = root;
            if (path.Length == 0)
            {
                return true;
            }

            // The whole path is scanned even after a step comes up empty, so that a malformed path
            // throws whether or not the data happened to contain its first segment. Anything less
            // makes a typo in our own code look like missing content in someone's pack.
            var found = true;
            var i = 0;
            while (i < path.Length)
            {
                if (path[i] == '[')
                {
                    i++;
                    var start = i;
                    while (i < path.Length && path[i] != ']')
                    {
                        i++;
                    }

                    if (i >= path.Length)
                    {
                        throw new FormatException("unclosed '[' in JSON path \"" + path + "\"");
                    }

                    var indexText = path.Substring(start, i - start);
                    i++; // ']'

                    if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    {
                        throw new FormatException(
                            "\"" + indexText + "\" is not an array index in JSON path \"" + path + "\"");
                    }

                    if (found)
                    {
                        if (!(value is IList<object?> array) || index >= array.Count)
                        {
                            value = null;
                            found = false;
                        }
                        else
                        {
                            value = array[index];
                        }
                    }

                    continue;
                }

                if (path[i] == '.')
                {
                    i++;
                    if (i >= path.Length || path[i] == '.' || path[i] == '[')
                    {
                        throw new FormatException("empty member name in JSON path \"" + path + "\"");
                    }

                    continue;
                }

                var nameStart = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                {
                    i++;
                }

                if (!found)
                {
                    continue;
                }

                var name = path.Substring(nameStart, i - nameStart);
                if (!(value is IDictionary<string, object?> obj) || !obj.TryGetValue(name, out value))
                {
                    value = null;
                    found = false;
                }
            }

            return found;
        }

        /// <summary>The string at <paramref name="path"/>, or null when it is absent or not a string.</summary>
        /// <param name="root">The graph.</param>
        /// <param name="path">Dotted path.</param>
        public static string? GetString(object? root, string path) => Get(root, path) as string;
    }
}
