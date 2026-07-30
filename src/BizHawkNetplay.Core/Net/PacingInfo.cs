namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// Periodic clock/quality report from the transport, consumed by a strategy's pacing logic
    /// (§3.6). Frame advantage = how far ahead of the remote's confirmed frontier we are running.
    /// </summary>
    public readonly struct PacingInfo
    {
        public PacingInfo(double roundTripMs, int frameAdvantage)
            : this(roundTripMs, frameAdvantage, hasFrameAdvantage: false,
                sampleSequence: 0, hasSampleSequence: false) { }

        public PacingInfo(double roundTripMs, int frameAdvantage, bool hasFrameAdvantage)
            : this(roundTripMs, frameAdvantage, hasFrameAdvantage,
                sampleSequence: 0, hasSampleSequence: false) { }

        /// <summary>Construct a report tied to one received wire sample. Reusing the same sequence is
        /// idempotent: pacing consumers must not charge the same measured advantage twice.</summary>
        public PacingInfo(double roundTripMs, int frameAdvantage, bool hasFrameAdvantage, int sampleSequence)
            : this(roundTripMs, frameAdvantage, hasFrameAdvantage,
                sampleSequence, hasSampleSequence: true) { }

        private PacingInfo(double roundTripMs, int frameAdvantage,
            bool hasFrameAdvantage, int sampleSequence, bool hasSampleSequence)
        {
            RoundTripMs = roundTripMs;
            FrameAdvantage = frameAdvantage;
            HasFrameAdvantage = hasFrameAdvantage;
            SampleSequence = sampleSequence;
            HasSampleSequence = hasSampleSequence;
        }

        /// <summary>Smoothed round-trip time in milliseconds.</summary>
        public double RoundTripMs { get; }

        /// <summary>Frames we are ahead of the remote (may be negative). Only meaningful when
        /// <see cref="HasFrameAdvantage"/>; 0 otherwise, which is also a legitimate measured value.</summary>
        public int FrameAdvantage { get; }

        /// <summary>
        /// Whether <see cref="FrameAdvantage"/> is a real measurement. Needed because 0 means "dead even"
        /// as well as "unknown", and a peer on an older build never reports its side of the exchange —
        /// in which case pacing must fall back to inferring a horizon from <see cref="RoundTripMs"/>.
        /// </summary>
        public bool HasFrameAdvantage { get; }

        /// <summary>Monotonic receiver-side revision for the wire sample that produced this report.</summary>
        public int SampleSequence { get; }

        /// <summary>False for legacy/in-process callers which intentionally treat each call as fresh.</summary>
        public bool HasSampleSequence { get; }
    }
}
