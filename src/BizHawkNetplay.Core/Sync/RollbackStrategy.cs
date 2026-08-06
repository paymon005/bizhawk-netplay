using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Core.Sync;

/// <summary>
/// GGPO-style rollback (§3.6) — the latency-hiding upgrade that drops in behind the same
/// <see cref="ISyncStrategy"/> seam as <see cref="LockstepStrategy"/>. Where lockstep stalls
/// until every port's input is confirmed, rollback runs immediately by <em>predicting</em>
/// unconfirmed remote inputs (repeat-last), then, when a real input arrives that contradicts a
/// prediction, silently re-simulates the affected frames to the present with the corrected input.
///
/// This is the one strategy that must touch the emulator directly: it owns a per-frame savestate
/// ring and drives synchronous repair through <see cref="IEmuAdapter.RunFramesInvisible"/> (the
/// reentrant frame-advance M0 proved works). Everything else — transport, pipeline, serialization,
/// desync detection — is shared with lockstep unchanged.
///
/// Correctness invariant: a frame is <em>final</em> once every port's real input for it (and all
/// prior frames) has arrived; final frames are computed from real inputs only, so they are
/// byte-identical to what lockstep would have produced. Prediction only ever affects the not-yet-
/// final tail, which rollback rewrites before it can be observed as a checkpoint. All methods run
/// single-threaded on the UI thread, interleaved with the <see cref="FrameDriver"/>.
/// </summary>
public sealed class RollbackStrategy : ISyncStrategy, IDisposable
{
    // How many extra frames of state to retain beyond the rollback cap, so a correction landing
    // exactly at the prediction horizon still finds its base state in the ring.
    private const int PruneMargin = 2;
    // Time-sync soft cap = (one-way latency in frames) + SoftMargin, floored at MinSoftCap.
    private const int SoftMargin = 2;
    private const int MinSoftCap = 3;
    // Measured frame advantage: ignore anything under this (ordinary jitter, and a 1-frame lead is
    // not worth a stall), and never give back more than MaxAdvantageStall frames from one report —
    // yielding the whole surplus at once overshoots and sets up an oscillation with the peer.
    private const int AdvantageStallThreshold = 2;
    private const int MaxAdvantageStall = 3;
    // Never let the measured cost ceiling collapse rollback into lockstep-with-extra-steps; below
    // this it isn't hiding any latency and the peer would be better off choosing lockstep outright.
    private const int MinCostCap = 2;

    private readonly InputPipeline _pipeline;
    private readonly IEmuAdapter _adapter;
    private readonly int _portCount;
    private readonly int _localPort;
    private readonly int _maxRollback;
    private readonly double _frameMs;   // console frame period; 0 disables time-sync
    private readonly PortInput[] _neutral;
    private int _softCap;               // horizon at which time-sync trims (<= _maxRollback)

    // --- tuning (see RollbackTuning; none of it is negotiated) ---------------------
    private readonly bool _elideConfirmedSaves;
    private readonly int _checksumAnchorInterval;
    private readonly int _keyframeInterval;  // 1 = snapshot every predicted frame
    private readonly double _repairBudgetMs;
    private readonly IMonotonicClock? _clock;
    private double _repairPerFrameMs;   // conservative rolling estimate of resim cost
    private int _costCap;               // horizon the measured repair budget affords (<= _maxRollback)

    // state[N] = whole-core state captured entering frame N (i.e. the result of frames 0..N-1).
    private readonly Dictionary<int, StateHandle> _states = new();
    // applied[N] = the InputSet actually run for frame N (real where known, predicted otherwise).
    private readonly Dictionary<int, InputSet> _applied = new();
    private readonly List<int> _pruneScratch = new();

    private int _advantageStallFrames;      // frames still owed back because we measured ourselves ahead
    private int _lastAdvantageSequence = -1; // makes periodic UI refreshes edge-triggered
    private int _rollbackTo = int.MaxValue; // earliest frame a late input contradicted (pending repair)
    private int _savedFrame = -1;           // frame whose entering-state is currently snapshotted
    private int _lastRunFrame = -1;         // highest frame actually simulated so far
    private int _lastChecksumFrame = -1;    // highest interval-boundary already checksummed (dedupe)
    private bool _restoreFailed;            // core stranded off its live frame; nothing can recover

    // Checksum of the state entering a checksum anchor, taken while the core was already standing
    // there. -1 means nothing cached and TryConfirmedChecksum must go and fetch the state itself.
    // One slot is enough: an anchor is only pending between the live frame reaching boundary+1 and
    // the confirmed frontier reaching boundary, a gap bounded by the prediction cap — so two
    // anchors could only overlap if the cap exceeded the checksum interval.
    private int _anchorHashFrame = -1;
    private uint _anchorHash;
    private double _pendingHashMs;

