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
    /// Lanes the fold runs in parallel. Fixed, and part of the wire format: two peers folding at
    /// different widths produce different numbers for the same memory.
    /// </summary>
    public const int Lanes = 8;

    /// <summary>
    /// The inner loop, and the hottest code in the session outside the core itself.
    ///
    /// <b>The ceiling this is built around.</b> FNV-1a is <c>h = (h ^ word) * prime</c>: each step
    /// needs the previous <c>h</c>, so a single chain runs at the latency of one 64-bit multiply
    /// per eight bytes no matter how much the CPU could otherwise overlap. Eight independent lanes,
    /// combined once at the end, give the out-of-order engine eight chains to interleave.
    ///
    /// Measured over 8 MiB — N64's RDRAM, the case this exists for — median of 21 runs, and the
    /// spread across four separate sessions of that because the single-chain figures move about:
    /// <code>
    ///                                         net48         net10
    ///   BitConverter, one chain (v0.36.0)   4.8-6.0 ms   1.5-2.4 ms
    ///   fixed pointer, one chain (v0.37.0)  1.5-2.0 ms   1.4-1.5 ms
    ///   fixed pointer, 8 lanes (here)       0.62-0.65ms  0.67-0.71ms
    ///   memcpy of the same buffer, for scale  ~1.1 ms      ~1.3 ms
    /// </code>
    /// <b>Call it 8x against what v0.36.0 shipped and about 2.5x against v0.37.0.</b> The lane
    /// figure is the stable one — it sits near what the memory system will give and barely moves
    /// between runs, which is the sign the dependency chain rather than bandwidth was the ceiling.
    /// It is also FASTER than memcpy of the same buffer, which is not a contradiction: this reads
    /// and does not write.
    ///
    /// That is roughly 4-5ms off every N64 checksum, and double that while divergence learning is
    /// running, on a core whose steady frame already sits near its whole budget.
    ///
    /// Four lanes measure the same as eight, so eight is not buying the last of it — the loop is at
    /// the memory system by then. Eight is kept because it costs nothing over four and leaves the
    /// margin where a wider machine could use it, and because changing the count later is another
    /// protocol break.
    ///
    /// <b>This changes the hash value, which is why it needed a protocol bump.</b> The lane count,
    /// the lane seeding and the combine are all wire format now — a peer that changed any of them
    /// would report a desync against a peer that had not, forever, with nothing to catch it. That
    /// is what <c>MemoryHashTests</c>'s pinned values are guarding.
    ///
    /// <b>Detection strength is unchanged in the way that matters.</b> A byte at offset i lands in
    /// lane (i/8) mod 8 and reaches the output through the combine, so any single-byte divergence
    /// still moves the result; what lanes cost is the property that a difference cannot be undone
    /// by a later difference, and that was never stronger than a hash collision to begin with.
    /// Detecting that two states differ is the whole requirement here — not collision resistance
    /// against an adversary, which a 32-bit value could not offer at any lane count.
    ///
    /// Short spans fold entirely through the tail into lane 0 and still combine all eight, which is
    /// deterministic and therefore fine. It also means the lane win does not apply to a buffer
    /// chopped into many small pieces by exclusions — the learned mask is a handful of ranges, so
    /// the spans that matter are long.
    /// </summary>
    public static ulong FoldRange(ulong h, byte[] data, int from, int to)
    {
        // Lanes seeded apart, each through one FNV step rather than a bare XOR.
        //
        // The bare XOR was wrong and a test caught it: the combine below pairs lanes with XOR, and
        // (h^0) ^ (h^1) cancels h completely — so an empty span returned the same constant whatever
        // it was seeded with, and every span leaked that structure into the result. Multiplication
        // does not distribute over XOR, so one step of the fold makes the lanes genuinely
        // independent functions of the seed.
        ulong h0 = (h ^ 0) * Prime, h1 = (h ^ 1) * Prime, h2 = (h ^ 2) * Prime, h3 = (h ^ 3) * Prime,
              h4 = (h ^ 4) * Prime, h5 = (h ^ 5) * Prime, h6 = (h ^ 6) * Prime, h7 = (h ^ 7) * Prime;
        int i = from;
        unsafe
        {
            fixed (byte* basePtr = data)
            {
                for (int limit = to - 63; i < limit; i += 64)
                {
                    h0 = (h0 ^ *(ulong*)(basePtr + i)) * Prime;
                    h1 = (h1 ^ *(ulong*)(basePtr + i + 8)) * Prime;
                    h2 = (h2 ^ *(ulong*)(basePtr + i + 16)) * Prime;
                    h3 = (h3 ^ *(ulong*)(basePtr + i + 24)) * Prime;
                    h4 = (h4 ^ *(ulong*)(basePtr + i + 32)) * Prime;
                    h5 = (h5 ^ *(ulong*)(basePtr + i + 40)) * Prime;
                    h6 = (h6 ^ *(ulong*)(basePtr + i + 48)) * Prime;
                    h7 = (h7 ^ *(ulong*)(basePtr + i + 56)) * Prime;
                }
                // Everything past the last whole 64-byte block, single-chain into lane 0.
                for (; i < to; i++) h0 = (h0 ^ basePtr[i]) * Prime;
            }
        }
        return Combine(h0, h1, h2, h3, h4, h5, h6, h7);
    }

    /// <summary>
    /// Fold eight lanes back to one. A balanced tree rather than a chain, so no lane's contribution
    /// passes through more multiplies than another's.
    /// </summary>
    private static ulong Combine(ulong h0, ulong h1, ulong h2, ulong h3,
        ulong h4, ulong h5, ulong h6, ulong h7)
    {
        ulong a = (h0 ^ h1) * Prime, b = (h2 ^ h3) * Prime,
              c = (h4 ^ h5) * Prime, d = (h6 ^ h7) * Prime;
        return (((a ^ b) * Prime) ^ ((c ^ d) * Prime)) * Prime;
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
