using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// Encodes and decodes input datagrams. Each datagram carries one port's inputs for a
/// contiguous run of frames — the last R (redundancy) frames, not just the newest — so a
/// lost packet costs nothing unless R in a row are lost (§3.2). Payload sizes are fixed per
/// port by the negotiated layout, so the wire format is derived, never per-game.
///
/// Datagram layout (little-endian):
///   [0]       type  (1 = input)
///   [1]       port
///   [2..9]    sessionId (uint64) — session-unique nonce
///   [10..13]  epoch (int32) — input-timeline generation within the session
///   [14..17]  baseFrame (int32) — simulation frame of the first payload
///   [18]      count — number of consecutive frames included
///   [19..]    count × payloadSize[port] bytes
///
/// Gap-request layout (type 2), same [1..13] prefix:
///   [14..17]  fromFrame (int32) — first missing frame the requester needs for `port`
/// A request asks the owner of `port` to re-send its inputs starting at fromFrame — the
/// recovery path for a frame that has aged out of the sender's redundant window. A peer on a
/// build without this type simply fails to decode it and ignores it.
/// </summary>
public sealed class InputPacketCodec
{
    private const byte TypeInput = 1;
    private const byte TypeRequest = 2;
    private const int HeaderSize = 19;
    private const int RequestSize = 18;

    /// <summary>
    /// Largest input datagram this codec will produce, so a session can never build one the network
    /// silently eats.
    ///
    /// A datagram carries R = max(redundancy, 2·delay+1) frames of one port's layout, and nothing
    /// used to bound the product. Past the path MTU an IP-fragmented UDP datagram is dropped
    /// outright by many NATs and firewalls, and past the receive buffer the socket raises a
    /// size error that the mesh's receive loop can only skip — so the port's input would stop
    /// arriving entirely, forever, and present as "no UDP input from PN": a network fault, on a
    /// network that was working.
    ///
    /// 1200 is the conservative figure QUIC and WebRTC use for exactly this reason: it survives
    /// PPPoE, VPN and tunnel overhead rather than assuming 1500. The mesh envelope adds 6 bytes on
    /// top, keeping the wire packet under the 1280-byte IPv6 minimum MTU — and well under
    /// <c>MeshUdpTransport</c>'s receive buffer, which must stay larger than this.
    /// </summary>
    public const int MaxDatagramBytes = 1200;

    /// <summary>
    /// How many consecutive frames of a <paramref name="payloadSize"/>-byte port fit in one
    /// datagram. Zero for a port with no serializable layout. Capped at 255 because the count is
    /// one byte on the wire.
    /// </summary>
    public static int MaxFramesPerDatagram(int payloadSize) =>
        payloadSize <= 0 ? 0 : Math.Min(byte.MaxValue, (MaxDatagramBytes - HeaderSize) / payloadSize);

    /// <summary>The largest input delay a <paramref name="payloadSize"/>-byte port can be run at,
    /// since the redundant window is at least 2·delay+1 frames wide.</summary>
    public static int MaxInputDelayFor(int payloadSize) =>
        Math.Max(0, (MaxFramesPerDatagram(payloadSize) - 1) / 2);

    private readonly int[] _payloadSizes; // indexed by port
    private readonly SessionGeneration _generation;

    public InputPacketCodec(int[] payloadSizesByPort, SessionGeneration? generation = null)
    {
        _payloadSizes = payloadSizesByPort ?? throw new ArgumentNullException(nameof(payloadSizesByPort));
        _generation = generation ?? SessionGeneration.Legacy;
        if (!_generation.IsValid)
            throw new ArgumentOutOfRangeException(nameof(generation), "Generation must have a non-zero session ID and non-negative epoch");
    }

    public SessionGeneration Generation => _generation;

    /// <summary>
    /// Input datagrams thrown away, by reason. A rejected input packet used to vanish without a
    /// trace: the decode returned false and the receive loop simply moved on. That made
    /// a whole class of failure undiagnosable from a log — a 3-player NES session where every packet
    /// from the third port was discarded on arrival read exactly like a network problem ("no UDP
    /// input from P3"), while the UDP path had opened and the packets were in fact arriving.
    ///
    /// A datagram that is not an input packet at all is NOT counted here; those are gap requests
    /// and are handled by the caller. These count only packets that claimed to be input and were
    /// then refused.
    /// </summary>
    public long RejectedGeneration { get; private set; }
    public long RejectedUnknownPort { get; private set; }
    public long RejectedPayloadSize { get; private set; }
    public long RejectedMalformed { get; private set; }

    /// <summary>Total input datagrams refused, for a cheap "is anything being dropped" check.</summary>
    public long RejectedTotal =>
        RejectedGeneration + RejectedUnknownPort + RejectedPayloadSize + RejectedMalformed;

