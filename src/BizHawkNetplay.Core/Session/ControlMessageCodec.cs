using System;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Wire format of the in-session control messages (pacing, checksum, resync), extracted from
/// the tool form so both directions of every message live side by side under round-trip tests.
/// All integers are big-endian, matching <see cref="HandshakeCodec.EncodeGeneration"/> — these
/// bodies travel over the same TCP control channel as the handshake messages.
///
/// Layouts (offsets in bytes; generation = sessionId(8) + epoch(4)):
///   Pacing      (28): generation, sequence, acknowledges, frame, localAdvantage
///   Checksum    (20): generation, frame, hash(uint32)
///   ResyncBegin (32): generation, stateBytes, waitSeconds, inputDelay, mode, isSettingsChange
///   StatePayload(17+n): generation, format(1: 0=raw 1=deflate), uncompressedLength, payload
/// Decoders are tolerant (return false) — the control channel frames reliably, but a peer on a
/// different build may still send shapes we don't understand.
/// </summary>
public static class ControlMessageCodec
{
    /// <summary>ControlChannel's frame cap minus the 12-byte generation prefix and the 5-byte
    /// state header (format + uncompressed length), so a maximal RAW payload still fits a frame.</summary>
    public const int MaxStateBytes = 64 * 1024 * 1024 - 17;

    /// <summary>Upper bound on a declared reconnect wait — anything bigger is a bogus frame.</summary>
    public const int MaxResyncWaitSeconds = 300;

    public const int PacingSize = 28;
    public const int ChecksumSize = 20;
    public const int ResyncBeginSize = 32;
    public const int GenerationSize = 12;
    public const int MeshRttSize = 28;
    public const int InputDelaySize = 16;

    /// <summary>Round-trip figures above this are a broken clock, not a slow link, and must not be
    /// allowed to buy input delay. Also bounds what a hostile peer can report.</summary>
    public const double MaxMeshRttMs = 10_000;

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

    /// <summary>
    /// Announce an incoming authoritative state, and the parameters the receiver must rebuild its
    /// driver with once it lands.
    ///
    /// The delay and mode ride along on EVERY resync, not just the ones that change them. A resync
    /// already tears the timeline down and stands it back up at a new generation, which is exactly
    /// what changing the input delay or the netcode needs — so carrying the settled parameters here
    /// makes a deliberate mid-session change and a desync recovery the same operation, rather than a
    /// second, less-tested path that does the same thing. It also means no peer can come out of a
    /// resync running parameters the host did not just state.
    ///
    /// <paramref name="isSettingsChange"/> is only ever about what to say and what to count: a
    /// deliberate change is not evidence of a determinism bug and must not spend the resync budget
    /// that exists to catch one.
    /// </summary>
    public static byte[] EncodeResyncBegin(SessionGeneration generation, int stateBytes,
        int inputDelay, SyncMode mode, int waitSeconds = 0, bool isSettingsChange = false)
    {
        if (stateBytes < 0 || stateBytes > MaxStateBytes)
            throw new ArgumentOutOfRangeException(nameof(stateBytes));
        if (waitSeconds < 0 || waitSeconds > MaxResyncWaitSeconds)
            throw new ArgumentOutOfRangeException(nameof(waitSeconds));
        if (inputDelay < 1 || inputDelay > HandshakeCodec.MaxInputDelay)
            throw new ArgumentOutOfRangeException(nameof(inputDelay));
        var body = new byte[ResyncBeginSize];
        WriteGeneration(body, 0, generation);
        WriteInt32(body, 12, stateBytes);
        WriteInt32(body, 16, waitSeconds);
        WriteInt32(body, 20, inputDelay);
        WriteInt32(body, 24, mode == SyncMode.Rollback ? 1 : 0);
        WriteInt32(body, 28, isSettingsChange ? 1 : 0);
        return body;
    }

    public static bool TryDecodeResyncBegin(byte[] body, out SessionGeneration generation,
        out int stateBytes, out int waitSeconds, out int inputDelay, out SyncMode mode,
        out bool isSettingsChange)
    {
        generation = default;
        stateBytes = 0;
        waitSeconds = 0;
        inputDelay = 0;
        mode = SyncMode.Lockstep;
        isSettingsChange = false;
        if (body == null || body.Length != ResyncBeginSize || !TryReadGeneration(body, 0, out generation))
            return false;
        stateBytes = ReadInt32(body, 12);
        waitSeconds = ReadInt32(body, 16);
        inputDelay = ReadInt32(body, 20);
        int modeCode = ReadInt32(body, 24);
        int settingsCode = ReadInt32(body, 28);
        if (modeCode < 0 || modeCode > 1 || settingsCode < 0 || settingsCode > 1) return false;
        mode = modeCode == 1 ? SyncMode.Rollback : SyncMode.Lockstep;
        isSettingsChange = settingsCode == 1;
        return stateBytes >= 0 && stateBytes <= MaxStateBytes
            && waitSeconds >= 0 && waitSeconds <= MaxResyncWaitSeconds
            && inputDelay >= 1 && inputDelay <= HandshakeCodec.MaxInputDelay;
    }

