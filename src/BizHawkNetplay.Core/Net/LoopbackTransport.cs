using System;
using System.Collections.Concurrent;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// A pair of in-process transports wired back-to-back: what one sends, the other receives.
/// Used by the two-instance integration tests and as the basis for same-machine local play.
/// An optional drop predicate simulates unreliable-UDP packet loss so redundancy can be
/// exercised deterministically.
/// </summary>
public sealed class LoopbackTransport : ITransport
{
    private readonly ConcurrentQueue<byte[]> _inbound;
    private ConcurrentQueue<byte[]> _peerInbound = null!;
    private readonly Func<byte[], bool>? _dropOutgoing;

    private LoopbackTransport(Func<byte[], bool>? dropOutgoing)
    {
        _inbound = new ConcurrentQueue<byte[]>();
        _dropOutgoing = dropOutgoing;
    }

    /// <summary>
    /// Create a connected pair. <paramref name="dropA"/> / <paramref name="dropB"/> may drop
    /// outgoing datagrams from A / B respectively (return true to drop) to model loss.
    /// </summary>
    public static (LoopbackTransport a, LoopbackTransport b) CreatePair(
        Func<byte[], bool>? dropA = null, Func<byte[], bool>? dropB = null)
    {
        var a = new LoopbackTransport(dropA);
        var b = new LoopbackTransport(dropB);
        a._peerInbound = b._inbound;
        b._peerInbound = a._inbound;
        return (a, b);
    }

    public void Send(byte[] datagram)
    {
        if (datagram == null) throw new ArgumentNullException(nameof(datagram));
        if (_dropOutgoing != null && _dropOutgoing(datagram)) return; // simulated loss
        _peerInbound.Enqueue(datagram);
    }

    public bool TryReceive(out byte[] datagram) => _inbound.TryDequeue(out datagram!);
}
