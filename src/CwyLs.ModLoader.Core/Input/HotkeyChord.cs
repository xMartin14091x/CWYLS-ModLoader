using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Input
{
    /// <summary>
    /// One key combination, as text. Unity-free on purpose: the runtime chord is BepInEx's
    /// <c>KeyboardShortcut</c>, and this is the half of it that can be parsed, formatted and
    /// unit-tested without an engine.
    /// <para>
    /// It exists because the keys have to be readable. BepInEx serialises a shortcut as
    /// <c>"S + LeftShift + LeftControl"</c> (main key first, then the modifiers sorted by KeyCode
    /// value - <c>KeyboardShortcut.SanitizeKeys</c>), which is correct and unreadable; a person
    /// wants <c>Ctrl+Shift+S</c>. Both forms parse, and <see cref="Format"/> always produces the
    /// short one, with the modifiers in the order a person writes them.
    /// </para>
    /// </summary>
    public sealed class HotkeyChord : IEquatable<HotkeyChord>
    {
        /// <summary>Separator BepInEx writes between the keys of a serialised shortcut.</summary>
        public const string BepInExSeparator = " + ";

        /// <summary>What <see cref="Format"/> prints for a chord with no main key.</summary>
        public const string NotSetText = "(unset)";

        private static readonly string[] Separators = { "+" };

        private readonly string[] _modifiers;

        /// <summary>Creates a chord.</summary>
        /// <param name="mainKey">
        /// Unity <c>KeyCode</c> name of the key pressed last, e.g. <c>F9</c> or <c>S</c>.
        /// Null or empty means "not bound" (<see cref="IsSet"/> is then false).
        /// </param>
        /// <param name="modifiers">
        /// Unity <c>KeyCode</c> names held down first, e.g. <c>LeftControl</c>. Null entries and
        /// blanks are dropped; order is preserved.
        /// </param>
        public HotkeyChord(string? mainKey, params string[]? modifiers)
        {
            MainKey = string.IsNullOrWhiteSpace(mainKey) ? string.Empty : mainKey!.Trim();

            if (modifiers is null || modifiers.Length == 0)
            {
                _modifiers = Array.Empty<string>();
                return;
            }

            var kept = new List<string>(modifiers.Length);
            foreach (var modifier in modifiers)
            {
                if (!string.IsNullOrWhiteSpace(modifier))
                {
                    kept.Add(modifier!.Trim());
                }
            }

            _modifiers = kept.ToArray();
        }

        /// <summary>Unity <c>KeyCode</c> name of the main key, or an empty string when unbound.</summary>
        public string MainKey { get; }

        /// <summary>Unity <c>KeyCode</c> names of the modifiers, in the order they were given.</summary>
        public IReadOnlyList<string> Modifiers => _modifiers;

        /// <summary>True when a main key is bound; an unbound chord can never fire.</summary>
        public bool IsSet => MainKey.Length > 0 && !string.Equals(MainKey, "None", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The short, human form: <c>Ctrl+Shift+S</c>, <c>F9</c>, <see cref="NotSetText"/> when
        /// unbound.
        /// <para>
        /// Modifiers are printed in the order a person writes them - Ctrl, Shift, Alt, Cmd -
        /// whatever order they arrived in. BepInEx sorts a shortcut's modifiers by KeyCode value,
        /// which puts Shift (304) before Ctrl (306), so without this the console would advertise
        /// <c>Shift+Ctrl+S</c> for the chord everyone calls Ctrl+Shift+S.
        /// </para>
        /// </summary>
        public string Format()
        {
            if (!IsSet)
            {
                return NotSetText;
            }

            var sb = new StringBuilder();
            foreach (var modifier in Ordered(_modifiers))
            {
                sb.Append(ShortName(modifier)).Append('+');
            }

            return sb.Append(ShortName(MainKey)).ToString();
        }

        /// <inheritdoc />
        public override string ToString() => Format();

        /// <summary>
        /// Parses <c>Ctrl+Shift+S</c> (modifiers first) or BepInEx's <c>S + LeftControl + LeftShift</c>
        /// (main key first). The two are told apart by the separator: BepInEx always pads its
        /// <c>+</c> with spaces, the short form never does.
        /// </summary>
        /// <param name="text">The chord text. Null, empty or unparseable yields false.</param>
        /// <param name="chord">The parsed chord; null when this returns false.</param>
        /// <returns>True when <paramref name="text"/> named at least one key.</returns>
        public static bool TryParse(string? text, out HotkeyChord? chord)
        {
            chord = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var bepInEx = text!.IndexOf(BepInExSeparator, StringComparison.Ordinal) >= 0;
            var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

            var keys = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    keys.Add(LongName(trimmed));
                }
            }

            if (keys.Count == 0)
            {
                return false;
            }

            if (bepInEx)
            {
                // BepInEx order: main key, then the modifiers it wants held down.
                var modifiers = keys.GetRange(1, keys.Count - 1);
                chord = new HotkeyChord(keys[0], modifiers.ToArray());
                return true;
            }

            // Short order: modifiers, then the main key last, the way a person writes it.
            var main = keys[keys.Count - 1];
            keys.RemoveAt(keys.Count - 1);
            chord = new HotkeyChord(main, keys.ToArray());
            return true;
        }

        /// <summary>
        /// Same main key and same modifiers. The order the modifiers were given in does not
        /// matter: <c>Ctrl+Shift+S</c> and <c>Shift+Ctrl+S</c> are one chord, and BepInEx hands
        /// them over in its own sorted order anyway.
        /// </summary>
        /// <param name="other">The chord to compare with; null is never equal.</param>
        public bool Equals(HotkeyChord? other)
        {
            if (other is null || !string.Equals(MainKey, other.MainKey, StringComparison.Ordinal)
                || _modifiers.Length != other._modifiers.Length)
            {
                return false;
            }

            var mine = Ordered(_modifiers);
            var theirs = Ordered(other._modifiers);

            for (var i = 0; i < mine.Length; i++)
            {
                if (!string.Equals(mine[i], theirs[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as HotkeyChord);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            // Over the canonical order, so two chords that are Equal hash the same.
            var hash = MainKey.GetHashCode();
            foreach (var modifier in Ordered(_modifiers))
            {
                hash = (hash * 397) ^ modifier.GetHashCode();
            }

            return hash;
        }

        /// <summary>
        /// The modifiers in the order a person writes them. A stable insertion sort by
        /// <see cref="Rank"/>: two modifiers of the same family (LeftControl and RightControl)
        /// keep the order they arrived in, and nothing allocates for the common cases of none or
        /// one modifier.
        /// </summary>
        private static string[] Ordered(string[] modifiers)
        {
            if (modifiers.Length < 2)
            {
                return modifiers;
            }

            var ordered = (string[])modifiers.Clone();
            for (var i = 1; i < ordered.Length; i++)
            {
                var current = ordered[i];
                var rank = Rank(current);

                var j = i - 1;
                while (j >= 0 && Rank(ordered[j]) > rank)
                {
                    ordered[j + 1] = ordered[j];
                    j--;
                }

                ordered[j + 1] = current;
            }

            return ordered;
        }

        /// <summary>Where a modifier sits in <c>Ctrl+Shift+Alt+Cmd</c>; anything unknown goes last.</summary>
        private static int Rank(string keyCode) => ShortName(keyCode) switch
        {
            "Ctrl" => 0,
            "Shift" => 1,
            "Alt" => 2,
            "Cmd" => 3,
            _ => 4,
        };

        /// <summary>The name a person reads: <c>LeftControl</c> becomes <c>Ctrl</c>.</summary>
        private static string ShortName(string keyCode) => keyCode switch
        {
            "LeftControl" or "RightControl" => "Ctrl",
            "LeftShift" or "RightShift" => "Shift",
            "LeftAlt" or "RightAlt" => "Alt",
            "LeftCommand" or "RightCommand" or "LeftApple" or "RightApple" => "Cmd",
            _ => keyCode,
        };

        /// <summary>The Unity <c>KeyCode</c> name a short name stands for; anything else is left alone.</summary>
        private static string LongName(string name) => name switch
        {
            "Ctrl" or "Control" => "LeftControl",
            "Shift" => "LeftShift",
            "Alt" or "Option" => "LeftAlt",
            "Cmd" or "Command" or "Meta" => "LeftCommand",
            _ => name,
        };
    }
}
