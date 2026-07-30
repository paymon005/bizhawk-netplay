using System;
using System.IO;
using System.IO.Compression;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Savestates on the wire: a five-byte header and deflate, used wherever a whole emulator state
/// crosses the control channel.
///
/// Every such transfer stops every emulator in the session until it lands — the initial join, a
/// desync resync, a rejoin, a live settings change. On a heavy core that is 16.7 MiB of console
/// RAM, which is mostly not random, and over a real upstream link the uncompressed version is the
/// difference between a pause players wait out and one that reads as a crash. Until now none of it
/// was compressed at all, on a path that has only ever been exercised over loopback and LAN, where
/// the cost of not compressing is invisible.
///
/// Header is <c>format(1) + uncompressedLength(4, big-endian)</c>, matching the rest of the control
/// wire format. Raw is emitted whenever deflate fails to win, so an incompressible state costs five
/// bytes and never more than it started with.
/// </summary>
public static class StateCompression
{
    public const byte FormatRaw = 0;
    public const byte FormatDeflate = 1;

    /// <summary>Header bytes added ahead of the payload.</summary>
    public const int HeaderSize = 5;

    /// <summary>Frame a state for sending, compressing it when that actually helps.</summary>
    public static byte[] Pack(byte[] state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));

        byte[] payload = state;
        byte format = FormatRaw;
        var deflated = TryDeflate(state);
        if (deflated != null && deflated.Length < state.Length)
        {
            payload = deflated;
            format = FormatDeflate;
        }

        var body = new byte[HeaderSize + payload.Length];
        body[0] = format;
        body[1] = (byte)(state.Length >> 24);
        body[2] = (byte)(state.Length >> 16);
        body[3] = (byte)(state.Length >> 8);
        body[4] = (byte)state.Length;
        Buffer.BlockCopy(payload, 0, body, HeaderSize, payload.Length);
        return body;
    }

    /// <summary>
    /// Recover a state, or refuse. <paramref name="maxBytes"/> caps the declared length before a
    /// buffer is allocated for it: a few compressed bytes can claim to expand to any size at all,
    /// and this runs on a peer's word.
    /// </summary>
    public static bool TryUnpack(byte[] body, int offset, int count, int maxBytes, out byte[] state)
    {
        state = [];
        if (body == null || offset < 0 || count < HeaderSize || offset + count > body.Length)
            return false;

        byte format = body[offset];
        int declared = (body[offset + 1] << 24) | (body[offset + 2] << 16)
            | (body[offset + 3] << 8) | body[offset + 4];
        if (declared < 0 || declared > maxBytes) return false;

        int payloadOffset = offset + HeaderSize;
        int payloadCount = count - HeaderSize;

        if (format == FormatRaw)
        {
            if (payloadCount != declared) return false;
            state = new byte[declared];
            Buffer.BlockCopy(body, payloadOffset, state, 0, declared);
            return true;
        }
        if (format != FormatDeflate) return false;

        var inflated = TryInflate(body, payloadOffset, payloadCount, declared);
        if (inflated == null) return false;
        state = inflated;
        return true;
    }

    public static bool TryUnpack(byte[] body, int maxBytes, out byte[] state) =>
        TryUnpack(body, 0, body?.Length ?? 0, maxBytes, out state);

    private static byte[]? TryDeflate(byte[] state)
    {
        try
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(state, 0, state.Length);
            return output.ToArray();
        }
        catch { return null; }   // never fail a transfer over a failed optimisation
    }

    /// <summary>
    /// Inflate to exactly <paramref name="expected"/> bytes and refuse any stream that does not fit
    /// that claim in either direction. One byte is read past the expected end: a stream still
    /// producing output has lied about its size, and truncating it would hand back a state that
    /// imports cleanly and then desyncs — the failure this whole layer exists to prevent.
    /// </summary>
    private static byte[]? TryInflate(byte[] body, int offset, int count, int expected)
    {
        try
        {
            var result = new byte[expected];
            using var input = new MemoryStream(body, offset, count, writable: false);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);

            int filled = 0;
            while (filled < expected)
            {
                int n = inflate.Read(result, filled, expected - filled);
                if (n <= 0) break;
                filled += n;
            }
            if (filled != expected) return null;

            var overflow = new byte[1];
            if (inflate.Read(overflow, 0, 1) != 0) return null;
            return result;
        }
        catch { return null; }
    }
}
