using System;
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
        public ProbeResult Run(double frameBudgetMs, double headroomMs)
        {
            var neutral = BuildNeutralInputs();

            // Capture a reference state once so load/advance have something valid to work on.
            var reference = _emu.SaveStateToMemory();

            // Representative serialized state size (the in-memory handle is opaque, so measure
            // the equivalent full binary state once — not on the timed hot path).
            int stateSize = _emu.ExportState().Length;

            double medianSave = MeasureMedian(() =>
            {
                var h = _emu.SaveStateToMemory();
                GC.KeepAlive(h);
            });

            double medianLoad = MeasureMedian(() => _emu.LoadStateFromMemory(reference));

            double medianFrame = MeasureMedian(() =>
                _emu.RunFramesInvisible(1, _ => neutral));

            int depth = SolveMaxDepth(
                frameBudgetMs, headroomMs, medianFrame, medianLoad, medianSave);

            return new ProbeResult(
                _emu.CoreName, stateSize, medianSave, medianLoad, medianFrame,
                frameBudgetMs, headroomMs, depth);
        }

        /// <summary>
        /// Largest depth d such that: normalFrame + load + d*(sim + save) &lt;= budget - headroom.
        /// A repair reloads once, then re-simulates and re-saves d frames. Returns 0 if even a
        /// single-frame repair overruns.
        /// </summary>
        internal static int SolveMaxDepth(
            double frameBudgetMs, double headroomMs,
            double normalFrameMs, double loadMs, double saveMs)
        {
            double available = frameBudgetMs - headroomMs - normalFrameMs - loadMs;
            double perFrame = saveMs + normalFrameMs; // re-sim + re-save one repaired frame
            if (perFrame <= 0) return 0;
            double depth = available / perFrame;
            return depth <= 0 ? 0 : (int)Math.Floor(depth);
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
