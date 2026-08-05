using System;
using System.Linq;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Who still owes an applied-acknowledgement, and the exact moment nobody does.
///
/// The release is the dangerous edge. Fire it early and every peer resumes on a baseline one of
/// them has not imported — a desync created at the moment the session was recovering from a desync,
/// which is the worst outcome this whole subsystem has. Fire it late, or never, and the survivors
/// freeze until a watchdog ends a session that had already finished rebuilding.
///
/// It was a per-peer int written in three loops and read back by a fourth that walked every link
/// looking for one still set. None of the four had a test.
/// </summary>
public class ApplyBarrierTests
{
    [Fact]
    public void AFreshBarrierWaitsForNobody()
    {
        var barrier = new ApplyBarrier();
        Assert.False(barrier.IsWaiting);
        Assert.Equal(0, barrier.Epoch);
        Assert.Equal(0, barrier.EpochOwedBy(1));
        Assert.Equal(ApplyAck.Ignored, barrier.Applied(1, 4));
    }

    [Fact]
    public void EverySeatOwesTheEpochUntilItAnswers()
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 1, 2, 3 }, 7);

        Assert.True(barrier.IsWaiting);
        Assert.Equal(7, barrier.Epoch);
        Assert.Equal(3, barrier.OutstandingCount);
        foreach (int seat in new[] { 1, 2, 3 }) Assert.Equal(7, barrier.EpochOwedBy(seat));
        Assert.Equal(0, barrier.EpochOwedBy(0));   // the host owes itself nothing
    }

    /// <summary>
    /// The release comes on the last acknowledgement and not one earlier — the property the whole
    /// type exists for.
    /// </summary>
    [Fact]
    public void NobodyIsReleasedUntilTheLastSeatHasApplied()
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 1, 2, 3 }, 7);

        Assert.Equal(ApplyAck.Recorded, barrier.Applied(2, 7));
        Assert.Equal(ApplyAck.Recorded, barrier.Applied(1, 7));
        Assert.True(barrier.IsWaiting);
        Assert.Equal(ApplyAck.Complete, barrier.Applied(3, 7));
        Assert.False(barrier.IsWaiting);
    }

    [Fact]
    public void TheReleaseIsReportedExactlyOnce()
    {
        // Both callers of Complete send a RESUME to every peer. A second Complete would send it
        // twice for one rebuild.
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 1 }, 7);
        Assert.Equal(ApplyAck.Complete, barrier.Applied(1, 7));
        Assert.Equal(ApplyAck.Ignored, barrier.Applied(1, 7));
    }

    [Fact]
    public void ADuplicateAcknowledgementChangesNothing()
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 1, 2 }, 7);
        Assert.Equal(ApplyAck.Recorded, barrier.Applied(1, 7));
        Assert.Equal(ApplyAck.Ignored, barrier.Applied(1, 7));   // ...and did NOT release
        Assert.True(barrier.IsWaiting);
        Assert.Equal(1, barrier.OutstandingCount);
    }

    /// <summary>
    /// An acknowledgement for the wrong epoch is not an acknowledgement.
    ///
    /// A straggler from a superseded rebuild carries an older epoch. Counted, it would release a
    /// rebuild still in flight — peers resuming on a baseline they are still importing.
    /// </summary>
    [Theory]
    [InlineData(6)]     // the rebuild before this one
    [InlineData(8)]     // impossible, but the wire is not trusted to agree
    [InlineData(0)]
    public void AnAcknowledgementForAnotherEpochIsIgnored(int epoch)
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 1 }, 7);
        Assert.Equal(ApplyAck.Ignored, barrier.Applied(1, epoch));
        Assert.True(barrier.IsWaiting);
    }

    [Fact]
    public void AnAcknowledgementFromASeatWeAreNotWaitingOnIsIgnored()
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 1, 2 }, 7);
        Assert.Equal(ApplyAck.Ignored, barrier.Applied(3, 7));
        Assert.Equal(2, barrier.OutstandingCount);
    }

    /// <summary>
    /// A new baseline replaces the wait it supersedes rather than adding to it.
    ///
    /// Adding would leave a seat owing an epoch nobody is going to acknowledge, and the session
    /// would sit at that barrier until a watchdog ended it — for a rebuild that had completed.
    /// </summary>
    [Fact]
    public void ANewEpochSupersedesTheWaitBeforeIt()
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 1, 2, 3 }, 7);
        barrier.Applied(1, 7);

        barrier.Expect(new[] { 2, 3 }, 8);   // P1 is gone; a fresh baseline goes to the rest
        Assert.Equal(8, barrier.Epoch);
        Assert.Equal(2, barrier.OutstandingCount);
        Assert.Equal(0, barrier.EpochOwedBy(1));
        Assert.Equal(ApplyAck.Ignored, barrier.Applied(1, 7));   // its old ack is meaningless now

        Assert.Equal(ApplyAck.Recorded, barrier.Applied(2, 8));
        Assert.Equal(ApplyAck.Complete, barrier.Applied(3, 8));
    }

    /// <summary>
    /// A host with no peers is not waiting for anything.
    ///
    /// Both callers check the peer count before arming, but a barrier that answered "waiting" for
    /// an empty set would hold a solo host frozen forever with nobody who could ever release it.
    /// </summary>
    [Fact]
    public void ABarrierWithNoSeatsIsNotAWait()
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(Array.Empty<int>(), 7);
        Assert.False(barrier.IsWaiting);
        Assert.Equal(0, barrier.Epoch);
    }

    [Fact]
    public void EpochZeroIsTheOwesNothingSentinelAndCannotBeWaitedOn()
    {
        // Accepting it would make EpochOwedBy indistinguishable from "owes nothing", and the link
        // watchdog reads exactly that value to decide whether a peer is holding the session up.
        var barrier = new ApplyBarrier();
        Assert.Throws<ArgumentOutOfRangeException>(() => barrier.Expect(new[] { 1 }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => barrier.Expect(new[] { 1 }, -1));
    }

    [Fact]
    public void ClearingAbandonsTheWaitEntirely()
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 1, 2 }, 7);
        barrier.Clear();
        Assert.False(barrier.IsWaiting);
        Assert.Equal(0, barrier.EpochOwedBy(1));
        Assert.Equal(ApplyAck.Ignored, barrier.Applied(1, 7));
    }

    [Fact]
    public void TheOutstandingSeatsAreNameableForALogLine()
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 3, 1, 2 }, 5);
        barrier.Applied(1, 5);
        Assert.Equal(new[] { 2, 3 }, barrier.Outstanding.OrderBy(s => s).ToArray());
    }

    /// <summary>
    /// A repeated seat is one seat.
    ///
    /// The caller builds the set from a peer list; a duplicate entry there would otherwise make the
    /// barrier expect two acknowledgements from one machine, which only ever arrives once.
    /// </summary>
    [Fact]
    public void ASeatListedTwiceIsStillOneAcknowledgement()
    {
        var barrier = new ApplyBarrier();
        barrier.Expect(new[] { 1, 1, 2 }, 7);
        Assert.Equal(2, barrier.OutstandingCount);
        Assert.Equal(ApplyAck.Recorded, barrier.Applied(1, 7));
        Assert.Equal(ApplyAck.Complete, barrier.Applied(2, 7));
    }
}
