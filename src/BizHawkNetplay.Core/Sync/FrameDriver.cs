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
                _adapter.SetPaused(true);
                IsStalled = true;
                return FrameStep.Stalled;
            }

            _adapter.SetPaused(false);
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
