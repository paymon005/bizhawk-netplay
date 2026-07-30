
namespace BizHawkNetplay.Core.Session;

/// <summary>A link watchdog verdict for one peer at one instant.</summary>
public enum LinkVerdict
{
    /// <summary>Nothing wrong (or the peer is legitimately excused right now).</summary>
    Healthy,
    /// <summary>The peer stayed alive but never applied the epoch it owes before its deadline.</summary>
    AppliedDeadlineExpired,
    /// <summary>A resync BEGIN arrived but the complete state frame did not, before its deadline.</summary>
    ResyncReceiveDeadlineExpired,
    /// <summary>Nothing heard from the peer for longer than the ping timeout.</summary>
    PingTimeout,
}

/// <summary>
/// The link watchdog's decision rule, extracted from the tool form so the barrier-death
/// scenarios are unit-testable. All times are monotonic (Stopwatch) ticks supplied by the
/// caller, which reads the snapshot fields with whatever synchronization it uses on the link.
///
/// Ordering is load-bearing:
/// 1. The apply barrier takes precedence over every excuse — pings only prove the reader
///    thread is alive, and a peer can answer them forever while never importing the state that
///    gates the current generation.
/// 2. A peer we are receiving a whole state FROM is excused from the ping timeout, but bounded
///    by its own receive deadline — BEGIN plus a silent or partial frame must never hold the
///    session forever.
/// 3. A peer still consuming a state we SENT it gets a bounded grace window (its reader can't
///    pong until the frame lands).
/// 4. Otherwise, silence past the ping timeout is a drop. A link that has never been heard
///    from at all (no receive stamp yet) is not flagged here — session start owns that window.
/// </summary>
public static class LinkHealth
{
    /// <summary>One peer's watchdog-relevant state, snapshotted by the caller.</summary>
    public readonly struct LinkSnapshot
    {
        public LinkSnapshot(int awaitingAppliedEpoch, long appliedDeadlineTicks,
            bool resyncReceiving, long resyncReceiveDeadlineTicks,
            long timeoutGraceUntilTicks, long lastRecvTicks)
        {
            AwaitingAppliedEpoch = awaitingAppliedEpoch;
            AppliedDeadlineTicks = appliedDeadlineTicks;
            ResyncReceiving = resyncReceiving;
            ResyncReceiveDeadlineTicks = resyncReceiveDeadlineTicks;
            TimeoutGraceUntilTicks = timeoutGraceUntilTicks;
            LastRecvTicks = lastRecvTicks;
        }

        /// <summary>Non-zero while this peer owes an epoch-applied acknowledgement.</summary>
        public int AwaitingAppliedEpoch { get; }
        public long AppliedDeadlineTicks { get; }
        /// <summary>True while a whole-state frame from this peer is expected.</summary>
        public bool ResyncReceiving { get; }
        public long ResyncReceiveDeadlineTicks { get; }
        /// <summary>This peer is consuming a state we sent; excused from pings until then.</summary>
        public long TimeoutGraceUntilTicks { get; }
        /// <summary>When we last heard anything from this peer; 0 = never.</summary>
        public long LastRecvTicks { get; }
    }

    public static LinkVerdict Judge(in LinkSnapshot link, long nowTicks, long pingTimeoutTicks)
    {
        if (link.AwaitingAppliedEpoch != 0 && nowTicks > link.AppliedDeadlineTicks)
            return LinkVerdict.AppliedDeadlineExpired;
        if (link.ResyncReceiving)
        {
            return nowTicks > link.ResyncReceiveDeadlineTicks
                ? LinkVerdict.ResyncReceiveDeadlineExpired
                : LinkVerdict.Healthy; // pulling a state from them, within its bounded window
        }
        if (nowTicks < link.TimeoutGraceUntilTicks) return LinkVerdict.Healthy; // digesting one we sent
        long last = link.LastRecvTicks;
        if (last != 0 && nowTicks - last > pingTimeoutTicks) return LinkVerdict.PingTimeout;
        return LinkVerdict.Healthy;
    }
}

/// <summary>What losing a peer means, given the session's current recovery phase.</summary>
public enum PeerLossAction
{
    /// <summary>A joiner has no recovery path of its own — end and let the user rejoin.</summary>
    EndSessionJoinerLostHost,
    /// <summary>Survivors may straddle two epochs; a nested barrier would be ambiguous.</summary>
    EndSessionDropDuringResync,
    /// <summary>Only one outstanding drop is supported at a time.</summary>
    EndSessionSecondDropDuringReconnect,
    /// <summary>Freeze the session and wait (bounded) for the dropped player to rejoin.</summary>
    BeginReconnectWait,
}

/// <summary>Whether a requested resync may start.</summary>
public enum ResyncGate
{
    /// <summary>One is already in flight — ignore the trigger.</summary>
    AlreadyInProgress,
    /// <summary>Too soon after the previous resync; the same desync is still settling.</summary>
    Debounced,
    /// <summary>Too many attempts — a persistent desync is a determinism bug, not transience.</summary>
    GiveUp,
    Start,
}

/// <summary>
/// The recovery phase/transition decisions of the reconnect + resync machinery, extracted from
/// the tool form. Pure functions: the form supplies its flags and executes the returned action.
/// </summary>
public static class RecoveryPolicy
{
    public static PeerLossAction OnPeerLost(bool isHost, bool resyncInProgress, bool awaitingReconnect)
    {
        if (!isHost) return PeerLossAction.EndSessionJoinerLostHost;
        if (resyncInProgress && !awaitingReconnect) return PeerLossAction.EndSessionDropDuringResync;
        if (awaitingReconnect) return PeerLossAction.EndSessionSecondDropDuringReconnect;
        return PeerLossAction.BeginReconnectWait;
    }

    /// <param name="attemptNumber">This attempt's 1-based ordinal (the caller's counter AFTER
    /// incrementing for this attempt).</param>
    public static ResyncGate GateResync(bool resyncInProgress, double secondsSinceLastResync,
        double graceSeconds, int attemptNumber, int maxResyncs)
    {
        if (resyncInProgress) return ResyncGate.AlreadyInProgress;
        if (secondsSinceLastResync < graceSeconds) return ResyncGate.Debounced;
        if (attemptNumber > maxResyncs) return ResyncGate.GiveUp;
        return ResyncGate.Start;
    }
}
