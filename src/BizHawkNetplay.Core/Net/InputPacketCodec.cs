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
    ///   [0]      type  (1 = input)
    ///   [1]      port
    ///   [2..5]   baseFrame (int32) — simulation frame of the first payload
    ///   [6]      count — number of consecutive frames included
    ///   [7..]    count × payloadSize[port] bytes
    /// </summary>
    public sealed class InputPacketCodec
    {
        private const byte TypeInput = 1;
        private const int HeaderSize = 7;

        private readonly int[] _payloadSizes; // indexed by port

        public InputPacketCodec(int[] payloadSizesByPort)
        {
            _payloadSizes = payloadSizesByPort ?? throw new ArgumentNullException(nameof(payloadSizesByPort));
        }

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
            WriteInt32(buffer, 2, baseFrame);
            buffer[6] = (byte)window.Count;

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
        /// Decode a datagram into per-frame <see cref="InputFrame"/>s. Returns false for unknown
        /// or malformed datagrams rather than throwing — the input channel is untrusted UDP.
        /// </summary>
        public bool TryDecodeInput(byte[] datagram, out List<InputFrame> frames)
        {
            frames = new List<InputFrame>();
            if (datagram == null || datagram.Length < HeaderSize) return false;
            if (datagram[0] != TypeInput) return false;

            byte port = datagram[1];
            if (port >= _payloadSizes.Length) return false;
            int payloadSize = _payloadSizes[port];

            int baseFrame = ReadInt32(datagram, 2);
            int count = datagram[6];
            if (payloadSize <= 0 || datagram.Length != HeaderSize + count * payloadSize) return false;

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

        private static void WriteInt32(byte[] b, int o, int v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static int ReadInt32(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    }
}
