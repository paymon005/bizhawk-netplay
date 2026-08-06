namespace BizHawkNetplay.Core.Sync;

/// <summary>
/// Why a frame was refused. Four different things stall a session and they call for four different
/// responses from the person watching it, so the strategy names which one rather than leaving the
/// caller to infer it.
///
/// This replaced a single <c>LastStallWasTimeSync</c> bool, which folded the repair-budget stall in
/// with the clock-skew ones because both wanted the same scheduler treatment. They do — but the
/// diagnostics are how a user tells "my machine is too slow for this core" from "my clock has
/// drifted from my opponent's", and those are opposite actions. The bool reported the second when
/// it meant the first, and had no name at all for the stall that is by far the most common.
/// </summary>
public enum StallReason
{
    /// <summary>The frame ran. Not a stall.</summary>
    None = 0,

    /// <summary>
    /// Lockstep is waiting for a remote port's input. Nothing to tune: this is what lockstep is.
    /// </summary>
    MissingRemoteInput,

    /// <summary>
    /// Rollback hit the hard prediction cap — some port's input has been missing long enough that
    /// running another frame would put the correction target outside the ring. The network is
    /// behind, not the machine. The ordinary stall, and the one to expect on a lossy link.
    /// </summary>
    PredictionLimit,

    /// <summary>
    /// The soft time-sync cap: this peer is running further ahead of the remote than the measured
    /// latency justifies, so a frame is handed back to keep rollbacks shallow. Clock skew.
    /// </summary>
    TimeSyncSoftCap,

    /// <summary>
    /// The cost cap: the measured repair cost says THIS MACHINE cannot afford to predict this far
    /// and still repair inside its frame budget. Nothing to do with the clock or the peer — the
    /// answer is a faster machine, a lighter core, or more input delay.
    /// </summary>
    RepairBudget,

    /// <summary>
    /// The pacing exchange measured this peer genuinely ahead, so a frame is given back before the
    /// skew turns into rollback depth at all. Clock skew, corrected earlier than
    /// <see cref="TimeSyncSoftCap"/> would.
    /// </summary>
    FrameAdvantage,
}

/// <summary>How a real-time scheduler should treat each reason.</summary>
public static class StallReasonExtensions
{
    /// <summary>
    /// True when the stall was this peer's own choice rather than something it is waiting on.
    ///
    /// A deliberate yield has nothing to retry for: the frame is being given away on purpose, so
    /// the scheduler should pay a whole frame period rather than spin back a couple of milliseconds
    /// later and refuse again. A wait, by contrast, ends the moment a datagram lands, and paying a
    /// full period for it would add latency the link never asked for.
    ///
    /// <see cref="StallReason.RepairBudget"/> counts as a yield even though it is not clock skew:
    /// the frame is still being given up by choice, and the scheduler treatment is the same. That
    /// shared treatment is exactly why the old bool conflated them — the fix is to keep the
    /// treatment and separate the name.
    /// </summary>
    public static bool IsDeliberateYield(this StallReason reason) =>
        reason == StallReason.TimeSyncSoftCap
        || reason == StallReason.RepairBudget
        || reason == StallReason.FrameAdvantage;

    /// <summary>True when the stall is clock skew specifically — the two the pacing exchange and
    /// the soft cap produce, and not the machine-speed one they used to be reported with.</summary>
    public static bool IsClockSkew(this StallReason reason) =>
        reason == StallReason.TimeSyncSoftCap || reason == StallReason.FrameAdvantage;

    /// <summary>A short phrase for a log line.</summary>
    public static string Describe(this StallReason reason) => reason switch
    {
        StallReason.MissingRemoteInput => "waiting for remote input",
        StallReason.PredictionLimit => "at the prediction limit — waiting for remote input",
        StallReason.TimeSyncSoftCap => "time-sync yield (running ahead of the remote)",
        StallReason.RepairBudget => "cost-cap yield (repair too expensive for this machine)",
        StallReason.FrameAdvantage => "time-sync yield (measured frame advantage)",
        _ => "running",
    };
}
