using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>
    /// Where the player last left each mod window, by <c>&lt;modId&gt;/&lt;windowId&gt;</c>. Pure:
    /// the adapter reads and writes the file (<c>BepInEx/data/ui/windows.json</c>).
    /// </summary>
    /// <remarks>
    /// <para>The file:</para>
    /// <code>
    /// { "schema": 1, "windows": { "my.mod/chat": { "left": 1210, "top": 80, "width": 640, "height": 720 } } }
    /// </code>
    /// <para>
    /// Values are canvas units at the game's 1920x1080 reference scaling. A remembered place is
    /// clamped onto the canvas when it is used (<see cref="WindowRectMath.Clamp"/>), so a smaller
    /// window or another aspect ratio never puts a window off screen. An entry that does not parse is
    /// skipped and named in the problem text; the rest still load.
    /// </para>
    /// </remarks>
    public sealed class WindowRectStore
    {
        /// <summary>The file's schema number.</summary>
        public const int Schema = 1;

        /// <summary>Most windows remembered; the file is cleared of the rest on the next save.</summary>
        public const int MaxEntries = 256;

        /// <summary>Largest coordinate or size accepted (canvas units).</summary>
        public const float MaxCoordinate = 100000f;

        private readonly SortedDictionary<string, WindowRect> _rects = new SortedDictionary<string, WindowRect>(StringComparer.Ordinal);

        /// <summary>Windows remembered.</summary>
        public int Count => _rects.Count;

        /// <summary>True when something changed since the last <see cref="ToJson"/>.</summary>
        public bool IsDirty { get; private set; }

        /// <summary>The keys, sorted.</summary>
        public IReadOnlyList<string> Keys => new List<string>(_rects.Keys);

        /// <summary>Reads a store from the file's text.</summary>
        /// <param name="json">The file's text; null or blank is an empty store.</param>
        /// <param name="problem">Empty when everything loaded; otherwise what was skipped and why.</param>
        public static WindowRectStore Parse(string? json, out string problem)
        {
            var store = new WindowRectStore();
            problem = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                return store;
            }

            if (!JsonReader.TryParse(json!, out var root, out var error))
            {
                problem = "not valid JSON: " + error;
                return store;
            }

            if (!(root is IDictionary<string, object?> top))
            {
                problem = "the file is not a JSON object";
                return store;
            }

            if (!top.TryGetValue("windows", out var windowsValue) || !(windowsValue is IDictionary<string, object?> windows))
            {
                problem = "no \"windows\" object";
                return store;
            }

            var skipped = new List<string>();
            foreach (var entry in windows)
            {
                if (store._rects.Count >= MaxEntries)
                {
                    skipped.Add(entry.Key + " (over " + MaxEntries + " entries)");
                    continue;
                }

                if (TryReadRect(entry.Value, out var rect))
                {
                    store._rects[entry.Key] = rect;
                }
                else
                {
                    skipped.Add(entry.Key);
                }
            }

            if (skipped.Count > 0)
            {
                problem = "skipped " + skipped.Count + " unreadable entr" + (skipped.Count == 1 ? "y" : "ies") + ": " + string.Join(", ", skipped);
            }

            return store;
        }

        /// <summary>The remembered place of a window.</summary>
        /// <param name="key">The window's key.</param>
        /// <param name="rect">The place.</param>
        public bool TryGet(string key, out WindowRect rect) => _rects.TryGetValue(key ?? string.Empty, out rect);

        /// <summary>Remembers a window's place.</summary>
        /// <param name="key">The window's key.</param>
        /// <param name="rect">The place.</param>
        /// <returns>False when the place is not a usable rectangle (it is not stored).</returns>
        public bool Set(string key, WindowRect rect)
        {
            if (string.IsNullOrEmpty(key) || !IsUsable(rect))
            {
                return false;
            }

            if (_rects.TryGetValue(key, out var old) && old.Equals(rect))
            {
                return true;
            }

            if (!_rects.ContainsKey(key) && _rects.Count >= MaxEntries)
            {
                return false;
            }

            _rects[key] = rect;
            IsDirty = true;
            return true;
        }

        /// <summary>Forgets a window's place.</summary>
        /// <param name="key">The window's key.</param>
        /// <returns>True when it was remembered.</returns>
        public bool Remove(string key)
        {
            var removed = _rects.Remove(key ?? string.Empty);
            IsDirty |= removed;
            return removed;
        }

        /// <summary>Forgets every place.</summary>
        /// <returns>How many were forgotten.</returns>
        public int Clear()
        {
            var count = _rects.Count;
            _rects.Clear();
            IsDirty |= count > 0;
            return count;
        }

        /// <summary>The file's text, sorted by key; clears <see cref="IsDirty"/>.</summary>
        public string ToJson()
        {
            var windows = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var entry in _rects)
            {
                windows[entry.Key] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["left"] = Round(entry.Value.Left),
                    ["top"] = Round(entry.Value.Top),
                    ["width"] = Round(entry.Value.Width),
                    ["height"] = Round(entry.Value.Height),
                };
            }

            var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schema"] = Schema,
                ["windows"] = windows,
            };

            IsDirty = false;
            return JsonWriter.Write(root);
        }

        /// <summary>True for a finite rectangle with a positive size inside the accepted range.</summary>
        /// <param name="rect">The rectangle.</param>
        public static bool IsUsable(WindowRect rect) =>
            Finite(rect.Left) && Finite(rect.Top) && Finite(rect.Width) && Finite(rect.Height)
            && rect.Width > 0f && rect.Height > 0f
            && Math.Abs(rect.Left) <= MaxCoordinate && Math.Abs(rect.Top) <= MaxCoordinate
            && rect.Width <= MaxCoordinate && rect.Height <= MaxCoordinate;

        private static bool TryReadRect(object? value, out WindowRect rect)
        {
            rect = default;
            if (!(value is IDictionary<string, object?> fields))
            {
                return false;
            }

            if (!TryNumber(fields, "left", out var left) || !TryNumber(fields, "top", out var top)
                || !TryNumber(fields, "width", out var width) || !TryNumber(fields, "height", out var height))
            {
                return false;
            }

            rect = new WindowRect(left, top, width, height);
            return IsUsable(rect);
        }

        private static bool TryNumber(IDictionary<string, object?> fields, string name, out float number)
        {
            number = 0f;
            if (!fields.TryGetValue(name, out var value))
            {
                return false;
            }

            switch (value)
            {
                case long l:
                    number = l;
                    return true;
                case double d:
                    number = (float)d;
                    return !double.IsNaN(d) && !double.IsInfinity(d);
                default:
                    return false;
            }
        }

        private static double Round(float value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>Where a mod window opens when nothing is remembered. Pure.</summary>
    public static class WindowPlacement
    {
        /// <summary>
        /// The first place of a window: its preferred size (or the default share of the canvas when
        /// a preferred dimension is 0), horizontally centred, a quarter of the free height down, kept
        /// on the canvas.
        /// </summary>
        /// <param name="canvasWidth">Canvas width.</param>
        /// <param name="canvasHeight">Canvas height.</param>
        /// <param name="preferredWidth">Preferred width, or 0.</param>
        /// <param name="preferredHeight">Preferred height, or 0.</param>
        /// <param name="minWidth">Smallest width.</param>
        /// <param name="minHeight">Smallest height.</param>
        public static WindowRect Default(
            float canvasWidth,
            float canvasHeight,
            float preferredWidth,
            float preferredHeight,
            float minWidth,
            float minHeight)
        {
            var fallback = WindowRectMath.Default(canvasWidth, canvasHeight, minWidth, minHeight);
            if (preferredWidth <= 0f && preferredHeight <= 0f)
            {
                return fallback;
            }

            var width = preferredWidth > 0f ? Math.Max(minWidth, preferredWidth) : fallback.Width;
            var height = preferredHeight > 0f ? Math.Max(minHeight, preferredHeight) : fallback.Height;
            width = Math.Min(width, Math.Max(minWidth, canvasWidth - (WindowRectMath.DefaultMargin * 2f)));
            height = Math.Min(height, Math.Max(minHeight, canvasHeight - (WindowRectMath.DefaultMargin * 2f)));

            var left = (canvasWidth - width) * 0.5f;
            var top = Math.Max(WindowRectMath.DefaultMargin, (canvasHeight - height) * 0.25f);
            return WindowRectMath.Clamp(new WindowRect(left, top, width, height), canvasWidth, canvasHeight, minWidth, minHeight);
        }

        /// <summary>A remembered place kept on the current canvas and above the minimum size.</summary>
        /// <param name="remembered">The remembered place.</param>
        /// <param name="canvasWidth">Canvas width.</param>
        /// <param name="canvasHeight">Canvas height.</param>
        /// <param name="minWidth">Smallest width.</param>
        /// <param name="minHeight">Smallest height.</param>
        public static WindowRect Restore(WindowRect remembered, float canvasWidth, float canvasHeight, float minWidth, float minHeight) =>
            WindowRectMath.Clamp(remembered, canvasWidth, canvasHeight, minWidth, minHeight);
    }
}
