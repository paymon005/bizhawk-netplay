using System;
using System.Linq;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// A desync, a recovery, and out the other side — driven end to end for two and four players.
///
/// This is the test the whole test-strategy pass wanted and could not have. The sequence lived in
/// six methods of the tool form, three of them running on other threads and hopping back, so
/// nothing could execute it; the pieces each had unit tests and the ORDER they run in had none.
///
/// The order is where the failures live. Two authoritative baselines racing on one generation, a
/// release fired before the last peer imported, a rebuild left claimed after its sequence died —
/// each is a correct-looking call made at the wrong moment.
/// </summary>
public class HostRebuildTests
{
    // ---------------------------------------------------------------- the whole cycle

    [Theory]
    [InlineData(1)]   // two players
    [InlineData(3)]   // four
    [InlineData(7)]   // the documented ceiling
    public void ADesyncRecoversAndEveryoneComesOutOnOneGeneration(int joiners)
    {
        var h = new RebuildHarness(joiners);
        int before = h.Generation.Epoch;

        Assert.Equal(ResyncGate.Start, h.GateDesync());
        Assert.True(h.Begin());
        Assert.True(h.Rebuild.Step == RebuildStep.Packing);
        Assert.True(h.Packed());
        Assert.True(h.Distribute());
        Assert.Equal(RebuildStep.AwaitingApply, h.Rebuild.Step);

        foreach (var joiner in h.Joiners.ToList()) h.Apply(joiner.Seat);

        Assert.Equal(RebuildStep.Idle, h.Rebuild.Step);
        Assert.False(h.Phase.IsRebuilding);
        Assert.True(h.Phase.IsPlaying);
        Assert.Equal(before + 1, h.Generation.Epoch);
        Assert.True(h.AllConverged());
        Assert.Equal(1, h.Budget.Used);
    }

    /// <summary>
    /// Nobody is released until the LAST peer has imported.
    ///
    /// Release early and every peer resumes on a baseline one of them is still loading — a desync
    /// manufactured at the exact moment the session was recovering from one, which is the worst
    /// outcome this subsystem has.
    /// </summary>
    [Fact]
    public void NobodyResumesUntilEveryoneHasImported()
    {
        var h = new RebuildHarness(joiners: 3);
        h.Begin();
        h.Packed();
        h.Distribute();

        Assert.Equal(ApplyAck.Recorded, h.Apply(1));
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("resume"));
        Assert.All(h.Joiners, j => Assert.False(j.Resumed));

