using System;
using System.Collections.Generic;
using System.IO;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The integrity tag lives under the same per-frame deadline as the body.
///
/// Appending bytes after a timed read is how the KI-2 progress bound gets quietly reintroduced as
/// a hang: the body arrives inside its budget, the timeout is restored, and the tag read then
/// waits forever on a peer that has stopped. A zero-length authenticated frame is the same hazard
/// with no body to hide behind.
/// </summary>
public class ControlChannelMacDeadlineTests
{
    /// <summary>Delivers exactly what it is given, then behaves like a socket with no data: it
    /// times out if a timeout is set, and refuses to pretend an unbounded wait is acceptable.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly Queue<byte> _readable = new();
        private int _readTimeout = System.Threading.Timeout.Infinite;
        public readonly List<int> TimeoutsSeenPerRead = new();

        public void Deliver(byte[] bytes) { foreach (byte b in bytes) _readable.Enqueue(b); }

        public override bool CanTimeout => true;
        public override int ReadTimeout { get => _readTimeout; set => _readTimeout = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            TimeoutsSeenPerRead.Add(_readTimeout);
            if (_readable.Count == 0)
            {
                if (_readTimeout > 0)
                    throw new IOException("read timed out (simulated socket receive timeout)");
                throw new InvalidOperationException(
                    "unbounded read on a stalled stream — this receive would hang forever");
            }
            int n = Math.Min(count, _readable.Count);
            for (int i = 0; i < n; i++) buffer[offset + i] = _readable.Dequeue();
            return n;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private static byte[] Key()
    {
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + 1);
        return key;
    }

    private static byte[] Header(ControlMessageType type, int length) =>
        [(byte)type, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length];

    [Fact]
    public void ABodyWithoutItsTagTimesOutInsteadOfHanging()
    {
        var stream = new StallingStream();
        var channel = new ControlChannel(stream) { BodyReadTimeoutMs = _ => 50 };
        channel.EnableIntegrity(Key(), isHost: false);

        // A complete body, then silence where the tag should be — the stall this test exists for.
        stream.Deliver(Header(ControlMessageType.Ping, 8));
        stream.Deliver(new byte[8]);

        Assert.Throws<IOException>(() => channel.Receive());
    }

    [Fact]
    public void AZeroLengthFrameWithoutItsTagTimesOutToo()
    {
        // No body at all, so nothing but the tag is outstanding — the case a body-only deadline
        // cannot cover even in principle.
        var stream = new StallingStream();
        var channel = new ControlChannel(stream) { BodyReadTimeoutMs = _ => 50 };
        channel.EnableIntegrity(Key(), isHost: false);
        stream.Deliver(Header(ControlMessageType.Bye, 0));

        Assert.Throws<IOException>(() => channel.Receive());
    }

    [Fact]
    public void TheTagIsReadUnderTheMappedTimeout_NotTheRestoredOne()
    {
        var stream = new StallingStream();
        var channel = new ControlChannel(stream) { BodyReadTimeoutMs = _ => 1234 };
        channel.EnableIntegrity(Key(), isHost: false);

        // Frame the sender's way, so the receive succeeds and we can inspect what each read saw.
        var writerSide = new StallingStream();
        var writer = new ControlChannel(writerSide);
        writer.EnableIntegrity(Key(), isHost: true);
        writer.Send(ControlMessageType.Ping, new byte[] { 1, 2, 3 });
        // StallingStream.Write discards; rebuild the wire bytes by hand from the same primitives.
        var body = new byte[] { 1, 2, 3 };
        stream.Deliver(Header(ControlMessageType.Ping, body.Length));
        stream.Deliver(body);
        stream.Deliver(MacOf(Key(), isHost: true, sequence: 0, type: ControlMessageType.Ping, body));

        var (type, received) = channel.Receive();
        Assert.Equal(ControlMessageType.Ping, type);
        Assert.Equal(body, received);

        // The header read is unbounded (an idle channel may wait minutes); every read after it —
        // body and tag alike — carries the mapped budget.
        Assert.Equal(System.Threading.Timeout.Infinite, stream.TimeoutsSeenPerRead[0]);
        for (int i = 1; i < stream.TimeoutsSeenPerRead.Count; i++)
            Assert.Equal(1234, stream.TimeoutsSeenPerRead[i]);
    }

    /// <summary>The tag the channel would produce, computed the same way it does.</summary>
    private static byte[] MacOf(byte[] key, bool isHost, long sequence, ControlMessageType type, byte[] body)
    {
        var preamble = new byte[14];
        preamble[0] = isHost ? (byte)1 : (byte)2;
        for (int i = 0; i < 8; i++) preamble[1 + i] = (byte)(sequence >> (56 - 8 * i));
        preamble[9] = (byte)type;
        int len = body.Length;
        preamble[10] = (byte)(len >> 24); preamble[11] = (byte)(len >> 16);
        preamble[12] = (byte)(len >> 8); preamble[13] = (byte)len;

        using var hmac = new System.Security.Cryptography.HMACSHA256(key);
        hmac.TransformBlock(preamble, 0, preamble.Length, null, 0);
        hmac.TransformFinalBlock(body, 0, body.Length);
        var tag = new byte[ControlChannel.MacBytes];
        Buffer.BlockCopy(hmac.Hash!, 0, tag, 0, tag.Length);
        return tag;
    }
}
