using System;

namespace BizHawkNetplay.Core.Emu;

/// <summary>
/// The desync checksum's arithmetic: FNV-1a over a byte buffer, folded to 32 bits, with ranges
/// skipped and the reading parameters mixed into the seed.
///
/// <b>Why this is in Core.</b> It touches no BizHawk type — it takes a <c>byte[]</c> and returns a
/// number. It lived in the adapter only because that is where the bytes were fetched, which meant
/// the one function in this codebase whose output crosses the wire and must be reproduced bit for
/// bit by every peer was in the half no test can reach. A change here is a silent, permanent
/// desync between versions, so it is exactly the wrong thing to leave untestable.
///
/// <b>Every value produced here is wire-visible.</b> Two peers compare these numbers directly. Any
/// change to the arithmetic — the order bytes are consumed, the lane structure, the seed — makes
/// the same state hash differently and requires a protocol bump. <c>MemoryHashTests</c> pins known
/// values for that reason: they are a wire format, not an implementation detail.
/// </summary>
public static class MemoryHash
{
    private const ulong Prime = 1099511628211UL;
    private const ulong Basis = 14695981039346656037UL;

    /// <summary>Identifies the route a hash took, so two peers on different ones cannot compare
    /// their results as though they described the same bytes.</summary>
    public const int PathPtr = 1;
    public const int PathArray = 2;
    public const int PathBulk = 3;
    public const int PathWord = 4;
    public const int PathClosure = 5;

    /// <summary>
    /// FNV-1a folded to 32 bits, consuming eight bytes per step. Both peers run the same arithmetic
    /// on the same bytes, so the result is identical wherever it is computed — that, not collision
    /// resistance, is the property desync detection needs.
    /// </summary>
    public static uint Fnv1a64(byte[] data, int length, int pathTag, long[] exRanges, ulong extraSeed)
    {
        ulong h = SeedWithExclusions(Basis, pathTag, exRanges, extraSeed);
        long cursor = 0;
        for (int r = 0; r < exRanges.Length; r += 2)
        {
            long start = Math.Min(exRanges[r], length);
            long end = Math.Min(exRanges[r + 1], length);
            if (start > cursor) h = FoldRange(h, data, (int)cursor, (int)start);
            if (end > cursor) cursor = end;
        }
        if (cursor < length) h = FoldRange(h, data, (int)cursor, length);
        // Fold the high half down so a divergence up there can't vanish in the truncation.
        return (uint)(h ^ (h >> 32));
    }

    /// <summary>
    /// The inner loop, and the hottest code in the session outside the core itself.
    ///
    /// <b>Why a fixed pointer rather than BitConverter.</b> Identical arithmetic on identical bytes
    /// — the value is bit-for-bit what the <c>BitConverter.ToUInt64</c> version produced, which
    /// <c>MemoryHashTests</c> checks against a reference implementation over unaligned starts, odd
    /// lengths and every exclusion shape. What changes is only how the eight bytes are read.
    ///
    /// Measured over 8 MiB (N64's RDRAM, the case this exists for), median of 21 runs:
    /// <b>.NET Framework 4.8 — 4.80ms with BitConverter against 2.00ms here, 2.41x.</b> On .NET 10
    /// it is 1.50ms against 1.36ms, only 1.10x. The tool ships on Framework, so the JIT that
    /// benefits most is the one it actually runs under — the same asymmetry the PBKDF2 measurement
    /// found, where Framework takes a slow legacy path the modern runtime does not.
    ///
    /// That is around 2.8ms off every N64 checksum, on a core whose steady frame already sits near
    /// its whole budget, for no wire change at all.
    ///
    /// <b>What was left on the table, and why.</b> The dependency chain is the real ceiling here:
    /// each step needs the previous <c>h</c>, so the loop runs at the latency of one 64-bit multiply
    /// per eight bytes however much the CPU could otherwise overlap. Splitting into eight
    /// independent lanes measured 0.62ms on Framework — 7.71x — but it produces a DIFFERENT hash,
    /// so every peer must change together. That is a protocol bump, and one is already owed for the
    /// KDF move off SHA-1 (see KNOWN-ISSUES); the two belong in the same bump rather than spending
    /// a wire break on either alone.
    /// </summary>
    public static ulong FoldRange(ulong h, byte[] data, int from, int to)
    {
        int i = from;
        unsafe
        {
            fixed (byte* basePtr = data)
            {
                for (int limit = to - 7; i < limit; i += 8)
                    h = (h ^ *(ulong*)(basePtr + i)) * Prime;
                for (; i < to; i++)
                    h = (h ^ basePtr[i]) * Prime;
            }
        }
        return h;
    }

    /// <summary>
    /// Per-bucket hashes over ALL of the buffer — buckets deliberately ignore every exclusion,
    /// because their whole purpose is to find the bytes worth excluding. Bucket slicing is a pure
    /// function of the domain size (see <c>DivergenceLearner.BucketSpan</c>), so every peer
    /// produces vectors that line up index for index.
    /// </summary>
    public static bool FillBuckets(byte[] data, int length, uint[]? buckets)
    {
        if (buckets == null) return false;
        long bucketSpan = Session.DivergenceLearner.BucketSpan(length, buckets.Length);
        for (int i = 0; i < buckets.Length; i++)
        {
            long start = i * bucketSpan;
            if (start >= length) { buckets[i] = 2166136261u; continue; } // past the end: constant
            long end = Math.Min(length, start + bucketSpan);
            ulong h = FoldRange(Basis, data, (int)start, (int)end);
            buckets[i] = (uint)(h ^ (h >> 32));
        }
        return true;
    }

    /// <summary>
    /// Which hash path ran, and which bytes it skipped, folded into the seed.
    ///
    /// Two peers are expected to agree on all of them — and if they ever did not, the point is that
    /// the resulting values must not be comparable. A visible disagreement resyncs and names the
    /// paths in the log; a plausible one would compare unlike byte sets forever.
    /// </summary>
    public static ulong SeedWithExclusions(ulong h, int pathTag, long[] exRanges, ulong extraSeed)
    {
        h = (h ^ (uint)pathTag) * Prime;
        h = (h ^ (ulong)exRanges.Length) * Prime;
        foreach (long bound in exRanges) h = (h ^ (ulong)bound) * Prime;
        h = (h ^ extraSeed) * Prime;
        return h;
    }
}
