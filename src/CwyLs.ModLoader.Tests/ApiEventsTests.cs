using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>S-API: the event ring (sequence, dropped, reset, long-poll) and the event type mapping.</summary>
    public class ApiEventsTests
    {
        [Fact]
        public void Events_get_consecutive_sequence_numbers_and_read_after_a_cursor()
        {
            var ring = new ApiEventRing(8, epoch: "e1");
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(i + 1, ring.Append("t", new JsonObject { ["i"] = i }));
            }

            var page = ring.Read(2, 2);
            Assert.Equal(new long[] { 3, 4 }, page.Events.Select(e => e.Seq).ToArray());
            Assert.Equal(4, page.Next);
            Assert.Equal(5, page.Latest);
            Assert.Equal(1, page.Oldest);
            Assert.Equal(0, page.Dropped);
            Assert.False(page.Reset);

            var json = JsonReader.ParseObject(page.ToJson());
            Assert.Equal("e1", json["epoch"]);
            var first = (JsonObject)((List<object?>)json["events"]!)[0]!;
            Assert.Equal(new[] { "seq", "time", "type", "data" }, first.Keys.ToArray());
        }

        [Fact]
        public void A_reader_that_fell_behind_is_told_how_many_it_lost()
        {
            var ring = new ApiEventRing(4);
            for (var i = 0; i < 10; i++)
            {
                ring.Append("t", null);
            }

            var page = ring.Read(2, 100);
            Assert.Equal(7, page.Oldest);
            Assert.Equal(4, page.Dropped);
            Assert.Equal(new long[] { 7, 8, 9, 10 }, page.Events.Select(e => e.Seq).ToArray());
            Assert.Equal(6, ring.DroppedTotal);
        }

        [Fact]
        public void A_cursor_from_an_earlier_start_resets_to_the_oldest_event()
        {
            var ring = new ApiEventRing(4);
            ring.Append("t", null);
            ring.Append("t", null);

            var page = ring.Read(500, 10);
            Assert.True(page.Reset);
            Assert.Equal(new long[] { 1, 2 }, page.Events.Select(e => e.Seq).ToArray());
            Assert.Equal(0, page.Dropped);

            var empty = new ApiEventRing(4).Read(9, 10);
            Assert.True(empty.Reset);
            Assert.Empty(empty.Events);
            Assert.Equal(0, empty.Next);
        }

        [Fact]
        public async Task A_long_poll_returns_as_soon_as_an_event_arrives()
        {
            var ring = new ApiEventRing(16);
            var watch = Stopwatch.StartNew();
            var waiter = Task.Run(() => ring.Wait(0, 10, TimeSpan.FromSeconds(10)));
            await Task.Delay(100);
            ring.Append("music.changed", new JsonObject { ["title"] = "Rain" });

            var page = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(page.Events);
            Assert.False(page.TimedOut);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task A_long_poll_with_nothing_new_times_out_and_close_wakes_it()
        {
            var ring = new ApiEventRing(16);
            var timedOut = ring.Wait(0, 10, TimeSpan.FromMilliseconds(150));
            Assert.True(timedOut.TimedOut);
            Assert.Empty(timedOut.Events);

            var waiter = Task.Run(() => ring.Wait(0, 10, TimeSpan.FromSeconds(20)));
            await Task.Delay(100);
            ring.Close();
            await waiter.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, ring.Append("t", null));
        }

        [Fact]
        public void The_events_route_long_polls_and_caps_concurrent_waits()
        {
            var rig = new RouterRig();
            var results = new ApiResponse?[ApiDefaults.MaxConcurrentWaits];
            var threads = Enumerable.Range(0, ApiDefaults.MaxConcurrentWaits)
                .Select(i => new Thread(() => results[i] = rig.Send(ApiTestKit.Get("/v1/events?after=0&wait=10", rig.Key))) { IsBackground = true })
                .ToArray();
            foreach (var t in threads)
            {
                t.Start();
            }

            Assert.True(SpinWait.SpinUntil(() => rig.Router.Waiting == ApiDefaults.MaxConcurrentWaits, 5000));
            var refused = rig.Send(ApiTestKit.Get("/v1/events?after=0&wait=10", rig.Key));
            Assert.Equal(429, refused.Status);

            rig.Router.Events.Append(ApiEventMapper.RoomReady, null);
            foreach (var t in threads)
            {
                Assert.True(t.Join(TimeSpan.FromSeconds(5)));
            }

            foreach (var r in results)
            {
                Assert.Equal(200, r!.Status);
                Assert.Single((List<object?>)ApiTestKit.Json(r)["events"]!);
            }

            var immediate = ApiTestKit.Json(rig.Send(ApiTestKit.Get("/v1/events?after=1", rig.Key)));
            Assert.Equal(1L, immediate["next"]);
            Assert.Empty((List<object?>)immediate["events"]!);
        }

        [Fact]
        public void Game_events_map_to_the_documented_types()
        {
            AssertMapped(new SatoneStateChanged("Idle", "Work", 0, 1), ApiEventMapper.SatoneState, "prev", "next");
            AssertMapped(
                new LineSpoken("Voice_1", "Hello", "こんにちは", "loc_1", "en", false, null, null, LineSource.SelfTalk, true, 2.5),
                ApiEventMapper.LineSpoken,
                "voice", "text", "textJa", "language", "localizationId", "source", "scenarioType", "scenarioGroupId", "isStory", "durationSec");
            AssertMapped(new LineEnded("Voice_1", 2.5), ApiEventMapper.LineEnded, "voice", "durationSec");
            AssertMapped(new ScenarioStarted("Main", "g1", "Auto"), ApiEventMapper.ScenarioStarted, "scenarioType", "groupId", "readMode");
            AssertMapped(new ScenarioEnded("g1"), ApiEventMapper.ScenarioEnded, "groupId");
            AssertMapped(new PomodoroEvent(PomodoroEventKind.WorkStarted, "Pomodoro"), ApiEventMapper.PomodoroChanged, "kind", "pomodoroType");
            AssertMapped(new WindowChanged("Rain", true), ApiEventMapper.WindowChanged, "window", "active");
            AssertMapped(new MusicChanged("Rain", 3, true), ApiEventMapper.MusicChanged, "title", "index", "shuffle");
            AssertMapped(new SaveSection("Todo"), ApiEventMapper.Saved, "section");
            AssertMapped(new SceneChanged("RoomScene", true), ApiEventMapper.SceneChanged, "scene", "loaded");
            AssertMapped(new RoomReady(), ApiEventMapper.RoomReady);

            Assert.True(ApiEventMapper.TryMap("PomodoroEvent", new PomodoroEvent(PomodoroEventKind.WorkStarted, "P"), out _, out var data));
            Assert.Equal("workStarted", data["kind"]);
        }

        [Fact]
        public void Ticks_are_not_events_and_unknown_events_carry_only_their_name()
        {
            Assert.False(ApiEventMapper.TryMap("PomodoroTick", new PomodoroTick("P", 1, 2), out _, out _));
            Assert.False(ApiEventMapper.TryMap("GameClockTick", new GameClockTick(DateTime.Now), out _, out _));
            Assert.False(ApiEventMapper.TryMap(null, null, out _, out _));

            Assert.True(ApiEventMapper.TryMap("FutureThing", new FutureEvent("private text"), out var type, out var data));
            Assert.Equal(ApiEventMapper.Other, type);
            Assert.Equal(new[] { "name" }, data.Keys.ToArray());
            Assert.Equal("FutureThing", data["name"]);
        }

        [Fact]
        public void Data_changes_name_the_section_and_reason_only()
        {
            var data = ApiEventMapper.FromDataChanged(new GameDataChanged(GameDataSection.Notes, "event:OnEndEditNote"));

            Assert.Equal(new[] { "section", "reason" }, data.Keys.ToArray());
            Assert.Equal("notes", data["section"]);
        }

        [Fact]
        public void Action_completion_events_hold_no_arguments_or_message()
        {
            var data = ApiEventMapper.FromActionCompleted("act-0123456789abcdef01234567", "todo.add", ActionTier.Write, ActionStatus.Declined);

            Assert.Equal(new[] { "handle", "action", "tier", "status" }, data.Keys.ToArray());
            Assert.Equal("write", data["tier"]);
            Assert.Equal("declined", data["status"]);
        }

        [Fact]
        public void Every_documented_type_is_listed_once()
        {
            Assert.Equal(ApiEventMapper.Types.Count, ApiEventMapper.Types.Distinct().Count());
            Assert.Contains(ApiEventMapper.ActionCompleted, ApiEventMapper.Types);
        }

        private static void AssertMapped(ICwyEvent e, string expectedType, params string[] keys)
        {
            Assert.True(ApiEventMapper.TryMap(e.GetType().Name, e, out var type, out var data));
            Assert.Equal(expectedType, type);
            Assert.Equal(keys, data.Keys.ToArray());
        }

        private sealed record FutureEvent(string Secretish) : ICwyEvent
        {
            public string Describe() => Secretish;

            public IReadOnlyDictionary<string, object?> Fields() => new Dictionary<string, object?> { ["text"] = Secretish };
        }
    }
}
