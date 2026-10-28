using System;
using CwyLs.ModLoader.Core.Console;
using UnityEngine;

namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// The overlay's look: one monospace-ish font, one 1x1 texture, and a style per kind of line.
    /// </summary>
    /// <remarks>
    /// Built lazily inside <c>OnGUI</c>, because <see cref="GUI.skin"/> only exists there, and
    /// rebuilt only when the text size actually changes - a style rebuild per frame would allocate
    /// on every repaint.
    /// </remarks>
    internal static class ConsoleStyles
    {
        /// <summary>Font size at 1080p before <c>Console.FontScale</c> is applied.</summary>
        private const float BaseFontSize = 13f;

        /// <summary>Height the window reserves for its title bar, which is also the drag area.</summary>
        internal const float TitleBarHeight = 26f;

        /// <summary>Monospace faces to try, in order; the first the OS has wins.</summary>
        private static readonly string[] MonospaceFaces =
        {
            "Menlo", "Monaco", "SF Mono", "Consolas", "Courier New", "DejaVu Sans Mono", "Liberation Mono"
        };

        private static readonly Color Background = new Color(0.06f, 0.07f, 0.09f);
        private static readonly Color Panel = new Color(0.10f, 0.11f, 0.14f);

        private static Texture2D? _white;
        private static Font? _mono;
        private static bool _fontTried;
        private static int _builtSize = -1;

        private static GUIStyle? _title;
        private static GUIStyle? _status;
        private static GUIStyle? _input;
        private static GUIStyle? _button;
        private static GUIStyle? _tab;
        private static GUIStyle? _output;
        private static GUIStyle? _echo;
        private static GUIStyle? _info;
        private static GUIStyle? _warning;
        private static GUIStyle? _error;

        /// <summary>The text size in use, in points.</summary>
        internal static int FontSize => _builtSize < 0 ? 13 : _builtSize;

        /// <summary>A 1x1 white texture, tinted with <see cref="GUI.color"/> where a fill is needed.</summary>
        internal static Texture2D White => _white ??= MakeWhite();

        /// <summary>The window's background colour at the configured opacity.</summary>
        internal static Color WindowColor =>
            new Color(Background.r, Background.g, Background.b, ConsoleFeature.Opacity);

        /// <summary>The inner panel colour (output area, tab body) at the configured opacity.</summary>
        internal static Color PanelColor =>
            new Color(Panel.r, Panel.g, Panel.b, Mathf.Min(1f, ConsoleFeature.Opacity + 0.05f));

        /// <summary>The window title.</summary>
        internal static GUIStyle Title => _title!;

        /// <summary>The right-hand status text in the title row.</summary>
        internal static GUIStyle Status => _status!;

        /// <summary>The command input line.</summary>
        internal static GUIStyle Input => _input!;

        /// <summary>A small button.</summary>
        internal static GUIStyle Button => _button!;

        /// <summary>The tab strip.</summary>
        internal static GUIStyle Tab => _tab!;

        /// <summary>Builds the styles if the text size changed. Call first in <c>OnGUI</c>.</summary>
        internal static void Ensure()
        {
            var size = WantedFontSize();
            if (_builtSize == size && _output != null)
            {
                return;
            }

            Build(size);
        }

        /// <summary>The style for one kind of line.</summary>
        /// <param name="kind">What the line is.</param>
        internal static GUIStyle For(ConsoleLineKind kind)
        {
            switch (kind)
            {
                case ConsoleLineKind.Echo: return _echo!;
                case ConsoleLineKind.Info: return _info!;
                case ConsoleLineKind.Warning: return _warning!;
                case ConsoleLineKind.Error: return _error!;
                default: return _output!;
            }
        }

        private static int WantedFontSize()
        {
            // The overlay has to stay legible from a 1280x720 window up to a 4K display, so the
            // size follows the window height and the user's multiplier on top of it.
            var height = Screen.height <= 0 ? 1080f : Screen.height;
            var scaled = BaseFontSize * ConsoleFeature.FontScale * Mathf.Clamp(height / 1080f, 0.8f, 2.5f);
            return Mathf.Clamp(Mathf.RoundToInt(scaled), 9, 48);
        }

        private static void Build(int size)
        {
            _builtSize = size;

            var font = EnsureFont(size);
            var label = new GUIStyle(GUI.skin.label)
            {
                font = font,
                fontSize = size,
                wordWrap = true,
                richText = false,
                padding = new RectOffset(2, 2, 1, 1),
                margin = new RectOffset(0, 0, 0, 0)
            };

            _output = Coloured(label, new Color(0.88f, 0.90f, 0.93f));
            _echo = Coloured(label, new Color(0.55f, 0.85f, 1f));
            _info = Coloured(label, new Color(0.72f, 0.78f, 0.84f));
            _warning = Coloured(label, new Color(1f, 0.83f, 0.35f));
            _error = Coloured(label, new Color(1f, 0.45f, 0.42f));

            _title = new GUIStyle(label)
            {
                fontSize = size + 2,
                fontStyle = FontStyle.Bold,
                wordWrap = false
            };
            _title.normal.textColor = new Color(0.95f, 0.96f, 0.98f);

            _status = new GUIStyle(label)
            {
                fontSize = Mathf.Max(9, size - 1),
                wordWrap = false,
                alignment = TextAnchor.MiddleRight
            };
            _status.normal.textColor = new Color(0.60f, 0.65f, 0.72f);

            _input = new GUIStyle(GUI.skin.textField)
            {
                font = font,
                fontSize = size,
                wordWrap = false,
                richText = false
            };

            _button = new GUIStyle(GUI.skin.button)
            {
                font = font,
                fontSize = Mathf.Max(9, size - 1),
                padding = new RectOffset(8, 8, 3, 3),
                margin = new RectOffset(3, 3, 2, 2)
            };

            _tab = new GUIStyle(GUI.skin.button)
            {
                font = font,
                fontSize = Mathf.Max(9, size - 1),
                padding = new RectOffset(8, 8, 3, 3)
            };
        }

        private static GUIStyle Coloured(GUIStyle from, Color color)
        {
            var style = new GUIStyle(from);
            style.normal.textColor = color;
            return style;
        }

        private static Font? EnsureFont(int size)
        {
            if (_fontTried)
            {
                return _mono;
            }

            _fontTried = true;
            try
            {
                // Columns only line up in a monospace face, and a console is all columns. Unity
                // falls back to the default face itself when the OS has none of these.
                _mono = Font.CreateDynamicFontFromOSFont(MonospaceFaces, size);
                if (_mono != null)
                {
                    _mono.hideFlags = HideFlags.HideAndDontSave;
                }
            }
            catch (Exception ex)
            {
                // Not fatal: the default skin font is proportional but perfectly readable.
                _mono = null;
                CwyLsModLoader.Log.LogWarning(
                    $"{ConsoleFeature.LogPrefix}: no monospace font, using the default face: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }

            return _mono;
        }

        private static Texture2D MakeWhite()
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            return texture;
        }
    }
}
