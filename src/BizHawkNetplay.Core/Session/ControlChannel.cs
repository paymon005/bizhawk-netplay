using System;
using System.IO;
using System.Security.Cryptography;

namespace BizHawkNetplay.Core.Session;

public enum ControlMessageType : byte
{
    Hello = 1,     // identity + preferences, both peers send
    Welcome = 2,   // host's verdict: accepted params + host identity + port assignment
    State = 3,     // initial whole-core savestate (host -> client)
    Checksum = 4,  // (frame, hash) desync-detection sample
    Start = 5,     // synchronized-start marker
    Error = 6,     // rejection with a reason string
    Bye = 7,       // graceful close
    Ping = 8,      // RTT probe: opaque 8-byte token, echoed during lobby setup and live play
    Pong = 9,      // RTT echo: the ping's 8-byte token returned unchanged
    Resync = 10,   // host -> client: [generation][authoritative whole-core state]
    // Reserved, never shipped: nothing encodes or handles it. A joiner reports its checksum and the
    // host decides, so there was never anything for a joiner to ask for. Kept rather than reused —
    // the slot number is protocol surface, and silently giving 11 a new meaning is the one change
    // that could make two builds disagree without either noticing.
    ResyncRequest = 11,
    PeerList = 12, // host -> client: the other peers' UDP endpoints for the direct input mesh
    Candidate = 13, // client -> host: my reflexive (STUN) UDP endpoint, for NAT-traversal candidates
    Ready = 14,    // multi-peer start barrier request/ack after state import data is received
    Go = 15,       // host releases every ready joiner at once
    ResyncBegin = 16, // host -> client: generation, state size, and wait budget for the Resync frame
    Auth = 17,     // session-password challenge-response proof (see SessionAuth)
    // Pacing: [generation:12][sequence:int32][ack:int32][frame:int32][localAdvantage:int32].
    // Sequence/ack makes frame advantage valid only after a two-way sample and edge-triggered.
    Pacing = 18,
    ResyncApplied = 19, // joiner -> host: authoritative state imported/rebuilt for this generation
    ResyncResume = 20,  // host -> joiner: every peer applied this generation; resume stepping
    // Pre-GO mesh measurement. Host -> joiner carries the generation alone ("measure your UDP edges
    // now"); joiner -> host carries the generation plus its worst edge. The host's own control links
    // reach only the joiners, so without this round nobody ever measures a joiner-to-joiner edge.
    MeshRtt = 21,
    InputDelay = 22,    // host -> joiner: the authoritative delay, once every edge has reported
    // Host -> joiner: [generation:12][port:1] — the named seat is permanently empty and the
    // session continues without its player. Sent BEFORE the rebuild that makes it true (the
    // control channel is ordered), so every peer rebuilds with the seat already neutral; the
    // generation is the one current when the vacate was decided, purely to retire stale copies.
    SeatVacated = 23,
    // Joiner -> host: [generation:12][port:1] — "no UDP input is reaching me from this seat".
    // The evidence behind live relay failover: the host cannot see a joiner-to-joiner leg die,
    // and this is the only party that can. The host answers by carrying the named pair over its
    // own legs, exactly as it would have at session start had the leg never opened.
    InputOutage = 24,
    // Joiner -> host, first checksum boundaries of a generation only:
    // [generation:12][frame:4][count:2][count x uint32] — main memory hashed in buckets, so the
    // host can see WHICH ranges disagree rather than only that some byte does. The evidence the
    // learned exclusion mask is built from; see DivergenceLearner.
    DivergenceReport = 25,
    // Host -> joiner: [generation:12][effectiveFrom:4][count:2][bitmap:count/8] — the buckets the
    // desync checksum must skip from the stated frame on, because their contents are produced per
    // machine (a video plugin resolving GPU output back into console RAM) and can never agree.
    ExclusionMask = 26,
    // Host -> joiner: [generation:12] — "your group outvoted me; send me your state, it is going to
    // become the session's." Only ever sent to the donor DesyncPartition.ChooseDonor named, and only
    // when deferring is enabled (on by default since v0.36.0). See MajorityRecovery.
    StateRequest = 27,
    // Joiner -> host: [generation:12][deflated state] — the answer. This is the ONLY message in the
    // protocol that carries a peer's savestate toward the host, and it is why deferring has a
    // switch at all: a savestate is a trusted-input format all the way into the core (KI-13), so
    // accepting one extends to the host an exposure that was once the joiners' alone.
    StateOffer = 28,
}

