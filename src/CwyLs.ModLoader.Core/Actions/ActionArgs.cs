using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// The arguments of one action call: names (case-insensitive) to JSON-shaped values - text,
    /// 64-bit whole numbers, doubles, booleans or null. Immutable. Built from parsed JSON (the API),
    /// from console words (<c>title=Rain</c>, all text) or by a mod; the typed getters parse text
    /// in invariant culture so every caller gets the same answer.
    /// </summary>
    public sealed class ActionArgs : IReadOnlyDictionary<string, object?>
    {
        private readonly Dictionary<string, object?> _values;
        private readonly List<string> _order;

        private ActionArgs(Dictionary<string, object?> values, List<string> order)
        {
            _values = values;
            _order = order;
        }

        /// <summary>No arguments.</summary>
        public static ActionArgs Empty { get; } =
            new ActionArgs(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), new List<string>());

        /// <summary>How many arguments there are.</summary>
        public int Count => _order.Count;

        /// <summary>The names, in the order given.</summary>
        public IEnumerable<string> Keys => _order;

        /// <summary>The values, in the order given.</summary>
        public IEnumerable<object?> Values
        {
            get
            {
                foreach (var key in _order)
                {
                    yield return _values[key];
                }
            }
        }

        /// <summary>The value of an argument.</summary>
        /// <param name="key">The name (case-insensitive).</param>
        /// <exception cref="KeyNotFoundException">There is no such argument.</exception>
        public object? this[string key] => _values[key];

        /// <summary>Builds arguments from pairs.</summary>
        /// <param name="pairs">Name/value pairs; null means none.</param>
        /// <returns>The arguments.</returns>
        /// <exception cref="ArgumentException">A name is blank or repeats, or a value is not JSON-shaped.</exception>
        public static ActionArgs From(IEnumerable<KeyValuePair<string, object?>>? pairs)
        {
            if (pairs is null)
            {
                return Empty;
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var pair in pairs)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new ArgumentException("an argument name is blank", nameof(pairs));
                }

                if (values.ContainsKey(pair.Key))
                {
                    throw new ArgumentException("the argument \"" + pair.Key + "\" appears twice", nameof(pairs));
                }

                values.Add(pair.Key, Normalise(pair.Key, pair.Value));
                order.Add(pair.Key);
            }

            return order.Count == 0 ? Empty : new ActionArgs(values, order);
        }

        /// <summary>Builds arguments from tuples: <c>ActionArgs.Of(("title", "Rain"), ("volume", 0.5))</c>.</summary>
        /// <param name="pairs">Name/value tuples.</param>
        /// <returns>The arguments.</returns>
        /// <exception cref="ArgumentException">As for <see cref="From"/>.</exception>
        public static ActionArgs Of(params (string Name, object? Value)[] pairs)
        {
            var list = new List<KeyValuePair<string, object?>>(pairs?.Length ?? 0);
            if (pairs != null)
            {
                foreach (var (name, value) in pairs)
                {
                    list.Add(new KeyValuePair<string, object?>(name, value));
                }
            }

            return From(list);
        }

        /// <summary>True when the argument was given (even as null).</summary>
        /// <param name="key">The name (case-insensitive).</param>
        public bool ContainsKey(string key) => key != null && _values.ContainsKey(key);

        /// <summary>The value of an argument, if given.</summary>
        /// <param name="key">The name (case-insensitive).</param>
        /// <param name="value">The value, or null.</param>
        public bool TryGetValue(string key, out object? value)
        {
            value = null;
            return key != null && _values.TryGetValue(key, out value);
        }

        /// <summary>The argument as text (numbers and booleans in invariant form), or null when absent or null.</summary>
        /// <param name="key">The name (case-insensitive).</param>
        public string? GetString(string key) =>
            TryGetValue(key, out var value) && value != null ? Text(value) : null;

        /// <summary>The argument as a whole number: a number without fraction, or text that parses (invariant).</summary>
        /// <param name="key">The name (case-insensitive).</param>
        /// <param name="value">The number, or 0.</param>
        public bool TryGetInt64(string key, out long value)
        {
            value = 0;
            if (!TryGetValue(key, out var raw) || raw is null)
            {
                return false;
            }

            switch (raw)
            {
                case long whole:
                    value = whole;
                    return true;
                case double real when Math.Abs(real % 1d) < double.Epsilon && real >= long.MinValue && real <= long.MaxValue:
                    value = (long)real;
                    return true;
                case string text:
                    return long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
                default:
                    return false;
            }
        }

        /// <summary>The argument as a number: any number, or text that parses (invariant).</summary>
        /// <param name="key">The name (case-insensitive).</param>
        /// <param name="value">The number, or 0.</param>
        public bool TryGetDouble(string key, out double value)
        {
            value = 0;
            if (!TryGetValue(key, out var raw) || raw is null)
            {
                return false;
            }

            switch (raw)
            {
                case long whole:
                    value = whole;
                    return true;
                case double real:
                    value = real;
                    return true;
                case string text:
                    return double.TryParse(
                               text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                           && !double.IsNaN(value) && !double.IsInfinity(value);
                default:
                    return false;
            }
        }

        /// <summary>The argument as a boolean: <c>true</c>/<c>false</c>, or the text <c>true</c>, <c>false</c>, <c>1</c>, <c>0</c> (case-insensitive).</summary>
        /// <param name="key">The name (case-insensitive).</param>
        /// <param name="value">The boolean, or false.</param>
        public bool TryGetBoolean(string key, out bool value)
        {
            value = false;
            if (!TryGetValue(key, out var raw) || raw is null)
            {
                return false;
            }

            switch (raw)
            {
                case bool flag:
                    value = flag;
                    return true;
                case string text:
                    var t = text.Trim();
                    if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase) || t == "1")
                    {
                        value = true;
                        return true;
                    }

                    return string.Equals(t, "false", StringComparison.OrdinalIgnoreCase) || t == "0";
                default:
                    return false;
            }
        }

        /// <summary>Enumerates the arguments in the order given.</summary>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (var key in _order)
            {
                yield return new KeyValuePair<string, object?>(key, _values[key]);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary><c>title=Rain volume=0.5</c>.</summary>
        public override string ToString()
        {
            var parts = new List<string>(_order.Count);
            foreach (var key in _order)
            {
                var value = _values[key];
                parts.Add(key + "=" + (value is null ? "null" : Text(value)));
            }

            return string.Join(" ", parts);
        }

        private static object? Normalise(string key, object? value)
        {
            switch (value)
            {
                case null:
                case string _:
                case bool _:
                case long _:
                case double _:
                    return value;
                case int i: return (long)i;
                case short s: return (long)s;
                case byte b: return (long)b;
                case sbyte sb: return (long)sb;
                case ushort us: return (long)us;
                case uint ui: return (long)ui;
                case float f when !float.IsNaN(f) && !float.IsInfinity(f): return (double)f;
                case decimal m: return (double)m;
                default:
                    throw new ArgumentException(
                        "the argument \"" + key + "\" is a " + value.GetType().Name +
                        "; an action argument is text, a number, true/false or null",
                        nameof(value));
            }
        }

        private static string Text(object value)
        {
            switch (value)
            {
                case bool flag: return flag ? "true" : "false";
                case double real: return real.ToString("R", CultureInfo.InvariantCulture);
                default: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }
}
