using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Threading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerDecodedPreviewOwnershipTests
    {
        // One path: the pack and validation scenarios are covered by the adapter harness (PreviewAdapterHarness, run by build.sh).
        [Fact]
        public void Abandoned_decode_is_destroyed_once_on_the_dispatcher_thread()
        {
            var queue = new MainThreadQueue(() => 0);
            queue.CaptureMainThread();
            var main = Thread.CurrentThread.ManagedThreadId;
            var destroyed = new List<object>();
            var clip = new object();
            var owner = new MakerPreviewOwnership<object>(work => Assert.True(queue.Post("maker", "preview-release", work)), value =>
            {
                Assert.Equal(main, Thread.CurrentThread.ManagedThreadId);
                destroyed.Add(value);
            });
            owner.Decoded(clip);
            var worker = new Thread(() => { owner.EndRequest(); owner.EndRequest(); });
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
            Assert.Empty(destroyed);
            queue.Pump(1);
            owner.PlaybackCompleted();
            queue.Pump(1);
            Assert.Same(clip, Assert.Single(destroyed));
        }

        [Fact]
        public void Cancelled_before_start_cannot_play_and_still_releases()
        {
            var released = 0;
            var owner = new MakerPreviewOwnership<object>(work => work(), _ => released++);
            owner.Decoded(new object());
            owner.EndRequest();
            var attempted = false;
            Assert.False(owner.TryStart(() => { attempted = true; return true; }));
            Assert.False(attempted);
            Assert.Equal(1, released);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Refused_or_throwing_playback_does_not_take_request_ownership(bool throws)
        {
            var released = 0;
            var owner = new MakerPreviewOwnership<object>(work => work(), _ => released++);
            owner.Decoded(new object());
            if (throws) Assert.Throws<InvalidOperationException>(() => owner.TryStart(() => throw new InvalidOperationException()));
            else Assert.False(owner.TryStart(() => false));
            owner.EndRequest();
            owner.PlaybackCompleted();
            Assert.Equal(1, released);
        }

        [Fact]
        public void Successful_playback_keeps_clip_until_completion_and_never_double_destroys()
        {
            var released = 0;
            var owner = new MakerPreviewOwnership<object>(work => work(), _ => released++);
            owner.Decoded(new object());
            Assert.True(owner.TryStart(() => true));
            owner.EndRequest();
            Assert.Equal(0, released);
            owner.PlaybackCompleted();
            owner.PlaybackCompleted();
            owner.EndRequest();
            Assert.Equal(1, released);
        }

        [Fact]
        public void Decoder_callback_after_request_timeout_is_reclaimed()
        {
            var released = 0;
            var owner = new MakerPreviewOwnership<object>(work => work(), _ => released++);
            owner.EndRequest();
            owner.Decoded(new object());
            owner.EndRequest();
            Assert.Equal(1, released);
        }

        [Fact]
        public void Borrowed_clip_is_never_destroyed()
        {
            var released = 0;
            var owner = new MakerPreviewOwnership<object>(work => work(), _ => released++);
            Assert.True(owner.TryStart(() => true));
            owner.EndRequest();
            owner.PlaybackCompleted();
            Assert.Equal(0, released);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Request_end_racing_actual_start_preserves_successful_playback_ownership(bool accepted)
        {
            var released = 0;
            var owner = new MakerPreviewOwnership<object>(work => work(), _ => Interlocked.Increment(ref released));
            owner.Decoded(new object());
            using var entered = new ManualResetEventSlim();
            using var finishStart = new ManualResetEventSlim();
            using var ending = new ManualResetEventSlim();
            var started = false;
            var start = new Thread(() => started = owner.TryStart(() =>
            {
                entered.Set();
                return finishStart.Wait(TimeSpan.FromSeconds(2)) && accepted;
            }));
            var end = new Thread(() => { ending.Set(); owner.EndRequest(); });
            start.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            end.Start();
            Assert.True(ending.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, Volatile.Read(ref released));
            finishStart.Set();
            Assert.True(start.Join(TimeSpan.FromSeconds(2)));
            Assert.True(end.Join(TimeSpan.FromSeconds(2)));
            Assert.Equal(accepted, started);
            Assert.Equal(accepted ? 0 : 1, released);
            owner.PlaybackCompleted();
            owner.EndRequest();
            Assert.Equal(1, released);
        }

        [Fact]
        public void Completion_before_request_finally_releases_once()
        {
            var released = 0;
            var owner = new MakerPreviewOwnership<object>(work => work(), _ => released++);
            Assert.True(owner.TryStart(() => { owner.PlaybackCompleted(); return true; }));
            owner.Decoded(new object());
            owner.EndRequest();
            Assert.Equal(1, released);
        }
    }
}
