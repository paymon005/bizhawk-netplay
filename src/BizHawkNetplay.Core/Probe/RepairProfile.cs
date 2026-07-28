using System;

namespace BizHawkNetplay.Core.Probe
{
    /// <summary>
    /// What a repair actually costs, timed whole at two depths instead of assembled from terms
    /// measured in isolation.
    ///
    /// The depth model charges <c>load + depth * (frame + save)</c> and assumes each of those three
    /// costs what it costs on its own. None of that is obvious on a recompiling core: a load from
    /// further back can invalidate the code cache, and the frames immediately after one run on caches
    /// the load has just cleared. Timing a load by itself answers the narrower half of the question and
    /// misses precisely the effect most likely to bite.
    ///
    /// Two depths give a line. Its slope is the true cost of one more re-simulated frame and its
    /// intercept is the true cost of the load that opens the repair; running the deep pass twice —
    /// once snapshotting each re-simulated frame, once not — isolates the third term, because those two
    /// passes differ by nothing else.
    ///
    /// Those three numbers are what decides how often a repair needs to snapshot at all: the snapshot
    /// is the dominant term on a heavy core, and dropping it from most re-simulated frames only pays if
    /// the frame and load terms behave as the model claims they do.
    /// </summary>
    public sealed class RepairProfile
    {
        /// <param name="deepResavedMs">The deep pass re-run snapshotting every frame. 0 means that pass
        /// was not taken, which leaves <see cref="MarginalSaveMs"/> at zero rather than negative.</param>
        public RepairProfile(int shallowDepth, double shallowMs, int deepDepth, double deepMs, double deepResavedMs = 0)
        {
            if (shallowDepth < 1)
                throw new ArgumentOutOfRangeException(nameof(shallowDepth), "A repair re-simulates at least one frame.");
            if (deepDepth <= shallowDepth)
                throw new ArgumentOutOfRangeException(nameof(deepDepth),
                    "The two depths must differ, or there is no lever arm to take a slope over.");

            ShallowDepth = shallowDepth;
            ShallowMs = shallowMs;
            DeepDepth = deepDepth;
            DeepMs = deepMs;
            DeepResavedMs = deepResavedMs;
        }

        /// <summary>Depths the two passes ran at. Recorded rather than assumed, so a log line stays
        /// readable after these are retuned and old logs stay comparable to new ones.</summary>
        public int ShallowDepth { get; }
        public int DeepDepth { get; }

        /// <summary>One load plus <see cref="ShallowDepth"/> frames re-simulated, no snapshots.</summary>
        public double ShallowMs { get; }

        /// <summary>The same at <see cref="DeepDepth"/>.</summary>
        public double DeepMs { get; }

        /// <summary>As <see cref="DeepMs"/>, but snapshotting every re-simulated frame — the shape a
        /// repair has today.</summary>
        public double DeepResavedMs { get; }

        /// <summary>
        /// Cost of one more re-simulated frame, taken as the slope between the two depths.
        ///
        /// Deliberately not clamped: a negative slope means the two passes disagreed by more than the
        /// gap between them, which is a measurement worth seeing rather than one worth hiding behind a
        /// plausible zero.
        /// </summary>
        public double MarginalFrameMs => (DeepMs - ShallowMs) / (DeepDepth - ShallowDepth);

        /// <summary>Cost of the load that opens a repair, as the intercept of that line — the figure to
        /// compare against a load timed from where the core already stands.</summary>
        public double ImpliedLoadMs => Math.Max(0, ShallowMs - ShallowDepth * MarginalFrameMs);

        /// <summary>Cost of the snapshot taken after each re-simulated frame: the whole difference
        /// between the two deep passes, which differ by nothing else.</summary>
        public double MarginalSaveMs => Math.Max(0, (DeepResavedMs - DeepMs) / DeepDepth);

        public override string ToString() =>
            $"repair {ShallowDepth}f={ShallowMs:F3}ms {DeepDepth}f={DeepMs:F3}ms " +
            $"(+saves {DeepResavedMs:F3}ms) -> per-frame {MarginalFrameMs:F3}ms " +
            $"+save {MarginalSaveMs:F3}ms, load {ImpliedLoadMs:F3}ms";
    }
}
