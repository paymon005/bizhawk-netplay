using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    public class ClockEstimatorTests
    {
        [Fact]
        public void RecoversKnownOffsetAndPing_FromCleanSamples()
        {
            // Construct samples with a true RTT of 40ms and remote clock 1000ms ahead of local.
            // t0 = send; remote receives at t0 + 20 (+offset); t3 = t0 + 40.
            const double offset = 1000.0, halfRtt = 20.0;
            var est = new ClockEstimator();
            for (int i = 0; i < 20; i++)
            {
                double t0 = i * 100.0;
                double t3 = t0 + 2 * halfRtt;
                double tRemote = t0 + halfRtt + offset; // remote stamps at its own clock
                est.AddSample(t0, tRemote, t3);
            }

            var e = est.Estimate();
            Assert.NotNull(e);
            Assert.Equal(halfRtt, e!.Value.baselinePingMs, 3);
            Assert.Equal(offset, e.Value.clockOffsetMs, 3);
        }

        [Fact]
        public void OutlierSpikeIsRejected()
        {
            const double offset = 500.0, halfRtt = 15.0;
            var est = new ClockEstimator();
            for (int i = 0; i < 20; i++)
            {
                double t0 = i * 100.0;
                double t3 = t0 + 2 * halfRtt;
                est.AddSample(t0, t0 + halfRtt + offset, t3);
            }
            // One massive latency spike (500ms RTT) that must not move the median estimate.
            est.AddSample(10_000, 10_000 + 250 + offset, 10_500);

            var e = est.Estimate();
            Assert.NotNull(e);
            Assert.Equal(halfRtt, e!.Value.baselinePingMs, 1);
        }

        [Fact]
        public void SuggestedDelay_ScalesWithPing()
        {
            var est = new ClockEstimator();
            // 50ms half-RTT (100ms RTT). At a 16.688ms Genesis frame: ceil(50/16.688)+1 = 4.
            for (int i = 0; i < 10; i++)
                est.AddSample(i * 200.0, i * 200.0 + 50.0, i * 200.0 + 100.0);

            Assert.Equal(4, est.SuggestedDelayFrames(16.688));
        }

        [Fact]
        public void NoSamples_YieldsNullAndDefaultDelay()
        {
            var est = new ClockEstimator();
            Assert.Null(est.Estimate());
            Assert.Equal(1, est.SuggestedDelayFrames(16.688));
        }
    }
}
