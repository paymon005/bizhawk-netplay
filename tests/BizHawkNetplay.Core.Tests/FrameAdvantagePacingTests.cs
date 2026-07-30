using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// Time-sync driven by a measured frame advantage rather than round-trip time. RTT is symmetric —
    /// it says how far apart two peers are, never which one is ahead — so a horizon derived from it
    /// throttles whichever peer happens to trip it. A signed advantage lets the peer that is actually
    /// running fast give the surplus back, which is what these cover.
    /// </summary>
    public class FrameAdvantagePacingTests
    {
        private const double FrameMs = 16.639;
        private const int MaxRollback = 12;

        private const int Ports = 2;

        private static RollbackStrategy NewStrategy(out FakeEmuAdapter emu, out InputPipeline pipeline)
        {
            emu = new FakeEmuAdapter(portCount: Ports);
            pipeline = new InputPipeline(Ports);
            for (int p = 0; p < Ports; p++) pipeline.SetLocal(p, p == 0);
            return new RollbackStrategy(pipeline, emu, localPort: 0, maxRollback: MaxRollback, frameMs: FrameMs);
        }

        /// <summary>
        /// Step frames with the remote port kept fed, so the prediction horizon stays healthy and the
        /// only thing that can stall us is the time-sync under test — not a starved frontier.
        /// </summary>
        private static int RunFrames(RollbackStrategy s, InputPipeline pipeline, FakeEmuAdapter emu, int from, int count)
        {
            int stalls = 0;
            var neutral = PortInput.Neutral(emu.GetControllerLayout(1));
            for (int f = from; f < from + count; f++)
            {
                pipeline.Add(1, f, neutral); // remote input for this frame has already arrived
                var d = s.BeginFrame(f);
                if (d.Stall) { stalls++; continue; }
                s.EndFrame(f);
            }
            return stalls;
        }

        [Fact]
        public void AheadPeer_GivesFramesBack()
        {
            var s = NewStrategy(out var emu, out var pipe);
            Assert.Equal(0, RunFrames(s, pipe, emu, 0, 5)); // healthy baseline: no stalls

            // We are 6 frames ahead. Half of that, capped, is handed back over the next frames.
            s.OnPacingReport(new PacingInfo(20, frameAdvantage: 6, hasFrameAdvantage: true));
            int stalls = RunFrames(s, pipe, emu, 5, 10);

            Assert.InRange(stalls, 1, 3);
            Assert.True(s.TimeSyncStalls > 0, "the ahead peer should have yielded via time-sync");
        }

        [Fact]
        public void BehindPeer_NeverYields()
        {
            var s = NewStrategy(out var emu, out var pipe);
            // Negative advantage = we are the slow one. Yielding here would make the gap worse.
            s.OnPacingReport(new PacingInfo(20, frameAdvantage: -6, hasFrameAdvantage: true));
            Assert.Equal(0, RunFrames(s, pipe, emu, 0, 10));
            Assert.Equal(0, s.TimeSyncStalls);
        }

        [Fact]
        public void SmallAdvantage_IsIgnoredAsJitter()
        {
            var s = NewStrategy(out var emu, out var pipe);
            // One frame apart is normal on any link; stalling for it would oscillate.
            s.OnPacingReport(new PacingInfo(20, frameAdvantage: 1, hasFrameAdvantage: true));
            Assert.Equal(0, RunFrames(s, pipe, emu, 0, 10));
            Assert.Equal(0, s.TimeSyncStalls);
        }

        [Fact]
        public void UnmeasuredAdvantage_ChangesNothing()
        {
            // A peer on an older build never reports, so HasFrameAdvantage stays false and the value is
            // meaningless — it must not be mistaken for "dead even" or, worse, acted on.
            var s = NewStrategy(out var emu, out var pipe);
            s.OnPacingReport(new PacingInfo(20, frameAdvantage: 6)); // 3-arg ctor => not measured
            Assert.Equal(0, RunFrames(s, pipe, emu, 0, 10));
            Assert.Equal(0, s.TimeSyncStalls);
        }

        [Fact]
        public void YieldIsBounded_NotTheWholeSurplus()
        {
            // A huge reported advantage must not stall us into a hole: the peer is closing the gap from
            // its side too, so handing back everything at once overshoots and starts a tug-of-war.
            var s = NewStrategy(out var emu, out var pipe);
            s.OnPacingReport(new PacingInfo(20, frameAdvantage: 100, hasFrameAdvantage: true));
            int stalls = RunFrames(s, pipe, emu, 0, 20);
            Assert.InRange(stalls, 1, 3);
        }

        [Fact]
        public void RepeatedReports_DoNotAccumulateIntoAStallStorm()
        {
            var s = NewStrategy(out var emu, out var pipe);
            for (int i = 0; i < 5; i++)
                s.OnPacingReport(new PacingInfo(20, frameAdvantage: 4, hasFrameAdvantage: true));

            // Five reports of the same skew owe the same debt, not five times it.
            int stalls = RunFrames(s, pipe, emu, 0, 20);
            Assert.InRange(stalls, 1, 3);
        }

        [Fact]
        public void SameWireSample_CannotRearmConsumedDebt()
        {
            var s = NewStrategy(out var emu, out var pipe);
            var neutral = PortInput.Neutral(emu.GetControllerLayout(1));
            pipe.Add(1, 0, neutral);
            var sample = new PacingInfo(20, frameAdvantage: 4, hasFrameAdvantage: true, sampleSequence: 7);

            s.OnPacingReport(sample);
            Assert.True(s.BeginFrame(0).Stall);
            Assert.True(s.BeginFrame(0).Stall);
            Assert.False(s.BeginFrame(0).Stall);
            s.EndFrame(0);

            // UI refreshes may outnumber wire reports. Replaying revision 7 is a no-op.
            s.OnPacingReport(sample);
            pipe.Add(1, 1, neutral);
            Assert.False(s.BeginFrame(1).Stall);
            s.EndFrame(1);

            // The same measured value from a genuinely new exchange may create fresh debt.
            s.OnPacingReport(new PacingInfo(20, 4, true, sampleSequence: 8));
            pipe.Add(1, 2, neutral);
            Assert.True(s.BeginFrame(2).Stall);
        }

        [Fact]
        public void SoftCapStall_AlsoPaysOneAdvantageDebtFrame()
        {
            var s = NewStrategy(out var emu, out var pipe);
            var neutral = PortInput.Neutral(emu.GetControllerLayout(1));
            s.OnPacingReport(new PacingInfo(0, frameAdvantage: 4,
                hasFrameAdvantage: true, sampleSequence: 1)); // soft cap 3, debt 2

            // With no remote frontier, frame 4 is beyond the soft cap. That stall must pay debt too.
            Assert.True(s.BeginFrame(4).Stall);
            Assert.True(s.LastStallWasTimeSync);

            pipe.Add(1, 0, neutral); // horizon is now exactly the soft cap
            Assert.True(s.BeginFrame(4).Stall);  // only one debt frame remains
            Assert.False(s.BeginFrame(4).Stall);
        }

        [Fact]
        public void MultiPeerFreshSample_DoesNotReplayAnotherPeersCachedDebt()
        {
            var tracker = new FrameAdvantageTracker();

            Assert.True(tracker.Record(peerPort: 1, sequence: 1,
                localAdvantage: 8, remoteAdvantage: 0, known: true));
            Assert.Equal(4, tracker.Consume(out bool known, out int revision, out bool fresh));
            Assert.True(known);
            Assert.True(fresh);
            Assert.Equal(1, revision);

            // P2's genuinely new even sample must not make P1's unchanged +4 look new again.
            Assert.True(tracker.Record(peerPort: 2, sequence: 1,
                localAdvantage: 0, remoteAdvantage: 0, known: true));
            Assert.Equal(4, tracker.Consume(out known, out revision, out fresh));
            Assert.True(known);
            Assert.False(fresh);
            Assert.Equal(1, revision);

            // A new sample from the peer defining the aggregate is a new pacing observation.
            Assert.True(tracker.Record(peerPort: 1, sequence: 2,
                localAdvantage: 8, remoteAdvantage: 0, known: true));
            Assert.Equal(4, tracker.Consume(out known, out revision, out fresh));
            Assert.True(fresh);
            Assert.Equal(2, revision);
        }

        [Fact]
        public void MultiPeerCachedRunnerUp_DoesNotBecomeFreshWhenLeaderChanges()
        {
            var tracker = new FrameAdvantageTracker();
            tracker.Record(peerPort: 1, sequence: 1,
                localAdvantage: 8, remoteAdvantage: 0, known: true); // +4, leader
            tracker.Record(peerPort: 2, sequence: 1,
                localAdvantage: 6, remoteAdvantage: 0, known: true); // +3, cached runner-up
            Assert.Equal(4, tracker.Consume(out _, out int revision, out bool fresh));
            Assert.True(fresh);
            Assert.Equal(1, revision);

            // P1's new +2 makes P2's old +3 the aggregate. P2 did not send a new sample, so its
            // already-consumed report must not manufacture new debt during this handoff.
            tracker.Record(peerPort: 1, sequence: 2,
                localAdvantage: 4, remoteAdvantage: 0, known: true);
            Assert.Equal(3, tracker.Consume(out _, out revision, out fresh));
            Assert.False(fresh);
            Assert.Equal(1, revision);

            tracker.Record(peerPort: 2, sequence: 2,
                localAdvantage: 6, remoteAdvantage: 0, known: true);
            Assert.Equal(3, tracker.Consume(out _, out revision, out fresh));
            Assert.True(fresh);
            Assert.Equal(2, revision);
        }
    }
}