        Assert.Equal(ApplyAck.Recorded, h.Apply(2));
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("resume"));

        Assert.Equal(ApplyAck.Complete, h.Apply(3));
        Assert.All(h.Joiners, j => Assert.True(j.Resumed));
    }

    [Fact]
    public void EveryPeerGetsTheBeginAndTheStateBeforeAnyResume()
    {
        var h = new RebuildHarness(joiners: 3);
        h.RunWholeRebuild();

        int firstResume = h.Sent.ToList().FindIndex(s => s.StartsWith("resume"));
        Assert.True(firstResume >= 0, "nothing ever resumed");
        for (int seat = 1; seat <= 3; seat++)
        {
            int begin = h.Sent.ToList().FindIndex(s => s == $"begin->{seat}@{h.Generation.Epoch}");
            int state = h.Sent.ToList().FindIndex(s => s == $"state->{seat}@{h.Generation.Epoch}");
            Assert.InRange(begin, 0, firstResume - 1);
            Assert.InRange(state, begin + 1, firstResume - 1);
        }
    }

    /// <summary>The RESUME goes out once, however many peers report last.</summary>
    [Fact]
    public void TheResumeIsSentOnce()
    {
        var h = new RebuildHarness(joiners: 2);
        h.RunWholeRebuild();
        Assert.Equal(2, h.Sent.Count(s => s.StartsWith("resume")));   // one per peer, one round
        Assert.False(h.Release());                                     // and no second round
        Assert.Equal(2, h.Sent.Count(s => s.StartsWith("resume")));
    }

    // ---------------------------------------------------------------- refusing to race

    /// <summary>
    /// A second rebuild triggered while one is in flight is refused — including during the pack,
    /// which is the window that made this worth enforcing rather than assuming.
    /// </summary>
    [Theory]
    [InlineData(RebuildStep.Capturing)]
    [InlineData(RebuildStep.Packing)]
    [InlineData(RebuildStep.AwaitingApply)]
    public void ASecondRebuildIsRefusedWhileOneIsInFlight(RebuildStep at)
    {
        var h = new RebuildHarness(joiners: 2);
        h.Begin();
        if (at != RebuildStep.Capturing && at != RebuildStep.Packing)
        {
            h.Packed();
            h.Distribute();
        }
        Assert.False(h.Begin(), $"a second rebuild was allowed to start at {h.Rebuild.Step}");
        Assert.Equal(ResyncGate.AlreadyInProgress, h.GateDesync());
    }

    /// <summary>
    /// The pack's result is discarded when the session moved on while it ran.
    ///
    /// Three ways it can have moved, and all three end the same: the bytes describe a world that no
    /// longer exists, and writing them over the live one is the failure.
    /// </summary>
    [Fact]
    public void APackFromASupersededGenerationIsDropped()
    {
        var h = new RebuildHarness(joiners: 2);
        h.Begin();
        var stale = new SessionGeneration(h.Generation.SessionId, h.Generation.Epoch - 1);
        Assert.False(h.Packed(generation: stale));
        Assert.Equal(RebuildStep.Packing, h.Rebuild.Step);   // and nothing was distributed
    }

    [Fact]
    public void APackFromAPreviousSessionAttemptIsDropped()
    {
        var h = new RebuildHarness(joiners: 2);
        h.Begin();
        int oldAttempt = h.Attempt;
        h.RestartSession();
        Assert.False(h.Packed(attempt: oldAttempt));
    }

    [Fact]
    public void APackThatLandsAfterTheSessionEndedIsDropped()
    {
        var h = new RebuildHarness(joiners: 2);
        h.Begin();
        var generation = h.Generation;
        int attempt = h.Attempt;
        h.EndSession();
        Assert.False(h.Packed(attempt, generation));
    }

    // ---------------------------------------------------------------- awkward acknowledgements

    [Fact]
    public void ADuplicateAcknowledgementDoesNotReleaseEarly()
    {
        var h = new RebuildHarness(joiners: 3);
        h.Begin();
        h.Packed();
        h.Distribute();

        Assert.Equal(ApplyAck.Recorded, h.Apply(1));
        Assert.Equal(ApplyAck.Ignored, h.Apply(1));
        Assert.Equal(ApplyAck.Ignored, h.Apply(1));
        Assert.Equal(RebuildStep.AwaitingApply, h.Rebuild.Step);
        Assert.Equal(2, h.Rebuild.Outstanding.Count());
    }

    /// <summary>
    /// A straggler from the previous rebuild does not count toward this one. It carries an older
    /// epoch, and counted it would release a rebuild still in flight.
    /// </summary>
    [Fact]
    public void AnAcknowledgementFromTheRebuildBeforeThisOneIsIgnored()
    {
        var h = new RebuildHarness(joiners: 2);
        h.RunWholeRebuild();
        int previous = h.Generation.Epoch;

        h.Budget.RecordAgreement();
        h.Begin();
        h.Packed();
        h.Distribute();

        Assert.Equal(ApplyAck.Ignored, h.Apply(1, epoch: previous));
        Assert.Equal(ApplyAck.Ignored, h.Apply(2, epoch: previous));
        Assert.Equal(RebuildStep.AwaitingApply, h.Rebuild.Step);
    }

    [Fact]
    public void AnAcknowledgementBeforeAnythingWasDistributedIsIgnored()
    {
        var h = new RebuildHarness(joiners: 2);
        Assert.Equal(ApplyAck.Ignored, h.Apply(1, epoch: 99));
        h.Begin();
        Assert.Equal(ApplyAck.Ignored, h.Apply(1));   // still packing; nobody was asked yet
    }

    // ---------------------------------------------------------------- peers coming and going

    /// <summary>
    /// A host whose last peer left during the pack finishes rather than waiting forever.
    ///
    /// A barrier that answered "waiting" for an empty set would freeze a solo host with nobody who
    /// could ever release it — and the pack window is exactly when a peer can vanish, because it is
    /// the longest step.
    /// </summary>
    [Fact]
    public void AHostLeftAloneDuringThePackFinishesInstead()
    {
        var h = new RebuildHarness(joiners: 1);
        h.Begin();
        h.DropJoiner(1);
        Assert.True(h.Packed());
        Assert.False(h.Distribute());   // nobody to wait for
        h.Rebuild.Complete();
        Assert.False(h.Phase.IsRebuilding);
        Assert.True(h.Phase.IsPlaying);
    }

    /// <summary>
    /// A peer that drops mid-rebuild does not hold the barrier, because the recovery that follows
    /// re-arms it from the peers still present. This drives that end to end rather than trusting
    /// the invariant.
    /// </summary>
    [Fact]
    public void ARebuildAfterADropWaitsOnlyOnWhoIsLeft()
    {
        var h = new RebuildHarness(joiners: 3);
        h.Begin();
        h.Packed();
        h.Distribute();
        h.Apply(1);

        // P2 drops. The form's drop path advances the generation, which is a fresh rebuild here.
        h.DropJoiner(2);
        h.EndSession();
        h.Phase.Start();

        Assert.True(h.Begin());
        h.Packed();
        Assert.True(h.Distribute());
        Assert.Equal(new[] { 1, 3 }, h.Rebuild.Outstanding.OrderBy(s => s).ToArray());

        h.Apply(1);
        Assert.Equal(ApplyAck.Complete, h.Apply(3));
        Assert.True(h.AllConverged());
    }

    // ---------------------------------------------------------------- the budget across cycles

    /// <summary>
    /// Six recoveries with no agreement between them ends the session; the seventh is never
    /// attempted. This walks the real cycle each time rather than poking the counter.
    /// </summary>
    [Fact]
    public void RepeatedRecoveriesWithNoAgreementRunOutOfBudget()
    {
        var h = new RebuildHarness(joiners: 2);
        for (int i = 1; i <= ResyncBudget.DefaultMaxAttempts; i++)
        {
            Assert.Equal(ResyncGate.Start, h.GateDesync());
            Assert.Equal(i, h.Budget.Used);
            Assert.True(h.Begin());
            h.Packed();
            h.Distribute();
            foreach (var joiner in h.Joiners.ToList()) h.Apply(joiner.Seat);
        }
        Assert.Equal(ResyncGate.GiveUp, h.GateDesync());
    }

    /// <summary>
    /// Recoveries that WORK never accumulate into a give-up. The checksums re-agree between them,
    /// which is what the budget's reset signal exists for — without it a healthy session that
    /// recovered from seven transient hiccups over an hour would be killed for persistent desync.
    /// </summary>
    [Fact]
    public void SuccessfulRecoveriesNeverExhaustTheBudget()
    {
        var h = new RebuildHarness(joiners: 3);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(ResyncGate.Start, h.GateDesync());
            Assert.True(h.RunWholeRebuild());
            Assert.True(h.AllConverged());
            h.Budget.RecordAgreement();   // the peers agree again
        }
    }

    /// <summary>
    /// Settings changes are free, and land every peer on the parameters the host just stated.
    ///
    /// Charged against the desync budget, six delay tweaks would end a session that never desynced
    /// — and a rebuild that did not restate the parameters would leave peers running whatever they
    /// happened to have.
    /// </summary>
    [Fact]
    public void ASettingsChangeCostsNothingAndRestatesTheParameters()
    {
        var h = new RebuildHarness(joiners: 3) { Delay = 3, Mode = SyncMode.Lockstep };
        for (int i = 0; i < 10; i++)
        {
            h.Delay = 2 + i;
            h.Mode = i % 2 == 0 ? SyncMode.Rollback : SyncMode.Lockstep;
            Assert.True(h.RunWholeRebuild(isSettingsChange: true));
            Assert.All(h.Joiners, j =>
            {
                Assert.Equal(h.Delay, j.Delay);
                Assert.Equal(h.Mode, j.Mode);
            });
        }
        Assert.Equal(0, h.Budget.Used);
    }

    // ---------------------------------------------------------------- failing safely

    /// <summary>
    /// A capture that throws releases the claim.
    ///
    /// A rebuild left claimed after its sequence died is worse than the original fault: the session
    /// refuses every future recovery, and shows nothing on screen to say why.
    /// </summary>
    [Fact]
    public void ACaptureThatThrowsDoesNotLeaveTheRebuildClaimed()
    {
        var h = new RebuildHarness(joiners: 2)
        {
            CaptureFault = new InvalidOperationException("the core refused to export"),
        };
        Assert.Throws<InvalidOperationException>(() => h.Begin());

        Assert.Equal(RebuildStep.Idle, h.Rebuild.Step);
        Assert.False(h.Phase.IsRebuilding);

        h.CaptureFault = null;
        Assert.True(h.Begin(), "the next recovery was refused by a claim nobody was holding");
    }

    /// <summary>
    /// Driving the sequence out of order is refused loudly rather than acted on.
    ///
    /// These are unreachable through the form today. They are guarded because the failure a
    /// careless future caller would produce — two baselines on one generation — is silent, and
    /// arrives as a desync nobody can explain rather than as an exception naming the line.
    /// </summary>
    [Fact]
    public void TheSequenceRefusesToBeDrivenOutOfOrder()
    {
        var h = new RebuildHarness(joiners: 2);
        Assert.Throws<InvalidOperationException>(() =>
            h.Rebuild.Captured(new SessionGeneration(1, 1), 1024));
        Assert.Throws<InvalidOperationException>(() => h.Rebuild.TryDistribute(new[] { 1 }));

        h.Begin();
        Assert.Throws<InvalidOperationException>(() => h.Rebuild.TryDistribute(new[] { 1 }));
        Assert.False(h.Rebuild.TryBeginResume());
    }

    [Fact]
    public void ARebuildDistributesARealGenerationOrRefusesTo()
    {
        var h = new RebuildHarness(joiners: 1);
        h.Rebuild.TryBegin(isSettingsChange: false, attempt: 1);
        Assert.Throws<ArgumentException>(() => h.Rebuild.Captured(default, 1024));
    }

    // ---------------------------------------------------------------- adopting a captured baseline

    /// <summary>
    /// The post-timeout vacate joins the sequence at distribution and leaves it the ordinary way.
    ///
    /// Its baseline was captured when the peer dropped and its phase claim taken then too, so there
    /// is nothing to capture or pack — but the wait and the single release are the same, and this
    /// is what makes them the same code rather than a second copy.
    /// </summary>
    [Fact]
    public void AnAdoptedBaselineWaitsAndReleasesLikeAnyOther()
    {
        var h = new RebuildHarness(joiners: 2);
        // The drop path's claim, taken when the peer went away.
        Assert.True(h.Phase.BeginRebuild(RebuildReason.PeerLoss));
        var generation = h.Generation.Next();

        Assert.True(h.Rebuild.TryAdopt(generation, stateBytes: 4096, h.Attempt));
        Assert.Equal(RebuildStep.Distributing, h.Rebuild.Step);
        Assert.True(h.Rebuild.TryDistribute(new[] { 1, 2 }));

        Assert.Equal(ApplyAck.Recorded, h.Rebuild.Applied(1, generation.Epoch));
        Assert.Equal(ApplyAck.Complete, h.Rebuild.Applied(2, generation.Epoch));
        Assert.True(h.Rebuild.TryBeginResume());
        h.Rebuild.Complete();
        Assert.False(h.Phase.IsRebuilding);
    }

    [Fact]
    public void AdoptingNeedsAClaimAlreadyTakenAndOneNotAlreadyDriven()
    {
        var h = new RebuildHarness(joiners: 1);
        // No claim: nothing to adopt.
        Assert.False(h.Rebuild.TryAdopt(h.Generation.Next(), 1024, h.Attempt));
        // A rebuild already being driven here is not adopted on top of.
        h.Begin();
        Assert.False(h.Rebuild.TryAdopt(h.Generation.Next(), 1024, h.Attempt));
    }
}
