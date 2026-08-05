namespace BizHawkNetplay.Core.Session;

/// <summary>Whether a requested resync may start.</summary>
public enum ResyncGate
{
    /// <summary>One is already in flight — ignore the trigger.</summary>
    AlreadyInProgress,
    /// <summary>Too soon after the previous resync; the same desync is still settling.</summary>
    Debounced,
    /// <summary>Too many attempts — a persistent desync is a determinism bug, not transience.</summary>
    GiveUp,
    /// <summary>Go ahead; the attempt has been charged.</summary>
    Start,
}

/// <summary>
/// How many recoveries a session gets before it admits the problem is not transient, and every way
/// that allowance comes back.
///
/// <b>Why bounded at all.</b> A resync is expensive — on a heavy core, the whole console's RAM to
/// every peer, with every emulator frozen until it lands. A desync that a resync fixes is a
/// transient; one that recurs immediately is a determinism bug, and re-shipping the state forever
/// is worse than stopping and saying so.
///
/// <b>Why this is a type.</b> The counter was an <c>int</c> on the form, spent in two places under
/// two different rules — the host's gate checked <c>count + 1 &gt; max</c> before incrementing, the
/// joiner's checked <c>++count &gt; max</c> — and cleared in four more, each with its own idea of
/// what "recovered" means. Nothing tied those six sites together, and a rule spread over six sites
/// is a rule that will eventually be six rules.
///
/// <b>The two recovery signals are not redundant.</b> A host aggregates every peer's checksum, so
/// it learns directly that the machines agree again and clears on that evidence. A joiner never
/// sees anyone else's checksum — it sends its own and hears nothing back — so it has no such
/// evidence and falls back to time: this long without another resync means the last one worked.
/// Two answers to one question, each the best available to the machine asking it.
/// </summary>
public sealed class ResyncBudget
{
    /// <summary>Attempts before a session gives up. Six is enough for a genuinely transient fault to
    /// clear several times over, and few enough that a determinism bug is named within seconds
    /// rather than shipping states until someone closes the tool.</summary>
    public const int DefaultMaxAttempts = 6;

    /// <summary>How long a joiner goes without another resync before it decides the last one
    /// worked. Comfortably longer than a resync takes to settle, so a rebuild still in progress
    /// never refunds its own attempt.</summary>
    public const double DefaultRecoverySeconds = 8.0;

    private readonly int _maxAttempts;

    public ResyncBudget(int maxAttempts = DefaultMaxAttempts)
    {
        _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
    }

    public int MaxAttempts => _maxAttempts;

    /// <summary>Attempts spent since the last confirmed recovery. This is the number in
    /// "resync #3", so it counts the attempt currently under way.</summary>
    public int Used { get; private set; }

    /// <summary>
    /// Spend one attempt. False when that would exceed the allowance — the caller ends the session
    /// rather than recovering again.
    ///
    /// The attempt is counted either way, so <see cref="Used"/> is what the give-up message quotes
    /// and a caller cannot spend twice by asking twice.
    /// </summary>
    public bool TrySpend()
    {
        Used++;
        return Used <= _maxAttempts;
    }

    /// <summary>
    /// The host's full gate: the two reasons not to start at all, then the allowance.
    ///
    /// One call rather than a phase check beside a counter check, because the ordering matters and
    /// was previously the caller's to remember: a trigger arriving mid-rebuild or inside the grace
    /// window must not CHARGE an attempt. It is the same fault twice, and charging it twice would
    /// let three genuine desyncs exhaust a six-attempt budget.
    ///
    /// <see cref="ResyncGate.Start"/> means the attempt has already been spent —
    /// <see cref="Used"/> is the number to put in "resync #N".
    /// </summary>
    public ResyncGate Gate(bool rebuilding, double secondsSinceLastResync, double graceSeconds)
    {
        if (rebuilding) return ResyncGate.AlreadyInProgress;
        if (secondsSinceLastResync < graceSeconds) return ResyncGate.Debounced;
        return TrySpend() ? ResyncGate.Start : ResyncGate.GiveUp;
    }

    /// <summary>
    /// A deliberate rebuild — a settings change, or a host savestate load — costs nothing.
    ///
    /// It is the same operation mechanically, and that is exactly the trap: charged against this
    /// budget, six delay tweaks would end a session that never desynced once. The budget exists to
    /// catch a determinism bug, and a player changing the input delay is not evidence of one.
    /// </summary>
    public void Excuse() { }

    /// <summary>
    /// Host: every machine reported the same checksum, so the last recovery worked. True when that
    /// actually cleared something, which is when the caller has a "back in sync" line worth
    /// printing.
    /// </summary>
    public bool RecordAgreement()
    {
        if (Used == 0) return false;
        Used = 0;
        return true;
    }

    /// <summary>
    /// Joiner: this long has passed without another resync, so the last one worked. True when that
    /// cleared something.
    ///
    /// See the class summary for why a joiner cannot use <see cref="RecordAgreement"/> instead.
    /// </summary>
    public bool RecordQuiet(double secondsSinceLastResync,
        double recoverySeconds = DefaultRecoverySeconds)
    {
        if (Used == 0 || secondsSinceLastResync <= recoverySeconds) return false;
        Used = 0;
        return true;
    }

    /// <summary>A fresh session, or a peer rejoined and everyone is back on one baseline.</summary>
    public void Reset() => Used = 0;
}
