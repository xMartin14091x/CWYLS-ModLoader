using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Threading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FIX (review #3, #9 and the API-freeze items): the dispatcher's two lanes - framework work
    /// in full every pump, mod work budgeted and round-robin - ready-time order for delayed work,
    /// the <see cref="DelayedWork"/> handle, <see cref="MainThreadQueue.Close"/>, clamped timeouts
    /// and the blocking <c>Invoke(Action)</c>. Reviewer probes P3, P4 and P5 are the first three.
    /// </summary>
    public class MainThreadQueueLaneTests
    {
        private double _now;
        private readonly List<MainThreadFailure> _failures = new List<MainThreadFailure>();

        [Fact]
        public void P4_framework_work_is_not_held_back_by_a_mod_backlog()
        {
            var queue = NewQueue();
            for (var i = 0; i < 100; i++)
            {
                queue.Post("mod:chatty", "chunk " + i, () => _now += 0.005); // 5 ms of work each
            }

            var restored = false;
            queue.PostAfter(0.001, "satone.motion", "restore", () => restored = true);
            _now += 0.002; // the restore is due

            queue.Pump(0.004);

            Assert.True(restored);
            Assert.Equal(99, queue.PendingOf("mod:chatty"));
        }

        [Fact]
        public void P5_CancelDelayed_also_drops_a_due_item_still_waiting_in_its_lane()
        {
            var queue = NewQueue();
            var ran = new List<string>();
            queue.Post("mod:x", "slow", () => { ran.Add("slow"); _now += 0.010; });
            queue.Post("mod:x", "slow2", () => { ran.Add("slow2"); _now += 0.010; });
            queue.PostAfter(0.001, "mod:x", "restore", () => ran.Add("restore"));
            _now += 0.002;

            queue.Pump(0.004); // promotes the restore; the budget stops after one item
            Assert.Equal(new[] { "slow" }, ran);
            Assert.Equal(0, queue.DelayedOf("mod:x"));

            Assert.Equal(1, queue.CancelDelayed("mod:x"));
            queue.Pump(1);
            queue.Pump(1);

            Assert.Equal(new[] { "slow", "slow2" }, ran);
            Assert.Equal(1, queue.Snapshot().Cancelled);
        }

        [Fact]
        public async Task P3_a_huge_timeout_waits_forever_instead_of_throwing_after_queueing()
        {
            var queue = NewQueue();
            var ran = 0;

            var maxValue = queue.InvokeAsync("mod:x", "forever", () => ++ran, TimeSpan.MaxValue);
            var thirtyDays = queue.InvokeAsync("mod:x", "a month", () => ++ran, TimeSpan.FromDays(30));
            queue.Pump(1);

            Assert.Equal(1, await maxValue);
            Assert.Equal(2, await thirtyDays);
            Assert.Equal(0, queue.Snapshot().TimedOut);
        }

        [Fact]
        public void A_negative_timeout_is_refused_before_anything_is_queued()
        {
            var queue = NewQueue();

            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = queue.InvokeAsync("mod:x", "x", () => 1, TimeSpan.FromMilliseconds(-5)); });
            Assert.Equal(0, queue.PendingCount);
            Assert.Equal(0, queue.Snapshot().Posted);
        }

        [Fact]
        public void Framework_work_runs_in_full_whatever_the_budget()
        {
            var queue = NewQueue();
            var ran = 0;
            for (var i = 0; i < 10; i++)
            {
                queue.Post("satone.say", "line " + i, () => { ran++; _now += 0.005; });
            }

            var result = queue.Pump(0.004);

            Assert.Equal(10, ran);
            Assert.Equal(10, result.Ran);
            Assert.Equal(0, result.Left);
            Assert.Equal(0, queue.Snapshot().Deferred);
        }

        [Fact]
        public void Framework_work_runs_before_mod_work_in_a_pump()
        {
            var queue = NewQueue();
            var order = new List<string>();
            queue.Post("mod:a", "mod", () => order.Add("mod"));
            queue.Post("cwy", "framework", () => order.Add("framework"));

            queue.Pump(1);

            Assert.Equal(new[] { "framework", "mod" }, order);
        }

        [Fact]
        public void At_least_one_mod_item_runs_even_after_slow_framework_work()
        {
            var queue = NewQueue();
            var modRan = 0;
            queue.Post("cwy", "slow", () => _now += 1);
            queue.Post("mod:a", "one", () => modRan++);
            queue.Post("mod:a", "two", () => modRan++);

            queue.Pump(0);

            Assert.Equal(1, modRan);
        }

        [Fact]
        public void Mods_take_turns_across_pumps()
        {
            var queue = NewQueue();
            var order = new List<string>();
            foreach (var mod in new[] { "a", "b", "c" })
            {
                for (var i = 1; i <= 3; i++)
                {
                    var name = mod + i;
                    queue.Post("mod:" + mod, name, () => { order.Add(name); _now += 0.25; });
                }
            }

            queue.Pump(0.5); // two items
            Assert.Equal(new[] { "a1", "b1" }, order);

            queue.Pump(0.5);
            Assert.Equal(new[] { "a1", "b1", "c1", "a2" }, order);

            queue.Pump(10);
            Assert.Equal(new[] { "a1", "b1", "c1", "a2", "b2", "c2", "a3", "b3", "c3" }, order);
            Assert.Equal(0, queue.Snapshot().ModOwners);
        }

        [Fact]
        public void A_mod_whose_queue_empties_leaves_the_rotation_without_skipping_the_next()
        {
            var queue = NewQueue();
            var order = new List<string>();
            queue.Post("mod:a", "a1", () => { order.Add("a1"); _now += 0.25; });
            queue.Post("mod:b", "b1", () => { order.Add("b1"); _now += 0.25; });
            queue.Post("mod:b", "b2", () => { order.Add("b2"); _now += 0.25; });
            queue.Post("mod:c", "c1", () => { order.Add("c1"); _now += 0.25; });

            queue.Pump(0.25); // a1; a leaves the ring
            queue.Pump(0.25); // b1
            queue.Pump(0.25); // c1
            queue.Pump(0.25); // b2

            Assert.Equal(new[] { "a1", "b1", "c1", "b2" }, order);
        }

        [Fact]
        public void A_delayed_item_runs_after_older_posted_work_and_before_newer_work()
        {
            var queue = NewQueue();
            var order = new List<string>();
            queue.Post("mod:x", "old", () => order.Add("old"));
            queue.PostAfter(1, "mod:x", "delayed", () => order.Add("delayed"));
            _now = 2;
            queue.Post("mod:x", "new", () => order.Add("new"));

            queue.Pump(10);

            Assert.Equal(new[] { "old", "delayed", "new" }, order);
        }

        [Fact]
        public void The_same_ready_order_holds_in_the_framework_lane()
        {
            var queue = NewQueue();
            var order = new List<string>();
            queue.PostAfter(3, "cwy", "late", () => order.Add("late"));
            queue.Post("cwy", "old", () => order.Add("old"));
            queue.PostAfter(1, "cwy", "early", () => order.Add("early"));
            _now = 2;
            queue.Post("cwy", "new", () => order.Add("new"));
            _now = 5;

            queue.Pump(0);

            Assert.Equal(new[] { "old", "early", "new", "late" }, order);
        }

        [Fact]
        public void A_delayed_handle_cancels_before_the_work_runs()
        {
            var queue = NewQueue();
            var ran = false;

            var handle = queue.PostAfter(1, "satone.motion", "restore", () => ran = true);
            Assert.True(handle.IsAccepted);
            Assert.True(handle.IsPending);
            Assert.Null(handle.RejectReason);

            handle.Dispose();
            handle.Dispose();
            _now = 5;
            queue.Pump(1);

            Assert.False(ran);
            Assert.False(handle.IsPending);
            Assert.False(handle.Cancel());
            Assert.Equal(0, queue.DelayedCount);
            Assert.Equal(1, queue.Snapshot().Cancelled);
        }

        [Fact]
        public void A_handle_also_cancels_an_item_already_due_and_waiting()
        {
            var queue = NewQueue();
            var ran = new List<string>();
            queue.Post("mod:x", "slow", () => { ran.Add("slow"); _now += 1; });
            var handle = queue.PostAfter(0.5, "mod:x", "later", () => ran.Add("later"));
            _now = 1;

            queue.Pump(0.1); // "later" is promoted but the budget is spent on "slow"
            Assert.True(handle.IsPending);
            Assert.True(handle.Cancel());
            queue.Pump(1);

            Assert.Equal(new[] { "slow" }, ran);
            Assert.Equal(0, queue.PendingCount);
            Assert.Equal(0, queue.Snapshot().ModOwners);
        }

        [Fact]
        public void A_handle_after_the_work_ran_cancels_nothing()
        {
            var queue = NewQueue();
            var runs = 0;
            var handle = queue.PostAfter(0, "cwy", "now", () => runs++);

            queue.Pump(1);

            Assert.False(handle.IsPending);
            Assert.False(handle.Cancel());
            Assert.Equal(1, runs);
        }

        [Fact]
        public void A_closed_queue_refuses_new_work_and_still_runs_what_it_holds()
        {
            var queue = NewQueue();
            var ran = new List<string>();
            queue.Post("cwy", "before", () => ran.Add("before"));

            queue.Close();
            queue.Close();

            Assert.True(queue.IsClosed);
            Assert.False(queue.Post("cwy", "after", () => ran.Add("after")));
            var delayed = queue.PostAfter(1, "cwy", "later", () => ran.Add("later"));
            Assert.False(delayed.IsAccepted);
            Assert.Equal(MainThreadRejectReason.ShuttingDown, delayed.RejectReason);
            var invoke = queue.InvokeAsync("mod:x", "ask", () => 1);
            var rejected = Assert.IsType<MainThreadRejectedException>(invoke.Exception!.InnerException);
            Assert.Equal(MainThreadRejectReason.ShuttingDown, rejected.Reason);

            queue.Pump(1);
            Assert.Equal(new[] { "before" }, ran);
            Assert.Equal(0, queue.Snapshot().Dropped);
        }

        [Fact]
        public void Invoke_of_an_action_runs_inline_on_the_main_thread()
        {
            var queue = NewQueue();
            queue.CaptureMainThread();
            var ran = false;

            queue.Invoke("cwy", "inline", () => { ran = true; }, TimeSpan.FromSeconds(1));

            Assert.True(ran);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public async Task Invoke_of_an_action_from_a_worker_waits_and_rethrows()
        {
            var queue = NewQueue();
            queue.CaptureMainThread();

            var worker = OffThread.Run(() => queue.Invoke("cwy", "boom", () => { throw new FormatException("bad"); }, TimeSpan.FromSeconds(5)));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!worker.IsCompleted && DateTime.UtcNow < deadline)
            {
                queue.Pump(1);
                Thread.Sleep(1);
            }

            await Assert.ThrowsAsync<FormatException>(() => worker);
        }

        [Fact]
        public async Task Invoke_of_an_action_on_a_closed_queue_throws_the_rejection()
        {
            var queue = NewQueue();
            queue.CaptureMainThread();
            queue.Close();

            // A dedicated thread (LongRunning), never the pool: the test's own pool thread is the captured
            // "main thread", and Task.Run could hand the work back to it, where Invoke runs inline by design
            // (flaky ~1 in 6 runs before 2026-10-02).
            var worker = Task.Factory.StartNew(
                () => queue.Invoke("mod:x", "late", () => { }, TimeSpan.FromSeconds(5)),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var error = await Assert.ThrowsAsync<MainThreadRejectedException>(() => worker);
            Assert.Equal(MainThreadRejectReason.ShuttingDown, error.Reason);
            Assert.Throws<ArgumentNullException>(() => queue.Invoke("cwy", "x", (Action)null!, TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public async Task A_timed_out_mod_invocation_leaves_the_rotation_clean()
        {
            var queue = NewQueue();

            var task = queue.InvokeAsync("mod:x", "stalled", () => 1, TimeSpan.FromMilliseconds(20));
            await Assert.ThrowsAsync<TimeoutException>(() => task);

            var stats = queue.Snapshot();
            Assert.Equal(0, stats.ModPending);
            Assert.Equal(0, stats.ModOwners);
            Assert.Equal(0, queue.PendingOf("mod:x"));
        }

        [Fact]
        public void Cancel_of_a_mod_owner_clears_its_lane_and_keeps_the_others()
        {
            var queue = NewQueue();
            var ran = new List<string>();
            queue.Post("mod:a", "a", () => ran.Add("a"));
            queue.Post("mod:b", "b", () => ran.Add("b"));
            queue.Post("mod:a", "a2", () => ran.Add("a2"));

            Assert.Equal(2, queue.Cancel("mod:a"));
            Assert.Equal(1, queue.Snapshot().ModOwners);
            queue.Pump(1);

            Assert.Equal(new[] { "b" }, ran);
        }

        [Fact]
        public void The_stats_show_both_lanes()
        {
            var queue = NewQueue();
            queue.Post("cwy", "f1", () => { });
            queue.Post("mod:a", "m1", () => { });
            queue.Post("mod:b", "m2", () => { });

            var before = queue.Snapshot();
            Assert.Equal(1, before.FrameworkPending);
            Assert.Equal(2, before.ModPending);
            Assert.Equal(2, before.ModOwners);
            Assert.Equal(3, before.Pending);

            queue.Pump(1);
            var after = queue.Snapshot();
            Assert.Equal(1, after.FrameworkRan);
            Assert.Equal(2, after.ModRan);
            Assert.Equal(3, after.Ran);
            Assert.EndsWith("; framework pending=0 ran=1; mods pending=0 ran=2 owners=0", after.ToString());
        }

        [Theory]
        [InlineData("mod:x", true)]
        [InlineData("mod:", true)]
        [InlineData("satone.motion", false)]
        [InlineData("MOD:x", false)]
        [InlineData(null, false)]
        public void Only_the_mod_prefix_puts_work_in_the_mod_lane(string? owner, bool isMod) =>
            Assert.Equal(isMod, MainThreadQueue.IsModOwner(owner));

        [Fact]
        public void A_nan_or_negative_budget_still_runs_one_mod_item()
        {
            var queue = NewQueue();
            var ran = 0;
            queue.Post("mod:a", "1", () => ran++);
            queue.Post("mod:a", "2", () => ran++);

            queue.Pump(double.NaN);
            Assert.Equal(1, ran);
            queue.Pump(-1);
            Assert.Equal(2, ran);
        }

        [Fact]
        public void The_rejection_message_names_owner_label_and_reason()
        {
            Assert.Equal(
                "the main-thread queue of 'mod:x' is full; 'load' was not queued",
                new MainThreadRejectedException(MainThreadRejectReason.QueueFull, "mod:x", "load").Message);
            Assert.Equal(
                "'mod:x' is not running; 'load' was not run",
                new MainThreadRejectedException(MainThreadRejectReason.OwnerNotRunning, "mod:x", "load").Message);
            Assert.Contains("quitting", new MainThreadRejectedException(MainThreadRejectReason.ShuttingDown, null, null).Message);
        }

        private MainThreadQueue NewQueue() => new MainThreadQueue(() => _now, _failures.Add);
    }
}