    /// <summary>
    /// The last size disagreement observed, as (port, expected, observed) — the payload size this
    /// machine computes for that port against the one the sender evidently used. Non-zero expected
    /// means a peer is running a different controller/peripheral configuration for that port, which
    /// no amount of network diagnosis will fix, so it is worth naming exactly.
    /// </summary>
    public int LastSizeMismatchPort { get; private set; } = -1;
    public int LastSizeMismatchExpected { get; private set; }
    public int LastSizeMismatchObserved { get; private set; }

    /// <summary>
    /// Encode a contiguous, ascending window of (frame, payload) for one port. Frames must be
    /// consecutive; payloads must match the port's fixed size.
    /// </summary>
    public byte[] EncodeInput(byte port, IReadOnlyList<KeyValuePair<int, byte[]>> window)
    {
        if (window == null) throw new ArgumentNullException(nameof(window));
        if (window.Count == 0) throw new ArgumentException("Empty window", nameof(window));
        if (window.Count > byte.MaxValue) throw new ArgumentException("Window too large", nameof(window));
        int payloadSize = _payloadSizes[port];
        // Unreachable in a session the driver accepted — it refuses the configuration up front so
        // this cannot fire mid-play — but an oversized datagram would be dropped somewhere out on
        // the path, silently, so it is worth refusing loudly at the only place that builds one.
        if (HeaderSize + window.Count * payloadSize > MaxDatagramBytes)
            throw new ArgumentException(
                $"Window of {window.Count} frames × {payloadSize} bytes exceeds the " +
                $"{MaxDatagramBytes}-byte datagram limit for port {port}", nameof(window));

        int baseFrame = window[0].Key;
        var buffer = new byte[HeaderSize + window.Count * payloadSize];
        buffer[0] = TypeInput;
        buffer[1] = port;
        WriteUInt64(buffer, 2, _generation.SessionId);
        WriteInt32(buffer, 10, _generation.Epoch);
        WriteInt32(buffer, 14, baseFrame);
        buffer[18] = (byte)window.Count;

        int offset = HeaderSize;
        for (int i = 0; i < window.Count; i++)
        {
            var kv = window[i];
            if (kv.Key != baseFrame + i)
                throw new ArgumentException($"Window not contiguous at index {i} (frame {kv.Key})");
            if (kv.Value.Length != payloadSize)
                throw new ArgumentException($"Payload size {kv.Value.Length} != expected {payloadSize} for port {port}");
            Buffer.BlockCopy(kv.Value, 0, buffer, offset, payloadSize);
            offset += payloadSize;
        }
        return buffer;
    }

    /// <summary>
    /// Allocate a datagram of exactly the right size and write its header, leaving the payload area
    /// for the caller to fill. <paramref name="payloadOffset"/> is where the first frame's bytes go;
    /// the rest follow contiguously at <c>PayloadSizeFor(port)</c> apiece.
    ///
    /// The counterpart to <see cref="InputWindow"/> on the receive side: the sender keeps its recent
    /// payloads in one contiguous ring, so gathering them into a list of arrays just to hand them
    /// back for copying was work with no purpose. The caller copies once, out of the ring, into here.
    /// </summary>
    public byte[] BeginInputDatagram(byte port, int baseFrame, int count, out int payloadOffset)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Empty window");
        if (count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(count), "Window too large");
        int payloadSize = _payloadSizes[port];
        if (HeaderSize + count * payloadSize > MaxDatagramBytes)
            throw new ArgumentException(
                $"Window of {count} frames × {payloadSize} bytes exceeds the " +
                $"{MaxDatagramBytes}-byte datagram limit for port {port}", nameof(count));

