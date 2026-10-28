using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// A point-in-time copy of everything the bus publishes, in one object. Written to
    /// <c>BepInEx/data/snapshot.json</c> so out-of-process tools (an LLM sidecar, a dashboard,
    /// a stream overlay) can read the player's state without loading into the game process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every field is optional in the sense that a section the game has not loaded yet is
    /// <c>null</c>; consumers must handle that rather than assume presence. Sections that are
    /// lists are never null, only empty.
    /// </para>
    /// <para>
    /// <see cref="SchemaVersion"/> is bumped whenever a field is removed or changes meaning.
    /// Adding a field is not a version bump, so readers must ignore unknown keys.
    /// </para>
    /// </remarks>
    public sealed class GameDataSnapshot
    {
        /// <summary>The schema version this class produces. See the remarks for the contract.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>Schema version of this document. Always <see cref="CurrentSchemaVersion"/>.</summary>
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        /// <summary>When the snapshot was taken, local time with offset.</summary>
        public DateTimeOffset Time { get; init; }

        /// <summary>The game's own version string, so a stale snapshot is recognisable.</summary>
        public string GameVersion { get; init; } = string.Empty;

        /// <summary>Version of the plugin that produced this snapshot.</summary>
        public string PluginVersion { get; init; } = string.Empty;

        /// <summary>
        /// False when the game had not finished loading save data at capture time; every section
        /// below is then the game's default-constructed state, not the player's.
        /// </summary>
        public bool IsReady { get; init; }

        /// <summary>Level, work totals, login history.</summary>
        public PlayerView? Player { get; init; }

        /// <summary>Birthday, icon, presence status.</summary>
        public ProfileView? Profile { get; init; }

        /// <summary>Device-account flags only; never the identifier itself.</summary>
        public AccountView? Account { get; init; }

        /// <summary>Timer configuration and presets.</summary>
        public PomodoroView? Pomodoro { get; init; }

        /// <summary>Every todo list with its items, in the player's order.</summary>
        public IReadOnlyList<TodoListView> Todos { get; init; } = Array.Empty<TodoListView>();

        /// <summary>Every note page, in the player's order.</summary>
        public IReadOnlyList<NotePageView> Notes { get; init; } = Array.Empty<NotePageView>();

        /// <summary>Every tracked habit with a recent completion window.</summary>
        public IReadOnlyList<HabitView> Habits { get; init; } = Array.Empty<HabitView>();

        /// <summary>A recent window of calendar days.</summary>
        public CalendarView? Calendar { get; init; }

        /// <summary>Volumes, language, window and notification flags.</summary>
        public SettingsView? Settings { get; init; }

        /// <summary>Playlist preferences and the imported local library.</summary>
        public MusicView? Music { get; init; }

        /// <summary>Window views and ambient sound mix.</summary>
        public EnvironmentView? Environment { get; init; }

        /// <summary>Story preference and scenario progress.</summary>
        public StoryView? Story { get; init; }

        /// <summary>Unlocked achievements.</summary>
        public AchievementsView? Achievements { get; init; }

        /// <summary>
        /// Sections published by other mods through <see cref="IGameDataProvider"/>, keyed by
        /// their <see cref="IGameDataProvider.SectionName"/>. Never null; empty when no provider
        /// is registered.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Extra { get; init; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);
    }
}
