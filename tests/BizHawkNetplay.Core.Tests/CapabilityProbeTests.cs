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
            Assert.Equal(101, emu.SaveCount);      // 1 reference + 100 samples
            Assert.Equal(100, emu.LoadCount);
            Assert.Equal(100, emu.InvisibleFrameCount);
        }
    }
}
