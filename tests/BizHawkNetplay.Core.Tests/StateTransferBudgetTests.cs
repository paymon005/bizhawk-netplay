using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// The state-transfer time budgets, and especially the review-F5 invariant: a frozen survivor's
    /// receive deadline must cover the host's whole three-phase post-rejoin pipeline (state to the
    /// rejoiner → rejoiner import/READY → survivor transfer), each phase of which the host's own
    /// socket deadlines individually allow. A budget of fewer phases ends the session mid-recovery.
    /// </summary>
    public class StateTransferBudgetTests
    {
        [Fact]
        public void OnePhase_IsGracePlusSizeAtFloorRate()
        {
            Assert.Equal(StateTransferBudget.GraceBaseSeconds, StateTransferBudget.OnePhaseSeconds(0));
            Assert.Equal(StateTransferBudget.GraceBaseSeconds, StateTransferBudget.OnePhaseSeconds(-5)); // defensive clamp

            const int fourMiB = 4 * 1024 * 1024;
            double expected = StateTransferBudget.GraceBaseSeconds + fourMiB / StateTransferBudget.MinBytesPerSecond;
            Assert.Equal(expected, StateTransferBudget.OnePhaseSeconds(fourMiB), 6);
            Assert.True(StateTransferBudget.OnePhaseSeconds(fourMiB) > StateTransferBudget.OnePhaseSeconds(fourMiB / 2),
                "budget must scale with state size");
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(64 * 1024, 8)]
        [InlineData(787 * 1024, 60)]        // the measured Genesis state, full reconnect window
        [InlineData(4 * 1024 * 1024, 60)]   // the F5 failure scenario: big state, late rejoin
        [InlineData(32 * 1024 * 1024, 60)]
        public void SurvivorReceiveDeadline_CoversTheHostsWholePipeline(int stateBytes, int waitSeconds)
        {
            // The rejoin can be admitted as late as the end of the wait window, after which the host
            // legitimately spends up to HostPipelinePhases full phases before the survivor's bytes
            // even finish. The survivor's clock starts at BEGIN (the start of the wait window), so
            // its budget must strictly exceed the sum — that strictness is exactly what review
            // finding F5 was about (it budgeted one phase too few and killed healthy recoveries).
            double hostWorstCase = waitSeconds
                + StateTransferBudget.HostPipelinePhases * StateTransferBudget.OnePhaseSeconds(stateBytes);
            Assert.True(
                StateTransferBudget.SurvivorReceiveDeadlineSeconds(stateBytes, waitSeconds) > hostWorstCase,
                $"survivor budget must strictly exceed the host's {StateTransferBudget.HostPipelinePhases}-phase worst case");
        }

        [Fact]
        public void SurvivorReceiveDeadline_IsFinite_AndScalesWithSize()
        {
            // Bounded by construction: BEGIN plus a silent TCP frame must never freeze the emulator
            // forever. And a bigger state buys more time, never less.
            double small = StateTransferBudget.SurvivorReceiveDeadlineSeconds(64 * 1024, 60);
            double large = StateTransferBudget.SurvivorReceiveDeadlineSeconds(8 * 1024 * 1024, 60);
            Assert.True(double.IsFinite(small) && double.IsFinite(large));
            Assert.True(large > small);
        }

        [Fact]
        public void ApplyDeadline_IsExactlyOnePhase()
        {
            const int size = 900 * 1024;
            Assert.Equal(StateTransferBudget.OnePhaseSeconds(size),
                StateTransferBudget.ApplyDeadlineSeconds(size), 6);
        }

        [Fact]
        public void SocketTimeout_FloorsAtHandshakeTimeout_AndScalesWithSize()
        {
            const int floorMs = 15000;
            // A tiny state's phase (10s) is under the floor — keep the ordinary handshake bound.
            Assert.Equal(floorMs, StateTransferBudget.SocketTimeoutMs(0, floorMs));
            // A big state must get the scaled window, not the floor.
            const int fourMiB = 4 * 1024 * 1024;
            int scaled = StateTransferBudget.SocketTimeoutMs(fourMiB, floorMs);
            Assert.Equal((int)(StateTransferBudget.OnePhaseSeconds(fourMiB) * 1000.0), scaled);
            Assert.True(scaled > floorMs);
        }
    }
}
