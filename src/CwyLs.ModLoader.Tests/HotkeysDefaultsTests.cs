using CwyLs.ModLoader.Core.Input;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// F4. The chords are now chord-first on both machines (<c>Ctrl+Shift+S | F9</c>), and a .cfg
    /// written by an older build still holds the old pair. These pin the one case the registry is
    /// allowed to rewrite - the entry is still, verbatim, what the old build wrote - and every
    /// case where the user's own choice must survive.
    /// </summary>
    public class HotkeysDefaultsTests
    {
        private static readonly HotkeyChord Chord = new HotkeyChord("S", "LeftControl", "LeftShift");
        private static readonly HotkeyChord FKey = new HotkeyChord("F9");

        /// <summary>The whole point: old pair on disk, new pair in the build, so rewrite it.</summary>
        [Fact]
        public void An_untouched_old_default_pair_is_migrated()
        {
            Assert.True(HotkeyDefaults.ShouldMigrate(
                storedPrimary: FKey, storedAlternate: Chord,
                oldPrimary: FKey, oldAlternate: Chord,
                newPrimary: Chord, newAlternate: FKey));
        }

        [Fact]
        public void An_already_migrated_pair_is_left_alone()
        {
            // Second launch: the stored pair is the new pair, which is not the old pair.
            Assert.False(HotkeyDefaults.ShouldMigrate(
                storedPrimary: Chord, storedAlternate: FKey,
                oldPrimary: FKey, oldAlternate: Chord,
                newPrimary: Chord, newAlternate: FKey));
        }

        [Theory]
        // One chord changed by the user: not the old pair any more, so both stay.
        [InlineData("F7", "S")]
        [InlineData("F9", "Q")]
        // Both changed.
        [InlineData("F7", "Q")]
        public void A_pair_the_user_changed_is_never_rewritten(string primaryKey, string alternateKey)
        {
            var stored = new HotkeyChord(primaryKey);
            var storedAlternate = new HotkeyChord(alternateKey, "LeftControl", "LeftShift");

            Assert.False(HotkeyDefaults.ShouldMigrate(
                stored, storedAlternate,
                oldPrimary: FKey, oldAlternate: Chord,
                newPrimary: Chord, newAlternate: FKey));
        }

        /// <summary>
        /// The default parameters of <c>Hotkeys.Register</c> are an unset legacy pair: a key whose
        /// owner declared no migration must never have its config rewritten.
        /// </summary>
        [Fact]
        public void No_declared_legacy_pair_means_no_migration()
        {
            var unset = new HotkeyChord(null);

            Assert.False(HotkeyDefaults.ShouldMigrate(
                storedPrimary: FKey, storedAlternate: Chord,
                oldPrimary: unset, oldAlternate: unset,
                newPrimary: Chord, newAlternate: FKey));

            Assert.False(HotkeyDefaults.ShouldMigrate(
                storedPrimary: FKey, storedAlternate: Chord,
                oldPrimary: FKey, oldAlternate: unset,
                newPrimary: Chord, newAlternate: FKey));
        }

        [Fact]
        public void A_build_whose_defaults_did_not_change_migrates_nothing()
        {
            Assert.False(HotkeyDefaults.ShouldMigrate(
                storedPrimary: FKey, storedAlternate: Chord,
                oldPrimary: FKey, oldAlternate: Chord,
                newPrimary: FKey, newAlternate: Chord));
        }

        [Fact]
        public void A_missing_chord_is_not_a_migration()
        {
            Assert.False(HotkeyDefaults.ShouldMigrate(
                null, Chord, FKey, Chord, Chord, FKey));
            Assert.False(HotkeyDefaults.ShouldMigrate(
                FKey, null, FKey, Chord, Chord, FKey));
            Assert.False(HotkeyDefaults.ShouldMigrate(
                FKey, Chord, FKey, Chord, null, FKey));
        }

        [Fact]
        public void A_pair_is_compared_in_both_roles()
        {
            Assert.True(HotkeyDefaults.IsPair(FKey, Chord, FKey, Chord));

            // Swapped is a different pair - that is exactly what the migration turns one into.
            Assert.False(HotkeyDefaults.IsPair(FKey, Chord, Chord, FKey));
            Assert.False(HotkeyDefaults.IsPair(null, Chord, FKey, Chord));
        }

        [Fact]
        public void The_migration_line_names_the_key()
        {
            Assert.Equal(
                "KEYS: migrated smoke.run to chord-first defaults",
                HotkeyDefaults.MigratedLine("smoke.run"));
            Assert.Equal(
                "KEYS: migrated console.toggle to chord-first defaults",
                HotkeyDefaults.MigratedLine("console.toggle"));
        }

        /// <summary>
        /// The chords the migration produces, in the form the KEYS line and the CONSOLE line
        /// print them - never BepInEx's <c>"S + LeftControl + LeftShift"</c> (R4 section 5.3.3).
        /// </summary>
        [Theory]
        [InlineData("S", "Ctrl+Shift+S")]
        [InlineData("C", "Ctrl+Shift+C")]
        [InlineData("R", "Ctrl+Shift+R")]
        public void The_new_primary_chords_read_the_way_a_person_writes_them(string key, string expected) =>
            Assert.Equal(expected, new HotkeyChord(key, "LeftControl", "LeftShift").Format());

        /// <summary>
        /// The order BepInEx actually stores. <c>KeyboardShortcut.SanitizeKeys</c> sorts the
        /// modifiers by KeyCode value, and LeftShift is 304 while LeftControl is 306 - so every
        /// chord we construct comes back as <c>"C + LeftShift + LeftControl"</c> and the deployed
        /// .cfg says exactly that. The console must still say Ctrl+Shift+C.
        /// </summary>
        [Fact]
        public void BepInExs_own_serialisation_prints_as_Ctrl_Shift_first()
        {
            Assert.True(HotkeyChord.TryParse("C + LeftShift + LeftControl", out var chord));
            Assert.Equal("Ctrl+Shift+C", chord!.Format());
            Assert.Equal("C", chord.MainKey);

            // R4 section 5.3.3 measured the old output: 'KEYS: console.toggle = F10 | Shift+Ctrl+C'.
            Assert.DoesNotContain("Shift+Ctrl", chord.Format());
        }

        [Fact]
        public void The_modifier_order_a_chord_arrived_in_does_not_change_how_it_reads()
        {
            // BepInEx's order (sorted by KeyCode) and the order a person types it.
            Assert.Equal("Ctrl+Shift+S", new HotkeyChord("S", "LeftShift", "LeftControl").Format());
            Assert.Equal("Ctrl+Shift+S", new HotkeyChord("S", "LeftControl", "LeftShift").Format());
        }

        [Fact]
        public void Two_orders_of_the_same_chord_are_the_same_chord()
        {
            var bepInExOrder = new HotkeyChord("S", "LeftShift", "LeftControl");
            var humanOrder = new HotkeyChord("S", "LeftControl", "LeftShift");

            // The migration compares what is in the .cfg with what the build declares; the two
            // reach HotkeyChord through different paths, so this equality is load-bearing.
            Assert.Equal(bepInExOrder, humanOrder);
            Assert.Equal(bepInExOrder.GetHashCode(), humanOrder.GetHashCode());
            Assert.True(HotkeyDefaults.IsPair(
                bepInExOrder, new HotkeyChord("F9"), humanOrder, new HotkeyChord("F9")));
        }

        [Fact]
        public void Every_modifier_family_has_its_place_in_the_line()
        {
            Assert.Equal(
                "Ctrl+Shift+Alt+K",
                new HotkeyChord("K", "LeftAlt", "LeftShift", "LeftControl").Format());
            Assert.Equal(
                "Ctrl+Cmd+K",
                new HotkeyChord("K", "LeftCommand", "RightControl").Format());
            Assert.Equal("Shift+F9", new HotkeyChord("F9", "RightShift").Format());
        }
    }
}
