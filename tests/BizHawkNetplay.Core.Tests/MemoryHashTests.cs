using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Emu;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The desync checksum's arithmetic, which is a wire format.
///
/// Two peers compare these numbers directly, so every value here is as load-bearing as any byte in
/// a datagram: change one and two correct machines report a desync that is not there, forever, with
/// no version check to catch it. It lived in the adapter — the half no test can reach — purely
/// because that is where the bytes were fetched.
///
/// Two kinds of test, and they answer different questions. The <c>Reference</c> comparisons show the
/// fast read produces bit-for-bit what the plain <c>BitConverter</c> loop produced, which is what
/// makes the optimization free. The known-value tests pin the arithmetic itself, so a future
/// "harmless" refactor that changes the number fails here rather than in someone's session.
/// </summary>
public class MemoryHashTests
{
    // The loop exactly as it was written before the fixed-pointer read replaced it. Kept as the
    // oracle rather than deleted: the claim being made is "same value, faster read", and a claim
    // like that needs the thing it is being compared against to still exist.
    private static ulong ReferenceFold(ulong h, byte[] data, int from, int to)
    {
        const ulong prime = 1099511628211UL;
        int i = from;
        for (int limit = to - 7; i < limit; i += 8)
            h = (h ^ BitConverter.ToUInt64(data, i)) * prime;
        for (; i < to; i++)
            h = (h ^ data[i]) * prime;
        return h;
    }

