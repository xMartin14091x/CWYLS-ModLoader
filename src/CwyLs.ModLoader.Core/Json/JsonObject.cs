using System;
using System.Collections;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Json
{
    /// <summary>
    /// A parsed JSON object: a string-keyed map that enumerates in <em>document order</em>.
    /// <para>
    /// <see cref="Dictionary{TKey,TValue}"/> is not used directly because its enumeration order is
    /// explicitly undefined; pack manifests and, later, master-data rows are diffed and re-emitted,
    /// and a reordered file is an unreadable diff. This type keeps the insertion order in a list
    /// beside the hash index, so lookup stays O(1) and enumeration stays stable.
    /// </para>
    /// Keys are compared with <see cref="StringComparer.Ordinal"/>: JSON keys are bytes, not text
    /// in a culture, and <c>"İ"</c> must never fold onto <c>"i"</c> on a Turkish machine.
    /// </summary>
    public sealed class JsonObject : IDictionary<string, object?>
    {
        private readonly List<string> _order;
        private readonly Dictionary<string, object?> _values;

        /// <summary>Creates an empty object.</summary>
        public JsonObject()
        {
            _order = new List<string>();
            _values = new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        /// <summary>Creates an empty object sized for <paramref name="capacity"/> members.</summary>
        /// <param name="capacity">Expected member count; a hint only.</param>
        public JsonObject(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity cannot be negative");
            }

            _order = new List<string>(capacity);
            _values = new Dictionary<string, object?>(capacity, StringComparer.Ordinal);
        }

        /// <summary>Member count.</summary>
        public int Count => _order.Count;

        /// <summary>Always false; a parsed object is a normal mutable map.</summary>
        public bool IsReadOnly => false;

        /// <summary>The keys, in document order.</summary>
        public ICollection<string> Keys => _order.ToArray();

        /// <summary>The values, in document order.</summary>
        public ICollection<object?> Values
        {
            get
            {
                var values = new object?[_order.Count];
                for (var i = 0; i < _order.Count; i++)
                {
                    values[i] = _values[_order[i]];
                }

                return values;
            }
        }

        /// <summary>
        /// Gets or sets a member. Setting an unknown key appends it; setting a known key replaces
        /// the value and keeps the key's original position.
        /// </summary>
        /// <exception cref="KeyNotFoundException">Reading a key that is not present.</exception>
        public object? this[string key]
        {
            get => _values[key ?? throw new ArgumentNullException(nameof(key))];
            set
            {
                if (key is null)
                {
                    throw new ArgumentNullException(nameof(key));
                }

                if (!_values.ContainsKey(key))
                {
                    _order.Add(key);
                }

                _values[key] = value;
            }
        }

        /// <summary>Appends a member.</summary>
        /// <exception cref="ArgumentException"><paramref name="key"/> is already present.</exception>
        public void Add(string key, object? value)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            _values.Add(key, value);
            _order.Add(key);
        }

        /// <inheritdoc />
        public void Add(KeyValuePair<string, object?> item) => Add(item.Key, item.Value);

        /// <summary>True when <paramref name="key"/> is present.</summary>
        public bool ContainsKey(string key) =>
            key != null && _values.ContainsKey(key);

        /// <inheritdoc />
        public bool Contains(KeyValuePair<string, object?> item) =>
            TryGetValue(item.Key, out var found) && Equals(found, item.Value);

        /// <summary>Reads a member; returns false when it is absent.</summary>
        public bool TryGetValue(string key, out object? value)
        {
            if (key is null)
            {
                value = null;
                return false;
            }

            return _values.TryGetValue(key, out value);
        }

        /// <summary>Removes a member; returns false when it was absent.</summary>
        public bool Remove(string key)
        {
            if (key is null || !_values.Remove(key))
            {
                return false;
            }

            _order.Remove(key);
            return true;
        }

        /// <inheritdoc />
        public bool Remove(KeyValuePair<string, object?> item) =>
            Contains(item) && Remove(item.Key);

        /// <summary>Removes every member.</summary>
        public void Clear()
        {
            _order.Clear();
            _values.Clear();
        }

        /// <inheritdoc />
        public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex)
        {
            if (array is null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            if (arrayIndex < 0 || array.Length - arrayIndex < _order.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex), arrayIndex, "the target array is too small");
            }

            foreach (var pair in this)
            {
                array[arrayIndex++] = pair;
            }
        }

        /// <summary>Enumerates the members in document order.</summary>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (var key in _order)
            {
                yield return new KeyValuePair<string, object?>(key, _values[key]);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc />
        public override string ToString() => "{" + string.Join(", ", _order.ToArray()) + "}";
    }
}
