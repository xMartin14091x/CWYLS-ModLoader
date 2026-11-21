using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Bus;
using Xunit;

public class BusTraceBusTests
{
    [Fact]
    public void Buffers_before_attach_and_replays_in_order()
    {
        var bus = new TraceBusChannel();
        bus.EmitFields("t", "a", ("n", 1));
        bus.EmitFields("t", "b", ("n", 2));
        var seen = new List<string>();
        using (bus.Attach(e => seen.Add(e.Name + e.Payload["n"])))
        {
            bus.EmitFields("t", "c", ("n", 3));
        }
        Assert.Equal(new[] { "a1", "b2", "c3" }, seen);
        Assert.Equal(3, bus.Emitted);
        Assert.Equal(0, bus.Dropped);
    }

    [Fact]
    public void Overflow_drops_oldest_and_counts()
    {
        var bus = new TraceBusChannel();
        for (var i = 0; i < TraceBus.BufferCapacity + 5; i++) bus.EmitFields("t", "e", ("i", i));
        Assert.Equal(5, bus.Dropped);
        Assert.Equal(TraceBus.BufferCapacity, bus.Buffered);
        var first = -1;
        using (bus.Attach(e => { if (first < 0) first = (int)e.Payload["i"]!; })) { }
        Assert.Equal(5, first);
    }

    [Fact]
    public void Consumer_exception_is_reported_not_thrown()
    {
        var bus = new TraceBusChannel();
        Exception? reported = null;
        bus.SetErrorSink(ex => reported = ex);
        using (bus.Attach(_ => throw new InvalidOperationException("boom")))
        {
            bus.Emit("t", "x");
        }
        Assert.NotNull(reported);
        Assert.Equal("boom", reported!.Message);
    }

    [Fact]
    public void Detach_buffers_again_and_null_payload_is_empty()
    {
        var bus = new TraceBusChannel();
        var d = bus.Attach(_ => { });
        d.Dispose();
        bus.Emit("t", "later", (IReadOnlyDictionary<string, object?>?)null);
        Assert.Equal(1, bus.Buffered);
        BusEvent? got = null;
        using (bus.Attach(e => got = e)) { }
        Assert.NotNull(got);
        Assert.Empty(got!.Value.Payload);
        Assert.False(bus.HasConsumer);
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
        var bus = new TraceBusChannel();
        var consumer = new List<string>();
        var observer = new List<string>();

        using (bus.Attach(e => consumer.Add(e.Name)))
        using (bus.Subscribe(e => observer.Add(e.Name)))
        {
            bus.Emit("t", "a");
            bus.Emit("t", "b");
        }

        Assert.Equal(new[] { "a", "b" }, consumer);
        Assert.Equal(new[] { "a", "b" }, observer);
    }

    /// <summary>
    /// Events published before the reader was ready were replayed with the time they were
    /// replayed at, not the time they happened - a measured 4 631 ms of skew between the two sinks
    /// for one and the same event.
    /// </summary>
    [Fact]
    public void The_replay_carries_the_original_event_time()
    {
        var bus = new TraceBusChannel();
        bus.Emit("t", "early");
        var emittedAt = DateTimeOffset.Now;

        System.Threading.Thread.Sleep(20);

        BusEvent? replayed = null;
        using (bus.Subscribe(e => replayed = e))
        {
        }

        Assert.NotNull(replayed);
        Assert.Equal("early", replayed!.Value.Name);
        Assert.True(
            replayed.Value.Time <= emittedAt,
            "the replayed event must keep the time it was published, not the time it was replayed");
    }

    [Fact]
    public void A_late_observer_still_gets_the_buffered_events_exactly_once()
    {
        var bus = new TraceBusChannel();
        bus.Emit("t", "a");
        bus.Emit("t", "b");

        var seen = new List<string>();
        using (bus.Subscribe(e => seen.Add(e.Name)))
        {
            bus.Emit("t", "c");
        }

        Assert.Equal(new[] { "a", "b", "c" }, seen);
    }

    [Fact]
    public void Unsubscribing_stops_delivery_and_leaves_the_others_alone()
    {
        var bus = new TraceBusChannel();
        var first = new List<string>();
        var second = new List<string>();

        var one = bus.Subscribe(e => first.Add(e.Name));
        using (bus.Subscribe(e => second.Add(e.Name)))
        {
            bus.Emit("t", "a");
            one.Dispose();
            bus.Emit("t", "b");
        }

        Assert.Equal(new[] { "a" }, first);
        Assert.Equal(new[] { "a", "b" }, second);
        Assert.Equal(0, bus.Observers);
    }

    [Fact]
    public void An_observer_that_throws_is_reported_and_never_stops_the_others()
    {
        var bus = new TraceBusChannel();
        Exception? reported = null;
        var reached = 0;

        bus.SetErrorSink(ex => reported = ex);
        using (bus.Subscribe(_ => throw new InvalidOperationException("observer boom")))
        using (bus.Subscribe(_ => reached++))
        {
            bus.Emit("t", "x");
        }

        Assert.NotNull(reported);
        Assert.Equal("observer boom", reported!.Message);
        Assert.Equal(1, reached);
    }
}