    private static byte[] Noise(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>
    /// The whole justification for the fixed pointer: identical arithmetic on identical bytes.
    ///
    /// Driven over unaligned starts and odd lengths on purpose. Exclusion ranges cut the buffer at
    /// whatever offsets the divergence learner measured, so the loop is routinely handed a span
    /// that neither starts nor ends on an eight-byte boundary — and the tail handling is exactly
    /// where a pointer rewrite would go wrong without showing it on a round-numbered buffer.
    /// </summary>
    [Fact]
    public void TheFastReadIsBitIdenticalToThePlainOne()
    {
        var data = Noise(64 * 1024 + 37, 0x5EED);
        var starts = new[] { 0, 1, 2, 3, 7, 8, 9, 15, 16, 31, 32, 33, 1023, 4095, 4096, 65536 };
        var ends = new[] { 0, 1, 7, 8, 9, 63, 64, 65, 127, 128, 129, 1024, 4097, 32768, 65535,
                           data.Length - 1, data.Length };

        int compared = 0;
        foreach (int from in starts)
            foreach (int to in ends)
            {
                if (to < from) continue;
                ulong expected = ReferenceFold(1469598103934665603UL, data, from, to);
                ulong actual = MemoryHash.FoldRange(1469598103934665603UL, data, from, to);
                Assert.True(expected == actual,
                    $"[{from},{to}) differed: {expected:X16} vs {actual:X16}");
                compared++;
            }

        Assert.True(compared > 100, $"only {compared} spans compared — the matrix collapsed");
    }

    /// <summary>An empty span must be the identity, not a step. A range excluded down to nothing
    /// is ordinary once a learned mask covers a whole region.</summary>
    [Fact]
    public void AnEmptySpanChangesNothing()
    {
        var data = Noise(1024, 7);
        Assert.Equal(12345UL, MemoryHash.FoldRange(12345UL, data, 500, 500));
    }

    /// <summary>
    /// Whole-buffer hashes across every exclusion shape, against the reference read.
    ///
    /// Exclusions are where the two halves meet: the seed folds in the ranges, and the fold then
    /// skips them. Both have to agree about which bytes were left out or the seed describes one
    /// byte set and the sum describes another.
    /// </summary>
    [Theory]
    [InlineData(new long[0])]
    [InlineData(new long[] { 0, 64 })]                       // leading
    [InlineData(new long[] { 960, 1024 })]                   // trailing
    [InlineData(new long[] { 100, 163 })]                    // odd length, unaligned both ends
    [InlineData(new long[] { 0, 1024 })]                     // everything
    [InlineData(new long[] { 8, 16, 100, 163, 900, 1000 })]  // several
    [InlineData(new long[] { 900, 5000 })]                   // runs past the end
    public void ExclusionsAgreeBetweenTheSeedAndTheFold(long[] ranges)
    {
        var data = Noise(1024, 0xC0FFEE);

        // The reference whole-buffer hash, built from the same skipping rule but the plain read.
        ulong h = MemoryHash.SeedWithExclusions(14695981039346656037UL, MemoryHash.PathPtr, ranges, 99);
        long cursor = 0;
        for (int r = 0; r < ranges.Length; r += 2)
        {
            long start = Math.Min(ranges[r], data.Length);
            long end = Math.Min(ranges[r + 1], data.Length);
            if (start > cursor) h = ReferenceFold(h, data, (int)cursor, (int)start);
            if (end > cursor) cursor = end;
        }
        if (cursor < data.Length) h = ReferenceFold(h, data, (int)cursor, data.Length);
        uint expected = (uint)(h ^ (h >> 32));

        Assert.Equal(expected,
            MemoryHash.Fnv1a64(data, data.Length, MemoryHash.PathPtr, ranges, 99));
    }

    /// <summary>
    /// A different set of excluded ranges must give a different hash even when the surviving bytes
    /// are identical — that is what the seed is for. Two peers that disagree about what to skip
    /// have to produce obviously different values rather than plausible ones.
    /// </summary>
    [Fact]
    public void DisagreeingAboutWhatToSkipGivesAnObviouslyDifferentHash()
    {
        var data = new byte[1024];             // all zeroes: the excluded bytes are the same bytes
        uint a = MemoryHash.Fnv1a64(data, data.Length, MemoryHash.PathPtr, new long[] { 0, 64 }, 0);
        uint b = MemoryHash.Fnv1a64(data, data.Length, MemoryHash.PathPtr, new long[] { 64, 128 }, 0);
        Assert.NotEqual(a, b);
    }

    /// <summary>Each read path is tagged, so a peer that reached the same bytes a different way
    /// cannot compare its answer as though the two described the same thing.</summary>
    [Fact]
    public void EachReadPathHashesDifferently()
    {
        var data = Noise(256, 11);
        var seen = new HashSet<uint>();
        foreach (int path in new[] { MemoryHash.PathPtr, MemoryHash.PathArray, MemoryHash.PathBulk,
                                     MemoryHash.PathWord, MemoryHash.PathClosure })
            Assert.True(seen.Add(MemoryHash.Fnv1a64(data, data.Length, path, new long[0], 0)),
                $"path {path} collided with another path");
        Assert.Equal(5, seen.Count);
    }

    /// <summary>Buckets ignore exclusions by design — they exist to FIND the bytes worth excluding,
    /// so a bucket that skipped them could never see the divergence that justifies the mask.</summary>
    [Fact]
    public void BucketsCoverTheWholeBufferAndLineUpAcrossPeers()
    {
        var data = Noise(4096, 3);
        var mine = new uint[64];
        var theirs = new uint[64];
        Assert.True(MemoryHash.FillBuckets(data, data.Length, mine));
        Assert.True(MemoryHash.FillBuckets(data, data.Length, theirs));
        Assert.Equal(mine, theirs);

        // One byte changed must move exactly one bucket, or the vector cannot localise anything.
        data[2000] ^= 0xFF;
        var after = new uint[64];
        MemoryHash.FillBuckets(data, data.Length, after);
        int moved = 0;
        for (int i = 0; i < mine.Length; i++) if (mine[i] != after[i]) moved++;
        Assert.Equal(1, moved);
    }

    [Fact]
    public void NoBucketSinkIsHarmless() =>
        Assert.False(MemoryHash.FillBuckets(Noise(64, 1), 64, null));

    /// <summary>
    /// The wire format, pinned.
    ///
    /// These constants have no meaning beyond "what this code produced on the day the values became
    /// something peers compare". That is the point: they are here so a refactor that quietly changes
    /// the arithmetic fails in CI rather than as a phantom desync between two releases, which no
    /// version check would catch because the protocol number would not have moved.
    ///
    /// If one of these ever legitimately changes, the protocol version must change with it.
    ///
    /// Written in decimal rather than hex deliberately — these get compared against a failure
    /// message, and a hand-converted hex constant is one transcription slip away from pinning the
    /// wrong number. They are identical on .NET Framework 4.8 and .NET 10, which a value that
    /// crosses between machines has to be.
    /// </summary>
    [Theory]
    [InlineData(0, 161449041u)]
    [InlineData(1, 2362860795u)]
    public void KnownValuesArePinnedBecauseTheyCrossTheWire(int variant, uint expected)
    {
        var data = new byte[512];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + variant);
        Assert.Equal(expected,
            MemoryHash.Fnv1a64(data, data.Length, MemoryHash.PathPtr, new long[] { 16, 48 }, 0xABCD));
    }
}
