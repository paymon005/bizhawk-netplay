using System.Collections.Generic;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;

namespace BizHawkNetplay.Core.Tests.Fakes;

/// <summary>
/// A transport whose inbound side is a queue the test fills, and whose outbound side goes nowhere.
///
/// Datagrams are handed to the driver's own codec rather than to a test-only injection point, so
/// what arrives has been through the same encode/decode/window path a real peer's input takes —
/// including the accept-window and lead checks that decide whether a frame is even considered. A
/// harness that reached past those would prove the rollback machinery works on frames the driver
/// would have dropped.
/// </summary>
public sealed class QueueTransport : ITransport
{
    private readonly Queue<byte[]> _inbound = new();

    public void Enqueue(byte[] datagram) => _inbound.Enqueue(datagram);

    public void Send(byte[] datagram) { }

    public bool TryReceive(out byte[] datagram)
    {
        if (_inbound.Count == 0) { datagram = null!; return false; }
        datagram = _inbound.Dequeue();
        return true;
    }

    /// <summary>One frame of input for a remote port, encoded exactly as that peer would send it.</summary>
    public static byte[] Frame(FrameDriver driver, byte port, int frame, byte value)
    {
        int size = driver.Codec.PayloadSizeFor(1);
        var payload = new byte[size];
        if (size > 0) payload[0] = value;
        return driver.Codec.EncodeInput(port,
            new List<KeyValuePair<int, byte[]>> { new(frame, payload) });
    }
}
