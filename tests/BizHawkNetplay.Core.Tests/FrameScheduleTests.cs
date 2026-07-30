using BizHawkNetplay.Core.Sync;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// The pacing arithmetic that turns an irregular UI callback into a steady frame rate. It lived
    /// inside a 378-line callback with core stepping, audio and telemetry, so none of it could be
    /// exercised without an emulator — which is why every judder question so far has been answered by
    /// reading a log rather than by running something.
    /// </summary>
    public class FrameScheduleTests
    {
        private const double Frame60 = 1000.0 / 60.0;   // 16.667ms

        private static FrameSchedule Sixty() => new FrameSchedule(Frame60, minBudgetMs: 8.0, maxFramesPerTick: 2);

        [Fact]
        public void AnEarlyCallbackTakesTheFrameItNearlyEarned()
        {
            // The bug this exists for: with a strict due time, a callback landing a few ms early runs
            // nothing, so the next finds two due, runs both and shows only the second. One picture lost
            // per pair — presented frames sat near 50 while the core emulated a steady 60.
            var s = Sixty();
            s.Restart(0);

            Assert.True(s.MayRunFrame(nowMs: 0, framesAlreadyRun: 0));
            s.FrameCompleted(1.0);                       // next due at 16.667

            // 8ms early is inside half a period, so it runs now rather than doubling up later.
            Assert.True(s.MayRunFrame(nowMs: 8.7, framesAlreadyRun: 0));
        }

        [Fact]
        public void ButNotArbitrarilyEarly_SoEmulationCannotRunAwayFromTheClock()
        {
            var s = Sixty();
            s.Restart(0);
            s.FrameCompleted(1.0);                       // next due at 16.667, tolerance is 8.333

            Assert.False(s.MayRunFrame(nowMs: 8.0, framesAlreadyRun: 0));
            Assert.True(s.MayRunFrame(nowMs: 8.4, framesAlreadyRun: 0));
        }

        [Fact]
        public void LaterFramesOfACallbackGetNoSuchLatitude()
        {
            // Frame 2 must be genuinely due, or a catch-up burst would be self-justifying.
            var s = Sixty();
            s.Restart(0);
            s.FrameCompleted(1.0);   // due 16.667

            Assert.False(s.MayRunFrame(nowMs: 12.0, framesAlreadyRun: 1));
            Assert.True(s.MayRunFrame(nowMs: 16.5, framesAlreadyRun: 1));
        }

        [Fact]
        public void ACallbackNeverRunsMoreFramesThanItsCap()
        {
            var s = new FrameSchedule(Frame60, 8.0, maxFramesPerTick: 2);
            s.Restart(0);
            Assert.True(s.MayRunFrame(1000, 0));
            Assert.True(s.MayRunFrame(1000, 1));
            Assert.False(s.MayRunFrame(1000, 2));   // however far behind it is
        }

        [Fact]
        public void DebtIsDiscardedOnlyOnceItIsRealDebt()
        {
            var s = Sixty();
            s.Restart(0);

            Assert.False(s.TryRebase(2 * Frame60));   // ordinary lateness is chased, not discarded
            Assert.False(s.TryRebase(3 * Frame60));   // exactly at the threshold is still chased
            Assert.True(s.TryRebase(3 * Frame60 + 1));

            // And rebasing means owing nothing from here, so the very next frame is due immediately.
            Assert.True(s.MayRunFrame(3 * Frame60 + 1, 0));
            Assert.False(s.TryRebase(3 * Frame60 + 2));
        }

        [Fact]
        public void TheSecondFrameIsRefusedUntilACoreFrameHasActuallyBeenTimed()
        {
            // A burst decided on no evidence is how a heavy core ends up committing to work it cannot
            // finish inside the callback.
            var s = Sixty();
            s.Restart(0);
            Assert.False(s.AnotherFrameFits(nowMs: 1000, framesAlreadyRun: 0, tickElapsedMs: 0));

            s.FrameCompleted(0.5);
            Assert.True(s.AnotherFrameFits(nowMs: 1000, framesAlreadyRun: 0, tickElapsedMs: 0));
        }

        [Fact]
        public void TheSecondFrameIsRefusedWhenTwoMoreWouldNotFitTheBudget()
        {
            var s = Sixty();
            s.Restart(0);
            s.FrameCompleted(6.0);                  // a costly core: budget is max(8, 1.7*16.667) = 28.3

            // 6ms already spent + 2x6ms estimated = 18 < 28.3: room.
            Assert.True(s.AnotherFrameFits(1000, 0, tickElapsedMs: 6.0));
            // 17ms spent + 12 = 29 > 28.3: no.
            Assert.False(s.AnotherFrameFits(1000, 0, tickElapsedMs: 17.0));
        }

        [Fact]
        public void TheCostEstimateRisesInstantlyAndDecaysSlowly()
        {
            // Pessimism is nearly free here and optimism is expensive, so a spike is adopted at once
            // and forgotten a tenth at a time.
            var s = Sixty();
            s.Restart(0);
            s.FrameCompleted(1.0);
            Assert.Equal(1.0, s.RecentCoreFrameMs, 3);

            s.FrameCompleted(9.0);
            Assert.Equal(9.0, s.RecentCoreFrameMs, 3);      // straight to the spike

            s.FrameCompleted(1.0);
            Assert.Equal(8.1, s.RecentCoreFrameMs, 3);      // 10% closer, not back to 1.0
        }

        [Fact]
        public void BudgetScalesWithTheFramePeriodRatherThanSittingAtItsFloor()
        {
            // A flat 8ms made the two-frame gate unreachable for any core costing over ~4ms a frame,
            // so the debt it could not repay accumulated until a rebase dropped it in a lump — which
            // reads as CPU-bound for a core comfortably inside budget.
            Assert.Equal(28.33, new FrameSchedule(Frame60, 8.0, 2).BudgetMs, 2);
            Assert.Equal(34.0, new FrameSchedule(20.0, 8.0, 2).BudgetMs, 2);   // 50Hz PAL
            Assert.Equal(8.0, new FrameSchedule(2.0, 8.0, 2).BudgetMs, 2);     // floor still applies
        }

        [Fact]
        public void ASkippedFrameStillPaysItsDebtButRecordsNoCost()
        {
            // Rollback's time-sync yield hands frames back in emulated-frame units, not callbacks.
            var s = Sixty();
            s.Restart(0);
            s.FrameCompleted(4.0);
            double estimate = s.RecentCoreFrameMs;
            double due = s.NextDueMs;

            s.SkipFrame();
            Assert.Equal(due + Frame60, s.NextDueMs, 3);
            Assert.Equal(estimate, s.RecentCoreFrameMs, 3);
        }

        [Fact]
        public void ARebaseKeepsTheCostEstimateButARestartDoesNot()
        {
            // A protocol pause did not make the core cheaper, and discarding the measurement would
            // forbid the next callback from bursting on the grounds that nothing has been timed.
            var s = Sixty();
            s.Restart(0);
            s.FrameCompleted(5.0);

            s.RebaseTo(9999);
            Assert.Equal(5.0, s.RecentCoreFrameMs, 3);
            Assert.Equal(9999, s.NextDueMs, 3);

            s.Restart(0);
            Assert.Equal(0.0, s.RecentCoreFrameMs, 3);
        }

        [Fact]
        public void LongRunRateIsExactlyOneFramePerPeriodDespiteEarlyStarts()
        {
            // The tolerance must not let emulation drift ahead of the wall clock: it changes WHEN a
            // frame runs, never how many are owed.
            var s = Sixty();
            s.Restart(0);
            int frames = 0;
            for (double now = 0; now < 10_000; now += 5.0)
            {
                // Per CALLBACK, not global parity: the argument is "how many has THIS callback run",
                // and passing frames % 2 made every other callback start as though it had already run
                // one — which suppresses exactly the first-frame tolerance this test is about.
                int framesThisCallback = 0;
                while (s.MayRunFrame(now, framesThisCallback))
                {
                    s.FrameCompleted(0.5);
                    frames++;
                    framesThisCallback++;   // MayRunFrame's own cap ends the callback at two
                }
            }

            // 10 seconds at 60Hz is 600 frames, give or take the half-period of slack.
            Assert.InRange(frames, 598, 602);
        }
    }
}
