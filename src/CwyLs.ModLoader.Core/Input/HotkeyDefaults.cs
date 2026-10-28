namespace CwyLs.ModLoader.Core.Input
{
    /// <summary>
    /// The rule that decides whether a <c>[Keys]</c> entry a user already has on disk should be
    /// rewritten to a new default pair. Pure, so the one destructive thing the key registry does
    /// to an existing config file can be unit-tested without an engine.
    /// <para>
    /// The chord is the primary chord on macOS and Windows (<c>Ctrl+Shift+S | F9</c> instead of
    /// <c>F9 | Ctrl+Shift+S</c>), because on macOS an F-key is a media key unless Fn is held and
    /// both platforms should read the same. A .cfg written by an older build still holds
    /// the old pair, and BepInEx never revisits a value it has already bound - so the framework
    /// migrates exactly one case: the entry is still, verbatim, the pair the old build wrote.
    /// Anything else is a choice the user made, and is left alone.
    /// </para>
    /// </summary>
    public static class HotkeyDefaults
    {
        /// <summary>What the registry logs when it rewrites a pair, e.g. <c>KEYS: migrated smoke.run to chord-first defaults</c>.</summary>
        public const string MigratedLineSuffix = " to chord-first defaults";

        /// <summary>
        /// Whether the stored pair should be replaced by the new default pair.
        /// </summary>
        /// <param name="storedPrimary">The <c>&lt;id&gt;.Primary</c> value read from the config file.</param>
        /// <param name="storedAlternate">The <c>&lt;id&gt;.Alt</c> value read from the config file.</param>
        /// <param name="oldPrimary">What this build's predecessor used as the primary default.</param>
        /// <param name="oldAlternate">What this build's predecessor used as the alternate default.</param>
        /// <param name="newPrimary">This build's primary default.</param>
        /// <param name="newAlternate">This build's alternate default.</param>
        /// <returns>
        /// True only when every chord is known, the old pair is not already the new pair, and the
        /// stored pair is exactly the old pair. A user who changed either chord keeps both.
        /// </returns>
        public static bool ShouldMigrate(
            HotkeyChord? storedPrimary,
            HotkeyChord? storedAlternate,
            HotkeyChord? oldPrimary,
            HotkeyChord? oldAlternate,
            HotkeyChord? newPrimary,
            HotkeyChord? newAlternate)
        {
            if (storedPrimary is null || storedAlternate is null
                || oldPrimary is null || oldAlternate is null
                || newPrimary is null || newAlternate is null)
            {
                return false;
            }

            // No declared migration (the caller passed the defaults twice, or an unset legacy
            // pair): nothing to rewrite.
            if (!oldPrimary.IsSet || !oldAlternate.IsSet
                || IsPair(oldPrimary, oldAlternate, newPrimary, newAlternate))
            {
                return false;
            }

            return IsPair(storedPrimary, storedAlternate, oldPrimary, oldAlternate);
        }

        /// <summary>True when two chords are the same two chords, in the same roles.</summary>
        /// <param name="primary">The primary chord to compare.</param>
        /// <param name="alternate">The alternate chord to compare.</param>
        /// <param name="expectedPrimary">The primary chord expected.</param>
        /// <param name="expectedAlternate">The alternate chord expected.</param>
        public static bool IsPair(
            HotkeyChord? primary,
            HotkeyChord? alternate,
            HotkeyChord? expectedPrimary,
            HotkeyChord? expectedAlternate) =>
            primary != null && alternate != null
            && primary.Equals(expectedPrimary) && alternate.Equals(expectedAlternate);

        /// <summary>The line the registry logs after a rewrite.</summary>
        /// <param name="id">Registry id of the migrated key, e.g. <c>smoke.run</c>.</param>
        public static string MigratedLine(string id) =>
            HotkeyInfo.LinePrefix + "migrated " + id + MigratedLineSuffix;
    }
}