    /// <param name="frameMs">
    /// Console frame period, used to turn a measured round-trip time (via <see cref="OnPacingReport"/>)
    /// into a target prediction horizon for time-sync. 0 (the default) disables time-sync entirely —
    /// the strategy then only ever stalls at the hard ring cap.
    /// </param>
    public RollbackStrategy(InputPipeline pipeline, IEmuAdapter adapter, int localPort, int maxRollback,
        double frameMs = 0, RollbackTuning? tuning = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _portCount = pipeline.PortCount;
        if (localPort < 0 || localPort >= _portCount) throw new ArgumentOutOfRangeException(nameof(localPort));
        if (maxRollback < 1) throw new ArgumentOutOfRangeException(nameof(maxRollback));
        _localPort = localPort;
        _maxRollback = maxRollback;
        _frameMs = frameMs;
        _softCap = maxRollback; // no trimming until a pacing report narrows it

        var t = tuning ?? RollbackTuning.Legacy;
        _elideConfirmedSaves = t.ElideConfirmedSaves;
        _checksumAnchorInterval = t.ChecksumAnchorInterval;
        _keyframeInterval = t.KeyframeInterval < 1 ? 1 : t.KeyframeInterval;
        _clock = t.Clock;
        _repairBudgetMs = _clock != null ? t.RepairBudgetMs : 0; // a budget with no clock is unmeasurable
        _costCap = maxRollback; // no trimming until a repair has actually been timed...
        // ...unless the probe already measured what one costs. RecordRepairCost applies the same
        // arithmetic a real sample would, so the cap starts correct rather than being discovered
        // by paying for it once.
        if (t.SeedRepairPerFrameMs > 0) RecordRepairCost(t.SeedRepairPerFrameMs);

        _neutral = new PortInput[_portCount];
        for (int p = 0; p < _portCount; p++)
            _neutral[p] = PortInput.Neutral(adapter.GetControllerLayout(p));
    }

    /// <summary>Depth of the savestate ring (max frames rollback may re-simulate).</summary>
    public int MaxRollback => _maxRollback;

    /// <summary>True while the most recent BeginFrame hit the prediction cap and could not proceed.</summary>
    public bool IsStalled { get; private set; }

    // --- Diagnostics (surfaced in the status line / asserted by tests) --------------
    public int RollbackCount { get; private set; }

    /// <summary>
    /// How far back the last correction reached — the distance from the frame being decided to the
    /// earliest frame whose inputs turned out wrong. This is the quantity the prediction caps bound
    /// and the probe's depth model is solved for.
    /// </summary>
    public int LastRollbackDepth { get; private set; }

    /// <summary>
    /// Frames the last repair actually re-simulated, which is <see cref="LastRollbackDepth"/> plus
    /// <see cref="LastRollbackWalkback"/>. Kept apart from the depth because they answer different
    /// questions: depth is how wrong the prediction was, this is what the correction cost.
    /// </summary>
    public int LastReplayedFrames { get; private set; }

    public int MaxRollbackDepthSeen { get; private set; }
    public long FramesResimulated { get; private set; }
    public int TimeSyncStalls { get; private set; }
    /// <summary>Frames yielded because the measured repair cost, not the clock skew, said to.</summary>
    public int CostStalls { get; private set; }
    public int SavesTaken { get; private set; }
    /// <summary>Snapshots skipped because the frame was already fully confirmed (see RollbackTuning).</summary>
    public int SavesElided { get; private set; }
    /// <summary>Frames a repair had to re-simulate BEFORE its target, because the nearest keyframe
    /// sat behind it. Zero unless <see cref="RollbackTuning.KeyframeInterval"/> is above 1.</summary>
    public int LastRollbackWalkback { get; private set; }
    public long FramesWalkedBack { get; private set; }
    /// <summary>Prediction horizon the measured repair budget currently affords.</summary>
    public int CostCap => _costCap;
    /// <summary>
    /// True once the measured repair cost has been high enough that the cost cap had to be held up
    /// by its floor — that is, this machine cannot repair even the minimum depth inside its frame
    /// budget, and rollback is running on a promise it has measured itself unable to keep. The
    /// session should raise input delay or switch to lockstep rather than carry on.
    /// </summary>
    public bool BudgetExceededByFloor { get; private set; }
    /// <summary>The per-frame repair cost when <see cref="BudgetExceededByFloor"/> first latched,
    /// so the advice can name the measurement rather than assert the conclusion.</summary>
    public double FloorRepairPerFrameMs { get; private set; }
    /// <summary>Highest interval boundary already checksummed, so a caller can tell whether the
    /// NEXT checksum will land inside a window it cares about (divergence learning asks).</summary>
    public int LastChecksumFrame => _lastChecksumFrame;

    /// <summary>Checksums answered from the anchor rather than by visiting the state again.</summary>
    public int ChecksumsFromAnchor { get; private set; }
    /// <summary>Checksums that had to save, load, hash and load back — the pre-anchor cost.</summary>
    public int ChecksumsByVisit { get; private set; }

