using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <see cref="MakerBackends"/> is process-wide state, so every test class that installs into it belongs to this
    /// collection (the game adapters' tests should join it too).
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class MakerBackendsCollection
    {
        public const string Name = "MakerBackends";
    }

    /// <summary>DS-01: the maker event sink (scalars only, never throws, from any thread) and the session lifecycle that installs it.</summary>
    [Collection(MakerBackendsCollection.Name)]
    public class MakerEventsTests
    {
        [Fact]
        public void Data_keeps_scalars_in_order_and_writes_anything_else_as_null()
        {
            var sink = new MakerEventRing(16, () => new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.FromHours(7)));
            sink.Publish("maker.voice.saved", new Dictionary<string, object?>
            {
                ["packId"] = "thai-dub",
                ["lang"] = "th",
                ["clip"] = "voice_sample_01",
                ["durationSec"] = 2.25,
                ["removed"] = false,
                ["count"] = 3,
                ["single"] = 1.5f,
                ["money"] = 1.25m,
                ["nothing"] = null,
                ["object"] = new { secret = "x" },
                ["list"] = new List<object?> { "a" },
                ["bytes"] = new byte[] { 1, 2, 3 },
            });

            var e = sink.Ring.Read(0, 10).Events.Single();
            Assert.Equal("maker.voice.saved", e.Type);
            Assert.Equal(
                "{\"seq\":1,\"time\":\"2026-11-07T12:00:00.000+07:00\",\"type\":\"maker.voice.saved\",\"data\":{\"packId\":\"thai-dub\",\"lang\":\"th\",\"clip\":\"voice_sample_01\"," +
                "\"durationSec\":2.25,\"removed\":false,\"count\":3,\"single\":1.5,\"money\":1.25,\"nothing\":null,\"object\":null,\"list\":null,\"bytes\":null}}",
                e.Json);
        }

        [Fact]
        public void Publish_never_throws_and_counts_what_it_drops()
        {
            var diagnostics = new List<string>();
            var sink = new MakerEventRing(4, null, d => diagnostics.Add(d));

            sink.Publish(null!, new Dictionary<string, object?>());
            sink.Publish("", new Dictionary<string, object?>());
            sink.Publish(new string('x', 65), new Dictionary<string, object?>());
            sink.Publish("game.language", null!);
            sink.Publish("room.ready", new ThrowingDictionary());

            Assert.Equal(4, sink.Refused);
            Assert.Single(diagnostics);
            Assert.Equal(1L, sink.Ring.Latest);
            Assert.Equal("{}", ApiJson.Write(JsonReader.ParseObject(sink.Ring.Read(0, 10).Events[0].Json)["data"]));

            var throwingDiagnostics = new MakerEventRing(4, null, _ => throw new InvalidOperationException("sink"));
            throwingDiagnostics.Publish("", new Dictionary<string, object?>());
            Assert.Equal(1, throwingDiagnostics.Refused);
        }

        [Fact]
        public void Publishing_from_many_threads_loses_nothing_below_capacity()
        {
            var sink = new MakerEventRing(1024);
            Parallel.For(0, 8, t =>
            {
                for (var i = 0; i < 100; i++)
                {
                    sink.Publish("line.spoken", new Dictionary<string, object?> { ["localizationId"] = "t" + t + "_" + i });
                }
            });

            Assert.Equal(800L, sink.Ring.Latest);
            Assert.Equal(0L, sink.Ring.DroppedTotal);
        }

        [Fact]
        public async Task After_close_publishing_is_ignored_and_waiting_readers_wake()
        {
            var sink = new MakerEventRing(8);
            var reader = Task.Run(() => sink.Ring.Wait(0, 10, TimeSpan.FromSeconds(20)));
            await Task.Delay(50);
            sink.Close();

            await reader.WaitAsync(TimeSpan.FromSeconds(5));
            sink.Publish("room.ready", new Dictionary<string, object?> { ["ready"] = true });
            Assert.Equal(0L, sink.Ring.Latest);
        }

        [Fact]
        public void A_session_installs_its_ring_as_the_event_slot_and_removes_only_its_own()
        {
            var previous = MakerBackends.Events;
            try
            {
                MakerBackends.UseEvents(null);
                var a = new MakerSurface(new MakerRouterOptions { Port = 7870 });
                var b = new MakerSurface(new MakerRouterOptions { Port = 7870 });
                Assert.Null(MakerBackends.Events);
                MakerBackends.Publish("room.ready", new Dictionary<string, object?> { ["ready"] = true }); // no slot: nothing happens

                a.Open();
                Assert.Same(a.Events, MakerBackends.Events);
                Assert.True(a.IsOpen);
                MakerBackends.Publish("game.language", new Dictionary<string, object?> { ["code"] = "th", ["isPack"] = true });
                Assert.Equal(1L, a.Events.Ring.Latest);

                b.Open();
                a.Close();
                Assert.Same(b.Events, MakerBackends.Events);
                Assert.False(a.IsOpen);
                Assert.True(a.Router.IsStopping);

                b.Close();
                b.Close();
                Assert.Null(MakerBackends.Events);
                MakerBackends.Publish("room.ready", new Dictionary<string, object?> { ["ready"] = true });
                Assert.Equal(0L, b.Events.Ring.Latest);
            }
            finally
            {
                MakerBackends.UseEvents(previous);
            }
        }

        [Fact]
        public void A_session_mints_its_own_token_and_shows_only_its_id()
        {
            var a = new MakerSurface(new MakerRouterOptions { Port = 7871 });
            var b = new MakerSurface(new MakerRouterOptions { Port = 7871 });

            Assert.NotEqual(a.TokenId, b.TokenId);
            Assert.Equal(8, a.TokenId.Length);
            Assert.Equal("http://127.0.0.1:7871/maker/", a.Url);
            Assert.Same(a.Events, a.Router.Events);
        }

        [Fact]
        public void A_closed_session_does_not_reopen()
        {
            var previous = MakerBackends.Events;
            try
            {
                MakerBackends.UseEvents(null);
                var surface = new MakerSurface(new MakerRouterOptions { Port = 7870 });
                surface.Close();
                surface.Open();

                Assert.Null(MakerBackends.Events);
                Assert.False(surface.IsOpen);
            }
            finally
            {
                MakerBackends.UseEvents(previous);
            }
        }

        [Fact]
        public void The_router_defaults_read_the_shared_slots()
        {
            var options = new MakerRouterOptions();
            var previousCatalog = MakerBackends.Catalog;
            var previousEditor = MakerBackends.Editor;
            try
            {
                var fake = new FakeMakerBackend();
                MakerBackends.UseCatalog(fake);
                MakerBackends.UseEditor(null);
                Assert.Same(fake, options.Catalog());
                Assert.Null(options.Editor());
            }
            finally
            {
                MakerBackends.UseCatalog(previousCatalog);
                MakerBackends.UseEditor(previousEditor);
            }
        }

        private sealed class ThrowingDictionary : IReadOnlyDictionary<string, object?>
        {
            public object? this[string key] => throw new InvalidOperationException("boom");

            public IEnumerable<string> Keys => throw new InvalidOperationException("boom");

            public IEnumerable<object?> Values => throw new InvalidOperationException("boom");

            public int Count => 1;

            public bool ContainsKey(string key) => false;

            public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException("boom");

            public bool TryGetValue(string key, out object? value) => throw new InvalidOperationException("boom");

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