/// <summary>
/// The reliable control channel (§3.3): length-prefixed framing over any duplex
/// <see cref="Stream"/> (a TCP <c>NetworkStream</c> in production, a paired memory stream in
/// tests). Carries the handshake, initial state transfer, and periodic checksums — everything
/// that must not be lost. The unreliable input hot path stays on <see cref="Net.MeshUdpTransport"/>.
///
/// Frame: <c>[type:1][length:int32 big-endian][body:length]</c>. A hard cap on length keeps a
/// malicious or corrupt peer from driving a huge allocation.
/// </summary>
public sealed class ControlChannel
{
    private const int MaxFrameLength = 64 * 1024 * 1024; // 64 MiB ceiling (states are ~1 MiB)

    /// <summary>
    /// Ceiling for every message type that is NOT a whole savestate.
    ///
    /// The 64MiB cap exists for states, and applying it uniformly meant a Ping could declare 64MiB
    /// and have it allocated before anything looked at the type. Worse for the text bodies: the
    /// handshake decoders split them into lines and build dictionaries two or three separate times,
    /// so a 64MiB WELCOME was several hundred megabytes of transient strings on the joiner. These
    /// bodies are around a kilobyte in practice; refusing at the header costs one comparison and
    /// never allocates.
    /// </summary>
    private const int MaxSmallFrameLength = 256 * 1024;

    /// <summary>
    /// Set once the peer on the other end has proved the session password.
    ///
    /// Until then the savestate ceiling does not apply, whatever a frame declares itself to be. A
    /// state is the one thing on this channel that may be enormous, and it is also the one thing
    /// nobody may send before the handshake has got that far — so an anonymous connection announcing
    /// a 64 MiB State was declaring an allocation it had no standing to ask for, and getting it,
    /// once per connection, for the cost of a five-byte header.
    /// </summary>
    public bool Authenticated { get; set; }

    /// <summary>
    /// Which end of the link this is, once the handshake has said. 0 = not yet declared.
    ///
    /// The savestate ceiling used to turn on for an authenticated peer and stay on in both
    /// directions, which is a weaker rule than its own comment claimed. A state travels host →
    /// joiner and an offer travels joiner → host, so half of every large-frame permission was
    /// granted to the side that has no business using it: a joiner could declare a 64 MiB State at
    /// its host, which the reader allocates in full before discovering it is not even a message the
    /// host handles. Repeatable at will by an admitted peer, and on the 32-bit BizHawk build that
    /// is an out-of-memory rather than merely churn.
    /// </summary>
    private int _role; // 1 = host, 2 = joiner

    /// <summary>
    /// The size ceiling for one frame, in the direction it is travelling.
    ///
    /// Three conditions, and each is load-bearing. The type must be one that may carry a state at
    /// all; the peer must have proved the password, because a state is the one enormous thing on
    /// this channel and nobody may send one before the handshake reaches that point; and this end
    /// must be the end that legitimately sends or receives it.
    ///
    /// All three now read <see cref="ControlMessageRouting"/> rather than predicates kept here by
    /// hand — see that table for what went wrong when they were separate. A role that has not been
    /// declared stays permissive, exactly as before, so a caller that authenticates without saying
    /// which end it is cannot be broken by this.
    /// </summary>
    private int MaxLengthFor(ControlMessageType type, bool sending)
    {
        if (ControlMessageRouting.Size(type) != MessageSize.State || !Authenticated)
            return MaxSmallFrameLength;
        if (_role == 0) return MaxFrameLength;   // undeclared: authentication alone governs
        bool weAreHost = _role == 1;
        bool permitted = sending
            ? ControlMessageRouting.MaySend(type, weAreHost)
            : ControlMessageRouting.Accepts(type, weAreHost);
        return permitted ? MaxFrameLength : MaxSmallFrameLength;
    }

