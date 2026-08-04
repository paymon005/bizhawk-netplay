using System.Collections.Generic;
using System.Linq;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

public class CapabilityProbeTests
{
    [Fact]
    public void SolveMaxDepth_LightCore_QualifiesForRollback()
    {
        // NES-class: ~0.2ms frame, tiny state -> very fast save/load.
        int depth = CapabilityProbe.SolveMaxDepth(
            frameBudgetMs: 16.639, headroomMs: 4.0,
            liveFrameMs: 0.2, repairFrameMs: 0.2, loadMs: 0.05, saveMs: 0.05);
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
            liveFrameMs: 8.0, repairFrameMs: 8.0, loadMs: 6.0, saveMs: 6.0);
        Assert.True(depth < ProbeResult.RollbackDepthThreshold);
    }

    [Fact]
    public void SolveMaxDepth_ReturnsZeroWhenBudgetAlreadyBlown()
    {
        int depth = CapabilityProbe.SolveMaxDepth(
            frameBudgetMs: 16.639, headroomMs: 4.0,
            liveFrameMs: 20.0, repairFrameMs: 20.0, loadMs: 1.0, saveMs: 1.0);
        Assert.Equal(0, depth);
    }

    [Fact]
    public void SolveMaxDepth_OmittedArgumentsMeanNoElisionAndNoRepairBudget()
    {
        // The optional arguments that replaced the shorter overloads must carry exactly what those
        // overloads supplied: no elision, one frame period of repair budget. This is the only thing
        // pinning those defaults, and a caller that omits them is relying on them.
        foreach (var (frame, load, save) in new[] { (0.2, 0.05, 0.05), (2.0, 1.5, 6.0), (8.0, 6.0, 6.0) })
            Assert.Equal(
                CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, frame, load, save),
                CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, frame, load, save,
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

        Assert.Equal(0, CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, frame, load, save,
            elideConfirmedSaves: false, repairBudgetMs: generousRepair));
        Assert.True(CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, frame, load, save,
            elideConfirmedSaves: true, repairBudgetMs: generousRepair) > 0);

        // With no repair budget the gate is redundant — the repair sum already returns 0 — so it
        // can never change an answer the plain unelided model gave.
        Assert.Equal(0, CapabilityProbe.SolveMaxDepth(16.639, 4.0, frame, frame, load, save));
    }

    [Fact]
    public void SolveMaxDepth_RealN64Timings_ReachTheThresholdOnlyWithBothChanges()
    {
        // Measured on the N64 core this was built for: save 6.084, load 1.505, frame 1.966,
        // against a 16.683ms budget. The original model says 1 — too shallow to be worth running.
        const double budget = 16.683, headroom = 16.683 * 0.25;
        const double frame = 1.966, load = 1.505, save = 6.084;

        Assert.Equal(1, CapabilityProbe.SolveMaxDepth(budget, headroom, frame, frame, load, save));

        // Elision alone doesn't move the repair sum — it removes the recurring tax, not the cost of
        // re-simulating. Allowing a repair two frame periods is what buys the depth.
        Assert.Equal(1, CapabilityProbe.SolveMaxDepth(budget, headroom, frame, frame, load, save,
            elideConfirmedSaves: true, repairBudgetMs: 0));

        int depth = CapabilityProbe.SolveMaxDepth(budget, headroom, frame, frame, load, save,
            elideConfirmedSaves: true, repairBudgetMs: 2 * budget);
        Assert.Equal(3, depth);
        Assert.True(depth >= ProbeResult.RollbackDepthThreshold);
    }

    /// <summary>
    /// The snapshot spacing was one constant for every core, taken from N64 measurements. What
    /// makes wide spacing pay is the snapshot dominating the frame — nearly 3:1 on N64, well under
    /// 1:1 on the Hawk cores, where the walk-back is pure loss. Both terms are measured by the
    /// time the question is asked, so the machine picks its own.
    /// </summary>
    [Fact]
    public void SolveKeyframeInterval_PicksTheSpacingEachCoreActuallyWants()
    {
        const double budget = 16.683, headroom = 16.683 * 0.25;

        // N64: save 6.084 against a 1.966ms frame. Spacing pays, and the measured knee is 2 —
        // past 3 the walk-back costs more frames than the snapshots it avoids.
        int n64 = CapabilityProbe.SolveKeyframeInterval(budget, headroom, 1.966, 1.966, 1.505, 6.084,
            elideConfirmedSaves: true, repairBudgetMs: 2 * budget);
        Assert.InRange(n64, 2, 3);
        // And it must beat the every-frame spacing it replaced, or there was no reason to solve it.
        Assert.True(
            CapabilityProbe.SolveMaxDepth(budget, headroom, 1.966, 1.966, 1.505, 6.084, true, 2 * budget, n64) >
            CapabilityProbe.SolveMaxDepth(budget, headroom, 1.966, 1.966, 1.505, 6.084, true, 2 * budget, 1),
            "the solved spacing should buy more depth than snapshotting every frame");

        // A light core (GPGX-shaped): the save is a fraction of the frame, so the arithmetic still
        // offers a couple more frames of depth for the walk-back — but at depths in the twenties,
        // which nothing will ever predict to. Past the useful ceiling the extra replayed frames
        // per repair are pure loss, so the cheaper spacing wins.
        int light = CapabilityProbe.SolveKeyframeInterval(budget, headroom, 1.2, 1.2, 0.2, 0.4,
            elideConfirmedSaves: true, repairBudgetMs: 2 * budget);
        Assert.Equal(1, light);
        Assert.True(
            CapabilityProbe.SolveMaxDepth(budget, headroom, 1.2, 1.2, 0.2, 0.4, true, 2 * budget, 1)
                >= CapabilityProbe.UsefulDepthCeiling,
            "the premise is that this core is already past the ceiling at every spacing");
    }

    /// <summary>The probe solves the spacing when asked to (0) and reports the value back, because
    /// the session is then required to run exactly that — a depth checked at one spacing says
    /// nothing about a repair performed at another.</summary>
    [Fact]
    public void Run_ReportsTheKeyframeIntervalItSolvedAgainst()
    {
        var emu = new FakeEmuAdapter(portCount: 2);
        var clock = new ManualClock(Enumerable.Repeat(0.05, 400));

        var asked = new CapabilityProbe(emu, clock, samples: 20)
            .Run(16.639, 4.0, elideConfirmedSaves: true, repairBudgetMs: 33.0, keyframeInterval: 3);
        Assert.Equal(3, asked.KeyframeInterval); // an explicit request is still honoured

        var solved = new CapabilityProbe(emu, clock, samples: 20)
            .Run(16.639, 4.0, elideConfirmedSaves: true, repairBudgetMs: 33.0, keyframeInterval: 0);
        Assert.InRange(solved.KeyframeInterval, 1, CapabilityProbe.MaxKeyframeInterval);
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
        // The verdict flips at about 3.44ms, and N64 at 1400x1050 measures a median of 3.55ms —
        // three consecutive probes of that configuration returned depth 2, 3 and 3. A machine can
        // sit on the line, which is the whole reason the result is reported as marginal rather than
        // as whichever answer the run happened to produce.
        const double budget = 16.683, headroom = 16.683 * 0.25;
        const double load = 1.6, save = 6.0;

        int fast = CapabilityProbe.SolveMaxDepth(budget, headroom, 1.863, 1.863, load, save,
            elideConfirmedSaves: true, repairBudgetMs: 2 * budget);
        int slow = CapabilityProbe.SolveMaxDepth(budget, headroom, 3.582, 3.582, load, save,
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

        // Probe order: 1 reference save (untimed), then samples of save x100, load x100,
        // frame-without-render x100, frame-with-render x100, then three whole-repair passes of 25
        // (samples/4). Feed constant per-op durations. This is a perfectly linear core — the repair
        // durations below are exactly load + depth*frame (+ depth*save on the last) — so every
        // derived term must come back as the isolated figure it was built from.
        var durations = new List<double>();
        durations.AddRange(Enumerable.Repeat(0.10, 100)); // save
        durations.AddRange(Enumerable.Repeat(0.05, 100)); // load
        durations.AddRange(Enumerable.Repeat(0.20, 100)); // frame, rendering off — the repair term
        durations.AddRange(Enumerable.Repeat(0.50, 100)); // frame, rendering on — the live term
        durations.AddRange(Enumerable.Repeat(0.25, 25));  // repair, 1 frame   = 0.05 + 1*0.20
        durations.AddRange(Enumerable.Repeat(1.65, 25));  // repair, 8 frames  = 0.05 + 8*0.20
        durations.AddRange(Enumerable.Repeat(0.65, 25));  // repair, 2 frames re-saving each
                                                         //                   = 0.05 + 2*0.20 + 2*0.10
        var clock = new ManualClock(durations);

        var probe = new CapabilityProbe(emu, clock, samples: 100);
        var result = probe.Run(frameBudgetMs: 16.639, headroomMs: 4.0);

        Assert.Equal("FakeCore", result.CoreName);
        Assert.Equal(0.10, result.MedianSaveMs, 3);
        Assert.Equal(0.05, result.MedianLoadMs, 3);
        Assert.Equal(0.20, result.MedianFrameMs, 3);
        // The two frame costs are kept apart. Scripted differently on purpose: a clock that runs
        // out returns zero-length ops, so a probe that never rendered would pass this silently.
        Assert.Equal(0.50, result.LiveFrameMs, 3);
        Assert.True(result.RollbackQualified);

        // The repair was timed whole, and taking it apart again recovers what it was made of.
        var repair = Assert.IsType<RepairProfile>(result.Repair);
        Assert.Equal(0.25, repair.ShallowMs, 3);
        Assert.Equal(1.65, repair.DeepMs, 3);
        Assert.Equal(0.65, repair.ResavedMs, 3);
        Assert.Equal(2, repair.ResaveDepth);
        Assert.Equal(result.MedianFrameMs, repair.MarginalFrameMs, 3);
        Assert.Equal(result.MedianLoadMs, repair.ImpliedLoadMs, 3);
        Assert.Equal(result.MedianSaveMs, repair.MarginalSaveMs, 3);
        Assert.Equal(0.0, result.RepairModelError, 6);
        Assert.False(result.RepairCostsMoreThanModelled);

        // Adapter was actually exercised the expected number of times.
        // 1 reference + 100 samples + 3 repair anchors + 25*2 repair re-saves + 1 replay anchor.
        Assert.Equal(155, emu.SaveCount);
        // 100 samples + 3 passes of (25 samples + 1 restore) + 2 in the replay check + 1 final.
        Assert.Equal(181, emu.LoadCount);
        // 100 timed + 100 between the save samples + 100 between the load samples
        // + (1+25) + (8+200) + (2+50) across the repair passes + 30 replayed twice.
        Assert.Equal(646, emu.InvisibleFrameCount);
        Assert.Equal(100, emu.RenderedFrameCount);
    }

    [Fact]
    public void SolveMaxDepth_ChargesTheRenderedFrameToTheLiveTermAndTheBareOneToRepair()
    {
        // A repair re-simulates with rendering off; the frame the player sees renders. The old
        // model measured only the first and charged it for both — which is optimistic twice over,
        // since the live cost appears in the steady-state check AND in what the repair has left to
        // spend. Numbers below are the measured N64 save/load against three hypothetical render
        // costs — on Mupen64Plus/Rice the two frame figures turn out to match, so this models the
        // core that does not (a software rasteriser), which is the case the split exists for.
        const double budget = 16.683, headroom = 16.683 * 0.25;
        const double repairFrame = 4.25, load = 1.6, save = 6.0;

        int asIfRenderWereFree = CapabilityProbe.SolveMaxDepth(budget, headroom, repairFrame, repairFrame,
            load, save, elideConfirmedSaves: true, repairBudgetMs: 2 * budget);
        Assert.Equal(2, asIfRenderWereFree);

        // A dearer video plugin buys fewer repaired frames with the same repair budget...
        Assert.Equal(1, CapabilityProbe.SolveMaxDepth(budget, headroom,
            liveFrameMs: 12.0, repairFrameMs: repairFrame, loadMs: load, saveMs: save,
            elideConfirmedSaves: true, repairBudgetMs: 2 * budget));

        // ...and past the point where the live frame no longer fits the budget at all, there is no
        // depth to have. The old figure would still have reported 2 here.
        Assert.Equal(0, CapabilityProbe.SolveMaxDepth(budget, headroom,
            liveFrameMs: 13.0, repairFrameMs: repairFrame, loadMs: load, saveMs: save,
            elideConfirmedSaves: true, repairBudgetMs: 2 * budget));
    }

    [Fact]
    public void Run_TimesSavesAndLoadsAgainstStateThatIsActuallyChanging()
    {
        // Both of these used to be timed in place, and both were cheap for the wrong reason.
        //
        // Saving does not advance the core, so the save pass snapshotted memory nothing had touched
        // since the previous sample, and the load pass then restored the state the core was already
        // standing on — 16.7MiB written back over identical bytes. Measured on N64: 5.6-6.7ms for a
        // save that should be ~7.0, and ~1.4ms for a load that should be ~3.0. Both understate, and
        // both feed the depth verdict, which flipped 4 -> 3 at the recommended settings once the
        // real figures were used.
        var emu = new FakeEmuAdapter(portCount: 2);
        var clock = new ManualClock(Enumerable.Repeat(0.05, 400));

        new CapabilityProbe(emu, clock, samples: 20).Run(16.639, 4.0);

        // The 20 timed saves each captured a frame the one before it had not seen. (Index 0 is the
        // untimed reference save, taken before the pass begins.)
        Assert.Equal(Enumerable.Range(0, 20), emu.SavedAtFrames.Skip(1).Take(20));

        // And nothing anywhere in the probe loads the state it already stands on.
        Assert.DoesNotContain(emu.LoadJumps, j => j.From == j.To);
    }

    [Fact]
    public void SolveMaxDepth_DefaultsToASnapshotOnEveryPredictedFrame()
    {
        // Omitting the spacing must mean one, or every caller that does so quietly changes answer:
        // the search is only the division it replaced while a snapshot is taken every frame.
        foreach (var (frame, load, save) in new[] { (0.2, 0.05, 0.05), (2.0, 1.5, 6.0), (8.0, 6.0, 6.0) })
            foreach (bool elide in new[] { false, true })
                foreach (double repairBudget in new[] { 0.0, 33.366 })
                    Assert.Equal(
                        CapabilityProbe.SolveMaxDepth(16.683, 4.171, frame, frame, load, save, elide, repairBudget),
                        CapabilityProbe.SolveMaxDepth(16.683, 4.171, frame, frame, load, save, elide, repairBudget, 1));
    }

    [Fact]
    public void SolveMaxDepth_SparseKeyframesBuyDepthUntilTheWalkBackEatsIt()
    {
        // The measured N64 terms, taken from the repair rather than in isolation: eight stationary
        // runs at Rice 320x240 give live 2.300, load 3.829, save 6.824, frame 2.414 against a
        // two-frame repair budget. Depth should climb to a peak and come back down, because a
        // repair walks back up to N-1 frames to reach its keyframe and still leaves keyframes
        // behind as it goes — so the snapshots fall as ceil((d+N-1)/N), not as d/N.
        const double budget = 16.683, headroom = 16.683 * 0.25, repairBudget = 2 * 16.683;
        int Depth(int n) => CapabilityProbe.SolveMaxDepth(budget, headroom,
            liveFrameMs: 2.300, repairFrameMs: 2.414, loadMs: 3.829, saveMs: 6.824,
            elideConfirmedSaves: true, repairBudgetMs: repairBudget, keyframeInterval: n);

        Assert.Equal(2, Depth(1));   // a snapshot every frame: what the honest terms say today
        Assert.Equal(3, Depth(2));   // the shipped setting
        Assert.Equal(3, Depth(3));
        Assert.Equal(2, Depth(4));   // past three the walk-back costs more than it saves
        Assert.True(Depth(8) <= Depth(2), "spacing them further apart must not keep paying");
    }

    [Fact]
    public void SolveMaxDepth_ChargesTheWalkBackRatherThanDividingTheSnapshotsAway()
    {
        // The naive version of this change divides the snapshot cost by N and forgets that a repair
        // starts further back. On these terms that would report a depth the repair budget cannot
        // actually pay for, which is the exact failure this whole exercise exists to stop.
        const double budget = 16.683, headroom = 16.683 * 0.25;
        const double frame = 2.414, save = 6.824, load = 3.829, live = 2.300;

        int honest = CapabilityProbe.SolveMaxDepth(budget, headroom, live, frame, load, save,
            elideConfirmedSaves: true, repairBudgetMs: 2 * budget, keyframeInterval: 3);

        double available = 2 * budget - live - load;
        int naive = (int)System.Math.Floor(available / (frame + save / 3.0));

        Assert.True(honest < naive,
            $"the walk-back must cost something: honest {honest} should be under the naive {naive}");

        // And what it does report has to fit, worst case: d+N-1 frames, ceil(that/N) snapshots.
        int frames = honest + 3 - 1;
        int saves = (frames + 3 - 1) / 3;
        Assert.True(frames * frame + saves * save <= available,
            $"depth {honest} does not fit its own budget");
        int overFrames = honest + 1 + 3 - 1;
        int overSaves = (overFrames + 3 - 1) / 3;
        Assert.True(overFrames * frame + overSaves * save > available,
            $"depth {honest + 1} would also have fitted, so the answer is not maximal");
    }

    [Fact]
    public void RepairProfile_IsTrustedOnlyWhenTheTwoPassesDescribeTheSameWork()
    {
        // Accepted: a real stationary in-game run, verbatim — Smash held on a savestate at Rice
        // 320x240, whose isolated frame and load were 2.124ms and 1.560ms. The slope lands within
        // a few percent of the frame, and the intercept sits well above the load because it
        // contains it plus the work the load defers onto the frame after it.
        var steady = new RepairProfile(1, 6.151, 8, 22.610, 8, 74.274);
        Assert.Equal(2.351, steady.MarginalFrameMs, 3);
        Assert.Equal(3.800, steady.ImpliedLoadMs, 3);
        Assert.True(steady.IsSelfConsistentWith(2.124, 1.560));

        // Rejected: the shapes actually produced by probing a booting game, where the workload
        // moved between passes. Ocarina's slope came back at four times its isolated frame; Mario
        // Kart's intercept collapsed to zero, below a load it is supposed to contain.
        var rampingUp = new RepairProfile(1, 2.735, 8, 15.578, 8, 63.510);
        Assert.False(rampingUp.IsSelfConsistentWith(0.599, 1.633));

        var collapsedIntercept = new RepairProfile(1, 2.410, 8, 19.665, 8, 74.398);
        Assert.Equal(0, collapsedIntercept.ImpliedLoadMs, 3);
        Assert.False(collapsedIntercept.IsSelfConsistentWith(1.852, 1.664));

        // And a slope that ran backwards is never usable.
        var backwards = new RepairProfile(1, 20.0, 8, 6.0);
        Assert.False(backwards.IsSelfConsistentWith(2.0, 1.5));
    }

    [Fact]
    public void Run_FallsBackToIsolatedTermsWhenTheRepairDecompositionIsIncoherent()
    {
        // A scripted clock whose repair passes do not describe a steady cost: the deep pass is far
        // too cheap for its depth, so the slope comes out well under the isolated frame. The probe
        // must say so and solve from what it can still trust, rather than quietly using a fit it
        // has no reason to believe.
        var emu = new FakeEmuAdapter(portCount: 2);
        var durations = new List<double>();
        durations.AddRange(Enumerable.Repeat(0.10, 100)); // save
        durations.AddRange(Enumerable.Repeat(0.05, 100)); // load
        durations.AddRange(Enumerable.Repeat(0.20, 100)); // frame, rendering off
        durations.AddRange(Enumerable.Repeat(0.50, 100)); // frame, rendering on
        durations.AddRange(Enumerable.Repeat(2.00, 12));  // repair 1f — implausibly dear
        durations.AddRange(Enumerable.Repeat(2.40, 12));  // repair 8f — barely dearer: slope 0.057
        durations.AddRange(Enumerable.Repeat(3.20, 12));  // ...re-saving each
        var result = new CapabilityProbe(emu, new ManualClock(durations), samples: 100)
            .Run(frameBudgetMs: 16.639, headroomMs: 4.0);

        Assert.NotNull(result.Repair);
        Assert.False(result.SolvedFromRepairTerms);
        Assert.Contains("from isolated terms", result.ToString());

        // Same numbers, solved the old way, must be the same answer — that is what "fell back" means.
        Assert.Equal(
            CapabilityProbe.SolveMaxDepth(16.639, 4.0, result.LiveFrameMs, result.MedianFrameMs,
                result.MedianLoadMs, result.MedianSaveMs, false, 0, 1),
            result.MaxRollbackDepth);
    }

    [Fact]
    public void RepairProfile_ReadsTheSlopeAsTheFrameAndTheInterceptAsTheLoad()
    {
        // Measured N64 terms: load 1.4, frame 2.4, save 5.9. A core that behaves exactly as the
        // depth model assumes produces these repair totals, and taking the line apart returns the
        // three terms — which is the whole point: when the real core does NOT return them, the
        // model is wrong about something and the difference says which term.
        var linear = new RepairProfile(
            shallowDepth: 1, shallowMs: 1.4 + 2.4,
            deepDepth: 8, deepMs: 1.4 + 8 * 2.4,
            resaveDepth: 8, resavedMs: 1.4 + 8 * 2.4 + 8 * 5.9);

        Assert.Equal(2.4, linear.MarginalFrameMs, 3);
        Assert.Equal(1.4, linear.ImpliedLoadMs, 3);
        Assert.Equal(5.9, linear.MarginalSaveMs, 3);

        // And a core where re-simulated frames cost half again as much as isolated ones — the
        // cache-cold case this measurement exists to catch. The decomposition still holds: the
        // slope reports the dearer frame and the load comes back unchanged.
        var cacheCold = new RepairProfile(
            shallowDepth: 1, shallowMs: 1.4 + 3.6,
            deepDepth: 8, deepMs: 1.4 + 8 * 3.6,
            resaveDepth: 8, resavedMs: 1.4 + 8 * 3.6 + 8 * 5.9);

        Assert.Equal(3.6, cacheCold.MarginalFrameMs, 3);
        Assert.Equal(1.4, cacheCold.ImpliedLoadMs, 3);
        Assert.Equal(5.9, cacheCold.MarginalSaveMs, 3);
    }

    [Fact]
    public void RepairProfile_ReadsTheSameSnapshotCostFromAnyResaveDepth()
    {
        // The whole justification for running the re-saving pass shallow -- it is 52% of the probe
        // at depth 8, on the connect path, where it lands as a hitch on joining. The snapshot is a
        // per-frame cost, so reading it off two frames must give what eight would, and at the deep
        // depth the answer must be identical to the difference-of-two-deep-passes this replaced.
        const double load = 1.4, frame = 2.4, save = 5.9;
        double Plain(int d) => load + d * frame;
        double Resaved(int d) => Plain(d) + d * save;

        foreach (int resaveDepth in new[] { 1, 2, 3, 4, 8 })
        {
            var p = new RepairProfile(1, Plain(1), 8, Plain(8), resaveDepth, Resaved(resaveDepth));
            Assert.Equal(save, p.MarginalSaveMs, 6);
        }

        // At the deep depth it is literally the old formula, since the line evaluated there is DeepMs.
        var atDeep = new RepairProfile(1, Plain(1), 8, Plain(8), 8, Resaved(8));
        Assert.Equal((Resaved(8) - Plain(8)) / 8, atDeep.MarginalSaveMs, 6);

        // A pass that was not run reports nothing rather than a negative cost.
        Assert.Equal(0, new RepairProfile(1, Plain(1), 8, Plain(8)).MarginalSaveMs, 6);
    }

    [Fact]
    public void RepairProfile_DoesNotReportANegativeCostForAnythingPhysical()
    {
        // A slope steep enough to extrapolate back past zero means there is no load term to find,
        // not that loading refunds time. The slope itself stays raw, though — a line that does
        // this is telling you the measurement is off, and rounding it to something plausible
        // would be hiding the only evidence of that.
        var steep = new RepairProfile(shallowDepth: 1, shallowMs: 2.0, deepDepth: 8, deepMs: 30.0);

        Assert.Equal(4.0, steep.MarginalFrameMs, 3);
        Assert.Equal(0.0, steep.ImpliedLoadMs, 3);

        var backwards = new RepairProfile(shallowDepth: 1, shallowMs: 20.0, deepDepth: 8, deepMs: 6.0);
        Assert.True(backwards.MarginalFrameMs < 0, "a negative slope must stay visible");
        Assert.Equal(0.0, backwards.MarginalSaveMs, 3);
    }

    [Fact]
    public void RepairProfile_RefusesTwoDepthsWithNoLeverArmBetweenThem()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new RepairProfile(4, 10.0, 4, 10.0));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new RepairProfile(8, 20.0, 1, 4.0));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new RepairProfile(0, 1.0, 8, 20.0));
    }

    [Fact]
    public void Result_FlagsARepairThatCostsMoreThanTheTermsItWasSolvedFrom()
    {
        // Modelled: 1.4 + 8*(2.4 + 5.9) = 67.8ms. Only the optimistic direction is an alarm — a
        // session that predicts as far as a 67.8ms repair allows, on a core where the repair
        // actually takes 90, overruns its budget every time it corrects.
        ProbeResult WithRepair(double measuredDeepResaved) => new(
            "N64", 1024, medianSaveMs: 5.9, medianLoadMs: 1.4, medianFrameMs: 2.4,
            frameBudgetMs: 16.683, headroomMs: 4.17, maxRollbackDepth: 3,
            repair: new RepairProfile(1, 3.8, 8, 20.6, 8, measuredDeepResaved));

        Assert.Equal(67.8, WithRepair(67.8).ModelledRepairMs, 3);

        var optimistic = WithRepair(90.0);
        Assert.True(optimistic.RepairModelError > ProbeResult.RepairModelTolerance);
        Assert.True(optimistic.RepairCostsMoreThanModelled);
        Assert.Contains("REPAIR OVERRUNS MODEL", optimistic.ToString());

        // Within tolerance, and cheaper than modelled, are both fine — and neither may raise it.
        Assert.False(WithRepair(72.0).RepairCostsMoreThanModelled);
        Assert.False(WithRepair(60.0).RepairCostsMoreThanModelled);
        Assert.True(WithRepair(60.0).RepairModelError < 0);
        Assert.DoesNotContain("REPAIR OVERRUNS MODEL", WithRepair(60.0).ToString());
    }

    /// <summary>
    /// The warning above is about the verdict, not about the arithmetic. A load timed on its own is
    /// systematically faster than the same load inside a real repair — nothing has evicted the state
    /// from cache in between — so a repair always overruns a model built from isolated terms. On
    /// GPGX that fired +18% on literally every run while the depth was already being solved from the
    /// repair's own terms, where the optimism cannot reach it. A warning that cannot distinguish
    /// "your verdict is unsafe" from "measurement works the way we documented" is worse than none.
    /// </summary>
    [Fact]
    public void Result_StaysQuietAboutAnOverrunThatCannotReachTheVerdict()
    {
        ProbeResult WithSolveSource(bool solvedFromRepairTerms) => new(
            "GPGX", 806000, medianSaveMs: 0.401, medianLoadMs: 0.237, medianFrameMs: 0.243,
            frameBudgetMs: 16.688, headroomMs: 16.0, maxRollbackDepth: 73,
            repair: new RepairProfile(1, 0.767, 8, 2.459, 2, 1.796),
            solvedFromRepairTerms: solvedFromRepairTerms);

        // Same measurement either way, and it really is over tolerance — this is not a rescaling.
        foreach (var r in new[] { WithSolveSource(true), WithSolveSource(false) })
            Assert.True(r.RepairModelError > ProbeResult.RepairModelTolerance);

        // Solved from the repair's own terms: the isolated optimism never touched the depth.
        Assert.False(WithSolveSource(true).RepairCostsMoreThanModelled);
        Assert.DoesNotContain("REPAIR OVERRUNS MODEL", WithSolveSource(true).ToString());

        // Solved from the isolated terms: the depth rests on figures the repair just contradicted.
        Assert.True(WithSolveSource(false).RepairCostsMoreThanModelled);
        Assert.Contains("REPAIR OVERRUNS MODEL", WithSolveSource(false).ToString());
    }

    [Fact]
    public void Result_SaysNothingAboutRepairCostWhenItWasNeverMeasured()
    {
        // Every ProbeResult built before this existed, and any built by a caller that skips it.
        var noRepair = new ProbeResult("NesHawk", 1024, 0.05, 0.05, 0.2, 16.639, 4.0, 49);

        Assert.Null(noRepair.Repair);
        Assert.False(noRepair.RepairCostsMoreThanModelled);
        Assert.Equal(0, noRepair.RepairModelError);
        Assert.DoesNotContain("repair", noRepair.ToString());
    }

    [Fact]
    public void Run_HandsBackEveryStateAndEveryFrameTheRepairPassesBorrowed()
    {
        // The repair passes load, re-simulate and snapshot far more than anything else here, all
        // against the position the user actually has loaded. They owe back every frame they moved
        // and every state they took.
        var emu = new FakeEmuAdapter(portCount: 2);
        var clock = new ManualClock(Enumerable.Repeat(0.05, 400));
        var before = emu.HashMainMemory();

        var result = new CapabilityProbe(emu, clock, samples: 20).Run(16.639, 4.0);

        Assert.NotNull(result.Repair);
        Assert.Equal(before, emu.HashMainMemory());
        Assert.Empty(emu.LiveStates);
        Assert.True(emu.SaveCount > 20, $"the repair passes should dominate the saves, got {emu.SaveCount}");
        Assert.Equal(emu.SaveCount, emu.ReleaseCount);
    }
}
