using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>S-API: /v1/state - privacy of the projection, the live part, the refresh policy and the runtime's tick.</summary>
    /// <remarks>In the TraceBus collection: a slow rebuild writes an <c>api/stateOverBudget</c> trace record.</remarks>
    [Collection("TraceBus")]
    public class ApiStateTests
    {
        private const string Marker = "PLAYERTEXT";

        [Fact]
        public void The_state_never_carries_note_diary_todo_habit_or_calendar_text()
        {
            var json = ApiJson.Write(ApiStateProjection.FromSnapshot(PrivateSnapshot()));

            Assert.DoesNotContain(Marker, json);
            Assert.DoesNotContain("PRESETNAME", json);
            Assert.DoesNotContain("EXTRAVALUE", json);
        }

        [Fact]
        public void The_state_carries_ids_counts_and_lengths_instead()
        {
            var state = JsonReader.ParseObject(ApiJson.Write(ApiStateProjection.FromSnapshot(PrivateSnapshot())));

            var todos = (JsonObject)state["todos"]!;
            Assert.Equal(1L, todos["listCount"]);
            Assert.Equal(2L, todos["itemCount"]);
            Assert.Equal(1L, todos["openCount"]);
            var list = (JsonObject)((List<object?>)todos["lists"]!)[0]!;
            Assert.Equal("638000000000000001", list["id"]);
            Assert.Equal((long)(Marker + " list").Length, list["titleLength"]);
            var item = (JsonObject)((List<object?>)list["items"]!)[0]!;
            Assert.Equal((long)(Marker + " todo").Length, item["textLength"]);
            Assert.Equal("open", item["state"]);

            var page = (JsonObject)((List<object?>)((JsonObject)state["notes"]!)["pages"]!)[0]!;
            Assert.Equal((long)(Marker + " note body").Length, page["textLength"]);
            var hidden = (JsonObject)((List<object?>)((JsonObject)state["notes"]!)["pages"]!)[1]!;
            Assert.Null(hidden["textLength"]);

            var habit = (JsonObject)((List<object?>)((JsonObject)state["habits"]!)["items"]!)[0]!;
            Assert.Equal(new object?[] { "monday", "friday" }, ((List<object?>)habit["scheduledDays"]!).ToArray());

            var day = (JsonObject)((List<object?>)((JsonObject)state["calendar"]!)["days"]!)[0]!;
            Assert.Equal("2026-10-01", day["date"]);
            Assert.Equal((long)(Marker + " diary").Length, day["diaryLength"]);

            Assert.Equal(new object?[] { "mod.section" }, ((List<object?>)state["extraSections"]!).ToArray());
            Assert.Equal(1L, ((JsonObject)state["pomodoro"]!)["presetCount"]);
            Assert.Equal("Rain", ((JsonObject)state["music"]!)["nowPlaying"]);
        }

        [Fact]
        public void No_snapshot_means_not_ready()
        {
            var state = ApiStateProjection.FromSnapshot(null);

            Assert.Equal(new[] { "ready" }, state.Keys.ToArray());
            Assert.Equal(false, state["ready"]);
        }

        [Fact]
        public void The_refresh_policy_rebuilds_soon_after_a_change_and_backs_off_when_slow()
        {
            var policy = new ApiRefreshPolicy(minIntervalSeconds: 0.25, maxAgeSeconds: 5, budgetMs: 4, maxIntervalSeconds: 8);

            Assert.True(policy.IsDue(0, dirty: false));
            policy.Record(0, 1);
            Assert.False(policy.IsDue(0.1, dirty: true));
            Assert.True(policy.IsDue(0.3, dirty: true));
            Assert.False(policy.IsDue(4, dirty: false));
            Assert.True(policy.IsDue(5, dirty: false));

            policy.Record(10, 9);
            Assert.Equal(0.5, policy.IntervalSeconds);
            policy.Record(11, 9);
            policy.Record(12, 9);
            policy.Record(13, 9);
            policy.Record(14, 9);
            policy.Record(15, 9);
            Assert.Equal(8, policy.IntervalSeconds);
            Assert.Equal(6, policy.OverBudget);
            Assert.False(policy.IsDue(15 + 5.5, dirty: false));
            Assert.True(policy.IsDue(15 + 8, dirty: false));

            policy.Record(30, 1);
            Assert.Equal(4, policy.IntervalSeconds);
        }

        [Fact]
        public void The_live_part_follows_the_latest_events()
        {
            var live = new ApiLiveState();
            var now = DateTimeOffset.Now;
            live.Seed("Idle", now);
            Assert.True(live.Observe(new SatoneStateChanged("Idle", "Work", 0, 1), now));
            live.Observe(new LineSpoken("Voice_1", "Hi", null, "loc", "en", false, null, null, LineSource.Other, true, 1), now);
            live.Observe(new WindowChanged("Rain", true), now);
            live.Observe(new WindowChanged("Snow", true), now);
            live.Observe(new WindowChanged("Rain", false), now);
            Assert.False(live.Observe(new WindowChanged("Rain", false), now));
            live.Observe(new PomodoroEvent(PomodoroEventKind.Paused, "Pomodoro"), now);
            live.Observe(new PomodoroTick("Pomodoro", 60, 1440), now);
            live.Observe(new MusicChanged("Rain", 2, false), now);
            live.Observe(new RoomReady(), now);

            var json = JsonReader.ParseObject(ApiJson.Write(live.ToJson("th")));
            Assert.Equal(true, json["roomReady"]);
            Assert.Equal("th", json["language"]);
            var satone = (JsonObject)json["satone"]!;
            Assert.Equal("Work", satone["state"]);
            Assert.Equal(true, satone["speaking"]);
            Assert.Equal(new object?[] { "Snow" }, ((List<object?>)json["windows"]!).ToArray());
            var pomodoro = (JsonObject)json["pomodoro"]!;
            Assert.Equal(true, pomodoro["paused"]);
            Assert.Equal(1440L, pomodoro["remainingSec"]);
            Assert.Equal("paused", pomodoro["lastEvent"]);

            live.Observe(new LineEnded("Voice_1", 1), now);
            live.Observe(new PomodoroEvent(PomodoroEventKind.Unpaused, "Pomodoro"), now);
            var after = JsonReader.ParseObject(ApiJson.Write(live.ToJson(null)));
            Assert.Equal(false, ((JsonObject)after["satone"]!)["speaking"]);
            Assert.Equal(false, ((JsonObject)after["pomodoro"]!)["paused"]);
            Assert.Null(after["language"]);
        }

        [Fact]
        public void The_runtime_publishes_events_live_state_and_data_through_its_tick()
        {
            var clock = new ManualApiClock();
            var builds = 0;
            var runtime = new ApiRuntime(ApiToken.Create(), new ApiRuntimeOptions
            {
                Clock = clock,
                Registry = new Core.Actions.ActionRegistry(),
                Services = new Core.Actions.ActionServices(),
                GameData = () =>
                {
                    builds++;
                    return PrivateSnapshot();
                },
                Language = () => "en",
                Refresh = new ApiRefreshPolicy(budgetMs: 1000),
                Diagnostics = _ => { },
                Audit = new ApiAuditLog(clock, () => false, (r, c, q) => { }),
            });

            Assert.True(runtime.Tick(1));
            Assert.Equal(1, builds);
            var first = JsonReader.ParseObject(runtime.State.Render());
            Assert.Equal(true, first["ready"]);
            Assert.Equal(true, first["dataAvailable"]);
            Assert.Equal("en", ((JsonObject)first["live"]!)["language"]);
            Assert.DoesNotContain(Marker, runtime.State.Render());

            Assert.False(runtime.Tick(2));
            runtime.OnEvent("MusicChanged", new MusicChanged("Snow", 1, false));
            Assert.True(runtime.Tick(3));
            Assert.Equal(1, builds);
            Assert.Equal(1, runtime.Events.Latest);

            runtime.OnDataChanged(new GameDataChanged(GameDataSection.Todos, "event:OnAddTodo"));
            Assert.False(runtime.Tick(4));
            Assert.Equal(1, builds);
            clock.Advance(0.3);
            Assert.True(runtime.Tick(5));
            Assert.Equal(2, builds);
            Assert.Equal(ApiEventMapper.DataChanged, runtime.Events.Read(1, 10).Events.Single().Type);

            runtime.OnEvent("PomodoroTick", new PomodoroTick("P", 1, 2));
            Assert.Equal(2, runtime.Events.Latest);
            Assert.True(runtime.Tick(6));
        }

        [Fact]
        public void Without_the_data_plugin_the_state_is_live_only_and_a_failing_snapshot_is_counted()
        {
            var clock = new ManualApiClock();
            var messages = new List<string>();
            var fail = false;
            var runtime = new ApiRuntime(ApiToken.Create(), new ApiRuntimeOptions
            {
                Clock = clock,
                Registry = new Core.Actions.ActionRegistry(),
                Services = new Core.Actions.ActionServices(),
                GameData = () => fail ? throw new InvalidOperationException("boom") : (GameDataSnapshot?)null,
                Diagnostics = messages.Add,
                Audit = new ApiAuditLog(clock, () => false, (r, c, q) => { }),
            });

            runtime.Tick(1);
            var state = JsonReader.ParseObject(runtime.State.Render());
            Assert.Equal(false, state["ready"]);
            Assert.Equal(false, state["dataAvailable"]);
            Assert.True(state.ContainsKey("live"));

            fail = true;
            clock.Advance(10);
            runtime.Tick(2);
            Assert.Equal(1, runtime.SourceFailures);
            Assert.Contains(messages, m => m.Contains("boom"));
        }

        private static GameDataSnapshot PrivateSnapshot() =>
            new GameDataSnapshot
            {
                Time = new DateTimeOffset(2026, 10, 2, 21, 0, 0, TimeSpan.FromHours(7)),
                GameVersion = "1.17.3",
                IsReady = true,
                Player = new PlayerView { Level = new LevelView { CurrentLevel = 12 } },
                Pomodoro = new PomodoroView
                {
                    WorkMinutes = 25,
                    BreakMinutes = 5,
                    Presets = new[] { new PomodoroPresetView { Name = "PRESETNAME" } },
                },
                Todos = new[]
                {
                    new TodoListView
                    {
                        Id = 638000000000000001UL,
                        Title = Marker + " list",
                        Items = new[]
                        {
                            new TodoView { Id = 638000000000000002UL, Text = Marker + " todo", State = TodoViewState.Working },
                            new TodoView { Id = 638000000000000003UL, Text = Marker + " done", State = TodoViewState.Complete, CompletedAt = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Local) },
                        },
                    },
                },
                Notes = new[]
                {
                    new NotePageView { Id = 1UL, Order = 0, Title = Marker + " note", Text = Marker + " note body", TextAvailable = true },
                    new NotePageView { Id = 2UL, Order = 1, Title = Marker + " other", Text = null, TextAvailable = false },
                },
                Habits = new[]
                {
                    new HabitView
                    {
                        Id = "4b0e0f5e-0000-0000-0000-000000000001",
                        Title = Marker + " habit",
                        CreatedDate = new DateTime(2026, 9, 1),
                        ScheduledDays = new[] { DayOfWeek.Monday, DayOfWeek.Friday },
                        RecentDays = new[] { new HabitDayView { Date = new DateTime(2026, 10, 1), Completed = true } },
                    },
                },
                Calendar = new CalendarView
                {
                    RecentDays = new[] { new CalendarDayView { Date = new DateTime(2026, 10, 1), WorkSeconds = 3600, CompletedTodoCount = 2, DiaryText = Marker + " diary" } },
                },
                Music = new MusicView { NowPlayingTitle = "Rain", LocalTracks = new[] { new LocalTrackView { Uuid = "u", FileName = "mine.mp3" } } },
                Extra = new Dictionary<string, object?> { ["mod.section"] = "EXTRAVALUE" },
            };
    }
}
