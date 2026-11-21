using System;
using System.Linq;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class MakerGameActivityTests
    {
        [Fact]
        public void Host_start_coalesces_initial_events_and_defers_the_next_busy_poll()
        {
            var source = System.IO.File.ReadAllText(System.IO.Path.Combine(TestRepo.Root!,
                "src/CwyLs.ModLoader.Bulbul/Maker/MakerActivityHost.cs"));
            var start = source.Substring(source.IndexOf("private void Start()", StringComparison.Ordinal));
            start = start.Substring(0, start.IndexOf("private void Update()", StringComparison.Ordinal));
            Assert.Contains("PollBusy(publish: false)", start);
            Assert.Contains("_nextBusyPoll = Time.unscaledTime + 0.25f", start);
            Assert.Equal(1, start.Split(new[] { "PublishIf(" }, StringSplitOptions.None).Length - 1);
        }
        [Fact]
        public void It_starts_unknown_and_stale_without_inventing_pause()
        {
            var activity = new MakerGameActivity(new ManualApiClock(), TimeSpan.FromSeconds(3));

            var state = activity.Snapshot();

            Assert.Null(state.Focused);
            Assert.Null(state.Paused);
            Assert.Null(state.Busy);
            Assert.Null(state.Reason);
            Assert.Null(state.ObservedAt);
            Assert.True(state.Stale);
        }

        [Fact]
        public void Focus_pause_and_busy_are_independent_and_transitions_deduplicate()
        {
            var clock = new ManualApiClock();
            var activity = new MakerGameActivity(clock, TimeSpan.FromSeconds(3));

            Assert.True(activity.ObserveFocus(false));
            Assert.False(activity.ObserveFocus(false));
            Assert.Equal("game_unfocused", activity.Snapshot().Reason);
            Assert.Null(activity.Snapshot().Paused);

            Assert.True(activity.ObservePause(true));
            Assert.Equal("game_paused", activity.Snapshot().Reason);
            Assert.True(activity.ObserveBusy(true, "satone_speaking"));
            Assert.Equal("game_paused", activity.Snapshot().Reason);

            Assert.True(activity.ObservePause(false));
            Assert.Equal("satone_speaking", activity.Snapshot().Reason);
            Assert.True(activity.ObserveFocus(true));
            Assert.True(activity.ObserveBusy(false, null));

            var ready = activity.Snapshot();
            Assert.True(ready.Focused);
            Assert.False(ready.Paused);
            Assert.False(ready.Busy);
            Assert.Null(ready.Reason);
        }

        [Fact]
        public void Heartbeat_controls_age_without_changing_known_facts()
        {
            var clock = new ManualApiClock();
            var activity = new MakerGameActivity(clock, TimeSpan.FromSeconds(3));
            activity.ObserveFocus(false);
            var observed = activity.Snapshot().ObservedAt;

            clock.Advance(3.1);
            var stale = activity.Snapshot();
            Assert.True(stale.Stale);
            Assert.False(stale.Focused);
            Assert.Null(stale.Paused);
            Assert.Equal("game_unfocused", stale.Reason);

            activity.Heartbeat();
            var fresh = activity.Snapshot();
            Assert.False(fresh.Stale);
            Assert.True(fresh.ObservedAt.HasValue);
            Assert.True(observed.HasValue);
            Assert.True(fresh.ObservedAt.Value > observed.Value);
            Assert.False(fresh.Focused);
        }

        [Fact]
        public async Task Concurrent_callbacks_and_worker_snapshots_are_safe()
        {
            var activity = new MakerGameActivity(new ManualApiClock(), TimeSpan.FromSeconds(3));
            activity.Heartbeat();
            var writers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                for (var n = 0; n < 1000; n++)
                {
                    activity.ObserveFocus((n + i) % 2 == 0);
                    activity.ObservePause((n + i) % 3 == 0);
                    activity.ObserveBusy((n + i) % 5 == 0, "poll");
                }
            }));
            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (var n = 0; n < 1000; n++)
                {
                    Assert.NotNull(activity.Snapshot().ObservedAt);
                }
            }));

            await Task.WhenAll(writers.Concat(readers));
        }
    }
}