    /// <summary>
    /// The authoritative state for a resync or a rejoin: the generation it belongs to, then the
    /// state framed by <see cref="StateCompression"/>.
    ///
    /// The size announced in RESYNC BEGIN stays the UNCOMPRESSED length, deliberately. Every
    /// receiver-side length check and both transfer deadlines are written against the state's real
    /// size, and leaving them there means compression can only make those deadlines more generous
    /// than they already were — it can never cause a premature timeout on a link that was fine.
    /// </summary>
    public static byte[] EncodeStatePayload(SessionGeneration generation, byte[] state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (state.Length > MaxStateBytes)
            throw new ArgumentException("Resync state exceeds control-frame cap", nameof(state));
        var generationBody = HandshakeCodec.EncodeGeneration(generation); // validates the generation
        var packed = StateCompression.Pack(state);
        var body = new byte[generationBody.Length + packed.Length];
        Buffer.BlockCopy(generationBody, 0, body, 0, generationBody.Length);
        Buffer.BlockCopy(packed, 0, body, generationBody.Length, packed.Length);
        return body;
    }

    public static bool TryDecodeStatePayload(byte[] body, out SessionGeneration generation, out byte[] state)
    {
        generation = default;
        state = Array.Empty<byte>();
        if (body == null || body.Length < GenerationSize + StateCompression.HeaderSize) return false;
        if (!TryReadGeneration(body, 0, out generation)) return false;
        return StateCompression.TryUnpack(body, GenerationSize, body.Length - GenerationSize,
            MaxStateBytes, out state);
    }

    // ---- pre-GO mesh measurement -------------------------------------------------

    /// <summary>
    /// A joiner's report of its worst UDP mesh edge: generation, median and high-water round-trip
    /// (microseconds on the wire, so sub-millisecond LAN figures survive the trip), and how many of
    /// its edges actually answered. The counts matter as much as the timings — a report covering
    /// 1 of 3 edges is not the same claim as one covering 3 of 3, and the host says so in the log
    /// rather than presenting a partial measurement as a complete one.
    /// </summary>
    public static byte[] EncodeMeshRtt(SessionGeneration generation, double medianMs, double highMs,
        int measuredEdges, int totalEdges)
    {
        if (measuredEdges < 0 || totalEdges < 0 || measuredEdges > totalEdges)
            throw new ArgumentOutOfRangeException(nameof(measuredEdges));
        var body = new byte[MeshRttSize];
        WriteGeneration(body, 0, generation);
        WriteInt32(body, 12, ToMicros(medianMs));
        WriteInt32(body, 16, ToMicros(highMs));
        WriteInt32(body, 20, measuredEdges);
        WriteInt32(body, 24, totalEdges);
        return body;
    }

    public static bool TryDecodeMeshRtt(byte[] body, out SessionGeneration generation,
        out double medianMs, out double highMs, out int measuredEdges, out int totalEdges)
    {
        generation = default;
        medianMs = 0;
        highMs = 0;
        measuredEdges = 0;
        totalEdges = 0;
        if (body == null || body.Length != MeshRttSize || !TryReadGeneration(body, 0, out generation))
            return false;
        int medianMicros = ReadInt32(body, 12);
        int highMicros = ReadInt32(body, 16);
        measuredEdges = ReadInt32(body, 20);
        totalEdges = ReadInt32(body, 24);
        if (medianMicros < 0 || highMicros < 0 || measuredEdges < 0 || totalEdges < 0
            || measuredEdges > totalEdges || totalEdges > HandshakeCodec.MaxPlayers)
            return false;
        medianMs = medianMicros / 1000.0;
        highMs = highMicros / 1000.0;
        if (medianMs > MaxMeshRttMs || highMs > MaxMeshRttMs) return false;
        if (highMs < medianMs) highMs = medianMs;
        return true;
    }

    /// <summary>The host's authoritative delay, sent after the mesh round and before READY.</summary>
    public static byte[] EncodeInputDelay(SessionGeneration generation, int delay)
    {
        if (delay < 1 || delay > HandshakeCodec.MaxInputDelay)
            throw new ArgumentOutOfRangeException(nameof(delay));
        var body = new byte[InputDelaySize];
        WriteGeneration(body, 0, generation);
        WriteInt32(body, 12, delay);
        return body;
    }

    public static bool TryDecodeInputDelay(byte[] body, out SessionGeneration generation, out int delay)
    {
        generation = default;
        delay = 0;
        if (body == null || body.Length != InputDelaySize || !TryReadGeneration(body, 0, out generation))
            return false;
        delay = ReadInt32(body, 12);
        return delay >= 1 && delay <= HandshakeCodec.MaxInputDelay;
    }

    private static int ToMicros(double ms)
    {
        if (double.IsNaN(ms) || ms <= 0) return 0;
        if (ms > MaxMeshRttMs) ms = MaxMeshRttMs;
        return (int)Math.Round(ms * 1000.0);
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
