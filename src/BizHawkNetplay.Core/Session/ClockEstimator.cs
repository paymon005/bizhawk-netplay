using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Session
{
    /// <summary>
    /// Session-start clock sync (§3.6), the NTP model RemotePlay's PING/PONG carries: for each
    /// probe the sender stamps t0, the peer echoes with its own receive time, and the reply lands
    /// at t3. RTT = (t3−t0); clock offset ≈ tRemote − (t0+t3)/2. Samples beyond one standard
    /// deviation of the mean RTT are rejected; the median of the survivors is the baseline ping and
    /// the median offset is the shared-clock offset. Pure math — fed timestamps, not sockets.
    /// </summary>
    public sealed class ClockEstimator
    {
        private readonly List<double> _rtts = new List<double>();
        private readonly List<double> _offsets = new List<double>();

        /// <summary>
        /// Record one completed probe. All times in the same millisecond unit.
        /// </summary>
        /// <param name="t0">Local send time.</param>
        /// <param name="tRemote">Remote's clock when it echoed.</param>
        /// <param name="t3">Local receive time of the echo.</param>
        public void AddSample(double t0, double tRemote, double t3)
        {
            double rtt = t3 - t0;
            if (rtt < 0) return; // clock went backwards / bogus sample
            _rtts.Add(rtt);
            _offsets.Add(tRemote - (t0 + t3) / 2.0);
        }

        public int SampleCount => _rtts.Count;

        /// <summary>
        /// Baseline ping and shared clock offset from the collected samples, or null until there is
        /// at least one. Outlier RTTs (beyond 1 SD of the mean) and their offsets are discarded
        /// before taking medians, so a single latency spike doesn't skew the estimate.
        /// </summary>
        public (double baselinePingMs, double clockOffsetMs)? Estimate()
        {
            int n = _rtts.Count;
            if (n == 0) return null;

            double mean = 0;
            for (int i = 0; i < n; i++) mean += _rtts[i];
            mean /= n;

            double variance = 0;
            for (int i = 0; i < n; i++) { double d = _rtts[i] - mean; variance += d * d; }
            double sd = Math.Sqrt(variance / n);

            var keptRtt = new List<double>(n);
            var keptOffset = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                if (sd == 0 || Math.Abs(_rtts[i] - mean) <= sd)
                {
                    keptRtt.Add(_rtts[i]);
                    keptOffset.Add(_offsets[i]);
                }
            }
            if (keptRtt.Count == 0) { keptRtt.AddRange(_rtts); keptOffset.AddRange(_offsets); }

            return (Median(keptRtt) / 2.0, Median(keptOffset));
        }

        /// <summary>
        /// Suggested lockstep input delay for the current estimate: ceil(halfRtt / frameMs) + 1
        /// frames (§1 non-functional target), clamped to at least 1.
        /// </summary>
        public int SuggestedDelayFrames(double frameMs)
        {
            var est = Estimate();
            if (est == null || frameMs <= 0) return 1;
            int d = (int)Math.Ceiling(est.Value.baselinePingMs / frameMs) + 1;
            return d < 1 ? 1 : d;
        }

        private static double Median(List<double> values)
        {
            values.Sort();
            int m = values.Count / 2;
            return (values.Count & 1) == 1 ? values[m] : (values[m - 1] + values[m]) / 2.0;
        }

        /// <summary>Build a PacingInfo snapshot for a strategy from the estimate and a frame advantage.</summary>
        public PacingInfo ToPacingInfo(int frameAdvantage)
        {
            var est = Estimate();
            if (est == null) return new PacingInfo(0, 0, frameAdvantage);
            return new PacingInfo(est.Value.baselinePingMs * 2.0, est.Value.clockOffsetMs, frameAdvantage);
        }
    }
}