    /// <summary>
    /// Hash time accrued inside <see cref="BeginFrame"/> since the last call, and reset.
    ///
    /// The anchor hash runs while the core is mid-frame-decision, so left alone it would land in
    /// whatever the caller measures around that call — where the checksum's cost is indistinguishable
    /// from repair, which is the one thing that measurement exists to separate. Drain it once per
    /// tick and the two stay disjoint.
    /// </summary>
    public double TakeHashCostMs()
    {
        double ms = _pendingHashMs;
        _pendingHashMs = 0;
        return ms;
    }
    /// <summary>Live entries in the applied-input map; bounded by the ring window, not the session.</summary>
    public int AppliedCount => _applied.Count;
    /// <summary>Whether the next otherwise-runnable frame will be yielded for measured clock skew.</summary>
    public bool HasPendingTimeSyncDebt => _advantageStallFrames > 0;

    /// <summary>True when the latest rejected frame was deliberate time synchronization rather
    /// than the hard prediction-safety gate. A real-time scheduler should pay one frame period for
    /// this stall instead of retrying it a couple of milliseconds later.</summary>
    public bool LastStallWasTimeSync { get; private set; }

    public FrameDecision BeginFrame(int frame)
    {
        // 1) Absorb any correction that arrived for an already-simulated frame: reload and
        //    re-run forward to `frame` with the fixed inputs before we decide anything new.
        ExecutePendingRollback(frame);

        int horizon = RemoteHorizon(frame);
        LastStallWasTimeSync = false;

        // 2a) Hard cap. Never run so far past the slowest remote port that a late correction could
        //     target a frame already evicted from the ring — that would be an unrecoverable desync.
        if (horizon > _maxRollback)
        {
            IsStalled = true;
            return FrameDecision.StallDecision;
        }

        // 2b) Time-sync soft cap. Once a clock-skewed peer runs further ahead of the remote than the
        //     latency actually warrants, hold it for a tick so the other catches up — keeping
        //     rollbacks shallow instead of letting depth grow toward the hard cap. Disabled (soft cap
        //     == hard cap) until a pacing report narrows it, so it never trims the latency we mean to
        //     hide. This is rollback's only routine backpressure and is rare on a sane link.
        //     The cost cap rides alongside it: where the soft cap trims skew the link doesn't
        //     justify, the cost cap trims depth this MACHINE can't repair inside its budget. Both
        //     only ever narrow how far we predict — neither touches the ring depth or the driver's
        //     accept window, which govern which late datagrams are still usable.
        if (horizon > _softCap || horizon > _costCap)
        {
            // A soft-cap stall already gives the faster peer one frame back. If measured-advantage
            // debt is outstanding, pay it here too so the two time-sync mechanisms do not charge
            // twice for the same skew.
            if (_advantageStallFrames > 0) _advantageStallFrames--;
            if (horizon > _softCap) TimeSyncStalls++; else CostStalls++;
            IsStalled = true;
            LastStallWasTimeSync = true;
            return FrameDecision.StallDecision;
        }

        // 2c) Measured-advantage stall. We know from the pacing exchange that we are genuinely
        //     running ahead of the peer, so hand a frame back. Unlike 2b this doesn't wait for the
        //     horizon to grow — it corrects the skew before it turns into rollback depth at all.
        if (_advantageStallFrames > 0)
        {
            _advantageStallFrames--;
            TimeSyncStalls++;
            IsStalled = true;
            LastStallWasTimeSync = true;
            return FrameDecision.StallDecision;
        }
        IsStalled = false;

        // 3) Snapshot the state entering this frame so a future correction can return here — unless
        //    this frame provably cannot BE a correction target (see ShouldAnchor).
        if (_savedFrame != frame)
        {
            AnchorOrDrop(frame);
            _savedFrame = frame;
        }

        // 4) Resolve inputs (real where confirmed, repeat-last prediction otherwise) and run.
        var inputs = ResolveInputs(frame);
        _applied[frame] = inputs;
        if (frame > _lastRunFrame) _lastRunFrame = frame;
        return FrameDecision.Run(inputs);
    }

    public void EndFrame(int frame)
    {
        Prune(frame);
    }

