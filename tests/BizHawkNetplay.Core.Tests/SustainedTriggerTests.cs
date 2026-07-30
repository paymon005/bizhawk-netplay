using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The once-per-session advisory policy. Written out by hand twice in the tool, in a UI callback
/// where nothing could reach it — which is how both copies stayed unverified.
/// </summary>
public class SustainedTriggerTests
{
    [Fact]
    public void DoesNotFireUntilTheConditionHasHeldForTheWholeWindow()
    {
        var trigger = new SustainedTrigger(5000);

        Assert.False(trigger.ShouldFire(true, 1000));  // starts the window, says nothing yet
        Assert.False(trigger.ShouldFire(true, 3000));  // 2s in
        Assert.False(trigger.ShouldFire(true, 5999));  // 4.999s in — still short
        Assert.True(trigger.ShouldFire(true, 6000));   // exactly 5s
    }

    [Fact]
    public void OneGoodSampleClearsTheWindowEntirely()
    {
        var trigger = new SustainedTrigger(5000);
        trigger.ShouldFire(true, 0);
        Assert.False(trigger.ShouldFire(true, 4000));

        // An intermittent problem must never accumulate its way to firing: the clock restarts,
        // it does not pause.
        Assert.False(trigger.ShouldFire(false, 4500));
        Assert.False(trigger.ShouldFire(true, 5000));   // window restarts here
        Assert.False(trigger.ShouldFire(true, 9000));   // 4s into the NEW window
        Assert.True(trigger.ShouldFire(true, 10000));   // 5s into it
    }

    [Fact]
    public void FiresOnceAndStaysQuietHoweverLongTheProblemLasts()
    {
        var trigger = new SustainedTrigger(1000);
        trigger.ShouldFire(true, 0);
        Assert.True(trigger.ShouldFire(true, 1000));
        Assert.True(trigger.HasFired);

        for (int t = 2000; t < 60000; t += 1000)
            Assert.False(trigger.ShouldFire(true, t));
    }

    [Fact]
    public void ZeroSustainIsAPlainOnceOnlyLatch()
    {
        var trigger = new SustainedTrigger(0);
        Assert.True(trigger.ShouldFire(true, 500));
        Assert.False(trigger.ShouldFire(true, 501));
    }

    [Fact]
    public void RestartingTheWindowDoesNotUnsayWhatWasAlreadySaid()
    {
        var trigger = new SustainedTrigger(1000);
        trigger.ShouldFire(true, 0);
        Assert.True(trigger.ShouldFire(true, 1000));

        // A frame-schedule rebase invalidates the measurement, not the advice already given.
        trigger.RestartWindow();
        Assert.True(trigger.HasFired);
        Assert.False(trigger.ShouldFire(true, 5000));
    }

    [Fact]
    public void ResetIsANewSessionAndSaysEverythingAgain()
    {
        var trigger = new SustainedTrigger(1000);
        trigger.ShouldFire(true, 0);
        Assert.True(trigger.ShouldFire(true, 1000));

        trigger.Reset();
        Assert.False(trigger.HasFired);
        // And the window is clear too, so the first bad sample of the new session starts it rather
        // than inheriting a deadline from the old one.
        Assert.False(trigger.ShouldFire(true, 1001));
        Assert.True(trigger.ShouldFire(true, 2001));
    }
}
