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
        Ping = 8,      // RTT probe: body = [t0Ms:double] (sender's monotonic send time)
        Pong = 9,      // RTT echo: body = [t0Ms:double] (the ping's t0, echoed back unchanged)
        Resync = 10,   // host -> client: authoritative whole-core state to recover from a desync
        ResyncRequest = 11, // client -> host: "I saw a desync, please resync us"
        PeerList = 12, // host -> client: the other peers' UDP endpoints for the direct input mesh
        Candidate = 13, // client -> host: my reflexive (STUN) UDP endpoint, for NAT-traversal candidates
    }

    /// <summary>
    /// The reliable control channel (§3.3): length-prefixed framing over any duplex
    /// <see cref="Stream"/> (a TCP <c>NetworkStream</c> in production, a paired memory stream in
    /// tests). Carries the handshake, initial state transfer, and periodic checksums — everything
    /// that must not be lost. The unreliable input hot path stays on <see cref="Net.UdpTransport"/>.
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
            var body = len == 0 ? Array.Empty<byte>() : ReadFully(len);
            return (type, body);
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
