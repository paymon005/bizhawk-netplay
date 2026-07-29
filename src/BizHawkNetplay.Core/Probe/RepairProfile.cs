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
        /// <param name="resaveDepth">Depth the re-saving pass ran at. Need not match
        /// <paramref name="deepDepth"/>: the snapshot term is a per-frame cost, so it can be read off
        /// any depth, and a shallow one costs a fraction of the probe time. 0 means the pass was not
        /// taken, which leaves <see cref="MarginalSaveMs"/> at zero rather than negative.</param>
        /// <param name="resavedMs">That pass's measurement.</param>
        public RepairProfile(int shallowDepth, double shallowMs, int deepDepth, double deepMs,
            int resaveDepth = 0, double resavedMs = 0)
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
            ResaveDepth = resaveDepth;
            ResavedMs = resavedMs;
        }

        /// <summary>Depths the two passes ran at. Recorded rather than assumed, so a log line stays
        /// readable after these are retuned and old logs stay comparable to new ones.</summary>
        public int ShallowDepth { get; }
        public int DeepDepth { get; }

        /// <summary>One load plus <see cref="ShallowDepth"/> frames re-simulated, no snapshots.</summary>
        public double ShallowMs { get; }

        /// <summary>The same at <see cref="DeepDepth"/>.</summary>
        public double DeepMs { get; }

        /// <summary>Depth the re-saving pass ran at, and its measurement: one load plus that many
        /// frames re-simulated, snapshotting each — the shape a repair has today.</summary>
        public int ResaveDepth { get; }
        public double ResavedMs { get; }

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

        /// <summary>
        /// Cost of the snapshot taken after each re-simulated frame: the re-saving pass minus what the
        /// same repair costs without the snapshots, per frame.
        ///
        /// The subtrahend is read off the line rather than measured again, which is what lets the
        /// re-saving pass run shallow. It is by far the dearest thing the probe does — one load plus N
        /// frames each followed by a whole-core snapshot — so at N=8 on N64 it was 52% of the entire
        /// probe, and the probe runs on the connect path where it lands as a hitch on joining. The
        /// snapshot is a per-frame cost and reads the same off any depth, so paying for depth 8 bought
        /// nothing the line did not already have.
        ///
        /// Evaluating the line AT the deep depth gives exactly DeepMs, so this reduces to the earlier
        /// difference-of-two-deep-passes when the two depths coincide — a property worth a test.
        /// </summary>
        public double MarginalSaveMs
        {
            get
            {
                if (ResaveDepth < 1) return 0;
                double plainAtResaveDepth = ShallowMs + (ResaveDepth - ShallowDepth) * MarginalFrameMs;
                return Math.Max(0, (ResavedMs - plainAtResaveDepth) / ResaveDepth);
            }
        }

        /// <summary>
        /// Widest and narrowest the slope may sit relative to a frame timed on its own before the
        /// decomposition is treated as describing something other than a steady workload.
        /// </summary>
        private const double SlopeFloor = 0.55;
        private const double SlopeCeiling = 1.9;

        /// <summary>
        /// Whether the two points actually describe one steady cost, and so whether the terms taken
        /// off them are worth solving a depth from.
        ///
        /// The decomposition assumes the underlying cost holds still across both passes. It usually
        /// does — on a held-still in-game state the slope lands within 4% of an isolated frame over
        /// eight runs. It badly does not while a game is booting, where the probe's several seconds
        /// span logos, an intro and an attract demo: across ten games probed at boot the slope ran from
        /// half the isolated frame cost to four times it, and the intercept collapsed to zero on three
        /// of them where a runaway slope extrapolated back past the origin.
        ///
        /// Two things give that away without needing to know what the game was doing. A slope far from
        /// the frame cost timed on its own means the two passes did not measure the same work. And an
        /// intercept below the load timed on its own is impossible for a term that contains that load,
        /// so it can only be fit error.
        ///
        /// Deliberately not a repair: a profile that fails this is discarded, not clamped. Salvaging
        /// one term from a fit whose other term is provably wrong would be inventing a measurement,
        /// since both come off the same two points.
        /// </summary>
        public bool IsSelfConsistentWith(double isolatedFrameMs, double isolatedLoadMs)
        {
            if (MarginalFrameMs <= 0 || isolatedFrameMs <= 0) return false;
            double ratio = MarginalFrameMs / isolatedFrameMs;
            if (ratio < SlopeFloor || ratio > SlopeCeiling) return false;
            return ImpliedLoadMs >= isolatedLoadMs;
        }

        public override string ToString() =>
            $"repair {ShallowDepth}f={ShallowMs:F3}ms {DeepDepth}f={DeepMs:F3}ms " +
            $"({ResaveDepth}f+saves {ResavedMs:F3}ms) -> per-frame {MarginalFrameMs:F3}ms " +
            $"+save {MarginalSaveMs:F3}ms, load {ImpliedLoadMs:F3}ms";
    }
}