    /// <summary>
    /// Produce an integrity checksum both peers can compare, for desync detection under rollback.
    /// Naively hashing the current frame is wrong here — it may be a prediction that legitimately
    /// differs between peers. Instead this returns the newest interval boundary that is <em>final</em>
    /// (every port's real input has arrived through it, so both peers computed it identically) and
    /// the hash of the state right after it. Quantizing to interval boundaries is what keeps the two
    /// peers comparing the <em>same</em> frame despite running at slightly different confirmed
    /// frontiers. Returns false when no new boundary is final yet.
    ///
    /// The hash itself is normally taken back when the anchor was saved, because the core was
    /// already standing on exactly this state at that moment. Fetching it here instead costs a
    /// save, a load, the hash and a load back to return — measured on N64 as 18.4ms against the
    /// 7.2ms the hash alone costs, a hitch lockstep never pays for the same information. That path
    /// remains as the fallback for the case the cache cannot cover: a repair that re-simulated the
    /// anchor frame invalidates the cached hash, since the state it described no longer exists.
    /// </summary>
    /// <summary>
    /// Put the core back on the live frame and release the pin that held it.
    ///
    /// The release happens either way — a failed restore is fatal to the session, but leaking the
    /// pooled state on the way out helps nobody. The failure is latched so the next entry point
    /// fails immediately rather than doing more work against a core standing on the wrong frame.
    /// </summary>
    private void RestoreLivePosition(StateHandle here)
    {
        Exception? restoreError = null;
        try { _adapter.LoadStateFromMemory(here); }
        catch (Exception ex) { restoreError = ex; _restoreFailed = true; }
        _adapter.ReleaseState(here);
        if (restoreError != null)
            throw new StateRestoreFailedException(
                "the core rejected the savestate that would have returned it to the live frame",
                restoreError);
    }

    /// <summary>Throw if a previous restore already stranded the core off its live frame.</summary>
    private void ThrowIfCoreStranded()
    {
        if (_restoreFailed)
            throw new StateRestoreFailedException(
                "the core was left on the wrong frame by an earlier failed restore");
    }

    public bool TryConfirmedChecksum(int interval, out int frame, out uint hash) =>
        TryConfirmedChecksumCore(interval, null, out frame, out hash, out _);

    /// <summary>
    /// As <see cref="TryConfirmedChecksum"/>, and additionally fill per-bucket hashes of the same
    /// boundary state for divergence learning. This deliberately FORCES the save/load/hash/restore
    /// visit — the cached anchor hash cannot help, since the buckets must come off the very state
    /// the hash describes — so it costs the pre-anchor hitch (~18ms on N64) and is only worth
    /// calling during the few learn boundaries of a generation, where a rebuild hitch just
    /// happened anyway. <paramref name="bucketsFilled"/> is false on a core whose domain cannot
    /// be read whole; the hash is valid regardless.
    /// </summary>
    public bool TryConfirmedChecksumWithBuckets(int interval, uint[] bucketSink,
        out int frame, out uint hash, out bool bucketsFilled)
    {
        if (bucketSink == null) throw new ArgumentNullException(nameof(bucketSink));
        return TryConfirmedChecksumCore(interval, bucketSink, out frame, out hash, out bucketsFilled);
    }

    private bool TryConfirmedChecksumCore(int interval, uint[]? bucketSink,
        out int frame, out uint hash, out bool bucketsFilled)
    {
        frame = 0;
        hash = 0;
        bucketsFilled = false;
        ThrowIfCoreStranded();
        if (interval < 1) return false;

        // Highest frame we've both simulated and confirmed from real inputs only.
        int confirmed = Math.Min(_lastRunFrame, _pipeline.MinFrontier());
        if (confirmed < 0) return false;

        int boundary = (confirmed / interval) * interval;
        if (boundary <= _lastChecksumFrame) return false;   // already reported (cadence + dedupe)

        if (bucketSink == null && _anchorHashFrame == boundary)
        {
            hash = _anchorHash;
            _anchorHashFrame = -1;
            ChecksumsFromAnchor++;
            frame = boundary;
            _lastChecksumFrame = boundary;
            return true;
        }

        int postFrame = boundary + 1;                        // entering postFrame == state right after `boundary`
        if (!_states.TryGetValue(postFrame, out var st)) return false; // just outside the ring; try again later

        var here = _adapter.SaveStateToMemory();             // pin the live position (not in the ring)
        try
        {
            _adapter.LoadStateFromMemory(st);
            if (bucketSink != null)
                bucketsFilled = _adapter.TryHashMainMemoryBuckets(boundary, bucketSink, out hash);
            else
                hash = _adapter.HashMainMemory(boundary);
        }
        finally
        {
            // Deliberately allowed to replace an in-flight exception: whatever went wrong above,
            // being unable to get back to the live frame is worse, and it is the fault the player
            // has to be told about. Release first so a failed restore does not also leak the pin.
            RestoreLivePosition(here);
        }
        if (_anchorHashFrame == boundary) _anchorHashFrame = -1; // the visit consumed this boundary
        ChecksumsByVisit++;
        frame = boundary;
        _lastChecksumFrame = boundary;
        return true;
    }

    public void OnRemoteInput(int frame, int port)
    {
        int f = frame;
        // Only already-simulated frames can be mispredicted; a not-yet-run frame will simply use
        // the real input when we reach it. (_applied holds exactly the frames we've run and kept.)
        if (!_applied.TryGetValue(f, out var applied)) return;

        if (port < 0 || port >= _portCount) return;

        // The pipeline was updated with the real input just before this call; compare it against
        // what we actually ran. Equal (a correct prediction, or plain redundancy) → nothing to do.
        if (!_pipeline.TryGet(port, f, out var real) || real == null) return;
        if (!real.ValueEquals(applied.Ports[port]) && f < _rollbackTo)
            _rollbackTo = f; // roll back to the earliest contradicted frame
    }

