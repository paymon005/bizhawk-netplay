using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BizHawkNetplay.Core.Emu;

/// <summary>
/// How a core writes its savestate, and what that shape costs us rather than it.
///
/// <b>The question this exists to settle.</b> On N64 a savestate measures ~6.1ms for ~16.7MiB,
/// which is about 2.7GB/s — well under what merely moving those bytes costs. The savestate is the
/// single largest term in the rollback budget on a heavy core (at depth 2 with no keyframe spacing
/// it is roughly two thirds of a repair, against one third for the emulation), so the difference
/// between "that is the core gathering its state" and "that is our stream absorbing the writes" is
/// worth several milliseconds of prediction depth.
///
/// It cannot be answered by reasoning, because it depends entirely on how the core chooses to
/// write. <c>BinaryWriter</c> over a <c>MemoryStream</c> pays per-call validation, so a core
/// emitting four-byte fields pays it millions of times and a core emitting whole memory regions
/// pays it a handful. Measured on this machine over 16.7MiB:
/// <code>
///                             net48       net10
///   4-byte fields            64.3 ms     42.6 ms
///   64-byte blocks            8.0 ms      6.8 ms
///   4KiB+ blocks              2.4 ms      2.4 ms
///   raw BlockCopy (floor)     1.6 ms
/// </code>
/// A 6.1ms save therefore sits between the 4KiB-block figure and the 64-byte one, and which end it
/// leans toward decides whether there is anything here to win.
/// </summary>
public sealed class WriteSizeHistogram
{
    /// <summary>Lower bound of each bucket. Chosen around the throughput cliffs above rather than
    /// as round numbers: the interesting boundary is 4KiB, where the per-call cost stops
    /// mattering.</summary>
    public static readonly int[] BucketFloors = { 1, 8, 64, 512, 4096, 65536, 1024 * 1024 };

    private readonly long[] _writes = new long[BucketFloors.Length];
    private readonly long[] _bytes = new long[BucketFloors.Length];

    /// <summary>Total calls the core made.</summary>
    public long Writes { get; private set; }

    /// <summary>Total bytes it wrote.</summary>
    public long Bytes { get; private set; }

    /// <summary>Largest single write, which is usually the one that tells you what the core is
    /// doing — a whole memory region shows up here as megabytes.</summary>
    public int LargestWrite { get; private set; }

    public void Note(int count)
    {
        if (count <= 0) return;
        Writes++;
        Bytes += count;
        if (count > LargestWrite) LargestWrite = count;
        _writes[BucketOf(count)]++;
        _bytes[BucketOf(count)] += count;
    }

    private static int BucketOf(int count)
    {
        int at = 0;
        for (int i = 1; i < BucketFloors.Length; i++) if (count >= BucketFloors[i]) at = i;
        return at;
    }

    public long WritesIn(int bucket) => _writes[bucket];
    public long BytesIn(int bucket) => _bytes[bucket];

    /// <summary>
    /// Fraction of BYTES that arrived in writes of at least <paramref name="size"/>.
    ///
    /// Bytes rather than calls, because that is what the cost tracks: a million four-byte writes
    /// and one 16MiB write are both "mostly small" by call count and nothing alike in cost.
    /// </summary>
    public double ByteShareAtOrAbove(int size)
    {
        if (Bytes == 0) return 0;
        long at = 0;
        for (int i = 0; i < BucketFloors.Length; i++) if (BucketFloors[i] >= size) at += _bytes[i];
        return (double)at / Bytes;
    }

    /// <summary>
    /// A synthetic write sequence with the same shape, for replaying the pattern without the core.
    ///
    /// Each bucket becomes its own count of writes at its own MEAN size, which reproduces both the
    /// call count and the byte total closely enough for a timing comparison — the cost cliffs are
    /// an order of magnitude apart, so being off by a factor inside a bucket does not move the
    /// answer.
    /// </summary>
    public IEnumerable<(int Size, long Count)> ReplayPlan()
    {
        for (int i = 0; i < BucketFloors.Length; i++)
        {
            if (_writes[i] == 0) continue;
            int mean = (int)Math.Max(1, _bytes[i] / _writes[i]);
            yield return (mean, _writes[i]);
        }
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append($"{Writes:N0} writes, {Bytes / 1024.0 / 1024.0:F1}MiB, largest {LargestWrite:N0}B");
        for (int i = 0; i < BucketFloors.Length; i++)
        {
            if (_writes[i] == 0) continue;
            string label = i == BucketFloors.Length - 1
                ? $">={BucketFloors[i] / 1024}KiB"
                : $"{BucketFloors[i]}-{BucketFloors[i + 1] - 1}B";
            sb.Append($" | {label}: {_writes[i]:N0} ({_bytes[i] * 100.0 / Math.Max(1, Bytes):F0}% of bytes)");
        }
        return sb.ToString();
    }
}

