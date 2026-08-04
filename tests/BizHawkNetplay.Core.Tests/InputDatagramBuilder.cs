using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Builds an input datagram from a window of (frame, payload) pairs, for tests that need to put a
/// specific window on the wire.
///
/// This used to be <c>InputPacketCodec.EncodeInput</c>, and it lived in the shipping codec despite
/// nothing shipping calling it: <see cref="InputPacketCodec.BeginInputDatagram"/> is what the
/// driver uses, because the sender keeps its payloads in one contiguous ring and gathering them
/// into a list of arrays just to copy them back out is work with no purpose.
///
/// Two encoders for one wire format is worse than redundant when the tests exercise the one that
/// is not shipped — a header change in the live encoder alone would have gone unnoticed here. So
/// this stays a convenience for tests and is built ON the real encoder rather than beside it: the
/// header comes from the code that ships, and only the payload gather is test-shaped.
/// </summary>
internal static class InputDatagramBuilder
{
    public static byte[] EncodeInput(
        this InputPacketCodec codec, byte port, IReadOnlyList<KeyValuePair<int, byte[]>> window)
    {
        if (codec == null) throw new ArgumentNullException(nameof(codec));
        if (window == null) throw new ArgumentNullException(nameof(window));
        if (window.Count == 0) throw new ArgumentException("Empty window", nameof(window));

        int payloadSize = codec.PayloadSizeFor(port);
        var datagram = codec.BeginInputDatagram(port, window[0].Key, window.Count, out int offset);
        for (int i = 0; i < window.Count; i++)
        {
            var frame = window[i];
            if (frame.Key != window[0].Key + i)
                throw new ArgumentException($"Window not contiguous at index {i} (frame {frame.Key})");
            if (frame.Value.Length != payloadSize)
                throw new ArgumentException(
                    $"Payload size {frame.Value.Length} != expected {payloadSize} for port {port}");
            Buffer.BlockCopy(frame.Value, 0, datagram, offset + i * payloadSize, payloadSize);
        }
        return datagram;
    }
}
