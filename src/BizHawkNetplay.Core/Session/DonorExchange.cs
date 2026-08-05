using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Session;

/// <summary>What a caller should do with an arriving state offer.</summary>
public enum OfferVerdict
{
    /// <summary>Not from the seat we asked, or we asked nobody. Discard without clearing the wait —
    /// an unsolicited offer must not cancel a legitimate one still in flight.</summary>
    Unsolicited,
    /// <summary>From the right seat, but for a generation the session has moved past. The wait is
    /// over — the peer answered — but the bytes are stale and the caller recovers its own way.</summary>
    Stale,
    /// <summary>The state we asked for. Adopt it and distribute it as the session's.</summary>
    Adopt,
}

/// <summary>
/// The host's side of a majority-recovery ask: one outstanding request, and the ways it can end.
///
/// <b>Why this is a type rather than two fields.</b> It was two fields and a timer in the tool form,
/// and the transitions were spread across four methods that each had to remember the others. Two of
/// them did not. A second desync arriving while an ask was in flight fell through to the ordinary
/// host-authoritative resync and left the ask armed; the donor's now-stale reply was discarded by a
/// guard that returned WITHOUT clearing it; and the timeout then fired, decided the donor had never
/// answered, and ran a second full-state resync for a divergence already recovered from. On N64
/// that is sixteen megabytes to every peer, for nothing, with a log line blaming a machine that had
/// answered on time.
///
/// Every way out of the wait is a method here, so "clear the wait" cannot be forgotten by one path
/// and remembered by another. The tool keeps the timer and the transport; this keeps the rule.
///
/// Not thread-safe: the caller drives it from one thread (the UI thread, where every recovery
/// decision is already marshalled).
/// </summary>
public sealed class DonorExchange
{
    /// <summary>
    /// How long the host waits for the donor's state before falling back to its own.
    ///
    /// This was a flat 15 seconds, chosen as "generous" against a donor that has to capture and
    /// deflate a whole savestate. It was generous against a fast link and nothing else: the same
    /// codebase budgets a state transfer at <see cref="StateTransferBudget.MinBytesPerSecond"/>,
    /// deliberately pessimistic, and one N64 state at that floor is a minute and a half. On the
    /// slow uplink that floor exists to model, the host gave up on a donor that was behaving
    /// perfectly and resynced everyone from its OWN state — which on the checksum evidence is the
    /// wrong one, and is the exact outcome majority recovery exists to prevent. It would then
    /// spend the same minute and a half distributing it.
    ///
    /// So it is the transfer model's own allowance for those bytes, like every other state
    /// deadline in the session. The wait is long, and that is the honest figure: the alternative
    /// to waiting is not a quick recovery, it is a slow wrong one.
    ///
    /// <paramref name="stateBytes"/> is the host's own state size, which is the best estimate it
    /// has of the donor's — same core, same game, same frame.
    /// </summary>
    public static double StateTimeoutSeconds(int stateBytes) =>
        StateTransferBudget.OnePhaseSeconds(stateBytes);

    /// <summary>The same figure in milliseconds, for a timer interval.</summary>
    public static int StateTimeoutMs(int stateBytes) =>
        (int)System.Math.Min(int.MaxValue, StateTimeoutSeconds(stateBytes) * 1000.0);

    /// <summary>The seat we are waiting on, or -1.</summary>
    public int AwaitingPort { get; private set; } = -1;

    /// <summary>The generation the ask belongs to. Meaningless while <see cref="IsWaiting"/> is false.</summary>
    public SessionGeneration AwaitingGeneration { get; private set; }

    public bool IsWaiting => AwaitingPort >= 0;

    /// <summary>
    /// Record that a request has gone out. False when one is already in flight — the caller must
    /// not have sent a second, and a second reply would be indistinguishable from the first.
    /// </summary>
    public bool Begin(int donorPort, SessionGeneration generation)
    {
        if (donorPort < 0) return false;
        if (IsWaiting) return false;
        AwaitingPort = donorPort;
        AwaitingGeneration = generation;
        return true;
    }

    /// <summary>
    /// Forget any outstanding ask.
    ///
    /// Called wherever recovery happens by some other route, because the ask belongs to the desync
    /// that route is replacing. This is the call the old code was missing: left armed, the timeout
    /// fires later, finds itself still waiting, and recovers a second time from a divergence that
    /// is already gone.
    /// </summary>
    public void Cancel()
    {
        AwaitingPort = -1;
        AwaitingGeneration = default;
    }

    /// <summary>
    /// Judge an arriving offer, and end the wait if it came from the seat we asked.
    ///
    /// The seat check and the generation check are deliberately separate outcomes. An offer from
    /// the wrong seat leaves the wait alone, because a peer we did not ask must not be able to
    /// cancel one we did. An offer from the RIGHT seat ends the wait whichever generation it
    /// carries: that peer has answered, and staying armed afterwards is what produced the redundant
    /// resync.
    /// </summary>
    public OfferVerdict Offer(int fromPort, SessionGeneration generation)
    {
        if (!IsWaiting || fromPort != AwaitingPort) return OfferVerdict.Unsolicited;
        bool current = generation == AwaitingGeneration;
        Cancel();
        return current ? OfferVerdict.Adopt : OfferVerdict.Stale;
    }

    /// <summary>
    /// The wait ran out. True when there was one to run out — false means something else already
    /// ended it and the caller must not recover a second time on a timer that was left running.
    /// </summary>
    public bool Expire(out int donorPort)
    {
        donorPort = AwaitingPort;
        if (!IsWaiting) return false;
        Cancel();
        return true;
    }
}
