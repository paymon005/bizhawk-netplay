using BizHawkNetplay.Core.Probe;
using Xunit;
using Xunit.Abstractions;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The N64 depth verdict, worked against terms measured on real hardware.
///
/// Every figure here came off one machine on 2026-08-06 (laptop, Mupen64Plus + Rice, 16.3MiB
/// state), from the capability probe's own repair-derived terms — the ones the solver actually
/// uses, not the isolated ones. They are written down as a test because the interesting question is
/// no longer "what does this machine measure" but "which constant is holding the verdict down", and
/// that is arithmetic anyone can re-run when a term changes.
///
/// The conclusion the numbers force: <b>the binding constraint is the repair BUDGET, a policy
/// constant, not any measured cost.</b> Save, load and frame are all at or near their floors — the
/// savestate arrives in one memcpy and the write path adds 0.03ms over a raw copy — so nothing on
/// the cost side is available. The budget is.
/// </summary>
public class N64BudgetTests
{
    private readonly ITestOutputHelper _out;
    public N64BudgetTests(ITestOutputHelper output) => _out = output;

    private const double FrameMs = 16.683;
    private const double Headroom = FrameMs * 0.25;

    // Measured 2026-08-06, repair-derived terms, 320x240.
    private const double LowLive = 4.127, LowFrame = 4.328, LowSave = 4.024, LowLoad = 6.900;

    // The same machine at 800x600.
    private const double HighLive = 6.990, HighFrame = 6.158, HighSave = 3.528, HighLoad = 6.945;

    private static int DepthAt(double budgetFrames, double live, double frame, double save,
        double load, int keyframes = 1) =>
        CapabilityProbe.SolveMaxDepth(FrameMs, Headroom, live, frame, load, save,
            elideConfirmedSaves: true, repairBudgetMs: budgetFrames * FrameMs,
            keyframeInterval: keyframes);

    /// <summary>
    /// What ships today: a repair may take two frame periods, and N64 gets depth 2 — one short of
    /// the three it needs to qualify, at both resolutions. This is the result ten consecutive
    /// probes reproduced without once flipping.
    /// </summary>
    [Fact]
    public void AtTheShippingRepairBudgetN64FallsOneShortOfQualifying()
    {
        Assert.Equal(2, DepthAt(2, LowLive, LowFrame, LowSave, LowLoad));
        Assert.Equal(2, DepthAt(2, HighLive, HighFrame, HighSave, HighLoad));
        Assert.True(2 < ProbeResult.RollbackDepthThreshold);
    }

    /// <summary>
    /// <b>Allowing a repair three frame periods instead of two qualifies it, at both
    /// resolutions.</b>
    ///
    /// This is the whole finding. Nothing measured has to change for N64 to qualify; the question
    /// is only whether a deeper worst-case repair is a price worth paying, which is a judgement
    /// about hitches and not an arithmetic fact — hence a setting rather than a new constant.
    ///
    /// The repair budget is <c>frame periods per tick × frame period</c>, and the two halves of
    /// that are tied deliberately rather than by accident: a repair spending N periods leaves N
    /// frames due when it returns, and a tick clears at most the cap, so at equality the next tick
    /// clears the debt exactly and above it the arrears grow until a rebase discards them. Raising
    /// one therefore has to raise the other, which is why the control is one number.
    /// </summary>
    [Fact]
    public void AThreeFrameRepairBudgetWouldQualifyN64AtBothResolutions()
    {
        int low = DepthAt(3, LowLive, LowFrame, LowSave, LowLoad);
        int high = DepthAt(3, HighLive, HighFrame, HighSave, HighLoad);
        _out.WriteLine($"320x240 -> depth {low}; 800x600 -> depth {high}");

        Assert.True(low >= ProbeResult.RollbackDepthThreshold,
            $"320x240 gave depth {low} at a three-frame budget");
        Assert.True(high >= ProbeResult.RollbackDepthThreshold,
            $"800x600 gave depth {high} at a three-frame budget");
    }

    /// <summary>
    /// How far each measured term would have to fall, on its own, to reach depth 3 at the shipping
    /// budget — so the alternatives to changing the budget are priced rather than waved away.
    ///
    /// I first assumed none of them could do it. That was wrong, and the solver said so: the
    /// margins are small. Which matters, because it changes the conclusion from "only the budget
    /// can move this" to "the budget is the only one of them we can actually reach" — the save is
    /// one memcpy plus core work, the load is core work plus deferred core work, and the frame is
    /// the emulator. All three are somebody else's to improve.
    /// </summary>
    [Fact]
    public void EachTermsDistanceFromQualifyingIsSmallButOutOfReach()
    {
        double Needed(double term, System.Func<double, int> depthWith)
        {
            for (double cut = 0; cut <= term; cut += 0.01)
                if (depthWith(term - cut) >= ProbeResult.RollbackDepthThreshold) return cut;
            return double.NaN;
        }

        double save = Needed(LowSave, s => DepthAt(2, LowLive, LowFrame, s, LowLoad));
        double frame = Needed(LowFrame, f => DepthAt(2, LowLive, f, LowSave, LowLoad));
        double load = Needed(LowLoad, l => DepthAt(2, LowLive, LowFrame, LowSave, l));

        _out.WriteLine($"320x240, to reach depth {ProbeResult.RollbackDepthThreshold} at the " +
                       $"shipping 2-frame budget:");
        _out.WriteLine($"  save  {LowSave:F3}ms -> needs -{save:F2}ms ({save / LowSave:P0})");
        _out.WriteLine($"  frame {LowFrame:F3}ms -> needs -{frame:F2}ms ({frame / LowFrame:P0})");
        _out.WriteLine($"  load  {LowLoad:F3}ms -> needs -{load:F2}ms ({load / LowLoad:P0})");

        // None is a large cut; all three are the core's, not ours. The point of the assertion is
        // that they are genuinely close — a claim of "impossible" would have been overstated.
        Assert.True(save < LowSave, "a cheaper save alone would qualify, if one were available");
        Assert.True(frame < LowFrame, "a cheaper frame alone would qualify");
        Assert.True(load < LowLoad, "a cheaper load alone would qualify");
    }

    /// <summary>
    /// Wider keyframe spacing does not rescue it, which is why the probe reports spacing 1.
    ///
    /// Sparse snapshots pay when the save dominates the frame — the ~3:1 ratio the original N64
    /// note was written against. Here it is ~1.1:1, so the extra frames walked back cost more than
    /// the skipped snapshot saves.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WiderKeyframeSpacingDoesNotRescueItAtTheShippingBudget(int keyframes)
    {
        Assert.True(DepthAt(2, LowLive, LowFrame, LowSave, LowLoad, keyframes)
            < ProbeResult.RollbackDepthThreshold);
    }

    /// <summary>
    /// Steady state was never the problem, and this is worth pinning because it is the thing most
    /// people would try first: N64 uses about a third of its steady allowance. Lowering resolution,
    /// which moves the live frame substantially, does not move the verdict — both resolutions gave
    /// depth 2.
    /// </summary>
    [Fact]
    public void SteadyStateHasRoomToSpareAtBothResolutions()
    {
        double allowance = FrameMs - Headroom;
        Assert.True(LowLive < allowance * 0.6, $"{LowLive}ms of a {allowance:F2}ms allowance");
        Assert.True(HighLive < allowance * 0.6, $"{HighLive}ms of a {allowance:F2}ms allowance");
    }
}