    public void OnPacingReport(PacingInfo info)
    {
        // Turn the measured round-trip into a target prediction horizon: about the one-way latency
        // (RTT/2) in frames, plus a small margin so ordinary jitter doesn't trip it. We allow the
        // horizon to grow to this much (hiding the real latency) but trim anything beyond it as
        // clock skew. Floored so a near-zero-latency link still tolerates a couple of frames, and
        // never above the hard ring cap. No-op when time-sync is disabled (frameMs <= 0).
        if (_frameMs <= 0) return;
        int latencyFrames = (int)Math.Ceiling((info.RoundTripMs / 2.0) / _frameMs);
        int cap = latencyFrames + SoftMargin;
        if (cap < MinSoftCap) cap = MinSoftCap;
        if (cap > _maxRollback) cap = _maxRollback;
        _softCap = cap;

        // Round-trip time is symmetric: it says how far apart the peers are, never which one is
        // ahead — so a cap derived from it throttles whoever happens to trip it, not whoever is
        // actually running fast. A measured frame advantage is signed and says exactly that, so
        // when we have one, the peer that is genuinely ahead gives the surplus back itself.
        bool freshAdvantage = !info.HasSampleSequence || info.SampleSequence != _lastAdvantageSequence;
        if (freshAdvantage)
        {
            if (info.HasSampleSequence) _lastAdvantageSequence = info.SampleSequence;
            if (info.HasFrameAdvantage && info.FrameAdvantage >= AdvantageStallThreshold)
            {
                // Yield about half the surplus per new report, capped: the other side is closing
                // the gap too, so giving all of it back would overshoot into a tug-of-war.
                _advantageStallFrames = Math.Min(info.FrameAdvantage / 2, MaxAdvantageStall);
            }
            else
            {
                // A newer measurement supersedes any unspent debt from an older one.
                _advantageStallFrames = 0;
            }
        }
    }

    // --- internals ----------------------------------------------------------------

    private void ExecutePendingRollback(int frame)
    {
        ThrowIfCoreStranded();
        if (_rollbackTo == int.MaxValue) return;
        int r = _rollbackTo;
        _rollbackTo = int.MaxValue;
        if (r >= frame) return; // nothing simulated beyond it to correct

        // The target itself is only guaranteed to be snapshotted when every predicted frame is.
        // With sparse keyframes the repair restarts from the newest one at or before it and
        // re-simulates the extra frames back up to it: correct because ResolveInputs recomputes
        // each frame from the pipeline, so replaying from further back lands in the same place.
        int b = NearestBaseAtOrBefore(r);
        if (b < 0 || !_states.TryGetValue(b, out var baseState))
            throw new InvalidOperationException(
                $"Rollback target frame {r} has no base in the ring within {_keyframeInterval} frame(s) " +
                $"(depth {frame - r} > cap {_maxRollback}); the prediction cap and the prune window " +
                "should together have prevented running this far ahead.");

        // Unlike the checksum visit above there is no pin to put back: the base state IS where the
        // repair means to stand. But a core that refuses it is stranded just the same, so the same
        // named failure applies rather than a generic session error.
        try { _adapter.LoadStateFromMemory(baseState); } // core now sits entering frame b
        catch (Exception ex)
        {
            _restoreFailed = true;
            throw new StateRestoreFailedException(
                $"the core rejected the savestate for frame {b}, which a rollback repair had to " +
                "replay from", ex);
        }
        int walkback = r - b;
        int count = frame - b;                   // re-simulate b .. frame-1
        double startedMs = _clock?.NowMs ?? 0;
        double hashBeforeMs = _pendingHashMs;
        _adapter.RunFramesInvisible(count, i =>
        {
            int f = b + i;
            // Re-snapshot the entering state (it may be corrected again later) on the same terms as
            // the live path. Frames the arriving correction just confirmed are dropped rather than
            // re-saved: any snapshot from the previous run of f is now stale, and nothing may find it.
            AnchorOrDrop(f, duringRepair: true);
            var inputs = ResolveInputs(f);
            _applied[f] = inputs;
            return inputs;
        });
        // Core is back at `frame`; the snapshot previously taken for it is now stale.
        _savedFrame = -1;
        // Any anchor re-hashed above is checksum cost, not repair cost — the caller drains it
        // separately through TakeHashCostMs. Charging it here would inflate the per-frame estimate
        // that governs prediction depth, which is the reason this used to invalidate the cached
        // hash instead of replacing it.
        if (_clock != null && count > 0)
            RecordRepairCost((_clock.NowMs - startedMs - (_pendingHashMs - hashBeforeMs)) / count);

        RollbackCount++;
        // Depth is how far the correction reached back; the walkback to the nearest keyframe is
        // extra frames replayed to GET there, and is reported separately below. Both were `count`,
        // which is their sum, so the status line rendered a true depth-3 correction with one
        // walkback frame as "d4+1wb" — the walkback counted once in the depth and again beside it.
        int correctionDepth = frame - r;
        LastRollbackDepth = correctionDepth;
        LastReplayedFrames = count;
        if (correctionDepth > MaxRollbackDepthSeen) MaxRollbackDepthSeen = correctionDepth;
        FramesResimulated += count;
        // Reported apart from the depth, because it is the price of sparse keyframes and the thing
        // to look at if a repair costs more than the depth alone says it should.
        LastRollbackWalkback = walkback;
        FramesWalkedBack += walkback;
    }

