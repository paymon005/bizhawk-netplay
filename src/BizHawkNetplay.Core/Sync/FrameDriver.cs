using System;
using System.Collections.Generic;
using System.Diagnostics;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Sync
{
    public enum FrameStep
    {
        /// <summary>Inputs were resolved and applied; the core may advance this frame.</summary>
        Ran,
        /// <summary>Inputs are not yet available; the core is paused and will retry next tick.</summary>
        Stalled,
    }

    /// <summary>
    /// Owns the per-frame sequence and is the only component that touches the strategy. Runs
    /// single-threaded on the UI thread. In EmuHawk, <see cref="OnPreFrame"/> is called from the
    /// PreFrame tool callback and <see cref="OnPostFrame"/> from PostFrame; a test harness drives
    /// the same two calls around a fake core advance. Strategy-agnostic: swap lockstep for
    /// rollback by passing a different factory — nothing else here changes.
    /// </summary>
    public sealed class FrameDriver : IDisposable
    {
        private readonly IEmuAdapter _adapter;
        private readonly ITransport _transport;
        private readonly InputPipeline _pipeline;
        private readonly ISyncStrategy _strategy;
        private readonly InputSerializer[] _serializers;
        private readonly InputPacketCodec _codec;

        private readonly int _localPort;
        private readonly int _delay;
        private readonly int _redundancy;
        private readonly int _historyKeep;
        private readonly int _rollbackWindow;
        private readonly int _maxLead;
        private FrameDecision _pendingDecision;

        // Keep receive work bounded on the UI thread. A duplicate/flooded UDP queue is allowed to
        // spill into a later timer callback instead of monopolizing one callback indefinitely.
        private const int MaxDatagramsPerPump = 128;
        private const long StallResendIntervalMs = 20; // at most 50 redundant windows/second
        private readonly Stopwatch _sendClock = Stopwatch.StartNew();
        private long _lastSendMs = long.MinValue;
        private readonly DateTime[] _lastRemoteInputUtc;
        private int _lastPacketsDrained;

        // Rolling window of the local port's most recent serialized inputs, for redundant sends.
        private readonly LinkedList<KeyValuePair<int, byte[]>> _sendWindow = new LinkedList<KeyValuePair<int, byte[]>>();
        private int _lastStamp = -1;
        private bool _started;

        /// <param name="strategyFactory">Builds the sync strategy over the shared pipeline (the swap point).</param>
        /// <param name="localPort">The controller port this instance owns and sources locally.</param>
        /// <param name="delay">Input delay D in frames (≥ 1); local input read at frame N applies at N+D.</param>
        /// <param name="redundancy">
        /// How many recent inputs each datagram repeats (R). Tolerates up to R-1 consecutive losses
        /// with no stall. Should be ≥ 2·delay+1: in lockstep the ahead peer can lead the behind peer
        /// by up to D frames, so the redundant window must reach back far enough to still cover the
        /// frame the behind peer needs. Below that, sustained loss can slide the window past a needed
        /// frame and stall until retransmission (an M2 feature) recovers it.
        /// </param>
        /// <param name="rollbackWindow">
        /// How many frames the sim may run ahead of a remote port's confirmed input before a stale
        /// correction can no longer arrive. 0 (the default) is pure lockstep: remote frames earlier
        /// than the current frame are never needed, so they're dropped. Rollback passes its ring
        /// depth here so late corrections for already-simulated frames reach the pipeline and the
        /// far-future/history bounds stretch to cover the prediction horizon.
        /// </param>
        public FrameDriver(
            IEmuAdapter adapter,
            ITransport transport,
            Func<InputPipeline, ISyncStrategy> strategyFactory,
            int localPort,
            int delay,
            int redundancy = 8,
            int rollbackWindow = 0,
            int portCount = 0)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            if (strategyFactory == null) throw new ArgumentNullException(nameof(strategyFactory));
            if (delay < 1) throw new ArgumentOutOfRangeException(nameof(delay), "Input delay must be >= 1");
            if (redundancy < 1) throw new ArgumentOutOfRangeException(nameof(redundancy));
            if (rollbackWindow < 0) throw new ArgumentOutOfRangeException(nameof(rollbackWindow));

            // The session may use fewer players than the core has controller ports (e.g. 2-player on an
            // N64's 4 ports). The driver then networks only the active ports; the core's remaining ports
            // are left unset by the controller (read as neutral). 0 = use every port the core exposes.
            int ports = portCount > 0 ? Math.Min(portCount, adapter.PortCount) : adapter.PortCount;
            if (localPort < 0 || localPort >= ports) throw new ArgumentOutOfRangeException(nameof(localPort));

            _localPort = localPort;
            _delay = delay;
            // The redundant send window must reach back far enough that the peer — which in lockstep can
            // lead us by up to the delay in either direction — still receives every frame it needs, i.e.
            // R >= 2·delay+1. If the caller asks for less (notably when delay exceeds the requested
            // redundancy), the earliest frames, including frame 0, are evicted from the window before the
            // first send and the peer stalls at frame 0 forever. Raise R to satisfy the bound.
            _redundancy = Math.Max(redundancy, 2 * delay + 1);
            _rollbackWindow = rollbackWindow;
            // Reject frames impossibly far in the future: at most the peer leads by the delay window
            // plus (in rollback) however far it may have predicted ahead of us.
            _maxLead = 2 * delay + _redundancy + 4 + rollbackWindow;

            _pipeline = new InputPipeline(ports);
            for (int p = 0; p < ports; p++) _pipeline.SetLocal(p, p == localPort);

            _serializers = new InputSerializer[ports];
            _lastRemoteInputUtc = new DateTime[ports];
            var payloadSizes = new int[ports];
            for (int p = 0; p < ports; p++)
            {
                _serializers[p] = new InputSerializer(adapter.GetControllerLayout(p));
                payloadSizes[p] = _serializers[p].PayloadSize;
            }
            _codec = new InputPacketCodec(payloadSizes);
            _strategy = strategyFactory(_pipeline);

            // Keep enough history to cover the delay pipeline, the redundancy window, and (for
            // rollback) the whole ring depth, so real inputs for any resimulated frame survive pruning.
            _historyKeep = _delay + _redundancy + 2 + _rollbackWindow;
        }

        public int CurrentFrame { get; private set; }
        public bool IsStalled { get; private set; }
        public InputSet? LastAppliedInputs { get; private set; }
        public ISyncStrategy Strategy => _strategy;
        public int LastPacketsDrained => _lastPacketsDrained;

        /// <summary>
        /// Seed the local port's first D frames (nothing has been stamped for them yet) with
        /// neutral input and publish them, so the peer can reach frame 0 without stalling.
        /// </summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            var neutral = PortInput.Neutral(_adapter.GetControllerLayout(_localPort));
            var now = DateTime.UtcNow;
            for (int p = 0; p < _lastRemoteInputUtc.Length; p++) _lastRemoteInputUtc[p] = now;
            for (int f = 0; f < _delay; f++)
                ProduceLocal(f, neutral);
            SendWindow(force: true);
        }

        public FrameStep OnPreFrame()
        {
            if (!_started) Start();

            DrainNetwork(MaxDatagramsPerPump);
            CaptureAndSendLocal();

            var decision = _strategy.BeginFrame(CurrentFrame);
            if (decision.Stall)
            {
                // Do NOT pause the emulator here: in EmuHawk, pausing stops the very callbacks
                // we'd need to un-stall. The host owns the frame clock (paused + DoFrameAdvance
                // per confirmed frame), so a stall simply means "don't advance this tick".
                IsStalled = true;
                return FrameStep.Stalled;
            }

            IsStalled = false;
            LastAppliedInputs = decision.Inputs;
            _adapter.SetInputs(decision.Inputs!);
            return FrameStep.Ran;
        }

        public void OnPostFrame()
        {
            _strategy.EndFrame(CurrentFrame);
            CurrentFrame++;
            int prune = CurrentFrame - _historyKeep;
            if (prune > 0) _pipeline.PruneBefore(prune);
        }

        // --- Split API for the EmuHawk driver ----------------------------------------
        // In EmuHawk the network pump + gate run on the frame timer, while the local-input
        // capture and injection must run inside the frame callback (the only place input is
        // actually polled and where joypad overrides stick). These decompose OnPreFrame so the
        // form can straddle that boundary; the loopback tests keep using OnPreFrame/OnPostFrame.

        /// <summary>Drain a bounded number of remote input datagrams into the pipeline. Sending is kept
        /// separate so one captured input produces one immediate packet rather than PumpNetwork and
        /// CaptureLocalInput duplicating the same window back-to-back.</summary>
        public void PumpNetwork()
        {
            if (!_started) Start();
            _lastPacketsDrained = DrainNetwork(MaxDatagramsPerPump);
        }

        /// <summary>During a stall, re-send the redundant local window on a wall-clock cadence. The
        /// window already carries loss recovery, so sending it on every 2ms UI tick only creates floods.</summary>
        public void ResendLocalInputIfDue()
        {
            if (!_started) Start();
            SendWindow(force: false);
        }

        /// <summary>
        /// Ask the strategy to decide the current frame. True when it can advance (lockstep: all
        /// ports confirmed; rollback: essentially always, using prediction). The decision — including
        /// any predicted inputs — is cached for <see cref="CurrentInputs"/>.
        /// </summary>
        public bool CurrentFrameReady()
        {
            _pendingDecision = _strategy.BeginFrame(CurrentFrame);
            IsStalled = _pendingDecision.Stall;
            return !_pendingDecision.Stall;
        }

        /// <summary>
        /// Capture the local pad for CurrentFrame+delay, enqueue and send it. Call from inside the
        /// frame callback, where input has actually been polled.
        /// </summary>
        public void CaptureLocalInput()
        {
            int stamp = CurrentFrame + _delay;
            if (stamp > _lastStamp)
            {
                var local = _adapter.ReadLocalInput(_localPort);
                ProduceLocal(stamp, local);
                SendWindow(force: true);
            }
        }

        /// <summary>Find the remote controller port whose valid input has been silent longest.</summary>
        public bool TryGetMostSilentRemotePort(out int port, out TimeSpan silence)
        {
            port = -1;
            silence = TimeSpan.Zero;
            var now = DateTime.UtcNow;
            for (int p = 0; p < _lastRemoteInputUtc.Length; p++)
            {
                if (p == _localPort || _lastRemoteInputUtc[p] == default) continue;
                var age = now - _lastRemoteInputUtc[p];
                if (port < 0 || age > silence) { port = p; silence = age; }
            }
            return port >= 0;
        }

        /// <summary>
        /// Inputs to apply for the current frame (valid once <see cref="CurrentFrameReady"/> returned
        /// true). Returns the strategy's decided set — which for rollback includes predicted inputs for
        /// unconfirmed ports; for lockstep this is exactly <c>Merge(CurrentFrame)</c>. Falls back to a
        /// merge if no decision was cached (defensive).
        /// </summary>
        public InputSet CurrentInputs() => _pendingDecision.Inputs ?? _pipeline.Merge(CurrentFrame);

        /// <summary>Advance the frame counter after the core has stepped the current frame.</summary>
        public void CompleteFrame()
        {
            _strategy.EndFrame(CurrentFrame);
            CurrentFrame++;
            int prune = CurrentFrame - _historyKeep;
            if (prune > 0) _pipeline.PruneBefore(prune);
        }

        // --- internals ----------------------------------------------------------------

        private int DrainNetwork(int maxDatagrams)
        {
            int drained = 0;
            while (drained < maxDatagrams && _transport.TryReceive(out var datagram))
            {
                drained++;
                if (!_codec.TryDecodeInput(datagram, out var frames)) continue;
                foreach (var inFrame in frames)
                {
                    if (inFrame.Port == _localPort) continue;      // never let the wire override our own port
                    // Any well-decoded frame from a remote port proves that port's UDP path is alive,
                    // including a redundant frame we already hold during a lockstep stall.
                    _lastRemoteInputUtc[inFrame.Port] = DateTime.UtcNow;
                    // Genuine redundancy: we already hold this exact (port, frame). This is also the
                    // ONLY way an earlier-than-current frame reaches lockstep — it can't have advanced
                    // past a frame it lacked — so with _rollbackWindow==0 this matches the old
                    // "< CurrentFrame" drop exactly. In rollback, a late correction for an
                    // already-simulated frame is NOT yet in the pipeline, so it falls through below.
                    if (_pipeline.TryGet(inFrame.Port, inFrame.Frame, out _)) continue;
                    // Too old to act on: before the rollback ring / pruned history can reach. For
                    // lockstep (window 0) that is anything strictly before the current frame.
                    if (inFrame.Frame < CurrentFrame - _rollbackWindow) continue;
                    // Impossibly far in the future — bogus, or a pre-resync datagram (high frame number)
                    // arriving after we rebuilt at frame 0. Left unchecked it would sit in the pipeline
                    // and reapply thousands of frames later.
                    if (inFrame.Frame > CurrentFrame + _maxLead) continue;
                    var input = _serializers[inFrame.Port].Deserialize(inFrame.Payload);
                    _pipeline.Add(inFrame.Port, inFrame.Frame, input);
                    _strategy.OnRemoteInput(inFrame);
                }
            }
            return drained;
        }

        private void CaptureAndSendLocal()
        {
            int stamp = CurrentFrame + _delay;
            if (stamp > _lastStamp) // produce each stamp exactly once, even across stall retries
            {
                var local = _adapter.ReadLocalInput(_localPort);
                ProduceLocal(stamp, local);
                SendWindow(force: true);
            }
            else SendWindow(force: false);
        }

        private void ProduceLocal(int frame, PortInput input)
        {
            _pipeline.Add(_localPort, frame, input);
            var payload = _serializers[_localPort].Serialize(input);
            _sendWindow.AddLast(new KeyValuePair<int, byte[]>(frame, payload));
            while (_sendWindow.Count > _redundancy) _sendWindow.RemoveFirst();
            _lastStamp = frame;
        }

        private void SendWindow(bool force)
        {
            if (_sendWindow.Count == 0) return;
            long now = _sendClock.ElapsedMilliseconds;
            if (!force && now - _lastSendMs < StallResendIntervalMs) return;
            var window = new List<KeyValuePair<int, byte[]>>(_sendWindow);
            _transport.Send(_codec.EncodeInput((byte)_localPort, window));
            _lastSendMs = now;
        }

        /// <summary>Release the strategy's resources (e.g. rollback's savestate ring). Call when replacing
        /// the driver on a resync or tearing the session down, so BizHawk state blobs don't accumulate.</summary>
        public void Dispose() => (_strategy as IDisposable)?.Dispose();
    }
}
