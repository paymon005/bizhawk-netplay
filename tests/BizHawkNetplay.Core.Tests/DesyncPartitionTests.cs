using System.Collections.Generic;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Who disagreed with whom. The host stays authoritative for recovery — these tests are about
/// being able to SEE the case where that policy overwrites a majority with the minority's state,
/// which the bare Agreement/Mismatch verdict made invisible.
/// </summary>
public class DesyncPartitionTests
{
    private static SessionGeneration Gen => new(11UL, 1);

    [Fact]
    public void TheHostAloneAgainstThreeIsRecognisedAsOutvoted()
    {
        var reports = new Dictionary<int, uint> { [0] = 0xBBBBBBBB, [1] = 0xAAAAAAAA, [2] = 0xAAAAAAAA, [3] = 0xAAAAAAAA };
        var partition = DesyncPartition.FromReports(600, reports);

        Assert.True(partition.HostIsOutvoted);
        Assert.Equal(1, partition.HostGroupSize);
        Assert.Equal(4, partition.ReportCount);
        // Largest group first, so the log leads with what most machines believe.
        Assert.Equal("P2+P3+P4=AAAAAAAA vs P1=BBBBBBBB", partition.Describe());
    }

    [Fact]
    public void AJoinerAloneLeavesTheHostInTheMajority()
    {
        var reports = new Dictionary<int, uint> { [0] = 0xAAAAAAAA, [1] = 0xAAAAAAAA, [2] = 0xCCCCCCCC, [3] = 0xAAAAAAAA };
        var partition = DesyncPartition.FromReports(600, reports);

        Assert.False(partition.HostIsOutvoted);
        Assert.Equal(3, partition.HostGroupSize);
        Assert.Equal("P1+P2+P4=AAAAAAAA vs P3=CCCCCCCC", partition.Describe());
    }

    [Fact]
    public void AnEvenSplitIsNotCalledAgainstTheHost()
    {
        // Two against two: there is no majority to be outside of, and claiming the host is wrong
        // would be inventing a verdict the evidence does not support.
        var reports = new Dictionary<int, uint> { [0] = 0xAAAAAAAA, [1] = 0xAAAAAAAA, [2] = 0xDDDDDDDD, [3] = 0xDDDDDDDD };
        var partition = DesyncPartition.FromReports(600, reports);

        Assert.False(partition.HostIsOutvoted);
        Assert.Equal(2, partition.HostGroupSize);
    }

    [Fact]
    public void EveryoneDifferingLeavesNobodyOutvoted()
    {
        var reports = new Dictionary<int, uint> { [0] = 1, [1] = 2, [2] = 3, [3] = 4 };
        var partition = DesyncPartition.FromReports(600, reports);

        Assert.False(partition.HostIsOutvoted); // all groups are size 1; none is larger
        Assert.Equal(4, partition.Groups.Count);
    }

    [Fact]
    public void TwoPlayerDisagreementHasNoMajorityEither()
    {
        var reports = new Dictionary<int, uint> { [0] = 0xAAAAAAAA, [1] = 0xBBBBBBBB };
        var partition = DesyncPartition.FromReports(300, reports);

        Assert.False(partition.HostIsOutvoted);
        Assert.Equal("P1=AAAAAAAA vs P2=BBBBBBBB", partition.Describe());
    }

    [Fact]
    public void TheLedgerPublishesThePartitionOnMismatchAndClearsItOnAgreement()
    {
        var ledger = new ChecksumLedger();
        Assert.Null(ledger.LastMismatch);

        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 0, 300, 0xBB, 4, 4));
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 1, 300, 0xAA, 4, 4));
        Assert.Equal(ChecksumOutcome.Pending, ledger.Record(Gen, 2, 300, 0xAA, 4, 4));
        Assert.Equal(ChecksumOutcome.Mismatch, ledger.Record(Gen, 3, 300, 0xAA, 4, 4));

        var partition = ledger.LastMismatch;
        Assert.NotNull(partition);
        Assert.Equal(300, partition!.Frame);
        Assert.True(partition.HostIsOutvoted);

        // A later agreeing boundary must not leave the old split standing as if it were current.
        for (int p = 0; p < 4; p++) ledger.Record(Gen, p, 600, 0xAA, 4, 4);
        Assert.Null(ledger.LastMismatch);
    }
}