    private readonly Stream _stream;
    private readonly object _writeLock = new();

    // --- per-frame integrity (KI-13) -----------------------------------------------------------
    // After EnableIntegrity, every frame carries a truncated HMAC-SHA256 appended after its body:
    //   mac = HMAC(macKey, direction || sequence:8 || type:1 || length:4 || body)[0..15]
    // The direction byte keeps a frame sent one way from ever verifying the other way (a
    // reflection), and the per-direction sequence — counted from the moment integrity enables,
    // never carried on the wire — makes every frame position-bound: replaying, reordering or
    // deleting a frame desynchronizes the receiver's count and the next verification fails loudly.
    // TCP already refuses reordering; this is for the peer on the PATH, who can do all three.
    //
    // What this does and does not buy, stated honestly: with a session password set, a man in the
    // middle who missed the handshake cannot forge or tamper with control frames — the Resync
    // parser KI-13 worries about is no longer reachable from the wire without the password. With
    // an EMPTY password the key derives from the public nonces, so an on-path observer of the
    // handshake can compute it; integrity then defends only against off-path (blind) injection.
    // Confidentiality is out of scope either way — frames are authenticated, not encrypted.

    /// <summary>MAC bytes appended to every frame once integrity is enabled.</summary>
    public const int MacBytes = 16;

    private HMACSHA256? _sendMac;   // guarded by _writeLock, like every send
    private HMACSHA256? _recvMac;   // single reader per channel, like every receive
    private long _sendSequence;
    private long _recvSequence;
    private byte _sendDirection;
    private byte _recvDirection;
    private readonly byte[] _sendPreamble = new byte[14]; // dir(1) + seq(8) + type(1) + len(4)
    private readonly byte[] _recvPreamble = new byte[14];

    /// <summary>
    /// Turn on per-frame authentication in both directions. Call at the END of the password
    /// exchange, once both sides hold the key and after the last unauthenticated frame has been
    /// read — the AUTH frames themselves cannot be MACed, because the key they prove is the key
    /// this uses. Both peers reach that point before either sends a post-auth frame, so the first
    /// MACed frame each sends is the first the other expects.
    /// </summary>
    public void EnableIntegrity(byte[] macKey, bool isHost)
    {
        if (macKey == null || macKey.Length < 16)
            throw new ArgumentException("integrity needs a real key", nameof(macKey));
        lock (_writeLock)
        {
            _sendMac = new HMACSHA256(macKey);
            _recvMac = new HMACSHA256(macKey);
            _role = isHost ? 1 : 2;
            _sendDirection = isHost ? (byte)1 : (byte)2;
            _recvDirection = isHost ? (byte)2 : (byte)1;
            _sendSequence = 0;
            _recvSequence = 0;
        }
    }

    /// <summary>Whether frames are currently authenticated (for logs and tests).</summary>
    public bool IntegrityEnabled => _sendMac != null;

    private static byte[] ComputeMac(HMACSHA256 mac, byte[] preamble, byte direction, long sequence,
        byte type, int length, byte[] body)
    {
        preamble[0] = direction;
        for (int i = 0; i < 8; i++) preamble[1 + i] = (byte)(sequence >> (56 - 8 * i));
        preamble[9] = type;
        preamble[10] = (byte)(length >> 24);
        preamble[11] = (byte)(length >> 16);
        preamble[12] = (byte)(length >> 8);
        preamble[13] = (byte)length;
        mac.Initialize();
        mac.TransformBlock(preamble, 0, preamble.Length, null, 0);
        mac.TransformFinalBlock(body, 0, body.Length);
        var full = mac.Hash!;
        var truncated = new byte[MacBytes];
        Buffer.BlockCopy(full, 0, truncated, 0, MacBytes);
        return truncated;
    }