        var buffer = new byte[HeaderSize + count * payloadSize];
        buffer[0] = TypeInput;
        buffer[1] = port;
        WriteUInt64(buffer, 2, _generation.SessionId);
        WriteInt32(buffer, 10, _generation.Epoch);
        WriteInt32(buffer, 14, baseFrame);
        buffer[18] = (byte)count;
        payloadOffset = HeaderSize;
        return buffer;
    }

    /// <summary>Serialized size of one frame for this port, as this codec was configured.</summary>
    public int PayloadSizeFor(byte port) => _payloadSizes[port];

    /// <summary>
    /// A validated input datagram described in place: which port and frames it covers, and where
    /// each frame's bytes start. No payload is copied, which matters because most of them are
    /// thrown away — a datagram repeats the last R frames, so at delay 4 nine frames arrive and
    /// typically eight are already held. With three remotes at 60Hz that was ~1,600–2,300 arrays a
    /// second allocated purely to be discarded, and the churn scales with the player count.
    /// </summary>
    public readonly struct InputWindow
    {
        public InputWindow(byte port, int baseFrame, int count, int payloadSize)
        {
            Port = port;
            BaseFrame = baseFrame;
            Count = count;
            PayloadSize = payloadSize;
        }

        public byte Port { get; }
        public int BaseFrame { get; }
        public int Count { get; }
        public int PayloadSize { get; }

        /// <summary>Offset within the datagram of the payload for <c>BaseFrame + index</c>.</summary>
        public int OffsetOf(int index) => HeaderSize + index * PayloadSize;
    }

    /// <summary>
    /// Validate a datagram and describe its contents without copying any of them. Same acceptance
    /// rules and same rejection counters the copying decode this replaced used to apply.
    /// </summary>
    public bool TryDecodeInputWindow(byte[] datagram, out InputWindow window)
    {
        window = default;
        if (datagram == null || datagram.Length < HeaderSize) return false;
        if (datagram[0] != TypeInput) return false;   // a request, not ours to refuse — see counters
        if (ReadUInt64(datagram, 2) != _generation.SessionId ||
            ReadInt32(datagram, 10) != _generation.Epoch) { RejectedGeneration++; return false; }

        byte port = datagram[1];
        if (port >= _payloadSizes.Length) { RejectedUnknownPort++; return false; }
        int payloadSize = _payloadSizes[port];

        int baseFrame = ReadInt32(datagram, 14);
        int count = datagram[18];
        if (payloadSize <= 0)
        {
            // This machine has no serializable layout for that port at all, yet a peer is sending
            // input for it — the session was started with more players than this core exposes
            // controllers for.
            RejectedPayloadSize++;
            LastSizeMismatchPort = port;
            LastSizeMismatchExpected = payloadSize;
            LastSizeMismatchObserved = count > 0 ? (datagram.Length - HeaderSize) / count : 0;
            return false;
        }
        if (datagram.Length != HeaderSize + count * payloadSize)
        {
            RejectedPayloadSize++;
            LastSizeMismatchPort = port;
            LastSizeMismatchExpected = payloadSize;
            // What the sender must have been using, inferred from the frame count it declared.
            LastSizeMismatchObserved = count > 0 ? (datagram.Length - HeaderSize) / count : 0;
            return false;
        }
        if (count == 0) { RejectedMalformed++; return false; }
        // No frame is ever legitimately negative — the driver starts every timeline at 0 — but a
        // datagram corrupted past UDP's 16-bit checksum can claim one, and the receive filters
        // downstream only bound the FUTURE side. Near frame 0 (session start, or right after any
        // resync rebuild) a small negative frame passed rollback's too-old filter and reached the
        // pipeline, whose argument check then threw — one bad packet ending the session with a
        // generic error. Refused here instead, where it gets a counter and shows up in the log's
        // rejection note. Overflow rides along: a huge baseFrame whose baseFrame+count wraps
        // negative is the same corruption wearing a different byte.
        if (baseFrame < 0 || baseFrame > int.MaxValue - count) { RejectedMalformed++; return false; }

        window = new InputWindow(port, baseFrame, count, payloadSize);
        return true;
    }

    /// <summary>Encode a request for <paramref name="targetPort"/>'s inputs from <paramref name="fromFrame"/> on.</summary>
    public byte[] EncodeRequest(byte targetPort, int fromFrame)
    {
        if (targetPort >= _payloadSizes.Length) throw new ArgumentOutOfRangeException(nameof(targetPort));
        if (fromFrame < 0) throw new ArgumentOutOfRangeException(nameof(fromFrame));
        var buffer = new byte[RequestSize];
        buffer[0] = TypeRequest;
        buffer[1] = targetPort;
        WriteUInt64(buffer, 2, _generation.SessionId);
        WriteInt32(buffer, 10, _generation.Epoch);
        WriteInt32(buffer, 14, fromFrame);
        return buffer;
    }

    /// <summary>Decode a gap request. Same tolerance rules as <see cref="TryDecodeInputWindow"/>.</summary>
    public bool TryDecodeRequest(byte[] datagram, out byte targetPort, out int fromFrame)
    {
        targetPort = 0;
        fromFrame = 0;
        if (datagram == null || datagram.Length != RequestSize) return false;
        if (datagram[0] != TypeRequest) return false;
        if (ReadUInt64(datagram, 2) != _generation.SessionId ||
            ReadInt32(datagram, 10) != _generation.Epoch) return false;
        targetPort = datagram[1];
        if (targetPort >= _payloadSizes.Length) return false;
        fromFrame = ReadInt32(datagram, 14);
        return fromFrame >= 0;
    }

    private static void WriteInt32(byte[] b, int o, int v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    private static void WriteUInt64(byte[] b, int o, ulong v)
    {
        for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
    }

    private static int ReadInt32(byte[] b, int o) =>
        b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

    private static ulong ReadUInt64(byte[] b, int o)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++) value |= (ulong)b[o + i] << (8 * i);
        return value;
    }
}
