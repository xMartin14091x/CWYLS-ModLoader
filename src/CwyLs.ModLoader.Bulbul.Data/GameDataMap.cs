using System;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core.Data;
using Game = global::Bulbul;
using Scopes = global::NestopiSystem.DIContainers;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// Copies the game's live save-data objects into the Unity-free views in
    /// <c>CwyLs.ModLoader.Core.Data</c>. Every method returns a fresh immutable view: nothing the bus
    /// hands out is a reference into the game's state, because mutating one of those from a mod
    /// would change the player's data without ever going through the game's own save path.
    /// </summary>
    /// <remarks>
    /// All of this runs on Unity's main thread (the callers are Harmony postfixes and a
    /// MonoBehaviour's Update). Two of the reads page files in from disk - a note body that is not
    /// in the game's three-page cache, and a calendar or habit month the game has not opened yet -
    /// so note bodies are memoised until the Notes section changes.
    /// </remarks>
    internal static class GameDataMap
    {
        private static readonly object NoteGate = new object();
        private static readonly Dictionary<ulong, string?> NoteTextCache = new Dictionary<ulong, string?>();

        /// <summary>
        /// Drops the memoised note bodies. Called whenever the Notes section changes, so an edit
        /// the player just made is never served from the cache.
        /// </summary>
        internal static void InvalidateNoteText()
        {
            lock (NoteGate)
            {
                NoteTextCache.Clear();
            }
        }

        // ---------------------------------------------------------------- player / profile

        /// <summary>Maps <c>PlayerDataV3</c> and its nested <c>LevelData</c>.</summary>
        internal static PlayerView Player(Game.SaveDataManager sdm)
        {
            var data = sdm.PlayerData;
            var level = data.LevelData ?? new Game.LevelData();

            return new PlayerView
            {
                Level = new LevelView
                {
                    CurrentLevel = level.CurrentLevel,
                    CurrentExp = level.CurrentExp,
                    NextLevelNecessaryExp = level.NextLevelNecessaryExp,
                },
                TotalLoginCount = data.TotalLoginCount,
                LastLoginAt = LastLogin(data),
                IsNeedTutorial = data.IsNeedTutorial,
                CurrentWorkSeconds = data.CurrentWorkSeconds,
                PomodoroTotalWorkSeconds = data.PomodoroTotalWorkSeconds,
                LastStoryUnlockWorkSeconds = data.LastStoryUnlockWorkSeconds,
                HasReceivedFirstPoint = data.HasReceivedFirstPoint,
            };
        }

        /// <summary>
        /// The game's <c>LastLoginDateTime</c> is a bare <c>DateTime.ParseExact</c> over a private
        /// string, so it throws on a first run or a corrupted field. The bus reports null instead.
        /// </summary>
        private static DateTime? LastLogin(Game.PlayerDataV3 data)
        {
            try
            {
                return data.IsFirstLogin ? (DateTime?)null : data.LastLoginDateTime;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"DATA player.lastLoginAt unreadable: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Maps <c>ProfileData</c>. The custom icon's bytes are deliberately not copied.</summary>
        internal static ProfileView Profile(Game.SaveDataManager sdm)
        {
            var data = sdm.ProfileData;
            var icon = data.ProfileIconData;

            return new ProfileView
            {
                BirthdayMonth = data.BirthdayMonth,
                BirthdayDay = data.BirthdayDay,
                IconType = data.IconType.ToString(),
                Status = data.Status.ToString(),
                HasCustomIcon = icon?.ProfileIconBytes is { Length: > 0 },
            };
        }

        /// <summary>Maps <c>AccountData</c> through the redaction rule; the device id never leaves.</summary>
        internal static AccountView Account(Game.SaveDataManager sdm) =>
            GameDataPrivacy.RedactAccount(sdm.AccountData?.DeviceID);

        // ---------------------------------------------------------------- pomodoro

        /// <summary>Maps <c>PomodoroData</c>, unwrapping its R3 reactive properties.</summary>
        internal static PomodoroView Pomodoro(Game.SaveDataManager sdm)
        {
            var data = sdm.PomodoroData;
            var presets = new List<PomodoroPresetView>();

            if (data.PresetData != null)
            {
                foreach (var preset in data.PresetData)
                {
                    if (preset is null)
                    {
                        continue;
                    }

                    presets.Add(new PomodoroPresetView
                    {
                        Id = preset.UniqueID,
                        Name = preset.Name ?? string.Empty,
                        LoopCount = preset.LoopCount,
                        WorkMinutes = preset.WorkMinutes,
                        BreakMinutes = preset.BreakMinutes,
                    });
                }
            }

            return new PomodoroView
            {
                LoopCount = data.LoopCount?.Value ?? 0,
                WorkMinutes = data.WorkMinutes?.Value ?? 0,
                BreakMinutes = data.BreakMinutes?.Value ?? 0,
                Mode = (TimerModeView)(int)data.CurrentTimerMode,
                LastUpdatedWorkAt = data.LastUpdatedWorkDateTime,
                LastUpdatedWorkSeconds = data.LastUpdatedWorkSeconds,
                Presets = presets,
            };
        }

        // ---------------------------------------------------------------- todos

        /// <summary>
        /// Maps every todo list. Items follow <c>TodoOrderList</c>, which is the order the player
        /// sees; an id in the order list with no matching item (the game tolerates that) is skipped,
        /// and an item missing from the order list is appended so nothing is silently lost.
        /// </summary>
        internal static IReadOnlyList<TodoListView> Todos(Game.SaveDataManager sdm)
        {
            var lists = new List<TodoListView>();
            var all = sdm.TodoAllData;
            if (all?.TodoListDic is null)
            {
                return lists;
            }

            foreach (var entry in all.TodoListDic)
            {
                var list = entry.Value;
                if (list is null)
                {
                    continue;
                }

                lists.Add(new TodoListView
                {
                    Id = list.UniqueID,
                    Title = list.TitleText ?? string.Empty,
                    Items = TodoItems(list),
                });
            }

            return lists;
        }

        private static IReadOnlyList<TodoView> TodoItems(Game.TodoListData list)
        {
            var items = new List<TodoView>();
            var taken = new HashSet<ulong>();

            if (list.TodoOrderList != null)
            {
                foreach (var id in list.TodoOrderList)
                {
                    if (list.TodoDic != null && list.TodoDic.TryGetValue(id, out var todo) && todo != null)
                    {
                        items.Add(Todo(todo));
                        taken.Add(id);
                    }
                }
            }

            if (list.TodoDic != null)
            {
                foreach (var entry in list.TodoDic)
                {
                    if (entry.Value != null && !taken.Contains(entry.Key))
                    {
                        items.Add(Todo(entry.Value));
                    }
                }
            }

            return items;
        }

        private static TodoView Todo(Game.TodoData todo) =>
            new TodoView
            {
                Id = todo.UniqueID,
                Text = todo.TodoText ?? string.Empty,
                State = todo.CurrentState == global::TodoState.Complete
                    ? TodoViewState.Complete
                    : TodoViewState.Working,
                CompletedAt = todo.Completed,
                ExpiresAt = todo.Expire,
            };

        // ---------------------------------------------------------------- notes

        /// <summary>
        /// Maps every note page in the player's order. Titles come from the game's cheap title
        /// index; bodies come from the game's three-page cache when they are there, otherwise from
        /// a memoised disk read, and not at all when <c>Data.IncludeNoteText</c> is off.
        /// </summary>
        internal static IReadOnlyList<NotePageView> Notes(Game.SaveDataManager sdm, DataOptions options)
        {
            var pages = new List<NotePageView>();
            var note = sdm.NoteData;
            var index = note?.NoteList;
            if (note is null || index?.PageOrderList is null || index.Titles is null)
            {
                return pages;
            }

            var cached = new Dictionary<ulong, Game.PageDataV2>();
            if (note.PageCache != null)
            {
                foreach (var page in note.PageCache)
                {
                    if (page != null)
                    {
                        cached[page.UniqueID] = page;
                    }
                }
            }

            var order = 0;
            foreach (var id in index.PageOrderList)
            {
                if (!index.Titles.TryGetValue(id, out var title))
                {
                    continue;
                }

                cached.TryGetValue(id, out var live);
                var text = options.IncludeNoteText ? Body(sdm, id, live) : null;

                pages.Add(new NotePageView
                {
                    Id = id,
                    Order = order++,
                    Title = title ?? string.Empty,
                    Text = text,
                    TextAvailable = options.IncludeNoteText && text != null,
                    ScrollValue = live?.ScrollValue ?? 0f,
                });
            }

            return pages;
        }

        /// <summary>
        /// The body of one page. The in-memory copy wins - it may hold an edit the player has not
        /// saved yet. Otherwise the page is read straight off disk with
        /// <c>SaveDataManager.LoadPageData</c> rather than <c>NoteDataV2.GetPageData</c>, because
        /// the latter would insert into the game's own three-page cache and evict a page the note
        /// UI is holding.
        /// </summary>
        private static string? Body(Game.SaveDataManager sdm, ulong id, Game.PageDataV2? live)
        {
            if (live != null)
            {
                return live.MainText ?? string.Empty;
            }

            lock (NoteGate)
            {
                if (NoteTextCache.TryGetValue(id, out var memo))
                {
                    return memo;
                }
            }

            string? text;
            try
            {
                text = sdm.LoadPageData(id)?.MainText ?? string.Empty;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"DATA note page {id} body unreadable: {ex.GetType().Name}: {ex.Message}");
                text = null;
            }

            lock (NoteGate)
            {
                NoteTextCache[id] = text;
            }

            return text;
        }

        // ---------------------------------------------------------------- habits

        /// <summary>
        /// Maps every habit with a window of recent days. The game keeps completion ticks in one
        /// file per month, so this pages in at most two months.
        /// </summary>
        internal static IReadOnlyList<HabitView> Habits(Game.SaveDataManager sdm, DataOptions options)
        {
            var views = new List<HabitView>();
            var headers = sdm.AllHabitHeaderData?.Habits;
            if (headers is null)
            {
                return views;
            }

            var days = RecentDates(options.RecentDays);
            var months = MonthlyHabitData(sdm, days);

            foreach (var header in headers)
            {
                if (header is null)
                {
                    continue;
                }

                views.Add(new HabitView
                {
                    Id = header.HabitID ?? string.Empty,
                    Title = header.Title ?? string.Empty,
                    CreatedDate = header.CreatedDate,
                    ScheduledDays = ScheduledDays(header),
                    RecentDays = HabitDays(header, days, months),
                });
            }

            return views;
        }

        private static IReadOnlyList<DayOfWeek> ScheduledDays(Game.HabitHeaderData header)
        {
            var days = new List<DayOfWeek>(7);
            foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
            {
                if (header.IsDayOfWeekEnabled(day))
                {
                    days.Add(day);
                }
            }

            return days;
        }

        private static IReadOnlyList<HabitDayView> HabitDays(
            Game.HabitHeaderData header,
            IReadOnlyList<DateTime> days,
            IReadOnlyDictionary<(int, int), Game.HabitAllMonthlyData?> months)
        {
            var result = new List<HabitDayView>(days.Count);
            var id = header.HabitID ?? string.Empty;

            foreach (var day in days)
            {
                var completed = false;
                if (months.TryGetValue((day.Year, day.Month), out var monthly) &&
                    monthly?.HabitDic != null &&
                    monthly.HabitDic.TryGetValue(id, out var span) &&
                    span?.DateDataDic != null &&
                    span.DateDataDic.TryGetValue(day.Day, out var date) &&
                    date != null)
                {
                    completed = date.Completed;
                }

                result.Add(new HabitDayView { Date = day, Completed = completed });
            }

            return result;
        }

        private static IReadOnlyDictionary<(int, int), Game.HabitAllMonthlyData?> MonthlyHabitData(
            Game.SaveDataManager sdm, IReadOnlyList<DateTime> days)
        {
            var months = new Dictionary<(int, int), Game.HabitAllMonthlyData?>();
            foreach (var day in days)
            {
                var key = (day.Year, day.Month);
                if (months.ContainsKey(key))
                {
                    continue;
                }

                try
                {
                    months[key] = sdm.TryGetOrLoadHabitsMonthlyData(day.Year, day.Month, out var monthly)
                        ? monthly
                        : null;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"DATA habit month {day.Year:D4}-{day.Month:D2} unreadable: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return months;
        }

        // ---------------------------------------------------------------- calendar

        /// <summary>
        /// Maps a recent window of calendar days: work seconds, completed-todo count and the
        /// diary entry (subject to <c>Data.IncludeNoteText</c>).
        /// </summary>
        internal static CalendarView Calendar(Game.SaveDataManager sdm, DataOptions options)
        {
            var entries = new List<CalendarDayView>();
            var calendar = sdm.CalendarData;
            if (calendar is null)
            {
                return new CalendarView { RecentDays = entries };
            }

            foreach (var day in RecentDates(options.RecentDays))
            {
                try
                {
                    // GetMonthlyData reads the month's file once and keeps it in the game's own
                    // ten-month cache, so this is a lookup after the first day of each month.
                    var monthly = calendar.GetMonthlyData(day.Year, day.Month);
                    if (monthly?.DiaryList is null ||
                        !monthly.DiaryList.TryGetValue(day.Day, out var data) ||
                        data is null)
                    {
                        continue;
                    }

                    entries.Add(new CalendarDayView
                    {
                        Date = day,
                        WorkSeconds = data.WorkTimeSeconds,
                        CompletedTodoCount = data.CompleteTodoListDic?.Count ?? 0,
                        DiaryText = GameDataPrivacy.ApplyTextPolicy(data.DiaryText, options.IncludeNoteText),
                    });
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"DATA calendar day {day:yyyy-MM-dd} unreadable: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return new CalendarView { RecentDays = entries };
        }

        /// <summary>The last <paramref name="count"/> days ending today, oldest first.</summary>
        private static IReadOnlyList<DateTime> RecentDates(int count)
        {
            var span = count < 1 ? 1 : count;
            var today = DateTime.Now.Date;
            var days = new List<DateTime>(span);
            for (var back = span - 1; back >= 0; back--)
            {
                days.Add(today.AddDays(-back));
            }

            return days;
        }

        // ---------------------------------------------------------------- settings

        /// <summary>Maps <c>SettingData</c>: the volume buses plus the flags a mod is likely to care about.</summary>
        internal static SettingsView Settings(Game.SaveDataManager sdm)
        {
            var data = sdm.SettingData;

            return new SettingsView
            {
                Language = data.GameLanguage?.Value.ToString() ?? string.Empty,
                TimeFormat = data.TimeFormat?.Value.ToString() ?? string.Empty,
                IsAlwaysOnTop = data.IsAlwaysOnTop,
                EnablePictureInPicture = data.EnablePictureInPicture,
                WindowMode = data.WindowMode?.Value.ToString() ?? string.Empty,
                ActiveFramerate = data.ActiveFramerate?.Value ?? 0,
                IsPlayPomodoroSe = data.IsPlayPomodoroSe?.Value ?? false,
                IsPlaySelfTalk = data.IsPlaySelfTalk?.Value ?? false,
                IsNotificationPomodoro = data.IsNotificationPomodoro?.Value ?? false,
                IsNotificationReminder = data.IsNotificationReminder?.Value ?? false,
                Volumes = Volumes(data),
            };
        }

        private static IReadOnlyList<VolumeView> Volumes(Game.SettingData data)
        {
            // Read the fields rather than SettingData.GetVolumeInfo: that method logs an error for
            // any mixer type it does not know, and a game update adding one would spam the log.
            var buses = new (string Name, global::VolumeInfo? Info)[]
            {
                ("Master", data.MasterVolumeInfo),
                ("Music", data.MusicVolumeInfo),
                ("SystemSE", data.SystemSEVolumeInfo),
                ("Voice", data.VoiceVolumeInfo),
                ("AmbientBGM", data.AmbientBGMVolumeInfo),
                ("AmbientSE", data.AmbientSEVolumeInfo),
                ("TimerSE", data.TimerSEVolumeInfo),
            };

            var views = new List<VolumeView>(buses.Length);
            foreach (var bus in buses)
            {
                if (bus.Info is null)
                {
                    continue;
                }

                views.Add(new VolumeView
                {
                    Name = bus.Name,
                    RawValue = bus.Info.RawValue?.Value ?? 0f,
                    IsMute = bus.Info.IsMute?.Value ?? false,
                });
            }

            return views;
        }

        // ---------------------------------------------------------------- music

        /// <summary>
        /// Maps <c>MusicSettingV2</c> and <c>LocalMusicSetting</c>, and asks the running
        /// <c>MusicService</c> what is playing. The service lives in the project DI scope and does
        /// not exist before the game is up, so a miss is normal and silent.
        /// </summary>
        internal static MusicView Music(Game.SaveDataManager sdm)
        {
            var setting = sdm.MusicSetting;
            var tracks = new List<LocalTrackView>();

            var local = sdm.LocalMusicSetting?.LocalAudioDatas;
            if (local != null)
            {
                foreach (var track in local)
                {
                    if (track is null)
                    {
                        continue;
                    }

                    tracks.Add(new LocalTrackView
                    {
                        Uuid = track.UUID ?? string.Empty,
                        FileName = GameDataPrivacy.RedactLocalMusicPath(track.FilePath),
                    });
                }
            }

            return new MusicView
            {
                IsGameStartPlayMusic = setting?.IsGameStartPlayMusic ?? false,
                IsShufflePlayMusic = setting?.IsShufflePlayMusic ?? false,
                CurrentAudioTag = setting?.CurrentAudioTag?.Value.ToString() ?? string.Empty,
                FavoriteCount = setting?.FavoriteAudioUUIDs?.Count ?? 0,
                ExcludedCount = setting?.ExcludedFromPlaylistUUIDs?.Count ?? 0,
                LocalTracks = tracks,
                NowPlayingTitle = NowPlaying(),
            };
        }

        private static string? NowPlaying()
        {
            try
            {
                return Scopes.ProjectLifetimeScope.TryResolve<global::MusicService>(out var music)
                    ? music?.PlayingMusic?.Title
                    : null;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug($"DATA now-playing unavailable: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------- environment

        /// <summary>Maps <c>EnviromentData</c> down to what is actually on: active views, audible layers.</summary>
        internal static EnvironmentView Environment(Game.SaveDataManager sdm)
        {
            var data = sdm.EnviromentData;
            var views = new List<string>();
            var sounds = new List<AmbientSoundView>();

            if (data?.WindowViewDic != null)
            {
                foreach (var entry in data.WindowViewDic)
                {
                    if (entry.Value?.IsActive == true)
                    {
                        views.Add(entry.Key.ToString());
                    }
                }
            }

            if (data?.AmbientSoundDic != null)
            {
                foreach (var entry in data.AmbientSoundDic)
                {
                    var sound = entry.Value;
                    if (sound is null || sound.IsMuteAmbient || sound.SoundVolume <= 0f)
                    {
                        continue;
                    }

                    sounds.Add(new AmbientSoundView
                    {
                        Type = entry.Key.ToString(),
                        Volume = sound.SoundVolume,
                        IsMute = sound.IsMuteAmbient,
                    });
                }
            }

            return new EnvironmentView { ActiveWindowViews = views, ActiveAmbientSounds = sounds };
        }

        // ---------------------------------------------------------------- story / achievements

        /// <summary>Maps <c>StoryData</c> together with <c>ScenarioProgressData</c>.</summary>
        internal static StoryView Story(Game.SaveDataManager sdm)
        {
            var story = sdm.StoryData;
            var progress = sdm.ScenarioProgressData;
            var played = progress?.PlayedScenarioGroupIDs;

            return new StoryView
            {
                IsUseAuto = story?.IsUseAuto?.Value ?? false,
                FinishedMainEpisodeNumber = progress?.FinishReadMainEpisodeNumber ?? 0f,
                NextEpisodeNumber = progress?.NextEpisodeNumber ?? 0f,
                NextEpisodeUnlockLevel = progress?.NextEpisodeUnlockLevel ?? 0,
                PlayedScenarioGroupCount = played?.Count ?? 0,
                PlayedScenarioGroupIds = played is null
                    ? Array.Empty<string>()
                    : played.ToArray(),
            };
        }

        /// <summary>
        /// Maps <c>AchievementSaveData</c>. The game exposes only <c>IsUnlocked(string)</c>, so
        /// the unlocked list is read reflectively from the private backing field; if a game update
        /// renames it, the view reports <c>available: false</c> rather than an empty list, which
        /// would read as "the player has unlocked nothing".
        /// </summary>
        internal static AchievementsView Achievements(Game.SaveDataManager sdm)
        {
            const string fieldName = "_unlockedAchievements";
            var data = sdm.AchievementSaveData;
            if (data is null)
            {
                return new AchievementsView { Available = false };
            }

            try
            {
                var field = typeof(Game.AchievementSaveData)
                    .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                if (field is null)
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"DATA AchievementSaveData.{fieldName} not found; achievements unavailable");
                    return new AchievementsView { Available = false };
                }

                var ids = field.GetValue(data) as List<string>;
                return new AchievementsView
                {
                    Available = true,
                    UnlockedIds = ids is null ? Array.Empty<string>() : ids.ToArray(),
                };
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"DATA achievements unreadable: {ex.GetType().Name}: {ex.Message}");
                return new AchievementsView { Available = false };
            }
        }
    }
}
