using System;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Session
{
    /// <summary>
    /// Wire format of the in-session control messages (pacing, checksum, resync), extracted from
    /// the tool form so both directions of every message live side by side under round-trip tests.
    /// All integers are big-endian, matching <see cref="HandshakeCodec.EncodeGeneration"/> — these
    /// bodies travel over the same TCP control channel as the handshake messages.
    ///
    /// Layouts (offsets in bytes; generation = sessionId(8) + epoch(4)):
    ///   Pacing      (28): generation, sequence, acknowledges, frame, localAdvantage
    ///   Checksum    (20): generation, frame, hash(uint32)
    ///   ResyncBegin (20): generation, stateBytes, waitSeconds
    ///   StatePayload(12+n): generation, raw state bytes
    /// Decoders are tolerant (return false) — the control channel frames reliably, but a peer on a
    /// different build may still send shapes we don't understand.
    /// </summary>
    public static class ControlMessageCodec
    {
        /// <summary>ControlChannel's frame cap minus the 12-byte generation prefix.</summary>
        public const int MaxStateBytes = 64 * 1024 * 1024 - 12;

        /// <summary>Upper bound on a declared reconnect wait — anything bigger is a bogus frame.</summary>
        public const int MaxResyncWaitSeconds = 300;

        public const int PacingSize = 28;
        public const int ChecksumSize = 20;
        public const int ResyncBeginSize = 20;
        public const int GenerationSize = 12;

        // ---- pacing -----------------------------------------------------------------

        public static byte[] EncodePacing(SessionGeneration generation, int sequence,
            int acknowledges, int frame, int localAdvantage)
        {
            var b = new byte[PacingSize];
            WriteGeneration(b, 0, generation);
            WriteInt32(b, 12, sequence);
            WriteInt32(b, 16, acknowledges);
            WriteInt32(b, 20, frame);
            WriteInt32(b, 24, localAdvantage);
            return b;
        }

        public static bool TryDecodePacing(byte[] body, out SessionGeneration generation,
            out int sequence, out int acknowledges, out int frame, out int localAdvantage)
        {
            sequence = 0;
            acknowledges = 0;
            frame = 0;
            localAdvantage = 0;
            generation = SessionGeneration.Legacy;
            if (body == null || body.Length != PacingSize || !TryReadGeneration(body, 0, out generation))
                return false;
            sequence = ReadInt32(body, 12);
            acknowledges = ReadInt32(body, 16);
            frame = ReadInt32(body, 20);
            localAdvantage = ReadInt32(body, 24);
            return true;
        }

        // ---- checksum ---------------------------------------------------------------

        public static byte[] EncodeChecksum(SessionGeneration generation, int frame, uint hash)
        {
            var b = new byte[ChecksumSize];
            WriteGeneration(b, 0, generation);
            WriteInt32(b, 12, frame);
            b[16] = (byte)(hash >> 24); b[17] = (byte)(hash >> 16);
            b[18] = (byte)(hash >> 8); b[19] = (byte)hash;
            return b;
        }

        /// <summary>Decodes only when the body carries <paramref name="expected"/> — a checksum from
        /// any other generation is a dead timeline's report and must never reach aggregation.</summary>
        public static bool TryDecodeChecksum(byte[] b, SessionGeneration expected, out int frame, out uint hash)
        {
            frame = 0;
            hash = 0;
            if (b == null || b.Length != ChecksumSize || !TryReadGeneration(b, 0, out var generation)
                || generation != expected) return false;
            frame = ReadInt32(b, 12);
            hash = ((uint)b[16] << 24) | ((uint)b[17] << 16) | ((uint)b[18] << 8) | b[19];
            return true;
        }

        // ---- resync -----------------------------------------------------------------

        public static byte[] EncodeResyncBegin(SessionGeneration generation, int stateBytes, int waitSeconds = 0)
        {
            if (stateBytes < 0 || stateBytes > MaxStateBytes)
                throw new ArgumentOutOfRangeException(nameof(stateBytes));
            if (waitSeconds < 0 || waitSeconds > MaxResyncWaitSeconds)
                throw new ArgumentOutOfRangeException(nameof(waitSeconds));
            var body = new byte[ResyncBeginSize];
            WriteGeneration(body, 0, generation);
            WriteInt32(body, 12, stateBytes);
            WriteInt32(body, 16, waitSeconds);
            return body;
        }

        public static bool TryDecodeResyncBegin(byte[] body, out SessionGeneration generation,
            out int stateBytes, out int waitSeconds)
        {
            generation = default;
            stateBytes = 0;
            waitSeconds = 0;
            if (body == null || body.Length != ResyncBeginSize || !TryReadGeneration(body, 0, out generation))
                return false;
            stateBytes = ReadInt32(body, 12);
            waitSeconds = ReadInt32(body, 16);
            return stateBytes >= 0 && stateBytes <= MaxStateBytes
                && waitSeconds >= 0 && waitSeconds <= MaxResyncWaitSeconds;
        }

        public static byte[] EncodeStatePayload(SessionGeneration generation, byte[] state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Length > MaxStateBytes)
                throw new ArgumentException("Resync state exceeds control-frame cap", nameof(state));
            var generationBody = HandshakeCodec.EncodeGeneration(generation); // validates the generation
            var body = new byte[generationBody.Length + state.Length];
            Buffer.BlockCopy(generationBody, 0, body, 0, generationBody.Length);
            Buffer.BlockCopy(state, 0, body, generationBody.Length, state.Length);
            return body;
        }

        public static bool TryDecodeStatePayload(byte[] body, out SessionGeneration generation, out byte[] state)
        {
            generation = default;
            state = Array.Empty<byte>();
            if (body == null || body.Length < GenerationSize) return false;
            if (!TryReadGeneration(body, 0, out generation)) return false;
            state = new byte[body.Length - GenerationSize];
            Buffer.BlockCopy(body, GenerationSize, state, 0, state.Length);
            return true;
        }

        /// <summary>Tolerant decode of a bare 12-byte generation body (READY/GO, resync ack/resume).</summary>
        public static bool TryDecodeGeneration(byte[] body, out SessionGeneration generation)
        {
            generation = default;
            return body != null && body.Length == GenerationSize && TryReadGeneration(body, 0, out generation);
        }

        // ---- primitives -------------------------------------------------------------

        private static void WriteGeneration(byte[] b, int offset, SessionGeneration generation)
        {
            ulong id = generation.SessionId;
            for (int i = 7; i >= 0; i--) { b[offset + i] = (byte)id; id >>= 8; }
            WriteInt32(b, offset + 8, generation.Epoch);
        }

        private static bool TryReadGeneration(byte[] b, int offset, out SessionGeneration generation)
        {
            generation = SessionGeneration.Legacy;
            if (b == null || offset < 0 || b.Length - offset < GenerationSize) return false;
            ulong id = 0;
            for (int i = 0; i < 8; i++) id = (id << 8) | b[offset + i];
            int epoch = ReadInt32(b, offset + 8);
            if (id == 0 || epoch < 0) return false;
            generation = new SessionGeneration(id, epoch);
            return true;
        }

        private static void WriteInt32(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static int ReadInt32(byte[] b, int o) =>
            (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
    }
}
