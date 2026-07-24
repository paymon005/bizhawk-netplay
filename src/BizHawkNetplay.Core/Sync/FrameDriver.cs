using System;
using System.Collections.Generic;
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
    public sealed class FrameDriver
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
        public FrameDriver(
            IEmuAdapter adapter,
            ITransport transport,
            Func<InputPipeline, ISyncStrategy> strategyFactory,
            int localPort,
            int delay,
            int redundancy = 8)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            if (strategyFactory == null) throw new ArgumentNullException(nameof(strategyFactory));
            if (delay < 1) throw new ArgumentOutOfRangeException(nameof(delay), "Input delay must be >= 1");
            if (redundancy < 1) throw new ArgumentOutOfRangeException(nameof(redundancy));

            int ports = adapter.PortCount;
            if (localPort < 0 || localPort >= ports) throw new ArgumentOutOfRangeException(nameof(localPort));

            _localPort = localPort;
            _delay = delay;
            _redundancy = redundancy;

            _pipeline = new InputPipeline(ports);
            for (int p = 0; p < ports; p++) _pipeline.SetLocal(p, p == localPort);

            _serializers = new InputSerializer[ports];
            var payloadSizes = new int[ports];
            for (int p = 0; p < ports; p++)
            {
                _serializers[p] = new InputSerializer(adapter.GetControllerLayout(p));
                payloadSizes[p] = _serializers[p].PayloadSize;
            }
            _codec = new InputPacketCodec(payloadSizes);
            _strategy = strategyFactory(_pipeline);

            // Keep enough history to cover the delay pipeline and the redundancy window.
            _historyKeep = _delay + _redundancy + 2;
        }

        public int CurrentFrame { get; private set; }
        public bool IsStalled { get; private set; }
        public InputSet? LastAppliedInputs { get; private set; }
        public ISyncStrategy Strategy => _strategy;

        /// <summary>
        /// Seed the local port's first D frames (nothing has been stamped for them yet) with
        /// neutral input and publish them, so the peer can reach frame 0 without stalling.
        /// </summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            var neutral = PortInput.Neutral(_adapter.GetControllerLayout(_localPort));
            for (int f = 0; f < _delay; f++)
                ProduceLocal(f, neutral);
            SendWindow();
        }

        public FrameStep OnPreFrame()
        {
            if (!_started) Start();

            DrainNetwork();
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

        /// <summary>Drain remote inputs into the pipeline and (re)send our redundant window. Safe every tick, including stalls.</summary>
        public void PumpNetwork()
        {
            if (!_started) Start();
            DrainNetwork();
            SendWindow();
        }

        /// <summary>True when every port's input for the current frame is confirmed (ready to advance).</summary>
        public bool CurrentFrameReady()
        {
            var decision = _strategy.BeginFrame(CurrentFrame);
            IsStalled = decision.Stall;
            return !decision.Stall;
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
            }
            SendWindow();
        }

        /// <summary>Merged inputs to apply for the current frame (only valid once <see cref="CurrentFrameReady"/> is true).</summary>
        public InputSet CurrentInputs() => _pipeline.Merge(CurrentFrame);

        /// <summary>Advance the frame counter after the core has stepped the current frame.</summary>
        public void CompleteFrame()
        {
            _strategy.EndFrame(CurrentFrame);
            CurrentFrame++;
            int prune = CurrentFrame - _historyKeep;
            if (prune > 0) _pipeline.PruneBefore(prune);
        }

        // --- internals ----------------------------------------------------------------

        private void DrainNetwork()
        {
            while (_transport.TryReceive(out var datagram))
            {
                if (!_codec.TryDecodeInput(datagram, out var frames)) continue;
                foreach (var inFrame in frames)
                {
                    if (inFrame.Port == _localPort) continue;      // never let the wire override our own port
                    if (inFrame.Frame < CurrentFrame) continue;    // already consumed; ignore stale redundancy
                    // Reject frames impossibly far in the future. In lockstep the peer leads by at most
                    // the delay window, so anything beyond it is bogus or — the case that matters — a
                    // pre-resync datagram (high frame number) arriving after we rebuilt at frame 0. Left
                    // unchecked it would sit in the pipeline and reapply thousands of frames later.
                    if (inFrame.Frame > CurrentFrame + 2 * _delay + _redundancy + 4) continue;
                    var input = _serializers[inFrame.Port].Deserialize(inFrame.Payload);
                    _pipeline.Add(inFrame.Port, inFrame.Frame, input);
                    _strategy.OnRemoteInput(inFrame);
                }
            }
        }

        private void CaptureAndSendLocal()
        {
            int stamp = CurrentFrame + _delay;
            if (stamp > _lastStamp) // produce each stamp exactly once, even across stall retries
            {
                var local = _adapter.ReadLocalInput(_localPort);
                ProduceLocal(stamp, local);
            }
            SendWindow(); // resend the redundant window every tick, including stalls, for fast recovery
        }

        private void ProduceLocal(int frame, PortInput input)
        {
            _pipeline.Add(_localPort, frame, input);
            var payload = _serializers[_localPort].Serialize(input);
            _sendWindow.AddLast(new KeyValuePair<int, byte[]>(frame, payload));
            while (_sendWindow.Count > _redundancy) _sendWindow.RemoveFirst();
            _lastStamp = frame;
        }

        private void SendWindow()
        {
            if (_sendWindow.Count == 0) return;
            var window = new List<KeyValuePair<int, byte[]>>(_sendWindow);
            _transport.Send(_codec.EncodeInput((byte)_localPort, window));
        }
    }
}
