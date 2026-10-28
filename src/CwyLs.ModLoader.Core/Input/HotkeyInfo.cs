using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Input
{
    /// <summary>
    /// One registered hotkey as it is shown to a person: what it does and the two chords that do
    /// it. Produced by the registry in <c>CwyLs.ModLoader.Input.Hotkeys</c>; kept here so the startup
    /// line and the future in-game console format keys the same way and can be tested.
    /// </summary>
    public sealed class HotkeyInfo
    {
        /// <summary>Prefix of the one-line startup summary of every key.</summary>
        public const string LinePrefix = "KEYS: ";

        /// <summary>What the summary says when nothing registered a key.</summary>
        public const string NoKeysText = "KEYS: none registered";

        /// <summary>Creates a description.</summary>
        /// <param name="id">Registry id, e.g. <c>smoke.run</c>. Required.</param>
        /// <param name="description">What the key does, one short sentence. Null becomes empty.</param>
        /// <param name="primary">The primary chord (Ctrl+Shift+letter). Required.</param>
        /// <param name="alternate">The alternate chord (the F-key). Required.</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is null or empty.</exception>
        /// <exception cref="ArgumentNullException">A chord is null.</exception>
        public HotkeyInfo(string id, string? description, HotkeyChord primary, HotkeyChord alternate)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("a hotkey id is required", nameof(id));
            }

            Id = id;
            Description = description ?? string.Empty;
            Primary = primary ?? throw new ArgumentNullException(nameof(primary));
            Alternate = alternate ?? throw new ArgumentNullException(nameof(alternate));
        }

        /// <summary>Registry id, e.g. <c>smoke.run</c>.</summary>
        public string Id { get; }

        /// <summary>What the key does.</summary>
        public string Description { get; }

        /// <summary>
        /// The primary chord: the one the framework advertises. That is
        /// <c>Ctrl+Shift+&lt;letter&gt;</c>, because on macOS an F-key is a media key unless Fn is
        /// held and macOS and Windows should read the same.
        /// </summary>
        public HotkeyChord Primary { get; }

        /// <summary>The alternate chord, normally the F-key. Both are always live.</summary>
        public HotkeyChord Alternate { get; }

        /// <summary><c>smoke.run = Ctrl+Shift+S | F9</c>; an unbound chord is omitted.</summary>
        public string Format() => new StringBuilder(Id).Append(" = ").Append(Chords()).ToString();

        /// <summary>
        /// Just the chords, primary first: <c>Ctrl+Shift+S | F9</c>. An unbound chord is omitted,
        /// so a key with one chord prints one chord and no dangling separator.
        /// </summary>
        public string Chords() =>
            Primary.IsSet && Alternate.IsSet
                ? Primary.Format() + " | " + Alternate.Format()
                : (Primary.IsSet ? Primary.Format() : Alternate.Format());

        /// <inheritdoc />
        public override string ToString() => Format();

        /// <summary>
        /// The startup line: <c>KEYS: smoke.run = Ctrl+Shift+S | F9, console.toggle = Ctrl+Shift+C | F10</c>.
        /// </summary>
        /// <param name="keys">The registered keys, in registration order. Null or empty yields <see cref="NoKeysText"/>.</param>
        public static string Line(IEnumerable<HotkeyInfo>? keys)
        {
            if (keys is null)
            {
                return NoKeysText;
            }

            var sb = new StringBuilder(LinePrefix);
            var written = 0;

            foreach (var key in keys)
            {
                if (key is null)
                {
                    continue;
                }

                if (written > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(key.Format());
                written++;
            }

            return written == 0 ? NoKeysText : sb.ToString();
        }
    }
}
