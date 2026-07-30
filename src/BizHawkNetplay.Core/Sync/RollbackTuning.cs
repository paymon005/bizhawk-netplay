using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Core.Sync;

/// <summary>
/// Per-peer knobs for how <see cref="RollbackStrategy"/> spends its savestate and repair budget.
///
/// None of this is negotiated, and none of it may be: every setting changes only <em>how</em> a peer
/// reconstructs a frame, never <em>which inputs</em> that frame applies. Finalized frames are
/// computed from real inputs only, so they stay byte-identical however the peer got there — the
/// same reasoning that already lets each peer size its own ring independently.
///
/// Every default reproduces the original behaviour exactly, so an omitted tuning is a no-op.
/// </summary>
public sealed class RollbackTuning
{
    /// <summary>
    /// Skip the entering-state snapshot for frames whose every port is already confirmed.
    ///
    /// Such a frame can never become a rollback target — see <see cref="RollbackStrategy"/> — so its
    /// state is dead weight. On a healthy link with input delay covering the latency that is
    /// <em>most</em> frames, which turns rollback's steady-state cost from "one whole-core savestate
    /// every frame, forever" into nothing. That tax is what makes rollback unaffordable on a heavy
    /// core: on N64 a save is 6.1ms against a 2.0ms frame, so 48% of the frame budget was being
    /// spent insuring against a correction that mostly never comes.
    /// </summary>
    public bool ElideConfirmedSaves { get; init; }

    /// <summary>
    /// Force an anchor at the frames <see cref="RollbackStrategy.TryConfirmedChecksum"/> needs, in
    /// frames (0 disables). MUST be set to the same interval the checksum is polled with whenever
    /// <see cref="ElideConfirmedSaves"/> is on: the frame a checksum reads is by construction inside
    /// the finalized region, which is exactly what elision drops. Without this, desync detection
    /// stops silently — the checksum simply never finds its state and reports nothing.
    ///
    /// The anchor is also where the checksum's hash is taken, since the core is already standing on
    /// the state there. A value that does not match the caller's interval therefore costs a hash
    /// nobody consumes on top of losing detection — the checksum falls back to fetching its own
    /// state, which is correct but is the expensive path this exists to avoid.
    /// </summary>
    public int ChecksumAnchorInterval { get; init; }

    /// <summary>
    /// Snapshot only every Nth frame that still carries a prediction, instead of every one
    /// (0 or 1 keeps the original every-frame behaviour).
    ///
    /// The savestate is what a repair actually spends its budget on. Measured on N64 over eight
    /// stationary runs: a repaired frame costs 9.24ms, of which the snapshot is 6.82ms — 74%. The
    /// frame it re-simulates is 2.41ms. So the way to buy prediction depth is to stop snapshotting
    /// most of the frames, not to make the frames cheaper.
    ///
    /// A repair then has to start from the newest keyframe at or before its target and re-simulate
    /// the extra frames up to it — at most N-1 of them. That trade is favourable exactly when the
    /// snapshot dominates the frame, which is N64's shape by a factor of nearly three. Past N=3 the
    /// walk-back starts costing more than the snapshots it saves, so this is not a knob to turn up.
    ///
    /// On the same measurements, against a 33.4ms repair budget:
    ///
    ///     N=1  depth 2   repair 31.5ms   (over the ~28.4ms tick budget)
    ///     N=2  depth 3   repair 27.1ms   (inside it)
    ///     N=3  depth 3   repair 25.7ms
    ///     N=4  depth 2   -- walk-back dominates
    ///
    /// So N=2 buys a frame of depth AND brings a worst-case repair inside the tick budget for the
    /// first time, which is the hitch that has been showing up as a slow tick on correction.
    ///
    /// Like every other tuning here this changes only HOW a frame is reconstructed, never which
    /// inputs it applies, so peers need not agree on it.
    /// </summary>
    public int KeyframeInterval { get; init; }

    /// <summary>
    /// Wall-clock ceiling for one synchronous repair, in milliseconds (0 disables). The strategy
    /// measures what repair actually costs and trims how far ahead it predicts to keep the next one
    /// inside this. A frame count cannot do that job when frame cost varies — which on a heavy core
    /// it does, by an order of magnitude between quiet and busy scenes.
    /// </summary>
    public double RepairBudgetMs { get; init; }

    /// <summary>Clock used to measure repair cost. Required for <see cref="RepairBudgetMs"/>.</summary>
    public IMonotonicClock? Clock { get; init; }

    /// <summary>The original behaviour: save every frame, no anchors, no cost ceiling.</summary>
    public static readonly RollbackTuning Legacy = new RollbackTuning();
}
