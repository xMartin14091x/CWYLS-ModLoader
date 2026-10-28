using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Threading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// E-ACT-CORE: the runner end to end over a real <see cref="MainThreadQueue"/> - schema, tier gate,
    /// confirmation, the main-thread hop with its timeout, late handlers, owners and the audit record.
    /// </summary>
    public class ActionsRunnerTests
    {
        private readonly ActionRegistry _registry = new ActionRegistry();
        private readonly ActionServices _services = new ActionServices();
        private readonly List<ActionAuditRecord> _audit = new List<ActionAuditRecord>();
        private readonly List<string> _errors = new List<string>();
        private readonly QueueDispatcher _dispatcher = new QueueDispatcher();

        public ActionsRunnerTests()
        {
            _services.ErrorSink = message => { lock (_errors) { _errors.Add(message); } };
            _dispatcher.Queue.CaptureMainThread();
        }

        private ActionRunner NewRunner() =>
            new ActionRunner(_registry, _dispatcher, _services, record => { lock (_audit) { _audit.Add(record); } });

        private static ActionDescriptor Play() => new ActionDescriptor(
            "music.play", ActionTier.Act, "Plays a track by its title.",
            new[]
            {
                new ActionParameter("title", ActionValueKind.String, true, "The track title."),
                new ActionParameter("volume", ActionValueKind.Number, false, "Volume from 0 to 1.", minimum: 0, maximum: 1),
            });

        private static ActionRequest Request(string name, ActionArgs? args = null, ActionCaller caller = ActionCaller.Console, string id = "console", TimeSpan? timeout = null) =>
            new ActionRequest(name, args, caller, id, timeout: timeout);

        private ActionResult Drive(Task<ActionResult> task)
        {
            // Whoever pumps is the main thread (an async test may resume on another thread).
            _dispatcher.Queue.CaptureMainThread();
            var watch = Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                _dispatcher.Queue.Pump(1);
                Thread.Sleep(1);
            }

            Assert.True(task.IsCompleted, "the run did not finish");
            return task.Result;
        }

        private ActionAuditRecord LastAudit()
        {
            lock (_audit)
            {
                Assert.NotEmpty(_audit);
                return _audit[_audit.Count - 1];
            }
        }

        [Fact]
        public void An_allowed_action_runs_once_on_the_main_thread_and_is_audited()
        {
            var ranOnMain = false;
            var calls = 0;
            _registry.Register("cwy", Play(), invocation =>
            {
                calls++;
                ranOnMain = _dispatcher.Queue.IsMainThread;
                Assert.Equal("Rain", invocation.Args.GetString("title"));
                Assert.False(invocation.Confirmed);
                return ActionResult.Ok("playing Rain");
            });

            var result = Drive(NewRunner().RunAsync(Request("MUSIC.PLAY", ActionArgs.Of(("title", "Rain"), ("volume", "0.5"))), CancellationToken.None));

            Assert.Equal(ActionStatus.Ok, result.Status);
            Assert.Equal(ActionPath.Semantic, result.Path);
            Assert.Equal(1, calls);
            Assert.True(ranOnMain);
            Assert.Equal(new[] { ActionRunner.Owner }, _dispatcher.Owners.ToArray());

            var record = LastAudit();
            Assert.Equal("music.play", record.Name);
            Assert.Equal(ActionTier.Act, record.Tier);
            Assert.Equal(ActionStatus.Ok, record.Status);
            Assert.Equal(ActionPath.Semantic, record.Path);
            Assert.Equal("title=Rain volume=0.5", record.Args);
            Assert.Equal("playing Rain", record.Detail);
            Assert.False(record.Late);
        }

        [Fact]
        public async Task An_unknown_name_is_not_found_and_its_values_are_not_audited()
        {
            var result = await NewRunner().RunAsync(Request("music.nope", ActionArgs.Of(("text", "dear diary"))), CancellationToken.None);

            Assert.Equal(ActionStatus.NotFound, result.Status);
            Assert.Empty(_dispatcher.Owners);
            Assert.Equal("text=<private:10>", LastAudit().Args);
            Assert.Null(LastAudit().Tier);
        }

        [Fact]
        public async Task Arguments_that_do_not_fit_never_reach_the_handler()
        {
            var calls = 0;
            _registry.Register("cwy", Play(), _ => { calls++; return ActionResult.Ok("x"); });
            var runner = NewRunner();

            var missing = await runner.RunAsync(Request("music.play"), CancellationToken.None);
            var unknown = await runner.RunAsync(Request("music.play", ActionArgs.Of(("title", "a"), ("speed", 2))), CancellationToken.None);
            var range = await runner.RunAsync(Request("music.play", ActionArgs.Of(("title", "a"), ("volume", 3))), CancellationToken.None);

            Assert.Equal(ActionStatus.InvalidArgs, missing.Status);
            Assert.Contains("missing required argument \"title\"", missing.Message);
            Assert.Equal(ActionStatus.InvalidArgs, unknown.Status);
            Assert.Contains("unknown argument \"speed\"", unknown.Message);
            Assert.Equal(ActionStatus.InvalidArgs, range.Status);
            Assert.Contains("at most 1", range.Message);
            Assert.Equal(0, calls);
            Assert.Empty(_dispatcher.Owners);
        }

        [Fact]
        public async Task A_write_is_denied_until_the_writes_switch_is_on()
        {
            var add = new ActionDescriptor("todo.add", ActionTier.Write, "Adds a todo.",
                new[] { new ActionParameter("title", ActionValueKind.String, true, "The todo text.") });
            var calls = 0;
            _registry.Register("cwy", add, _ => { calls++; return ActionResult.Ok("added 'buy milk'"); });
            var runner = NewRunner();

            var denied = await runner.RunAsync(Request("todo.add", ActionArgs.Of(("title", "buy milk"))), CancellationToken.None);
            Assert.Equal(ActionStatus.Denied, denied.Status);
            Assert.Contains(ActionGateSettings.AllowWritesKey, denied.Message);
            Assert.Equal(0, calls);

            var on = true;
            _services.UseWritesSwitch(() => on);
            var allowed = Drive(runner.RunAsync(Request("todo.add", ActionArgs.Of(("title", "buy milk"))), CancellationToken.None));
            Assert.Equal(ActionStatus.Ok, allowed.Status);
            Assert.Equal(1, calls);

            // The player's text never reaches the audit: argument and handler message are lengths only.
            var record = LastAudit();
            Assert.Equal("title=<private:8>", record.Args);
            Assert.Equal("<private:16>", record.Detail);

            on = false;
            Assert.Equal(ActionStatus.Denied, (await runner.RunAsync(Request("todo.add", ActionArgs.Of(("title", "x"))), CancellationToken.None)).Status);
        }

        [Fact]
        public async Task The_api_may_only_read_until_api_actions_are_switched_on()
        {
            _registry.Register("cwy", Play(), _ => ActionResult.Ok("playing"));
            _registry.Register("cwy", new ActionDescriptor("music.current", ActionTier.Read, "The current track."), _ => ActionResult.Ok("Rain"));
            var runner = NewRunner();

            Assert.Equal(ActionStatus.Denied, (await runner.RunAsync(Request("music.play", ActionArgs.Of(("title", "a")), ActionCaller.Api, "cwy-mcp"), CancellationToken.None)).Status);
            Assert.Equal(ActionStatus.Ok, Drive(runner.RunAsync(Request("music.current", null, ActionCaller.Api, "cwy-mcp"), CancellationToken.None)).Status);

            _services.UseApiActionsSwitch(() => true);
            Assert.Equal(ActionStatus.Ok, Drive(runner.RunAsync(Request("music.play", ActionArgs.Of(("title", "a")), ActionCaller.Api, "cwy-mcp"), CancellationToken.None)).Status);
        }

        [Fact]
        public async Task A_delete_is_declined_while_no_confirmation_dialog_is_installed()
        {
            var delete = new ActionDescriptor("todo.delete", ActionTier.Write, "Deletes a todo.",
                new[] { new ActionParameter("id", ActionValueKind.Integer, true, "The todo id.") }, isDestructive: true);
            var calls = 0;
            _registry.Register("cwy", delete, _ => { calls++; return ActionResult.Ok("deleted"); });
            _services.UseWritesSwitch(() => true);

            var result = await NewRunner().RunAsync(Request("todo.delete", ActionArgs.Of(("id", 7))), CancellationToken.None);

            Assert.Equal(ActionStatus.Declined, result.Status);
            Assert.Contains(DenyingConfirmationGate.Reason, result.Message);
            Assert.Equal(0, calls);
            Assert.Empty(_dispatcher.Owners);
        }

        [Fact]
        public void An_approved_confirmation_runs_the_action_and_marks_it_confirmed()
        {
            var delete = new ActionDescriptor("note.delete", ActionTier.Write, "Deletes a note page.",
                new[]
                {
                    new ActionParameter("id", ActionValueKind.Integer, true, "The page id."),
                    new ActionParameter("reason", ActionValueKind.String, false, "Why.", isPrivate: true),
                },
                isDestructive: true);
            var confirmed = false;
            _registry.Register("cwy", delete, invocation => { confirmed = invocation.Confirmed; return ActionResult.Ok("deleted"); });
            _services.UseWritesSwitch(() => true);
            _services.UseApiActionsSwitch(() => true);
            var gate = new RecordingGate(ConfirmationResult.Approved());
            _services.UseConfirmationGate(gate);

            var result = Drive(NewRunner().RunAsync(
                Request("note.delete", ActionArgs.Of(("id", 12), ("reason", "my secret diary")), ActionCaller.Api, "cwy-mcp"),
                CancellationToken.None));

            Assert.Equal(ActionStatus.Ok, result.Status);
            Assert.True(confirmed);
            Assert.True(LastAudit().Confirmed);

            var question = Assert.Single(gate.Requests);
            Assert.Equal("note.delete", question.ActionName);
            Assert.True(question.IsDestructive);
            Assert.Equal(ActionCaller.Api, question.Caller);
            Assert.Equal("my secret diary", question.Arguments.GetString("reason"));
            Assert.DoesNotContain("secret", question.Message);
            Assert.Contains("id=12", question.Message);
            Assert.Contains("cannot be undone", question.Message);
        }

        [Fact]
        public async Task A_confirmation_that_says_no_or_breaks_ends_declined()
        {
            var delete = new ActionDescriptor("todo.delete", ActionTier.Act, "Deletes.", null, isDestructive: true);
            _registry.Register("cwy", delete, _ => ActionResult.Ok("deleted"));
            var runner = NewRunner();

            _services.UseConfirmationGate(new RecordingGate(ConfirmationResult.Declined("the player said no")));
            var no = await runner.RunAsync(Request("todo.delete"), CancellationToken.None);
            Assert.Equal(ActionStatus.Declined, no.Status);
            Assert.Contains("the player said no", no.Message);

            _services.UseConfirmationGate(new ThrowingGate());
            var broken = await runner.RunAsync(Request("todo.delete"), CancellationToken.None);
            Assert.Equal(ActionStatus.Declined, broken.Status);
            Assert.Contains(_errors, e => e.Contains("confirmation gate failed"));
            Assert.Empty(_dispatcher.Owners);
        }

        [Fact]
        public void A_throwing_handler_is_failed_and_a_write_keeps_its_message_out_of_the_logs()
        {
            _registry.Register("cwy", Play(), _ => throw new InvalidOperationException("track list empty"));
            _registry.Register("cwy", new ActionDescriptor("note.add", ActionTier.Write, "Adds a page."),
                _ => throw new InvalidOperationException("could not add 'my diary text'"));
            _services.UseWritesSwitch(() => true);
            var runner = NewRunner();

            var act = Drive(runner.RunAsync(Request("music.play", ActionArgs.Of(("title", "a"))), CancellationToken.None));
            Assert.Equal(ActionStatus.Failed, act.Status);
            Assert.Contains("InvalidOperationException: track list empty", act.Message);

            var write = Drive(runner.RunAsync(Request("note.add"), CancellationToken.None));
            Assert.Equal(ActionStatus.Failed, write.Status);
            Assert.DoesNotContain("diary", write.Message);
            lock (_errors)
            {
                Assert.Contains(_errors, e => e.Contains("track list empty"));
                Assert.DoesNotContain(_errors, e => e.Contains("diary"));
            }
        }

        [Fact]
        public void A_handler_that_returns_nothing_is_failed()
        {
            _registry.Register("cwy", new ActionDescriptor("music.stop", ActionTier.Act, "Stops."), _ => null!);

            var result = Drive(NewRunner().RunAsync(Request("music.stop"), CancellationToken.None));

            Assert.Equal(ActionStatus.Failed, result.Status);
            Assert.Contains("returned no result", result.Message);
        }

        [Fact]
        public async Task Work_the_main_thread_never_started_times_out_and_never_runs_late()
        {
            var calls = 0;
            _registry.Register("cwy", new ActionDescriptor("music.stop", ActionTier.Act, "Stops."), _ => { calls++; return ActionResult.Ok("stopped"); });

            // Nothing pumps: the hop must come back on its own wall-clock timer.
            var result = await NewRunner().RunAsync(Request("music.stop", timeout: TimeSpan.FromMilliseconds(50)), CancellationToken.None);

            Assert.Equal(ActionStatus.TimedOut, result.Status);
            Assert.Contains("did not start music.stop", result.Message);
            Assert.Contains("it will not run", result.Message);

            _dispatcher.Queue.Pump(1);
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task A_handler_that_finishes_after_the_timeout_gets_a_late_audit_record()
        {
            var started = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var dispatcher = new QueueDispatcher();
            var runner = new ActionRunner(_registry, dispatcher, _services, record => { lock (_audit) { _audit.Add(record); } });
            _registry.Register("cwy", new ActionDescriptor("music.import", ActionTier.Act, "Imports."), _ =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return ActionResult.Ok("imported 3");
            });

            var stop = false;
            var pump = new Thread(() =>
            {
                dispatcher.Queue.CaptureMainThread();
                while (!Volatile.Read(ref stop))
                {
                    dispatcher.Queue.Pump(1);
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            pump.Start();

            try
            {
                var result = await runner.RunAsync(Request("music.import", timeout: TimeSpan.FromMilliseconds(100)), CancellationToken.None);
                Assert.True(started.IsSet);
                Assert.Equal(ActionStatus.TimedOut, result.Status);
                Assert.Contains("did not finish music.import", result.Message);

                release.Set();
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    lock (_audit)
                    {
                        if (_audit.Count == 2)
                        {
                            break;
                        }
                    }

                    Thread.Sleep(5);
                }

                lock (_audit)
                {
                    Assert.Equal(2, _audit.Count);
                    Assert.Equal(ActionStatus.TimedOut, _audit[0].Status);
                    Assert.False(_audit[0].Late);
                    Assert.Equal(ActionStatus.Ok, _audit[1].Status);
                    Assert.True(_audit[1].Late);
                    Assert.Equal("imported 3", _audit[1].Detail);
                }

                Assert.Equal(1, runner.Stats.Late);
                Assert.Equal(1, runner.Stats.TimedOut);
            }
            finally
            {
                release.Set();
                Volatile.Write(ref stop, true);
                pump.Join(TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task A_caller_that_stops_waiting_before_the_hop_gets_timed_out_and_nothing_runs()
        {
            var calls = 0;
            _registry.Register("cwy", new ActionDescriptor("music.stop", ActionTier.Act, "Stops."), _ => { calls++; return ActionResult.Ok("stopped"); });
            using var cancel = new CancellationTokenSource();
            var task = NewRunner().RunAsync(Request("music.stop"), cancel.Token);

            cancel.Cancel();
            var result = await task;
            _dispatcher.Queue.Pump(1);

            Assert.Equal(ActionStatus.TimedOut, result.Status);
            Assert.Contains("caller stopped waiting before music.stop started", result.Message);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void A_mod_caller_runs_in_its_own_dispatcher_lane()
        {
            _registry.Register("cwy", Play(), _ => ActionResult.Ok("playing"));

            Drive(NewRunner().RunAsync(Request("music.play", ActionArgs.Of(("title", "a")), ActionCaller.Mod, "my.mod"), CancellationToken.None));

            Assert.Equal(new[] { "mod:my.mod" }, _dispatcher.Owners.ToArray());
            Assert.Equal("mod:my.mod", ActionRunner.OwnerFor(Request("x.y", null, ActionCaller.Mod, "my.mod")));
            Assert.Equal(ActionRunner.Owner, ActionRunner.OwnerFor(Request("x.y", null, ActionCaller.Script, "cwy_goodnight_01")));
        }

        [Fact]
        public async Task Timeouts_must_be_positive_and_are_capped()
        {
            _registry.Register("cwy", new ActionDescriptor("music.stop", ActionTier.Act, "Stops."), _ => ActionResult.Ok("stopped"));
            var runner = NewRunner();

            var zero = await runner.RunAsync(Request("music.stop", timeout: TimeSpan.Zero), CancellationToken.None);
            Assert.Equal(ActionStatus.InvalidArgs, zero.Status);

            Drive(runner.RunAsync(Request("music.stop", timeout: TimeSpan.FromHours(1)), CancellationToken.None));
            Assert.Equal(ActionRunner.MaxTimeout, _dispatcher.Timeouts[_dispatcher.Timeouts.Count - 1]);

            Drive(runner.RunAsync(Request("music.stop"), CancellationToken.None));
            Assert.Equal(ActionRunner.DefaultTimeout, _dispatcher.Timeouts[_dispatcher.Timeouts.Count - 1]);
        }

        [Fact]
        public async Task A_closed_dispatcher_answers_not_available()
        {
            _registry.Register("cwy", new ActionDescriptor("music.stop", ActionTier.Act, "Stops."), _ => ActionResult.Ok("stopped"));
            _dispatcher.Queue.Close();

            var result = await NewRunner().RunAsync(Request("music.stop"), CancellationToken.None);

            Assert.Equal(ActionStatus.NotAvailable, result.Status);
            Assert.Contains("quitting", result.Message);
        }

        [Fact]
        public void A_broken_audit_sink_is_reported_and_the_result_still_returned()
        {
            _registry.Register("cwy", new ActionDescriptor("music.current", ActionTier.Read, "Now playing."), _ => ActionResult.Ok("Rain"));
            var runner = new ActionRunner(_registry, _dispatcher, _services, _ => throw new InvalidOperationException("sink down"));

            var result = Drive(runner.RunAsync(Request("music.current"), CancellationToken.None));

            Assert.Equal(ActionStatus.Ok, result.Status);
            Assert.Contains(_errors, e => e.Contains("audit sink failed") && e.Contains("sink down"));
        }

        [Fact]
        public async Task The_runner_never_throws_and_counts_every_outcome()
        {
            _registry.Register("cwy", Play(), _ => ActionResult.Ok("playing"));
            var runner = NewRunner();

            Assert.Equal(ActionStatus.Failed, (await runner.RunAsync(null!, CancellationToken.None)).Status);
            Drive(runner.RunAsync(Request("music.play", ActionArgs.Of(("title", "a"))), CancellationToken.None));
            await runner.RunAsync(Request("music.nope"), CancellationToken.None);
            await runner.RunAsync(Request("music.play"), CancellationToken.None);

            var stats = runner.Stats;
            Assert.Equal(3, stats.Requests);
            Assert.Equal(1, stats.Ok);
            Assert.Equal(2, stats.Refused);
            Assert.Equal(0, stats.Failed);
            Assert.Contains("requests=3 ok=1 refused=2", stats.ToString());
        }

        [Fact]
        public async Task The_shared_door_runs_through_the_installed_runner()
        {
            _registry.Register("cwy", Play(), _ => ActionResult.Ok("playing"));
            Assert.False(_services.HasRunner);
            Assert.Equal(ActionStatus.NotAvailable, (await _services.RunAsync(Request("music.play"))).Status);

            _services.UseRunner(NewRunner());
            var result = Drive(_services.RunAsync(Request("music.play", ActionArgs.Of(("title", "a")))));

            Assert.True(_services.HasRunner);
            Assert.Equal(ActionStatus.Ok, result.Status);
        }

        private sealed class QueueDispatcher : IActionDispatcher
        {
            public QueueDispatcher() => Queue = new MainThreadQueue(() => 0d);

            public MainThreadQueue Queue { get; }

            public List<string> Owners { get; } = new List<string>();

            public List<TimeSpan> Timeouts { get; } = new List<TimeSpan>();

            public Task<ActionResult> InvokeAsync(string owner, string label, Func<ActionResult> work, TimeSpan timeout, CancellationToken cancellationToken)
            {
                lock (Owners)
                {
                    Owners.Add(owner);
                    Timeouts.Add(timeout);
                }

                return Queue.InvokeAsync(owner, label, work, timeout, cancellationToken);
            }
        }

        private sealed class RecordingGate : IConfirmationGate
        {
            private readonly ConfirmationResult _answer;

            public RecordingGate(ConfirmationResult answer) => _answer = answer;

            public List<ConfirmationRequest> Requests { get; } = new List<ConfirmationRequest>();

            public Task<ConfirmationResult> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(_answer);
            }
        }

        private sealed class ThrowingGate : IConfirmationGate
        {
            public Task<ConfirmationResult> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("dialog prefab missing");
        }
    }
}
