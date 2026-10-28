using System;
using System.Collections.Generic;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Data;
using R3;
using UnityEngine;
using Game = global::Bulbul;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// The Game Data Bus: one typed, read-only door onto everything the player owns, plus change
    /// notifications, a JSON snapshot for out-of-process tools, an extension point for other mods,
    /// and a small guarded write API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Threading.</b> Every accessor reads the game's live objects, so call them from Unity's
    /// main thread. <see cref="Changed"/> is raised on whatever thread performed the save, which in
    /// practice is always the main thread.
    /// </para>
    /// <para>
    /// <b>Copies, not references.</b> Accessors return freshly built immutable views. Nothing here
    /// hands out a live game object, because writing to one would change the player's data without
    /// the game ever saving it.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Every accessor rebuilds its view on each call - cheap for todos and settings,
    /// less so for <see cref="Notes"/>, <see cref="Habits"/> and <see cref="Calendar"/>, which can
    /// page files in from disk. Read once per frame at most; subscribe to <see cref="Changed"/>
    /// rather than polling.
    /// </para>
    /// </remarks>
    public static class GameData
    {
        /// <summary>Subsystem tag this plugin publishes under on <see cref="TraceBus"/>.</summary>
        public const string TraceSys = "data";

        private static readonly object Gate = new object();
        private static readonly List<IGameDataProvider> ProviderList = new List<IGameDataProvider>();
        private static readonly Subject<GameDataChanged> ChangedSubject = new Subject<GameDataChanged>();

        private static DataOptions _options = new DataOptions();

        /// <summary>
        /// Raised after a section of the player's data changed. Handlers run synchronously inside
        /// the game's save call, so keep them short; a handler that throws is logged and the rest
        /// still run.
        /// </summary>
        public static event Action<GameDataChanged>? Changed;

        /// <summary>
        /// The same notifications as an R3 stream, for code that already composes observables.
        /// Never completes. Dispose your subscription; the bus outlives every scene.
        /// </summary>
        public static Observable<GameDataChanged> ChangedObservable => ChangedSubject;

        /// <summary>
        /// True once the game has loaded the player's save data. Until then the accessors below
        /// still work but describe the game's default-constructed state, not the player's - so a
        /// mod that acts on the data should wait for this rather than for its own Awake.
        /// </summary>
        public static bool IsReady =>
            SaveDataAccess.Resolve() != null &&
            (SaveDataAccess.LoadObserved || SaveDataAccess.SceneObserved);

        /// <summary>The switches from the plugin's .cfg. Read-only to mods; the plugin owns them.</summary>
        public static DataOptions Options => _options;

        /// <summary>Level, work totals and login history, or <c>null</c> before the game is up.</summary>
        public static PlayerView? Player => Read(GameDataMap.Player);

        /// <summary>Birthday, icon and presence status, or <c>null</c> before the game is up.</summary>
        public static ProfileView? Profile => Read(GameDataMap.Profile);

        /// <summary>Device-account flags. The device id itself is never exposed.</summary>
        public static AccountView? Account => Read(GameDataMap.Account);

        /// <summary>Timer configuration and presets, or <c>null</c> before the game is up.</summary>
        public static PomodoroView? Pomodoro => Read(GameDataMap.Pomodoro);

        /// <summary>Every todo list with its items in the player's order. Empty before the game is up.</summary>
        public static IReadOnlyList<TodoListView> Todos =>
            Read(GameDataMap.Todos) ?? Array.Empty<TodoListView>();

        /// <summary>
        /// Every note page in the player's order. Bodies are subject to <c>Data.IncludeNoteText</c>
        /// and to whether the game has the page in memory - see <see cref="NotePageView"/>.
        /// </summary>
        public static IReadOnlyList<NotePageView> Notes =>
            Read(sdm => GameDataMap.Notes(sdm, _options)) ?? Array.Empty<NotePageView>();

        /// <summary>Tracked habits with a recent completion window. Empty before the game is up.</summary>
        public static IReadOnlyList<HabitView> Habits =>
            Read(sdm => GameDataMap.Habits(sdm, _options)) ?? Array.Empty<HabitView>();

        /// <summary>A recent window of calendar days, or <c>null</c> before the game is up.</summary>
        public static CalendarView? Calendar => Read(sdm => GameDataMap.Calendar(sdm, _options));

        /// <summary>Volumes, language and notification flags, or <c>null</c> before the game is up.</summary>
        public static SettingsView? Settings => Read(GameDataMap.Settings);

        /// <summary>Playlist preferences and the imported local library.</summary>
        public static MusicView? Music => Read(GameDataMap.Music);

        /// <summary>Active window views and the ambient sound mix.</summary>
        public static EnvironmentView? Environment => Read(GameDataMap.Environment);

        /// <summary>Story preference and scenario progress.</summary>
        public static StoryView? Story => Read(GameDataMap.Story);

        /// <summary>Unlocked achievements.</summary>
        public static AchievementsView? Achievements => Read(GameDataMap.Achievements);

        /// <summary>The guarded write API. See <see cref="GameDataWriter"/>.</summary>
        public static GameDataWriter Write { get; } = new GameDataWriter();

        /// <summary>Providers currently registered, in registration order.</summary>
        public static IReadOnlyList<IGameDataProvider> Providers
        {
            get
            {
                lock (Gate)
                {
                    return ProviderList.ToArray();
                }
            }
        }

        /// <summary>
        /// Publishes a mod's own section onto the bus. It is captured into
        /// <see cref="GameDataSnapshot.Extra"/> on every snapshot, under
        /// <see cref="IGameDataProvider.SectionName"/>. Registering a name that is already taken
        /// replaces the previous provider, so a plugin whose Awake runs twice cannot double-publish.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
        /// <exception cref="ArgumentException">Its <c>SectionName</c> is null or empty.</exception>
        public static void RegisterProvider(IGameDataProvider provider)
        {
            if (provider is null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (string.IsNullOrEmpty(provider.SectionName))
            {
                throw new ArgumentException("a provider needs a non-empty SectionName", nameof(provider));
            }

            lock (Gate)
            {
                for (var i = 0; i < ProviderList.Count; i++)
                {
                    if (string.Equals(ProviderList[i].SectionName, provider.SectionName, StringComparison.Ordinal))
                    {
                        ProviderList[i] = provider;
                        CwyLsModLoader.Log.LogDebug($"DATA provider replaced: {provider.SectionName}");
                        return;
                    }
                }

                ProviderList.Add(provider);
            }

            CwyLsModLoader.Log.LogDebug($"DATA provider registered: {provider.SectionName}");
        }

        /// <summary>
        /// Removes a provider by section name. Returns true when one was removed.
        /// </summary>
        public static bool UnregisterProvider(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName))
            {
                return false;
            }

            lock (Gate)
            {
                for (var i = 0; i < ProviderList.Count; i++)
                {
                    if (string.Equals(ProviderList[i].SectionName, sectionName, StringComparison.Ordinal))
                    {
                        ProviderList.RemoveAt(i);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Builds a complete snapshot of everything above, plus every registered provider's
        /// section. Main thread only; this is the expensive call on the bus, so let the plugin's
        /// debounced writer produce the file rather than calling this on a timer.
        /// </summary>
        public static GameDataSnapshot Snapshot()
        {
            var sdm = SaveDataAccess.Resolve();
            var ready = IsReady;

            // Each section is isolated: one that throws becomes null in the document and a logged
            // error, rather than costing the sidecar the whole file.
            return new GameDataSnapshot
            {
                Time = DateTimeOffset.Now,
                GameVersion = Application.version,
                PluginVersion = GameDataPlugin.PluginVersion,
                IsReady = ready,
                Player = Section(sdm, "player", GameDataMap.Player),
                Profile = Section(sdm, "profile", GameDataMap.Profile),
                Account = Section(sdm, "account", GameDataMap.Account),
                Pomodoro = Section(sdm, "pomodoro", GameDataMap.Pomodoro),
                Todos = Section(sdm, "todos", GameDataMap.Todos) ?? Array.Empty<TodoListView>(),
                Notes = Section(sdm, "notes", x => GameDataMap.Notes(x, _options))
                        ?? Array.Empty<NotePageView>(),
                Habits = Section(sdm, "habits", x => GameDataMap.Habits(x, _options))
                         ?? Array.Empty<HabitView>(),
                Calendar = Section(sdm, "calendar", x => GameDataMap.Calendar(x, _options)),
                Settings = Section(sdm, "settings", GameDataMap.Settings),
                Music = Section(sdm, "music", GameDataMap.Music),
                Environment = Section(sdm, "environment", GameDataMap.Environment),
                Story = Section(sdm, "story", GameDataMap.Story),
                Achievements = Section(sdm, "achievements", GameDataMap.Achievements),
                Extra = CaptureProviders(),
            };
        }

        /// <summary>Maps one snapshot section, or returns null when it is unavailable or failed.</summary>
        private static T? Section<T>(Game.SaveDataManager? sdm, string name, Func<Game.SaveDataManager, T> map)
            where T : class
        {
            if (sdm is null)
            {
                return null;
            }

            try
            {
                return map(sdm);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"DATA snapshot section '{name}' failed and was written as null: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Installs the config-backed options. Called once by the plugin's Awake.</summary>
        internal static void UseOptions(DataOptions options) =>
            _options = options ?? throw new ArgumentNullException(nameof(options));

        /// <summary>
        /// Publishes a change to every subscriber. Each sink is isolated: a throwing event handler
        /// is logged and the R3 stream still gets the notification, and vice versa.
        /// </summary>
        internal static void Raise(GameDataSection section, string reason)
        {
            if (section == GameDataSection.Notes || section == GameDataSection.All)
            {
                GameDataMap.InvalidateNoteText();
            }

            var change = new GameDataChanged(section, reason);

            // The diagnostic stream first: a subscriber that throws must not cost the trace the
            // record of what changed. TraceBus never throws back into a producer.
            ConsoleEvents.Emit(
                TraceSys,
                "changed",
                ConsoleTags.Data,
                change.Section.ToString() + " changed",
                "reason",
                ("section", change.Section.ToString()),
                ("reason", change.Reason),
                ("time", MiniJson.Iso8601(change.Time)));

            try
            {
                Changed?.Invoke(change);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"DATA a Changed handler threw for {change}: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                ChangedSubject.OnNext(change);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"DATA ChangedObservable threw for {change}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static Dictionary<string, object?> CaptureProviders()
        {
            var captured = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var provider in Providers)
            {
                try
                {
                    captured[provider.SectionName] = provider.Capture();
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        $"DATA provider '{provider.SectionName}' threw during capture, section skipped: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            return captured;
        }

        /// <summary>
        /// Runs a mapper against the live manager, or returns null when the game has not built one.
        /// A mapper that throws is logged rather than propagated: a property getter on a read-only
        /// bus must not be able to take a mod down.
        /// </summary>
        private static T? Read<T>(Func<Game.SaveDataManager, T> map)
            where T : class
        {
            var sdm = SaveDataAccess.Resolve();
            if (sdm is null)
            {
                return null;
            }

            try
            {
                return map(sdm);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError($"DATA read failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
