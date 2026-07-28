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
        public ProbeResult Run(double frameBudgetMs, double headroomMs,
            bool elideConfirmedSaves = false, double repairBudgetMs = 0)
        {
            var neutral = BuildNeutralInputs();

            // Capture a reference state once so load/advance have something valid to work on.
            var reference = _emu.SaveStateToMemory();

            // Representative serialized state size (the in-memory handle is opaque, so measure
            // the equivalent full binary state once — not on the timed hot path).
            int stateSize = _emu.ExportState().Length;

            // Retain the timed save handles so we can free them afterwards — otherwise every probe
            // leaks ~samples whole-core states into the emulator's in-memory store (~hundreds of MiB).
            var scratch = new List<StateHandle>(_samples);
            double medianSave = MeasureMedian(() => scratch.Add(_emu.SaveStateToMemory()));

            double medianLoad = MeasureMedian(() => _emu.LoadStateFromMemory(reference));

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

            int depth = SolveMaxDepth(
                frameBudgetMs, headroomMs, medianLiveFrame, medianFrame, medianLoad, medianSave,
                elideConfirmedSaves, repairBudgetMs);

            // What the same machine would have concluded on a slower-than-typical frame. When this
            // disagrees with the median verdict, the answer is a coin flip and the user deserves to
            // know rather than re-rolling the probe until they like it. The high end is measured on the
            // repair term, so scale the live term by the same ratio rather than pretending only one of
            // them has a bad run.
            double liveRatio = medianFrame > 0 ? highFrame / medianFrame : 1.0;
            int depthAtWorst = SolveMaxDepth(
                frameBudgetMs, headroomMs, medianLiveFrame * liveRatio, highFrame, medianLoad, medianSave,
                elideConfirmedSaves, repairBudgetMs);

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
                replayDeterministic, depthAtWorst, highFrame, medianLiveFrame);
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
            bool elideConfirmedSaves, double repairBudgetMs)
        {
            double steadyMs = liveFrameMs + (elideConfirmedSaves ? 0 : saveMs);
            if (steadyMs > frameBudgetMs - headroomMs) return 0;

            double budget = repairBudgetMs > 0 ? repairBudgetMs : frameBudgetMs - headroomMs;
            double available = budget - liveFrameMs - loadMs;
            double perFrame = saveMs + repairFrameMs; // re-sim + re-save one repaired frame
            if (perFrame <= 0) return 0;
            double depth = available / perFrame;
            return depth <= 0 ? 0 : (int)Math.Floor(depth);
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

        private double MeasureMedian(Action op) => MeasureMedian(op, out _);

        /// <summary>
        /// Median cost of an operation, plus the 90th percentile so callers can see how much the
        /// figure moves. On a heavy core the spread is not a detail: the depth verdict flips between 3
        /// and 2 at about 3.44ms, and N64 at 1400x1050 measures a median of 3.55ms — three consecutive
        /// probes of that one configuration returned depth 2, 3 and 3, with nothing in the output to
        /// say the answer had ever been close.
        /// </summary>
        private double MeasureMedian(Action op, out double highMs)
        {
            var samples = new double[_samples];
            for (int i = 0; i < _samples; i++)
            {
                double t0 = _clock.NowMs;
                op();
                samples[i] = _clock.NowMs - t0;
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
