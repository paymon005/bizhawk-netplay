using System.Collections.Generic;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// What a host does when checksums disagree.
///
/// The decision was a run of early returns in the tool form, interleaved with the logging and the
/// sending it produced, and none of it was reachable by a test. Two of its rules have been wrong in
/// shipped code — the trend recording sat where a successful deferral skipped it, and the deferral
/// branch's fall-through was the only thing keeping a session with an unreachable donor from
/// staying desynced.
/// </summary>
public class DesyncPolicyTests
{
    private const int Interval = 300;

    /// <summary>A boundary past the learning window, so these cases test the recovery decision
    /// rather than the suppression that outranks it. See
    /// <see cref="ADisagreementInsideTheLearningWindowIsAMeasurement"/> for the other side.</summary>
    private const int PastLearning = (DivergenceLearner.LearnRounds + 1) * Interval;

    /// <summary>A four-player split where <paramref name="hostAlone"/> puts the host in a group of
    /// one against three that agree.</summary>
    private static DesyncPartition Partition(bool hostAlone)
    {
        var reports = hostAlone
            ? new Dictionary<int, uint> { [0] = 0xAAAA, [1] = 0xBBBB, [2] = 0xBBBB, [3] = 0xBBBB }
            : new Dictionary<int, uint> { [0] = 0xBBBB, [1] = 0xBBBB, [2] = 0xBBBB, [3] = 0xAAAA };
        return DesyncPartition.FromReports(600, reports);
    }

    /// <summary>An even split: two against two, where there is no majority to defer to.</summary>
    private static DesyncPartition Tie() => DesyncPartition.FromReports(600,
        new Dictionary<int, uint> { [0] = 0xAAAA, [1] = 0xAAAA, [2] = 0xBBBB, [3] = 0xBBBB });

    private static DesyncOutcome Decide(DesyncPartition? partition, bool defer,
        bool rebuilding = false, double since = 60.0, int frame = PastLearning) =>
        DesyncPolicy.Decide(frame, Interval, rebuilding, since, graceSeconds: 2.0, partition, defer);

    // ---- the two reasons to do nothing ----

    [Fact]
    public void ARebuildAlreadyInFlightOwnsTheRecovery()
    {
        Assert.Equal(DesyncAction.Ignore, Decide(Partition(false), false, rebuilding: true).Action);
    }

    [Fact]
    public void AResyncMomentsOldHasNotHadTimeToProveItself()
    {
        Assert.Equal(DesyncAction.Ignore, Decide(Partition(false), false, since: 1.9).Action);
        Assert.NotEqual(DesyncAction.Ignore, Decide(Partition(false), false, since: 2.0).Action);
    }

    /// <summary>
    /// Inside the learning window a disagreement is the measurement, not an emergency.
    ///
    /// Right after a rebuild every peer stands on byte-identical memory, so a boundary that
    /// disagrees here means machine-produced bytes. Resyncing instead restarts the same learning
    /// from another identical baseline — forever, which is the resync loop above-native N64 used
    /// to be.
    /// </summary>
    [Fact]
    public void ADisagreementInsideTheLearningWindowIsAMeasurement()
    {
        for (int round = 1; round <= DivergenceLearner.LearnRounds; round++)
        {
            int frame = round * Interval;
            Assert.True(DesyncPolicy.IsMeasuring(frame, Interval), $"frame {frame} should be a learn frame");
            Assert.Equal(DesyncAction.Measuring, Decide(Partition(false), false, frame: frame).Action);
        }
    }

    [Fact]
    public void TheFirstBoundaryPastTheWindowRecoversNormally()
    {
        Assert.False(DesyncPolicy.IsMeasuring(PastLearning, Interval));
        Assert.Equal(DesyncAction.ResyncFromHost,
            Decide(Partition(false), false, frame: PastLearning).Action);
    }

    /// <summary>
    /// Measuring outranks the grace window and an in-flight rebuild.
    ///
    /// Order matters: a learn boundary landing inside the grace window must still read as a
    /// measurement, not as "ignore", because the caller logs the two differently and only one of
    /// them means the learner is working.
    /// </summary>
    [Fact]
    public void MeasuringIsDecidedBeforeTheReasonsToDoNothing()
    {
        int learn = DivergenceLearner.LearnRounds * Interval;
        Assert.Equal(DesyncAction.Measuring,
            Decide(Partition(false), false, rebuilding: true, since: 0.0, frame: learn).Action);
    }

    // ---- who recovers ----

    [Fact]
    public void WithoutTheOptInTheHostAlwaysRecoversFromItsOwnState()
    {
        var outcome = Decide(Partition(hostAlone: true), defer: false);
        Assert.Equal(DesyncAction.ResyncFromHost, outcome.Action);
        Assert.Equal(-1, outcome.DonorPort);
        // ...and the caller is told to say so, because it is about to distribute the minority's.
        Assert.True(outcome.HostIsOutvoted);
    }

    [Fact]
    public void AnOutvotedHostThatOptedInAsksTheMajority()
    {
        var outcome = Decide(Partition(hostAlone: true), defer: true);
        Assert.Equal(DesyncAction.AskDonor, outcome.Action);
        Assert.Equal(1, outcome.DonorPort);   // lowest seat in the largest group
        Assert.True(outcome.HostIsOutvoted);
    }

    [Fact]
    public void AHostInTheMajorityRecoversFromItsOwnStateAndOwesNoApology()
    {
        var outcome = Decide(Partition(hostAlone: false), defer: true);
        Assert.Equal(DesyncAction.ResyncFromHost, outcome.Action);
        Assert.False(outcome.HostIsOutvoted);
    }

    /// <summary>
    /// A tie is not a majority. Handing authority to one half of an even split would be inventing
    /// a verdict the evidence does not support.
    /// </summary>
    [Fact]
    public void AnEvenSplitIsNotAMajorityToDeferTo()
    {
        var outcome = Decide(Tie(), defer: true);
        Assert.Equal(DesyncAction.ResyncFromHost, outcome.Action);
        Assert.False(outcome.HostIsOutvoted);
    }

    /// <summary>
    /// No partition is not a reason to skip recovery.
    ///
    /// The ledger cannot always describe the split. That costs the log a sentence naming who
    /// disagreed with whom; it must not cost the session its convergence.
    /// </summary>
    [Fact]
    public void AnUndescribedSplitStillRecovers()
    {
        var outcome = Decide(null, defer: true);
        Assert.Equal(DesyncAction.ResyncFromHost, outcome.Action);
        Assert.Equal(-1, outcome.DonorPort);
        Assert.False(outcome.HostIsOutvoted);
    }

    /// <summary>
    /// Every path that is not Ignore or Measuring ends in a recovery.
    ///
    /// The one thing this decision must never do is leave a session desynced with nothing
    /// happening — which is what an unreachable donor would have done, had the caller not fallen
    /// through. Pinned across the whole input space rather than case by case.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void EveryActionableDesyncEndsInARecovery(bool hostAlone, bool defer)
    {
        var outcome = Decide(Partition(hostAlone), defer);
        Assert.True(outcome.Action == DesyncAction.AskDonor
                 || outcome.Action == DesyncAction.ResyncFromHost,
            $"a desync resolved as {outcome.Action}, which leaves the session diverged");
        if (outcome.Action == DesyncAction.AskDonor)
            Assert.InRange(outcome.DonorPort, 0, HandshakeCodec.MaxPlayers - 1);
    }
}
