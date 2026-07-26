using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;

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

        private readonly InputPipeline _pipeline;
        private readonly IEmuAdapter _adapter;
        private readonly int _portCount;
        private readonly int _localPort;
        private readonly int _maxRollback;
        private readonly double _frameMs;   // console frame period; 0 disables time-sync
        private readonly PortInput[] _neutral;
        private int _softCap;               // horizon at which time-sync trims (<= _maxRollback)

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

        /// <param name="frameMs">
        /// Console frame period, used to turn a measured round-trip time (via <see cref="OnPacingReport"/>)
        /// into a target prediction horizon for time-sync. 0 (the default) disables time-sync entirely —
        /// the strategy then only ever stalls at the hard ring cap.
        /// </param>
        public RollbackStrategy(InputPipeline pipeline, IEmuAdapter adapter, int localPort, int maxRollback, double frameMs = 0)
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
        public int SoftCap => _softCap;
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
            if (horizon > _softCap)
            {
                // A soft-cap stall already gives the faster peer one frame back. If measured-advantage
                // debt is outstanding, pay it here too so the two time-sync mechanisms do not charge
                // twice for the same skew.
                if (_advantageStallFrames > 0) _advantageStallFrames--;
                TimeSyncStalls++;
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

            // 3) Snapshot the state entering this frame so a future correction can return here.
            if (_savedFrame != frame)
            {
                SaveStateFor(frame);
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
        /// frontiers. The final state is visited via a temporary save/restore, leaving the live position
        /// and the ring untouched. Returns false when no new boundary is final yet.
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

            int postFrame = boundary + 1;                        // entering postFrame == state right after `boundary`
            if (!_states.TryGetValue(postFrame, out var st)) return false; // just outside the ring; try again later

            var here = _adapter.SaveStateToMemory();             // pin the live position (not in the ring)
            try
            {
                _adapter.LoadStateFromMemory(st);
                hash = _adapter.HashMainMemory();
            }
            finally
            {
                _adapter.LoadStateFromMemory(here);
                _adapter.ReleaseState(here);
            }
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
            _adapter.RunFramesInvisible(count, i =>
            {
                int f = r + i;
                SaveStateFor(f);           // re-snapshot entering-state (it may be corrected again later)
                var inputs = ResolveInputs(f);
                _applied[f] = inputs;
                return inputs;
            });
            // Core is back at `frame`; the snapshot previously taken for it is now stale.
            _savedFrame = -1;

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
                if (key < keepFrom) _pruneScratch.Add(key);
            foreach (var key in _pruneScratch)
            {
                _adapter.ReleaseState(_states[key]);
                _states.Remove(key);
                _applied.Remove(key);
            }
        }
    }
}
