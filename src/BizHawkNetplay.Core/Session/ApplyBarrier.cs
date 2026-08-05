using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Session;

/// <summary>What an arriving applied-acknowledgement meant.</summary>
public enum ApplyAck
{
    /// <summary>Nobody was owed this, or not by that seat, or not for that epoch. A duplicate, a
    /// straggler from a superseded rebuild, or a peer answering for a generation the session has
    /// left. Discard it and change nothing.</summary>
    Ignored,

    /// <summary>Recorded. Others are still outstanding.</summary>
    Recorded,

    /// <summary>Recorded, and that was the last one — release the session.</summary>
    Complete,
}

/// <summary>
/// Who still owes an "I applied that epoch" acknowledgement, and the moment nobody does.
///
/// <b>Why this is a type.</b> It was a per-peer <c>AwaitingAppliedEpoch</c> field written in three
/// loops — desync resync, reconnect resync, post-timeout vacate — and read back by a fourth site
/// that walked every peer looking for one still set. Four places, one rule, and the rule includes
/// the two easy things to get wrong: an acknowledgement for the WRONG epoch is not an
/// acknowledgement (a straggler from a superseded rebuild would otherwise release a rebuild still
/// in flight), and a peer that DROPS while owing one must stop being waited on or the session hangs
/// at the barrier until a watchdog kills it.
///
/// Releasing early is the worse failure of the two: peers resume on a baseline one of them has not
/// imported, and the session desyncs at the very moment it was recovering from a desync.
///
/// <b>Threading.</b> Single-threaded by construction, like <see cref="SessionPhase"/>: every caller
/// is on the UI thread, where acknowledgements arrive marshalled from the peer readers and where
/// the link watchdog reads. Nothing here is interlocked and nothing needs to be.
/// </summary>
public sealed class ApplyBarrier
{
    private readonly HashSet<int> _outstanding = new();

    /// <summary>The epoch being waited on, or 0 when nothing is.</summary>
    public int Epoch { get; private set; }

    /// <summary>True while at least one seat still owes an acknowledgement.</summary>
    public bool IsWaiting => _outstanding.Count > 0;

    /// <summary>How many seats have not answered yet.</summary>
    public int OutstandingCount => _outstanding.Count;

    /// <summary>The seats still owing, for a log line that names them.</summary>
    public IEnumerable<int> Outstanding => _outstanding;

    /// <summary>
    /// The epoch <paramref name="seat"/> owes an acknowledgement for, or 0 if it owes nothing.
    ///
    /// This is what the link watchdog judges against: a peer can answer pings forever while never
    /// importing the state that gates the current generation, so the barrier — not liveness — is
    /// what says whether it is holding the session up. See <see cref="LinkHealth"/>.
    /// </summary>
    public int EpochOwedBy(int seat) => _outstanding.Contains(seat) ? Epoch : 0;

    /// <summary>
    /// Wait on exactly these seats for this epoch, replacing whatever was outstanding.
    ///
    /// Replacing rather than adding is deliberate: a new authoritative baseline supersedes the one
    /// before it, and a seat left over from the superseded rebuild would hold the session at a
    /// barrier for a generation nobody is going to acknowledge.
    /// </summary>
    public void Expect(IEnumerable<int> seats, int epoch)
    {
        if (seats == null) throw new ArgumentNullException(nameof(seats));
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch),
            "epoch 0 is the 'owes nothing' sentinel and cannot be waited on");
        _outstanding.Clear();
        Epoch = epoch;
        foreach (int seat in seats) _outstanding.Add(seat);
        if (_outstanding.Count == 0) Epoch = 0;   // nobody to wait for is not a wait
    }

    /// <summary>
    /// Record that a seat applied an epoch. <see cref="ApplyAck.Complete"/> is the release signal,
    /// and it is returned exactly once per barrier — a duplicate acknowledgement after the release
    /// finds the seat already gone and reads as <see cref="ApplyAck.Ignored"/>.
    /// </summary>
    public ApplyAck Applied(int seat, int epoch)
    {
        if (epoch != Epoch || !_outstanding.Remove(seat)) return ApplyAck.Ignored;
        if (_outstanding.Count > 0) return ApplyAck.Recorded;
        Epoch = 0;
        return ApplyAck.Complete;
    }

    /// <summary>
    /// Abandon the wait entirely — the session ended, or a new generation supersedes it.
    ///
    /// <b>There is deliberately no "forget one seat".</b> A dropped peer never acknowledges, so a
    /// barrier still expecting it would freeze the survivors until a watchdog ended the session —
    /// for a rebuild everyone still present had finished. What makes that unreachable is an
    /// invariant of the paths above, not of this type: <b>every way a peer leaves either ends the
    /// session or advances the generation</b>, and advancing it clears the barrier here. A joiner
    /// losing its host ends; a second drop during a reconnect ends; a drop mid-resync ends; a first
    /// drop begins a reconnect wait, which captures a boundary and advances; and a graceful leave
    /// ships a fresh authoritative state, which advances.
    ///
    /// A future drop path that does neither would hang at this barrier, and would need that seat
    /// removed from it. The single-seat removal is not written until there is such a path, because
    /// an untested method for an unreachable case is a worse guide to the rules than this sentence.
    /// </summary>
    public void Clear()
    {
        _outstanding.Clear();
        Epoch = 0;
    }
}
