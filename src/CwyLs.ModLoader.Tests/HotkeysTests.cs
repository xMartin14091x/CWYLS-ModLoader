using System.Collections.Generic;
using CwyLs.ModLoader.Core.Input;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-9a. The pure half of the hotkey registry: how a chord reads, how BepInEx's own
    /// serialisation round-trips, when a key is allowed to fire, and when typing wins.
    /// </summary>
    public class HotkeysTests
    {
        [Fact]
        public void A_chord_reads_the_way_a_person_writes_it()
        {
            Assert.Equal("F9", new HotkeyChord("F9").Format());
            Assert.Equal(
                "Ctrl+Shift+S",
                new HotkeyChord("S", "LeftControl", "LeftShift").Format());
        }

        [Fact]
        public void An_unbound_chord_says_so_instead_of_printing_None()
        {
            Assert.False(new HotkeyChord(null).IsSet);
            Assert.False(new HotkeyChord("None").IsSet);
            Assert.Equal(HotkeyChord.NotSetText, new HotkeyChord("None").Format());
        }

        /// <summary>
        /// BepInEx serialises a shortcut main key first, padded: <c>"S + LeftControl + LeftShift"</c>.
        /// A person writes it modifiers first, unpadded. Both have to mean the same chord.
        /// </summary>
        [Theory]
        [InlineData("S + LeftControl + LeftShift")]
        [InlineData("Ctrl+Shift+S")]
        [InlineData("LeftControl+LeftShift+S")]
        public void Both_written_forms_parse_to_the_same_chord(string text)
        {
            Assert.True(HotkeyChord.TryParse(text, out var chord));
            Assert.NotNull(chord);
            Assert.Equal("S", chord!.MainKey);
            Assert.Equal(new[] { "LeftControl", "LeftShift" }, chord.Modifiers);
            Assert.Equal("Ctrl+Shift+S", chord.Format());
        }

        [Fact]
        public void A_bare_key_parses_with_no_modifiers()
        {
            Assert.True(HotkeyChord.TryParse("F9", out var chord));
            Assert.Equal("F9", chord!.MainKey);
            Assert.Empty(chord.Modifiers);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("+")]
        public void Nothing_useful_fails_to_parse(string? text)
        {
            Assert.False(HotkeyChord.TryParse(text, out var chord));
            Assert.Null(chord);
        }

        [Fact]
        public void Equality_is_by_keys_not_by_reference()
        {
            var a = new HotkeyChord("S", "LeftControl", "LeftShift");
            var b = new HotkeyChord("S", "LeftControl", "LeftShift");

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, new HotkeyChord("S", "LeftControl"));
        }

        /// <summary>
        /// Every function has two chords and both are polled in the same Update; a user who
        /// satisfies both, or a host polled twice in one frame, must still get one action.
        /// </summary>
        [Fact]
        public void A_key_fires_once_per_frame()
        {
            var debounce = new HotkeyDebounce();

            Assert.True(debounce.TryFire(100));
            Assert.False(debounce.TryFire(100));
            Assert.False(debounce.TryFire(100));
            Assert.True(debounce.TryFire(101));
        }

        [Fact]
        public void A_reset_counter_does_not_deadlock_the_key()
        {
            var debounce = new HotkeyDebounce();

            Assert.True(debounce.TryFire(5000));
            Assert.True(debounce.TryFire(1));

            debounce.Reset();
            Assert.Equal(HotkeyDebounce.NeverFiredFrame, debounce.LastFiredFrame);
            Assert.True(debounce.TryFire(1));
        }

        [Theory]
        [InlineData("TMP_InputField", true)]
        [InlineData("TMPro.TMP_InputField", true)]
        [InlineData("InputField", true)]
        [InlineData("UnityEngine.UI.InputField", true)]
        [InlineData("Button", false)]
        [InlineData("TextMeshProUGUI", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Only_a_text_field_counts_as_typing(string? typeName, bool expected) =>
            Assert.Equal(expected, TextEntryGuard.IsTextEntryType(typeName));

        [Fact]
        public void A_focused_object_is_typing_when_any_of_its_components_is_a_field()
        {
            Assert.True(TextEntryGuard.IsTextEntry(new[] { "RectTransform", "Image", "TMP_InputField" }));
            Assert.False(TextEntryGuard.IsTextEntry(new[] { "RectTransform", "Image", "Button" }));
            Assert.False(TextEntryGuard.IsTextEntry(null));
        }

        [Fact]
        public void The_startup_line_lists_every_key_with_both_chords()
        {
            // F4: the chord comes first, on both machines - it is the one that always works.
            var keys = new List<HotkeyInfo>
            {
                new HotkeyInfo(
                    "smoke.run", "Runs the checks.",
                    new HotkeyChord("S", "LeftControl", "LeftShift"),
                    new HotkeyChord("F9")),
                new HotkeyInfo(
                    "console.toggle", "Opens the console.",
                    new HotkeyChord("C", "LeftControl", "LeftShift"),
                    new HotkeyChord("F10")),
            };

            Assert.Equal(
                "KEYS: smoke.run = Ctrl+Shift+S | F9, console.toggle = Ctrl+Shift+C | F10",
                HotkeyInfo.Line(keys));

            // The CONSOLE line prints the chords without the id, and never BepInEx's raw form.
            Assert.Equal("Ctrl+Shift+C | F10", keys[1].Chords());
        }

        [Fact]
        public void A_key_with_only_one_chord_does_not_print_a_dangling_separator()
        {
            var info = new HotkeyInfo(
                "smoke.run", "Runs the checks.",
                new HotkeyChord("S", "LeftControl", "LeftShift"),
                new HotkeyChord(null));

            Assert.Equal("smoke.run = Ctrl+Shift+S", info.Format());
            Assert.Equal("Ctrl+Shift+S", info.Chords());

            // The other way round too: only the F-key bound still prints one chord.
            var alternateOnly = new HotkeyInfo(
                "smoke.run", "Runs the checks.",
                new HotkeyChord(null),
                new HotkeyChord("F9"));

            Assert.Equal("smoke.run = F9", alternateOnly.Format());
        }

        [Fact]
        public void No_keys_says_so_rather_than_printing_an_empty_list()
        {
            Assert.Equal(HotkeyInfo.NoKeysText, HotkeyInfo.Line(null));
            Assert.Equal(HotkeyInfo.NoKeysText, HotkeyInfo.Line(new List<HotkeyInfo>()));
        }
    }
}
