using System;
using System.Collections.Generic;
using System.IO;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The control channel's per-frame progress bound (KI-2): an idle channel may be silent for
/// minutes (a joiner waiting out the host's lobby), but once a frame's header has arrived its
/// body must keep flowing — a peer that dies mid-transfer must fail the receive, not hang it.
/// </summary>
public class ControlChannelTests
{
    /// <summary>Socket-like stream: hands out queued chunks; when drained, times out (throws
    /// IOException) if a read timeout is set, and loudly refuses to "block forever" otherwise —
    /// which is exactly what a real NetworkStream would do, minus the eternity.</summary>
    private sealed class SocketLikeStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _current;
        private int _offset;
        private int _readTimeout = System.Threading.Timeout.Infinite;

        public readonly List<int> TimeoutsSeenPerRead = new();

        public void Enqueue(byte[] chunk) => _chunks.Enqueue(chunk);

        public override bool CanTimeout => true;
        public override int ReadTimeout
        {
            get => _readTimeout;
            set => _readTimeout = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            TimeoutsSeenPerRead.Add(_readTimeout);
            if (_current == null || _offset >= _current.Length)
            {
                if (_chunks.Count == 0)
                {
                    if (_readTimeout > 0)
                        throw new IOException("read timed out (simulated socket receive timeout)");
                    throw new InvalidOperationException(
                        "unbounded read on a stalled stream — this receive would hang forever");
                }
                _current = _chunks.Dequeue();
                _offset = 0;
            }
            int n = Math.Min(count, _current.Length - _offset);
            Buffer.BlockCopy(_current, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Header(ControlMessageType type, int length)
    {
        var h = new byte[5];
        h[0] = (byte)type;
        h[1] = (byte)(length >> 24); h[2] = (byte)(length >> 16);
        h[3] = (byte)(length >> 8); h[4] = (byte)length;
        return h;
    }

    [Fact]
    public void BodyTimeout_AppliesOnlyToBodyReads_AndIsRestoredAfterTheFrame()
    {
        var stream = new SocketLikeStream();
        stream.Enqueue(Header(ControlMessageType.State, 6));
        stream.Enqueue([1, 2, 3]);
        stream.Enqueue([4, 5, 6]);

        var channel = new ControlChannel(stream) { BodyReadTimeoutMs = len => 1234 };
        var (type, body) = channel.Receive();

        Assert.Equal(ControlMessageType.State, type);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, body);
        // The header wait ran unbounded (the legitimate idle/lobby wait); the body reads ran
        // under the mapped timeout; and the stream is back to unbounded for the next frame.
        Assert.Equal(System.Threading.Timeout.Infinite, stream.TimeoutsSeenPerRead[0]);
        for (int i = 1; i < stream.TimeoutsSeenPerRead.Count; i++)
            Assert.Equal(1234, stream.TimeoutsSeenPerRead[i]);
        Assert.Equal(System.Threading.Timeout.Infinite, stream.ReadTimeout);
    }

    [Fact]
    public void PeerDyingMidTransfer_FailsTheReceive_InsteadOfHangingForever()
    {
        // The KI-2 scenario: the WELCOME/state frame starts arriving, then the host stalls.
        var stream = new SocketLikeStream();
        stream.Enqueue(Header(ControlMessageType.State, 100));
        stream.Enqueue(new byte[40]); // …and nothing more, ever

        var channel = new ControlChannel(stream) { BodyReadTimeoutMs = len => 15000 };
        Assert.Throws<IOException>(() => channel.Receive());
        // Restored even on the failure path — the socket isn't left with a stale timeout.
        Assert.Equal(System.Threading.Timeout.Infinite, stream.ReadTimeout);
    }

    [Fact]
    public void WithoutTheBound_TheSameStallReadsUnbounded()
    {
        // Pre-fix behavior, made loud by the fake: no body timeout means the mid-frame stall
        // is an unbounded read — the join hung until a manual Disconnect.
        var stream = new SocketLikeStream();
        stream.Enqueue(Header(ControlMessageType.State, 100));
        stream.Enqueue(new byte[40]);

        var channel = new ControlChannel(stream);
        Assert.Throws<InvalidOperationException>(() => channel.Receive());
    }

    [Fact]
    public void ZeroLengthFrames_NeverConsultTheMapping()
    {
        var stream = new SocketLikeStream();
        stream.Enqueue(Header(ControlMessageType.Ready, 0));
        var channel = new ControlChannel(stream)
        {
            BodyReadTimeoutMs = _ => throw new InvalidOperationException("must not be called for empty bodies"),
        };
        var (type, body) = channel.Receive();
        Assert.Equal(ControlMessageType.Ready, type);
        Assert.Empty(body);
    }

    [Fact]
    public void StreamsWithoutTimeoutSupport_AreUnaffected()
    {
        // Loopback/test streams (MemoryStream pairs) report CanTimeout=false; the hook must be
        // inert there rather than throwing on ReadTimeout access.
        var frame = new List<byte>(Header(ControlMessageType.Checksum, 3)) { 7, 8, 9 };
        var channel = new ControlChannel(new MemoryStream([.. frame]))
        {
            BodyReadTimeoutMs = len => 5,
        };
        var (type, body) = channel.Receive();
        Assert.Equal(ControlMessageType.Checksum, type);
        Assert.Equal(new byte[] { 7, 8, 9 }, body);
    }

    /// <summary>
    /// A savestate is the one thing on this channel that may be enormous, and also the one thing
    /// nobody may send before the handshake has reached the point of transferring one. Sizing the
    /// allocation off the declared TYPE alone let an anonymous connection ask for 64 MiB, and get
    /// it, for the cost of a five-byte header — repeatable per connection.
    ///
    /// The length is checked at the header, so a refusal never reads or allocates the body. The
    /// authenticated case proves the check passed rather than the frame being read: with no body
    /// behind the header it can only end at the end of the stream.
    /// </summary>
    [Fact]
    public void ASavestateSizedFrameIsRefusedUntilThePeerHasAuthenticated()
    {
        var header = Header(ControlMessageType.State, 1_000_000);

        var anonymous = new ControlChannel(new MemoryStream(header));
        Assert.Throws<InvalidDataException>(() => anonymous.Receive());

        var authenticated = new ControlChannel(new MemoryStream(header)) { Authenticated = true };
        Assert.Throws<EndOfStreamException>(() => authenticated.Receive());
    }

    /// <summary>The small-frame ceiling is the same either way — only a state was ever allowed to
    /// be large, so authenticating must not raise the bar for anything else.</summary>
    [Fact]
    public void AuthenticatingDoesNotRaiseTheCeilingForOrdinaryFrames()
    {
        var header = Header(ControlMessageType.Checksum, 1_000_000);
        var channel = new ControlChannel(new MemoryStream(header)) { Authenticated = true };
        Assert.Throws<InvalidDataException>(() => channel.Receive());
    }

    /// <summary>
    /// Every type that can carry a whole savestate must be able to SEND one.
    ///
    /// StateOffer was added without being added to the large-frame list, so a donor answering the
    /// host's majority request threw on the 256 KiB cap. Its writer thread treats a send failure as
    /// a link fault, and a joiner that loses its host link ends its session — so the one peer whose
    /// state the session had just decided was correct got dropped for having been asked for it.
    /// Majority recovery could not work on any core with a state over 256 KiB, which is all of them.
    ///
    /// The v0.34.0 tests covered the CODEC and never put an offer through a channel, which is
    /// exactly the seam the fault lived in. This drives the send path with a state-sized body.
    /// </summary>
    [Theory]
    [InlineData(ControlMessageType.State)]
    [InlineData(ControlMessageType.Resync)]
    [InlineData(ControlMessageType.StateOffer)]
    public void EveryStateBearingTypeCanCarryOne(ControlMessageType type)
    {
        var sink = new MemoryStream();
        var channel = new ControlChannel(sink) { Authenticated = true };
        var body = new byte[2 * 1024 * 1024];   // ~a deflated N64 state; far over the small cap

        channel.Send(type, body);   // threw ArgumentException for StateOffer

        Assert.Equal(5 + body.Length, sink.Length);

        // And the receiver must accept the length it just wrote, or the fault simply moves.
        sink.Position = 0;
        var reader = new ControlChannel(sink) { Authenticated = true };
        var (readType, readBody) = reader.Receive();
        Assert.Equal(type, readType);
        Assert.Equal(body.Length, readBody.Length);
    }

    /// <summary>
    /// The large cap is still earned rather than assumed: an unauthenticated peer declaring a
    /// state-sized StateOffer is refused at the header, like the other two.
    /// </summary>
    [Fact]
    public void AnUnauthenticatedStateOfferIsStillCappedSmall()
    {
        var header = Header(ControlMessageType.StateOffer, 2 * 1024 * 1024);
        var channel = new ControlChannel(new MemoryStream(header));   // not authenticated
        Assert.Throws<InvalidDataException>(() => channel.Receive());
    }

    /// <summary>
    /// The savestate ceiling is granted per DIRECTION, not merely per type.
    ///
    /// A state travels host → joiner and an offer travels joiner → host, so half of every large-frame
    /// permission used to be handed to the side with no business using it: a joiner could declare a
    /// 64 MiB State at its host and the reader allocated it in full before discovering the host does
    /// not even handle that message. Repeatable at will by an admitted peer, and on the 32-bit
    /// BizHawk build that is an out-of-memory rather than churn.
    /// </summary>
    [Theory]
    // (declared type, we are host, may it be that large inbound?)
    [InlineData(ControlMessageType.State, true, false)]        // a joiner sending the host a state
    [InlineData(ControlMessageType.State, false, true)]        // the host sending a joiner a state
    [InlineData(ControlMessageType.Resync, true, false)]
    [InlineData(ControlMessageType.Resync, false, true)]
    [InlineData(ControlMessageType.StateOffer, true, true)]    // the donor answering its host
    [InlineData(ControlMessageType.StateOffer, false, false)]  // a host pushing an offer at a joiner
    public void TheLargeCapIsGrantedOnlyInTheDirectionTheTypeTravels(
        ControlMessageType type, bool weAreHost, bool allowed)
    {
        var key = new byte[32];
        var header = Header(type, 2 * 1024 * 1024);
        var channel = new ControlChannel(new MemoryStream(header)) { Authenticated = true };
        channel.EnableIntegrity(key, isHost: weAreHost);   // declares which end we are

        // Allowed: the length passes and the read then fails for want of a body, which is a
        // different exception and proves the cap was not what stopped it.
        if (allowed) Assert.Throws<EndOfStreamException>(() => channel.Receive());
        else Assert.Throws<InvalidDataException>(() => channel.Receive());
    }

    /// <summary>
    /// A channel that never declared a role keeps the old behaviour, so the direction rule cannot
    /// silently break a caller that authenticates without saying which end it is.
    /// </summary>
    [Fact]
    public void AnUndeclaredRoleFallsBackToAuthenticationAlone()
    {
        var header = Header(ControlMessageType.State, 2 * 1024 * 1024);
        var channel = new ControlChannel(new MemoryStream(header)) { Authenticated = true };
        Assert.Throws<EndOfStreamException>(() => channel.Receive());   // length accepted
    }
}
