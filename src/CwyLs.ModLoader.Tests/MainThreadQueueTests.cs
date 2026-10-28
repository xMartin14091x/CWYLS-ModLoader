using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Threading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FRONTDOOR: the one main-thread dispatcher - order, per-frame budget, delays, owners and
    /// caps, exception isolation, tickers, and caller-side timeouts that hold even when nothing
    /// pumps. Time is a fake clock except where a real timer is the point.
    /// </summary>
    public class MainThreadQueueTests
    {
        private double _now;
        private readonly List<MainThreadFailure> _failures = new List<MainThreadFailure>();

        [Fact]
        public void Posted_work_runs_on_the_next_pump_in_order()
        {
            var queue = NewQueue();
            var ran = new List<int>();

            Assert.True(queue.Post("a", "one", () => ran.Add(1)));
            Assert.True(queue.Post("b", "two", () => ran.Add(2)));
            Assert.Empty(ran);

            var result = queue.Pump(1);

            Assert.Equal(new[] { 1, 2 }, ran);
            Assert.Equal(2, result.Ran);
            Assert.Equal(0, result.Left);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public void The_budget_stops_the_mod_lane_and_the_rest_waits_in_order()
        {
            var queue = NewQueue();
            var ran = new List<int>();
            for (var i = 0; i < 5; i++)
            {
                var n = i;
                // Binary fractions, so the budget arithmetic is exact. The budget governs mod work.
                queue.Post("mod:a", "item " + n, () => { ran.Add(n); _now += 0.25; });
            }

            var first = queue.Pump(0.5);
            Assert.Equal(new[] { 0, 1 }, ran);
            Assert.Equal(3, first.Left);

            queue.Pump(0.5);
            Assert.Equal(new[] { 0, 1, 2, 3 }, ran);

            queue.Pump(0.5);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, ran);
            Assert.Equal(2, queue.Snapshot().Deferred);
        }

        [Fact]
        public void At_least_one_mod_item_runs_even_with_no_budget()
        {
            var queue = NewQueue();
            var ran = 0;
            queue.Post("mod:a", "one", () => { ran++; _now += 1; });
            queue.Post("mod:a", "two", () => ran++);

            queue.Pump(0);
            Assert.Equal(1, ran);
            queue.Pump(0);
            Assert.Equal(2, ran);
        }

        [Fact]
        public void Work_posted_during_a_pump_waits_for_the_next_one()
        {
            var queue = NewQueue();
            var count = 0;
            void Again()
            {
                count++;
                queue.Post("a", "again", Again);
            }

            queue.Post("a", "again", Again);

            queue.Pump(10);
            Assert.Equal(1, count);
            queue.Pump(10);
            Assert.Equal(2, count);
        }

        [Fact]
        public void Delayed_work_runs_once_due_in_due_order()
        {
            var queue = NewQueue();
            var ran = new List<string>();

            queue.PostAfter(2, "a", "late", () => ran.Add("late"));
            queue.PostAfter(1, "a", "early", () => ran.Add("early"));
            queue.PostAfter(1, "a", "early2", () => ran.Add("early2"));
            queue.Post("a", "now", () => ran.Add("now"));

            queue.Pump(1);
            Assert.Equal(new[] { "now" }, ran);
            Assert.Equal(3, queue.DelayedCount);

            _now = 1.5;
            queue.Pump(1);
            Assert.Equal(new[] { "now", "early", "early2" }, ran);

            _now = 2;
            queue.Pump(1);
            Assert.Equal(new[] { "now", "early", "early2", "late" }, ran);
            Assert.Equal(0, queue.DelayedCount);
        }

        [Fact]
        public void A_zero_or_negative_delay_is_a_plain_post()
        {
            var queue = NewQueue();
            var ran = 0;

            queue.PostAfter(0, "a", "zero", () => ran++);
            queue.PostAfter(-3, "a", "negative", () => ran++);
            queue.PostAfter(double.NaN, "a", "nan", () => ran++);
            Assert.Equal(0, queue.DelayedCount);

            queue.Pump(1);
            Assert.Equal(3, ran);
        }

        [Fact]
        public void CancelDelayed_drops_only_that_owners_delayed_work()
        {
            var queue = NewQueue();
            var ran = new List<string>();
            queue.PostAfter(1, "satone.motion", "restore", () => ran.Add("restore"));
            queue.PostAfter(1, "mod:x", "mod", () => ran.Add("mod"));
            queue.Post("satone.motion", "now", () => ran.Add("now"));

            Assert.Equal(1, queue.CancelDelayed("satone.motion"));
            Assert.Equal(0, queue.DelayedOf("satone.motion"));
            Assert.Equal(1, queue.DelayedOf("mod:x"));

            _now = 5;
            queue.Pump(1);
            Assert.Equal(new[] { "now", "mod" }, ran);
        }

        [Fact]
        public void Cancel_drops_an_owners_waiting_and_delayed_work_and_cancels_its_tasks()
        {
            var queue = NewQueue();
            var ran = new List<string>();
            queue.Post("mod:x", "a", () => ran.Add("a"));
            queue.PostAfter(1, "mod:x", "b", () => ran.Add("b"));
            var task = queue.InvokeAsync("mod:x", "c", () => 42);
            queue.Post("cwy", "keep", () => ran.Add("keep"));

            Assert.Equal(3, queue.Cancel("mod:x"));
            Assert.True(task.IsCanceled);
            Assert.Equal(0, queue.PendingOf("mod:x"));

            _now = 5;
            queue.Pump(1);
            Assert.Equal(new[] { "keep" }, ran);
            Assert.Equal(3, queue.Snapshot().Cancelled);
        }

        [Fact]
        public void An_owner_cannot_fill_the_queue_for_everyone()
        {
            var queue = new MainThreadQueue(() => _now, _failures.Add, capacity: 10, ownerCapacity: 3, delayedCapacity: 10, ownerDelayedCapacity: 2);

            for (var i = 0; i < 3; i++)
            {
                Assert.True(queue.Post("mod:greedy", "x", () => { }));
            }

            Assert.False(queue.Post("mod:greedy", "x", () => { }));
            Assert.True(queue.Post("satone.say", "line", () => { }));
            Assert.Equal(1, queue.DroppedOf("mod:greedy"));
            Assert.Equal(0, queue.DroppedOf("satone.say"));

            Assert.True(queue.PostAfter(1, "mod:greedy", "d", () => { }).IsAccepted);
            Assert.True(queue.PostAfter(1, "mod:greedy", "d", () => { }).IsAccepted);
            var refused = queue.PostAfter(1, "mod:greedy", "d", () => { });
            Assert.False(refused.IsAccepted);
            Assert.Equal(MainThreadRejectReason.QueueFull, refused.RejectReason);
            Assert.Equal(2, queue.DroppedOf("mod:greedy"));
        }

        [Fact]
        public void The_total_capacity_holds_across_owners()
        {
            var queue = new MainThreadQueue(() => _now, _failures.Add, capacity: 2, ownerCapacity: 10);

            Assert.True(queue.Post("a", "1", () => { }));
            Assert.True(queue.Post("b", "2", () => { }));
            Assert.False(queue.Post("c", "3", () => { }));
            Assert.Equal(1, queue.Snapshot().Dropped);
        }

        [Fact]
        public void A_throwing_item_is_reported_and_the_rest_still_run()
        {
            var queue = NewQueue();
            var ran = new List<int>();
            queue.Post("mod:bad", "boom", () => throw new InvalidOperationException("bad"));
            queue.Post("cwy", "fine", () => ran.Add(2));

            var result = queue.Pump(1);

            Assert.Equal(new[] { 2 }, ran);
            Assert.Equal(2, result.Ran);
            var failure = Assert.Single(_failures);
            Assert.Equal("mod:bad", failure.Owner);
            Assert.Equal("boom", failure.Label);
            Assert.IsType<InvalidOperationException>(failure.Error);
            Assert.False(failure.IsTicker);
            Assert.False(failure.DeliveredToCaller);
            Assert.Equal(1, queue.Snapshot().Failed);
        }

        [Fact]
        public void A_failure_callback_that_throws_does_not_escape_the_pump()
        {
            var queue = new MainThreadQueue(() => _now, _ => throw new Exception("logger broke"));
            queue.Post("a", "boom", () => throw new Exception("bad"));

            var result = queue.Pump(1);

            Assert.Equal(1, result.Ran);
        }

        [Fact]
        public void Tickers_run_every_pump_after_the_work_and_stay_after_throwing()
        {
            var queue = NewQueue();
            var order = new List<string>();
            var throwNow = true;
            queue.AddTicker("satone.say", "tick", () => order.Add("tick"));
            queue.AddTicker("mod:x", "bad", () =>
            {
                if (throwNow)
                {
                    throw new Exception("tick failed");
                }

                order.Add("bad ok");
            });
            queue.Post("a", "work", () => order.Add("work"));

            queue.Pump(1);
            throwNow = false;
            queue.Pump(1);

            Assert.Equal(new[] { "work", "tick", "tick", "bad ok" }, order);
            var failure = Assert.Single(_failures);
            Assert.True(failure.IsTicker);
            Assert.Equal("mod:x", failure.Owner);
            Assert.Equal(2, queue.TickerCount);
        }

        [Fact]
        public void A_disposed_ticker_stops_and_RemoveTickers_removes_an_owners_tickers()
        {
            var queue = NewQueue();
            var a = 0;
            var b = 0;
            var handle = queue.AddTicker("cwy", "a", () => a++);
            queue.AddTicker("mod:x", "b1", () => b++);
            queue.AddTicker("mod:x", "b2", () => b++);

            queue.Pump(1);
            handle.Dispose();
            handle.Dispose();
            Assert.Equal(2, queue.RemoveTickers("mod:x"));
            queue.Pump(1);

            Assert.Equal(1, a);
            Assert.Equal(2, b);
            Assert.Equal(0, queue.TickerCount);
        }

        [Fact]
        public void A_ticker_that_disposes_itself_while_running_is_harmless()
        {
            var queue = NewQueue();
            var runs = 0;
            IDisposable? self = null;
            self = queue.AddTicker("cwy", "once", () =>
            {
                runs++;
                self!.Dispose();
            });

            queue.Pump(1);
            queue.Pump(1);

            Assert.Equal(1, runs);
        }

        [Fact]
        public async Task InvokeAsync_returns_the_result_after_the_pump()
        {
            var queue = NewQueue();
            var task = queue.InvokeAsync("mod:x", "answer", () => 42);

            Assert.False(task.IsCompleted);
            queue.Pump(1);

            Assert.Equal(42, await task);
        }

        [Fact]
        public async Task InvokeAsync_hands_the_exception_to_the_caller_and_records_it()
        {
            var queue = NewQueue();
            var task = queue.InvokeAsync("mod:x", "boom", () => throw new FormatException("bad input"));

            queue.Pump(1);

            var error = await Assert.ThrowsAsync<FormatException>(() => task);
            Assert.Equal("bad input", error.Message);
            Assert.True(Assert.Single(_failures).DeliveredToCaller);
        }

        [Fact]
        public async Task InvokeAsync_is_queued_even_on_the_main_thread()
        {
            var queue = NewQueue();
            queue.CaptureMainThread();
            var ran = false;

            var task = queue.InvokeAsync("cwy", "work", () => { ran = true; });

            Assert.False(ran);
            queue.Pump(1);
            await task;
            Assert.True(ran);
        }

        [Fact]
        public async Task A_caller_times_out_when_nothing_pumps_and_the_work_never_runs()
        {
            var queue = NewQueue();
            var ran = false;

            var task = queue.InvokeAsync("mod:x", "stalled", () => { ran = true; return 1; }, TimeSpan.FromMilliseconds(30));

            var error = await Assert.ThrowsAsync<TimeoutException>(() => WithinSeconds(task, 5));
            Assert.Contains("did not run 'stalled'", error.Message);
            Assert.Equal(0, queue.PendingOf("mod:x"));

            queue.Pump(1);
            Assert.False(ran);
            Assert.Equal(1, queue.Snapshot().TimedOut);
        }

        [Fact]
        public async Task A_caller_times_out_while_the_work_is_running_and_the_result_is_discarded()
        {
            var queue = NewQueue();
            using var release = new ManualResetEventSlim(false);
            var finished = false;

            var task = queue.InvokeAsync(
                "mod:x",
                "slow",
                () =>
                {
                    release.Wait(TimeSpan.FromSeconds(5));
                    finished = true;
                    return 7;
                },
                TimeSpan.FromMilliseconds(50));

            var pump = OffThread.Run(() => queue.Pump(10));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => WithinSeconds(task, 5));
            Assert.Contains("still running", error.Message);

            release.Set();
            await WithinSeconds(pump, 5);
            Assert.True(finished);
            Assert.True(task.IsFaulted);
        }

        [Fact]
        public async Task A_cancelled_token_cancels_the_wait_and_the_work()
        {
            var queue = NewQueue();
            using var cts = new CancellationTokenSource();
            var ran = false;

            var task = queue.InvokeAsync("mod:x", "cancel me", () => { ran = true; }, null, cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithinSeconds(task, 5));
            queue.Pump(1);
            Assert.False(ran);
        }

        [Fact]
        public void An_already_cancelled_token_never_queues()
        {
            var queue = NewQueue();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = queue.InvokeAsync("mod:x", "late", () => 1, null, cts.Token);

            Assert.True(task.IsCanceled);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public async Task A_negative_timeout_is_refused_and_an_infinite_one_is_allowed()
        {
            var queue = NewQueue();

            Assert.Throws<ArgumentOutOfRangeException>(() => Discard(() => queue.InvokeAsync("a", "x", () => 1, TimeSpan.FromSeconds(-1))));
            var task = queue.InvokeAsync("a", "x", () => 1, Timeout.InfiniteTimeSpan);
            queue.Pump(1);
            Assert.Equal(1, await task);
        }

        [Fact]
        public void A_full_queue_faults_the_invocation_at_once()
        {
            var queue = new MainThreadQueue(() => _now, _failures.Add, capacity: 1);
            queue.Post("a", "fill", () => { });

            var task = queue.InvokeAsync("a", "overflow", () => 1);

            Assert.True(task.IsFaulted);
            var rejected = Assert.IsType<MainThreadRejectedException>(task.Exception!.InnerException);
            Assert.Equal(MainThreadRejectReason.QueueFull, rejected.Reason);
            Assert.Equal("a", rejected.Owner);
            Assert.Equal("overflow", rejected.Label);
            Assert.IsAssignableFrom<InvalidOperationException>(rejected);
        }

        [Fact]
        public void Invoke_runs_inline_on_the_main_thread()
        {
            var queue = NewQueue();
            queue.CaptureMainThread();

            Assert.True(queue.IsMainThread);
            Assert.Equal(5, queue.Invoke("cwy", "inline", () => 5, TimeSpan.FromSeconds(1)));
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public async Task Invoke_from_a_worker_waits_for_the_main_thread()
        {
            var queue = NewQueue();
            queue.CaptureMainThread();

            var worker = OffThread.Run(() => queue.Invoke("cwy", "from worker", () => Thread.CurrentThread.ManagedThreadId, TimeSpan.FromSeconds(5)));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!worker.IsCompleted && DateTime.UtcNow < deadline)
            {
                queue.Pump(1);
                Thread.Sleep(1);
            }

            var mainThread = Thread.CurrentThread.ManagedThreadId;
            Assert.Equal(mainThread, await worker);
        }

        [Fact]
        public async Task Invoke_from_a_worker_times_out_without_a_pump()
        {
            var queue = NewQueue();
            queue.CaptureMainThread();

            var worker = OffThread.Run(() => queue.Invoke("cwy", "never", () => 1, TimeSpan.FromMilliseconds(30)));

            await Assert.ThrowsAsync<TimeoutException>(() => WithinSeconds(worker, 5));
        }

        [Fact]
        public void RunOrPost_runs_inline_on_the_main_thread_and_isolates_exceptions()
        {
            var queue = NewQueue();
            queue.CaptureMainThread();
            var ran = false;

            Assert.True(queue.RunOrPost("satone.motion", "face", () => ran = true));
            Assert.True(ran);
            Assert.True(queue.RunOrPost("satone.motion", "boom", () => throw new Exception("x")));
            Assert.Single(_failures);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public async Task RunOrPost_off_the_main_thread_queues()
        {
            var queue = NewQueue();
            var ran = false;

            await OffThread.Run(() => queue.RunOrPost("satone.motion", "face", () => ran = true));

            Assert.False(ran);
            Assert.Equal(1, queue.PendingOf("satone.motion"));
            queue.Pump(1);
            Assert.True(ran);
        }

        [Fact]
        public void Before_capture_no_thread_is_the_main_thread()
        {
            var queue = NewQueue();

            Assert.False(queue.HasMainThread);
            Assert.False(queue.IsMainThread);
            queue.CaptureMainThread();
            Assert.True(queue.HasMainThread);
        }

        [Fact]
        public void Posting_from_many_threads_loses_nothing()
        {
            var queue = new MainThreadQueue(() => _now, _failures.Add, capacity: 10000, ownerCapacity: 10000);
            var ran = 0;

            Parallel.For(0, 1000, i => queue.Post("t" + (i % 7), "p", () => Interlocked.Increment(ref ran)));
            queue.Pump(1000);

            Assert.Equal(1000, ran);
            Assert.Equal(1000, queue.Snapshot().Ran);
        }

        [Fact]
        public void The_stats_line_is_readable()
        {
            var queue = NewQueue();
            queue.Post("a", "x", () => { _now += 0.0009765625; });
            queue.Pump(1);

            var stats = queue.Snapshot();
            Assert.Equal(1, stats.Pumps);
            Assert.Equal(1, stats.Posted);
            Assert.Equal(0.9765625, stats.LastPumpMs);
            Assert.StartsWith("pending=0 delayed=0 tickers=0 ran=1 failed=0 dropped=0 cancelled=0 timedOut=0 deferred=0 maxPumpMs=0.98", stats.ToString());
        }

        [Fact]
        public void Null_arguments_are_refused()
        {
            var queue = NewQueue();

            Assert.Throws<ArgumentNullException>(() => new MainThreadQueue(null!));
            Assert.Throws<ArgumentNullException>(() => queue.Post("a", "x", null!));
            Assert.Throws<ArgumentNullException>(() => queue.RunOrPost("a", "x", null!));
            Assert.Throws<ArgumentNullException>(() => queue.PostAfter(1, "a", "x", null!));
            Assert.Throws<ArgumentNullException>(() => Discard(() => queue.InvokeAsync<int>("a", "x", null!)));
            Assert.Throws<ArgumentNullException>(() => Discard(() => queue.InvokeAsync("a", "x", (Action)null!)));
            Assert.Throws<ArgumentNullException>(() => queue.AddTicker("a", "x", null!));
        }

        private MainThreadQueue NewQueue() => new MainThreadQueue(() => _now, _failures.Add);

        /// <summary>Calls a task-returning member only for its synchronous argument checks.</summary>
        private static void Discard(Func<Task> call) => _ = call();

        private static async Task WithinSeconds(Task task, int seconds)
        {
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
            Assert.Same(task, finished);
            await task;
        }
    }
}
