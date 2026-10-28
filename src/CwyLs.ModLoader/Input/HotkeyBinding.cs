using System;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Input;
using UnityEngine;

namespace CwyLs.ModLoader.Input
{
    /// <summary>
    /// One function bound to two chords, with the config entries that own them. Created only by
    /// <see cref="Hotkeys.Register"/>.
    /// </summary>
    internal sealed class HotkeyBinding
    {
        private readonly HotkeyDebounce _debounce = new HotkeyDebounce();

        internal HotkeyBinding(
            string id,
            string description,
            ConfigEntry<KeyboardShortcut> primary,
            ConfigEntry<KeyboardShortcut> alternate,
            Action onPressed)
        {
            Id = id;
            Description = description;
            Primary = primary;
            Alternate = alternate;
            OnPressed = onPressed;
        }

        /// <summary>Registry id, e.g. <c>smoke.run</c>.</summary>
        internal string Id { get; }

        /// <summary>What the key does, for the config comment and the KEYS line.</summary>
        internal string Description { get; }

        /// <summary>The <c>[Keys] &lt;id&gt;.Primary</c> entry, normally Ctrl+Shift+letter.</summary>
        internal ConfigEntry<KeyboardShortcut> Primary { get; }

        /// <summary>The <c>[Keys] &lt;id&gt;.Alt</c> entry, normally the F-key.</summary>
        internal ConfigEntry<KeyboardShortcut> Alternate { get; }

        /// <summary>What to run when either chord is pressed.</summary>
        internal Action OnPressed { get; }

        /// <summary>
        /// True when either chord went down this frame and the key has not already fired in it.
        /// Both chords are live on every platform, so a Windows user may use Ctrl+Shift+S too.
        /// </summary>
        /// <param name="frame">Unity's frame counter, used for the once-per-frame rule.</param>
        internal bool Pressed(int frame) =>
            (Primary.Value.IsDown() || Alternate.Value.IsDown()) && _debounce.TryFire(frame);

        /// <summary>This binding as the registry describes it to a person.</summary>
        internal HotkeyInfo Describe() =>
            new HotkeyInfo(Id, Description, Chord(Primary.Value), Chord(Alternate.Value));

        /// <summary>
        /// The Unity-free form of a BepInEx shortcut, for formatting, for the default-migration
        /// rule and for tests. Exposed to the assembly through <c>Hotkeys.Chord</c>.
        /// </summary>
        /// <param name="shortcut">The chord to convert; an unbound one yields an unset chord.</param>
        internal static HotkeyChord Chord(KeyboardShortcut shortcut)
        {
            var main = shortcut.MainKey;
            if (main == KeyCode.None)
            {
                return new HotkeyChord(null);
            }

            // Modifiers is IEnumerable<KeyCode> and never null; building the array by hand keeps
            // this allocation-free of LINQ on a path the startup line and the console both use.
            var names = new System.Collections.Generic.List<string>(2);
            foreach (var modifier in shortcut.Modifiers)
            {
                names.Add(modifier.ToString());
            }

            return new HotkeyChord(main.ToString(), names.ToArray());
        }
    }
}
