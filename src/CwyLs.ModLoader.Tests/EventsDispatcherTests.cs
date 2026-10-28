using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Events;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class EventsDispatcherTests
    {
        private static SceneChanged Sample => new SceneChanged("RoomScene", true);

        [Fact]
        public void A_throwing_subscriber_does_not_stop_the_others()
        {
            var failures = new List<EventDispatchFailure>();
            var dispatcher = new EventDispatcher<SceneChanged>("SceneChanged", failures.Add);

            var seen = new List<string>();
            dispatcher.Subscribe(_ => seen.Add("first"));
            dispatcher.Subscribe(_ => throw new InvalidOperationException("mod bug"));
            dispatcher.Subscribe(_ => seen.Add("third"));
            dispatcher.Subscribe(_ => seen.Add("fourth"));

            // The whole point: publishing from inside the game's call stack must not propagate.
            dispatcher.Publish(Sample);

            Assert.Equal(new[] { "first", "third", "fourth" }, seen);
            Assert.Equal(1L, dispatcher.FailureCount);
            Assert.Equal(1L, dispatcher.PublishCount);

            var failure = Assert.Single(failures);
            Assert.Equal("SceneChanged", failure.EventName);
            Assert.IsType<InvalidOperationException>(failure.Error);
            Assert.Contains("mod bug", failure.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_null_error_sink_still_isolates_the_failure()
        {
            var dispatcher = new EventDispatcher<SceneChanged>("SceneChanged");
            var reached = false;

            dispatcher.Subscribe(_ => throw new Exception("boom"));
            dispatcher.Subscribe(_ => reached = true);

            dispatcher.Publish(Sample);

            Assert.True(reached);
            Assert.Equal(1L, dispatcher.FailureCount);
        }

        [Fact]
        public void A_throwing_error_sink_is_swallowed_rather_than_rethrown()
        {
            var dispatcher = new EventDispatcher<SceneChanged>(
                "SceneChanged", _ => throw new Exception("the sink is broken too"));

            dispatcher.Subscribe(_ => throw new Exception("mod bug"));

            dispatcher.Publish(Sample);

            Assert.Equal(1L, dispatcher.FailureCount);
        }

        [Fact]
        public void Disposing_the_token_unsubscribes_and_is_idempotent()
        {
            var dispatcher = new EventDispatcher<SceneChanged>("SceneChanged");
            var calls = 0;

            var token = dispatcher.Subscribe(_ => calls++);
            dispatcher.Publish(Sample);
            Assert.Equal(1, calls);
            Assert.Equal(1, dispatcher.SubscriberCount);

            token.Dispose();
            token.Dispose();

            dispatcher.Publish(Sample);
            Assert.Equal(1, calls);
            Assert.Equal(0, dispatcher.SubscriberCount);
        }

        [Fact]
        public void Unsubscribe_removes_one_registration_and_reports_whether_it_found_one()
        {
            var dispatcher = new EventDispatcher<SceneChanged>("SceneChanged");
            var calls = 0;
            Action<SceneChanged> handler = _ => calls++;

            // Subscribing twice means being called twice; each removal takes one registration.
            dispatcher.Subscribe(handler);
            dispatcher.Subscribe(handler);
            dispatcher.Publish(Sample);
            Assert.Equal(2, calls);

            Assert.True(dispatcher.Unsubscribe(handler));
            dispatcher.Publish(Sample);
            Assert.Equal(3, calls);

            Assert.True(dispatcher.Unsubscribe(handler));
            Assert.False(dispatcher.Unsubscribe(handler));
            dispatcher.Publish(Sample);
            Assert.Equal(3, calls);
        }

        [Fact]
        public void Unsubscribing_from_inside_a_callback_does_not_disturb_the_publish_in_progress()
        {
            var dispatcher = new EventDispatcher<SceneChanged>("SceneChanged");
            var seen = new List<string>();

            IDisposable? token = null;
            token = dispatcher.Subscribe(
                _ =>
                {
                    seen.Add("first");
                    token!.Dispose();
                });
            dispatcher.Subscribe(_ => seen.Add("second"));

            dispatcher.Publish(Sample);
            Assert.Equal(new[] { "first", "second" }, seen);

            dispatcher.Publish(Sample);
            Assert.Equal(new[] { "first", "second", "second" }, seen);
        }

        [Fact]
        public void Subscribe_and_unsubscribe_reject_a_null_handler()
        {
            var dispatcher = new EventDispatcher<SceneChanged>("SceneChanged");

            Assert.Throws<ArgumentNullException>(() => dispatcher.Subscribe(null!));
            Assert.Throws<ArgumentNullException>(() => dispatcher.Unsubscribe(null!));
        }

        [Fact]
        public void A_channel_needs_a_name()
        {
            Assert.Throws<ArgumentException>(() => new EventDispatcher<SceneChanged>(" "));
            Assert.Equal("SceneChanged", new EventDispatcher<SceneChanged>("SceneChanged").Name);
        }

        [Fact]
        public void A_failing_subscriber_is_named_by_its_declaring_type_and_method()
        {
            var failures = new List<EventDispatchFailure>();
            var dispatcher = new EventDispatcher<SceneChanged>("SceneChanged", failures.Add);

            dispatcher.Subscribe(ThrowingSubscriber);
            dispatcher.Publish(Sample);

            var failure = Assert.Single(failures);
            Assert.Contains(nameof(ThrowingSubscriber), failure.Subscriber, StringComparison.Ordinal);
            Assert.Contains(nameof(EventsDispatcherTests), failure.Subscriber, StringComparison.Ordinal);
        }

        [Fact]
        public void Clear_removes_every_subscriber()
        {
            var dispatcher = new EventDispatcher<SceneChanged>("SceneChanged");
            var calls = 0;

            dispatcher.Subscribe(_ => calls++);
            dispatcher.Subscribe(_ => calls++);
            dispatcher.Clear();

            dispatcher.Publish(Sample);

            Assert.Equal(0, calls);
            Assert.Equal(0, dispatcher.SubscriberCount);
        }

        [Fact]
        public void A_dispatch_failure_needs_a_name_a_subscriber_and_an_error()
        {
            Assert.Throws<ArgumentException>(
                () => new EventDispatchFailure(" ", "Mod.Handler", new Exception()));
            Assert.Throws<ArgumentException>(
                () => new EventDispatchFailure("SceneChanged", string.Empty, new Exception()));
            Assert.Throws<ArgumentNullException>(
                () => new EventDispatchFailure("SceneChanged", "Mod.Handler", null!));
        }

        private static void ThrowingSubscriber(SceneChanged args) => throw new Exception("from a named method");
    }
}
