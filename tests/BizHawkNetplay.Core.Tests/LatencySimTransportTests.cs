using System.Collections.Generic;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

public class LatencySimTransportTests
{
    /// <summary>An inbound-only fake: Deliver() simulates a datagram arriving from the wire.</summary>
    private sealed class QueueTransport : ITransport
    {
        private readonly Queue<byte[]> _in = new();
        public void Deliver(byte[] d) => _in.Enqueue(d);
        public void Send(byte[] datagram) { /* outbound not exercised here */ }
        public bool TryReceive(out byte[] datagram)
        {
            if (_in.Count > 0) { datagram = _in.Dequeue(); return true; }
            datagram = null!;
            return false;
        }
    }

    [Fact]
    public void HoldsDatagramsForTheConfiguredDelay()
    {
        long now = 0;
        var inner = new QueueTransport();
        var sim = new LatencySimTransport(inner, delayMs: 50, () => now);

        inner.Deliver(new byte[] { 7 });        // "arrives" at now=0 -> deliverable at 50
        Assert.False(sim.TryReceive(out _));     // buffered, not yet due
        now = 49;
        Assert.False(sim.TryReceive(out _));
        now = 50;
        Assert.True(sim.TryReceive(out var d));  // exactly due now
        Assert.Equal(new byte[] { 7 }, d);
        Assert.False(sim.TryReceive(out _));     // nothing left
    }

    [Fact]
    public void PreservesOrderWhenPolledPromptly()
    {
        // The delay is measured from when TryReceive first observes a datagram; the real driver
        // polls every couple of ms, so draining promptly (as here) reflects true arrival times.
        long now = 0;
        var inner = new QueueTransport();
        var sim = new LatencySimTransport(inner, delayMs: 20, () => now);

        inner.Deliver(new byte[] { 1 });         // observed at now=0 -> due at 20
        Assert.False(sim.TryReceive(out _));
        now = 10;
        inner.Deliver(new byte[] { 2 });         // observed at now=10 -> due at 30
        Assert.False(sim.TryReceive(out _));

        now = 20;
        Assert.True(sim.TryReceive(out var first));
        Assert.Equal(new byte[] { 1 }, first);
        Assert.False(sim.TryReceive(out _));      // {2} not due yet

        now = 30;
        Assert.True(sim.TryReceive(out var second));
        Assert.Equal(new byte[] { 2 }, second);
        Assert.False(sim.TryReceive(out _));
    }
}
