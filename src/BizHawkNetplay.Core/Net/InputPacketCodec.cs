using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;

namespace BizHawkNetplay.Core.Net
{
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
        /// trace: <c>TryDecodeInput</c> returned false and the receive loop simply moved on. That made
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
        /// Decode a datagram into per-frame <see cref="InputFrame"/>s. Returns false for unknown,
        /// malformed, or differently generated datagrams rather than throwing — the input channel
        /// is untrusted UDP.
        /// </summary>
        public bool TryDecodeInput(byte[] datagram, out List<InputFrame> frames)
        {
            frames = new List<InputFrame>();
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

            int offset = HeaderSize;
            for (int i = 0; i < count; i++)
            {
                var payload = new byte[payloadSize];
                Buffer.BlockCopy(datagram, offset, payload, 0, payloadSize);
                offset += payloadSize;
                frames.Add(new InputFrame(baseFrame + i, port, payload));
            }
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

        /// <summary>Decode a gap request. Same tolerance rules as <see cref="TryDecodeInput"/>.</summary>
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
}
