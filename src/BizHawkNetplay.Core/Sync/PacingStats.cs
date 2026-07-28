using System;

namespace BizHawkNetplay.Core.Sync
{
    /// <summary>
    /// One window's worth of frame-pacing telemetry, already reduced to rates and percentiles.
    /// Produced by <see cref="PacingStats.Summarize"/>; carries no state of its own so the caller can
    /// format it whenever its own throttle allows.
    /// </summary>
    public readonly struct PacingSummary
    {
        public PacingSummary(int ticks, int frames, int presents, int stalledTicks, int timeSyncTicks,
            int rebases, double ticksPerSecond, double advancedFps, double presentedFps,
            double coreMeanMs, double coreP95Ms, double coreMaxMs, double gateMeanMs, double gateP95Ms,
            double presentMeanMs, double tickGapMeanMs, double tickGapMinMs, double tickGapMaxMs,
            int renderedFrames = 0)
        {
            RenderedFrames = renderedFrames;
            CoreMaxMs = coreMaxMs;
            TicksPerSecond = ticksPerSecond;
            TickGapMeanMs = tickGapMeanMs;
            TickGapMinMs = tickGapMinMs;
            TickGapMaxMs = tickGapMaxMs;
            Ticks = ticks;
            Frames = frames;
            Presents = presents;
            StalledTicks = stalledTicks;
            TimeSyncTicks = timeSyncTicks;
            Rebases = rebases;
            AdvancedFps = advancedFps;
            PresentedFps = presentedFps;
            CoreMeanMs = coreMeanMs;
            CoreP95Ms = coreP95Ms;
            GateMeanMs = gateMeanMs;
            GateP95Ms = gateP95Ms;
            PresentMeanMs = presentMeanMs;
        }

        public int Ticks { get; }
        public int Frames { get; }
        public int Presents { get; }
        public int StalledTicks { get; }
        public int TimeSyncTicks { get; }

        /// <summary>Pacing rebases in the window: each one silently discarded real wall-clock debt.</summary>
        public int Rebases { get; }

        /// <summary>
        /// Frame-tick callbacks per second. This is a ceiling on everything else: a frame is presented
        /// at most once per tick, so a tick rate below the console's frame rate shows up as a picture
        /// that judders no matter how fast the core is or how healthy the link is.
        /// </summary>
        public double TicksPerSecond { get; }

        /// <summary>Gap between consecutive frame-tick callbacks. A mean near the frame period with a
        /// tight min/max means the tick is locked to something frame-rate-shaped (a host run loop or
        /// vsync) rather than to its own timer; a wide spread means timer coalescing.</summary>
        public double TickGapMeanMs { get; }
        public double TickGapMinMs { get; }
        public double TickGapMaxMs { get; }

        /// <summary>Frames actually stepped through the core, per second.</summary>
        public double AdvancedFps { get; }

        /// <summary>Frames actually presented to the window, per second. Below <see cref="AdvancedFps"/>
        /// whenever the catch-up path renders only the last frame of a burst.</summary>
        public double PresentedFps { get; }

        /// <summary>Frames the core was asked to draw a picture for. Compare with
        /// <see cref="Presents"/>: the difference is work paid for and thrown away.</summary>
        public int RenderedFrames { get; }

        /// <summary>
        /// Pictures rendered but never shown. A frame is presented at most once per tick, so every
        /// frame beyond the first in a tick costs a full render that nothing ever displays — unless
        /// the catch-up path saw it coming and asked the core to skip drawing. On a light core that
        /// skip always fires and this stays at zero; on a heavy one, where the render is the expensive
        /// part, this is how much of it is being burned for nothing.
        /// </summary>
        public int UndrawnRenders => RenderedFrames > Presents ? RenderedFrames - Presents : 0;

        /// <summary>
        /// Share of emulated frames that reached the screen, 0..1.
        ///
        /// This is the one reading that separates "the session is slow" from "the session is fine and
        /// the picture is coarse". Everything else — fps, CPU-bound, stall rate — is computed from
        /// frames advanced, which stay at the console's rate precisely because the pacing code is doing
        /// its job. A session can therefore read 60/60 fps at 100% while the window updates half that
        /// often, and from the chair that looks like dropped inputs rather than a display problem.
        /// </summary>
        public double PresentedShare => AdvancedFps <= 0 ? 1 : Math.Min(1, PresentedFps / AdvancedFps);

        public double CoreMeanMs { get; }
        public double CoreP95Ms { get; }

        /// <summary>Worst single core frame in the window. Worth having next to the p95: a window only
        /// holds a few dozen frames, so p95 is effectively "second worst" and a lone spike hides in it
        /// — a mean above the p95 is that spike showing through.</summary>
        public double CoreMaxMs { get; }
        public double GateMeanMs { get; }
        public double GateP95Ms { get; }
        public double PresentMeanMs { get; }

        /// <summary>Share of ticks that stalled waiting on remote input, 0..100. The denominator is
        /// ticks, not frames — a stalled tick advances no frame, so a frame-based rate would hide
        /// exactly the case this exists to expose.</summary>
        public double StallTickPct => Ticks <= 0 ? 0 : StalledTicks * 100.0 / Ticks;

        /// <summary>Share of ticks yielded to rollback's time-sync throttle, 0..100.</summary>
        public double TimeSyncTickPct => Ticks <= 0 ? 0 : TimeSyncTicks * 100.0 / Ticks;
    }

