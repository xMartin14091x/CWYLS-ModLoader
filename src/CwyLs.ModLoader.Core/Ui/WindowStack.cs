using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>
    /// The mod windows' order and which one the player is "in". Pure; the game adapter mirrors
    /// the order onto the overlay canvas (last sibling = on top).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A window becomes <see cref="Active"/> when it is shown or the player presses a mouse button
    /// over it, and stops being active when the player presses a button anywhere outside every CWY
    /// window (the room, the game's own UI), or when it is hidden or removed. The active window is
    /// the one an Escape with no text box selected belongs to (<see cref="EscapeRouting"/>), so
    /// clicking the room hands Escape back to the game.
    /// </para>
    /// <para>Not thread-safe: main thread only, like the windows it orders.</para>
    /// </remarks>
    public sealed class WindowStack
    {
        private readonly List<string> _order = new List<string>();

        /// <summary>The windows, bottom first.</summary>
        public IReadOnlyList<string> Order => _order;

        /// <summary>The window the player is in, or null.</summary>
        public string? Active { get; private set; }

        /// <summary>The window on top, or null.</summary>
        public string? Top => _order.Count == 0 ? null : _order[_order.Count - 1];

        /// <summary>Windows held.</summary>
        public int Count => _order.Count;

        /// <summary>Adds a window on top (not active: it is still hidden). Adding one twice does nothing.</summary>
        /// <param name="key">The window's key.</param>
        public void Add(string key)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (!_order.Contains(key))
            {
                _order.Add(key);
            }
        }

        /// <summary>Removes a window.</summary>
        /// <param name="key">The window's key.</param>
        /// <returns>True when it was there.</returns>
        public bool Remove(string key)
        {
            if (Active == key)
            {
                Active = null;
            }

            return _order.Remove(key);
        }

        /// <summary>Moves a window to the top.</summary>
        /// <param name="key">The window's key.</param>
        /// <returns>True when the order changed.</returns>
        public bool BringToFront(string key)
        {
            var index = _order.IndexOf(key);
            if (index < 0 || index == _order.Count - 1)
            {
                return false;
            }

            _order.RemoveAt(index);
            _order.Add(key);
            return true;
        }

        /// <summary>A window was shown: it goes on top and becomes active.</summary>
        /// <param name="key">The window's key.</param>
        public void Shown(string key) => Activate(key);

        /// <summary>A window was hidden: it is no longer active.</summary>
        /// <param name="key">The window's key.</param>
        public void Hidden(string key)
        {
            if (Active == key)
            {
                Active = null;
            }
        }

        /// <summary>
        /// The player pressed a mouse button. Over a mod window: it goes on top and becomes active.
        /// Over anything else (another CWY window such as the console, the game's UI, the room): no
        /// mod window is active.
        /// </summary>
        /// <param name="hitKey">The mod window under the pointer, or null.</param>
        /// <returns>True when the order changed.</returns>
        public bool PointerDown(string? hitKey)
        {
            if (hitKey is null || !_order.Contains(hitKey))
            {
                Active = null;
                return false;
            }

            return Activate(hitKey);
        }

        /// <summary>Puts a window on top and makes it active.</summary>
        /// <param name="key">The window's key.</param>
        /// <returns>True when the order changed.</returns>
        public bool Activate(string key)
        {
            if (!_order.Contains(key))
            {
                return false;
            }

            Active = key;
            return BringToFront(key);
        }

        /// <summary>No mod window is active.</summary>
        public void Deactivate() => Active = null;

        /// <summary>
        /// The top-most window for which <paramref name="hit"/> is true (the pointer is inside it and
        /// it takes clicks), or null.
        /// </summary>
        /// <param name="hit">The hit test.</param>
        public string? HitTest(Func<string, bool> hit)
        {
            if (hit is null)
            {
                throw new ArgumentNullException(nameof(hit));
            }

            for (var i = _order.Count - 1; i >= 0; i--)
            {
                if (hit(_order[i]))
                {
                    return _order[i];
                }
            }

            return null;
        }

        /// <summary>A window's place from the bottom (0) up, or -1.</summary>
        /// <param name="key">The window's key.</param>
        public int IndexOf(string key) => _order.IndexOf(key);
    }
}
