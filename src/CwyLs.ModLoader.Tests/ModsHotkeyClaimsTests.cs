using System.Collections.Generic;
using CwyLs.ModLoader.Core.Input;
using CwyLs.ModLoader.Core.Mods;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FIX (review #4, the registry half): an id another owner holds is refused, never replaced;
    /// the same owner re-registering replaces; a chord another key uses is reported.
    /// </summary>
    public class ModsHotkeyClaimsTests
    {
        private const string Framework = "/game/BepInEx/config/cwyls.modloader.cfg";
        private const string ModA = "/game/BepInEx/config/cwy.mod.a.mod.cfg";

        [Fact]
        public void A_free_id_is_new()
        {
            Assert.Equal(HotkeyClaim.New, HotkeyClaims.Decide("a.mod.greet", ModA, Held(("console.toggle", Framework)), out var holder));
            Assert.Null(holder);
            Assert.Equal(HotkeyClaim.New, HotkeyClaims.Decide("x", ModA, null, out _));
        }

        [Fact]
        public void The_same_owner_registering_again_replaces()
        {
            Assert.Equal(HotkeyClaim.SameOwner, HotkeyClaims.Decide("console.toggle", Framework.ToUpperInvariant(), Held(("console.toggle", Framework)), out var holder));
            Assert.Equal(Framework, holder);
        }

        [Fact]
        public void Another_owners_id_is_refused()
        {
            // "a" + "b.greet" and "a.b" + "greet" both become "a.b.greet".
            Assert.Equal(HotkeyClaim.OtherOwner, HotkeyClaims.Decide("a.b.greet", ModA, Held(("a.b.greet", "/cfg/cwy.mod.a.b.cfg")), out var holder));
            Assert.Equal("/cfg/cwy.mod.a.b.cfg", holder);
            Assert.Equal(HotkeyClaim.OtherOwner, HotkeyClaims.Decide("console.toggle", ModA, Held(("console.toggle", Framework)), out _));
        }

        [Fact]
        public void Ids_compare_exactly()
        {
            Assert.Equal(HotkeyClaim.New, HotkeyClaims.Decide("Console.Toggle", ModA, Held(("console.toggle", Framework)), out _));
        }

        [Fact]
        public void A_chord_another_key_uses_is_reported_once_per_key_and_chord()
        {
            var others = new[]
            {
                new HotkeyInfo("console.toggle", "", Chord("C", "LeftControl", "LeftShift"), Chord("F7")),
                new HotkeyInfo("smoke.run", "", Chord("S", "LeftControl", "LeftShift"), Chord("F9")),
                new HotkeyInfo("a.mod.greet", "", Chord("C", "LeftShift", "LeftControl"), Chord(null)),
            };

            var clashes = HotkeyClaims.Clashes("a.mod.greet", Chord("C", "LeftShift", "LeftControl"), Chord("F9"), others);

            Assert.Equal(new[] { "console.toggle (Ctrl+Shift+C)", "smoke.run (F9)" }, clashes);
        }

        [Fact]
        public void Unset_chords_never_clash()
        {
            var others = new[] { new HotkeyInfo("x", "", Chord(null), Chord(null)) };

            Assert.Empty(HotkeyClaims.Clashes("y", Chord(null), Chord(null), others));
            Assert.Empty(HotkeyClaims.Clashes("y", null, null, others));
            Assert.Empty(HotkeyClaims.Clashes("y", Chord("F1"), null, null));
        }

        [Theory]
        [InlineData("mod:a.mod/greet", "Hotkeys", "greet")]
        [InlineData("mod:cwy.sample.hello/open.window", "Hotkeys", "open.window")]
        [InlineData("console.toggle", "Keys", "console.toggle")]
        [InlineData("mod:/greet", "Keys", "mod:/greet")]
        [InlineData("mod:a.mod/", "Keys", "mod:a.mod/")]
        [InlineData("MOD:a.mod/greet", "Keys", "MOD:a.mod/greet")]
        public void A_mod_key_is_bound_as_its_own_key_in_the_hotkeys_section(string id, string section, string key)
        {
            HotkeyClaims.ConfigSlot(id, "Keys", out var actualSection, out var actualKey);

            Assert.Equal(section, actualSection);
            Assert.Equal(key, actualKey);
        }

        private static HotkeyChord Chord(string? main, params string[] modifiers) => new HotkeyChord(main, modifiers);

        private static List<KeyValuePair<string, string>> Held(params (string Id, string Owner)[] held)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var (id, owner) in held)
            {
                list.Add(new KeyValuePair<string, string>(id, owner));
            }

            return list;
        }
    }
}
