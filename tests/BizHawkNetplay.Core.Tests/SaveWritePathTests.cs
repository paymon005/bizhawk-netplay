using System.IO;
using System.Linq;
using System.Text;
using BizHawkNetplay.Core.Emu;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The savestate write-path measurement — a diagnostic that exists to settle one question with a
/// number instead of an argument.
///
/// On a heavy core the snapshot is the largest term in the rollback budget: N64 measures ~6.1ms
/// against a ~3.5ms frame, so at depth 2 it is roughly two thirds of a repair. Whether any of that
/// is recoverable depends on how the core chooses to write, which nothing in this repository can
/// know. Four-byte fields through a BinaryWriter run at ~260 MiB/s on .NET Framework; 4KiB blocks
/// run at ~6,900. Same bytes, twenty-six times the cost.
///
/// The measurement is only worth having if it cannot mislead, hence these: the histogram has to
/// count what actually happened, the replay plan has to reproduce the shape it recorded, and the
/// verdict has to say "nothing to win" when there is nothing to win.
/// </summary>
public class SaveWritePathTests
{
    private static WriteSizeHistogram Record(params int[] writeSizes)
    {
        var histogram = new WriteSizeHistogram();
        using var sink = new MemoryStream();
        using var measuring = new MeasuringStream(sink, histogram);
        using var writer = new BinaryWriter(measuring, Encoding.UTF8, leaveOpen: true);
        foreach (int size in writeSizes) writer.Write(new byte[size], 0, size);
        writer.Flush();
        return histogram;
    }

    [Fact]
    public void ItCountsWhatWasActuallyWritten()
    {
        var h = Record(4, 4, 4, 8192, 100);
        Assert.Equal(5, h.Writes);
        Assert.Equal(4 + 4 + 4 + 8192 + 100, h.Bytes);
        Assert.Equal(8192, h.LargestWrite);
    }

    /// <summary>
    /// The bytes still reach the stream underneath. A measuring decorator that quietly dropped
    /// writes would corrupt the savestate it was measuring, which is the one thing it must not do
    /// — and the pooled buffer it wraps is the same one the rollback ring uses.
    /// </summary>
    [Fact]
    public void ItForwardsEveryByteToTheStreamUnderneath()
    {
        var histogram = new WriteSizeHistogram();
        using var sink = new MemoryStream();
        using (var measuring = new MeasuringStream(sink, histogram))
        using (var writer = new BinaryWriter(measuring, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x11223344);
            writer.Write((byte)0xAB);
            writer.Write(new byte[64], 0, 64);
            writer.Flush();
        }
        Assert.Equal(4 + 1 + 64, sink.Length);
        Assert.Equal(4 + 1 + 64, histogram.Bytes);
    }

    /// <summary>
    /// Single bytes go through <c>WriteByte</c> rather than the array overload, so counting only
    /// the latter would miss a core writing one byte at a time — which is precisely the pattern
    /// most worth finding, and the one that would look like a suspiciously empty histogram.
    /// </summary>
    [Fact]
    public void SingleByteWritesAreCountedToo()
    {
        var histogram = new WriteSizeHistogram();
        using var sink = new MemoryStream();
        using var measuring = new MeasuringStream(sink, histogram);
        for (int i = 0; i < 1000; i++) measuring.WriteByte((byte)i);

        Assert.Equal(1000, histogram.Writes);
        Assert.Equal(1000, histogram.Bytes);
        Assert.Equal(1, histogram.LargestWrite);
        Assert.Equal(0.0, histogram.ByteShareAtOrAbove(4096));
    }

    /// <summary>
    /// The share is of BYTES, not of calls — because that is what the cost tracks. A million
    /// four-byte writes plus one 16 MiB write is "mostly small" by call count and almost entirely
    /// large by cost, and getting this backwards would invert the verdict.
    /// </summary>
    [Fact]
    public void TheLargeWriteShareIsMeasuredInBytesNotCalls()
    {
        var h = new WriteSizeHistogram();
        for (int i = 0; i < 1_000_000; i++) h.Note(4);   // 4 MiB in a million calls
        h.Note(16 * 1024 * 1024);                        // 16 MiB in one

        Assert.True(h.ByteShareAtOrAbove(4096) > 0.79,
            $"by bytes this is overwhelmingly large writes, got {h.ByteShareAtOrAbove(4096):P0}");
    }

    /// <summary>The replay plan has to reproduce the shape it recorded, or the timing comparison
    /// built on it is measuring a different workload than the core produced.</summary>
    [Fact]
    public void TheReplayPlanReproducesTheRecordedShape()
    {
        var h = Record(4, 4, 4, 4, 64, 64, 8192, 1048576);
        var plan = h.ReplayPlan().ToList();

        Assert.Equal(h.Writes, plan.Sum(p => p.Count));
        long replayed = plan.Sum(p => p.Size * p.Count);
        Assert.True(replayed >= h.Bytes * 0.99 && replayed <= h.Bytes * 1.01,
            $"replay covers {replayed} bytes against the recorded {h.Bytes}");
        Assert.All(plan, p => Assert.True(p.Size >= 1));
    }

    [Fact]
    public void AnEmptyHistogramIsHarmless()
    {
        var h = new WriteSizeHistogram();
        Assert.Equal(0, h.Writes);
        Assert.Equal(0.0, h.ByteShareAtOrAbove(4096));
        Assert.Empty(h.ReplayPlan());
        Assert.NotEqual("", h.Describe());
    }

    /// <summary>
    /// The verdict must say there is nothing to win when there is nothing to win. A diagnostic that
    /// always finds something is worse than none: it would send someone rewriting a write path that
    /// is already at the memory system, on the strength of a line that could never have said
    /// otherwise.
    /// </summary>
    [Fact]
    public void ABlockWritingCoreIsToldThereIsNothingToWin()
    {
        var h = new WriteSizeHistogram();
        for (int i = 0; i < 16; i++) h.Note(1024 * 1024);

        string verdict = SaveWritePathVerdict.Describe(h, actualMs: 6.1, replayMs: 2.3, floorMs: 1.6);
        Assert.Contains("nothing here to win", verdict);
    }

    /// <summary>And the opposite, on the same numbers except the shape.</summary>
    [Fact]
    public void AFieldWritingCoreIsToldWhatIsRecoverable()
    {
        var h = new WriteSizeHistogram();
        for (int i = 0; i < 4_000_000; i++) h.Note(4);

        string verdict = SaveWritePathVerdict.Describe(h, actualMs: 64.0, replayMs: 60.0, floorMs: 1.6);
        Assert.Contains("worth building", verdict);
        Assert.Contains("58.4ms", verdict);   // replay minus floor: what a leaner stream could take
    }

    /// <summary>
    /// A small write pattern that is nonetheless cheap gets no recommendation. The shape alone does
    /// not justify work — the recoverable milliseconds do, and on a small state there are none
    /// worth the risk of touching the save path.
    /// </summary>
    [Fact]
    public void ASmallStateIsLeftAloneEvenWithAnAwkwardShape()
    {
        var h = new WriteSizeHistogram();
        for (int i = 0; i < 20_000; i++) h.Note(4);   // 80KiB: a Hawk core

        string verdict = SaveWritePathVerdict.Describe(h, actualMs: 0.4, replayMs: 0.3, floorMs: 0.02);
        Assert.Contains("the save cost is the core's", verdict);
        Assert.DoesNotContain("worth building", verdict);
    }
}
