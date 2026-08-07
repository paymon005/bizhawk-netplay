using BizHawkNetplay.Core.Sync;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The per-tick frame cap, now that it is settable.
///
/// It became settable because it is the constant a heavy core is short on: it is also the repair
/// budget (the caller ties them, and must — a repair spending N frame periods leaves N frames due
/// and a tick clears at most this many), and on real N64 the difference between two and three is
/// the difference between depth 2 and depth 3-4, which is the difference between lockstep and
/// rollback. See <c>N64BudgetTests</c> for that arithmetic.
///
/// The first duty of these is to show the default did not move. A knob that changes behaviour at
/// its old value is not a knob, it is a regression with a dial on it.
/// </summary>
public class FrameScheduleBudgetTests
{
    private const double FrameMs = 16.683;
    private const double MinBudget = 8.0;

    private static FrameSchedule New(int cap = 2) => new(FrameMs, MinBudget, cap);

    /// <summary>
    /// At a cap of two the tick budget is exactly 1.7 frame periods, which is the figure everything
    /// written about this behaviour was measured against. The generalised formula has to reproduce
    /// it exactly, not approximately.
    /// </summary>
    [Fact]
    public void TheDefaultCapKeepsTheBudgetItAlwaysHad()
    {
        Assert.Equal(1.7 * FrameMs, New(2).BudgetMs, 10);
    }

    /// <summary>
    /// The budget has to scale with the cap or raising the cap does nothing.
    ///
    /// The gate asks whether two more frames fit the REMAINING budget, so a budget fixed at 1.7
    /// periods would refuse the third frame of a three-frame tick and quietly undo the raise —
    /// leaving someone to conclude the setting does not work, which is worse than not having it.
    /// </summary>
    [Fact]
    public void RaisingTheCapRaisesTheBudgetWithIt()
    {
        Assert.True(New(3).BudgetMs > New(2).BudgetMs);
        Assert.Equal(2.7 * FrameMs, New(3).BudgetMs, 10);
        Assert.Equal(3.7 * FrameMs, New(4).BudgetMs, 10);
    }

    /// <summary>The floor still wins on a fast core, which is what it is for.</summary>
    [Fact]
    public void TheMinimumBudgetStillAppliesToShortFramePeriods()
    {
        var quick = new FrameSchedule(frameMs: 1.0, minBudgetMs: MinBudget, maxFramesPerTick: 2);
        Assert.Equal(MinBudget, quick.BudgetMs, 10);
    }

    [Fact]
    public void TheCapCanBeRaisedAfterConstruction()
    {
        var s = New(2);
        s.MaxFramesPerTick = 3;
        Assert.Equal(3, s.MaxFramesPerTick);
        Assert.Equal(2.7 * FrameMs, s.BudgetMs, 10);
    }

    /// <summary>
    /// A cap below one would let a tick run no frames at all, which stops the session dead rather
    /// than slowing it. Refused the same way <see cref="FrameSchedule.FrameMs"/> refuses zero:
    /// silently keeping the last good value, because this is fed from a UI control and throwing
    /// into a WinForms event handler helps nobody.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ACapBelowOneIsIgnoredRatherThanAccepted(int bad)
    {
        var s = New(2);
        s.MaxFramesPerTick = bad;
        Assert.Equal(2, s.MaxFramesPerTick);
    }

    /// <summary>The cap governs how many frames a tick may run — the property the repair budget's
    /// invariant leans on, driven rather than asserted about.</summary>
    [Fact]
    public void ATickRunsAtMostTheCapManyFrames()
    {
        foreach (int cap in new[] { 1, 2, 3, 4 })
        {
            var s = New(cap);
            s.Restart(0);
            // Far enough past due that every frame is owed; only the cap can stop it.
            double now = 1000;
            int ran = 0;
            while (s.MayRunFrame(now, ran)) ran++;
            Assert.Equal(cap, ran);
        }
    }
}
