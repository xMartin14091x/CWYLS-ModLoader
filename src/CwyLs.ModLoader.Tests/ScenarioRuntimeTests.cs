using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Scenario;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// E-SCRIPT: the handler contract the runtime enforces (one decision, default continue, a throw or
    /// a missing handler never stalls, unknown jump targets continue, waits are clamped) and the wait
    /// book the game side polls once a frame.
    /// </summary>
    public class ScenarioRuntimeTests
    {
        private static ScenarioCommandRow Row(int command = ScenarioCommandIds.Wait, string arg1 = "1") =>
            new ScenarioCommandRow("r_010", "g_01", 3, command, "cwy.wait", arg1, 0f, "0", "0", "0");

        private static (ScenarioCommandRegistry Registry, List<string> Warnings) Registry(ScenarioCommandHandler handler, string name = "my.mod.go")
        {
            var registry = new ScenarioCommandRegistry();
            registry.Register("my.mod", new ScenarioCommandDescriptor(name, "Goes."), handler);
            return (registry, new List<string>());
        }

        private static ScenarioCommandRow RowFor(ScenarioCommandRegistry registry, string name)
        {
            Assert.True(registry.TryResolve(name, out var id));
            return new ScenarioCommandRow("r_010", "g_01", 3, id, name, "a", 1.5f, "b", "c", "0");
        }

        [Fact]
        public void A_handler_that_decides_nothing_continues()
        {
            var (registry, warnings) = Registry(_ => { });
            var decision = ScenarioCommandRunner.Run(registry, RowFor(registry, "my.mod.go"), "ExtraScenario", null, warnings.Add);

            Assert.Equal(ScenarioCommandOutcome.Continue, decision.Outcome);
            Assert.Empty(warnings);
        }

        [Fact]
        public void A_row_without_a_handler_continues_with_a_warning()
        {
            var registry = new ScenarioCommandRegistry();
            var warnings = new List<string>();
            Assert.True(registry.TryResolve("nobody.handles.this", out var id));

            var decision = ScenarioCommandRunner.Run(
                registry, new ScenarioCommandRow("r", "g", 0, id, "nobody.handles.this", "", 0f, "", "", ""), "ExtraScenario", null, warnings.Add);

            Assert.Equal(ScenarioCommandOutcome.Continue, decision.Outcome);
            Assert.Contains(warnings, w => w.Contains("no handler", StringComparison.Ordinal));
        }

        [Fact]
        public void A_throwing_handler_continues_and_names_its_owner_even_after_deciding()
        {
            var (registry, warnings) = Registry(ctx =>
            {
                ctx.JumpTo("r_999");
                throw new InvalidOperationException("boom");
            });

            var decision = ScenarioCommandRunner.Run(registry, RowFor(registry, "my.mod.go"), "ExtraScenario", _ => true, warnings.Add);

            Assert.Equal(ScenarioCommandOutcome.Continue, decision.Outcome);
            Assert.Contains(warnings, w => w.Contains("my.mod", StringComparison.Ordinal) && w.Contains("boom", StringComparison.Ordinal));
        }

        [Fact]
        public void The_handler_sees_its_row_group_and_scene_type()
        {
            ScenarioCommandRow? seen = null;
            string? type = null;
            var (registry, warnings) = Registry(ctx =>
            {
                seen = ctx.Row;
                type = ctx.ScenarioType;
                Assert.Equal("g_01", ctx.GroupId);
                Assert.False(ctx.HasDecided);
                ctx.Continue();
                Assert.True(ctx.HasDecided);
            });

            ScenarioCommandRunner.Run(registry, RowFor(registry, "my.mod.go"), "MotionScenario", null, warnings.Add);

            Assert.NotNull(seen);
            Assert.Equal("a", seen!.Arg1);
            Assert.Equal(1.5f, seen.Arg2);
            Assert.Equal("b", seen.Arg3);
            Assert.Equal(3, seen.Index);
            Assert.Equal("MotionScenario", type);
            Assert.Empty(warnings);
        }

        [Fact]
        public void JumpTo_a_row_of_the_group_jumps_and_an_unknown_one_continues_with_a_warning()
        {
            var rows = new HashSet<string> { "r_100", "r_200" };
            var warnings = new List<string>();

            var ok = new ScenarioCommandContext(Row(), "ExtraScenario", rows.Contains, warnings.Add);
            ok.JumpTo(" r_200 ");
            Assert.Equal(ScenarioCommandOutcome.Jump, ok.Decision.Outcome);
            Assert.Equal("r_200", ok.Decision.JumpTarget);
            Assert.Equal("jump r_200", ok.Decision.ToString());

            var missing = new ScenarioCommandContext(Row(), "ExtraScenario", rows.Contains, warnings.Add);
            missing.JumpTo("r_300");
            Assert.Equal(ScenarioCommandOutcome.Continue, missing.Decision.Outcome);
            Assert.True(missing.HasDecided);
            Assert.Contains(warnings, w => w.Contains("r_300", StringComparison.Ordinal));
        }

        [Fact]
        public void A_second_decision_is_ignored_with_a_warning()
        {
            var warnings = new List<string>();
            var context = new ScenarioCommandContext(Row(), "ExtraScenario", _ => true, warnings.Add);

            context.WaitFor(() => true, TimeSpan.FromSeconds(1));
            context.JumpTo("r_100");
            context.Continue();

            Assert.Equal(ScenarioCommandOutcome.Wait, context.Decision.Outcome);
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, w => Assert.Contains("ignored", w, StringComparison.Ordinal));
        }

        [Fact]
        public void WaitFor_is_clamped_to_five_minutes_and_needs_a_check()
        {
            var warnings = new List<string>();
            var longWait = new ScenarioCommandContext(Row(), "", null, warnings.Add);
            longWait.WaitFor(() => false, TimeSpan.FromHours(1));
            Assert.Equal(ScenarioCommandContext.MaxWait, longWait.Decision.WaitTimeout);
            Assert.Single(warnings);

            var negative = new ScenarioCommandContext(Row(), "", null, warnings.Add);
            negative.WaitFor(() => false, TimeSpan.FromSeconds(-3));
            Assert.Equal(TimeSpan.Zero, negative.Decision.WaitTimeout);

            var nothing = new ScenarioCommandContext(Row(), "", null, warnings.Add);
            nothing.WaitFor(null!, TimeSpan.FromSeconds(3));
            Assert.Equal(ScenarioCommandOutcome.Continue, nothing.Decision.Outcome);
            Assert.Equal(2, warnings.Count);
        }

        [Fact]
        public void The_wait_book_ends_entries_as_done_timed_out_threw_or_abandoned()
        {
            var book = new ScenarioWaitBook();
            var done = false;
            var a = book.Add("done", () => done, TimeSpan.FromSeconds(10), 0, "cursor-a");
            var b = book.Add("timeout", () => false, TimeSpan.FromSeconds(2), 0, "cursor-b");
            var c = book.Add("throws", () => throw new InvalidOperationException("bad check"), TimeSpan.FromSeconds(10), 0, "cursor-c");
            var d = book.Add("moved", () => throw new InvalidOperationException("must not run"), TimeSpan.FromSeconds(10), 0, "cursor-d");

            var first = book.Poll(1, cursor => !Equals(cursor, "cursor-d"));
            Assert.Equal(new[] { c, d }, new[] { first[0].Ticket, first[1].Ticket });
            Assert.Equal(ScenarioWaitEndKind.Threw, first[0].Kind);
            Assert.Contains("bad check", first[0].Detail, StringComparison.Ordinal);
            Assert.Equal(ScenarioWaitEndKind.Abandoned, first[1].Kind);
            Assert.Equal(2, book.Count);

            var second = book.Poll(2, _ => true);
            Assert.Single(second);
            Assert.Equal(b, second[0].Ticket);
            Assert.Equal(ScenarioWaitEndKind.TimedOut, second[0].Kind);
            Assert.Equal(2d, second[0].WaitedSeconds);

            done = true;
            var third = book.Poll(3);
            Assert.Single(third);
            Assert.Equal(a, third[0].Ticket);
            Assert.Equal(ScenarioWaitEndKind.Done, third[0].Kind);
            Assert.Equal("cursor-a", third[0].Cursor);
            Assert.Equal(0, book.Count);
            Assert.Empty(book.Poll(4));
        }

        [Fact]
        public void A_check_met_at_its_deadline_counts_as_done_and_clear_drops_everything()
        {
            var book = new ScenarioWaitBook();
            book.Add("edge", () => true, TimeSpan.FromSeconds(1), 0, null);
            Assert.Equal(ScenarioWaitEndKind.Done, book.Poll(1)[0].Kind);

            book.Add("x", () => false, TimeSpan.FromSeconds(1), 0, null);
            book.Add("y", () => false, TimeSpan.FromSeconds(1), 0, null);
            Assert.Equal(new[] { "x", "y" }, book.Labels);
            Assert.Equal(2, book.Clear());
            Assert.Empty(book.Poll(5));
        }

        [Fact]
        public void A_throwing_cursor_check_abandons_the_entry_without_running_its_check()
        {
            var book = new ScenarioWaitBook();
            var ran = false;
            book.Add("w", () => ran = true, TimeSpan.FromSeconds(1), 0, null);

            var ended = book.Poll(0.5, _ => throw new InvalidOperationException("reader gone"));

            Assert.Equal(ScenarioWaitEndKind.Abandoned, ended[0].Kind);
            Assert.Contains("reader gone", ended[0].Detail, StringComparison.Ordinal);
            Assert.False(ran);
        }
    }
}