    private InputSet ResolveInputs(int frame)
    {
        var ports = new PortInput[_portCount];
        for (int p = 0; p < _portCount; p++)
        {
            if (_pipeline.TryGet(p, frame, out var real) && real != null)
                ports[p] = real;
            else
                ports[p] = Predict(p);
        }
        return new InputSet(frame, ports);
    }

    /// <summary>Repeat the port's most recent confirmed input (classic GGPO prediction); neutral if none.</summary>
    private PortInput Predict(int port)
    {
        int fr = _pipeline.ConfirmedFrontier(port);
        if (fr >= 0 && _pipeline.TryGet(port, fr, out var last) && last != null)
            return last;
        return _neutral[port];
    }

    /// <summary>
    /// Whether <paramref name="frame"/> would be run rather than stalled — the same three gates
    /// <see cref="BeginFrame"/> applies (hard ring cap, time-sync and cost caps, outstanding
    /// measured-advantage debt), asked without side effects.
    ///
    /// This exists for the catch-up burst, which repays wall-clock debt by stepping a second,
    /// unrendered frame in one tick. That burst used to require the next frame's every port to be
    /// already CONFIRMED — a condition that is false in rollback's steady state whenever input
    /// delay is below the link's latency, which is the normal setting. So the sessions that hitch
    /// most (a heavy core's repairs and anchor ticks) were exactly the ones that could never catch
    /// up afterwards, and the debt was eventually discarded in a lump by the schedule rebase — the
    /// judder the pacing telemetry reports as "rebases".
    ///
    /// Running a predicted frame is what rollback is FOR, and predicting one frame further is
    /// bounded by the very caps asked about here. A wrong answer costs nothing new either: the
    /// burst is already written to tolerate a second frame that never runs.
    /// </summary>
    public bool CanRunWithoutStalling(int frame)
    {
        if (_restoreFailed) return false;
        if (_advantageStallFrames > 0) return false;
        int horizon = RemoteHorizon(frame);
        return horizon <= _maxRollback && horizon <= _softCap && horizon <= _costCap;
    }

    /// <summary>Frames we'd be predicting for the least-advanced remote port if we run <paramref name="frame"/>.</summary>
    private int RemoteHorizon(int frame)
    {
        int minRemote = int.MaxValue;
        for (int p = 0; p < _portCount; p++)
        {
            if (p == _localPort) continue;
            int fr = _pipeline.ConfirmedFrontier(p);
            if (fr < minRemote) minRemote = fr;
        }
        if (minRemote == int.MaxValue) return 0; // no remote ports (solo)
        return frame - 1 - minRemote;
    }

    /// <summary>
    /// Snapshot the state entering <paramref name="frame"/>, replacing any snapshot already held
    /// for it — which a repair does routinely, since it re-snapshots every frame it replays.
    ///
    /// <b>The entry is dropped BEFORE the old state is released, and that ordering is the whole
    /// point.</b> Releasing hands the buffer back to the adapter's pool for the next save to refill.
    /// If the save below then throws — a core that cannot export, memory it cannot get — the
    /// dictionary would be left pointing at a handle already sitting in that pool, and the next
    /// successful save pops it and fills it with a DIFFERENT frame's bytes. A later repair choosing
    /// this frame as its base would restore that, putting the core somewhere nobody asked for, with
    /// nothing raised and nothing logged.
    ///
    /// Dropping the key first makes the failure "no snapshot for this frame", which every reader
    /// already handles: <see cref="NearestBaseAtOrBefore"/> walks back for an older base, and
    /// failing that <see cref="ExecutePendingRollback"/> throws by name. Costs one dictionary
    /// removal, and nothing at all in the pool — the buffer is still released, just afterwards.
    /// </summary>
    private void SaveStateFor(int frame)
    {
        if (_states.TryGetValue(frame, out var old))
        {
            _states.Remove(frame);
            _adapter.ReleaseState(old);
        }
        _states[frame] = _adapter.SaveStateToMemory();
        SavesTaken++;
    }

