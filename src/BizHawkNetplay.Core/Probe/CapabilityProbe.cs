using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;

namespace BizHawkNetplay.Core.Probe
{
    /// <summary>
    /// Runs the §5 capability probe against a core: times save-to-memory, load-from-memory,
    /// and invisible frame advance, then solves for the deepest rollback the frame budget
    /// affords. Pure over <see cref="IEmuAdapter"/> + <see cref="IMonotonicClock"/>, so the
    /// math is unit-tested with a scripted clock and the real timings come from the tool.
    /// </summary>
    public sealed class CapabilityProbe
    {
        private readonly IEmuAdapter _emu;
        private readonly IMonotonicClock _clock;
        private readonly int _samples;

        public CapabilityProbe(IEmuAdapter emu, IMonotonicClock clock, int samples = 100)
        {
            _emu = emu ?? throw new ArgumentNullException(nameof(emu));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (samples < 1) throw new ArgumentOutOfRangeException(nameof(samples));
            _samples = samples;
        }

        /// <param name="frameBudgetMs">Console's exact frame period (e.g. 16.639 ms NTSC, 16.743 ms GBA).</param>
        /// <param name="headroomMs">Slack reserved for the rest of the per-frame work (render, transport, GC).</param>
        /// <param name="elideConfirmedSaves">Whether the session will skip snapshots on confirmed frames
        /// (see <see cref="Sync.RollbackTuning"/>). Changes what rollback costs when nothing is being
        /// repaired, which is the common case and the one that decides whether a core can run it at all.</param>
        /// <param name="repairBudgetMs">Wall clock one repair may take, if the session is willing to spend
        /// more than a single frame period on it. 0 keeps the original one-frame ceiling.</param>
        /// <param name="keyframeInterval">Snapshot spacing the session will run
        /// (see <see cref="Sync.RollbackTuning.KeyframeInterval"/>). MUST match it: a depth solved
        /// against a different spacing was never checked against the repair the session will perform.</param>
        public ProbeResult Run(double frameBudgetMs, double headroomMs,
            bool elideConfirmedSaves = false, double repairBudgetMs = 0, int keyframeInterval = 1)
        {
            var neutral = BuildNeutralInputs();

            // Capture a reference state once so load/advance have something valid to work on.
            var reference = _emu.SaveStateToMemory();

            // Representative serialized state size (the in-memory handle is opaque, so measure
            // the equivalent full binary state once — not on the timed hot path).
            int stateSize = _emu.ExportState().Length;

            // Retain the timed save handles so we can free them afterwards — otherwise every probe
            // leaks ~samples whole-core states into the emulator's in-memory store (~hundreds of MiB).
            //
            // A frame runs between samples, outside the timing. Saving does not advance the core, so
            // without it every sample after the first snapshots memory nothing has touched since the
            // last one — and a snapshot of unchanged memory is measurably cheaper than the real thing.
            // Across six N64 configurations this pass reported 5.6-6.7ms with no pattern, against a
            // steady ~7.0ms (±5%) for the same operation timed inside a repair. A session snapshots
            // after a frame that has just dirtied RAM, so that is what gets timed.
            var scratch = new List<StateHandle>(_samples);
            double medianSave = MeasureMedian(
                () => scratch.Add(_emu.SaveStateToMemory()),
                _samples, out _,
                between: () => _emu.RunFramesInvisible(1, _ => neutral));

            // Same treatment, so each sample loads a state the core is not already standing on. This
            // pass used to restore the reference from the reference position, over and over: 16.7MiB
            // written back over identical bytes, which measured ~1.4ms against ~3.0ms for a load that
            // genuinely moves the core. That one understated the term the repair budget spends first.
            //
            // The core arrives here already displaced by the pass above, so every sample crosses a real
            // gap. How wide does not matter: the repair passes below measure loads across 1 and 8
            // frames and find them the same to within half a percent.
            double medianLoad = MeasureMedian(
                () => _emu.LoadStateFromMemory(reference),
                _samples, out _,
                between: () => _emu.RunFramesInvisible(1, _ => neutral));

            // These advance with rendering OFF, which is exactly what a repair does — so this is the
            // repair term, and the right figure for it.
            double highFrame;
            double medianFrame = MeasureMedian(() =>
                _emu.RunFramesInvisible(1, _ => neutral), out highFrame);

            // ...and this is the live frame, which renders. The two used to be the same number, which
            // made the probe optimistic on precisely the cores where the verdict is close, and left it
            // almost blind to the resolution setting — the one knob a user tuning for performance
            // reaches for first. Measured separately now, and each charged where it belongs.
            double medianLiveFrame = MeasureMedian(() => _emu.AdvanceRenderedFrame(neutral));

            foreach (var h in scratch) _emu.ReleaseState(h);

            // What a repair actually costs, timed whole rather than added up from the three figures
            // above. Reported, not yet spent: the depth below is still solved from the isolated terms,
            // because changing the verdict on the strength of a measurement nothing has corroborated
            // would be the same mistake in the other direction. See RepairProfile for what it answers.
            var repair = new RepairProfile(
                RepairProbeShallow, MeasureRepair(RepairProbeShallow, resave: false, neutral),
                RepairProbeDeep, MeasureRepair(RepairProbeDeep, resave: false, neutral),
                MeasureRepair(RepairProbeDeep, resave: true, neutral));

            // Solve from the repair itself where it can be trusted, and from the isolated terms where
            // it cannot. The repair-derived figures are the ones the model actually wants: it charges
            // the load once per repair, and the once-per-repair cost is not the load call alone —
            // measured on N64, an isolated load claims 17.1MiB restored in 1.57ms, which is 10.9GB/s
            // and not physically possible, because the work is deferred onto the frame that follows.
            // The intercept catches that; a load timed on its own cannot, by construction.
            //
            // The same figures are worthless when the workload moved between passes, which is why they
            // are used only when the decomposition is self-consistent — see RepairProfile.
            bool solveFromRepair = repair.IsSelfConsistentWith(medianFrame, medianLoad);
            double solveFrame = solveFromRepair ? repair.MarginalFrameMs : medianFrame;
            double solveLoad = solveFromRepair ? repair.ImpliedLoadMs : medianLoad;
            double solveSave = solveFromRepair ? repair.MarginalSaveMs : medianSave;

            int depth = SolveMaxDepth(
                frameBudgetMs, headroomMs, medianLiveFrame, solveFrame, solveLoad, solveSave,
                elideConfirmedSaves, repairBudgetMs, keyframeInterval);

            // What the same machine would have concluded on a slower-than-typical frame. When this
            // disagrees with the median verdict, the answer is a coin flip and the user deserves to
            // know rather than re-rolling the probe until they like it. The high end is measured on the
            // repair term, so scale the live term by the same ratio rather than pretending only one of
            // them has a bad run.
            double liveRatio = medianFrame > 0 ? highFrame / medianFrame : 1.0;
            int depthAtWorst = SolveMaxDepth(
                frameBudgetMs, headroomMs, medianLiveFrame * liveRatio, solveFrame * liveRatio,
                solveLoad, solveSave, elideConfirmedSaves, repairBudgetMs, keyframeInterval);

            bool replayDeterministic = VerifyReplayDeterminism(neutral, ReplayCheckFrames);

            // Hand the position back. The timed passes advance the core and the replay check advances
            // it further, and this runs against whatever the user currently has loaded — so the probe
            // owes them the frame it started on. (The tool wraps this in its own save/restore as well;
            // belt and braces, since a probe that quietly moves the game is a nasty surprise.)
            try { _emu.LoadStateFromMemory(reference); } catch { }
            _emu.ReleaseState(reference);

            return new ProbeResult(
                _emu.CoreName, stateSize, medianSave, medianLoad, medianFrame,
                frameBudgetMs, headroomMs, depth,
                medianLiveFrame + (elideConfirmedSaves ? 0 : medianSave),
                replayDeterministic, depthAtWorst, highFrame, medianLiveFrame, repair,
                solveFromRepair, keyframeInterval);
        }

