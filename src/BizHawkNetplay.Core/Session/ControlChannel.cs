using System;
using System.IO;

namespace BizHawkNetplay.Core.Session
{
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
        ResyncRequest = 11, // client -> host: "I saw a desync, please resync us"
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

        private readonly Stream _stream;
        private readonly object _writeLock = new object();

        public ControlChannel(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>
        /// Optional per-frame progress bound: maps a frame's declared body length to a read timeout
        /// in milliseconds. When set (and the stream supports timeouts), the wait for a frame's
        /// FIRST byte stays governed by the stream's own timeout — an idle channel may legitimately
        /// be silent for minutes (a joiner waiting out the host's lobby) — but once a header has
        /// arrived, the body must keep flowing: its reads run under the mapped timeout, so a peer
        /// that dies mid-transfer surfaces as an <see cref="IOException"/> instead of hanging the
        /// receive forever. The stream's previous timeout is restored after every frame.
        /// </summary>
        public Func<int, int>? BodyReadTimeoutMs { get; set; }

        public void Send(ControlMessageType type, byte[] body)
        {
            if (body == null) body = Array.Empty<byte>();
            if (body.Length > MaxFrameLength)
                throw new ArgumentException($"Frame body {body.Length} exceeds cap");
            var header = new byte[5];
            header[0] = (byte)type;
            WriteInt32BE(header, 1, body.Length);
            lock (_writeLock)
            {
                _stream.Write(header, 0, header.Length);
                if (body.Length > 0) _stream.Write(body, 0, body.Length);
                _stream.Flush();
            }
        }

        /// <summary>Block until a full frame arrives. Throws <see cref="EndOfStreamException"/> on close.</summary>
        public (ControlMessageType type, byte[] body) Receive()
        {
            var header = ReadFully(5);
            var type = (ControlMessageType)header[0];
            int len = ReadInt32BE(header, 1);
            if (len < 0 || len > MaxFrameLength)
                throw new InvalidDataException($"Frame length {len} out of range");
            var body = len == 0 ? Array.Empty<byte>() : ReadBody(len);
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
}
