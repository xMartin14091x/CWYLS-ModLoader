using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    [Collection(MakerBackendsCollection.Name)]
    public class MakerShutdownTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void Every_cleanup_is_attempted_even_when_other_cleanups_throw(int failures)
        {
            var calls = new List<int>();
            var errors = new[]
            {
                new InvalidOperationException("detach"),
                new InvalidOperationException("surface"),
                new InvalidOperationException("listener"),
            };
            void Step(int step)
            {
                calls.Add(step);
                if ((failures & (1 << step)) != 0) throw errors[step];
            }

            var error = Record.Exception(() =>
                MakerShutdown.Close(() => Step(0), () => Step(1), () => Step(2)));

            Assert.Equal(new[] { 0, 1, 2 }, calls);
            var lastFailure = (failures & 4) != 0 ? 2 : (failures & 2) != 0 ? 1 : (failures & 1) != 0 ? 0 : -1;
            if (lastFailure < 0) Assert.Null(error);
            else Assert.Same(errors[lastFailure], error);
        }

        [Fact]
        public void Detach_failure_still_closes_the_real_surface_and_owned_listener()
        {
            var previous = MakerBackends.Events;
            var surface = new MakerSurface(new MakerRouterOptions { Port = 7870 });
            var listener = new FakeTransport();
            var failure = new InvalidOperationException("detach");
            try
            {
                surface.Open();
                Assert.Same(surface.Events, MakerBackends.Events);

                var error = Record.Exception(() => MakerShutdown.Close(
                    () => throw failure,
                    surface.Close,
                    listener.Stop));

                Assert.Same(failure, error);
                Assert.False(surface.IsOpen);
                Assert.True(surface.Router.IsStopping);
                Assert.Null(MakerBackends.Events);
                Assert.False(listener.IsRunning);
                Assert.Equal(1, listener.StopCalls);
            }
            finally
            {
                surface.Close();
                MakerBackends.UseEvents(previous);
            }
        }

        private sealed class FakeTransport : IApiTransport
        {
            public string Name => "fake";
            public int Port => 7870;
            public bool IsRunning { get; private set; } = true;
            public int StopCalls { get; private set; }
            public void Start() => IsRunning = true;
            public void Stop()
            {
                StopCalls++;
                IsRunning = false;
            }
            public void Dispose() => Stop();
        }
    }
}