        /// <summary>
        /// Depths at which a whole repair is timed. One frame is nearly all load, so it fixes the
        /// intercept; eight gives a seven-frame lever arm for the slope, long enough that the per-frame
        /// cost clears the run-to-run scatter a single frame is lost in. Eight also brackets the range
        /// worth arguing about — the qualifying threshold is 3, and the depths a heavy core could
        /// plausibly be lifted to sit between the two.
        /// </summary>
        private const int RepairProbeShallow = 1;
        private const int RepairProbeDeep = 8;

        /// <summary>
        /// Samples per repair pass. Each already averages over up to <see cref="RepairProbeDeep"/>
        /// frames and is correspondingly steadier than a single-frame sample, so it needs fewer
        /// repetitions; the full count would add seconds to a probe for resolution it does not need.
        ///
        /// The probe runs synchronously on the UI thread, so this is freeze length. Eight samples costs
        /// an N64 at native resolution about 0.8s — most of it the pass that re-snapshots every frame,
        /// which is 68ms a sample on its own. That roughly triples the probe's freeze, and is the price
        /// of knowing what the snapshot in a repair really costs rather than assuming it costs what an
        /// isolated one does.
        /// </summary>
        private int RepairSamples => Math.Max(8, _samples / 8);

        /// <summary>
        /// Times one whole repair shape: a load, then <paramref name="depth"/> frames re-simulated from
        /// it, each optionally re-snapshotted the way a repair does today.
        ///
        /// The frames are advanced one call at a time whether or not they are being snapshotted, so the
        /// two deep passes differ by the snapshot and by nothing else — which is what lets their
        /// difference be read as the snapshot's cost.
        /// </summary>
        private double MeasureRepair(int depth, bool resave, InputSet neutral)
        {
            var anchor = _emu.SaveStateToMemory();
            var taken = new List<StateHandle>(depth);
            try
            {
                // Leave the core where every later sample will leave it, so the first timed sample
                // loads across the same gap as the rest of them rather than across none.
                for (int f = 0; f < depth; f++) _emu.RunFramesInvisible(1, _ => neutral);

                return MeasureMedian(
                    () =>
                    {
                        _emu.LoadStateFromMemory(anchor);
                        for (int f = 0; f < depth; f++)
                        {
                            _emu.RunFramesInvisible(1, _ => neutral);
                            if (resave) taken.Add(_emu.SaveStateToMemory());
                        }
                    },
                    RepairSamples,
                    out _,
                    between: () =>
                    {
                        // Freed between samples rather than at the end: holding every sample's states
                        // at once would peak at hundreds of MiB on exactly the cores this measures.
                        // Outside the timed region, because a repair does not pay to free the ring's
                        // evictions either.
                        foreach (var h in taken) _emu.ReleaseState(h);
                        taken.Clear();
                    });
            }
            finally
            {
                foreach (var h in taken) _emu.ReleaseState(h);
                try { _emu.LoadStateFromMemory(anchor); } catch { }
                _emu.ReleaseState(anchor);
            }
        }

