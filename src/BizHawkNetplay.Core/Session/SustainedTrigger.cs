namespace BizHawkNetplay.Core.Session
{
    /// <summary>
    /// Fires at most once, and only after a condition has held continuously for a sustain window.
    ///
    /// The shape a session advisory needs: say it when the problem is real, say it once, and do not
    /// say it for a single bad second. The tool had this written out twice — for the stalling hint and
    /// the presentation hint — as a bool plus a "since" double plus five branches, each maintained by
    /// hand inside a WinForms callback where nothing could test it. Two copies of a state machine is
    /// one copy too many when the whole thing is four lines.
    ///
    /// <see cref="RestartWindow"/> exists for the rebase case: after the frame schedule discards
    /// accumulated debt, whatever the condition read a moment ago describes a session that no longer
    /// exists, but an advisory already given has still been given.
    /// </summary>
    public sealed class SustainedTrigger
    {
        private readonly double _sustainMs;
        private double _holdingSinceMs = double.NegativeInfinity;

        /// <param name="sustainMs">How long the condition must hold before firing. Zero fires on the
        /// first update that holds, which is the plain once-per-session latch.</param>
        public SustainedTrigger(double sustainMs)
        {
            _sustainMs = sustainMs < 0 ? 0 : sustainMs;
        }

        /// <summary>True once this has fired, until <see cref="Reset"/>.</summary>
        public bool HasFired { get; private set; }

        /// <summary>
        /// Returns true exactly once — on the first update where <paramref name="conditionHolds"/> has
        /// been true continuously for the sustain window. A single update with the condition false
        /// clears the window, so an intermittent problem never accumulates its way to firing.
        /// </summary>
        public bool ShouldFire(bool conditionHolds, double nowMs)
        {
            if (HasFired) return false;
            if (!conditionHolds) { _holdingSinceMs = double.NegativeInfinity; return false; }
            // Note the fall-through: the first holding update opens the window and is then tested
            // against it, so a zero sustain fires immediately rather than costing a second update.
            // With a real window the test fails on that update anyway (now - now = 0), so a
            // configured trigger behaves exactly as the hand-written version it replaces.
            if (double.IsNegativeInfinity(_holdingSinceMs)) _holdingSinceMs = nowMs;
            if (nowMs - _holdingSinceMs < _sustainMs) return false;
            HasFired = true;
            return true;
        }

        /// <summary>Forget how long the condition has been holding, but not that it already fired.</summary>
        public void RestartWindow() => _holdingSinceMs = double.NegativeInfinity;

        /// <summary>A new session: nothing has been said and nothing is holding.</summary>
        public void Reset()
        {
            HasFired = false;
            _holdingSinceMs = double.NegativeInfinity;
        }
    }
}
