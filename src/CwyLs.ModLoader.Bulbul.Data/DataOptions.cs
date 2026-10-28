namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// The bus's runtime switches, read once from the plugin's .cfg and then held here so the
    /// static facade does not have to carry a BepInEx <c>ConfigFile</c> around. Defaults match the
    /// shipped .cfg, so the facade behaves sanely even if the plugin's Awake never ran.
    /// </summary>
    public sealed class DataOptions
    {
        /// <summary>Config section name all of these are bound under.</summary>
        public const string Section = "Data";

        /// <summary>
        /// Write <c>BepInEx/data/snapshot.json</c> whenever the data changes. Turning this off
        /// leaves the in-process API (events, <c>GameData.Snapshot()</c>) fully working.
        /// </summary>
        public bool WriteSnapshot { get; set; } = true;

        /// <summary>
        /// Milliseconds of quiet after the last change before the snapshot is written. The game
        /// calls <c>SaveAll</c> in bursts of ~27 saves, so writing per change would mean 27 files.
        /// </summary>
        public int DebounceMs { get; set; } = 500;

        /// <summary>
        /// Publish the text the player wrote - note bodies and diary entries.
        /// </summary>
        /// <remarks>
        /// Off by default (RP-1, public release): it is the most private thing in the save file, and
        /// the snapshot is a plain file on disk that any process - and, through a mod, any remote
        /// service - can read. A player who wants an assistant mod to help with their notes turns it
        /// on. Off, note titles, ids and ordering still publish; only the bodies are withheld (as
        /// <c>null</c>, distinct from an empty page).
        /// </remarks>
        public bool IncludeNoteText { get; set; }

        /// <summary>
        /// Allow <see cref="GameData.Write"/> to modify the player's save data. Off by default:
        /// reading is harmless, writing touches a file the player cannot easily recover.
        /// </summary>
        public bool AllowWrites { get; set; }

        /// <summary>
        /// How many days of habit and calendar history to publish. The game stores one file per
        /// month and loads them lazily, so this bounds how much it is asked to page in.
        /// </summary>
        public int RecentDays { get; set; } = 14;
    }
}