        /// <summary>
        /// Largest depth d such that: normalFrame + load + d*(sim + save) &lt;= budget - headroom.
        /// A repair reloads once, then re-simulates and re-saves d frames. Returns 0 if even a
        /// single-frame repair overruns. Original signature, preserved exactly.
        /// </summary>
        internal static int SolveMaxDepth(
            double frameBudgetMs, double headroomMs,
            double normalFrameMs, double loadMs, double saveMs) =>
            SolveMaxDepth(frameBudgetMs, headroomMs, normalFrameMs, loadMs, saveMs,
                elideConfirmedSaves: false, repairBudgetMs: 0);

        /// <summary>As the six-argument form, charging one frame cost for both roles. Kept because most
        /// callers and every test written before the live frame was measured separately pass one.</summary>
        internal static int SolveMaxDepth(
            double frameBudgetMs, double headroomMs,
            double normalFrameMs, double loadMs, double saveMs,
            bool elideConfirmedSaves, double repairBudgetMs) =>
            SolveMaxDepth(frameBudgetMs, headroomMs, normalFrameMs, normalFrameMs, loadMs, saveMs,
                elideConfirmedSaves, repairBudgetMs);

        /// <summary>
        /// As above, but modelling the two things the original formula left out.
        ///
        /// <b>Steady state.</b> The old arithmetic only ever charged for savestates taken during a
        /// repair, and silently ignored that rollback also takes one every ordinary frame whether or
        /// not anything is ever corrected. On a light core that omission is noise; on a heavy one it is
        /// the whole story — N64 measures save 6.1ms against a 2.0ms frame, so nearly half the frame
        /// budget was going on insurance the formula never counted. A core that cannot afford that
        /// recurring cost has no usable depth however the repair sum works out, so it is checked first.
        /// Elision removes the cost rather than accounting for it.
        ///
        /// <b>Repair budget.</b> Requiring a repair to fit inside a single frame period is stricter than
        /// it needs to be now that the frame tick can absorb a short overrun. Passing an explicit budget
        /// buys real depth on a heavy core; the frames re-simulated are still charged at the full
        /// sim+save rate, because a correction generally confirms only the frames near its own and
        /// leaves the rest of the window predicted — and therefore still worth anchoring.
        ///
        /// <b>Two frame costs.</b> A repair re-simulates with rendering off; the live frame renders.
        /// Charging one figure for both was wrong in the direction that matters — the live frame is the
        /// dearer of the two, and it appears in the steady-state check and in what the repair has left
        /// to spend, so a single cheap number inflated the answer twice.
        /// </summary>
        internal static int SolveMaxDepth(
            double frameBudgetMs, double headroomMs,
            double liveFrameMs, double repairFrameMs, double loadMs, double saveMs,
            bool elideConfirmedSaves, double repairBudgetMs) =>
            SolveMaxDepth(frameBudgetMs, headroomMs, liveFrameMs, repairFrameMs, loadMs, saveMs,
                elideConfirmedSaves, repairBudgetMs, keyframeInterval: 1);

