using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>S-API: the action catalog, the API-side pre-gate (writes refused without the in-game dialog), argument binding, handles and idempotency.</summary>
    /// <remarks>In the TraceBus collection: finished actions write an <c>api/actionDone</c> trace record.</remarks>
    [Collection("TraceBus")]
    public class ApiActionsTests
    {
        private static readonly ActionDescriptor ReadAction = new ActionDescriptor("music.current", ActionTier.Read, "The current track.");

        private static readonly ActionDescriptor PlayAction = new ActionDescriptor(
            "music.play",
            ActionTier.Act,
            "Plays a track by title.",
            new[]
            {
                new ActionParameter("title", ActionValueKind.String, true, "The track title."),
                new ActionParameter("volume", ActionValueKind.Number, false, "0 to 1."),
                new ActionParameter("repeat", ActionValueKind.Integer, false, "How many times."),
                new ActionParameter("shuffle", ActionValueKind.Boolean, false, "Shuffle after."),
                new ActionParameter("mode", ActionValueKind.String, false, "Where.", new[] { "room", "desk" }),
            });

        private static readonly ActionDescriptor WriteAction = new ActionDescriptor(
            "todo.add", ActionTier.Write, "Adds a todo.", new[] { new ActionParameter("text", ActionValueKind.String, true, "The todo text.") });

        private static readonly ActionDescriptor DestructiveAct = new ActionDescriptor("music.forget", ActionTier.Act, "Removes an imported track.", isDestructive: true);

        [Fact]
        public void The_catalog_describes_every_action_with_a_json_schema()
        {
            var rig = new RouterRig();
            rig.Register(PlayAction);
            rig.Register(ReadAction);

            var catalog = ApiTestKit.Json(rig.Send(ApiTestKit.Get("/v1/actions", rig.Key)));
            var actions = ((List<object?>)catalog["actions"]!).Cast<JsonObject>().ToList();

            Assert.Equal(2L, catalog["count"]);
            Assert.Equal(new[] { "music.current", "music.play" }, actions.Select(a => (string)a["name"]!).ToArray());
            var play = actions[1];
            Assert.Equal("act", play["tier"]);
            Assert.Equal("music", play["family"]);
            Assert.Equal("cwy", play["owner"]);
            var schema = (JsonObject)play["inputSchema"]!;
            Assert.Equal("object", schema["type"]);
            Assert.Equal(false, schema["additionalProperties"]);
            Assert.Equal(new object?[] { "title" }, ((List<object?>)schema["required"]!).ToArray());
            var properties = (JsonObject)schema["properties"]!;
            Assert.Equal("string", ((JsonObject)properties["title"]!)["type"]);
            Assert.Equal("number", ((JsonObject)properties["volume"]!)["type"]);
            Assert.Equal("integer", ((JsonObject)properties["repeat"]!)["type"]);
            Assert.Equal("boolean", ((JsonObject)properties["shuffle"]!)["type"]);
            Assert.Equal(new object?[] { "room", "desk" }, ((List<object?>)((JsonObject)properties["mode"]!)["enum"]!).ToArray());
            Assert.Equal(false, ((JsonObject)play["api"]!)["allowed"]);
            Assert.Equal(true, ((JsonObject)actions[0]["api"]!)["allowed"]);
        }

        [Fact]
        public void Act_tier_is_denied_while_AllowActions_is_off_and_the_runner_never_sees_it()
        {
            var rig = new RouterRig();
            var runner = new FakeRunner();
            rig.Services.UseRunner(runner);
            rig.Register(PlayAction);

            var response = rig.Send(ApiTestKit.Post("/v1/actions/music.play", rig.Key, "{\"args\":{\"title\":\"Rain\"}}"));

            Assert.Equal(403, response.Status);
            Assert.Equal(ApiErrorCodes.Denied, ApiTestKit.ErrorCode(response));
            Assert.Contains(ActionGateSettings.AllowApiActionsKey, response.Body);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public void Writes_are_refused_without_the_in_game_confirmation_never_auto_approved()
        {
            var rig = new RouterRig(allowActions: true);
            var runner = new FakeRunner();
            rig.Services.UseRunner(runner);
            rig.Register(WriteAction);
            rig.Register(DestructiveAct);

            var write = rig.Send(ApiTestKit.Post("/v1/actions/todo.add", rig.Key, "{\"args\":{\"text\":\"x\"}}"));
            var delete = rig.Send(ApiTestKit.Post("/v1/actions/music.forget", rig.Key, null));

            Assert.Equal(403, write.Status);
            Assert.Equal(ApiErrorCodes.ConfirmationUnavailable, ApiTestKit.ErrorCode(write));
            Assert.Equal(403, delete.Status);
            Assert.Equal(ApiErrorCodes.ConfirmationUnavailable, ApiTestKit.ErrorCode(delete));
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public void With_the_in_game_confirmation_installed_a_write_reaches_the_runner()
        {
            var rig = new RouterRig(allowActions: true);
            var runner = new FakeRunner();
            rig.Services.UseRunner(runner);
            rig.Services.UseConfirmationGate(new ApprovingGate());
            rig.Register(WriteAction);

            var response = rig.Send(ApiTestKit.Post("/v1/actions/todo.add", rig.Key, "{\"args\":{\"text\":\"buy tea\"}}"));

            Assert.Equal(202, response.Status);
            Assert.Single(runner.Requests);
        }

        [Fact]
        public void Without_a_runner_actions_are_503()
        {
            var rig = new RouterRig();
            rig.Register(ReadAction);

            var response = rig.Send(ApiTestKit.Post("/v1/actions/music.current", rig.Key, null));

            Assert.Equal(503, response.Status);
            Assert.Contains(UnavailableActionRunner.Reason, response.Body);
        }

        [Fact]
        public void An_unknown_action_is_404()
        {
            var rig = new RouterRig();
            rig.Services.UseRunner(new FakeRunner());

            Assert.Equal(404, rig.Send(ApiTestKit.Post("/v1/actions/music.nothing", rig.Key, null)).Status);
        }

        [Fact]
        public void A_started_action_returns_a_handle_at_once_and_the_result_by_handle()
        {
            var rig = new RouterRig(allowActions: true);
            var runner = new FakeRunner(hold: true);
            rig.Services.UseRunner(runner);
            rig.Register(PlayAction);

            var started = rig.Send(ApiTestKit.Post(
                "/v1/actions/music.play", rig.Key, "{\"args\":{\"title\":\"Rain\",\"volume\":1,\"repeat\":2.0}}", ApiTestKit.Port, ("X-CWY-Client", "satone")));
            Assert.Equal(202, started.Status);
            var body = ApiTestKit.Json(started);
            var handle = (string)body["handle"]!;
            Assert.True(ApiActionHandles.IsHandleId(handle));
            Assert.Equal("pending", body["state"]);
            Assert.Equal("/v1/actions/" + handle, ApiTestKit.Header(started, "Location"));

            var pending = ApiTestKit.Json(rig.Send(ApiTestKit.Get("/v1/actions/" + handle, rig.Key)));
            Assert.Equal("pending", pending["state"]);

            runner.Release();
            var done = ApiTestKit.Json(rig.Send(ApiTestKit.Get("/v1/actions/" + handle + "?wait=5", rig.Key)));
            Assert.Equal("done", done["state"]);
            var result = (JsonObject)done["result"]!;
            Assert.Equal("ok", result["status"]);
            Assert.Equal("semantic", result["path"]);
            Assert.Equal(1L, ((JsonObject)result["data"]!)["n"]);

            var request = runner.Requests.Single();
            Assert.Equal(ActionCaller.Api, request.Caller);
            Assert.Equal("api:satone", request.CallerId);
            Assert.Equal(TimeSpan.FromSeconds(2), request.Timeout);
            Assert.Equal("Rain", request.Args.GetString("title"));
            Assert.Equal(1d, request.Args["volume"]);
            Assert.Equal(2L, request.Args["repeat"]);
        }

        [Fact]
        public void The_completion_event_carries_no_arguments_and_no_message()
        {
            var rig = new RouterRig(allowActions: true);
            rig.Services.UseRunner(new FakeRunner(ActionResult.Ok("added the todo private words")));
            rig.Services.UseConfirmationGate(new ApprovingGate());
            rig.Register(WriteAction);

            var started = ApiTestKit.Json(rig.Send(ApiTestKit.Post("/v1/actions/todo.add", rig.Key, "{\"args\":{\"text\":\"private words\"}}")));
            var handle = (string)started["handle"]!;
            rig.Send(ApiTestKit.Get("/v1/actions/" + handle + "?wait=5", rig.Key));
            var page = rig.Router.Events.Read(0, 100);

            var completed = page.Events.Single(e => e.Type == ApiEventMapper.ActionCompleted);
            Assert.Contains(handle, completed.Json);
            Assert.Contains("\"status\":\"ok\"", completed.Json);
            Assert.DoesNotContain("private", completed.Json);
        }

        [Theory]
        [InlineData("{\"args\":{}}", "required")]
        [InlineData("{\"args\":{\"title\":null}}", "required")]
        [InlineData("{\"args\":{\"title\":5}}", "string")]
        [InlineData("{\"args\":{\"title\":\"a\",\"volume\":\"loud\"}}", "number")]
        [InlineData("{\"args\":{\"title\":\"a\",\"repeat\":2.5}}", "whole")]
        [InlineData("{\"args\":{\"title\":\"a\",\"shuffle\":\"yes\"}}", "true or false")]
        [InlineData("{\"args\":{\"title\":\"a\",\"mode\":\"garden\"}}", "one of")]
        [InlineData("{\"args\":{\"title\":\"a\",\"colour\":\"red\"}}", "not a parameter")]
        [InlineData("{\"args\":{\"title\":\"a\",\"Title\":\"b\"}}", "twice")]
        public void Arguments_are_bound_strictly(string body, string expected)
        {
            var rig = new RouterRig(allowActions: true);
            rig.Services.UseRunner(new FakeRunner());
            rig.Register(PlayAction);

            var response = rig.Send(ApiTestKit.Post("/v1/actions/music.play", rig.Key, body));

            Assert.Equal(400, response.Status);
            Assert.Equal(ApiErrorCodes.InvalidArgs, ApiTestKit.ErrorCode(response));
            Assert.Contains(expected, response.Body);
        }

        [Fact]
        public void Allowed_values_match_case_insensitively_and_bind_their_canonical_spelling()
        {
            Assert.True(ApiActionCatalog.TryBind(PlayAction, new JsonObject { ["title"] = "a", ["mode"] = "DESK" }, out var args, out _));
            Assert.Equal("desk", args!.GetString("mode"));
        }

        [Theory]
        [InlineData("{\"args\":{\"title\":\"a\"},\"extra\":1}", 400)]
        [InlineData("[1,2]", 400)]
        [InlineData("{\"args\":[1]}", 400)]
        [InlineData("{'args':{}}", 400)]
        [InlineData("{\"args\":{\"title\":\"a\",}}", 400)]
        public void The_body_must_be_a_strict_json_envelope(string body, int status)
        {
            var rig = new RouterRig(allowActions: true);
            rig.Services.UseRunner(new FakeRunner());
            rig.Register(PlayAction);

            Assert.Equal(status, rig.Send(ApiTestKit.Post("/v1/actions/music.play", rig.Key, body)).Status);
        }

        [Fact]
        public void Media_type_size_and_chunking_are_checked()
        {
            var rig = new RouterRig(allowActions: true);
            rig.Services.UseRunner(new FakeRunner());
            rig.Register(PlayAction);

            var json = ApiTestKit.Build("POST", "/v1/actions/music.play", rig.Key, "{\"args\":{\"title\":\"a\"}}", ApiTestKit.Port);
            var headers = new ApiHeaders();
            headers.Add("Host", ApiTestKit.Authority());
            headers.Add("Authorization", ApiTestKit.AuthValue(rig.Key));
            headers.Add("Content-Type", "text/plain");
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"args\":{\"title\":\"a\"}}");
            var textPlain = new ApiRequest("test", "POST", "/v1/actions/music.play", headers, bytes.Length, false, _ => ApiBody.Of(bytes));
            Assert.Equal(415, rig.Send(textPlain).Status);
            Assert.Equal(202, rig.Send(json).Status);

            var big = new string('x', ApiDefaults.MaxBodyBytes + 1);
            Assert.Equal(413, rig.Send(ApiTestKit.Post("/v1/actions/music.play", rig.Key, "{\"args\":{\"title\":\"" + big + "\"}}")).Status);

            var chunked = ApiTestKit.Build("POST", "/v1/actions/music.play", rig.Key, null, ApiTestKit.Port, ("Transfer-Encoding", "chunked"));
            Assert.Equal(411, rig.Send(chunked).Status);

            var badUtf8 = new ApiRequest("test", "POST", "/v1/actions/music.play", JsonHeaders(rig), 2, false, _ => ApiBody.Of(new byte[] { 0xC3, 0x28 }));
            Assert.Equal(400, rig.Send(badUtf8).Status);
        }

        [Fact]
        public void An_idempotency_key_replays_the_first_handle_and_refuses_a_different_request()
        {
            var rig = new RouterRig(allowActions: true);
            var runner = new FakeRunner();
            rig.Services.UseRunner(runner);
            rig.Register(PlayAction);

            var first = rig.Send(ApiTestKit.Post("/v1/actions/music.play", rig.Key, "{\"args\":{\"title\":\"Rain\",\"volume\":0.5}}", ApiTestKit.Port, ("Idempotency-Key", "k-1")));
            var again = rig.Send(ApiTestKit.Post("/v1/actions/music.play", rig.Key, "{\"args\":{\"volume\":0.5,\"title\":\"Rain\"}}", ApiTestKit.Port, ("Idempotency-Key", "k-1")));
            var other = rig.Send(ApiTestKit.Post("/v1/actions/music.play", rig.Key, "{\"args\":{\"title\":\"Snow\"}}", ApiTestKit.Port, ("Idempotency-Key", "k-1")));
            var bad = rig.Send(ApiTestKit.Post("/v1/actions/music.play", rig.Key, "{\"args\":{\"title\":\"Snow\"}}", ApiTestKit.Port, ("Idempotency-Key", "has space")));

            Assert.Equal(202, first.Status);
            Assert.Equal(200, again.Status);
            Assert.Equal(ApiTestKit.Json(first)["handle"], ApiTestKit.Json(again)["handle"]);
            Assert.Equal(true, ApiTestKit.Json(again)["replayed"]);
            Assert.Equal(409, other.Status);
            Assert.Equal(400, bad.Status);
            Assert.Single(runner.Requests);
            Assert.Equal("k-1", runner.Requests[0].RequestId);
        }

        [Fact]
        public void At_most_eight_actions_run_at_once()
        {
            var rig = new RouterRig(allowActions: true);
            var runner = new FakeRunner(hold: true);
            rig.Services.UseRunner(runner);
            rig.Register(ReadAction);

            var handles = new List<string>();
            for (var i = 0; i < ApiDefaults.MaxActionsInFlight; i++)
            {
                var started = rig.Send(ApiTestKit.Post("/v1/actions/music.current", rig.Key, null));
                Assert.Equal(202, started.Status);
                handles.Add((string)ApiTestKit.Json(started)["handle"]!);
            }

            var refused = rig.Send(ApiTestKit.Post("/v1/actions/music.current", rig.Key, null));
            Assert.Equal(429, refused.Status);
            Assert.NotNull(ApiTestKit.Header(refused, "Retry-After"));

            runner.Release();
            foreach (var id in handles)
            {
                Assert.True(rig.Router.Handles.TryGet(id, out var handle));
                Assert.True(handle!.Wait(TimeSpan.FromSeconds(5)));
            }

            Assert.Equal(202, rig.Send(ApiTestKit.Post("/v1/actions/music.current", rig.Key, null)).Status);
        }

        [Fact]
        public void Stopping_the_api_ends_pending_handles_as_timed_out()
        {
            var rig = new RouterRig(allowActions: true);
            var runner = new CancellableRunner();
            rig.Services.UseRunner(runner);
            rig.Register(ReadAction);
            var handle = (string)ApiTestKit.Json(rig.Send(ApiTestKit.Post("/v1/actions/music.current", rig.Key, null)))["handle"]!;

            rig.Router.BeginStop();
            Assert.True(rig.Router.Handles.TryGet(handle, out var h));
            Assert.True(h!.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(ActionStatus.TimedOut, h.Result!.Status);
        }

        [Fact]
        public void Unknown_or_malformed_handles_are_404()
        {
            var rig = new RouterRig();

            Assert.Equal(404, rig.Send(ApiTestKit.Get("/v1/actions/act-0123456789abcdef01234567", rig.Key)).Status);
            Assert.False(ApiActionHandles.IsHandleId("act-0123"));
            Assert.False(ApiActionHandles.IsHandleId("act-0123456789ABCDEF01234567"));
            Assert.False(ApiActionHandles.IsHandleId("music.play"));
        }

        [Fact]
        public void The_pre_gate_follows_the_action_gate_table()
        {
            Assert.Equal(ApiActionVerdict.Allowed, ApiActionCatalog.Decide(ReadAction, false, false, out _));
            Assert.Equal(ApiActionVerdict.Denied, ApiActionCatalog.Decide(PlayAction, false, true, out _));
            Assert.Equal(ApiActionVerdict.Allowed, ApiActionCatalog.Decide(PlayAction, true, false, out _));
            Assert.Equal(ApiActionVerdict.Denied, ApiActionCatalog.Decide(WriteAction, false, true, out _));
            Assert.Equal(ApiActionVerdict.NeedsConfirmation, ApiActionCatalog.Decide(WriteAction, true, false, out var reason));
            Assert.Contains("not installed", reason);
            Assert.Equal(ApiActionVerdict.Allowed, ApiActionCatalog.Decide(WriteAction, true, true, out _));
            Assert.Equal(ApiActionVerdict.NeedsConfirmation, ApiActionCatalog.Decide(DestructiveAct, true, false, out _));
        }

        [Fact]
        public void Fingerprints_ignore_argument_order_but_not_values_or_types()
        {
            var a = ActionArgs.Of(("title", "Rain"), ("volume", 0.5));
            var b = ActionArgs.Of(("volume", 0.5), ("title", "Rain"));
            var c = ActionArgs.Of(("title", "Rain"), ("volume", "0.5"));

            Assert.Equal(ApiActionCatalog.Fingerprint("music.play", a), ApiActionCatalog.Fingerprint("music.play", b));
            Assert.NotEqual(ApiActionCatalog.Fingerprint("music.play", a), ApiActionCatalog.Fingerprint("music.play", c));
            Assert.NotEqual(ApiActionCatalog.Fingerprint("music.play", a), ApiActionCatalog.Fingerprint("music.stop", a));
        }

        private static ApiHeaders JsonHeaders(RouterRig rig)
        {
            var headers = new ApiHeaders();
            headers.Add("Host", ApiTestKit.Authority());
            headers.Add("Authorization", ApiTestKit.AuthValue(rig.Key));
            headers.Add("Content-Type", "application/json; charset=utf-8");
            return headers;
        }

        /// <summary>A runner that only ends when its token is cancelled.</summary>
        private sealed class CancellableRunner : IActionRunner
        {
            public async System.Threading.Tasks.Task<ActionResult> RunAsync(ActionRequest request, System.Threading.CancellationToken cancellationToken)
            {
                await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return ActionResult.Ok("never");
            }
        }
    }
}
