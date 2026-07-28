using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Core.Sync
{
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
        private readonly double _repairBudgetMs;
        private readonly IMonotonicClock? _clock;
        private double _repairPerFrameMs;   // conservative rolling estimate of resim cost
        private int _costCap;               // horizon the measured repair budget affords (<= _maxRollback)

        // state[N] = whole-core state captured entering frame N (i.e. the result of frames 0..N-1).
        private readonly Dictionary<int, StateHandle> _states = new Dictionary<int, StateHandle>();
        // applied[N] = the InputSet actually run for frame N (real where known, predicted otherwise).
        private readonly Dictionary<int, InputSet> _applied = new Dictionary<int, InputSet>();
        private readonly List<int> _pruneScratch = new List<int>();

        private int _advantageStallFrames;      // frames still owed back because we measured ourselves ahead
        private int _lastAdvantageSequence = -1; // makes periodic UI refreshes edge-triggered
        private int _rollbackTo = int.MaxValue; // earliest frame a late input contradicted (pending repair)
        private int _savedFrame = -1;           // frame whose entering-state is currently snapshotted
        private int _lastRunFrame = -1;         // highest frame actually simulated so far
        private int _lastChecksumFrame = -1;    // highest interval-boundary already checksummed (dedupe)

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
            _clock = t.Clock;
            _repairBudgetMs = _clock != null ? t.RepairBudgetMs : 0; // a budget with no clock is unmeasurable
            _costCap = maxRollback; // no trimming until a repair has actually been timed

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
        public int LastRollbackDepth { get; private set; }
        public int MaxRollbackDepthSeen { get; private set; }
        public long FramesResimulated { get; private set; }
        public int PredictionStalls { get; private set; }
        public int TimeSyncStalls { get; private set; }
        /// <summary>Frames yielded because the measured repair cost, not the clock skew, said to.</summary>
        public int CostStalls { get; private set; }
        public int SavesTaken { get; private set; }
        /// <summary>Snapshots skipped because the frame was already fully confirmed (see RollbackTuning).</summary>
        public int SavesElided { get; private set; }
        public int SoftCap => _softCap;
        /// <summary>Prediction horizon the measured repair budget currently affords.</summary>
        public int CostCap => _costCap;
        /// <summary>Conservative rolling estimate of what one re-simulated frame costs, in ms.</summary>
        public double RepairPerFrameMs => _repairPerFrameMs;
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
                PredictionStalls++;
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
        public bool TryConfirmedChecksum(int interval, out int frame, out uint hash)
        {
            frame = 0;
            hash = 0;
            if (interval < 1) return false;

            // Highest frame we've both simulated and confirmed from real inputs only.
            int confirmed = Math.Min(_lastRunFrame, _pipeline.MinFrontier());
            if (confirmed < 0) return false;

            int boundary = (confirmed / interval) * interval;
            if (boundary <= _lastChecksumFrame) return false;   // already reported (cadence + dedupe)

            if (_anchorHashFrame == boundary)
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
                hash = _adapter.HashMainMemory(boundary);
            }
            finally
            {
                _adapter.LoadStateFromMemory(here);
                _adapter.ReleaseState(here);
            }
            ChecksumsByVisit++;
            frame = boundary;
            _lastChecksumFrame = boundary;
            return true;
        }

        public void OnRemoteInput(InputFrame input)
        {
            int f = input.Frame;
            // Only already-simulated frames can be mispredicted; a not-yet-run frame will simply use
            // the real input when we reach it. (_applied holds exactly the frames we've run and kept.)
            if (!_applied.TryGetValue(f, out var applied)) return;

            int port = input.Port;
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
            if (_rollbackTo == int.MaxValue) return;
            int r = _rollbackTo;
            _rollbackTo = int.MaxValue;
            if (r >= frame) return; // nothing simulated beyond it to correct

            if (!_states.TryGetValue(r, out var baseState))
                throw new InvalidOperationException(
                    $"Rollback target frame {r} is not in the ring (depth {frame - r} > cap {_maxRollback}); " +
                    "the prediction cap should have prevented running this far ahead.");

            _adapter.LoadStateFromMemory(baseState); // core now sits entering frame r
            int count = frame - r;                   // re-simulate r .. frame-1
            double startedMs = _clock?.NowMs ?? 0;
            _adapter.RunFramesInvisible(count, i =>
            {
                int f = r + i;
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
            if (_clock != null && count > 0) RecordRepairCost((_clock.NowMs - startedMs) / count);

            RollbackCount++;
            LastRollbackDepth = count;
            if (count > MaxRollbackDepthSeen) MaxRollbackDepthSeen = count;
            FramesResimulated += count;
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

        private void SaveStateFor(int frame)
        {
            if (_states.TryGetValue(frame, out var old))
                _adapter.ReleaseState(old);
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
        private bool ShouldAnchor(int frame) =>
            !_elideConfirmedSaves
            || IsChecksumAnchor(frame)
            || !_pipeline.AllConfirmed(frame);

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
        /// A checksum anchor gets hashed here too, on the live path only. Standing on the state is the
        /// whole cost of checksumming it — the alternative is to come back later and save, load, hash
        /// and load again for the same bytes. Doing it during a repair instead would fold the hash into
        /// the timed re-simulation and inflate the per-frame repair cost that governs how far we
        /// predict, so a repair that rewrites an anchor invalidates the cached hash rather than
        /// replacing it, and the checksum falls back to fetching that one itself.
        /// </summary>
        private void AnchorOrDrop(int frame, bool duringRepair = false)
        {
            if (ShouldAnchor(frame))
            {
                SaveStateFor(frame);
                if (!IsChecksumAnchor(frame)) return;
                if (duringRepair)
                {
                    if (_anchorHashFrame == frame - 1) _anchorHashFrame = -1;
                    return;
                }
                double startedMs = _clock?.NowMs ?? 0;
                _anchorHash = _adapter.HashMainMemory(frame - 1);
                if (_clock != null) _pendingHashMs += _clock.NowMs - startedMs;
                _anchorHashFrame = frame - 1;
                return;
            }
            if (_states.TryGetValue(frame, out var stale))
            {
                _adapter.ReleaseState(stale);
                _states.Remove(frame);
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
            int cap = (int)Math.Floor(_repairBudgetMs / _repairPerFrameMs);
            if (cap < MinCostCap) cap = MinCostCap;
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
            int keepFrom = frame + 1 - _maxRollback - PruneMargin;
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
}
