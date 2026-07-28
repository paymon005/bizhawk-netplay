using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Core.Sync
{
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
        /// </summary>
        public int ChecksumAnchorInterval { get; init; }

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
}
