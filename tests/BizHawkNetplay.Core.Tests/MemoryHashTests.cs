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
    private const ulong Prime = 1099511628211UL;

    /// <summary>
    /// The eight-lane fold written the obvious way — indexed reads through BitConverter, an
    /// explicit lane array, no pointer arithmetic.
    ///
    /// This is the oracle, and it is worth being clear about what it does and does not prove. It is
    /// a transcription of the same algorithm, so it cannot catch a mistake in the DESIGN. What it
    /// catches is the class of mistake the shipping version is actually exposed to: an off-by-one
    /// in the 64-byte block bound, a lane read at the wrong offset, a tail that starts in the wrong
    /// place. Those are pointer-arithmetic slips, and they are invisible on a round-numbered buffer
    /// — which is why the matrix below is nearly all awkward lengths.
    /// </summary>
    private static ulong ReferenceFold(ulong h, byte[] data, int from, int to)
    {
        var lanes = new ulong[MemoryHash.Lanes];
        for (int k = 0; k < lanes.Length; k++) lanes[k] = (h ^ (ulong)k) * Prime;

        int i = from;
        int block = 8 * MemoryHash.Lanes;
        while (i + block <= to)
        {
            for (int k = 0; k < lanes.Length; k++)
                lanes[k] = (lanes[k] ^ BitConverter.ToUInt64(data, i + 8 * k)) * Prime;
            i += block;
        }
        for (; i < to; i++) lanes[0] = (lanes[0] ^ data[i]) * Prime;

        ulong a = (lanes[0] ^ lanes[1]) * Prime, b = (lanes[2] ^ lanes[3]) * Prime,
              c = (lanes[4] ^ lanes[5]) * Prime, d = (lanes[6] ^ lanes[7]) * Prime;
        return (((a ^ b) * Prime) ^ ((c ^ d) * Prime)) * Prime;
    }

    /// <summary>The single-chain fold protocol 23 and earlier shipped. Kept only to show the two
    /// disagree, which is the fact that made this a wire break.</summary>
    private static ulong LegacySingleChainFold(ulong h, byte[] data, int from, int to)
    {
        int i = from;
        for (int limit = to - 7; i < limit; i += 8)
            h = (h ^ BitConverter.ToUInt64(data, i)) * Prime;
        for (; i < to; i++)
            h = (h ^ data[i]) * Prime;
        return h;
    }

    private static byte[] Noise(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>
    /// The pointer-arithmetic version agrees with the obvious one, everywhere it is awkward.
    ///
    /// Driven over unaligned starts and odd lengths on purpose. Exclusion ranges cut the buffer at
    /// whatever offsets the divergence learner measured, so the loop is routinely handed a span
    /// that neither starts nor ends on a 64-byte block boundary — and the tail handling is exactly
    /// where a pointer rewrite goes wrong without showing it on a round-numbered buffer.
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

    /// <summary>
    /// The lane fold and the single chain protocol 23 shipped disagree — which is the entire
    /// reason this needed a protocol bump rather than being a free optimization like the pointer
    /// read that preceded it.
    ///
    /// Stated as a test because it is the fact a future reader is most likely to doubt: two
    /// versions of this tool computing different numbers for identical memory is exactly the
    /// failure the version check exists to prevent, and it is silent if the check is ever relaxed.
    /// </summary>
    [Fact]
    public void TheLaneFoldDisagreesWithTheSingleChainItReplaced()
    {
        var data = Noise(4096, 0xB10C);
        Assert.NotEqual(LegacySingleChainFold(0UL, data, 0, data.Length),
            MemoryHash.FoldRange(0UL, data, 0, data.Length));
    }

    /// <summary>
    /// The property lanes could plausibly have cost, checked rather than assumed: every single-byte
    /// difference still moves the hash.
    ///
    /// With eight lanes a byte at offset i only ever touches lane (i/8) mod 8, so the worry is a
    /// difference that reaches the output through fewer multiplies and cancels. It does not — but
    /// "it does not" is a claim, and detecting that two states differ is the whole job here, so it
    /// gets driven over every offset in a buffer rather than argued.
    /// </summary>
    [Fact]
    public void EverySingleByteDifferenceStillMovesTheHash()
    {
        var data = Noise(1024 + 37, 0xD1FF);   // deliberately not a whole number of blocks
        uint baseline = MemoryHash.Fnv1a64(data, data.Length, MemoryHash.PathPtr, new long[0], 0);

        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= 0x01;                    // the smallest change there is
            uint moved = MemoryHash.Fnv1a64(data, data.Length, MemoryHash.PathPtr, new long[0], 0);
            data[i] ^= 0x01;
            Assert.True(baseline != moved, $"flipping the low bit of byte {i} left the hash at {moved:X8}");
        }
    }

    /// <summary>An empty span must be a pure function of the seed, and the same one every time — a
    /// range excluded down to nothing is ordinary once a learned mask covers a whole region.</summary>
    [Fact]
    public void AnEmptySpanIsStableAndSeedDetermined()
    {
        var data = Noise(1024, 7);
        Assert.Equal(MemoryHash.FoldRange(12345UL, data, 500, 500),
                     MemoryHash.FoldRange(12345UL, data, 900, 900));
        Assert.NotEqual(MemoryHash.FoldRange(12345UL, data, 500, 500),
                        MemoryHash.FoldRange(54321UL, data, 500, 500));
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
    // Protocol 24 values. They differ from protocol 23's (161449041 / 2362860795) because the fold
    // went to eight lanes, which is the whole reason 24 exists.
    [InlineData(0, 1761017244u)]
    [InlineData(1, 2229619052u)]
    public void KnownValuesArePinnedBecauseTheyCrossTheWire(int variant, uint expected)
    {
        var data = new byte[512];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + variant);
        Assert.Equal(expected,
            MemoryHash.Fnv1a64(data, data.Length, MemoryHash.PathPtr, new long[] { 16, 48 }, 0xABCD));
    }
}