    public ControlChannel(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Optional per-frame progress bound: maps a frame's declared body length to a read timeout
    /// in milliseconds. When set (and the stream supports timeouts), the wait for a frame's
    /// FIRST byte stays governed by the stream's own timeout — an idle channel may legitimately
    /// be silent for minutes (a joiner waiting out the host's lobby) — but once a header has
    /// arrived, the rest of the frame must keep flowing: those reads run under the mapped
    /// timeout, so a peer that dies mid-transfer surfaces as an <see cref="IOException"/> instead
    /// of hanging the receive forever. The stream's previous timeout is restored after every frame.
    ///
    /// "The rest of the frame" includes the integrity tag, not merely the body — see
    /// <see cref="Receive"/>. The length handed here is therefore body + tag, so the budget a
    /// caller computes from it covers everything still to arrive.
    /// </summary>
    public Func<int, int>? BodyReadTimeoutMs { get; set; }

    public void Send(ControlMessageType type, byte[] body)
    {
        if (body == null) body = [];
        int sendCap = MaxLengthFor(type, sending: true);
        if (body.Length > sendCap)
            throw new ArgumentException(
                $"Frame body {body.Length} exceeds the {sendCap}-byte cap for {type}");
        var header = new byte[5];
        header[0] = (byte)type;
        WriteInt32BE(header, 1, body.Length);
        lock (_writeLock)
        {
            _stream.Write(header, 0, header.Length);
            if (body.Length > 0) _stream.Write(body, 0, body.Length);
            if (_sendMac != null)
            {
                var mac = ComputeMac(_sendMac, _sendPreamble, _sendDirection, _sendSequence++,
                    (byte)type, body.Length, body);
                _stream.Write(mac, 0, mac.Length);
            }
            _stream.Flush();
        }
    }

    /// <summary>Block until a full frame arrives. Throws <see cref="EndOfStreamException"/> on close.</summary>
    public (ControlMessageType type, byte[] body) Receive()
    {
        var header = ReadFully(5);
        var type = (ControlMessageType)header[0];
        int len = ReadInt32BE(header, 1);
        int max = MaxLengthFor(type, sending: false);
        if (len < 0 || len > max)
            throw new InvalidDataException(
                $"Frame length {len} out of range for {type} (max {max})");
        // Body AND tag under ONE deadline. Reading the tag after ReadBody restored the previous
        // timeout left the tag read unbounded: a peer that sent a complete body and then stopped
        // held the reader forever — the exact stall the per-frame progress bound (KI-2) exists to
        // prevent, reintroduced by appending bytes after it. A zero-length authenticated frame is
        // the same hazard with no body at all, which is why the timed read now covers the tag even
        // when len is 0.
        bool authenticated = _recvMac != null;
        int trailing = authenticated ? MacBytes : 0;
        var framed = len + trailing == 0 ? [] : ReadBody(len + trailing);
        byte[] body;
        if (trailing == 0) body = framed;
        else
        {
            body = len == 0 ? [] : new byte[len];
            if (len > 0) Buffer.BlockCopy(framed, 0, body, 0, len);
        }
        if (_recvMac != null)
        {
            var expected = ComputeMac(_recvMac, _recvPreamble, _recvDirection, _recvSequence++,
                header[0], len, body);
            int diff = 0;
            for (int i = 0; i < MacBytes; i++) diff |= framed[len + i] ^ expected[i];
            if (diff != 0)
                throw new InvalidDataException(
                    $"control frame {type} failed its integrity check — the stream was tampered " +
                    "with, replayed, or reordered by something on the path");
        }
        return (type, body);
    }

    private byte[] ReadBody(int len)
    {
        var bodyTimeout = BodyReadTimeoutMs;
        if (bodyTimeout == null || !_stream.CanTimeout) return ReadFully(len);

        int previous = _stream.ReadTimeout;
        try
        {
            _stream.ReadTimeout = Math.Max(1, bodyTimeout(len));
            return ReadFully(len);
        }
        finally
        {
            // A NetworkStream reports 0 for "no timeout" but only accepts Infinite (-1) back.
            try { _stream.ReadTimeout = previous > 0 ? previous : System.Threading.Timeout.Infinite; }
            catch { /* restoring on a dead stream is best-effort */ }
        }
    }

    private byte[] ReadFully(int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _stream.Read(buffer, read, count - read);
            if (n <= 0) throw new EndOfStreamException("Control channel closed");
            read += n;
        }
        return buffer;
    }

    private static void WriteInt32BE(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static int ReadInt32BE(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
