using BizHawkNetplay.Core.Sync;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Windowed deltas of counters that belong to an object which gets replaced mid-session. The
/// failure this exists to prevent was observed in a live 4-player session, as
/// <c>saves -15 taken/-4652 elided</c> in the first window after a netcode change.
/// </summary>
public class CounterWindowTests
{
    [Fact]
    public void ReportsProgressSinceTheLastObservation()
    {
        var window = new CounterWindow();
        Assert.Equal(0, window.Observe(0));
        Assert.Equal(10, window.Observe(10));
        Assert.Equal(5, window.Observe(15));
        Assert.Equal(0, window.Observe(15)); // a quiet window is zero, not a repeat of the total
    }

    [Fact]
    public void AReplacedCounterReportsItsOwnProgress_NotANegativeNumber()
    {
        var window = new CounterWindow();
        window.Observe(4652);   // a strategy that ran for a while

        // Resync, reconnect or a netcode change: same field, brand-new object, counting from zero.
        // Three saves in before the window closes.
        long afterRebuild = window.Observe(3);

        Assert.Equal(3, afterRebuild);
        // And the window is now baselined on the NEW counter, so the next one is an ordinary delta
        // rather than a second correction.
        Assert.Equal(4, window.Observe(7));
    }

    [Fact]
    public void ARebuildObservedBeforeTheNewCounterMovesReportsNothing()
    {
        var window = new CounterWindow();
        window.Observe(900);
        // The window closed immediately after the rebuild — the new strategy has done nothing yet,
        // which must read as nothing rather than as 900 or -900.
        Assert.Equal(0, window.Observe(0));
        Assert.Equal(12, window.Observe(12));
    }

    [Fact]
    public void RepeatedRebuildsEachStartCleanly()
    {
        var window = new CounterWindow();
        for (int session = 0; session < 5; session++)
        {
            Assert.Equal(6, window.Observe(6));
            Assert.Equal(4, window.Observe(10));
        }
    }

    [Fact]
    public void ResetForgetsTheBaseline()
    {
        var window = new CounterWindow();
        window.Observe(500);
        window.Reset();
        // A new session's counter starting at 500 again is 500 of progress, not zero — Reset means
        // "count from nothing", which is what a session start is.
        Assert.Equal(500, window.Observe(500));
    }
}
