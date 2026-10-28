using System;
using System.Collections.Generic;
using System.Text.Json;
using CwyLs.ModLoader.Core.Data;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Pins the snapshot document itself: the fields a sidecar is told it can rely on, and the
    /// shapes that would be most expensive to get wrong (ids, note text presence, provider sections).
    /// </summary>
    public class DataSnapshotTests
    {
        private sealed class StubProvider : IGameDataProvider
        {
            public string SectionName => "test.mood";

            public object? Capture() => new Dictionary<string, object?> { ["mood"] = "calm", ["level"] = 3 };
        }

        private static GameDataSnapshot Sample() =>
            new GameDataSnapshot
            {
                Time = new DateTimeOffset(2026, 9, 22, 13, 42, 0, TimeSpan.FromHours(9)),
                GameVersion = "1.17.2",
                PluginVersion = "0.1.0",
                IsReady = true,
                Player = new PlayerView
                {
                    Level = new LevelView { CurrentLevel = 7, CurrentExp = 12.5f, NextLevelNecessaryExp = 40f },
                    TotalLoginCount = 31,
                    LastLoginAt = new DateTime(2026, 9, 22, 9, 0, 0, DateTimeKind.Unspecified),
                    CurrentWorkSeconds = 120.0,
                    PomodoroTotalWorkSeconds = 360_000.0,
                },
                Profile = new ProfileView { BirthdayMonth = 5, BirthdayDay = 4, IconType = "Icon2", Status = "Active" },
                Account = GameDataPrivacy.RedactAccount("device-abc-123"),
                Pomodoro = new PomodoroView
                {
                    LoopCount = 4,
                    WorkMinutes = 25,
                    BreakMinutes = 5,
                    Mode = TimerModeView.Pomodoro,
                    Presets = new[]
                    {
                        new PomodoroPresetView { Name = "深夜", LoopCount = 2, WorkMinutes = 50, BreakMinutes = 10 },
                    },
                },
                Todos = new[]
                {
                    new TodoListView
                    {
                        Id = 638_000_000_000_000_001UL,
                        Title = "今日",
                        Items = new[]
                        {
                            new TodoView { Id = 638_000_000_000_000_002UL, Text = "書く", State = TodoViewState.Working },
                            new TodoView
                            {
                                Id = 638_000_000_000_000_003UL,
                                Text = "読む",
                                State = TodoViewState.Complete,
                                CompletedAt = new DateTime(2026, 9, 22, 11, 0, 0, DateTimeKind.Unspecified),
                            },
                        },
                    },
                },
                Notes = new[]
                {
                    new NotePageView { Id = 10UL, Order = 0, Title = "idea", Text = "body", TextAvailable = true },
                    new NotePageView { Id = 11UL, Order = 1, Title = "locked", Text = null, TextAvailable = false },
                },
                Habits = new[]
                {
                    new HabitView
                    {
                        Id = "habit-1",
                        Title = "stretch",
                        CreatedDate = new DateTime(2026, 1, 1),
                        ScheduledDays = new[] { DayOfWeek.Monday, DayOfWeek.Friday },
                        RecentDays = new[]
                        {
                            new HabitDayView { Date = new DateTime(2026, 9, 21), Completed = true },
                            new HabitDayView { Date = new DateTime(2026, 9, 22), Completed = false },
                        },
                    },
                },
                Calendar = new CalendarView
                {
                    RecentDays = new[]
                    {
                        new CalendarDayView
                        {
                            Date = new DateTime(2026, 9, 22),
                            WorkSeconds = 5400,
                            CompletedTodoCount = 1,
                            DiaryText = "ไปได้สวย",
                        },
                    },
                },
                Settings = new SettingsView
                {
                    Language = "Japanese",
                    IsPlaySelfTalk = true,
                    Volumes = new[] { new VolumeView { Name = "Master", RawValue = 0.4f, IsMute = true } },
                },
                Music = new MusicView
                {
                    IsShufflePlayMusic = true,
                    LocalTracks = new[] { new LocalTrackView { Uuid = "u1", FileName = "lofi.ogg" } },
                    NowPlayingTitle = "rainy window",
                },
                Environment = new EnvironmentView { ActiveWindowViews = new[] { "Sunset" } },
                Story = new StoryView { FinishedMainEpisodeNumber = 12f, NextEpisodeNumber = 13f },
                Achievements = new AchievementsView { Available = true, UnlockedIds = new[] { "first_pomodoro" } },
                Extra = new Dictionary<string, object?> { ["test.mood"] = new StubProvider().Capture() },
            };

        private static JsonElement Root(GameDataSnapshot snapshot) =>
            JsonDocument.Parse(SnapshotSerializer.ToJson(snapshot)).RootElement.Clone();

        [Fact]
        public void An_empty_snapshot_still_serialises_with_its_schema_version()
        {
            var root = Root(new GameDataSnapshot());

            Assert.Equal(GameDataSnapshot.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1, GameDataSnapshot.CurrentSchemaVersion);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("player").ValueKind);
            Assert.Equal(0, root.GetProperty("todos").GetArrayLength());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("extra").ValueKind);
        }

        [Fact]
        public void Header_fields_are_present_and_typed()
        {
            var root = Root(Sample());

            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("2026-09-22T13:42:00.000+09:00", root.GetProperty("time").GetString());
            Assert.Equal("1.17.2", root.GetProperty("gameVersion").GetString());
            Assert.Equal("0.1.0", root.GetProperty("pluginVersion").GetString());
            Assert.True(root.GetProperty("isReady").GetBoolean());
        }

        [Fact]
        public void Every_documented_section_key_exists()
        {
            var root = Root(Sample());

            foreach (var key in new[]
                     {
                         "player", "profile", "account", "pomodoro", "todos", "notes", "habits",
                         "calendar", "settings", "music", "environment", "story", "achievements", "extra",
                     })
            {
                Assert.True(root.TryGetProperty(key, out _), "missing snapshot section: " + key);
            }
        }

        [Fact]
        public void Todo_ids_survive_as_strings_and_keep_their_order()
        {
            var root = Root(Sample());
            var list = root.GetProperty("todos")[0];

            Assert.Equal("638000000000000001", list.GetProperty("id").GetString());
            Assert.Equal("今日", list.GetProperty("title").GetString());
            Assert.Equal("書く", list.GetProperty("items")[0].GetProperty("text").GetString());
            Assert.Equal("Complete", list.GetProperty("items")[1].GetProperty("state").GetString());
            Assert.Equal(1, list.GetProperty("completedCount").GetInt32());
            Assert.Equal(1, list.GetProperty("openCount").GetInt32());
        }

        [Fact]
        public void A_withheld_note_body_is_null_and_flagged_distinctly_from_an_empty_one()
        {
            var root = Root(Sample());
            var notes = root.GetProperty("notes");

            Assert.True(notes[0].GetProperty("textAvailable").GetBoolean());
            Assert.Equal("body", notes[0].GetProperty("text").GetString());

            Assert.False(notes[1].GetProperty("textAvailable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, notes[1].GetProperty("text").ValueKind);
        }

        [Fact]
        public void Habit_history_carries_days_of_week_and_completion_counts()
        {
            var root = Root(Sample());
            var habit = root.GetProperty("habits")[0];

            Assert.Equal("Monday", habit.GetProperty("scheduledDays")[0].GetString());
            Assert.Equal(2, habit.GetProperty("recentDays").GetArrayLength());
            Assert.Equal(1, habit.GetProperty("recentCompletedCount").GetInt32());
        }

        [Fact]
        public void Calendar_totals_and_thai_diary_text_survive()
        {
            var root = Root(Sample());
            var calendar = root.GetProperty("calendar");

            Assert.Equal(5400, calendar.GetProperty("recentWorkSeconds").GetDouble());
            Assert.Equal("ไปได้สวย", calendar.GetProperty("recentDays")[0].GetProperty("diaryText").GetString());
        }

        [Fact]
        public void A_muted_volume_reports_zero_effective_level_but_keeps_the_slider_position()
        {
            var root = Root(Sample());
            var volume = root.GetProperty("settings").GetProperty("volumes")[0];

            Assert.Equal(0.4, Math.Round(volume.GetProperty("rawValue").GetDouble(), 3));
            Assert.True(volume.GetProperty("isMute").GetBoolean());
            Assert.Equal(0d, volume.GetProperty("effectiveValue").GetDouble());
        }

        [Fact]
        public void A_provider_section_lands_under_extra_keyed_by_its_name()
        {
            var root = Root(Sample());
            var mood = root.GetProperty("extra").GetProperty("test.mood");

            Assert.Equal("calm", mood.GetProperty("mood").GetString());
            Assert.Equal(3, mood.GetProperty("level").GetInt32());
        }

        [Fact]
        public void The_snapshot_never_contains_the_device_id()
        {
            var json = SnapshotSerializer.ToJson(Sample());

            Assert.DoesNotContain("device-abc-123", json, StringComparison.Ordinal);
            Assert.True(Root(Sample()).GetProperty("account").GetProperty("hasDeviceId").GetBoolean());
        }

        [Fact]
        public void Serialising_a_null_snapshot_is_a_programming_error_not_a_silent_empty_file()
        {
            Assert.Throws<ArgumentNullException>(() => SnapshotSerializer.ToJson(null!));
        }
    }
}
