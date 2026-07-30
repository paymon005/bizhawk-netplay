namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Distinguishes emulation drift from a systematic mismatch, by whether desyncs are interleaved
/// with agreements or unbroken.
///
/// A real drift syncs fine for a while first, so a resync clears it. A divergence that recurs at
/// EVERY checksum interval with nothing agreeing in between means the two machines are comparing
/// memory that was never going to match — on N64 typically an above-native video resolution, whose
/// framebuffer resolve lands GPU-produced bytes inside the hashed region. Resyncing cannot fix that
/// and will keep shipping whole states forever, so it is worth naming.
///
/// Extracted from the tool because the version living there had the agreement recorded inside a
/// <c>Verbose</c> logging branch — so with logging off, which is the default, nothing ever counted
/// as an agreement and the second desync of an ordinary session was announced as systematic. That
/// class of bug is what a state machine buried in a UI callback invites; here it is three methods
/// and a test.
/// </summary>
public sealed class DesyncTrend
{
    /// <summary>Unbroken desyncs before the pattern is called systematic. Two, because one desync
    /// says nothing and waiting longer means more whole-state transfers that cannot help.</summary>
    public const int SystematicThreshold = 2;

    private bool _agreedSinceLastDesync;

    /// <summary>Consecutive desyncs with no agreeing checksum in between.</summary>
    public int UnbrokenDesyncs { get; private set; }

    /// <summary>True once <see cref="UnbrokenDesyncs"/> has reached the threshold.</summary>
    public bool IsSystematic => UnbrokenDesyncs >= SystematicThreshold;

    /// <summary>Every player agreed on a checksum. Records that the run of desyncs is broken.</summary>
    public void RecordAgreement() => _agreedSinceLastDesync = true;

    /// <summary>
    /// Record a desync. Returns true exactly once — on the report that first makes the pattern
    /// systematic — so the caller cannot accidentally announce it repeatedly or not at all.
    /// </summary>
    public bool RecordDesync()
    {
        bool wasSystematic = IsSystematic;
        if (!_agreedSinceLastDesync) UnbrokenDesyncs++;
        _agreedSinceLastDesync = false;
        return !wasSystematic && IsSystematic;
    }

    /// <summary>Start over — a new session, or a new timeline that has yet to prove anything.</summary>
    public void Reset()
    {
        _agreedSinceLastDesync = false;
        UnbrokenDesyncs = 0;
    }
}
