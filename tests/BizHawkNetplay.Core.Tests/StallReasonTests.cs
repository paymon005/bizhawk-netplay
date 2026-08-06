using System.Linq;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Four things stall a session and they blame four different culprits: the link, the clock, this
/// machine, and — for lockstep — nothing at all, since blocking is what lockstep is.
///
/// The caller used to read a single <c>LastStallWasTimeSync</c> bool, which was true for the
/// cost-cap stall as well. That was right about the scheduling (both are frames given up on
/// purpose, so both should cost a whole frame period rather than a retry) and wrong about the
/// cause: a machine too slow to repair reported itself in the log as a clock-skew problem, and the
/// most common stall of all — waiting at the hard prediction cap — had neither a name nor a
/// counter. In a tool whose whole job is telling a user why their game is stuttering, that is the
/// diagnostic that matters most.
///
/// These pin the mapping, and pin that the scheduling did not change while the names did.
/// </summary>
public class StallReasonTests
{
    private const double FrameMs = 16.639;
    private const int MaxRollback = 12;
    private const int Ports = 2;

    private static RollbackStrategy NewStrategy(out FakeEmuAdapter emu, out InputPipeline pipeline,
        RollbackTuning? tuning = null)
    {
        emu = new FakeEmuAdapter(portCount: Ports);
        pipeline = new InputPipeline(Ports);
        for (int p = 0; p < Ports; p++) pipeline.SetLocal(p, p == 0);
        return new RollbackStrategy(pipeline, emu, localPort: 0, maxRollback: MaxRollback,
            frameMs: FrameMs, tuning: tuning);
    }

    [Fact]
    public void AFrameThatRunsHasNoStallReason()
    {
        var s = NewStrategy(out var emu, out var pipe);
        pipe.Add(1, 0, PortInput.Neutral(emu.GetControllerLayout(1)));
        Assert.False(s.BeginFrame(0).Stall);
        Assert.Equal(StallReason.None, s.LastStallReason);
    }

    /// <summary>
    /// The ordinary stall: a remote port has gone quiet long enough that another frame would put
    /// the correction target outside the ring. The link is behind, not the machine and not the
    /// clock — and this is the one the old bool reported by saying nothing.
    /// </summary>
    [Fact]
    public void RunningPastTheRingWithNoRemoteInputBlamesThePredictionLimit()
    {
        var s = NewStrategy(out _, out _);
        Assert.True(s.BeginFrame(MaxRollback + 1).Stall);
        Assert.Equal(StallReason.PredictionLimit, s.LastStallReason);
        Assert.Equal(1, s.PredictionLimitStalls);
        Assert.Equal(0, s.TimeSyncStalls);
        Assert.Equal(0, s.CostStalls);
    }

    /// <summary>
    /// Waiting is not a yield. The frame becomes runnable the instant the missing datagram lands,
    /// so a scheduler that paid a whole frame period for it would be adding latency the link never
    /// asked for. This is the half of the old bool that was already correct, kept correct.
    /// </summary>
    [Fact]
    public void WaitingForTheLinkIsNotADeliberateYield()
    {
        var s = NewStrategy(out _, out _);
        Assert.True(s.BeginFrame(MaxRollback + 1).Stall);
        Assert.False(s.LastStallReason.IsDeliberateYield());
        Assert.False(s.LastStallReason.IsClockSkew());
    }

    [Fact]
    public void MeasuredAdvantageDebtBlamesTheClock()
    {
        var s = NewStrategy(out var emu, out var pipe);
        var neutral = PortInput.Neutral(emu.GetControllerLayout(1));
        pipe.Add(1, 0, neutral);   // the frontier is healthy; only the pacing debt can stall us
        s.OnPacingReport(new PacingInfo(20, frameAdvantage: 4, hasFrameAdvantage: true,
            sampleSequence: 1));

        Assert.True(s.BeginFrame(0).Stall);
        Assert.Equal(StallReason.FrameAdvantage, s.LastStallReason);
        Assert.True(s.LastStallReason.IsClockSkew());
        Assert.True(s.LastStallReason.IsDeliberateYield());
        Assert.Equal(0, s.PredictionLimitStalls);
    }

