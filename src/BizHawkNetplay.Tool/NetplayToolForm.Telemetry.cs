using System;
using System.Drawing;
using System.Threading;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    /// <summary>
    /// Worst round-trip across peers, preferring the mesh's own measurement over the control link's.
    /// Input rides UDP; once the mesh punches direct paths that isn't even the same route as TCP, and
    /// TCP's number is inflated by its queueing and retransmits. Since this figure both advises the
    /// player's input delay and sizes rollback's prediction horizon, measuring the wrong path costs
    /// real latency. Falls back to the TCP ping when no peer's ack carried a timestamp (older build).
    /// </summary>
    /// <summary>
    /// Milliseconds since a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reading.
    ///
    /// The frame loop times several spans per tick and per frame. A Stopwatch object for each was
    /// roughly 180 allocations a second of pure measurement overhead — inside the very loop whose
    /// collection counts it exists to instrument. GetTimestamp is a static that allocates nothing.
    /// </summary>
    private static double ElapsedMs(long sinceTimestamp) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - sinceTimestamp) * 1000.0
        / System.Diagnostics.Stopwatch.Frequency;

    private double WorstPingMs(out bool udpMeasured)
    {
        udpMeasured = false;
        var mesh = _mesh;
        if (mesh != null && mesh.TryGetWorstRttMs(out double udp) && udp >= 0)
        {
            udpMeasured = true;
            return udp;
        }
        double ping = -1;
        lock (_pingLock) { foreach (var link in _peers) if (link.PingMs > ping) ping = link.PingMs; }
        return ping;
    }

    /// <summary>
    /// How many frames ahead of the peers we are actually running, or 0 when unmeasured.
    ///
    /// Measured one-sidedly this is useless: our view of a peer's frame is stale by the one-way
    /// latency, so both peers always compute themselves "ahead" by about that much even when
    /// perfectly aligned. Each peer therefore reports its own figure, and the difference cancels the
    /// shared latency term — (ours − theirs) / 2 is the real skew, positive when we are the fast one.
    /// The worst (most ahead) peer decides, since that is the one we would out-run.
    /// </summary>
    private int ComputeFrameAdvantage(out bool known, out int revision, out bool fresh)
    {
        lock (_pingLock)
            return _frameAdvantage.Consume(out known, out revision, out fresh);
    }

    private void CheckUdpInputProgress()
    {
        if (_driver == null || _phase.AwaitingRejoin || _phase.IsRebuilding) return;
        // KI-9 backstop, checked BEFORE the silence gate below: a frozen peer's redundant
        // resends keep arrival-silence near zero, so an unrepairable input hole never trips the
        // silence-based watchdog. If gap retransmission has failed to fill a beyond-window hole
        // for this long, end with a clear error instead of freezing indefinitely.
        if (_driver.TryGetUnrepairedHole(out int holePort, out var stuck)
            && stuck.TotalSeconds >= UdpLostAfterSeconds)
        {
            EndSession($"P{holePort + 1}'s input has a gap retransmission could not repair " +
                $"({stuck.TotalSeconds:F0}s) — mismatched builds, or requests lost in both directions");
            return;
        }
        if (!_driver.TryGetMostSilentRemotePort(out int port, out var silence)) return;
        double seconds = silence.TotalSeconds;
        if (seconds < UdpRepunchAfterSeconds)
        {
            if (_udpWarningActive)
            {
                _udpWarningActive = false;
                Log("UDP input path recovered");
            }
            return;
        }

        double nowMs = _paceClock.Elapsed.TotalMilliseconds;
        if (nowMs - _lastUdpRepunchMs >= 1000)
        {
            _lastUdpRepunchMs = nowMs;
            _mesh?.RequestRepunch(port);
            if (!_udpWarningActive)
            {
                _udpWarningActive = true;
                Log($"no UDP input from P{port + 1} for {seconds:F1}s — re-punching the input path" +
                    RejectionNote());
            }
        }
        if (seconds >= UdpLostAfterSeconds)
            EndSession($"UDP input path lost for P{port + 1} ({seconds:F0}s without input; " +
                       $"control link was still alive){RejectionNote()}");
    }

    /// <summary>
    /// If input datagrams are being refused on arrival, say so and say why — otherwise "".
    ///
    /// Appended to the UDP-silence warnings because those two situations are indistinguishable from
    /// the chair and need opposite fixes. A 3-player NES session reported "no UDP input from P3" and
    /// then dropped the session for a lost UDP path, twice, with the two joiners swapping slots in
    /// between — so it was the third PORT failing, not either person's network, and the packets were
    /// arriving all along and being discarded for a size disagreement. Nothing in the log could have
    /// told anyone that.
    /// </summary>
    private string RejectionNote()
    {
        var codec = _driver?.Codec;
        if (codec == null || codec.RejectedTotal == 0) return "";

        string detail = codec.LastSizeMismatchPort >= 0
            ? $" — P{codec.LastSizeMismatchPort + 1}'s input is {codec.LastSizeMismatchObserved} byte(s) " +
              $"per frame, this machine expects {codec.LastSizeMismatchExpected} for that port, so the " +
              "controller/peripheral configuration differs between you (on NES/SNES check the multitap " +
              "or Four Score setting on every machine, then reconnect)"
            : "";
        return $" — NOTE: {codec.RejectedTotal} input packet(s) arrived and were REFUSED " +
               $"(gen {codec.RejectedGeneration}, port {codec.RejectedUnknownPort}, " +
               $"size {codec.RejectedPayloadSize}, malformed {codec.RejectedMalformed}){detail}. " +
               "This is not a network fault — the packets are getting through.";
    }

    private void MaybeSendChecksum()
    {
        int frame;
        uint hash;
        if (_driver!.Strategy is RollbackStrategy rb)
        {
            // Under rollback the current frame may be a prediction that legitimately differs
            // between peers — checksum the newest FINAL interval boundary instead. Both peers
            // quantize to the same boundary, so their reports line up for the host to compare.
            // Normally free: the hash was taken back when the anchor was saved, and its cost was
            // already attributed on that tick. What this still times is the fallback — a repair
            // rewrote the anchor, so the state has to be visited again to hash it.
            //
            // Timestamps rather than a Stopwatch object: this runs on every stepped frame and
            // returns false on roughly 299 of every 300, so the allocation was paying for a
            // measurement that was usually of nothing. Stopwatch.GetTimestamp allocates nothing.
            long hashStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!rb.TryConfirmedChecksum(ChecksumInterval, out frame, out hash)) return;
            _lastHashMs += ElapsedMs(hashStart);
        }
        else
        {
            frame = _driver.CurrentFrame;
            if (frame % ChecksumInterval != 0) return;
            long hashStart = System.Diagnostics.Stopwatch.GetTimestamp();
            hash = _adapter!.HashMainMemory(frame);
            _lastHashMs += ElapsedMs(hashStart);
        }
        // Which checksum path the core actually got, once per session. This is the only place the
        // cost is attributable — in a slow-tick line it just reads as an unexplained hitch.
        if (!_hashDiagLogged && _adapter?.HashDiagnostic != null)
        {
            _hashDiagLogged = true;
            Log(_adapter.HashDiagnostic);
        }
        if (_forceDesyncOnce)
        {
            // Diagnostic: corrupt the reported hash (not the actual state) so the peers disagree
            // and exercise the resync path. The state is fine, so recovery re-matches immediately.
            hash ^= 0xDEADBEEFu;
            _forceDesyncOnce = false;
            Log($"injected a fake desync at frame {frame} (diagnostic)");
        }
        if (Verbose)
        {
            int emuDelta = APIs.Emulation.FrameCount() - _startEmuFrame;
            // In rollback `frame` is a past boundary, so compare drift against the live frame.
            string drift = emuDelta == _driver.CurrentFrame ? "" : $"  !! emuΔ={emuDelta} (expected {_driver.CurrentFrame})";
            Log($"checksum frame {frame}: local {hash:X8}{drift}");
        }
        // The host aggregates all peers' checksums itself; joiners just report theirs to the host.
        var generation = _driver.Generation;
        if (generation != CurrentGeneration) return;
        if (_isHost) RecordChecksum(CurrentConnectionAttempt, generation, _localPort, frame, hash);
        else if (_peers.Count > 0)
        {
            QueueControl(_peers[0], ControlMessageType.Checksum,
                ControlMessageCodec.EncodeChecksum(generation, frame, hash));
        }
    }

    /// <summary>
    /// On a wall-clock cadence (not tied to frame stepping, so a stalled peer keeps them flowing),
    /// ping each peer with our monotonic clock; the peer echoes it back and the returning Pong gives
    /// that link's round-trip time. Doubles as the liveness signal the drop watchdog watches for.
    /// </summary>
    private void MaybeSendPing()
    {
        if (_simUnresponsive) return; // diagnostic: pretend we're frozen
        double nowMs = _pingClock.Elapsed.TotalMilliseconds;
        if (_lastPingMs >= 0 && nowMs - _lastPingMs < PingIntervalMs) return;
        _lastPingMs = nowMs;
        var body = BitConverter.GetBytes(nowMs);
        int frame = _driver?.CurrentFrame ?? 0;
        var generation = CurrentGeneration;
        foreach (var link in _peers)
        {
            QueueControl(link, ControlMessageType.Ping, body);
            // Piggyback the frame-advantage exchange on the same cadence: where we are, and how far
            // ahead we currently measure ourselves against this peer. Additive message type — a peer
            // on an older build ignores it and simply never reports back.
            int mine, sequence, acknowledges;
            lock (_pingLock)
            {
                mine = link.LocalAdvantage;
                sequence = ++link.PacingSendSequence;
                acknowledges = link.LastReceivedPacingSequence;
            }
            QueueControl(link, ControlMessageType.Pacing,
                ControlMessageCodec.EncodePacing(generation, sequence, acknowledges, frame, mine));
        }
    }

    /// <summary>
    /// Watchdog: a link that hasn't sent us anything for <see cref="PingTimeoutSeconds"/> is presumed
    /// dropped (frozen peer or a silent cable-pull that never broke TCP) and routed into the same
    /// drop handling as a broken connection. Pings/pongs are serviced on the reader thread regardless
    /// of stepping, so a merely stalled — but alive — peer keeps answering and is never flagged here.
    ///
    /// The exemptions are per link, not global. Blanket-skipping every peer whenever a resync or a
    /// reconnect was in flight left the other peers unwatched exactly when a session is most fragile:
    /// with 3–4 players, a second peer pulling its cable while the host waited on the first went
    /// unnoticed until the 60s rejoin timer expired. A peer is excused only if it's the one busy with
    /// a whole-state transfer — receiving one (<see cref="PeerLink.ResyncReceiving"/>) or still
    /// consuming one we sent it (<see cref="PeerLink.TimeoutGraceUntilTicks"/>).
    /// </summary>
    private void CheckLinkTimeouts()
    {
        // The decision rule (and why its ordering is load-bearing) lives in Core: LinkHealth.
        long now = MonotonicNow();
        long limit = MonotonicTicks(PingTimeoutSeconds);
        PeerLink? dead = null;
        var verdict = LinkVerdict.Healthy;
        int unappliedEpoch = 0;
        int incompleteEpoch = 0;
        foreach (var link in _peers)
        {
            var snapshot = new LinkHealth.LinkSnapshot(
                link.AwaitingAppliedEpoch,
                Interlocked.Read(ref link.AppliedDeadlineTicks),
                link.ResyncReceiving,
                Interlocked.Read(ref link.ResyncReceiveDeadlineTicks),
                Interlocked.Read(ref link.TimeoutGraceUntilTicks),
                Interlocked.Read(ref link.LastRecvTicks));
            verdict = LinkHealth.Judge(snapshot, now, limit);
            if (verdict != LinkVerdict.Healthy)
            {
                dead = link;
                unappliedEpoch = snapshot.AwaitingAppliedEpoch;
                incompleteEpoch = link.ReceivingResyncEpoch;
                break;
            }
        }
        if (dead == null) return;
        // Guard against the completion race: the reader clears ResyncReceiving/epoch/deadline as
        // separate writes, so a scan can catch ResyncReceiving still true with the deadline
        // already zeroed — a spurious "expired" with epoch 0. Route that through the ordinary
        // drop path (whose _phase.IsActive/_peers guards make it a no-op for a healthy link)
        // instead of unconditionally ending the session.
        if (verdict == LinkVerdict.ResyncReceiveDeadlineExpired && incompleteEpoch != 0)
            EndSession($"{dead.Label} did not finish sending resync epoch {incompleteEpoch} before its deadline");
        else if (verdict == LinkVerdict.AppliedDeadlineExpired)
            EndSession($"{dead.Label} did not apply resync epoch {unappliedEpoch} before its deadline");
        else
            OnPeerLinkLost(dead, $"no response for {PingTimeoutSeconds:F0}s (ping timeout)");
    }

    /// <summary>
    /// Excuse one peer from the ping watchdog while a whole state of <paramref name="stateBytes"/> is
    /// on its way to it. The window covers the transfer at a pessimistic wire rate plus the peer's
    /// read+import — never open-ended, so a peer that dies mid-transfer is still caught, just later.
    /// </summary>
    private void GraceForStateTransfer(PeerLink link, int stateBytes)
    {
        Interlocked.Exchange(ref link.TimeoutGraceUntilTicks,
            MonotonicDeadline(StateTransferBudget.ApplyDeadlineSeconds(stateBytes)));
    }

    private static long StateApplyDeadlineTicks(int stateBytes) =>
        MonotonicDeadline(StateTransferBudget.ApplyDeadlineSeconds(stateBytes));

    // Why the survivor budget spans the host's whole 3-phase pipeline: see StateTransferBudget.
    private static long StateReceiveDeadlineTicks(int stateBytes, int waitSeconds) =>
        MonotonicDeadline(StateTransferBudget.SurvivorReceiveDeadlineSeconds(stateBytes, waitSeconds));

    /// <summary>Apply the host's pre-WELCOME RTT estimate without ever lowering an explicit ask.</summary>
    private int SelectLobbyDelay(int manualFloor, int automaticMaximum, SyncMode mode,
        double measuredRttMs, double frameMs, int simulatedOneWayMs, int players,
        double jitterMs = 0)
    {
        double effectiveRttMs = measuredRttMs + 2.0 * Math.Max(0, simulatedOneWayMs);
        var choice = LobbyDelayPolicy.Choose(effectiveRttMs, frameMs, mode,
            manualFloor, automaticMaximum, jitterMs);

        string simulated = simulatedOneWayMs > 0
            ? $", including {2 * simulatedOneWayMs}ms simulated"
            : "";
        string jitter = jitterMs >= 1 ? $", jitter ±{jitterMs:F0}ms" : "";
        string capped = choice.WasCapped
            ? $"; smooth target {choice.AutomaticFrames} was capped at {automaticMaximum}"
            : "";
        string floor = manualFloor > automaticMaximum
            ? $"; explicit floor {manualFloor} remains above the automatic max"
            : $"; manual floor {manualFloor}, max {automaticMaximum}";
        string meshNote = players > 2
            ? " Figure covers every direct UDP path, joiner-to-joiner included, not just this host's links."
            : "";

        UiConnLog($"Auto delay: worst lobby RTT ~{effectiveRttMs:F0}ms{simulated}{jitter} → " +
            $"{choice.Frames} frame(s) for {(mode == SyncMode.Rollback ? "rollback" : "lockstep")}" +
            floor + capped + "." + meshNote,
            choice.WasCapped ? Color.DarkOrange : Color.DarkGreen);
        return choice.Frames;
    }
    /// <summary>Session-shaped wrappers over <see cref="DelayAdvice"/>: the controls it needs are
    /// this form's, the wording and the branching are not.</summary>
    private string ApplyDelayAdvice(int suggested) =>
        DelayAdvice.ApplyNow(_isHost, suggested, (int)_delayBox.Maximum);

    private string DelayRemedy(int suggested) => DelayAdvice.Remedy(
        _isHost, suggested, _autoDelayCheck.Checked, (int)_autoDelayMaxBox.Value,
        (int)_delayBox.Maximum);


    /// <summary>
    /// Once ping is stable, if the negotiated input delay is lower than the worst link's round-trip
    /// really needs, say so once — too-low delay is the usual cause of constant stalling on a real
    /// network. Lockstep needs delay·frameMs to cover the one-way latency (≈ RTT/2).
    /// </summary>
    private void MaybeHintDelay()
    {
        if (_delayHintShown || _peers.Count == 0) return;
        int minCount = int.MaxValue;
        lock (_pingLock)
        {
            foreach (var link in _peers)
                if (link.PingCount < minCount) minCount = link.PingCount;
        }
        // Gate on control-channel samples either way — it's the count that proves the session has
        // been running long enough for any reading to have settled.
        double worst = WorstPingMs(out _);
        if (minCount < 6 || worst < 0) return;
        _delayHintShown = true;
        // Include the simulated one-way UDP delay (RTT contribution = 2×) — the input actually rides
        // that delayed channel, so the recommendation must reflect it even though the TCP ping doesn't.
        double effWorst = worst + 2.0 * _simLatencyMs;
        string simNote = _simLatencyMs > 0 ? $" (incl. {2 * _simLatencyMs}ms sim)" : "";

        var recommendation = LobbyDelayPolicy.Choose(effWorst, _frameMs, _mode,
            manualFloor: 1, automaticMaximum: 20);
        int suggested = recommendation.AutomaticFrames;
        if (suggested > _sessionDelay)
        {
            ConnLog($"worst link ping ~{effWorst:F0}ms{simNote}: smooth " +
                $"{(_mode == SyncMode.Rollback ? "rollback" : "lockstep")} recommends delay {suggested} " +
                $"(this session is {_sessionDelay}). {DelayRemedy(suggested)}",
                Color.DarkOrange);
        }
        else if (_sessionDelay - suggested >= 2)
        {
            // Only ever nagging upward left people permanently over-delayed: the box is sticky, so a
            // value picked for one bad link keeps costing latency on every good one afterwards.
            double excessMs = (_sessionDelay - suggested) * _frameMs;
            ConnLog($"worst link ping ~{effWorst:F0}ms{simNote}: this link only needs input delay {suggested}, and " +
                $"the session is running {_sessionDelay} — about {excessMs:F0}ms of extra response time. " +
                // Not DelayRemedy: its tail explains why the lobby chose too LOW a number, which is
                // the opposite problem and would read as nonsense here.
                $"{ApplyDelayAdvice(suggested)}",
                Color.DarkOrange);
        }
        else
        {
            ConnLog($"worst link ping ~{effWorst:F0}ms{simNote}: input delay {_sessionDelay} is comfortable for " +
                "this link.", Color.DimGray);
        }
    }

}
