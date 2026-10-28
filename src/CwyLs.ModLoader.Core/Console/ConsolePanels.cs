using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// The extra tabs the in-game console shows. A panel is a name and a function that returns
    /// the text to draw; the console calls it once per frame while its tab is open, so it must be
    /// cheap and must not block.
    /// <para>
    /// Process-wide for the same reason as the command registry: a mod lives in its own BepInEx
    /// assembly with no reference to the console's. The game adapter registers "Stream" and
    /// "State" this way, which is what keeps game-specific state out of the framework.
    /// </para>
    /// </summary>
    public static class ConsolePanels
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Func<string>> Panels =
            new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Order = new List<string>();

        private static Action<string>? _errorSink;

        /// <summary>
        /// Where a panel that throws is reported besides the tab itself. Set by the console
        /// feature to the BepInEx log.
        /// </summary>
        public static Action<string>? ErrorSink
        {
            get { lock (Gate) { return _errorSink; } }
            set { lock (Gate) { _errorSink = value; } }
        }

        /// <summary>How many panels are registered.</summary>
        public static int Count
        {
            get { lock (Gate) { return Order.Count; } }
        }

        /// <summary>The panel names, in registration order. These become the extra tabs.</summary>
        public static IReadOnlyList<string> Names
        {
            get { lock (Gate) { return Order.ToArray(); } }
        }

        /// <summary>
        /// Registers a tab. Registering an existing name replaces its renderer and keeps its
        /// position, so a plugin whose Awake runs twice does not produce two identical tabs.
        /// </summary>
        /// <param name="name">Tab label, e.g. <c>Stream</c>. Required; matched case-insensitively.</param>
        /// <param name="render">
        /// Returns the text to draw. Called on the Unity main thread, once per frame while its tab
        /// is open. It may throw - the tab then shows the failure - but it must be quick.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="render"/> is null.</exception>
        public static void Register(string name, Func<string> render)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a panel name is required", nameof(name));
            }

            if (render is null)
            {
                throw new ArgumentNullException(nameof(render));
            }

            lock (Gate)
            {
                if (!Panels.ContainsKey(name))
                {
                    Order.Add(name);
                }

                Panels[name] = render;
            }
        }

        /// <summary>Removes a tab.</summary>
        /// <param name="name">The registered name.</param>
        /// <returns>True when a panel was removed.</returns>
        public static bool Unregister(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (Gate)
            {
                if (!Panels.Remove(name!))
                {
                    return false;
                }

                for (var i = 0; i < Order.Count; i++)
                {
                    if (string.Equals(Order[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        Order.RemoveAt(i);
                        break;
                    }
                }

                return true;
            }
        }

        /// <summary>True when a panel of this name is registered.</summary>
        /// <param name="name">The name to look for.</param>
        public static bool Has(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (Gate)
            {
                return Panels.ContainsKey(name!);
            }
        }

        /// <summary>
        /// Renders a panel. Never throws: an unknown name and a renderer that throws both come
        /// back as the text to draw, and a failure is also sent to <see cref="ErrorSink"/> so it
        /// survives in the log.
        /// </summary>
        /// <param name="name">The panel to render.</param>
        /// <param name="text">The text to draw; never null.</param>
        /// <returns>True when the panel exists and rendered without throwing.</returns>
        public static bool TryRender(string? name, out string text)
        {
            Func<string>? render;
            lock (Gate)
            {
                if (string.IsNullOrWhiteSpace(name) || !Panels.TryGetValue(name!, out render))
                {
                    text = "no panel named '" + (name ?? string.Empty) + "'";
                    return false;
                }
            }

            try
            {
                text = render() ?? string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                var message = "panel '" + name + "' failed: " + ex.GetType().Name + ": " + ex.Message;
                Report(message);
                text = message;
                return false;
            }
        }

        /// <summary>Removes every panel. Used by tests; harmless at runtime.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Panels.Clear();
                Order.Clear();
            }
        }

        private static void Report(string message)
        {
            var sink = ErrorSink;
            if (sink is null)
            {
                return;
            }

            try
            {
                sink(message);
            }
            catch (Exception ex)
            {
                // Nowhere left to report to; throwing from a render path would cost the frame.
                System.Diagnostics.Debug.WriteLine("console panel error sink threw: " + ex.Message);
            }
        }
    }
}
