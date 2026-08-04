using System;
using System.Collections.Generic;
using System.Diagnostics;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Sync;

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

    /// <summary>
    /// The wire codec, exposed so a session can report WHY input stopped arriving. "No UDP input
    /// from P3" and "P3's packets are arriving and being thrown away" are indistinguishable from
    /// the outside, need opposite fixes, and the second one used to leave no trace at all.
    /// </summary>
    public InputPacketCodec Codec => _codec;
    private readonly SessionGeneration _generation;

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
    // "Long ago", expressed so that `now - this` cannot overflow. long.MinValue looks like the
    // natural sentinel and is a trap: the clock starts near zero, so `now - long.MinValue` wraps
    // to a NEGATIVE number, every "is it due yet" test answers no, and the timer it guards never
    // fires again — see _serveWindowStartMs below, where that cost a permanent session freeze.
    private long _lastSendMs = -StallResendIntervalMs;
    private readonly long[] _lastRemoteInputStamp;
    private int _lastPacketsDrained;

    private int _lastStamp = -1;
    private bool _started;

    // Longer local-input history for answering gap requests: a frame that has aged out of the
    // redundant send window can still be re-sent from here. A few payload-bytes per frame — memory
    // is negligible.
    private const int RetransmitKeepFrames = 240;   // ~4 s at 60 fps
    private const long GapRequestIntervalMs = 50;   // per-port request cadence while a gap persists
    private const long GapServeWindowMs = 50;       // serve-side budget window (see ServeGapRequest)
    private const int GapServesPerWindow = 8;
    // Same overflow-safe sentinel as _lastSendMs, and here it was load-bearing: with long.MinValue
    // the window-reset test `now - _serveWindowStartMs >= GapServeWindowMs` overflowed negative and
    // never fired, so _servesThisWindow climbed to GapServesPerWindow and STAYED there — after the
    // eighth serve of a session this peer refused every gap request for the rest of it. A peer that
    // then lost a burst wider than its redundant window could never be re-sent the frames it was
    // missing: both sides sat at their prediction caps forever, asking and refusing, which is the
    // permanent freeze the gap-request path exists to prevent.
    private long _serveWindowStartMs = -GapServeWindowMs;
    private int _servesThisWindow;

    /// <summary>
    /// One contiguous ring of the local port's serialized inputs, indexed by frame.
    ///
    /// This replaced a LinkedList of (frame, byte[]) for the redundant window PLUS a Dictionary of
    /// those same arrays for the longer retransmit history. Between them, every frame allocated a
    /// payload array and a list node, and every datagram built from the window allocated a List and
    /// its backing array and walked the linked list to fill it — five objects and three passes over
    /// the window, sixty times a second, for bytes whose home was already decided. Serialization now
    /// writes straight into the slot the frame will be sent from, and building a datagram is one
    /// copy out of the ring.
    /// </summary>
    private readonly byte[] _sentRing;
    private readonly int _sentRingFrames;
    private readonly int _localPayloadSize;
    private int _oldestSentFrame = -1;   // -1 until the first local frame is produced
    private int _newestSentFrame = -1;
    private readonly long[] _lastGapRequestMs;
    private readonly int[] _newestRemoteFrame; // highest frame ever decoded per port (-1 = none)
    private readonly long[] _holeFirstSeenMs;  // send-clock ms when a beyond-window hole appeared (-1 = none)
    private readonly bool[] _vacated;          // port permanently empty; see VacatePort

    /// <param name="strategyFactory">Builds the sync strategy over the shared pipeline (the swap point).</param>
    /// <param name="localPort">The controller port this instance owns and sources locally.</param>
    /// <param name="delay">Input delay D in frames (≥ 1); local input read at frame N applies at N+D.</param>
    /// <param name="redundancy">
    /// How many recent inputs each datagram repeats (R). Tolerates up to R-1 consecutive losses
    /// with no stall. Should be ≥ 2·delay+1: in lockstep the ahead peer can lead the behind peer
    /// by up to D frames, so the redundant window must reach back far enough to still cover the
    /// frame the behind peer needs. Under rollback, a burst of ~R losses can still slide the
    /// window past a needed frame; the gap-request retransmit path then recovers it.
    /// </param>
    /// <param name="rollbackWindow">
    /// How many frames the sim may run ahead of a remote port's confirmed input before a stale
    /// correction can no longer arrive. 0 (the default) is pure lockstep: remote frames earlier
    /// than the current frame are never needed, so they're dropped. Rollback passes its ring
    /// depth here so late corrections for already-simulated frames reach the pipeline and the
    /// far-future/history bounds stretch to cover the prediction horizon.
    /// </param>
    /// <param name="generation">
    /// The negotiated session/timeline generation placed on every input datagram. Replace it
    /// with <see cref="SessionGeneration.Next"/> whenever a resync rebuilds the driver.
    /// </param>
    public FrameDriver(
        IEmuAdapter adapter,
        ITransport transport,
        Func<InputPipeline, ISyncStrategy> strategyFactory,
        int localPort,
        int delay,
        int redundancy = 8,
        int rollbackWindow = 0,
        int portCount = 0,
        SessionGeneration? generation = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (strategyFactory == null) throw new ArgumentNullException(nameof(strategyFactory));
        if (delay < 1) throw new ArgumentOutOfRangeException(nameof(delay), "Input delay must be >= 1");
        if (redundancy < 1) throw new ArgumentOutOfRangeException(nameof(redundancy));
        if (rollbackWindow < 0) throw new ArgumentOutOfRangeException(nameof(rollbackWindow));
        _generation = generation ?? SessionGeneration.Legacy;

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
        _lastRemoteInputStamp = new long[ports];
        _lastGapRequestMs = new long[ports];
        _newestRemoteFrame = new int[ports];
        _holeFirstSeenMs = new long[ports];
        _vacated = new bool[ports];
        for (int p = 0; p < ports; p++)
        {
            _lastGapRequestMs[p] = -GapRequestIntervalMs;
            _newestRemoteFrame[p] = -1;
            _holeFirstSeenMs[p] = -1;
        }
        var payloadSizes = new int[ports];
        for (int p = 0; p < ports; p++)
        {
            _serializers[p] = new InputSerializer(adapter.GetControllerLayout(p));
            payloadSizes[p] = _serializers[p].PayloadSize;
        }
        // Refuse a configuration whose datagrams would not survive the path, here, where it is still
        // a startup error with a number in it. Left unchecked the session starts, looks healthy,
        // and then reports "no UDP input from PN" forever — because every packet that port sends is
        // dropped for its size, on a network that is working perfectly.
        for (int p = 0; p < ports; p++)
        {
            if (payloadSizes[p] <= 0) continue; // no serializable layout; caught by the codec's counters
            int fits = InputPacketCodec.MaxFramesPerDatagram(payloadSizes[p]);
            if (_redundancy <= fits) continue;
            int allowed = InputPacketCodec.MaxInputDelayFor(payloadSizes[p]);
            throw new ArgumentOutOfRangeException(nameof(delay),
                $"Input delay {delay} needs a {_redundancy}-frame redundant window, and port {p}'s " +
                $"{payloadSizes[p]}-byte layout makes that a datagram larger than the " +
                $"{InputPacketCodec.MaxDatagramBytes}-byte limit. Use an input delay of {allowed} " +
                "or less for this controller configuration.");
        }
        _codec = new InputPacketCodec(payloadSizes, _generation);
        _strategy = strategyFactory(_pipeline);

        // Sized to hold the retransmit history, but never fewer frames than one datagram carries —
        // a tiny layout allows a redundant window wider than the history would otherwise keep, and
        // the window has to be readable in one contiguous run of slots.
        _localPayloadSize = payloadSizes[localPort];
        _sentRingFrames = Math.Max(RetransmitKeepFrames, _redundancy);
        _sentRing = new byte[_sentRingFrames * Math.Max(1, _localPayloadSize)];

        // Keep enough history to cover the delay pipeline, the redundancy window, and (for
        // rollback) the whole ring depth, so real inputs for any resimulated frame survive pruning.
        _historyKeep = _delay + _redundancy + 2 + _rollbackWindow;
    }

    public int CurrentFrame { get; private set; }
    public bool IsStalled { get; private set; }
    public InputSet? LastAppliedInputs { get; private set; }
    public ISyncStrategy Strategy => _strategy;
    public int LastPacketsDrained => _lastPacketsDrained;
    public SessionGeneration Generation => _generation;

    /// <summary>True when every port's real input for the frame after <see cref="CurrentFrame"/>
    /// is already buffered. A scheduler may combine this with strategy-specific pacing state before
    /// committing a two-frame presentation burst.</summary>
    public bool NextFrameFullyConfirmed => _pipeline.AllConfirmed(CurrentFrame + 1);

    /// <summary>
    /// Mark a remote port as permanently empty: its player left and the session continues without
    /// them. The pipeline answers neutral for it forever, and every per-port network mechanism —
    /// UDP-silence liveness, gap requests, the unrepaired-hole backstop — stops watching it, since
    /// each of them would otherwise read an empty seat as a broken link and end the session.
    ///
    /// Callers vacate on a FRESH driver only (at construction, before any frame runs) — see
    /// <see cref="InputPipeline.Vacate"/> for why a mid-timeline vacate is a desync by
    /// construction. The local port cannot be vacated; a peer with no seat has no driver.
    /// </summary>
    public void VacatePort(int port)
    {
        if (port < 0 || port >= _vacated.Length) throw new ArgumentOutOfRangeException(nameof(port));
        if (port == _localPort)
            throw new ArgumentException("the local port cannot be vacated", nameof(port));
        _vacated[port] = true;
        _pipeline.Vacate(port, PortInput.Neutral(_adapter.GetControllerLayout(port)));
        _lastRemoteInputStamp[port] = 0;   // 0 = "never heard", which the silence scan skips
        _holeFirstSeenMs[port] = -1;
        _newestRemoteFrame[port] = -1;
    }

    /// <summary>Rebase UDP-silence tracking at GO. A driver may be seeded before READY, and time
    /// spent waiting at the barrier must not look like an active-session input outage.</summary>
    public void ResetRemoteInputLiveness()
    {
        long now = Stopwatch.GetTimestamp();
        for (int p = 0; p < _lastRemoteInputStamp.Length; p++)
            if (!_vacated[p]) _lastRemoteInputStamp[p] = now; // an empty seat must stay unwatched
    }

    /// <summary>
    /// Seed the local port's first D frames (nothing has been stamped for them yet) with
    /// neutral input and publish them, so the peer can reach frame 0 without stalling.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        var neutral = PortInput.Neutral(_adapter.GetControllerLayout(_localPort));
        long now = Stopwatch.GetTimestamp();
        for (int p = 0; p < _lastRemoteInputStamp.Length; p++)
            if (!_vacated[p]) _lastRemoteInputStamp[p] = now; // an empty seat must stay unwatched
        for (int f = 0; f < _delay; f++)
            ProduceLocal(f, neutral);
        SendWindow(force: true);
    }

    public FrameStep OnPreFrame()
    {
        if (!_started) Start();

        DrainNetwork(MaxDatagramsPerPump);
        RequestMissingRemoteInputIfDue();
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
        RequestMissingRemoteInputIfDue();
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

    /// <summary>
    /// The last-resort freeze detector behind gap retransmission. True when some remote port's
    /// input has a hole that provably slid out of the peer's live resend window (so only a
    /// gap-request can fill it) and has stayed unfilled for <paramref name="stuck"/> despite
    /// those requests. A healthy stall never sets this — the condition requires newer frames
    /// from that peer beyond an unfillable gap. The caller escalates once the duration exceeds
    /// its patience: at that point retransmission itself has failed (requests lost in both
    /// directions, a peer on a build without the request type, or the frame aged out of the
    /// peer's retransmit history) and the session should end with a clear error instead of
    /// freezing while redundant resends keep the arrival-based watchdog quiet.
    /// </summary>
    public bool TryGetUnrepairedHole(out int port, out TimeSpan stuck)
    {
        port = -1;
        stuck = TimeSpan.Zero;
        long now = _sendClock.ElapsedMilliseconds;
        for (int p = 0; p < _holeFirstSeenMs.Length; p++)
        {
            if (p == _localPort || _holeFirstSeenMs[p] < 0) continue;
            var age = TimeSpan.FromMilliseconds(now - _holeFirstSeenMs[p]);
            if (port < 0 || age > stuck) { port = p; stuck = age; }
        }
        return port >= 0;
    }

    /// <summary>Find the remote controller port whose valid input has been silent longest.</summary>
    public bool TryGetMostSilentRemotePort(out int port, out TimeSpan silence)
    {
        port = -1;
        silence = TimeSpan.Zero;
        long now = Stopwatch.GetTimestamp();
        for (int p = 0; p < _lastRemoteInputStamp.Length; p++)
        {
            if (p == _localPort || _lastRemoteInputStamp[p] == 0) continue;
            var age = TimeSpan.FromSeconds(
                (now - _lastRemoteInputStamp[p]) / (double)Stopwatch.Frequency);
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
            // Describe the datagram without copying it. A window repeats the last R frames, nearly
            // all of which we already hold, so the payload for a frame is copied out only once the
            // filters below have decided we are actually going to keep it.
            if (!_codec.TryDecodeInputWindow(datagram, out var window))
            {
                // Not an input window — maybe a peer asking us to re-send frames that slid out
                // of our redundant window (rollback loss recovery). Anything else is ignored.
                if (_codec.TryDecodeRequest(datagram, out byte target, out int fromFrame))
                    ServeGapRequest(target, fromFrame);
                continue;
            }
            int port = window.Port;
            if (port == _localPort) continue;      // never let the wire override our own port
            // A vacated port's input is neutral by definition. The generation check above already
            // rejects the departed player's stragglers; this guard is for the datagram that would
            // otherwise re-stamp liveness and put an empty seat back under the silence watchdog.
            if (_vacated[port]) continue;

            // Any well-decoded frame from a remote port proves that port's UDP path is alive,
            // including a redundant frame we already hold during a lockstep stall.
            _lastRemoteInputStamp[port] = Stopwatch.GetTimestamp();
            // Track the newest frame the peer has SENT (even if we drop it below): the gap
            // between this and the confirmed frontier is what tells us a missing frame has
            // slid out of the peer's live resend window and must be requested.
            //
            // Clamped to the same horizon the per-frame filter below uses. BaseFrame is a raw
            // ReadInt32 off the wire — the codec checks size, port and generation, not range — so a
            // datagram corrupted past UDP's 16-bit checksum could latch a frame number far in the
            // future. Nothing lowers this value, so one such packet made the "unrepaired hole" test
            // permanently true: a gap request every 50ms forever, and the session killed eight
            // seconds later for a hole that was never there.
            int newest = window.BaseFrame + window.Count - 1;
            if (newest > _newestRemoteFrame[port] && newest <= CurrentFrame + _maxLead)
                _newestRemoteFrame[port] = newest;

            for (int i = 0; i < window.Count; i++)
            {
                int frame = window.BaseFrame + i;
                // Genuine redundancy: we already hold this exact (port, frame). This is also the
                // ONLY way an earlier-than-current frame reaches lockstep — it can't have advanced
                // past a frame it lacked — so with _rollbackWindow==0 this matches the old
                // "< CurrentFrame" drop exactly. In rollback, a late correction for an
                // already-simulated frame is NOT yet in the pipeline, so it falls through below.
                if (_pipeline.TryGet(port, frame, out _)) continue;
                // Too old to act on: before the rollback ring / pruned history can reach. For
                // lockstep (window 0) that is anything strictly before the current frame.
                // Rollback gets one extra frame of grace below the window: a hard-cap stall at
                // frame N is waiting for exactly frame N - window - 1 (the first frame past the
                // stalled horizon), and the ring's prune margin keeps that frame's base state
                // alive for the repair — dropping it here would make the stall permanent.
                if (frame < CurrentFrame - _rollbackWindow - (_rollbackWindow > 0 ? 1 : 0)) continue;
                // Impossibly far in the future — bogus, or a pre-resync datagram (high frame number)
                // arriving after we rebuilt at frame 0. Left unchecked it would sit in the pipeline
                // and reapply thousands of frames later.
                if (frame > CurrentFrame + _maxLead) continue;
                var input = _serializers[port].Deserialize(datagram, window.OffsetOf(i));
                _pipeline.Add(port, frame, input);
                _strategy.OnRemoteInput(frame, port);
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
        // Straight into the ring slot this frame will be sent from — no intermediate array, and no
        // separate copy for the retransmit history, which is the same bytes in the same place.
        _serializers[_localPort].Serialize(input, _sentRing, SlotOffset(frame));
        if (_oldestSentFrame < 0) _oldestSentFrame = frame;
        _newestSentFrame = frame;
        // The slot a new frame lands in is the one the frame RetransmitKeepFrames back used, so the
        // history ages out by being overwritten rather than by being removed from anything.
        if (_newestSentFrame - _oldestSentFrame >= _sentRingFrames)
            _oldestSentFrame = _newestSentFrame - _sentRingFrames + 1;
        _lastStamp = frame;
    }

    /// <summary>Byte offset of a frame's payload within <see cref="_sentRing"/>.</summary>
    private int SlotOffset(int frame) => (frame % _sentRingFrames) * _localPayloadSize;

    /// <summary>Whether the ring still holds this frame's payload.</summary>
    private bool HasSentFrame(int frame) =>
        _oldestSentFrame >= 0 && frame >= _oldestSentFrame && frame <= _newestSentFrame;

    /// <summary>
    /// Build and send one input datagram covering <paramref name="count"/> frames from
    /// <paramref name="baseFrame"/>, copying their payloads out of the ring.
    ///
    /// Two copies at most: the run is contiguous in the ring unless it straddles the wrap.
    /// </summary>
    private void SendFrames(int baseFrame, int count)
    {
        if (count <= 0 || _localPayloadSize <= 0) return;
        var datagram = _codec.BeginInputDatagram((byte)_localPort, baseFrame, count, out int offset);

        int startSlot = baseFrame % _sentRingFrames;
        int firstRun = Math.Min(count, _sentRingFrames - startSlot);
        Buffer.BlockCopy(_sentRing, startSlot * _localPayloadSize,
            datagram, offset, firstRun * _localPayloadSize);
        if (firstRun < count)
            Buffer.BlockCopy(_sentRing, 0,
                datagram, offset + firstRun * _localPayloadSize, (count - firstRun) * _localPayloadSize);

        _transport.Send(datagram);
    }

    /// <summary>
    /// Rollback's loss-recovery backstop. The redundant window covers short bursts, but under
    /// rollback the sender does not stall near a frame the peer is missing — it keeps producing
    /// until its own prediction cap — so a burst of ~R consecutive losses slides its window past
    /// the missing frame and that frame would never be sent again, freezing both peers at their
    /// caps with no way out. When a remote port's confirmed frontier falls a window's worth
    /// behind, ask that port's owner to re-send from the first missing frame. Lockstep never
    /// needs this: the behind peer stalls at the missing frame itself and R ≥ 2·delay+1 keeps
    /// the peer's live window covering it.
    /// </summary>
    private void RequestMissingRemoteInputIfDue()
    {
        if (_rollbackWindow <= 0) return;
        // Fire while still running when possible (gap ≥ R means the window may have slid), and
        // guaranteed by the time the hard cap stalls us (gap = window+1) even when R > window.
        int trigger = Math.Min(_redundancy, _rollbackWindow + 1);
        long now = _sendClock.ElapsedMilliseconds;
        for (int p = 0; p < _lastGapRequestMs.Length; p++)
        {
            // The vacated guard is not merely a skip of pointless work: a vacated frontier is
            // int.MaxValue, and `frontier + _redundancy` below would wrap negative — making the
            // beyond-window-hole test permanently true and the KI-9 backstop end the session for
            // a hole in a seat nobody occupies.
            if (p == _localPort || _vacated[p]) continue;
            int frontier = _pipeline.ConfirmedFrontier(p);
            // Depth alone is NOT a sufficient trigger: with time-sync enabled the soft cap
            // stalls us only ~(latency+2) frames past the frontier — far shallower than the
            // window — so a hole burned in during a loss burst never reaches `trigger` and both
            // peers freeze in a mutual "time-sync yield" forever (the first real-internet
            // session deadlocked exactly like this at frame 17). The direct evidence of an
            // unrecoverable hole is the peer's NEWEST frame sitting more than a whole resend
            // window past our frontier: the frame we need has slid out of its live window and
            // will never arrive on its own, no matter how shallow our own stall is.
            bool deepGap = CurrentFrame - 1 - frontier >= trigger;
            bool holeBeyondWindow = _newestRemoteFrame[p] > frontier + _redundancy;
            // KI-9 backstop bookkeeping: remember when a beyond-window hole appeared so the
            // caller can escalate if retransmission never manages to fill it.
            if (holeBeyondWindow)
            {
                if (_holeFirstSeenMs[p] < 0) _holeFirstSeenMs[p] = now;
            }
            else _holeFirstSeenMs[p] = -1;
            if (!deepGap && !holeBeyondWindow) continue;
            if (now - _lastGapRequestMs[p] < GapRequestIntervalMs) continue;
            _lastGapRequestMs[p] = now;
            _transport.Send(_codec.EncodeRequest((byte)p, frontier + 1));
        }
    }

    /// <summary>Answer a peer's gap request from the long retransmit history (no-op unless the
    /// requested port is ours and the frame is still retained).</summary>
    private void ServeGapRequest(byte targetPort, int fromFrame)
    {
        if (targetPort != _localPort) return;
        // Metered, because this is an amplifier: an 18-byte request produces a full redundant
        // window of up to 1200 bytes, and DrainNetwork will process 128 datagrams in a single pump.
        // A peer stuck in a request loop — or one built against a shorter request cadence — turned
        // that into megabytes a second of outbound traffic. Peers ask on a 50ms cadence each, so a
        // budget of eight per window leaves a four-player session an order of magnitude of headroom
        // while still bounding the worst case.
        long now = _sendClock.ElapsedMilliseconds;
        if (now - _serveWindowStartMs >= GapServeWindowMs)
        {
            _serveWindowStartMs = now;
            _servesThisWindow = 0;
        }
        if (_servesThisWindow >= GapServesPerWindow) return;

        if (!HasSentFrame(fromFrame)) return; // aged out even of the long history
        int count = 0;
        while (count < _redundancy && HasSentFrame(fromFrame + count)) count++;
        if (count > 0)
        {
            _servesThisWindow++;
            SendFrames(fromFrame, count);
        }
    }

    private void SendWindow(bool force)
    {
        if (_oldestSentFrame < 0) return;
        long now = _sendClock.ElapsedMilliseconds;
        if (!force && now - _lastSendMs < StallResendIntervalMs) return;
        // The redundant window is the newest R frames the ring still holds — which is what the
        // LinkedList was maintaining by hand, one AddLast and one RemoveFirst per frame.
        int baseFrame = Math.Max(_oldestSentFrame, _newestSentFrame - _redundancy + 1);
        SendFrames(baseFrame, _newestSentFrame - baseFrame + 1);
        _lastSendMs = now;
    }

    /// <summary>Release the strategy's resources (e.g. rollback's savestate ring). Call when replacing
    /// the driver on a resync or tearing the session down, so BizHawk state blobs don't accumulate.</summary>
    public void Dispose() => (_strategy as IDisposable)?.Dispose();
}
