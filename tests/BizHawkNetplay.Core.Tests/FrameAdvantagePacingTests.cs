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
            s.OnPacingReport(new PacingInfo(20, 0, frameAdvantage: 6, hasFrameAdvantage: true));
            int stalls = RunFrames(s, pipe, emu, 5, 10);

            Assert.InRange(stalls, 1, 3);
            Assert.True(s.TimeSyncStalls > 0, "the ahead peer should have yielded via time-sync");
        }

        [Fact]
        public void BehindPeer_NeverYields()
        {
            var s = NewStrategy(out var emu, out var pipe);
            // Negative advantage = we are the slow one. Yielding here would make the gap worse.
            s.OnPacingReport(new PacingInfo(20, 0, frameAdvantage: -6, hasFrameAdvantage: true));
            Assert.Equal(0, RunFrames(s, pipe, emu, 0, 10));
            Assert.Equal(0, s.TimeSyncStalls);
        }

        [Fact]
        public void SmallAdvantage_IsIgnoredAsJitter()
        {
            var s = NewStrategy(out var emu, out var pipe);
            // One frame apart is normal on any link; stalling for it would oscillate.
            s.OnPacingReport(new PacingInfo(20, 0, frameAdvantage: 1, hasFrameAdvantage: true));
            Assert.Equal(0, RunFrames(s, pipe, emu, 0, 10));
            Assert.Equal(0, s.TimeSyncStalls);
        }

        [Fact]
        public void UnmeasuredAdvantage_ChangesNothing()
        {
            // A peer on an older build never reports, so HasFrameAdvantage stays false and the value is
            // meaningless — it must not be mistaken for "dead even" or, worse, acted on.
            var s = NewStrategy(out var emu, out var pipe);
            s.OnPacingReport(new PacingInfo(20, 0, frameAdvantage: 6)); // 3-arg ctor => not measured
            Assert.Equal(0, RunFrames(s, pipe, emu, 0, 10));
            Assert.Equal(0, s.TimeSyncStalls);
        }

        [Fact]
        public void YieldIsBounded_NotTheWholeSurplus()
        {
            // A huge reported advantage must not stall us into a hole: the peer is closing the gap from
            // its side too, so handing back everything at once overshoots and starts a tug-of-war.
            var s = NewStrategy(out var emu, out var pipe);
            s.OnPacingReport(new PacingInfo(20, 0, frameAdvantage: 100, hasFrameAdvantage: true));
            int stalls = RunFrames(s, pipe, emu, 0, 20);
            Assert.InRange(stalls, 1, 3);
        }

        [Fact]
        public void RepeatedReports_DoNotAccumulateIntoAStallStorm()
        {
            var s = NewStrategy(out var emu, out var pipe);
            for (int i = 0; i < 5; i++)
                s.OnPacingReport(new PacingInfo(20, 0, frameAdvantage: 4, hasFrameAdvantage: true));

            // Five reports of the same skew owe the same debt, not five times it.
            int stalls = RunFrames(s, pipe, emu, 0, 20);
            Assert.InRange(stalls, 1, 3);
        }
    }
}
