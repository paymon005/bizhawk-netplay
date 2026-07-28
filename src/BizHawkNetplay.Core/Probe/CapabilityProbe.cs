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

            double medianFrame = MeasureMedian(() =>
                _emu.RunFramesInvisible(1, _ => neutral));

            foreach (var h in scratch) _emu.ReleaseState(h);

            int depth = SolveMaxDepth(
                frameBudgetMs, headroomMs, medianFrame, medianLoad, medianSave,
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
                medianFrame + (elideConfirmedSaves ? 0 : medianSave),
                replayDeterministic);
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
        /// </summary>
        internal static int SolveMaxDepth(
            double frameBudgetMs, double headroomMs,
            double normalFrameMs, double loadMs, double saveMs,
            bool elideConfirmedSaves, double repairBudgetMs)
        {
            double steadyMs = normalFrameMs + (elideConfirmedSaves ? 0 : saveMs);
            if (steadyMs > frameBudgetMs - headroomMs) return 0;

            double budget = repairBudgetMs > 0 ? repairBudgetMs : frameBudgetMs - headroomMs;
            double available = budget - normalFrameMs - loadMs;
            double perFrame = saveMs + normalFrameMs; // re-sim + re-save one repaired frame
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

        private double MeasureMedian(Action op)
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
            return (samples.Length & 1) == 1
                ? samples[mid]
                : (samples[mid - 1] + samples[mid]) / 2.0;
        }
    }
}
