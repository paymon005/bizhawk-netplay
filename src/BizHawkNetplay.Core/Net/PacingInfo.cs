namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// Periodic clock/quality report from the transport, consumed by a strategy's pacing logic
    /// (§3.6). Frame advantage = how far ahead of the remote's confirmed frontier we are running.
    /// </summary>
    public readonly struct PacingInfo
    {
        public PacingInfo(double roundTripMs, double clockOffsetMs, int frameAdvantage)
            : this(roundTripMs, clockOffsetMs, frameAdvantage, hasFrameAdvantage: false) { }

        public PacingInfo(double roundTripMs, double clockOffsetMs, int frameAdvantage, bool hasFrameAdvantage)
        {
            RoundTripMs = roundTripMs;
            ClockOffsetMs = clockOffsetMs;
            FrameAdvantage = frameAdvantage;
            HasFrameAdvantage = hasFrameAdvantage;
        }

        /// <summary>Smoothed round-trip time in milliseconds.</summary>
        public double RoundTripMs { get; }

        /// <summary>Shared-clock offset from the NTP-style session sync.</summary>
        public double ClockOffsetMs { get; }

        /// <summary>Frames we are ahead of the remote (may be negative). Only meaningful when
        /// <see cref="HasFrameAdvantage"/>; 0 otherwise, which is also a legitimate measured value.</summary>
        public int FrameAdvantage { get; }

        /// <summary>
        /// Whether <see cref="FrameAdvantage"/> is a real measurement. Needed because 0 means "dead even"
        /// as well as "unknown", and a peer on an older build never reports its side of the exchange —
        /// in which case pacing must fall back to inferring a horizon from <see cref="RoundTripMs"/>.
        /// </summary>
        public bool HasFrameAdvantage { get; }
    }
}
