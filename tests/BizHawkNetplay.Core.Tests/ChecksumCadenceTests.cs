using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The checksum cadence policy: fast hashes buy fast detection, slow ones stay rare, and a figure
/// off the wire is either sane or replaced with the default — a wrong interval would not merely
/// misbehave, it would silently stop desync detection completing.
/// </summary>
public class ChecksumCadenceTests
{
    private const double Ntsc = 16.639;

    [Fact]
    public void FastHashesGetTheFloor()
    {
        // A light core's ~0.1ms hash, and N64's ~2ms fast-path hash, both afford once a second.
        Assert.Equal(ChecksumCadence.MinIntervalFrames, ChecksumCadence.Choose(0.1, Ntsc));
        Assert.Equal(ChecksumCadence.MinIntervalFrames, ChecksumCadence.Choose(2.0, Ntsc));
    }

    [Fact]
    public void ExpensiveHashesStayRare()
    {
        // The SHA fallback's ~38ms hash lands past the historical figure and gets the ceiling.
        Assert.Equal(ChecksumCadence.MaxIntervalFrames, ChecksumCadence.Choose(60.0, Ntsc));
        // A middling one lands in between, at the amortized budget.
        int mid = ChecksumCadence.Choose(10.0, Ntsc);
        Assert.InRange(mid, ChecksumCadence.MinIntervalFrames + 1, ChecksumCadence.MaxIntervalFrames - 1);
        // The budget property itself: one hash per interval, amortized under the share.
        Assert.True(10.0 / mid <= ChecksumCadence.BudgetShare * Ntsc * 1.01);
    }

    [Fact]
    public void GarbageMeasurementsGetTheHistoricalDefault()
    {
        Assert.Equal(ChecksumCadence.DefaultIntervalFrames, ChecksumCadence.Choose(0, Ntsc));
        Assert.Equal(ChecksumCadence.DefaultIntervalFrames, ChecksumCadence.Choose(-1, Ntsc));
        Assert.Equal(ChecksumCadence.DefaultIntervalFrames, ChecksumCadence.Choose(double.NaN, Ntsc));
        Assert.Equal(ChecksumCadence.DefaultIntervalFrames, ChecksumCadence.Choose(2.0, 0));
    }

    [Fact]
    public void WireValuesOutsideTheRangeAreReplaced()
    {
        Assert.True(ChecksumCadence.IsAcceptable(ChecksumCadence.MinIntervalFrames));
        Assert.True(ChecksumCadence.IsAcceptable(ChecksumCadence.MaxIntervalFrames));
        Assert.False(ChecksumCadence.IsAcceptable(1));      // a hostile flood
        Assert.False(ChecksumCadence.IsAcceptable(100_000)); // detection effectively off
        Assert.False(ChecksumCadence.IsAcceptable(0));

        var generation = new BizHawkNetplay.Core.Net.SessionGeneration(3UL, 1);
        var sane = HandshakeCodec.EncodeWelcome(1, 2, 2, SyncMode.Lockstep, generation,
            checksumInterval: 120);
        Assert.Equal(120, HandshakeCodec.DecodeChecksumInterval(sane));

        // An old-shape WELCOME (no ckint line) and out-of-range values both read as the default.
        var old = HandshakeCodec.EncodeWelcome(1, 2, 2, SyncMode.Lockstep, generation);
        // EncodeWelcome always writes the line now; simulate absence by decoding unrelated text.
        Assert.Equal(ChecksumCadence.DefaultIntervalFrames,
            HandshakeCodec.DecodeChecksumInterval(System.Text.Encoding.UTF8.GetBytes("port=1\n")));
        Assert.Equal(ChecksumCadence.DefaultIntervalFrames,
            HandshakeCodec.DecodeChecksumInterval(System.Text.Encoding.UTF8.GetBytes("ckint=7\n")));
        _ = old;
    }
}
