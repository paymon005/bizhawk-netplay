namespace BizHawkNetplay.Core.Sync;

/// <summary>
/// Per-window delta of a cumulative counter whose SOURCE may be replaced while the window is open.
///
/// The obvious form of this — remember the total, subtract it next time — is correct only while the
/// thing being counted is the same object throughout. It is not here: a resync, a reconnect, or a
/// mid-session netcode change disposes the rollback strategy and builds a new one whose counters
/// start at zero, while the remembered baseline still holds the old one's lifetime total. A live
/// 4-player session reported <c>saves -15 taken/-4652 elided</c> for exactly that reason, in the
/// first window after a settings change — which is the window you would most want to read.
///
/// Resetting the baseline at every site that rebuilds a strategy would fix that instance and leave
/// the trap in place for the next one. A counter that has gone BACKWARDS cannot be the same counter,
/// so this detects the replacement itself and reports the new source's own progress. Callers need
/// know nothing about when a rebuild happened.
/// </summary>
public sealed class CounterWindow
{
    private long _baseline;

    /// <summary>
    /// Progress since the last call. A <paramref name="cumulative"/> below the baseline means the
    /// counter was replaced, in which case the whole of it is this window's progress.
    /// </summary>
    public long Observe(long cumulative)
    {
        if (cumulative < 0) cumulative = 0;
        long delta = cumulative < _baseline ? cumulative : cumulative - _baseline;
        _baseline = cumulative;
        return delta;
    }

    /// <summary>Forget the baseline — a new session, counting from nothing again.</summary>
    public void Reset() => _baseline = 0;
}