        /// <summary>Depth past which the answer stops meaning anything; only reached by cores whose
        /// state operations are effectively free, where any of these numbers is already ample.</summary>
        private const int DepthSearchCeiling = 4096;

        /// <summary>
        /// As above, and modelling the snapshot spacing the session will actually run
        /// (<see cref="Sync.RollbackTuning.KeyframeInterval"/>).
        ///
        /// With a snapshot on every predicted frame the repair sum is a straight line and the old
        /// division was exact. Spacing them apart bends it, in two ways that pull opposite:
        ///
        ///   * a repair restarts from the newest keyframe at or before its target, so it re-simulates
        ///     up to N-1 frames MORE than its depth;
        ///   * but it only snapshots every Nth of those, so it saves far fewer times.
        ///
        /// So the cost of depth d is <c>load + (d+N-1)*frame + ceil((d+N-1)/N)*save</c>, which is a
        /// step function rather than a line — the ceiling means depth sometimes comes free and
        /// sometimes costs a whole snapshot. Searching it is exact where a division would not be, and
        /// this runs once per probe.
        ///
        /// Solving the wrong N is not a small error in the safe direction. The session and the probe
        /// must agree on it, or the depth negotiated is one the repair budget was never checked against.
        /// </summary>
        internal static int SolveMaxDepth(
            double frameBudgetMs, double headroomMs,
            double liveFrameMs, double repairFrameMs, double loadMs, double saveMs,
            bool elideConfirmedSaves, double repairBudgetMs, int keyframeInterval)
        {
            double steadyMs = liveFrameMs + (elideConfirmedSaves ? 0 : saveMs);
            if (steadyMs > frameBudgetMs - headroomMs) return 0;

            double budget = repairBudgetMs > 0 ? repairBudgetMs : frameBudgetMs - headroomMs;
            double available = budget - liveFrameMs - loadMs;
            if (available <= 0) return 0;
            if (repairFrameMs <= 0 && saveMs <= 0) return 0;

            int n = keyframeInterval < 1 ? 1 : keyframeInterval;
            for (int depth = 1; depth <= DepthSearchCeiling; depth++)
            {
                int frames = depth + n - 1;          // walk back to the nearest keyframe, then forward
                int saves = (frames + n - 1) / n;    // ceil(frames / n)
                if (frames * repairFrameMs + saves * saveMs > available) return depth - 1;
            }
            return DepthSearchCeiling;
        }

