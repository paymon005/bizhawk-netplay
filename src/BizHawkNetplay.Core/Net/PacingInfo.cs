namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// Periodic clock/quality report from the transport, consumed by a strategy's pacing logic
    /// (§3.6). Frame advantage = how far ahead of the remote's confirmed frontier we are running.
    /// </summary>
    public readonly struct PacingInfo
    {
        public PacingInfo(double roundTripMs, double clockOffsetMs, int frameAdvantage)
        {
            RoundTripMs = roundTripMs;
            ClockOffsetMs = clockOffsetMs;
            FrameAdvantage = frameAdvantage;
        }

        /// <summary>Smoothed round-trip time in milliseconds.</summary>
        public double RoundTripMs { get; }

        /// <summary>Shared-clock offset from the NTP-style session sync.</summary>
        public double ClockOffsetMs { get; }

        /// <summary>Frames we are ahead of the remote's confirmed frontier (may be negative).</summary>
        public int FrameAdvantage { get; }
    }
}
