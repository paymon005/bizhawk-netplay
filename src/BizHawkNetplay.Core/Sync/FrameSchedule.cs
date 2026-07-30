using System;

namespace BizHawkNetplay.Core.Sync
{
    /// <summary>
    /// When the next frame is due, how many a single callback may run, and when to stop chasing debt.
    ///
    /// The tool owns the frame clock: EmuHawk is paused and a UI-thread callback steps the core. That
    /// callback does not arrive on a clean cadence — measured gaps run 3ms to 35ms around a 16.7ms mean
    /// — so every decision here exists to turn an irregular callback into a regular frame rate without
    /// either starving the window or letting emulation run away from the wall clock.
    ///
    /// It is pure arithmetic over a monotonic millisecond reading, deliberately knowing nothing about
    /// emulators, sockets or WinForms. The caller supplies the conditions this cannot see — whether a
    /// strategy is safe to burst, whether the next frame's input is confirmed — and this supplies the
    /// timing half of the same decision. That split is what makes the pacing testable at all: it used
    /// to live in a 378-line callback with the core stepping, audio pumping and telemetry inline.
    /// </summary>
    public sealed class FrameSchedule
    {
        /// <summary>
        /// How far behind before the schedule stops chasing and discards the debt. Three frame periods
        /// is long enough that ordinary jitter never trips it and short enough that one real hitch
        /// does not turn into a burst that starves presentation.
        /// </summary>
        public const double RebaseThresholdFrames = 3.0;

        /// <summary>Frames 2+ of a tick get only a hair of tolerance; the first gets much more (see
        /// <see cref="EarlyToleranceMs"/>), which is what stops "none, then two" pairs.</summary>
        private const double LaterFrameToleranceMs = 0.25;

        private readonly double _minBudgetMs;
        private readonly int _maxFramesPerTick;
        private double _frameMs;
        private double _nextDueMs;
        private double _recentCoreFrameMs;

        public FrameSchedule(double frameMs, double minBudgetMs, int maxFramesPerTick)
        {
            if (frameMs <= 0) throw new ArgumentOutOfRangeException(nameof(frameMs));
            if (maxFramesPerTick < 1) throw new ArgumentOutOfRangeException(nameof(maxFramesPerTick));
            _frameMs = frameMs;
            _minBudgetMs = minBudgetMs;
            _maxFramesPerTick = maxFramesPerTick;
        }

        /// <summary>Console frame period. Set when a session starts; the core decides it, not us.</summary>
        public double FrameMs
        {
            get => _frameMs;
            set { if (value > 0) _frameMs = value; }
        }

        /// <summary>Wall-clock reading at which the next frame becomes due.</summary>
        public double NextDueMs => _nextDueMs;

        /// <summary>
        /// Conservative rolling estimate of what one core frame costs. Rises instantly to any spike and
        /// decays at 10% a frame, because the decision it feeds — committing to a second frame inside
        /// one callback — is one where being wrong is expensive and being pessimistic is nearly free.
        /// </summary>
        public double RecentCoreFrameMs => _recentCoreFrameMs;

        /// <summary>
        /// How long one callback may spend before it must return to the message loop. Scales with the
        /// frame period rather than sitting at a fixed floor: the two-frame gate requires
        /// <c>elapsed + 2·coreCost &lt; budget</c>, so a flat 8ms made catch-up unreachable for any core
        /// costing more than ~4ms a frame, and the debt it could not repay accumulated until a rebase
        /// discarded it in a lump — which reads as "CPU-bound" for a core comfortably inside budget.
        /// </summary>
        public double BudgetMs => Math.Max(_minBudgetMs, 1.7 * _frameMs);

        /// <summary>
        /// How early the FIRST frame of a callback may run. Against a strict due-time, an irregular
        /// callback is worst-case: the one that lands early runs nothing, so the next finds two due,
        /// runs both and shows only the second — one picture lost per pair, which is why presented
        /// frames sat near 50 while the core emulated a steady 60. Half a period of slack turns "none
        /// then two" into "one then one" without changing the long-run rate, since the due time still
        /// advances by exactly one period per frame.
        /// </summary>
        public double EarlyToleranceMs => _frameMs * 0.5;

        /// <summary>
        /// Owe nothing from here. Used after a protocol pause — a resync, a reconnect, a settings
        /// change — where the wall clock advanced but no frames were owed for it.
        ///
        /// Deliberately keeps the cost estimate: the core did not get cheaper because the session
        /// paused, and throwing the measurement away would forbid the next callback from bursting on
        /// the grounds that nothing has been timed yet.
        /// </summary>
        public void RebaseTo(double nowMs) => _nextDueMs = nowMs;

        /// <summary>A new session: owe nothing and know nothing about what a frame costs.</summary>
        public void Restart(double nowMs)
        {
            _nextDueMs = nowMs;
            _recentCoreFrameMs = 0;
        }

        /// <summary>Whether a fine-grained clock should bother waking for this reading yet.</summary>
        public bool ShouldWake(double nowMs, double wakeMarginMs) => nowMs >= _nextDueMs - wakeMarginMs;

        /// <summary>
        /// Discard accumulated wall-clock debt if it has grown past the threshold, and report whether
        /// it did. Debt is discarded, never emulated frames: chasing a large hitch indefinitely is what
        /// starves presentation on a slow core.
        /// </summary>
        public bool TryRebase(double nowMs)
        {
            if (nowMs - _nextDueMs <= RebaseThresholdFrames * _frameMs) return false;
            _nextDueMs = nowMs;
            return true;
        }

        /// <summary>Whether this callback may start frame number <paramref name="framesAlreadyRun"/>.</summary>
        public bool MayRunFrame(double nowMs, int framesAlreadyRun)
        {
            if (framesAlreadyRun >= _maxFramesPerTick) return false;
            double tolerance = framesAlreadyRun == 0 ? EarlyToleranceMs : LaterFrameToleranceMs;
            return nowMs + tolerance >= _nextDueMs;
        }

        /// <summary>
        /// The timing half of "commit to another frame in this callback": one more is allowed, it is
        /// genuinely due, and the estimated cost of two more fits the remaining budget. The caller ANDs
        /// this with the conditions only it can see. Returns false while no frame has been timed yet,
        /// so the first frame of a session is never part of a burst decided on no evidence.
        /// </summary>
        public bool AnotherFrameFits(double nowMs, int framesAlreadyRun, double tickElapsedMs)
            => framesAlreadyRun + 1 < _maxFramesPerTick
               && nowMs + LaterFrameToleranceMs >= _nextDueMs + _frameMs
               && _recentCoreFrameMs > 0
               && tickElapsedMs + 2.0 * _recentCoreFrameMs < BudgetMs;

        /// <summary>Book a completed frame: the next one is due a period later, and the cost estimate
        /// takes the spike or decays a tenth toward it.</summary>
        public void FrameCompleted(double coreCostMs)
        {
            _nextDueMs += _frameMs;
            _recentCoreFrameMs = _recentCoreFrameMs <= 0
                ? coreCostMs
                : Math.Max(coreCostMs, _recentCoreFrameMs * 0.9);
        }

        /// <summary>
        /// Pay a frame's worth of debt without having run one. Rollback's time-sync yield gives frames
        /// back to let a peer catch up, and that debt is denominated in emulated frames rather than in
        /// callbacks — so the due time has to move even though nothing was stepped. No cost is recorded
        /// because no core frame ran.
        /// </summary>
        public void SkipFrame() => _nextDueMs += _frameMs;
    }
}
