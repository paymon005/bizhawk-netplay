namespace BizHawkNetplay.Core.Session;

/// <summary>What a host does about a checksum disagreement.</summary>
public enum DesyncAction
{
    /// <summary>Do nothing at all. A rebuild is already standing the timeline back up, or the
    /// previous recovery has not had time to settle, and acting again would either race it or
    /// charge the budget twice for one fault.</summary>
    Ignore,

    /// <summary>Not a fault yet — this boundary is inside the divergence-learning window, where a
    /// disagreement IS the measurement. See <see cref="DivergenceLearner"/>.</summary>
    Measuring,

    /// <summary>Ask <see cref="DesyncOutcome.DonorPort"/> for its state: its group outvoted the
    /// host, and the host opted in to deferring. If the ask cannot be sent, fall back to
    /// <see cref="ResyncFromHost"/> — the session still has to converge on something.</summary>
    AskDonor,

    /// <summary>Recover from the host's own state, the way this has always worked.</summary>
    ResyncFromHost,
}

/// <summary>The decision, and the two facts a caller needs to say why out loud.</summary>
public readonly struct DesyncOutcome
{
    internal DesyncOutcome(DesyncAction action, int donorPort, bool hostIsOutvoted)
    {
        Action = action;
        DonorPort = donorPort;
        HostIsOutvoted = hostIsOutvoted;
    }

    public DesyncAction Action { get; }

    /// <summary>The seat to ask, or -1. Only meaningful for <see cref="DesyncAction.AskDonor"/>.</summary>
    public int DonorPort { get; }

    /// <summary>
    /// The host is about to distribute a state a majority disagreed with.
    ///
    /// True for <see cref="DesyncAction.ResyncFromHost"/> when the host lost the vote but is
    /// recovering from its own state anyway — because the player did not opt in to deferring. The
    /// caller owes them a plain sentence saying so; it is the difference between a log they can act
    /// on and one they cannot.
    /// </summary>
    public bool HostIsOutvoted { get; }
}

/// <summary>
/// What a host does when checksums disagree, as one function.
///
/// <b>Why this is here.</b> The decision was a run of early returns and nested branches in the tool
/// form, interleaved with the logging and the sending it produced. Three of its rules were
/// load-bearing and none was reachable by a test: that a disagreement inside the learning window is
/// a measurement rather than an emergency, that the systematic-mismatch trend must be recorded
/// BEFORE the branch that can return without resyncing, and that failing to reach a donor falls
/// through to the host's own state rather than leaving the session desynced.
///
/// The last two have both been wrong in shipped code. The trend recording sat after the deferral
/// branch, where the successful path's <c>return</c> skipped it — so a session that deferred every
/// time never recorded a single desync, and the one warning that names an unresyncable mismatch
/// could never fire, on exactly the sessions it exists for.
///
/// This decides; the caller executes and narrates.
/// </summary>
public static class DesyncPolicy
{
    /// <summary>
    /// Whether this boundary is inside the divergence-learning window, where peers disagreeing is
    /// the measurement being taken rather than a fault to recover from.
    ///
    /// Right after a rebuild every peer stands on byte-identical memory, so a boundary that
    /// disagrees here means machine-produced bytes — GPU write-back into main RAM — which is
    /// precisely what the learner is collecting and what the mask it produces will exclude.
    /// Resyncing instead would restart the same learning from another identical baseline, forever:
    /// the resync loop above-native N64 used to be.
    ///
    /// A real desync in this window is still caught — by the learner's own share cap, which ends
    /// the suppression with a verdict, and by the very next boundary past the window regardless.
    /// </summary>
    public static bool IsMeasuring(int frame, int checksumInterval) =>
        DivergenceLearner.IsLearnFrame(frame, checksumInterval);

    /// <summary>
    /// Decide what to do about a disagreement at <paramref name="frame"/>.
    ///
    /// <paramref name="partition"/> may be null when the ledger could not describe the split; that
    /// is not a reason to skip recovery, only a reason not to name a donor.
    /// </summary>
    public static DesyncOutcome Decide(
        int frame,
        int checksumInterval,
        bool rebuilding,
        double secondsSinceLastResync,
        double graceSeconds,
        DesyncPartition? partition,
        bool deferToMajority)
    {
        if (IsMeasuring(frame, checksumInterval))
            return new DesyncOutcome(DesyncAction.Measuring, -1, false);

        // A rebuild already in flight owns the recovery, and a resync moments old has not had time
        // to prove itself. Both are "the session is already dealing with this".
        if (rebuilding || secondsSinceLastResync < graceSeconds)
            return new DesyncOutcome(DesyncAction.Ignore, -1, false);

        bool outvoted = partition is { HostIsOutvoted: true };
        int donor = MajorityRecovery.SelectDonor(partition, deferToMajority);
        return donor >= 0
            ? new DesyncOutcome(DesyncAction.AskDonor, donor, outvoted)
            : new DesyncOutcome(DesyncAction.ResyncFromHost, -1, outvoted);
    }
}