    /// <summary>
    /// Whether the state entering <paramref name="frame"/> is worth keeping.
    ///
    /// A frame whose every port was already confirmed when it ran can never become a rollback
    /// target, so its state is dead weight. The argument is short and rests entirely on code that
    /// already exists:
    ///
    ///   * <c>ResolveInputs</c> takes the real input for every port that has one, so a frame run
    ///     while <c>AllConfirmed</c> holds applied real input on every port — nothing predicted.
    ///   * <c>OnRemoteInput</c> only ever sets <c>_rollbackTo</c> when an arriving input DIFFERS
    ///     from what was applied. It cannot differ from itself.
    ///   * A stored (port, frame) input is never overwritten — <c>InputPipeline.Add</c> ignores a
    ///     repeat, and <c>FrameDriver</c> drops a datagram it already holds before this is reached.
    ///   * <c>AllConfirmed</c> reads the contiguous frontier, which only ever advances, so the
    ///     property is monotonic: once true for a frame it stays true for the rest of the session.
    ///
    /// So the elided frames are exactly the ones no correction can reach. Frames still carrying a
    /// prediction are anchored as before, which is what keeps repair possible at all.
    ///
    /// The one exception is the checksum: it reads the state entering an interval boundary + 1,
    /// which is by construction deep inside the confirmed region and would otherwise be the first
    /// thing dropped. Those frames are anchored unconditionally — without that, desync detection
    /// goes quiet and nothing says so.
    /// </summary>
    /// ...and with <see cref="RollbackTuning.KeyframeInterval"/> above 1, a frame that still carries
    /// a prediction is only snapshotted when nothing within reach already is. A repair then starts
    /// from the newest keyframe at or before its target — see <see cref="NearestBaseAtOrBefore"/>.
    private bool ShouldAnchor(int frame)
    {
        if (IsChecksumAnchor(frame)) return true;
        if (_elideConfirmedSaves && _pipeline.AllConfirmed(frame)) return false;
        return !HasBaseWithinReach(frame);
    }

    /// <summary>
    /// Is there already a snapshot a repair could start from, close enough behind
    /// <paramref name="frame"/> to be worth walking back to?
    ///
    /// Stated as a reachability question against the live ring rather than as
    /// <c>frame % interval == 0</c>, because the modulo version is quietly wrong. Frames are only
    /// considered for anchoring while they carry a prediction, so a run of confirmed frames leaves
    /// gaps in the sequence; a modulo rule can then skip its own keyframe and leave a later
    /// predicted frame with nothing behind it to roll back to. Asking the ring keeps the invariant
    /// true by construction: every predicted frame either has a base within N-1 frames, or becomes
    /// one itself. The scan is at most N-1 dictionary probes, with N small by design.
    /// </summary>
    private bool HasBaseWithinReach(int frame)
    {
        for (int b = frame - 1; b >= 0 && frame - b < _keyframeInterval; b--)
            if (_states.ContainsKey(b)) return true;
        return false;
    }

    /// <summary>
    /// The frame a repair aiming at <paramref name="target"/> must actually load and restart from:
    /// the newest snapshot at or before it. Returns -1 if the ring holds nothing in reach, which is
    /// a bug in the caps or the prune window rather than a condition to recover from.
    /// </summary>
    private int NearestBaseAtOrBefore(int target)
    {
        for (int b = target; b >= 0 && target - b < _keyframeInterval; b--)
            if (_states.ContainsKey(b)) return b;
        return -1;
    }

    private bool IsChecksumAnchor(int frame) =>
        _checksumAnchorInterval > 0 && frame >= 1 && (frame - 1) % _checksumAnchorInterval == 0;

    /// <summary>
    /// An anchor whose boundary has not been checksummed yet, and which therefore outranks the
    /// prune window.
    ///
    /// Insurance, not a fix for a live bug: at present the arithmetic already works out. The
    /// confirmed frontier can only lag the live frame by the prediction cap, and the prune window
    /// is the ring depth plus a margin, so an anchor is still resident when its boundary becomes
    /// checksummable — verified by removing this clause and watching the shallow-ring test still
    /// pass. What it buys is that the property stops depending on that coincidence surviving every
    /// future change to ring depth, prune margin or cap. The cost is one extra state, and the
    /// failure it guards against is silent — checksums merely stop being produced.
    /// </summary>
    private bool IsUnconsumedChecksumAnchor(int frame) =>
        IsChecksumAnchor(frame) && frame - 1 > _lastChecksumFrame;

