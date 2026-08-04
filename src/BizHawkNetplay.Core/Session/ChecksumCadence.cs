using System;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// How often the desync checksum runs, decided from what one hash actually costs instead of a
/// constant chosen when every hash was expensive.
///
/// The 300-frame interval dates from when a full-memory hash was a ~7-38ms hitch, so it had to be
/// rare. With the fast paths a hash is ~0.1ms on the light cores and ~2ms on N64 — at 300 frames
/// that is detection deliberately five seconds late for a cost that no longer exists. Five seconds
/// matters: a desync detected at the next second costs a resync over one second of divergence and
/// play that barely hiccups; the same desync five seconds later has had that much longer to matter.
///
/// The interval is a SESSION agreement, not a preference: peers quantize checksums to interval
/// boundaries and the host's ledger resolves a frame only when every peer reports it, so two peers
/// on different intervals never complete a frame — detection silently stops. The host therefore
/// measures, chooses, and publishes one figure in WELCOME; a value outside the sane range reads as
/// a malformed frame and falls back to the default.
///
/// The host's machine is the sample, and the figure is applied to everyone — a deliberately rough
/// model, biased safe by the floor: even if a joiner's hash costs several times the host's, the
/// floor keeps the amortized cost per frame in the noise.
/// </summary>
public static class ChecksumCadence
{
    /// <summary>The historical interval, kept as the fallback for anything unmeasured.</summary>
    public const int DefaultIntervalFrames = 300;

    /// <summary>Fastest cadence worth running: once a second at 60fps. Below this the checksum
    /// anchor's bookkeeping (rollback keeps a snapshot per interval) starts being the cost.</summary>
    public const int MinIntervalFrames = 60;

    /// <summary>Slowest cadence tolerated: twice the historical figure, for the SHA-fallback cores
    /// where one hash really is tens of milliseconds.</summary>
    public const int MaxIntervalFrames = 600;

    /// <summary>Amortized budget: one hash per interval may cost up to this share of the frame
    /// period per frame. Half a percent keeps the checksum invisible in the pacing telemetry.</summary>
    public const double BudgetShare = 0.005;

    /// <summary>The interval one measured hash cost affords, clamped to the sane range. Garbage
    /// measurements (nothing measured, a stopped clock) get the historical default.</summary>
    public static int Choose(double hashCostMs, double frameMs)
    {
        if (double.IsNaN(hashCostMs) || double.IsInfinity(hashCostMs) || hashCostMs <= 0
            || double.IsNaN(frameMs) || double.IsInfinity(frameMs) || frameMs <= 0)
            return DefaultIntervalFrames;
        double needed = hashCostMs / (BudgetShare * frameMs);
        if (needed <= MinIntervalFrames) return MinIntervalFrames;
        if (needed >= MaxIntervalFrames) return MaxIntervalFrames;
        return (int)Math.Ceiling(needed);
    }

    /// <summary>Whether a wire-carried interval is one this build will run. Outside the range is
    /// a malformed or hostile frame, not a preference to honour.</summary>
    public static bool IsAcceptable(int intervalFrames) =>
        intervalFrames >= MinIntervalFrames && intervalFrames <= MaxIntervalFrames;
}
