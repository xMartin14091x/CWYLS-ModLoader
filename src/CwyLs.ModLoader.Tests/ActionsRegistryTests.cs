using System;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Actions;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>W0-CONTRACTS: the action registry (owners, duplicates) and the services' "not available" defaults.</summary>
    public class ActionsRegistryTests
    {
        private static readonly ActionHandler Ok = _ => ActionResult.Ok("done");

        private static ActionDescriptor Act(string name) => new ActionDescriptor(name, ActionTier.Act, "Does " + name + ".");

        private sealed class ThrowingRunner : IActionRunner
        {
            public Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("runner broke");
        }

        private sealed class NullRunner : IActionRunner
        {
            public Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken) =>
                Task.FromResult<ActionResult>(null!);
        }

        private sealed class CancelledRunner : IActionRunner
        {
            public Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken) =>
                Task.FromCanceled<ActionResult>(new CancellationToken(true));
        }

        [Fact]
        public void A_duplicate_name_is_refused_never_replaced()
        {
            var registry = new ActionRegistry();

            Assert.True(registry.TryRegister("cwy", Act("music.play"), Ok, out var first, out _));
            Assert.False(registry.TryRegister("cwy", Act("music.play"), Ok, out var second, out var problem));
            Assert.Null(second);
            Assert.Contains("already registered by cwy", problem);
            Assert.Throws<InvalidOperationException>(() => registry.Register("cwy", Act("music.play"), Ok));

            Assert.True(registry.TryGet("MUSIC.PLAY", out var binding));
            Assert.Equal("cwy", binding!.Owner);

            first!.Dispose();
            first.Dispose();
            Assert.Equal(0, registry.Count);
            Assert.True(registry.TryRegister("cwy", Act("music.play"), Ok, out _, out _));
        }

        [Fact]
        public void A_mod_registers_only_names_under_its_id()
        {
            var registry = new ActionRegistry();

            Assert.True(registry.TryRegister("my.mod", Act("my.mod.greet"), Ok, out _, out _));
            Assert.False(registry.TryRegister("my.mod", Act("music.play"), Ok, out _, out var foreign));
            Assert.Contains("my.mod.<verb>", foreign);
            Assert.False(registry.TryRegister("my.mod", Act("my.modder.x"), Ok, out _, out _));
            Assert.False(registry.TryRegister("Bad Owner", Act("bad.x"), Ok, out _, out _));
            Assert.False(registry.TryRegister("cwy", null!, Ok, out _, out _));
            Assert.False(registry.TryRegister("cwy", Act("a.b"), null!, out _, out _));
        }

        [Fact]
        public void The_listing_is_sorted_by_name()
        {
            var registry = new ActionRegistry();
            registry.Register("cwy", Act("todo.add"), Ok);
            registry.Register("cwy", Act("music.play"), Ok);
            registry.Register("cwy", Act("music.next"), Ok);

            var names = new[] { registry.Descriptors[0].Name, registry.Descriptors[1].Name, registry.Descriptors[2].Name };
            Assert.Equal(new[] { "music.next", "music.play", "todo.add" }, names);
        }

        [Fact]
        public async Task Without_a_runner_every_request_is_not_available()
        {
            var services = new ActionServices();

            Assert.False(services.HasRunner);
            Assert.Same(UnavailableActionRunner.Instance, services.Runner);
            var result = await services.RunAsync(new ActionRequest("music.play", null, ActionCaller.Mod, "my.mod"));
            Assert.Equal(ActionStatus.NotAvailable, result.Status);
            Assert.Equal(UnavailableActionRunner.Reason, result.Message);
            Assert.Equal(ActionPath.None, result.Path);
        }

        [Fact]
        public async Task A_broken_runner_becomes_a_result_and_a_sink_line()
        {
            var services = new ActionServices();
            var sunk = new System.Collections.Generic.List<string>();
            services.ErrorSink = sunk.Add;
            var request = new ActionRequest("music.play", null, ActionCaller.Console, "console");

            services.UseRunner(new ThrowingRunner());
            Assert.True(services.HasRunner);
            var thrown = await services.RunAsync(request);
            Assert.Equal(ActionStatus.Failed, thrown.Status);
            Assert.Contains("runner broke", thrown.Message);

            services.UseRunner(new NullRunner());
            Assert.Equal(ActionStatus.Failed, (await services.RunAsync(request)).Status);

            services.UseRunner(new CancelledRunner());
            Assert.Equal(ActionStatus.TimedOut, (await services.RunAsync(request)).Status);

            Assert.Equal(2, sunk.Count);
            Assert.Throws<ArgumentNullException>(() => services.UseRunner(null!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => services.RunAsync(null!));
        }

        [Fact]
        public async Task The_default_confirmation_gate_never_approves()
        {
            var services = new ActionServices();
            Assert.False(services.HasConfirmationGate);
            Assert.Same(DenyingConfirmationGate.Instance, services.ConfirmationGate);

            var request = new ConfirmationRequest("todo.delete", "Delete a todo?", "\"buy milk\" will be deleted.", ActionCaller.Api, "cwy-mcp", isDestructive: true);
            var answer = await services.ConfirmationGate.ConfirmAsync(request, CancellationToken.None);

            Assert.Equal(ConfirmationOutcome.Unavailable, answer.Outcome);
            Assert.False(answer.IsApproved);
            Assert.Equal(DenyingConfirmationGate.Reason, answer.Detail);
            Assert.Throws<ArgumentNullException>(() => services.UseConfirmationGate(null!));
        }

        [Fact]
        public void A_confirmation_request_checks_its_text_and_timeout()
        {
            Assert.Throws<ArgumentException>(() => new ConfirmationRequest("x.y", " ", "m", ActionCaller.Api, "c", false));
            Assert.Throws<ArgumentException>(() => new ConfirmationRequest("x.y", "t", "m", ActionCaller.Api, " ", false));
            Assert.Throws<ArgumentException>(() => new ConfirmationRequest("x.y", "t", "m", ActionCaller.Api, "c", false, TimeSpan.Zero));
            Assert.Equal(ConfirmationRequest.DefaultTimeout, new ConfirmationRequest("x.y", "t", "m", ActionCaller.Api, "c", false).Timeout);
            Assert.True(ConfirmationResult.Approved().IsApproved);
            Assert.Equal("Declined: no", ConfirmationResult.Declined("no").ToString());
        }
    }
}
