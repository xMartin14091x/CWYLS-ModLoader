using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Bus;
using Xunit;

[Collection("TraceBus")]
public class BusTraceBusTests
{
    [Fact]
    public void Buffers_before_attach_and_replays_in_order()
    {
        TraceBus.Reset();
        TraceBus.EmitFields("t", "a", ("n", 1));
        TraceBus.EmitFields("t", "b", ("n", 2));
        var seen = new List<string>();
        using (TraceBus.Attach(e => seen.Add(e.Name + e.Payload["n"])))
        {
            TraceBus.EmitFields("t", "c", ("n", 3));
        }
        Assert.Equal(new[] { "a1", "b2", "c3" }, seen);
        Assert.Equal(3, TraceBus.Emitted);
        Assert.Equal(0, TraceBus.Dropped);
    }

    [Fact]
    public void Overflow_drops_oldest_and_counts()
    {
        TraceBus.Reset();
        for (var i = 0; i < TraceBus.BufferCapacity + 5; i++) TraceBus.EmitFields("t", "e", ("i", i));
        Assert.Equal(5, TraceBus.Dropped);
        Assert.Equal(TraceBus.BufferCapacity, TraceBus.Buffered);
        var first = -1;
        using (TraceBus.Attach(e => { if (first < 0) first = (int)e.Payload["i"]!; })) { }
        Assert.Equal(5, first);
    }

    [Fact]
    public void Consumer_exception_is_reported_not_thrown()
    {
        TraceBus.Reset();
        Exception? reported = null;
        TraceBus.SetErrorSink(ex => reported = ex);
        using (TraceBus.Attach(_ => throw new InvalidOperationException("boom")))
        {
            TraceBus.Emit("t", "x");
        }
        Assert.NotNull(reported);
        Assert.Equal("boom", reported!.Message);
        TraceBus.SetErrorSink(null);
    }

    [Fact]
    public void Detach_buffers_again_and_null_payload_is_empty()
    {
        TraceBus.Reset();
        var d = TraceBus.Attach(_ => { });
        d.Dispose();
        TraceBus.Emit("t", "later", (IReadOnlyDictionary<string, object?>?)null);
        Assert.Equal(1, TraceBus.Buffered);
        BusEvent? got = null;
        using (TraceBus.Attach(e => got = e)) { }
        Assert.NotNull(got);
        Assert.Empty(got!.Value.Payload);
        Assert.False(TraceBus.HasConsumer);
    }

    // ---- F3: the second kind of reader ---------------------------------------------------------

    /// <summary>
    /// The console sink subscribes; the trace file attaches. Both must see every event, and the
    /// console's copy must be delivered on the publishing thread so the order of the console is
    /// the order of the game.
    /// </summary>
    [Fact]
    public void An_observer_and_the_consumer_both_see_every_event()
    {
        TraceBus.Reset();
        var consumer = new List<string>();
        var observer = new List<string>();

        using (TraceBus.Attach(e => consumer.Add(e.Name)))
        using (TraceBus.Subscribe(e => observer.Add(e.Name)))
        {
            TraceBus.Emit("t", "a");
            TraceBus.Emit("t", "b");
        }

        Assert.Equal(new[] { "a", "b" }, consumer);
        Assert.Equal(new[] { "a", "b" }, observer);
        TraceBus.Reset();
    }

    /// <summary>
    /// Events published before the reader was ready were replayed with the time they were
    /// replayed at, not the time they happened - a measured 4 631 ms of skew between the two sinks
    /// for one and the same event.
    /// </summary>
    [Fact]
    public void The_replay_carries_the_original_event_time()
    {
        TraceBus.Reset();
        TraceBus.Emit("t", "early");
        var emittedAt = DateTimeOffset.Now;

        System.Threading.Thread.Sleep(20);

        BusEvent? replayed = null;
        using (TraceBus.Subscribe(e => replayed = e))
        {
        }

        Assert.NotNull(replayed);
        Assert.Equal("early", replayed!.Value.Name);
        Assert.True(
            replayed.Value.Time <= emittedAt,
            "the replayed event must keep the time it was published, not the time it was replayed");

        TraceBus.Reset();
    }

    [Fact]
    public void A_late_observer_still_gets_the_buffered_events_exactly_once()
    {
        TraceBus.Reset();
        TraceBus.Emit("t", "a");
        TraceBus.Emit("t", "b");

        var seen = new List<string>();
        using (TraceBus.Subscribe(e => seen.Add(e.Name)))
        {
            TraceBus.Emit("t", "c");
        }

        Assert.Equal(new[] { "a", "b", "c" }, seen);
        TraceBus.Reset();
    }

    [Fact]
    public void Unsubscribing_stops_delivery_and_leaves_the_others_alone()
    {
        TraceBus.Reset();
        var first = new List<string>();
        var second = new List<string>();

        var one = TraceBus.Subscribe(e => first.Add(e.Name));
        using (TraceBus.Subscribe(e => second.Add(e.Name)))
        {
            TraceBus.Emit("t", "a");
            one.Dispose();
            TraceBus.Emit("t", "b");
        }

        Assert.Equal(new[] { "a" }, first);
        Assert.Equal(new[] { "a", "b" }, second);
        Assert.Equal(0, TraceBus.Observers);
        TraceBus.Reset();
    }

    [Fact]
    public void An_observer_that_throws_is_reported_and_never_stops_the_others()
    {
        TraceBus.Reset();
        Exception? reported = null;
        var reached = 0;

        TraceBus.SetErrorSink(ex => reported = ex);
        using (TraceBus.Subscribe(_ => throw new InvalidOperationException("observer boom")))
        using (TraceBus.Subscribe(_ => reached++))
        {
            TraceBus.Emit("t", "x");
        }

        Assert.NotNull(reported);
        Assert.Equal("observer boom", reported!.Message);
        Assert.Equal(1, reached);

        TraceBus.SetErrorSink(null);
        TraceBus.Reset();
    }
}
