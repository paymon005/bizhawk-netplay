using System;
using System.Collections.Generic;
using System.IO;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The control-frame MAC (KI-13's network fix): once the password exchange ends, every frame in
/// both directions is authenticated, position-bound and direction-bound. These tests drive the
/// framing directly, with a tap in the middle standing in for the on-path attacker the mechanism
/// exists to refuse; the handshake suite exercises the same MAC end-to-end over real sockets,
/// since VerifyPassword now enables it inside every session that reaches AUTH.
/// </summary>
public class ControlChannelIntegrityTests
{
    /// <summary>One direction of a link, with the attacker's hands on it: everything one channel
    /// sends lands in a buffer the test may deliver intact, tampered, duplicated, or not at all.</summary>
    private sealed class TappedLink : Stream
    {
        private readonly List<byte> _readable = new();
        public readonly List<byte[]> SentFrames = new(); // one entry per Write burst is not needed; frames are reassembled by the test
        private readonly List<byte> _written = new();

        public byte[] DrainWritten()
        {
            var all = _written.ToArray();
            _written.Clear();
            return all;
        }

        public void Deliver(byte[] bytes) => _readable.AddRange(bytes);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_readable.Count == 0)
                throw new InvalidOperationException("read past everything delivered");
            int n = Math.Min(count, _readable.Count);
            _readable.CopyTo(0, buffer, offset, n);
            _readable.RemoveRange(0, n);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) _written.Add(buffer[offset + i]);
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static byte[] Key(byte fill)
    {
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = fill;
        return key;
    }

    private static (ControlChannel sender, ControlChannel receiver, TappedLink senderSide, TappedLink receiverSide)
        HostToJoiner(byte[] key)
    {
        var senderSide = new TappedLink();
        var receiverSide = new TappedLink();
        var sender = new ControlChannel(senderSide);
        var receiver = new ControlChannel(receiverSide);
        sender.EnableIntegrity(key, isHost: true);
        receiver.EnableIntegrity(key, isHost: false);
        return (sender, receiver, senderSide, receiverSide);
    }

    [Fact]
    public void AuthenticatedFramesRoundTripInOrder()
    {
        var (sender, receiver, senderSide, receiverSide) = HostToJoiner(Key(0x11));
        sender.Send(ControlMessageType.Ping, new byte[] { 1, 2, 3 });
        sender.Send(ControlMessageType.Checksum, new byte[] { 9, 8, 7, 6 });
        receiverSide.Deliver(senderSide.DrainWritten());

        var (t1, b1) = receiver.Receive();
        var (t2, b2) = receiver.Receive();
        Assert.Equal(ControlMessageType.Ping, t1);
        Assert.Equal(new byte[] { 1, 2, 3 }, b1);
        Assert.Equal(ControlMessageType.Checksum, t2);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, b2);
    }

    [Fact]
    public void ATamperedBodyIsRefused()
    {
        var (sender, receiver, senderSide, receiverSide) = HostToJoiner(Key(0x22));
        sender.Send(ControlMessageType.Resync, new byte[] { 10, 20, 30 });
        var wire = senderSide.DrainWritten();
        wire[6] ^= 0x01; // one bit of the body — the Resync payload KI-13 is about
        receiverSide.Deliver(wire);

        Assert.Throws<InvalidDataException>(() => receiver.Receive());
    }

    [Fact]
    public void AReplayedFrameIsRefused_BySequence()
    {
        var (sender, receiver, senderSide, receiverSide) = HostToJoiner(Key(0x33));
        sender.Send(ControlMessageType.Ping, new byte[] { 5 });
        var frame = senderSide.DrainWritten();
        receiverSide.Deliver(frame);
        receiver.Receive(); // the genuine copy
        receiverSide.Deliver(frame); // the attacker's replay: identical bytes, wrong position

        Assert.Throws<InvalidDataException>(() => receiver.Receive());
    }

    [Fact]
    public void ADeletedFrameIsRefused_TheNextOneFails()
    {
        var (sender, receiver, senderSide, receiverSide) = HostToJoiner(Key(0x44));
        sender.Send(ControlMessageType.Ping, new byte[] { 1 });
        var first = senderSide.DrainWritten();
        sender.Send(ControlMessageType.Ping, new byte[] { 2 });
        var second = senderSide.DrainWritten();
        _ = first; // the attacker drops the first frame entirely
        receiverSide.Deliver(second);

        Assert.Throws<InvalidDataException>(() => receiver.Receive());
    }

    [Fact]
    public void AFrameReflectedBackAtItsSenderIsRefused_ByDirection()
    {
        var key = Key(0x55);
        var side = new TappedLink();
        var host = new ControlChannel(side);
        host.EnableIntegrity(key, isHost: true);
        host.Send(ControlMessageType.Ping, new byte[] { 7 });
        // Bounce the host's own frame straight back at it. Same key, same sequence position —
        // only the direction byte in the MAC preimage says whose frame this was.
        side.Deliver(side.DrainWritten());

        Assert.Throws<InvalidDataException>(() => host.Receive());
    }

    [Fact]
    public void TheWrongKeyIsRefused()
    {
        var senderSide = new TappedLink();
        var receiverSide = new TappedLink();
        var sender = new ControlChannel(senderSide);
        var receiver = new ControlChannel(receiverSide);
        sender.EnableIntegrity(Key(0x66), isHost: true);
        receiver.EnableIntegrity(Key(0x77), isHost: false);
        sender.Send(ControlMessageType.Ping, new byte[] { 1 });
        receiverSide.Deliver(senderSide.DrainWritten());

        Assert.Throws<InvalidDataException>(() => receiver.Receive());
    }

    [Fact]
    public void FramesBeforeIntegrityEnablesCarryNoMac()
    {
        // The pre-auth phase (challenge, intro, AUTH) cannot be MACed — the key it proves is the
        // key the MAC uses — so a channel without integrity must frame exactly as before.
        var senderSide = new TappedLink();
        var receiverSide = new TappedLink();
        var sender = new ControlChannel(senderSide);
        var receiver = new ControlChannel(receiverSide);
        sender.Send(ControlMessageType.Hello, new byte[] { 42 });
        var wire = senderSide.DrainWritten();
        Assert.Equal(5 + 1, wire.Length); // header + body, not a MAC byte more
        receiverSide.Deliver(wire);
        var (type, body) = receiver.Receive();
        Assert.Equal(ControlMessageType.Hello, type);
        Assert.Equal(new byte[] { 42 }, body);
    }

    [Fact]
    public void MacKeyIsDerivedFromButNeverEqualToTheSessionKey()
    {
        var sessionKey = Key(0x88);
        var mac = SessionAuth.MacKey(sessionKey);
        Assert.Equal(32, mac.Length);
        Assert.NotEqual(sessionKey, mac);
        // Deterministic: both peers derive the same MAC key from the same session key.
        Assert.Equal(mac, SessionAuth.MacKey(Key(0x88)));
    }
}
