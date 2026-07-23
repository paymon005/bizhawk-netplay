namespace BizHawkNetplay.Core.Probe
{
    /// <summary>
    /// Outcome of the capability probe for one machine/core. Published in the handshake;
    /// rollback is offered only when both peers report <see cref="RollbackQualified"/>.
    /// </summary>
    public sealed class ProbeResult
    {
        public ProbeResult(
            string coreName,
            int stateSizeBytes,
            double medianSaveMs,
            double medianLoadMs,
            double medianFrameMs,
            double frameBudgetMs,
            double headroomMs,
            int maxRollbackDepth)
        {
            CoreName = coreName;
            StateSizeBytes = stateSizeBytes;
            MedianSaveMs = medianSaveMs;
            MedianLoadMs = medianLoadMs;
            MedianFrameMs = medianFrameMs;
            FrameBudgetMs = frameBudgetMs;
            HeadroomMs = headroomMs;
            MaxRollbackDepth = maxRollbackDepth;
        }

        public string CoreName { get; }
        public int StateSizeBytes { get; }
        public double MedianSaveMs { get; }
        public double MedianLoadMs { get; }
        public double MedianFrameMs { get; }
        public double FrameBudgetMs { get; }
        public double HeadroomMs { get; }

        /// <summary>
        /// Deepest misprediction (in frames) whose repair — one load plus depth re-simulated
        /// frames each re-saved — still fits inside one frame budget.
        /// </summary>
        public int MaxRollbackDepth { get; }

        /// <summary>The minimum depth at which enabling rollback is worthwhile (§5).</summary>
        public const int RollbackDepthThreshold = 6;

        public bool RollbackQualified => MaxRollbackDepth >= RollbackDepthThreshold;

        public override string ToString() =>
            $"{CoreName}: state={StateSizeBytes / 1024.0:F1}KiB " +
            $"save={MedianSaveMs:F3}ms load={MedianLoadMs:F3}ms frame={MedianFrameMs:F3}ms " +
            $"budget={FrameBudgetMs:F3}ms -> maxDepth={MaxRollbackDepth} " +
            $"({(RollbackQualified ? "ROLLBACK OK" : "lockstep only")})";
    }
}
