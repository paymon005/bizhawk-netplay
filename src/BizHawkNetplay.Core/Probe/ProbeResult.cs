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
            int maxRollbackDepth,
            double steadyStateMs = 0,
            bool replayDeterministic = true)
        {
            SteadyStateMs = steadyStateMs > 0 ? steadyStateMs : medianFrameMs + medianSaveMs;
            ReplayDeterministic = replayDeterministic;
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
        /// What rollback costs per frame when nothing is being repaired — the recurring tax, as opposed
        /// to the occasional repair. Equals the frame cost alone when the session elides snapshots on
        /// confirmed frames, and frame + save when it does not.
        /// </summary>
        public double SteadyStateMs { get; }

        /// <summary>
        /// Whether replaying the same inputs from the same savestate reproduced the same memory.
        ///
        /// Rollback repair IS load-and-re-simulate, so a core that fails this desyncs the moment the
        /// link makes it predict — and stays flawlessly in sync on a connection fast enough that it
        /// never has to, which is what makes the failure so easy to miss in local testing. False is
        /// conclusive; true is evidence rather than proof, since it covers only the frames the probe
        /// happened to replay.
        /// </summary>
        public bool ReplayDeterministic { get; }

        /// <summary>
        /// Deepest misprediction (in frames) whose repair — one load plus depth re-simulated
        /// frames each re-saved — still fits inside one frame budget.
        /// </summary>
        public int MaxRollbackDepth { get; }

        /// <summary>
        /// The minimum depth at which enabling rollback is worthwhile.
        ///
        /// Depth is how many frames a peer may run ahead of the slowest remote port, so it is exactly
        /// the one-way latency rollback can hide. Three frames covers a ~100ms round trip, which is an
        /// ordinary broadband link between two people in the same country — enough to run at input
        /// delay 1 where lockstep would need 4 or 5.
        ///
        /// This was 6, chosen before anything heavy had been measured. Nothing light is affected by the
        /// change: cores that qualify at all report depths in the tens, so the threshold only ever bites
        /// on the heavy end, where the honest choice is between shallow rollback and none. Keeping it a
        /// single number (rather than a per-core one) matters because peers compare each other's
        /// reported depth against it, and only the depth crosses the wire.
        /// </summary>
        public const int RollbackDepthThreshold = 3;

        /// <summary>Depth alone is not enough: a core that cannot replay is disqualified however much
        /// budget it has, because the thing it would be spending that budget on is replaying.</summary>
        public bool RollbackQualified => ReplayDeterministic && MaxRollbackDepth >= RollbackDepthThreshold;

        public override string ToString() =>
            $"{CoreName}: state={StateSizeBytes / 1024.0:F1}KiB " +
            $"save={MedianSaveMs:F3}ms load={MedianLoadMs:F3}ms frame={MedianFrameMs:F3}ms " +
            $"steady={SteadyStateMs:F3}ms budget={FrameBudgetMs:F3}ms -> maxDepth={MaxRollbackDepth} " +
            $"replay={(ReplayDeterministic ? "ok" : "DIVERGED")} " +
            $"({(RollbackQualified ? "ROLLBACK OK" : "lockstep only")})";
    }
}