    /// <summary>
    /// Accumulates frame-pacing telemetry so a session can tell apart the three things that all look
    /// like "lag" from the outside: a core too slow to make frame budget, lockstep stalling on the
    /// network, and pacing debt being discarded rather than repaid.
    ///
    /// Every Add* method is called from the frame tick, so all of them are allocation-free — a handful
    /// of adds and one array store. Percentiles are computed only in <see cref="Summarize"/>, which the
    /// caller runs on its existing once-per-window throttle.
    ///
    /// Not thread-safe: it is owned by the frame tick, which is single-threaded on the UI thread.
    /// </summary>
    public sealed class PacingStats
    {
        /// <summary>Ring size for the percentile samples. A second at 60fps is 60 frames, so this holds
        /// a full window with room to spare; older samples simply roll off.</summary>
        private const int SampleCapacity = 128;

        private readonly double[] _coreSamples = new double[SampleCapacity];
        private readonly double[] _gateSamples = new double[SampleCapacity];
        private readonly double[] _scratch = new double[SampleCapacity];

        private int _coreWrites;
        private int _gateWrites;
        private double _coreSum;
        private double _coreMax;
        private double _gateSum;
        private double _presentSum;
        private int _frames;
        private int _renderedFrames;
        private int _presents;
        private int _ticks;
        private int _stalledTicks;
        private int _timeSyncTicks;
        private int _rebases;
        private int _intervalCount;
        private double _intervalSum;
        private double _intervalMax;
        private double _intervalMin = double.MaxValue;

        /// <summary>
        /// One frame stepped through the core, with the wall-clock cost of that step.
        /// <paramref name="rendered"/> is whether the core was asked to draw a picture for it — false
        /// only for a frame the catch-up path already knew would be superseded before the next present.
        /// </summary>
        public void AddFrame(double coreMs, bool rendered = true)
        {
            _frames++;
            if (rendered) _renderedFrames++;
            _coreSum += coreMs;
            if (coreMs > _coreMax) _coreMax = coreMs;
            _coreSamples[_coreWrites++ & (SampleCapacity - 1)] = coreMs;
        }

        /// <summary>One present of the latest frame to the window.</summary>
        public void AddPresent(double presentMs)
        {
            _presents++;
            _presentSum += presentMs;
        }

