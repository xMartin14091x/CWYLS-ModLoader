using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>
    /// Projects the data-bus snapshot (<see cref="GameDataSnapshot"/>) onto the save-backed
    /// sections of <c>/v1/state</c>. The member list is the contract in <c>Core/Api/README.md</c>;
    /// every member is chosen here explicitly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Privacy (project rule): the text of notes, the diary, todos, habits and
    /// calendar entries is never copied - only ids, counts, lengths, dates and states. Todo list
    /// titles, note titles and habit titles are player text too and get the same treatment.
    /// Profile and account data, and sections other mods publish (<c>extra</c>), are left out;
    /// only the names of the extra sections are listed.
    /// </para>
    /// <para>The music section names the current track (game, pack or imported file name), not the imported library.</para>
    /// </remarks>
    public static class ApiStateProjection
    {
        /// <summary>The save-backed members of <c>/v1/state</c>, in document order.</summary>
        /// <param name="snapshot">The data-bus snapshot; null gives <c>ready: false</c> and no sections.</param>
        public static JsonObject FromSnapshot(GameDataSnapshot? snapshot)
        {
            if (snapshot is null)
            {
                return new JsonObject { ["ready"] = false };
            }

            var o = new JsonObject
            {
                ["ready"] = snapshot.IsReady,
                ["gameVersion"] = snapshot.GameVersion,
                ["dataAt"] = ApiJson.Time(snapshot.Time),
                ["player"] = Player(snapshot.Player),
                ["pomodoro"] = Pomodoro(snapshot.Pomodoro),
                ["todos"] = Todos(snapshot.Todos),
                ["notes"] = Notes(snapshot.Notes),
                ["habits"] = Habits(snapshot.Habits),
                ["calendar"] = Calendar(snapshot.Calendar),
                ["settings"] = Settings(snapshot.Settings),
                ["music"] = Music(snapshot.Music),
                ["environment"] = Environment(snapshot.Environment),
                ["story"] = Story(snapshot.Story),
                ["achievements"] = snapshot.Achievements is null
                    ? null
                    : new JsonObject
                    {
                        ["available"] = snapshot.Achievements.Available,
                        ["unlockedCount"] = snapshot.Achievements.UnlockedCount,
                    },
                ["extraSections"] = SortedKeys(snapshot.Extra),
            };
            return o;
        }

        private static JsonObject? Player(PlayerView? p) =>
            p is null
                ? null
                : new JsonObject
                {
                    ["level"] = p.Level.CurrentLevel,
                    ["exp"] = p.Level.CurrentExp,
                    ["nextLevelExp"] = p.Level.NextLevelNecessaryExp,
                    ["totalLoginCount"] = p.TotalLoginCount,
                    ["lastLoginAt"] = ApiJson.Time(p.LastLoginAt),
                    ["currentWorkSeconds"] = p.CurrentWorkSeconds,
                    ["pomodoroTotalWorkSeconds"] = p.PomodoroTotalWorkSeconds,
                };

        private static JsonObject? Pomodoro(PomodoroView? p) =>
            p is null
                ? null
                : new JsonObject
                {
                    ["mode"] = ApiJson.Camel(p.Mode.ToString()),
                    ["workMinutes"] = p.WorkMinutes,
                    ["breakMinutes"] = p.BreakMinutes,
                    ["loopCount"] = p.LoopCount,
                    ["presetCount"] = p.Presets.Count,
                };

        private static JsonObject Todos(IReadOnlyList<TodoListView> lists)
        {
            var listJson = new List<object?>(lists.Count);
            int items = 0, open = 0, completed = 0;
            foreach (var list in lists)
            {
                var itemJson = new List<object?>(list.Items.Count);
                foreach (var item in list.Items)
                {
                    itemJson.Add(new JsonObject
                    {
                        ["id"] = item.Id,
                        ["state"] = item.State == TodoViewState.Complete ? "complete" : "open",
                        ["textLength"] = Length(item.Text),
                        ["completedAt"] = ApiJson.Time(item.CompletedAt),
                        ["expiresAt"] = ApiJson.Time(item.ExpiresAt),
                    });
                }

                items += list.Items.Count;
                open += list.OpenCount;
                completed += list.CompletedCount;
                listJson.Add(new JsonObject
                {
                    ["id"] = list.Id,
                    ["titleLength"] = Length(list.Title),
                    ["itemCount"] = list.Items.Count,
                    ["openCount"] = list.OpenCount,
                    ["completedCount"] = list.CompletedCount,
                    ["items"] = itemJson,
                });
            }

            return new JsonObject
            {
                ["listCount"] = lists.Count,
                ["itemCount"] = items,
                ["openCount"] = open,
                ["completedCount"] = completed,
                ["lists"] = listJson,
            };
        }

        private static JsonObject Notes(IReadOnlyList<NotePageView> pages)
        {
            var pageJson = new List<object?>(pages.Count);
            foreach (var page in pages)
            {
                pageJson.Add(new JsonObject
                {
                    ["id"] = page.Id,
                    ["order"] = page.Order,
                    ["titleLength"] = Length(page.Title),
                    ["textLength"] = page.TextAvailable && page.Text != null ? page.Text.Length : (int?)null,
                });
            }

            return new JsonObject { ["pageCount"] = pages.Count, ["pages"] = pageJson };
        }

        private static JsonObject Habits(IReadOnlyList<HabitView> habits)
        {
            var habitJson = new List<object?>(habits.Count);
            foreach (var habit in habits)
            {
                var days = new List<object?>(habit.RecentDays.Count);
                foreach (var day in habit.RecentDays)
                {
                    days.Add(new JsonObject { ["date"] = ApiJson.Date(day.Date), ["completed"] = day.Completed });
                }

                var scheduled = new List<object?>(habit.ScheduledDays.Count);
                foreach (var weekday in habit.ScheduledDays)
                {
                    scheduled.Add(ApiJson.Camel(weekday.ToString()));
                }

                habitJson.Add(new JsonObject
                {
                    ["id"] = habit.Id,
                    ["titleLength"] = Length(habit.Title),
                    ["createdDate"] = ApiJson.Date(habit.CreatedDate),
                    ["scheduledDays"] = scheduled,
                    ["recentCompletedCount"] = habit.RecentCompletedCount,
                    ["recentDays"] = days,
                });
            }

            return new JsonObject { ["count"] = habits.Count, ["items"] = habitJson };
        }

        private static JsonObject? Calendar(CalendarView? calendar)
        {
            if (calendar is null)
            {
                return null;
            }

            var days = new List<object?>(calendar.RecentDays.Count);
            foreach (var day in calendar.RecentDays)
            {
                days.Add(new JsonObject
                {
                    ["date"] = ApiJson.Date(day.Date),
                    ["workSeconds"] = day.WorkSeconds,
                    ["completedTodoCount"] = day.CompletedTodoCount,
                    ["diaryLength"] = day.DiaryText?.Length,
                });
            }

            return new JsonObject { ["recentWorkSeconds"] = calendar.RecentWorkSeconds, ["days"] = days };
        }

        private static JsonObject? Settings(SettingsView? s)
        {
            if (s is null)
            {
                return null;
            }

            var volumes = new List<object?>(s.Volumes.Count);
            foreach (var v in s.Volumes)
            {
                volumes.Add(new JsonObject { ["name"] = v.Name, ["value"] = v.RawValue, ["mute"] = v.IsMute });
            }

            return new JsonObject
            {
                ["language"] = s.Language,
                ["timeFormat"] = s.TimeFormat,
                ["windowMode"] = s.WindowMode,
                ["alwaysOnTop"] = s.IsAlwaysOnTop,
                ["activeFramerate"] = s.ActiveFramerate,
                ["selfTalk"] = s.IsPlaySelfTalk,
                ["pomodoroSe"] = s.IsPlayPomodoroSe,
                ["volumes"] = volumes,
            };
        }

        private static JsonObject? Music(MusicView? m) =>
            m is null
                ? null
                : new JsonObject
                {
                    ["nowPlaying"] = m.NowPlayingTitle,
                    ["shuffle"] = m.IsShufflePlayMusic,
                    ["playOnStart"] = m.IsGameStartPlayMusic,
                    ["currentTag"] = m.CurrentAudioTag,
                    ["favoriteCount"] = m.FavoriteCount,
                    ["excludedCount"] = m.ExcludedCount,
                    ["localTrackCount"] = m.LocalTracks.Count,
                };

        private static JsonObject? Environment(EnvironmentView? e)
        {
            if (e is null)
            {
                return null;
            }

            var views = new List<object?>(e.ActiveWindowViews.Count);
            foreach (var v in e.ActiveWindowViews)
            {
                views.Add(v);
            }

            var sounds = new List<object?>(e.ActiveAmbientSounds.Count);
            foreach (var s in e.ActiveAmbientSounds)
            {
                sounds.Add(new JsonObject { ["type"] = s.Type, ["volume"] = s.Volume, ["mute"] = s.IsMute });
            }

            return new JsonObject { ["windowViews"] = views, ["ambientSounds"] = sounds };
        }

        private static JsonObject? Story(StoryView? s) =>
            s is null
                ? null
                : new JsonObject
                {
                    ["useAuto"] = s.IsUseAuto,
                    ["finishedMainEpisode"] = s.FinishedMainEpisodeNumber,
                    ["nextEpisode"] = s.NextEpisodeNumber,
                    ["nextEpisodeUnlockLevel"] = s.NextEpisodeUnlockLevel,
                    ["playedScenarioGroupCount"] = s.PlayedScenarioGroupCount,
                };

        private static List<object?> SortedKeys(IReadOnlyDictionary<string, object?> extra)
        {
            var keys = new List<string>(extra.Count);
            foreach (var pair in extra)
            {
                keys.Add(pair.Key);
            }

            keys.Sort(StringComparer.Ordinal);
            var list = new List<object?>(keys.Count);
            foreach (var key in keys)
            {
                list.Add(key);
            }

            return list;
        }

        private static int Length(string? text) => text?.Length ?? 0;
    }
}
