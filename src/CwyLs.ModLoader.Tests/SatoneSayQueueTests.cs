using CwyLs.ModLoader.Core.Satone.Say;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P2-2: the queue's busy policy - who speaks, who waits, who falls back and who is given up
    /// on. Time is a parameter, so all of it is decided with plain numbers.
    /// </summary>
    public class SatoneSayQueueTests
    {
        private static SayLine Line(int id, double queuedAt = 0d) =>
            new SayLine(id, SayRequest.For("line " + id), "Voice_CWY_" + id, -1, -1, queuedAt);

        [Fact]
        public void Nothing_queued_means_nothing_to_do()
        {
            var queue = new SayQueue();

            var step = queue.Next(0d, canStartNow: true);

            Assert.Equal(SayStepKind.Idle, step.Kind);
            Assert.False(step.HasWork);
            Assert.Null(step.Line);
            Assert.False(queue.IsSpeaking);
        }

        [Fact]
        public void The_head_starts_when_the_game_is_free_and_becomes_the_speaking_line()
        {
            var queue = new SayQueue();
            Assert.True(queue.TryEnqueue(Line(1)));
            Assert.True(queue.TryEnqueue(Line(2)));

            var step = queue.Next(0d, canStartNow: true);

            Assert.Equal(SayStepKind.Start, step.Kind);
            Assert.Equal(1, step.Line!.Id);
            Assert.True(queue.IsSpeaking);
            Assert.Equal(1, queue.WaitingCount);
        }

        [Fact]
        public void Only_one_line_speaks_at_a_time()
        {
            var queue = new SayQueue();
            queue.TryEnqueue(Line(1));
            queue.TryEnqueue(Line(2));

            queue.Next(0d, canStartNow: true);
            var second = queue.Next(1d, canStartNow: true);

            Assert.Equal(SayStepKind.Idle, second.Kind);
            Assert.Equal(1, queue.Active!.Id);
        }

        [Fact]
        public void The_next_line_starts_once_the_games_end_event_names_the_speaking_group()
        {
            var queue = new SayQueue();
            queue.TryEnqueue(Line(1));
            queue.TryEnqueue(Line(2));
            queue.Next(0d, canStartNow: true);

            Assert.False(queue.Complete("motion_scenario_001", out var other));
            Assert.Null(other);

            Assert.True(queue.Complete("cwy_say_1", out var finished));
            Assert.Equal(1, finished!.Id);
            Assert.False(queue.IsSpeaking);

            Assert.Equal(SayStepKind.Start, queue.Next(2d, canStartNow: true).Kind);
            Assert.Equal(2, queue.Active!.Id);
        }

        [Fact]
        public void A_busy_game_makes_the_head_wait_until_the_queues_patience_runs_out()
        {
            var queue = new SayQueue(maxWaitSeconds: 10d);
            queue.TryEnqueue(Line(1, queuedAt: 100d));

            Assert.Equal(SayStepKind.Idle, queue.Next(105d, canStartNow: false).Kind);

            var fallback = queue.Next(110d, canStartNow: false);

            Assert.Equal(SayStepKind.Fallback, fallback.Kind);
            Assert.Equal(1, fallback.Line!.Id);
            Assert.False(queue.IsSpeaking);
            Assert.Equal(0, queue.WaitingCount);
        }

        [Fact]
        public void With_the_fallback_switched_off_a_line_waits_for_the_scenario_path_forever()
        {
            var queue = new SayQueue(maxWaitSeconds: 10d, fallbackWhenBusy: false);
            queue.TryEnqueue(Line(1, queuedAt: 0d));

            Assert.Equal(SayStepKind.Idle, queue.Next(600d, canStartNow: false).Kind);
            Assert.Equal(1, queue.WaitingCount);

            Assert.Equal(SayStepKind.Start, queue.Next(601d, canStartNow: true).Kind);
        }

        [Fact]
        public void A_full_queue_refuses_the_newest_line_rather_than_dropping_the_oldest()
        {
            var queue = new SayQueue(capacity: 2);

            Assert.True(queue.TryEnqueue(Line(1)));
            Assert.True(queue.TryEnqueue(Line(2)));
            Assert.False(queue.TryEnqueue(Line(3)));

            Assert.Equal(2, queue.WaitingCount);
            Assert.Equal(2L, queue.Accepted);
            Assert.Equal(1L, queue.RefusedFull);
            Assert.Equal(1, queue.Snapshot()[0].Id);
        }

        [Fact]
        public void A_waiting_line_can_be_cancelled_and_a_speaking_one_cannot()
        {
            var queue = new SayQueue();
            queue.TryEnqueue(Line(1));
            queue.TryEnqueue(Line(2));
            queue.Next(0d, canStartNow: true);

            Assert.False(queue.Cancel(1));
            Assert.True(queue.Cancel(2));
            Assert.False(queue.Cancel(2));
            Assert.Equal(0, queue.WaitingCount);
        }

        [Fact]
        public void A_speaking_line_the_game_never_ends_is_abandoned_so_the_queue_keeps_moving()
        {
            var queue = new SayQueue(activeTimeoutSeconds: 30d);
            queue.TryEnqueue(Line(1));
            queue.TryEnqueue(Line(2));
            queue.Next(0d, canStartNow: true);

            Assert.Equal(SayStepKind.Idle, queue.Next(29d, canStartNow: true).Kind);

            var abandoned = queue.Next(31d, canStartNow: true);

            Assert.Equal(SayStepKind.Abandon, abandoned.Kind);
            Assert.Equal(1, abandoned.Line!.Id);
            Assert.False(queue.IsSpeaking);
            Assert.Equal(SayStepKind.Start, queue.Next(32d, canStartNow: true).Kind);
        }

        [Fact]
        public void Clearing_hands_back_the_speaking_line_first_then_the_waiting_ones()
        {
            var queue = new SayQueue();
            queue.TryEnqueue(Line(1));
            queue.TryEnqueue(Line(2));
            queue.TryEnqueue(Line(3));
            queue.Next(0d, canStartNow: true);

            var dropped = queue.Clear();

            Assert.Equal(3, dropped.Count);
            Assert.Equal(1, dropped[0].Id);
            Assert.Equal(2, dropped[1].Id);
            Assert.Equal(0, queue.WaitingCount);
            Assert.False(queue.IsSpeaking);
        }

        [Fact]
        public void The_constructor_clamps_nonsense_settings_instead_of_throwing()
        {
            var queue = new SayQueue(capacity: 0, maxWaitSeconds: -5d, activeTimeoutSeconds: 0d);

            Assert.Equal(1, queue.Capacity);
            Assert.Equal(0d, queue.MaxWaitSeconds);
            Assert.Equal(1d, queue.ActiveTimeoutSeconds);
        }
    }
}
