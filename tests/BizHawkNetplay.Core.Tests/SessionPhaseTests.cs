using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// The session's life as one object. These were four hand-maintained booleans spread over the
    /// recovery paths, which are the paths least likely to be exercised by playing normally — so the
    /// combinations below have never been checkable outside a live session with a peer pulled off it.
    /// </summary>
    public class SessionPhaseTests
    {
        [Fact]
        public void ANewPhaseIsNotInASession()
        {
            var p = new SessionPhase();
            Assert.False(p.IsActive);
            Assert.False(p.IsRebuilding);
            Assert.False(p.AwaitingRejoin);
            Assert.False(p.ResumeQueued);
            Assert.False(p.IsPlaying);
            Assert.Equal(RebuildReason.None, p.Rebuild);
        }

        [Fact]
        public void LosingAPeerIsBothARebuildAndAnEmptySeat()
        {
            // The case a state ENUM cannot express, and the reason this is flags-behind-methods: the
            // survivors are put back on a shared baseline (a rebuild) while the seat waits for its
            // player (a rejoin). They start together and are cleared at different moments.
            var p = new SessionPhase();
            p.Start();

            Assert.True(p.BeginRebuild(RebuildReason.PeerLoss));
            p.BeginAwaitingRejoin();

            Assert.True(p.IsRebuilding);
            Assert.True(p.AwaitingRejoin);
            Assert.False(p.IsPlaying);

            p.EndAwaitingRejoin();          // they came back...
            Assert.True(p.IsRebuilding);    // ...and are still being caught up
            Assert.False(p.IsPlaying);

            p.EndRebuild();
            Assert.True(p.IsPlaying);
        }

        [Fact]
        public void ASecondRebuildIsRefusedWhileOneIsInFlight()
        {
            // Two authoritative baselines racing each other is exactly the desync a resync exists to
            // repair, so the refusal is the point.
            var p = new SessionPhase();
            p.Start();

            Assert.True(p.BeginRebuild(RebuildReason.Desync));
            Assert.False(p.BeginRebuild(RebuildReason.SettingsChange));
            Assert.Equal(RebuildReason.Desync, p.Rebuild);   // the first one keeps the session

            p.EndRebuild();
            Assert.True(p.BeginRebuild(RebuildReason.SettingsChange));
        }

        [Fact]
        public void ARefusedRebuildDoesNotDisturbAResumeAlreadyInFlight()
        {
            var p = new SessionPhase();
            p.Start();
            p.BeginRebuild(RebuildReason.Desync);
            Assert.True(p.TryQueueResume());

            Assert.False(p.BeginRebuild(RebuildReason.PeerLoss));
            Assert.True(p.ResumeQueued);
        }

        [Fact]
        public void TheResumeIsSentOnce()
        {
            // Every peer that applied the baseline reports back, and each report reaches the same
            // release path; the second must not put another RESUME on the wire.
            var p = new SessionPhase();
            p.Start();
            p.BeginRebuild(RebuildReason.Desync);

            Assert.True(p.TryQueueResume());
            Assert.False(p.TryQueueResume());
            Assert.False(p.TryQueueResume());
        }

        [Fact]
        public void EachRebuildGetsItsOwnResume()
        {
            var p = new SessionPhase();
            p.Start();

            p.BeginRebuild(RebuildReason.Desync);
            Assert.True(p.TryQueueResume());
            p.EndRebuild();
            Assert.False(p.ResumeQueued);

            p.BeginRebuild(RebuildReason.Desync);
            Assert.True(p.TryQueueResume());
        }

        [Fact]
        public void BeginningARebuildClearsAStaleResume()
        {
            // Belt to EndRebuild's braces: whichever of the two ran last, a fresh rebuild owes a fresh
            // resume.
            var p = new SessionPhase();
            p.Start();
            p.BeginRebuild(RebuildReason.Desync);
            p.TryQueueResume();
            p.EndRebuild();
            p.BeginRebuild(RebuildReason.PeerLoss);

            Assert.False(p.ResumeQueued);
        }

        [Fact]
        public void StartSubsumesThePreClearsItReplaced()
        {
            // GO used to be preceded by hand-clearing the recovery flags a few lines earlier. Anything
            // left over from a previous session has to be gone by the time the session is active.
            var p = new SessionPhase();
            p.Start();
            p.BeginRebuild(RebuildReason.Desync);
            p.BeginAwaitingRejoin();
            p.TryQueueResume();

            p.Start();

            Assert.True(p.IsActive);
            Assert.False(p.IsRebuilding);
            Assert.False(p.AwaitingRejoin);
            Assert.False(p.ResumeQueued);
            Assert.True(p.IsPlaying);
        }

        [Fact]
        public void StopClearsEverythingHoweverTheSessionEnded()
        {
            var p = new SessionPhase();
            p.Start();
            p.BeginRebuild(RebuildReason.PeerLoss);
            p.BeginAwaitingRejoin();
            p.TryQueueResume();

            p.Stop();

            Assert.False(p.IsActive);
            Assert.False(p.IsRebuilding);
            Assert.False(p.AwaitingRejoin);
            Assert.False(p.ResumeQueued);
            Assert.Equal(RebuildReason.None, p.Rebuild);
        }

        [Fact]
        public void RebuildingWithNoReasonIsNotARebuild()
        {
            var p = new SessionPhase();
            p.Start();

            Assert.False(p.BeginRebuild(RebuildReason.None));
            Assert.False(p.IsRebuilding);
            Assert.True(p.IsPlaying);
        }

        [Fact]
        public void NothingRebuildsOrHoldsASeatOutsideASession()
        {
            // Before GO and after the session ends there is no timeline to rebuild and no seat to
            // hold. Every caller checks this today; the type checking it is what stops the next one
            // from having to.
            var p = new SessionPhase();

            Assert.False(p.BeginRebuild(RebuildReason.Desync));
            p.BeginAwaitingRejoin();
            Assert.False(p.IsRebuilding);
            Assert.False(p.AwaitingRejoin);

            p.Start();
            p.BeginRebuild(RebuildReason.Desync);
            p.Stop();

            Assert.False(p.BeginRebuild(RebuildReason.PeerLoss));
            p.BeginAwaitingRejoin();
            Assert.False(p.IsRebuilding);
            Assert.False(p.AwaitingRejoin);
        }

        [Fact]
        public void ALateAppliedReportCannotResumeATwiceResumedSession()
        {
            // Both callers fire when a peer reports it applied the baseline. One arriving after the
            // rebuild ended finds the queue flag cleared and the generation unchanged, so without this
            // it would put a second RESUME on the wire for a session that already resumed.
            var p = new SessionPhase();
            p.Start();
            p.BeginRebuild(RebuildReason.Desync);
            Assert.True(p.TryQueueResume());
            p.EndRebuild();

            Assert.False(p.TryQueueResume());
            Assert.False(p.ResumeQueued);
        }

        [Fact]
        public void AwaitingARejoinAloneStopsPlayWithoutClaimingARebuild()
        {
            // A joiner waiting to be let back in is held, but nothing is being rebuilt for it yet.
            var p = new SessionPhase();
            p.Start();
            p.BeginAwaitingRejoin();

            Assert.False(p.IsPlaying);
            Assert.False(p.IsRebuilding);
            Assert.True(p.IsActive);
        }
    }
}