        /// <summary>Time spent deciding whether a frame could run — for rollback this includes repair.</summary>
        public void AddGate(double gateMs)
        {
            _gateSum += gateMs;
            _gateSamples[_gateWrites++ & (SampleCapacity - 1)] = gateMs;
        }

        /// <summary>One frame-tick callback. Call exactly once per tick so the stall rate stays a
        /// share of ticks.</summary>
        public void AddTick(bool stalled, bool timeSyncYield)
        {
            _ticks++;
            if (stalled) _stalledTicks++;
            if (timeSyncYield) _timeSyncTicks++;
        }

        /// <summary>
        /// Wall-clock gap since the previous tick. The rate alone can't tell a steady cadence from a
        /// jittery one, and the difference matters: presented frames are capped by the tick rate, but
        /// frames are *dropped* by the jitter — a tick that lands early runs no frame, so the next one
        /// runs two and shows only the second.
        /// </summary>
        public void AddTickInterval(double intervalMs)
        {
            _intervalCount++;
            _intervalSum += intervalMs;
            if (intervalMs > _intervalMax) _intervalMax = intervalMs;
            if (intervalMs < _intervalMin) _intervalMin = intervalMs;
        }

        /// <summary>The pacing clock gave up on accumulated debt and jumped to now, discarding frames.</summary>
        public void AddRebase() => _rebases++;

        /// <summary>
        /// Reduce the window to rates and percentiles. Pure: safe to call more than once, and does not
        /// reset — call <see cref="Reset"/> when the window actually rolls over.
        /// </summary>
        public PacingSummary Summarize(double elapsedMs)
        {
            double perSecond = elapsedMs > 0 ? 1000.0 / elapsedMs : 0;
            return new PacingSummary(
                ticks: _ticks,
                frames: _frames,
                presents: _presents,
                stalledTicks: _stalledTicks,
                timeSyncTicks: _timeSyncTicks,
                rebases: _rebases,
                ticksPerSecond: _ticks * perSecond,
                advancedFps: _frames * perSecond,
                presentedFps: _presents * perSecond,
                coreMeanMs: Mean(_coreSum, _frames),
                coreP95Ms: Percentile95(_coreSamples, _coreWrites),
                coreMaxMs: _coreMax,
                gateMeanMs: Mean(_gateSum, _gateWrites),
                gateP95Ms: Percentile95(_gateSamples, _gateWrites),
                presentMeanMs: Mean(_presentSum, _presents),
                tickGapMeanMs: Mean(_intervalSum, _intervalCount),
                tickGapMinMs: _intervalCount == 0 ? 0 : _intervalMin,
                tickGapMaxMs: _intervalMax,
                renderedFrames: _renderedFrames);
        }

        public void Reset()
        {
            _coreWrites = 0;
            _gateWrites = 0;
            _coreSum = 0;
            _coreMax = 0;
            _gateSum = 0;
            _presentSum = 0;
            _frames = 0;
            _renderedFrames = 0;
            _presents = 0;
            _ticks = 0;
            _stalledTicks = 0;
            _timeSyncTicks = 0;
            _rebases = 0;
            _intervalCount = 0;
            _intervalSum = 0;
            _intervalMax = 0;
            _intervalMin = double.MaxValue;
        }

        private static double Mean(double sum, int count) => count <= 0 ? 0 : sum / count;

        /// <summary>
        /// 95th percentile over the retained samples, using nearest-rank on a sorted copy. Sorts into
        /// the scratch buffer rather than the ring so repeated calls keep returning the same answer.
        /// </summary>
        private double Percentile95(double[] samples, int writes)
        {
            int count = writes < SampleCapacity ? writes : SampleCapacity;
            if (count <= 0) return 0;
            Array.Copy(samples, _scratch, count);
            Array.Sort(_scratch, 0, count);
            int rank = (int)Math.Ceiling(0.95 * count);
            if (rank < 1) rank = 1;
            if (rank > count) rank = count;
            return _scratch[rank - 1];
        }
    }
}
