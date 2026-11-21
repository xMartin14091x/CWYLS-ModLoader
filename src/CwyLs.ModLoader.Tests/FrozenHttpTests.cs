using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests;

[Collection("TraceBus")]
public sealed class FrozenHttpTests
{
    private static readonly (string Method, string Template, string Target)[] Routes = {
        ("GET", "/v1/info", "/v1/info"), ("GET", "/v1/state", "/v1/state"),
        ("GET", "/v1/events", "/v1/events?after=0&wait=0&limit=100"), ("GET", "/v1/actions", "/v1/actions"),
        ("POST", "/v1/actions/{name}", "/v1/actions/freeze.read"),
        ("GET", "/v1/actions/{handle}", "/v1/actions/act-0123456789abcdef01234567?wait=0"),
    };

    [Fact]
    public void Frozen_v1_routes_and_envelopes_match_1_0_without_sockets()
    {
        FrozenSdkTests.EqualLines("six frozen HTTP templates", FrozenSdkTests.Baseline("http-routes-1.0.txt"), Routes.Select(r => r.Method + " " + r.Template));
        // This supplements behavioral requests: a new literal route cannot hide outside the six probes.
        FrozenSdkTests.EqualLines("HTTP route selectors", FrozenSdkTests.Baseline("http-selectors-1.0.txt"),
            FrozenCompiledIl.Strings(typeof(ApiRouter).Assembly.Location, "CwyLs.ModLoader.Core.Api.ApiRouter", "HandleCore")
                .Where(s => s.StartsWith("/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal));
        FrozenSdkTests.EqualLines("HTTP envelope fields", FrozenSdkTests.Baseline("http-fields-1.0.txt"), CaptureFields());
    }

    internal static string[] CaptureFields()
    {
        var rig = new RouterRig();
        var runner = new FakeRunner(hold: true);
        rig.Services.UseRunner(runner);
        rig.Register(new ActionDescriptor("freeze.read", ActionTier.Read, "Contract fixture.", new[] {
            new ActionParameter("mode", ActionValueKind.String, false, "Mode.", new[] { "a", "b" }),
        }));
        var actual = new HashSet<string>(StringComparer.Ordinal);
        // All request work is in-memory through the same router/fakes as ApiRouterTests.
        foreach (var route in Routes)
        {
            if (route.Template.Contains("{", StringComparison.Ordinal)) continue;
            var response = rig.Send(ApiTestKit.Build(route.Method, route.Target, rig.Key, null, ApiTestKit.Port));
            Response(response, 200, route.Method + " " + route.Template + " empty", actual);
            foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE", "CONNECT" })
                Assert.Equal(405, rig.Send(ApiTestKit.Build(method, route.Target.Split('?')[0], rig.Key, null, ApiTestKit.Port)).Status);
        }
        Assert.Equal(404, rig.Send(ApiTestKit.Get("/v1/future-unclassified", rig.Key)).Status);
        Response(rig.Send(ApiTestKit.Get("/v1/future-unclassified", rig.Key)), 404, "error", actual);
        // Fully populated synthetic data closes every nested state shape without reading any save.
        var projection = ApiStateProjection.FromSnapshot(Snapshot());
        projection["dataAvailable"] = true;
        var live = new ApiLiveState();
        live.Observe(new RoomReady(), rig.Clock.Now);
        live.Observe(new SceneChanged("RoomScene", true), rig.Clock.Now);
        live.Observe(new SatoneStateChanged("Idle", "Work", 0, 1), rig.Clock.Now);
        live.Observe(new LineSpoken("voice", "hello", "hello", "id", "en", false, "Main", "g", LineSource.Story, true, 1), rig.Clock.Now);
        live.Observe(new ScenarioStarted("Main", "g", "Auto"), rig.Clock.Now);
        live.Observe(new PomodoroEvent(PomodoroEventKind.WorkStarted, "Pomodoro"), rig.Clock.Now);
        live.Observe(new PomodoroTick("Pomodoro", 1, 2), rig.Clock.Now);
        live.Observe(new MusicChanged("Rain", 0, false), rig.Clock.Now);
        live.Observe(new GameClockTick(new DateTime(2026, 1, 2)), rig.Clock.Now);
        live.Observe(new WindowChanged("Rain", true), rig.Clock.Now);
        projection["live"] = live.ToJson("en");
        rig.Router.State.Publish(projection, 42);
        Response(rig.Send(ApiTestKit.Get("/v1/state", rig.Key)), 200, "GET /v1/state populated", actual);
        var events = new ICwyEvent[] {
            new SatoneStateChanged("Idle", "Work", 0, 1),
            new LineSpoken("voice", "hello", "hello", "id", "en", false, "Main", "g", LineSource.Story, true, 1),
            new LineEnded("voice", 1), new ScenarioStarted("Main", "g", "Auto"), new ScenarioEnded("g"),
            new PomodoroEvent(PomodoroEventKind.WorkStarted, "Pomodoro"), new WindowChanged("Rain", true),
            new MusicChanged("Rain", 1, true), new SaveSection("Todo"), new SceneChanged("RoomScene", true), new RoomReady(),
        };
        foreach (var e in events)
        {
            Assert.True(ApiEventMapper.TryMap(e.GetType().Name, e, out var type, out var data));
            rig.Router.Events.Append(type, data);
            Shape("event " + type, data, actual);
        }
        var dataChanged = ApiEventMapper.FromDataChanged(new GameDataChanged(GameDataSection.Notes, "changed"));
        var completed = ApiEventMapper.FromActionCompleted("act-0123456789abcdef01234567", "freeze.read", ActionTier.Read, ActionStatus.Ok);
        Shape("event data.changed", dataChanged, actual);
        Shape("event action.completed", completed, actual);
        // Unknown event payload is intentionally just its channel name.
        Assert.True(ApiEventMapper.TryMap("Future", new FutureEvent(), out var unknownType, out var unknownData));
        Shape("event " + unknownType, unknownData, actual);
        rig.Router.Events.Append(ApiEventMapper.DataChanged, dataChanged);
        rig.Router.Events.Append(ApiEventMapper.ActionCompleted, completed);
        rig.Router.Events.Append(unknownType, unknownData);
        Response(rig.Send(ApiTestKit.Get("/v1/events?after=0&wait=0&limit=100", rig.Key)), 200, "GET /v1/events populated", actual);
        ApiResponse started;
        try
        {
            started = rig.Send(ApiTestKit.Post("/v1/actions/freeze.read", rig.Key, "{\"args\":{\"mode\":\"a\"}}", ApiTestKit.Port, ("Idempotency-Key", "freeze-fixture")));
            Response(started, 202, "POST /v1/actions/{name} pending", actual);
            var handle = (string)ApiTestKit.Json(started)["handle"]!;
            Assert.Equal("/v1/actions/" + handle, ApiTestKit.Header(started, "Location"));
            Response(rig.Send(ApiTestKit.Get("/v1/actions/" + handle + "?wait=0", rig.Key)), 200, "GET /v1/actions/{handle} pending", actual);
            Assert.Equal(405, rig.Send(ApiTestKit.Get("/v1/actions/freeze.read", rig.Key)).Status);
            Assert.Equal(405, rig.Send(ApiTestKit.Post("/v1/actions/" + handle, rig.Key, null)).Status);
            runner.Release();
            var done = rig.Send(ApiTestKit.Get("/v1/actions/" + handle + "?wait=1", rig.Key));
            Assert.Equal("done", ApiTestKit.Json(done)["state"]);
            Response(done, 200, "GET /v1/actions/{handle} done", actual);
            var replay = rig.Send(ApiTestKit.Post("/v1/actions/freeze.read", rig.Key, "{\"args\":{\"mode\":\"a\"}}", ApiTestKit.Port, ("Idempotency-Key", "freeze-fixture")));
            Response(replay, 200, "POST /v1/actions/{name} replay", actual);
            Assert.Equal(true, ApiTestKit.Json(replay)["replayed"]);
        }
        finally { runner.Release(); }
        return actual.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static void Response(ApiResponse response, int status, string label, HashSet<string> fields)
    {
        Assert.True(response.Status == status, label + " expected " + status + ", got " + response.Status + ": " + response.Body);
        Assert.StartsWith("application/json", response.MediaType);
        Assert.Equal("no-store", ApiResponse.CommonHeaders.Single(h => h.Key == "Cache-Control").Value);
        Assert.Null(ApiTestKit.Header(response, "Access-Control-Allow-Origin"));
        Shape(label, ApiTestKit.Json(response), fields);
    }

    private static void Shape(string path, object? value, HashSet<string> fields)
    {
        // Extension action schemas and result data have their own contracts, not this frozen envelope.
        if (path.EndsWith(".result.data", StringComparison.Ordinal) || path.EndsWith(".inputSchema.properties", StringComparison.Ordinal))
        { fields.Add(path + " open"); return; }
        var kind = value switch { JsonObject => "object", List<object?> => "array", null => "null", string => "string", bool => "boolean", _ => "number" };
        fields.Add(path + " " + kind);
        if (value is JsonObject obj)
            foreach (var item in obj) Shape(path + "." + item.Key, item.Value, fields);
        if (value is List<object?> list)
            foreach (var item in list) Shape(path + "[]", item, fields);
    }

    private static GameDataSnapshot Snapshot() => new GameDataSnapshot {
        Time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), IsReady = true, GameVersion = "test",
        Player = new PlayerView { Level = new LevelView(), LastLoginAt = new DateTime(2026, 1, 2) },
        Pomodoro = new PomodoroView(),
        Todos = new[] { new TodoListView { Id = 1, Title = "private", Items = new[] { new TodoView { Id = 2, Text = "private", CompletedAt = new DateTime(2026, 1, 2), ExpiresAt = new DateTime(2026, 1, 3) } } } },
        Notes = new[] { new NotePageView { Id = 1, Title = "private", Text = "private", TextAvailable = true } },
        Habits = new[] { new HabitView { Id = "id", Title = "private", CreatedDate = new DateTime(2026, 1, 2), ScheduledDays = new[] { DayOfWeek.Monday }, RecentDays = new[] { new HabitDayView { Date = new DateTime(2026, 1, 2) } } } },
        Calendar = new CalendarView { RecentDays = new[] { new CalendarDayView { Date = new DateTime(2026, 1, 2), DiaryText = "private" } } },
        Settings = new SettingsView { Volumes = new[] { new VolumeView { Name = "Music", RawValue = 0.5f } } },
        Music = new MusicView { NowPlayingTitle = "Rain" },
        Environment = new EnvironmentView { ActiveWindowViews = new[] { "Rain" }, ActiveAmbientSounds = new[] { new AmbientSoundView { Type = "Rain", Volume = 0.5f } } },
        Story = new StoryView(), Achievements = new AchievementsView(),
        Extra = new Dictionary<string, object?> { ["mod.extra"] = "private" },
    };

    private sealed record FutureEvent : ICwyEvent
    {
        public string Describe() => "future";
        public IReadOnlyDictionary<string, object?> Fields() => new Dictionary<string, object?>();
    }
}
