namespace BizHawkNetplay.Core.Session
{
    /// <summary>Why the session is standing itself back up. Only ever meaningful while
    /// <see cref="SessionPhase.IsRebuilding"/>.</summary>
    public enum RebuildReason
    {
        /// <summary>Not rebuilding.</summary>
        None = 0,

        /// <summary>Checksums disagreed; everyone is being put back on one state.</summary>
        Desync,

        /// <summary>The host changed netcode or input delay and every peer is being rebuilt for it.</summary>
        SettingsChange,

        /// <summary>A peer's link broke; survivors are frozen on a shared baseline while its seat waits.</summary>
        PeerLoss,
    }

    /// <summary>
    /// Where a session is in its life, as one object rather than four booleans maintained by hand.
    ///
    /// The shape is taken from the combinations the code actually occupies, not from a tidy sequence:
    /// <b>rebuilding and awaiting-a-rejoin are independent</b>. Losing a peer sets both — the survivors
    /// are rebuilt onto a shared baseline (a rebuild) while the empty seat waits for its player (a
    /// rejoin) — and they are cleared at different moments. An enum that listed "Recovering" and
    /// "AwaitingReconnect" as alternatives would have been unable to express the one case that matters
    /// most, which is exactly what a session-state enum proposed for this code did.
    ///
    /// <b>Single writer.</b> Every mutating method here must be called from the UI thread, which is
    /// where the form has always maintained these flags. The two that peer threads READ stay
    /// <c>volatile</c>, and the properties over them are therefore volatile reads — but nothing here
    /// is interlocked, so <see cref="TryQueueResume"/> is a check-then-set that is only atomic by
    /// virtue of that single writer, not by its own construction. Composite questions like
    /// <see cref="IsPlaying"/> read two flags in sequence and are likewise not atomic, so off-thread
    /// they answer "was this true a moment ago" — which is all a reader on another thread could act
    /// on anyway, and is exactly what the four booleans offered before.
    ///
    /// The transitions refuse impossible states rather than trusting callers to check first. Every
    /// caller does check today, so the guards change nothing now; they exist because the whole point
    /// of naming these transitions was to stop the next one from having to know the rules.
    /// </summary>
    public sealed class SessionPhase
    {
        private volatile bool _active;
        private volatile bool _awaitingRejoin;
        private RebuildReason _rebuild;
        private bool _resumeQueued;

        /// <summary>GO has been given and this peer is in the session (whatever it is doing in it).</summary>
        public bool IsActive => _active;

        /// <summary>Why the timeline is being rebuilt, or <see cref="RebuildReason.None"/>.</summary>
        public RebuildReason Rebuild => _rebuild;

        /// <summary>A rebuild is in flight: frames are held, and a second one must not start.</summary>
        public bool IsRebuilding => _rebuild != RebuildReason.None;

        /// <summary>A seat is empty and its player has been given time to come back.</summary>
        public bool AwaitingRejoin => _awaitingRejoin;

        /// <summary>The resume that ends this rebuild has been handed to the writers; do not send it twice.</summary>
        public bool ResumeQueued => _resumeQueued;

        /// <summary>Running normally — active, not rebuilding, nobody's seat empty.</summary>
        public bool IsPlaying => _active && !IsRebuilding && !_awaitingRejoin;

        /// <summary>GO. Everything else starts clear.</summary>
        public void Start()
        {
            _active = true;
            _rebuild = RebuildReason.None;
            _awaitingRejoin = false;
            _resumeQueued = false;
        }

        /// <summary>The session is over, however it ended.</summary>
        public void Stop()
        {
            _active = false;
            _rebuild = RebuildReason.None;
            _awaitingRejoin = false;
            _resumeQueued = false;
        }

        /// <summary>
        /// Begin standing the timeline back up. Returns false if one is already in flight — two
        /// authoritative baselines racing each other is the way this desyncs the session it exists to
        /// keep running, so the refusal is the point rather than an inconvenience.
        /// </summary>
        public bool BeginRebuild(RebuildReason reason)
        {
            if (!_active) return false;   // nothing to rebuild before GO or after the session ends
            if (reason == RebuildReason.None) return false;
            if (IsRebuilding) return false;
            _rebuild = reason;
            _resumeQueued = false;
            return true;
        }

        /// <summary>Every peer applied the new baseline and has been released.</summary>
        public void EndRebuild()
        {
            _rebuild = RebuildReason.None;
            _resumeQueued = false;
        }

        /// <summary>
        /// Mark the resume as sent, so it is sent once. Returns false if it already was, or if no
        /// rebuild is in flight to resume from.
        ///
        /// That second condition is not merely defensive. Both call sites fire when a peer reports it
        /// applied the new baseline, and a report arriving after the rebuild already ended would find
        /// the queue flag cleared by <see cref="EndRebuild"/>, the generation unchanged, and would
        /// therefore put a second RESUME on the wire for a session that had already resumed.
        /// </summary>
        public bool TryQueueResume()
        {
            if (!IsRebuilding) return false;
            if (_resumeQueued) return false;
            _resumeQueued = true;
            return true;
        }

        /// <summary>A seat is empty; hold it. Only meaningful inside a session.</summary>
        public void BeginAwaitingRejoin()
        {
            if (_active) _awaitingRejoin = true;
        }

        /// <summary>The seat is filled, or the wait was given up on.</summary>
        public void EndAwaitingRejoin() => _awaitingRejoin = false;
    }
}
