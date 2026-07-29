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
            bool replayDeterministic = true,
            int depthAtWorstFrame = -1,
            double highFrameMs = 0,
            double liveFrameMs = 0,
            RepairProfile? repair = null,
            bool solvedFromRepairTerms = false,
            int keyframeInterval = 1)
        {
            Repair = repair;
            SolvedFromRepairTerms = solvedFromRepairTerms;
            KeyframeInterval = keyframeInterval < 1 ? 1 : keyframeInterval;
            LiveFrameMs = liveFrameMs > 0 ? liveFrameMs : medianFrameMs;
            SteadyStateMs = steadyStateMs > 0 ? steadyStateMs : medianFrameMs + medianSaveMs;
            ReplayDeterministic = replayDeterministic;
            DepthAtWorstFrame = depthAtWorstFrame < 0 ? maxRollbackDepth : depthAtWorstFrame;
            HighFrameMs = highFrameMs > 0 ? highFrameMs : medianFrameMs;
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
        /// <summary>Median cost of a frame advanced with rendering OFF — what a repair re-simulates.</summary>
        public double MedianFrameMs { get; }

        /// <summary>
        /// Median cost of a frame with video rendered — what the player's own frame costs.
        ///
        /// Separate from <see cref="MedianFrameMs"/> because the difference is the video plugin, which on
        /// a heavy core is the largest single term the probe measures and the one a user actually
        /// controls. When these two are far apart, the setting worth changing is the render one.
        /// </summary>
        public double LiveFrameMs { get; }

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

        /// <summary>90th-percentile frame cost, and the depth the same machine would have reported on
        /// one. A heavy core's frame cost moves enough between runs to change the verdict.</summary>
        public double HighFrameMs { get; }
        public int DepthAtWorstFrame { get; }

        /// <summary>
        /// A repair timed whole at two depths, or null if the probe did not measure one.
        ///
        /// Everything else here is a term measured on its own, and the depth is solved by adding those
        /// terms up. This is the same repair measured as one operation, so the sum can be checked
        /// against the thing it claims to describe.
        /// </summary>
        public RepairProfile? Repair { get; }

        /// <summary>
        /// Whether the depth was solved from the repair's own terms rather than from the ones timed in
        /// isolation. False means the decomposition did not describe a steady workload and was
        /// discarded — usually because the game was still booting — so the verdict rests on the
        /// isolated figures, which under-state the once-per-repair cost.
        /// </summary>
        public bool SolvedFromRepairTerms { get; }

        /// <summary>Snapshot spacing the depth was solved for. Must match what the session runs.</summary>
        public int KeyframeInterval { get; }

        /// <summary>What the depth model predicts the deep repair costs, from terms timed in isolation
        /// — the sum <see cref="CapabilityProbe.SolveMaxDepth(double,double,double,double,double)"/>
        /// reasons with.</summary>
        public double ModelledRepairMs =>
            Repair == null || Repair.ResaveDepth < 1
                ? 0
                : MedianLoadMs + Repair.ResaveDepth * (MedianFrameMs + MedianSaveMs);

        /// <summary>
        /// Signed fraction by which the measured repair overruns the modelled one. Positive means the
        /// model is optimistic and the session predicts further ahead than it can actually repair;
        /// negative means a repair is cheaper in one piece than as a sum, which is good news and no
        /// cause for alarm.
        /// </summary>
        public double RepairModelError =>
            Repair == null || ModelledRepairMs <= 0 ? 0 : (Repair.ResavedMs - ModelledRepairMs) / ModelledRepairMs;

        /// <summary>
        /// How far the measured repair may overrun the model before it is worth saying so. Each sample
        /// already averages over several frames, so the figure is steadier than the single-frame ones —
        /// steady enough that a few percent is noise and fifteen is roughly where the error starts to
        /// move a depth verdict near the threshold.
        /// </summary>
        public const double RepairModelTolerance = 0.15;

        /// <summary>True when a repair costs materially more than the terms it was solved from say it
        /// should. One-sided on purpose: only the optimistic direction can desync a session.</summary>
        public bool RepairCostsMoreThanModelled => Repair != null && RepairModelError > RepairModelTolerance;

        /// <summary>
        /// True when the verdict depends on which run you happened to look at: the median qualifies and
        /// the slower end does not. Worth surfacing rather than silently returning whichever answer the
        /// dice gave, because the honest reading is "this machine is on the boundary" — the setting to
        /// change is the one making frames expensive, not the probe.
        /// </summary>
        public bool DepthIsMarginal =>
            MaxRollbackDepth >= RollbackDepthThreshold && DepthAtWorstFrame < RollbackDepthThreshold;

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

        /// <summary>The verdict and the terms it was solved from, as one line.</summary>
        public string Summary =>
            $"{CoreName}: state={StateSizeBytes / 1024.0:F1}KiB " +
            $"save={MedianSaveMs:F3}ms load={MedianLoadMs:F3}ms frame={MedianFrameMs:F3}ms " +
            $"live={LiveFrameMs:F3}ms " +
            $"steady={SteadyStateMs:F3}ms budget={FrameBudgetMs:F3}ms " +
            $"-> maxDepth={MaxRollbackDepth}" +
            $"{(KeyframeInterval > 1 ? $" (keyframes 1-in-{KeyframeInterval})" : "")}" +
            $"{(SolvedFromRepairTerms ? "" : ", from isolated terms")} " +
            $"replay={(ReplayDeterministic ? "ok" : "DIVERGED")} " +
            $"({(RollbackQualified ? "ROLLBACK OK" : "lockstep only")}" +
            $"{(DepthIsMarginal ? $"; MARGINAL — {DepthAtWorstFrame} on a {HighFrameMs:F3}ms frame" : "")})";

        /// <summary>
        /// The measured repair set against what the terms above predict for it, or "" if none was
        /// measured. Kept apart from <see cref="Summary"/> so a caller comparing many probes can put it
        /// on its own line — these two answer different questions and the combined line is a mouthful.
        /// </summary>
        public string RepairDiagnostic =>
            Repair == null
                ? ""
                : $"{Repair} | modelled {ModelledRepairMs:F3}ms ({RepairModelError:+0.0%;-0.0%;0.0%})" +
                  (RepairCostsMoreThanModelled ? " — REPAIR OVERRUNS MODEL" : "");

        public override string ToString() =>
            Repair == null ? Summary : $"{Summary} | {RepairDiagnostic}";
    }
}