/// <summary>
/// A stream that records how it was written to and forwards everything to another.
///
/// Deliberately a decorator rather than a change to the pooled buffer: this runs once, from a
/// diagnostics button, and the shipping save path must not grow a branch for a measurement nobody
/// takes during play.
/// </summary>
public sealed class MeasuringStream : Stream
{
    private readonly Stream _inner;

    public MeasuringStream(Stream inner, WriteSizeHistogram histogram)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Histogram = histogram ?? throw new ArgumentNullException(nameof(histogram));
    }

    public WriteSizeHistogram Histogram { get; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Histogram.Note(count);
        _inner.Write(buffer, offset, count);
    }

    /// <summary>Single bytes go through their own path on <c>BinaryWriter</c>, so counting only the
    /// array overload would miss the very pattern most worth finding.</summary>
    public override void WriteByte(byte value)
    {
        Histogram.Note(1);
        _inner.WriteByte(value);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
}

/// <summary>
/// Reads a write-path measurement and says what it means, so the answer is not left to whoever is
/// squinting at the log.
/// </summary>
public static class SaveWritePathVerdict
{
    /// <summary>Below this share of bytes arriving in large writes, the per-call cost is worth
    /// attacking. Set at the throughput cliff: 4KiB blocks already run at the memcpy floor.</summary>
    public const int LargeWriteBytes = 4096;

    /// <summary>
    /// What the measurement concluded.
    ///
    /// <paramref name="actualMs"/> is the real save — the core gathering its state AND our stream
    /// taking it. <paramref name="replayMs"/> is the same write pattern replayed into the same kind
    /// of stream with no core involved, so it is what the stream alone costs.
    /// <paramref name="floorMs"/> is one block copy of the same byte count: the least any
    /// implementation could spend.
    ///
    /// The difference that matters is <c>replayMs - floorMs</c>: that is what a leaner stream could
    /// recover. <c>actualMs - replayMs</c> is the core's own work and is not ours to take.
    ///
    /// <b>The decomposition can fail, and when it does it must say so.</b> If the replay comes out
    /// at or above the real save, the two are not being separated — the state is essentially one
    /// memcpy and every figure here is measuring the same thing to within noise. The first run of
    /// this diagnostic on real N64 hit exactly that and printed "core work ~0.00ms", which reads as
    /// a finding and is not one. Subtracting to a floor of zero is how a measurement lies quietly.
    /// </summary>
    public static string Describe(WriteSizeHistogram histogram,
        double actualMs, double replayMs, double floorMs)
    {
        double recoverable = Math.Max(0, replayMs - floorMs);
        double largeShare = histogram.ByteShareAtOrAbove(LargeWriteBytes);
        bool separated = actualMs > replayMs * 1.05;   // the core's share is above the noise

        string verdict = largeShare >= 0.90
            ? "the core writes in large blocks, so the stream is already at the floor — the save " +
              "cost is the core's own, and no change to the write path can touch it"
            : recoverable >= 1.0
                ? $"about {recoverable:F1}ms of this is the write path rather than the core, " +
                  "so a leaner stream is worth building"
                : "the write path costs little even at this shape — the save cost is the core's";

        string split = separated
            ? $"core work ~{actualMs - replayMs:F2}ms, write path ~{replayMs:F2}ms " +
              $"(recoverable ~{recoverable:F1}ms)"
            : "the replay is not cheaper than the real save, so these three are measuring the same " +
              "memcpy and the core's share cannot be separated from it — which is itself the answer " +
              "when the state arrives in one write";

        return $"save write path: {histogram.Describe()}\n" +
               $"  actual save {actualMs:F2}ms | same pattern, no core {replayMs:F2}ms | " +
               $"block-copy floor {floorMs:F2}ms\n" +
               $"  {largeShare:P0} of bytes in writes >={LargeWriteBytes / 1024}KiB; {split}\n" +
               $"  => {verdict}";
    }
}