    /// <summary>
    /// The one the old bool got wrong. A cost-cap stall says this machine cannot repair the depth
    /// it is predicting inside its frame budget — the answer is more input delay or a lighter core,
    /// and it has nothing whatever to do with either peer's clock. It reported itself as a
    /// "time-sync yield", which points the user at the opposite fix.
    /// </summary>
    [Fact]
    public void TheCostCapBlamesThisMachineRatherThanTheClock()
    {
        // Every repair is scripted to cost 100ms against a 5ms allowance, so the cap collapses to
        // its floor and starts refusing frames well inside the ring.
        var tuning = new RollbackTuning
        {
            RepairBudgetMs = 5.0,
            Clock = new ManualClock(Enumerable.Repeat(100.0, 20000)),
        };
        var s = NewStrategy(out var emu, out var pipe, tuning);
        var layout = emu.GetControllerLayout(1);
        var neutral = PortInput.Neutral(layout);
        var pressed = new PortInput(
            Enumerable.Range(0, neutral.Buttons.Length).Select(i => i == 0).ToArray(),
            neutral.Axes);

        // Two confirmed frames, then two predicted ones. Nothing has been repaired yet, so the cap
        // is still the ring depth — the strategy trims only from a repair it has actually timed.
        for (int f = 0; f < 2; f++) pipe.Add(1, f, neutral);
        for (int f = 0; f < 4; f++)
        {
            var d = s.BeginFrame(f);
            Assert.False(d.Stall);
            emu.AdvanceRenderedFrame(d.Inputs!);
            s.EndFrame(f);
        }

        // Contradict the prediction at frame 2. The repair that follows is scripted at 100ms
        // against a 5ms allowance, which is what teaches the cap how little this machine can afford.
        pipe.Add(1, 2, pressed);
        s.OnRemoteInput(2, 1);   // the drain tells the strategy; the pipeline add alone does not

        StallReason seen = StallReason.None;
        for (int f = 4; f < 4 + MaxRollback + 4 && seen != StallReason.RepairBudget; f++)
        {
            var d = s.BeginFrame(f);
            if (d.Stall) seen = s.LastStallReason;
            else { emu.AdvanceRenderedFrame(d.Inputs!); s.EndFrame(f); }
        }

        Assert.True(s.RollbackCount > 0, "the scenario must actually repair, or nothing is timed");
        Assert.True(s.CostCap < MaxRollback,
            $"the cap should have tightened below the ring depth, stayed at {s.CostCap}");
        Assert.Equal(StallReason.RepairBudget, seen);
        Assert.True(s.CostStalls > 0);
        Assert.True(seen.IsDeliberateYield(), "the frame is given up on purpose, so the scheduler " +
            "must still pay a whole frame period — that part of the old bool was right");
        Assert.False(seen.IsClockSkew(), "and this is the part that was wrong");
    }

    /// <summary>
    /// Lockstep has exactly one reason to refuse and it is never a yield. The caller reached for
    /// the reason through a type test against RollbackStrategy, so lockstep's stall fell through to
    /// whatever the rollback wording happened to be.
    /// </summary>
    [Fact]
    public void LockstepNamesItsOnlyStall()
    {
        var emu = new FakeEmuAdapter(portCount: Ports);
        var pipe = new InputPipeline(Ports);
        for (int p = 0; p < Ports; p++) pipe.SetLocal(p, p == 0);
        ISyncStrategy s = new LockstepStrategy(pipe);

        Assert.True(s.BeginFrame(0).Stall);
        Assert.Equal(StallReason.MissingRemoteInput, s.LastStallReason);
        Assert.False(s.LastStallReason.IsDeliberateYield());

        pipe.Add(0, 0, PortInput.Neutral(emu.GetControllerLayout(0)));
        pipe.Add(1, 0, PortInput.Neutral(emu.GetControllerLayout(1)));
        Assert.False(s.BeginFrame(0).Stall);
        Assert.Equal(StallReason.None, s.LastStallReason);
    }

    /// <summary>
    /// The set the scheduler acts on, stated once rather than inferred at each call site. Waiting
    /// retries; yielding pays a frame.
    /// </summary>
    [Theory]
    [InlineData(StallReason.None, false, false)]
    [InlineData(StallReason.MissingRemoteInput, false, false)]
    [InlineData(StallReason.PredictionLimit, false, false)]
    [InlineData(StallReason.TimeSyncSoftCap, true, true)]
    [InlineData(StallReason.RepairBudget, true, false)]
    [InlineData(StallReason.FrameAdvantage, true, true)]
    public void YieldAndSkewClassificationIsFixed(StallReason reason, bool yield, bool skew)
    {
        Assert.Equal(yield, reason.IsDeliberateYield());
        Assert.Equal(skew, reason.IsClockSkew());
        Assert.False(string.IsNullOrWhiteSpace(reason.Describe()));
    }
}
