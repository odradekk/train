using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Logging;

namespace Train.Tests.Core.Logging;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:logging")]
public sealed class RingBufferLogSinkTests
{
    [TestMethod]
    public void Snapshot_keeps_insertion_order_until_capacity_then_drops_oldest()
    {
        var buffer = new RingBufferLogSink();
        for (var i = 0; i < RingBufferLogSink.Capacity; i++)
            buffer.Emit(Event($"e{i}"));

        var full = buffer.Snapshot();
        Assert.AreEqual(RingBufferLogSink.Capacity, full.Count);
        Assert.AreEqual("e0", full[0].EventId);
        Assert.AreEqual($"e{RingBufferLogSink.Capacity - 1}", full[^1].EventId);

        buffer.Emit(Event("overflow"));
        var after = buffer.Snapshot();
        Assert.AreEqual(RingBufferLogSink.Capacity, after.Count);
        Assert.AreEqual("e1", after[0].EventId);
        Assert.AreEqual("overflow", after[^1].EventId);
        Assert.IsFalse(after.Any(e => e.EventId == "e0"));
    }

    [TestMethod]
    public void Clear_empties_the_buffer_and_later_emits_start_fresh()
    {
        var buffer = new RingBufferLogSink();
        buffer.Emit(Event("before"));
        buffer.Clear();
        Assert.AreEqual(0, buffer.Snapshot().Count);

        buffer.Emit(Event("after"));
        var snapshot = buffer.Snapshot();
        Assert.AreEqual(1, snapshot.Count);
        Assert.AreEqual("after", snapshot[0].EventId);
    }

    [TestMethod]
    public void Snapshot_is_isolated_from_later_emits_and_clear()
    {
        var buffer = new RingBufferLogSink();
        buffer.Emit(Event("kept"));
        var snapshot = buffer.Snapshot();

        buffer.Emit(Event("later"));
        buffer.Clear();

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreEqual("kept", snapshot[0].EventId);
        Assert.AreEqual(0, buffer.Snapshot().Count);
    }

    [TestMethod]
    public void Emit_rejects_null()
    {
        var buffer = new RingBufferLogSink();
        Assert.ThrowsException<ArgumentNullException>(() => buffer.Emit(null!));
    }

    private static LogEvent Event(string eventId) =>
        new(DateTimeOffset.UtcNow, Guid.Empty, 1, LogLevel.Info, "test", eventId, "x", new Dictionary<string, object?>(), null);
}
