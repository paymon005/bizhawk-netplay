using BizHawkNetplay.Core.Sync;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    public class PacingStatsTests
    {
        [Fact]
        public void RatesAreScaledByTheWindowLength()
        {
            var stats = new PacingStats();
            for (int i = 0; i < 30; i++) stats.AddFrame(10);
            for (int i = 0; i < 24; i++) stats.AddPresent(1);

            // 30 frames in half a second is 60fps, not 30.
            var summary = stats.Summarize(500);
            Assert.Equal(60, summary.AdvancedFps, 3);
            Assert.Equal(48, summary.PresentedFps, 3);
        }

        [Fact]
        public void StallRateIsAShareOfTicksNotFrames()
        {
            var stats = new PacingStats();
            // 10 ticks: 4 stalled (advancing nothing), 6 that each advanced a frame.
            for (int i = 0; i < 4; i++) stats.AddTick(stalled: true, timeSyncYield: false);
            for (int i = 0; i < 6; i++) { stats.AddFrame(8); stats.AddTick(false, false); }

            var summary = stats.Summarize(1000);
            Assert.Equal(10, summary.Ticks);
            Assert.Equal(6, summary.Frames);
            // Against frames the answer would have been 4/6 = 67%, which would overstate the problem
            // precisely because stalled ticks produce no frames.
            Assert.Equal(40, summary.StallTickPct, 3);
        }

        [Fact]
        public void TimeSyncYieldsAreCountedSeparatelyFromPlainStalls()
        {
            var stats = new PacingStats();
            stats.AddTick(stalled: true, timeSyncYield: true);
            stats.AddTick(stalled: true, timeSyncYield: false);
            stats.AddTick(stalled: false, timeSyncYield: false);
            stats.AddTick(stalled: false, timeSyncYield: false);

            var summary = stats.Summarize(1000);
            Assert.Equal(50, summary.StallTickPct, 3);
            Assert.Equal(25, summary.TimeSyncTickPct, 3);
        }

        [Fact]
        public void PercentileUsesNearestRankOverTheRetainedSamples()
        {
            var stats = new PacingStats();
            // 1..100ms: nearest-rank p95 over 100 samples is the 95th smallest.
            for (int i = 1; i <= 100; i++) stats.AddFrame(i);

            var summary = stats.Summarize(1000);
            Assert.Equal(95, summary.CoreP95Ms, 3);
            Assert.Equal(50.5, summary.CoreMeanMs, 3);
        }

        [Fact]
        public void TickRateIsReportedSeparatelyFromFrameRate()
        {
            var stats = new PacingStats();
            // 19 callbacks in half a second, most of them advancing two frames — the shape that
            // caps presentation near 38fps while the core happily emulates 60.
            for (int i = 0; i < 19; i++)
            {
                stats.AddTick(false, false);
                stats.AddFrame(3);
                if (i % 2 == 0) stats.AddFrame(3);
                stats.AddPresent(0.5);
            }

            var summary = stats.Summarize(500);
            Assert.Equal(38, summary.TicksPerSecond, 3);
            Assert.Equal(38, summary.PresentedFps, 3); // one present per tick, so it tracks the tick rate
            Assert.Equal(58, summary.AdvancedFps, 3);  // ...while frames advanced runs well ahead
        }

        [Fact]
        public void PresentedShareSeparatesACoarsePictureFromASlowSession()
        {
            // Both windows from the same N64 game on the same machine. The only reading that tells them
            // apart is this one: frames advanced is 60 in both, so fps, CPU-bound and the stall rate all
            // say the session is healthy, and only the presented share notices that at max resolution
            // the window is updating barely half as often as the core is emulating.
            var coarse = new PacingStats();
            for (int i = 0; i < 60; i++) coarse.AddFrame(13.7);
            for (int i = 0; i < 32; i++) coarse.AddPresent(2.2);

            var fine = new PacingStats();
            for (int i = 0; i < 60; i++) fine.AddFrame(4.7);
            for (int i = 0; i < 56; i++) fine.AddPresent(0.9);

            var a = coarse.Summarize(1000);
            var b = fine.Summarize(1000);

            Assert.Equal(a.AdvancedFps, b.AdvancedFps, 3);
            Assert.True(a.PresentedShare < 0.6, $"coarse window read {a.PresentedShare:F2}");
            Assert.True(b.PresentedShare > 0.9, $"fine window read {b.PresentedShare:F2}");
        }

        [Fact]
        public void PresentedShareIsOneWhenNothingWasEmulated()
        {
            // A window that stalled end to end presents nothing and advances nothing. That is a link
            // problem the stall rate already reports; it must not also read as a display problem.
            var stats = new PacingStats();
            for (int i = 0; i < 30; i++) stats.AddTick(stalled: true, timeSyncYield: false);

            Assert.Equal(1, stats.Summarize(1000).PresentedShare, 6);
        }

        [Fact]
        public void UndrawnRendersCountsThePicturesPaidForAndThrownAway()
        {
            var stats = new PacingStats();
            // Ten ticks of two frames each. The catch-up path saw the second frame coming on four of
            // them and skipped drawing the first; on the other six it did not, so those six pictures
            // were rendered in full and then immediately superseded by the tick's single present.
            for (int i = 0; i < 10; i++)
            {
                stats.AddTick(false, false);
                stats.AddFrame(13.7, rendered: i >= 4);
                stats.AddFrame(13.7);
                stats.AddPresent(2.2);
            }

            var summary = stats.Summarize(1000);
            Assert.Equal(20, summary.Frames);
            Assert.Equal(16, summary.RenderedFrames);
            Assert.Equal(10, summary.Presents);
            Assert.Equal(6, summary.UndrawnRenders);
        }

        [Fact]
        public void UndrawnRendersIsZeroRatherThanNegativeWhenEveryRenderWasShown()
        {
            var stats = new PacingStats();
            for (int i = 0; i < 10; i++) { stats.AddFrame(4, rendered: false); stats.AddPresent(1); }

            Assert.Equal(0, stats.Summarize(1000).UndrawnRenders);
        }

        [Fact]
        public void TickGapsSeparateASteadyCadenceFromAJitteryOne()
        {
            var steady = new PacingStats();
            for (int i = 0; i < 30; i++) { steady.AddTick(false, false); steady.AddTickInterval(16.7); }

            var jittery = new PacingStats();
            for (int i = 0; i < 30; i++)
            {
                jittery.AddTick(false, false);
                jittery.AddTickInterval(i % 2 == 0 ? 9.9 : 23.5);
            }

            var a = steady.Summarize(500);
            var b = jittery.Summarize(500);

            // Same rate, very different behaviour — which is the whole point of carrying the spread.
            Assert.Equal(a.TicksPerSecond, b.TicksPerSecond, 3);
            Assert.Equal(16.7, a.TickGapMeanMs, 3);
            Assert.Equal(0, a.TickGapMaxMs - a.TickGapMinMs, 3);
            Assert.Equal(16.7, b.TickGapMeanMs, 3);
            Assert.True(b.TickGapMaxMs - b.TickGapMinMs > 13);
        }

        [Fact]
        public void TickGapsAreZeroRatherThanMaxValueWhenNoneWereRecorded()
        {
            var stats = new PacingStats();
            stats.AddTick(false, false); // the very first tick has no predecessor to measure against

            var summary = stats.Summarize(500);
            Assert.Equal(0, summary.TickGapMinMs);
            Assert.Equal(0, summary.TickGapMeanMs);
            Assert.Equal(0, summary.TickGapMaxMs);
        }

        [Fact]
        public void MaxSurvivesASpikeThatThePercentileHides()
        {
            var stats = new PacingStats();
            // A single 180ms spike among 19 ordinary frames: p95 over 20 samples is the 19th smallest,
            // so the spike lands outside it and only the max (and a mean above the p95) reveals it.
            for (int i = 0; i < 19; i++) stats.AddFrame(3);
            stats.AddFrame(180);

            var summary = stats.Summarize(1000);
            Assert.Equal(180, summary.CoreMaxMs, 3);
            Assert.Equal(3, summary.CoreP95Ms, 3);
            Assert.True(summary.CoreMeanMs > summary.CoreP95Ms);
        }

        [Fact]
        public void PercentileIgnoresSamplesRolledOffTheRing()
        {
            var stats = new PacingStats();
            // Capacity is 128. Push 128 cheap samples out with 128 expensive ones; only the
            // expensive window should remain, while the mean still reflects every frame seen.
            for (int i = 0; i < 128; i++) stats.AddFrame(1);
            for (int i = 0; i < 128; i++) stats.AddFrame(101);

            var summary = stats.Summarize(1000);
            Assert.Equal(101, summary.CoreP95Ms, 3);
            Assert.Equal(256, summary.Frames);
            Assert.Equal(51, summary.CoreMeanMs, 3);
        }

        [Fact]
        public void SummarizeIsPureAndDoesNotReset()
        {
            var stats = new PacingStats();
            for (int i = 1; i <= 40; i++) stats.AddFrame(i);
            for (int i = 1; i <= 40; i++) stats.AddGate(i * 0.5);
            stats.AddTick(stalled: true, timeSyncYield: false);
            stats.AddRebase();

            var first = stats.Summarize(1000);
            var second = stats.Summarize(1000);

            Assert.Equal(first.CoreP95Ms, second.CoreP95Ms, 6);
            Assert.Equal(first.GateP95Ms, second.GateP95Ms, 6);
            Assert.Equal(first.CoreMeanMs, second.CoreMeanMs, 6);
            Assert.Equal(first.Frames, second.Frames);
            Assert.Equal(first.Rebases, second.Rebases);
            Assert.Equal(first.StallTickPct, second.StallTickPct, 6);
        }

        [Fact]
        public void EmptyWindowReportsZerosRatherThanNaN()
        {
            var summary = new PacingStats().Summarize(1000);

            Assert.Equal(0, summary.AdvancedFps);
            Assert.Equal(0, summary.PresentedFps);
            Assert.Equal(0, summary.TicksPerSecond);
            Assert.Equal(0, summary.CoreMeanMs);
            Assert.Equal(0, summary.CoreP95Ms);
            Assert.Equal(0, summary.CoreMaxMs);
            Assert.Equal(0, summary.GateMeanMs);
            Assert.Equal(0, summary.GateP95Ms);
            Assert.Equal(0, summary.PresentMeanMs);
            Assert.Equal(0, summary.StallTickPct);
            Assert.Equal(0, summary.TimeSyncTickPct);
        }

        [Fact]
        public void ZeroLengthWindowDoesNotProduceInfiniteRates()
        {
            var stats = new PacingStats();
            stats.AddFrame(10);
            stats.AddPresent(1);

            var summary = stats.Summarize(0);
            Assert.Equal(0, summary.AdvancedFps);
            Assert.Equal(0, summary.PresentedFps);
            // The underlying counts survive even when the rate is undefined.
            Assert.Equal(1, summary.Frames);
            Assert.Equal(10, summary.CoreMeanMs, 3);
        }

        [Fact]
        public void ResetClearsEveryCounter()
        {
            var stats = new PacingStats();
            for (int i = 0; i < 20; i++) { stats.AddFrame(12); stats.AddGate(3); stats.AddPresent(2); }
            stats.AddTick(stalled: true, timeSyncYield: true);
            stats.AddRebase();

            stats.Reset();
            var summary = stats.Summarize(1000);

            Assert.Equal(0, summary.Ticks);
            Assert.Equal(0, summary.Frames);
            Assert.Equal(0, summary.RenderedFrames);
            Assert.Equal(0, summary.Presents);
            Assert.Equal(0, summary.Rebases);
            Assert.Equal(0, summary.CoreP95Ms);
            Assert.Equal(0, summary.CoreMaxMs);
            Assert.Equal(0, summary.CoreMeanMs);
        }
    }
}