    /// <summary>
    /// Take the snapshot, or make sure no stale one survives in its place.
    ///
    /// A checksum anchor gets hashed here, live or during a repair. Standing on the state is the
    /// whole cost of checksumming it — the alternative is to come back later and save, load, hash
    /// and load again for the same bytes, measured on N64 at 18.4ms against the 7.2ms the hash
    /// alone costs.
    ///
    /// A repair used to INVALIDATE the cached hash instead of replacing it, so every correction
    /// that reached across a checksum boundary bought that 18.4ms hitch — and did so precisely on
    /// the high-latency links where corrections are deepest and most frequent. The stated reason
    /// was that hashing inside the timed re-simulation would inflate the per-frame repair cost
    /// governing prediction depth; that is what <see cref="_pendingHashMs"/> already solves for
    /// the live path, and <see cref="ExecutePendingRollback"/> now discounts it the same way.
    /// </summary>
    private void AnchorOrDrop(int frame, bool duringRepair = false)
    {
        if (ShouldAnchor(frame))
        {
            SaveStateFor(frame);
            if (!IsChecksumAnchor(frame)) return;
            double startedMs = _clock?.NowMs ?? 0;
            _anchorHash = _adapter.HashMainMemory(frame - 1);
            if (_clock != null) _pendingHashMs += _clock.NowMs - startedMs;
            _anchorHashFrame = frame - 1;
            return;
        }
        if (_states.TryGetValue(frame, out var stale))
        {
            // Dropped before released, for the same reason as SaveStateFor: an entry must never
            // outlive the state it names, and the release is what ends that state's life.
            _states.Remove(frame);
            _adapter.ReleaseState(stale);
        }
        SavesElided++;
    }

    /// <summary>
    /// Fold a measured per-frame repair cost into the ceiling on how far we predict.
    ///
    /// The estimate decays toward the truth but jumps straight to any worse sample (the same
    /// conservative shape the tool uses for core frame cost), so one expensive repair immediately
    /// tightens the cap while a run of cheap ones only loosens it gradually.
    /// </summary>
    private void RecordRepairCost(double perFrameMs)
    {
        if (perFrameMs <= 0) return;
        _repairPerFrameMs = _repairPerFrameMs <= 0
            ? perFrameMs
            : Math.Max(perFrameMs, _repairPerFrameMs * 0.9);

        if (_repairBudgetMs <= 0) return;
        // The budget buys a number of re-simulated FRAMES; the cap governs prediction DEPTH. Sparse
        // keyframes drives a wedge between the two, since a repair at depth d re-simulates up to
        // d + N-1 frames. Charging the walk-back here is what stops the cheaper per-frame cost from
        // being read as more depth than the budget can actually pay for.
        int cap = (int)Math.Floor(_repairBudgetMs / _repairPerFrameMs) - (_keyframeInterval - 1);
        // Below the floor, rollback is hiding no latency and is paying for the privilege. The floor
        // is still applied — collapsing to zero would be lockstep with extra steps — but the fact
        // that it had to be applied is now recorded rather than silently absorbed. Forcing two
        // frames of repair after measuring that one does not fit means committing, every time, to
        // work this machine has already demonstrated it cannot finish inside the frame. The honest
        // answer is a higher input delay or lockstep, and only the session above can choose that.
        if (cap < MinCostCap)
        {
            cap = MinCostCap;
            if (!BudgetExceededByFloor)
            {
                BudgetExceededByFloor = true;
                FloorRepairPerFrameMs = _repairPerFrameMs;
            }
        }
        if (cap > _maxRollback) cap = _maxRollback;
        _costCap = cap;
    }

    /// <summary>
    /// Release every savestate still held in the ring. The strategy has no other reference to hand
    /// these back through, so without this a resync (which builds a fresh driver) or session end
    /// would strand a ring's worth of BizHawk state blobs — ~<see cref="MaxRollback"/> of them each
    /// time. Idempotent.
    /// </summary>
    public void Dispose()
    {
        foreach (var st in _states.Values) _adapter.ReleaseState(st);
        _states.Clear();
        _applied.Clear();
    }

    private void Prune(int frame)
    {
        // The keyframe term is not decoration: a correction landing at the very edge of the
        // prediction horizon restarts from a snapshot up to N-1 frames older still, and pruning to
        // the old window would throw that base away and turn a legal rollback into the exception in
        // ExecutePendingRollback. The applied inputs are held to the same line, since those frames
        // have to be re-resolved on the way back up.
        int keepFrom = frame + 1 - _maxRollback - PruneMargin - (_keyframeInterval - 1);
        if (keepFrom <= 0) return;

        _pruneScratch.Clear();
        foreach (var key in _states.Keys)
            if (key < keepFrom && !IsUnconsumedChecksumAnchor(key)) _pruneScratch.Add(key);
        foreach (var key in _pruneScratch)
        {
            _adapter.ReleaseState(_states[key]);
            _states.Remove(key);
        }

        // _applied is pruned on its own keys, not as a passenger of _states. It used to be dropped
        // alongside the matching state, which was only correct while every frame had one — with
        // elision most frames have an applied input and no state, and riding along would have left
        // them to accumulate for the whole session.
        _pruneScratch.Clear();
        foreach (var key in _applied.Keys)
            if (key < keepFrom) _pruneScratch.Add(key);
        foreach (var key in _pruneScratch)
            _applied.Remove(key);
    }
}
