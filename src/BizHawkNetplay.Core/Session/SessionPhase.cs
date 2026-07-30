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
    /// The two flags a peer thread reads stay <c>volatile</c>, and the properties over them are
    /// therefore volatile reads. Composite questions like <see cref="IsPlaying"/> read them one after
    /// another and are not atomic — as the hand-written version was not — so they answer "was this
    /// true a moment ago", which is all any caller on another thread can act on anyway.
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

        /// <summary>Mark the resume as sent, so it is sent once. Returns false if it already was.</summary>
        public bool TryQueueResume()
        {
            if (_resumeQueued) return false;
            _resumeQueued = true;
            return true;
        }

        /// <summary>A seat is empty; hold it.</summary>
        public void BeginAwaitingRejoin() => _awaitingRejoin = true;

        /// <summary>The seat is filled, or the wait was given up on.</summary>
        public void EndAwaitingRejoin() => _awaitingRejoin = false;
    }
}