        /// <summary>Frames replayed on each side of the determinism check. Long enough for a divergence
        /// to spread into main memory, short enough that the whole check is a fraction of a second.</summary>
        private const int ReplayCheckFrames = 30;

        /// <summary>Slices hashed per side. Covers a domain sampled with a stride of up to this much;
        /// on a core that hashes everything the extra passes simply repeat the same answer.</summary>
        private const int ReplayCheckSlices = 4;

        /// <summary>
        /// Does this core actually REPRODUCE from a savestate?
        ///
        /// Everything else here measures how fast save, load and advance are — never whether replaying
        /// the same inputs from the same state lands in the same place. That is the one property
        /// rollback cannot work without: repair is exactly load-and-re-simulate, so a core that drifts
        /// across it desyncs whenever the link makes it predict, and stays perfectly in sync on a
        /// connection fast enough that it never has to. A timing-only probe passes such a core happily
        /// and the failure surfaces later as an unexplained desync mid-session.
        ///
        /// Save, run, hash; rewind, run the identical inputs, hash again; compare. A mismatch is
        /// conclusive — rollback is unsafe on this core. A match is NOT proof of determinism, only
        /// evidence over these frames from this position, so the result is reported as what it is.
        /// </summary>
        private bool VerifyReplayDeterminism(InputSet neutral, int frames)
        {
            StateHandle? anchor = null;
            try
            {
                anchor = _emu.SaveStateToMemory();

                _emu.RunFramesInvisible(frames, _ => neutral);
                var first = new uint[ReplayCheckSlices];
                for (int slice = 0; slice < ReplayCheckSlices; slice++)
                    first[slice] = _emu.HashMainMemory(slice);

                _emu.LoadStateFromMemory(anchor);
                _emu.RunFramesInvisible(frames, _ => neutral);
                for (int slice = 0; slice < ReplayCheckSlices; slice++)
                    if (_emu.HashMainMemory(slice) != first[slice]) return false;

                return true;
            }
            catch
            {
                // A core that cannot be driven through this at all is not one to trust with rollback.
                return false;
            }
            finally
            {
                if (anchor != null)
                {
                    try { _emu.LoadStateFromMemory(anchor); } catch { }
                    try { _emu.ReleaseState(anchor); } catch { }
                }
            }
        }

        private InputSet BuildNeutralInputs()
        {
            int ports = _emu.PortCount;
            var layouts = new ControllerLayout[ports];
            for (int p = 0; p < ports; p++) layouts[p] = _emu.GetControllerLayout(p);
            return InputSet.AllNeutral(0, layouts);
        }

        private double MeasureMedian(Action op) => MeasureMedian(op, _samples, out _);

        private double MeasureMedian(Action op, out double highMs) => MeasureMedian(op, _samples, out highMs);

        /// <summary>
        /// Median cost of an operation, plus the 90th percentile so callers can see how much the
        /// figure moves. On a heavy core the spread is not a detail: the depth verdict flips between 3
        /// and 2 at about 3.44ms, and N64 at 1400x1050 measures a median of 3.55ms — three consecutive
        /// probes of that one configuration returned depth 2, 3 and 3, with nothing in the output to
        /// say the answer had ever been close.
        ///
        /// <paramref name="between"/> runs after each sample and outside its timing, for bookkeeping a
        /// caller must do per iteration but is not trying to measure.
        /// </summary>
        private double MeasureMedian(Action op, int sampleCount, out double highMs, Action? between = null)
        {
            var samples = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                double t0 = _clock.NowMs;
                op();
                samples[i] = _clock.NowMs - t0;
                between?.Invoke();
            }
            Array.Sort(samples);
            int mid = samples.Length / 2;
            int rank = (int)Math.Ceiling(0.9 * samples.Length);
            if (rank < 1) rank = 1;
            if (rank > samples.Length) rank = samples.Length;
            highMs = samples[rank - 1];
            return (samples.Length & 1) == 1
                ? samples[mid]
                : (samples[mid - 1] + samples[mid]) / 2.0;
        }
    }
}
