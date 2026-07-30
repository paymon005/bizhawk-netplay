using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The host's desync-detection ledger. The properties under test are exactly the ways checksum
/// aggregation could fabricate a verdict: double-counting one port toward the player total,
/// mixing reports across generations, or resolving the same frame twice from late duplicates.
/// </summary>
public class ChecksumLedgerTests
{
    private static readonly SessionGeneration Gen = new(0xfeedbeefcafe1234UL, 3);

    [Fact]
    public void ResolvesOnlyWhenEveryPlayerReported_AndAgreesOnEqualHashes()
    {
        var ledger = new ChecksumLedger();
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 0, 300, 0xAAAA0001, 3));
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 1, 300, 0xAAAA0001, 3));
        Assert.Equal(ChecksumOutcome.Agreement, ledger.Record(Gen, 2, 300, 0xAAAA0001, 3));
    }

    [Fact]
    public void AnyDisagreement_IsAMismatch()
    {
        var ledger = new ChecksumLedger();
        ledger.Record(Gen, 0, 300, 0xAAAA0001, 3);
        ledger.Record(Gen, 1, 300, 0xBBBB0002, 3);
        Assert.Equal(ChecksumOutcome.Mismatch, ledger.Record(Gen, 2, 300, 0xAAAA0001, 3));
    }

    [Fact]
    public void DuplicateReportFromOnePort_OverwritesInsteadOfCountingTwice()
    {
        // A duplicated packet (or the local player recorded twice) must never stand in for the
        // missing third player and fabricate agreement.
        var ledger = new ChecksumLedger();
        ledger.Record(Gen, 0, 300, 0xAAAA0001, 3);
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 0, 300, 0xAAAA0001, 3));
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 1, 300, 0xAAAA0001, 3));
        // The overwrite also means a port can correct itself before the frame resolves.
        ledger.Record(Gen, 1, 300, 0xCCCC0003, 3);
        Assert.Equal(ChecksumOutcome.Mismatch, ledger.Record(Gen, 2, 300, 0xAAAA0001, 3));
    }

    [Fact]
    public void ResolvedFrame_IsForgotten_SoLateDuplicatesStartFresh()
    {
        var ledger = new ChecksumLedger();
        ledger.Record(Gen, 0, 300, 0xAAAA0001, 2);
        Assert.Equal(ChecksumOutcome.Agreement, ledger.Record(Gen, 1, 300, 0xAAAA0001, 2));
        // A late duplicate of the same frame cannot ride the resolved entry to a second verdict.
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 0, 300, 0xAAAA0001, 2));
    }

    [Fact]
    public void ReportsFromDifferentGenerations_NeverMeet()
    {
        // A stale reader delivering an old-timeline hash for the same frame number must not
        // complete (or contaminate) the current generation's entry.
        var ledger = new ChecksumLedger();
        var oldGen = Gen;
        var newGen = Gen.Next();
        ledger.Record(oldGen, 0, 300, 0xDEAD0001, 2);
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(newGen, 1, 300, 0xAAAA0001, 2));
        // Recording under the new generation retired the old one's partial entry entirely.
        Assert.Equal(0, ledger.OpenFrames(oldGen));
        // And the new generation still needs BOTH its own reports.
        Assert.Equal(ChecksumOutcome.Agreement, ledger.Record(newGen, 0, 300, 0xAAAA0001, 2));
    }

    [Fact]
    public void OutOfRangePort_IsIgnored()
    {
        var ledger = new ChecksumLedger();
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, -1, 300, 1, 2));
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 2, 300, 1, 2)); // ports are 0..count-1
        Assert.Equal(0, ledger.OpenFrames(Gen));
    }

    [Fact]
    public void AbandonedPartialEntries_AreBounded()
    {
        // A peer that never reports must not grow the ledger without bound: once many frames are
        // open, entries far behind the newest report are dropped.
        var ledger = new ChecksumLedger();
        for (int frame = 0; frame < 40 * 300; frame += 300)
            ledger.Record(Gen, 0, frame, 0xAAAA0001, 2);
        Assert.True(ledger.OpenFrames(Gen) <= 33,
            $"ledger holds {ledger.OpenFrames(Gen)} open frames — unbounded growth");
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var ledger = new ChecksumLedger();
        ledger.Record(Gen, 0, 300, 0xAAAA0001, 2);
        ledger.Clear();
        Assert.Equal(0, ledger.OpenFrames(Gen));
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 1, 300, 0xAAAA0001, 2));
    }
}
