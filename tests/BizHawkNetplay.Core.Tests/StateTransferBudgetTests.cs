using BizHawkNetplay.Core.Session;
using Xunit;
using static BizHawkNetplay.Core.Tests.Net48Compat;

namespace BizHawkNetplay.Core.Tests;

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
        // legitimately spends up to THREE full phases (state to rejoiner → rejoiner import/READY
        // → survivor transfer) before the survivor's bytes even finish. The survivor's clock
        // starts at BEGIN (the start of the wait window), so its budget must strictly exceed
        // that sum — review finding F5 budgeted one phase too few and killed healthy recoveries.
        // The 3 here is deliberately a literal, independent of HostPipelinePhases: deriving it
        // from the same constant would reduce this test to "slack > 0" and let the exact F5
        // regression pass.
        double hostWorstCase = waitSeconds
            + 3 * (10.0 + stateBytes / (200.0 * 1024));
        Assert.True(
            StateTransferBudget.SurvivorReceiveDeadlineSeconds(stateBytes, waitSeconds) > hostWorstCase,
            "survivor budget must strictly exceed the host's three-phase worst case");
    }

    [Fact]
    public void SurvivorReceiveDeadline_ExactValueForTheF5Scenario()
    {
        // 4 MiB at the 200 KiB/s floor: one phase = 10 + 4194304/204800 = 30.48 s;
        // 60 s wait + 3 × 30.48 + 5 s slack = 156.44 s. Hand-computed, so a change to the
        // phase count or the slack — not just to the shape of the formula — fails here.
        Assert.Equal(156.44,
            StateTransferBudget.SurvivorReceiveDeadlineSeconds(4 * 1024 * 1024, 60), 6);
    }

    [Fact]
    public void SurvivorReceiveDeadline_IsFinite_AndScalesWithSize()
    {
        // Bounded by construction: BEGIN plus a silent TCP frame must never freeze the emulator
        // forever. And a bigger state buys more time, never less.
        double small = StateTransferBudget.SurvivorReceiveDeadlineSeconds(64 * 1024, 60);
        double large = StateTransferBudget.SurvivorReceiveDeadlineSeconds(8 * 1024 * 1024, 60);
        // double.IsFinite is .NET Core only; this project's Core also runs on net48.
        Assert.True(IsFinite(small) && IsFinite(large));
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
