using System;
using System.Collections.Generic;
using System.IO;

namespace CwyLs.ModLoader.Core.Subtitles
{
    /// <summary>
    /// The mark tables the loader carries itself, embedded in this assembly, so text in a script the
    /// game's own fonts draw comes out right with no pack installed. Today: Thai, for the game's Thai
    /// font assets <c>Base_TH</c> and <c>Dynamic_TH</c> (IBM Plex Sans Thai Looped Medium).
    /// </summary>
    /// <remarks>
    /// <para>
    /// TextMeshPro runs none of the font's mark positioning, so without a table a Thai tone mark that
    /// follows an upper vowel (ที่, ซื้อ) is drawn inside the vowel. The table holds the font's own
    /// offsets (GPOS <c>mkmk</c>, derived by <c>tools/thai-marks</c>): numbers only, no font data and no
    /// translation. It is the same table the language packs used to carry.
    /// </para>
    /// <para>
    /// A pack that declares the same font assets with its own <c>marks</c> table still wins: pack
    /// tables are installed first, and an installed pair is never replaced. The table applies only to
    /// font assets of its family (<see cref="MarkPlacement.Plan"/>), so a game update that swaps the
    /// Thai font gets nothing instead of wrong offsets.
    /// </para>
    /// <para>
    /// Loading is lazy, once, and never throws: a missing or broken resource leaves <see cref="Thai"/>
    /// null and <see cref="LoadError"/> set.
    /// </para>
    /// </remarks>
    public static class BuiltInMarkTables
    {
        /// <summary>Name of the embedded Thai table.</summary>
        public const string ThaiResourceName = "CwyLs.ModLoader.Core.Subtitles.thai-marks.json";

        /// <summary>The game's font assets that draw Thai, which the Thai table is installed into.</summary>
        public static readonly IReadOnlyList<string> ThaiFontAssets = new[] { "Base_TH", "Dynamic_TH" };

        private static readonly object Gate = new object();
        private static MarkTable? _thai;
        private static string? _loadError;
        private static bool _attempted;

        /// <summary>The parsed Thai table, or null when it could not be read (see <see cref="LoadError"/>).</summary>
        public static MarkTable? Thai
        {
            get
            {
                lock (Gate)
                {
                    if (!_attempted)
                    {
                        _attempted = true;
                        Load();
                    }

                    return _thai;
                }
            }
        }

        /// <summary>Why the Thai table could not be read, or null when it was.</summary>
        public static string? LoadError
        {
            get
            {
                _ = Thai;
                lock (Gate)
                {
                    return _loadError;
                }
            }
        }

        /// <summary>Test hook: forget the loaded table so the next query re-reads the resource.</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _thai = null;
                _loadError = null;
                _attempted = false;
            }
        }

        private static void Load()
        {
            try
            {
                var assembly = typeof(BuiltInMarkTables).Assembly;
                using var stream = assembly.GetManifestResourceStream(ThaiResourceName);
                if (stream is null)
                {
                    _loadError = $"embedded resource '{ThaiResourceName}' is not in {assembly.GetName().Name}.dll; update the loader";
                    return;
                }

                using var reader = new StreamReader(stream);
                _thai = MarkTable.Parse(reader.ReadToEnd(), "built-in thai-marks.json");
            }
            catch (Exception ex)
            {
                // Core has no logger; the caller logs LoadError. A broken table must not take the language feature down.
                _thai = null;
                _loadError = ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
