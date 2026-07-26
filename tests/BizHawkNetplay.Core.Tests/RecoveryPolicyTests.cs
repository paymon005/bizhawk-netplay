using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// The link watchdog's decision rule and the recovery-phase policy — the "who unblocks the
    /// barrier when a peer dies mid-recovery" questions from the review, as executable checks.
    /// Times are plain numbers; Judge is unit-agnostic (the tool feeds Stopwatch ticks).
    /// </summary>
    public class RecoveryPolicyTests
    {
        private const long PingTimeout = 3000;

        private static LinkHealth.LinkSnapshot Link(
            int awaitingEpoch = 0, long appliedDeadline = 0,
            bool resyncReceiving = false, long receiveDeadline = 0,
            long graceUntil = 0, long lastRecv = 1) =>
            new LinkHealth.LinkSnapshot(awaitingEpoch, appliedDeadline,
                resyncReceiving, receiveDeadline, graceUntil, lastRecv);

        [Fact]
        public void HealthyChattyPeer_IsHealthy()
        {
            Assert.Equal(LinkVerdict.Healthy, LinkHealth.Judge(Link(lastRecv: 9_000), 10_000, PingTimeout));
        }

        [Fact]
        public void SilencePastPingTimeout_IsADrop()
        {
            Assert.Equal(LinkVerdict.PingTimeout, LinkHealth.Judge(Link(lastRecv: 1_000), 10_000, PingTimeout));
        }

        [Fact]
        public void NeverHeardPeer_IsNotPingFlagged()
        {
            // Session start owns that window — a link with no receive stamp yet is not the watchdog's call.
            Assert.Equal(LinkVerdict.Healthy, LinkHealth.Judge(Link(lastRecv: 0), 1_000_000, PingTimeout));
        }

        [Fact]
        public void PeerThatPingsButNeverApplies_IsDroppedAtTheApplyDeadline()
        {
            // THE mid-barrier death scenario: pings only prove the reader thread is alive. A peer
            // can answer them forever while never importing the state that gates the generation —
            // the apply barrier must take precedence over ping recency AND over any grace window.
            var link = Link(awaitingEpoch: 7, appliedDeadline: 5_000,
                graceUntil: long.MaxValue, lastRecv: 9_999);
            Assert.Equal(LinkVerdict.AppliedDeadlineExpired, LinkHealth.Judge(link, 10_000, PingTimeout));
        }

        [Fact]
        public void PeerOwingAnEpochWithinItsDeadline_FallsThroughToTheOrdinaryChecks()
        {
            // Within the apply deadline the barrier is not a verdict — but it is not an excuse
            // either. Whether the peer is flagged then depends on grace/silence as usual.
            var withGrace = Link(awaitingEpoch: 7, appliedDeadline: 50_000, graceUntil: 50_000, lastRecv: 1_000);
            Assert.Equal(LinkVerdict.Healthy, LinkHealth.Judge(withGrace, 10_000, PingTimeout));
            var noGrace = Link(awaitingEpoch: 7, appliedDeadline: 50_000, graceUntil: 0, lastRecv: 1_000);
            Assert.Equal(LinkVerdict.PingTimeout, LinkHealth.Judge(noGrace, 10_000, PingTimeout));
        }

        [Fact]
        public void PeerSendingUsAState_IsExcusedFromPings_ButBoundedByItsOwnDeadline()
        {
            // BEGIN arrived, the big frame is in flight: silence must not ping-drop the sender…
            var inFlight = Link(resyncReceiving: true, receiveDeadline: 50_000, lastRecv: 1_000);
            Assert.Equal(LinkVerdict.Healthy, LinkHealth.Judge(inFlight, 10_000, PingTimeout));
            // …but BEGIN with a frame that never completes must not hold the session forever.
            var overdue = Link(resyncReceiving: true, receiveDeadline: 8_000, lastRecv: 9_999);
            Assert.Equal(LinkVerdict.ResyncReceiveDeadlineExpired, LinkHealth.Judge(overdue, 10_000, PingTimeout));
        }

        [Fact]
        public void GraceForAStateWeSent_ExcusesSilence_OnlyUntilItLapses()
        {
            var digesting = Link(graceUntil: 20_000, lastRecv: 1_000);
            Assert.Equal(LinkVerdict.Healthy, LinkHealth.Judge(digesting, 10_000, PingTimeout));
            Assert.Equal(LinkVerdict.PingTimeout, LinkHealth.Judge(digesting, 30_000, PingTimeout));
        }

        // ---- recovery-phase policy -------------------------------------------------

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Joiner_AlwaysEndsOnPeerLoss(bool resync, bool reconnect)
        {
            Assert.Equal(PeerLossAction.EndSessionJoinerLostHost,
                RecoveryPolicy.OnPeerLost(isHost: false, resync, reconnect));
        }

        [Fact]
        public void Host_ActiveSession_BeginsTheReconnectWait()
        {
            Assert.Equal(PeerLossAction.BeginReconnectWait,
                RecoveryPolicy.OnPeerLost(isHost: true, resyncInProgress: false, awaitingReconnect: false));
        }

        [Fact]
        public void Host_DropDuringResync_EndsInsteadOfNestingBarriers()
        {
            Assert.Equal(PeerLossAction.EndSessionDropDuringResync,
                RecoveryPolicy.OnPeerLost(isHost: true, resyncInProgress: true, awaitingReconnect: false));
        }

        [Fact]
        public void Host_SecondDropDuringReconnectWait_Ends()
        {
            // A reconnect wait freezes with BOTH flags set; only one outstanding drop is supported.
            Assert.Equal(PeerLossAction.EndSessionSecondDropDuringReconnect,
                RecoveryPolicy.OnPeerLost(isHost: true, resyncInProgress: true, awaitingReconnect: true));
        }

        // ---- resync gate -----------------------------------------------------------

        [Fact]
        public void ResyncGate_InProgressWinsOverEverything()
        {
            Assert.Equal(ResyncGate.AlreadyInProgress,
                RecoveryPolicy.GateResync(true, 0.0, 5.0, 99, 3));
        }

        [Fact]
        public void ResyncGate_DebouncesRepeatTriggersForTheSameDesync()
        {
            Assert.Equal(ResyncGate.Debounced, RecoveryPolicy.GateResync(false, 4.9, 5.0, 1, 3));
            Assert.Equal(ResyncGate.Start, RecoveryPolicy.GateResync(false, 5.0, 5.0, 1, 3));
        }

        [Fact]
        public void ResyncGate_GivesUpBeyondTheCap_NotAtIt()
        {
            Assert.Equal(ResyncGate.Start, RecoveryPolicy.GateResync(false, 60.0, 5.0, 3, 3));
            Assert.Equal(ResyncGate.GiveUp, RecoveryPolicy.GateResync(false, 60.0, 5.0, 4, 3));
        }
    }
}
