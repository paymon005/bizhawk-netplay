using System.Collections.Generic;
using System.Linq;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    public class CapabilityProbeTests
    {
        [Fact]
        public void SolveMaxDepth_LightCore_QualifiesForRollback()
        {
            // NES-class: ~0.2ms frame, tiny state -> very fast save/load.
            int depth = CapabilityProbe.SolveMaxDepth(
                frameBudgetMs: 16.639, headroomMs: 4.0,
                normalFrameMs: 0.2, loadMs: 0.05, saveMs: 0.05);
            // available = 16.639 - 4 - 0.2 - 0.05 = 12.389 ; perFrame = 0.25 -> ~49
            Assert.True(depth >= ProbeResult.RollbackDepthThreshold);
            Assert.Equal(49, depth);
        }

        [Fact]
        public void SolveMaxDepth_HeavyCore_FailsRollback()
        {
            // N64/PSX-class: expensive frame and multi-millisecond state ops.
            int depth = CapabilityProbe.SolveMaxDepth(
                frameBudgetMs: 16.639, headroomMs: 4.0,
                normalFrameMs: 8.0, loadMs: 6.0, saveMs: 6.0);
            Assert.True(depth < ProbeResult.RollbackDepthThreshold);
        }

        [Fact]
        public void SolveMaxDepth_ReturnsZeroWhenBudgetAlreadyBlown()
        {
            int depth = CapabilityProbe.SolveMaxDepth(
                frameBudgetMs: 16.639, headroomMs: 4.0,
                normalFrameMs: 20.0, loadMs: 1.0, saveMs: 1.0);
            Assert.Equal(0, depth);
        }

        [Fact]
        public void SolveMaxDepth_LegacyOverload_MatchesTheExplicitModel()
        {
            // The five-argument form must stay exactly what it always was: no elision, one frame period
            // of repair budget. Every existing caller depends on that.
            foreach (var (frame, load, save) in new[] { (0.2, 0.05, 0.05), (2.0, 1.5, 6.0), (8.0, 6.0, 6.0) })
                Assert.Equal(
                    CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, load, save),
                    CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, load, save,
                        elideConfirmedSaves: false, repairBudgetMs: 0));
        }

        [Fact]
        public void SolveMaxDepth_SteadyStateGateStopsARepairBudgetPaperingOverAnUnaffordableCore()
        {
            // frame+save is 14ms against 12.639ms of usable budget: this core cannot afford a snapshot
            // every frame, full stop. A generous repair budget must not be able to hide that — the
            // repair sum alone would happily report a workable depth. Eliding the recurring save is
            // what actually makes the core viable, not a bigger allowance for the occasional repair.
            const double frame = 6.0, load = 0.5, save = 8.0;
            const double generousRepair = 2 * 16.639;

            Assert.Equal(0, CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, load, save,
                elideConfirmedSaves: false, repairBudgetMs: generousRepair));
            Assert.True(CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, load, save,
                elideConfirmedSaves: true, repairBudgetMs: generousRepair) > 0);

            // With no repair budget the gate is redundant — the repair sum already returns 0 — so it
            // can never change an answer the original five-argument form gave.
            Assert.Equal(0, CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, load, save));
        }

        [Fact]
        public void SolveMaxDepth_RealN64Timings_ReachTheThresholdOnlyWithBothChanges()
        {
            // Measured on the N64 core this was built for: save 6.084, load 1.505, frame 1.966,
            // against a 16.683ms budget. The original model says 1 — too shallow to be worth running.
            const double budget = 16.683, headroom = 16.683 * 0.25;
            const double frame = 1.966, load = 1.505, save = 6.084;

            Assert.Equal(1, CapabilityProbe.SolveMaxDepth(budget, headroom, frame, load, save));

            // Elision alone doesn't move the repair sum — it removes the recurring tax, not the cost of
            // re-simulating. Allowing a repair two frame periods is what buys the depth.
            Assert.Equal(1, CapabilityProbe.SolveMaxDepth(budget, headroom, frame, load, save,
                elideConfirmedSaves: true, repairBudgetMs: 0));

            int depth = CapabilityProbe.SolveMaxDepth(budget, headroom, frame, load, save,
                elideConfirmedSaves: true, repairBudgetMs: 2 * budget);
            Assert.Equal(3, depth);
            Assert.True(depth >= ProbeResult.RollbackDepthThreshold);
        }

        [Fact]
        public void Run_DisqualifiesACoreThatDoesNotReproduceOnReplay()
        {
            // Timing says this core is fine. It still cannot do rollback, because rollback repair IS
            // load-and-re-simulate — and a core that lands somewhere else when it does that desyncs
            // only once the link makes it predict, which is why a timing-only probe waves it through
            // and the failure shows up later against a distant opponent.
            var emu = new FakeEmuAdapter(portCount: 2) { DriftsOnReplay = true };
            var clock = new ManualClock(Enumerable.Repeat(0.05, 400));

            var result = new CapabilityProbe(emu, clock, samples: 20).Run(16.639, 4.0);

            Assert.False(result.ReplayDeterministic);
            Assert.False(result.RollbackQualified);
            Assert.True(result.MaxRollbackDepth > ProbeResult.RollbackDepthThreshold,
                "the timing side must still say yes, or this proves nothing about the replay check");
            Assert.Contains("DIVERGED", result.ToString());
        }

        [Fact]
        public void Run_PassesTheReplayCheckOnAReproducibleCore()
        {
            var emu = new FakeEmuAdapter(portCount: 2);
            var clock = new ManualClock(Enumerable.Repeat(0.05, 400));

            var result = new CapabilityProbe(emu, clock, samples: 20).Run(16.639, 4.0);

            Assert.True(result.ReplayDeterministic);
            Assert.True(result.RollbackQualified);
        }

        [Fact]
        public void Run_ReplayCheckLeavesThePositionWhereItFoundIt()
        {
            // The probe runs before a session against the user's live game. It advances 60 frames to
            // answer this question and must hand every one of them back.
            var emu = new FakeEmuAdapter(portCount: 2);
            var clock = new ManualClock(Enumerable.Repeat(0.05, 400));
            var before = emu.HashMainMemory();

            new CapabilityProbe(emu, clock, samples: 20).Run(16.639, 4.0);

            Assert.Equal(before, emu.HashMainMemory());
            Assert.Empty(emu.LiveStates);
        }

        [Fact]
        public void SolveMaxDepth_N64FrameCostStraddlesTheVerdictBoundary()
        {
            // Fourteen consecutive probes of one N64 configuration measured frame costs from 1.863ms to
            // 3.582ms — noise, not a resolution curve, since the probe advances with rendering off. The
            // verdict flips across that range, which is the whole reason it is now reported as marginal
            // rather than as whichever answer the run happened to produce.
            const double budget = 16.683, headroom = 16.683 * 0.25;
            const double load = 1.6, save = 6.0;

            int fast = CapabilityProbe.SolveMaxDepth(budget, headroom, 1.863, load, save,
                elideConfirmedSaves: true, repairBudgetMs: 2 * budget);
            int slow = CapabilityProbe.SolveMaxDepth(budget, headroom, 3.582, load, save,
                elideConfirmedSaves: true, repairBudgetMs: 2 * budget);

            Assert.True(fast >= ProbeResult.RollbackDepthThreshold, $"fast end should qualify, got {fast}");
            Assert.True(slow < ProbeResult.RollbackDepthThreshold, $"slow end should not, got {slow}");
        }

        [Fact]
        public void Result_FlagsAVerdictThatDependsOnWhichRunYouLookedAt()
        {
            var marginal = new ProbeResult("N64", 1024, 6.0, 1.6, 2.0, 16.683, 4.17,
                maxRollbackDepth: ProbeResult.RollbackDepthThreshold, steadyStateMs: 2.0,
                replayDeterministic: true,
                depthAtWorstFrame: ProbeResult.RollbackDepthThreshold - 1, highFrameMs: 3.58);

            Assert.True(marginal.DepthIsMarginal);
            Assert.True(marginal.RollbackQualified); // the median still decides; the flag informs
            Assert.Contains("MARGINAL", marginal.ToString());

            var solid = new ProbeResult("GPGX", 1024, 0.4, 0.2, 0.2, 16.688, 4.17,
                maxRollbackDepth: 45, steadyStateMs: 0.2, replayDeterministic: true,
                depthAtWorstFrame: 42, highFrameMs: 0.3);

            Assert.False(solid.DepthIsMarginal);
            Assert.DoesNotContain("MARGINAL", solid.ToString());
        }

        [Fact]
        public void Run_ComputesMediansAndDepthFromScriptedTimings()
        {
            var emu = new FakeEmuAdapter(portCount: 2);

            // Probe order: 1 reference save (untimed), then samples of
            // save x100, load x100, frame x100. Feed constant per-op durations.
            var durations = new List<double>();
            durations.AddRange(Enumerable.Repeat(0.10, 100)); // save
            durations.AddRange(Enumerable.Repeat(0.05, 100)); // load
            durations.AddRange(Enumerable.Repeat(0.20, 100)); // frame
            var clock = new ManualClock(durations);

            var probe = new CapabilityProbe(emu, clock, samples: 100);
            var result = probe.Run(frameBudgetMs: 16.639, headroomMs: 4.0);

            Assert.Equal("FakeCore", result.CoreName);
            Assert.Equal(0.10, result.MedianSaveMs, 3);
            Assert.Equal(0.05, result.MedianLoadMs, 3);
            Assert.Equal(0.20, result.MedianFrameMs, 3);
            Assert.True(result.RollbackQualified);

            // Adapter was actually exercised the expected number of times.
            Assert.Equal(102, emu.SaveCount);   // 1 reference + 100 samples + 1 replay-check anchor
            Assert.Equal(103, emu.LoadCount);   // 100 samples + 2 in the replay check + 1 final restore
            Assert.Equal(160, emu.InvisibleFrameCount); // 100 timed + 30 replayed twice
        }
    }
}
