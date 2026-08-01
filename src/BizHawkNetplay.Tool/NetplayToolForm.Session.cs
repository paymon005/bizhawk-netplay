using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Threading;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    // --- State used only by this file (everything shared stays in NetplayToolForm.cs) ---
    private int _delayBoxSyncedTo = -1;   // last session delay pushed into _delayBox; see RefreshLiveSettingsUi

    // ------------------------------------------------------------------ session

    private void BeginSessionHost(List<PeerLink> links, int players, int delay, SyncMode mode,
        SessionGeneration generation)
    {
        try
        {
            if (!DriverPreparedFor(generation, mode))
                PrepareSessionHost(links, players, delay, mode, generation);
            foreach (var link in links) UntrackHandshakeResources(link);
            Log($"emulator frame at start: {APIs.Emulation.FrameCount()}");
            ConnLog($"all {players} players connected — you are P1 (host)", Color.DarkGreen);
            BeginSessionCommon(mode, $"{links.Count} peer(s)");
        }
        catch (Exception ex) { FailSession(ex.Message); }
    }

    private void BeginSessionJoiner(SessionParams sp, PeerLink hostLink, bool initialStateApplied = false)
    {
        try
        {
            if (!initialStateApplied || !DriverPreparedFor(sp.Generation, sp.Mode))
                PrepareSessionJoiner(sp, hostLink);
            UntrackHandshakeResources(hostLink);
            // Both peers should print the SAME number here; if not, the start is misaligned.
            Log($"emulator frame at start: {APIs.Emulation.FrameCount()}");
            ConnLog($"connected — joined as P{sp.LocalPort + 1} of {sp.PlayerCount}", Color.DarkGreen);
            if (_pendingJoinIp != null) { RecordJoinIp(_pendingJoinIp); _pendingJoinIp = null; } // connect succeeded
            BeginSessionCommon(sp.Mode, hostLink.Label);
        }
        catch (Exception ex) { FailSession(ex.Message); }
    }

    private void PrepareSessionHost(List<PeerLink> links, int players, int delay, SyncMode mode,
        SessionGeneration generation)
    {
        _peers.Clear(); _peers.AddRange(links);
        _isHost = true; _playerCount = players; _sessionDelay = delay; _localPort = 0;
        SetGeneration(generation);
        _mesh?.SetPeerRoutes(RoutesExcept(links, null));
        // The lobby decided WHICH ports to relay for; this is the first moment _peers can turn those
        // port numbers into endpoints. Say how many routes actually resulted — a relay that was
        // announced in the log but resolved to nothing is the failure this ordering exists to stop.
        RefreshRelayRoutes();
        if (_relayPorts.Count > 0)
            ConnLog($"relay resolved: {_mesh?.RelayRouteCount ?? 0} route(s) for {_relayPorts.Count} " +
                    "relayed player(s).", (_mesh?.RelayRouteCount ?? 0) > 0 ? Color.DarkOrange : Color.Firebrick);
        PrepareSessionDriver(mode);
    }

    private void PrepareSessionJoiner(SessionParams sp, PeerLink hostLink)
    {
        if (_preJoinRestoreState == null) _preJoinRestoreState = _adapter!.ExportState();
        ApplyInitialState(sp);
        _peers.Clear(); _peers.Add(hostLink);
        _isHost = false; _playerCount = sp.PlayerCount; _sessionDelay = sp.InputDelay; _localPort = sp.LocalPort;
        SetGeneration(sp.Generation);
        _meshOthers = new List<PeerRoute>(sp.PeerRoutes);
        _mesh?.ApplyTokens(sp.Tokens);
        ApplyJoinerMesh();
        PrepareSessionDriver(sp.Mode);
    }

    private void ApplyInitialState(SessionParams sp)
    {
        if (sp.InitialState == null) return;
        _adapter!.ImportState(sp.InitialState);
        Log($"imported {sp.InitialState.Length / 1024}KiB host state");
    }

    private bool DriverPreparedFor(SessionGeneration generation, SyncMode mode) =>
        _sessionDriverPrepared && _driver != null && _driver.Generation == generation && _mode == mode;

    /// <summary>
    /// Size this peer's savestate ring and say what that costs. Called wherever rollback becomes the
    /// running mode — at session start, and again when the host switches netcode mid-session, which
    /// needs exactly the same measurement and deserves exactly the same warnings.
    /// </summary>
    private void ConfigureRollbackDepth()
    {
        // Ring depth = this peer's probe depth, clamped so resim cost + memory stay bounded.
        // Each peer bounds its own ring independently; correctness never needs them equal.
        // Floored at MinRollbackRing, NOT at the qualifying threshold. Flooring at the threshold
        // meant a core the probe measured at 2 silently ran a ring of 3 — booking repair work
        // the machine had just been told it could not afford, and then reporting the inflated
        // number back to the user as if it had been measured.
        int measured = _probeDepth > 0 ? _probeDepth : ProbeResult.RollbackDepthThreshold;
        _rollbackDepth = Math.Max(MinRollbackRing, Math.Min(measured, RollbackDepthCap));
        if (_probeDepth >= 0 && _probeDepth < ProbeResult.RollbackDepthThreshold)
            ConnLog($"rollback is overriding this machine's own measurement: the probe found a " +
                $"usable depth of {_probeDepth}, below the {ProbeResult.RollbackDepthThreshold} it " +
                "considers worthwhile, so every correction will cost more than a frame and the " +
                "picture will stutter whenever the link makes it predict. Netcode is on forced " +
                "Rollback — switch it to Automatic to let the probe decide, or Lockstep to stop " +
                "predicting entirely.", Color.Firebrick);
        else if (_rollbackDepth <= ShallowRollbackDepth)
            ConnLog($"rollback on a heavy core: this machine measured a usable depth of " +
                $"{_rollbackDepth} frames, so it can hide about {_rollbackDepth} frames of one-way " +
                "latency and no more — good for a nearby opponent, not a distant one. Corrections " +
                "cost a brief hitch here rather than the stall lockstep would have taken. Switch " +
                "Netcode to Lockstep if you prefer the steadier frame time.",
                Color.DarkSlateBlue);
        if (_playerCount > 2)
            ConnLog($"rollback with {_playerCount} players: every peer predicts the other " +
                $"{_playerCount - 1} ports, so a correction from any of them rolls everyone back — " +
                "expect rollbacks to fire more often than in a 2-player session (they are no deeper: " +
                "input goes peer-to-peer in one hop). Switch Netcode to Lockstep if it feels choppy.",
                Color.DarkSlateBlue);
    }

    private void UpdateNetcodeLabel()
    {
        bool rollback = _mode == SyncMode.Rollback;
        _netcodeLabel.Text = _phase.IsActive
            ? $"Netcode in use: {(rollback ? "Rollback" : "Lockstep")}, delay {_sessionDelay}"
            : "Netcode in use: " + (rollback ? "Rollback" : "Lockstep");
        _netcodeLabel.ForeColor = rollback ? Color.DarkGreen : Color.DarkSlateBlue;
    }

    /// <summary>
    /// What the lobby/join path knows and no session state records: that we are dialling out, or how
    /// many seats are still empty. Cleared with an empty string once a session takes over, after
    /// which <see cref="UpdateLobbyStatus"/> derives everything itself.
    /// </summary>
    private void SetLobbyPhase(string text, Color color)
    {
        _lobbyPhaseText = text ?? "";
        _lobbyPhaseColor = color;
        UpdateLobbyStatus();
    }

    /// <summary>
    /// Re-derive the lobby status box. Called on a timer rather than from the frame loop, because
    /// every state worth flashing is one where frames are not running.
    ///
    /// The flash is the point: "frozen" has several causes that look identical from the chair — a
    /// peer's input has not arrived, the session is rebuilding around a resync, a seat is being held
    /// for someone who dropped, or the lobby simply has not filled yet. All of them stop the picture,
    /// and previously all of them looked like the tool having hung. A flashing line says the tool
    /// knows, and names which one it is.
    /// </summary>
    private void UpdateLobbyStatus()
    {
        if (_lobbyStateLabel == null || _lobbyStateLabel.IsDisposed) return;

        string text;
        Color color;
        bool flash;

        if (_phase.AwaitingRejoin)
        {
            int seat = _reconnectPort + 1;
            text = $"P{seat} dropped — holding their seat, waiting up to {ReconnectTimeoutSeconds:F0}s for a rejoin…";
            color = Color.DarkOrange;
            flash = true;
        }
        else if (_phase.IsRebuilding)
        {
            text = "Resynchronizing — sharing an authoritative state with every player…";
            color = Color.DarkOrange;
            flash = true;
        }
        else if (_phase.IsActive)
        {
            bool stalled = _driver?.IsStalled == true;
            text = stalled
                ? "Waiting for input from another player…"
                : $"Lobby live — game running with {_playerCount} player(s).";
            color = stalled ? Color.DarkOrange : Color.DarkGreen;
            flash = stalled;
        }
        else if (_lobbyPhaseText.Length > 0)
        {
            text = _lobbyPhaseText;
            color = _lobbyPhaseColor;
            flash = true;   // nothing is running yet, by definition
        }
        else
        {
            text = "Not connected — pick Host or Join, then Start.";
            color = Color.DimGray;
            flash = false;
        }

        if (!string.Equals(_lobbyStateLabel.Text, text, StringComparison.Ordinal))
            _lobbyStateLabel.Text = text;
        // Pulse between the state's colour and a washed-out version of it rather than blinking to
        // invisible: it reads as "still working" instead of as a fault light, and the text stays
        // legible on both halves of the cycle.
        _lobbyStateLabel.ForeColor = flash && !_lobbyFlashOn ? Fade(color, _lobbyPanel.BackColor) : color;
    }

    /// <summary>Halfway between two colours — the dimmed half of the status flash.</summary>
    private static Color Fade(Color c, Color toward) => Color.FromArgb(
        (c.R + toward.R) / 2, (c.G + toward.G) / 2, (c.B + toward.B) / 2);

    /// <summary>
    /// Netcode and input delay stay editable for the HOST while a session runs; "Apply changes" is
    /// what actually pushes them, so spinning the delay box does not ship a savestate per click. A
    /// joiner's copies stay read-only for the same reason they are in the lobby: these are the
    /// host's to settle, and a control that looks live but is ignored is worse than a greyed one.
    /// </summary>
    private void RefreshLiveSettingsUi()
    {
        bool liveHost = _phase.IsActive && _isHost;
        if (liveHost)
        {
            _netcodeCombo.Enabled = true;
            _delayBox.Enabled = true;
            // Show the delay the session is ACTUALLY on, not the floor that was asked for. With
            // auto-delay the two differ — the lobby may have measured its way from 1 up to 4 — and
            // a box reading 1 next to an Apply button would quietly halve the delay of a session
            // the host only meant to switch the netcode of.
            //
            // Only when the session's delay CHANGED, though. This runs after a desync resync too,
            // and overwriting the box there would throw away a number the host was halfway through
            // typing. Behind the settings guard either way, so it never persists over the saved
            // preference.
            if (_delayBoxSyncedTo != _sessionDelay)
            {
                _delayBoxSyncedTo = _sessionDelay;
                _loadingSettings = true;
                try { _delayBox.Value = Clamp(_sessionDelay, (int)_delayBox.Minimum, (int)_delayBox.Maximum); }
                finally { _loadingSettings = false; }
            }
        }
        else _delayBoxSyncedTo = -1;
        // Refused while another rebuild is in flight: two authoritative baselines racing each other
        // is the one way this could desync a session it exists to keep running.
        _applyLiveButton.Enabled = liveHost && !_phase.IsRebuilding && !_phase.AwaitingRejoin;
    }

    /// <summary>Construct and seed the exact generation-bound driver before READY. It may publish
    /// neutral input, but no frame clock or control reader is activated until GO.</summary>
    private void PrepareSessionDriver(SyncMode mode)
    {
        _mode = mode;
        if (mode == SyncMode.Rollback) ConfigureRollbackDepth();
        try { _driver?.Dispose(); } catch { }
        _driver = CreateDriver();
        _startEmuFrame = APIs.Emulation.FrameCount();
        _driver.Start();
        _sessionDriverPrepared = true;
    }

    /// <summary>The role-independent post-GO activation: audio, control I/O, and frame pacing.</summary>
    private void BeginSessionCommon(SyncMode mode, string remoteLabel)
    {
        // A banner in the file only. One launch's log can hold several sessions plus the failed
        // attempts between them, and someone reading a log they did not produce needs to find where
        // the run they were told about begins. The window's own log gets this from ConnLog.
        _logFile?.Write($"{Environment.NewLine}=== session start — {mode}, {_playerCount} player(s), " +
                        $"P{_localPort + 1}, vs {remoteLabel}, delay {_sessionDelay} ==={Environment.NewLine}");

        if (!DriverPreparedFor(CurrentGeneration, mode)) PrepareSessionDriver(mode);

        // The lobby's own account of itself stops here; from now on the status is derived from the
        // session, which knows more than the lobby thread could.
        _lobbyPhaseText = "";

        // Ownership and rewind suspension were taken at the first pause, back when the lobby opened;
        // PauseForSession is idempotent about that and this call is just the re-pause.
        PauseForSession(); // we own the clock now
        _startEmuFrame = APIs.Emulation.FrameCount(); // baseline for frame-advance drift checks
        _checksumDue = false;
        _resyncCount = 0;
        _desyncTrend.Reset();
        _reconnectState = null;
        _reconnectGeneration = default;
        _pendingReconnectLink = null;
        _pendingReconnectStateLength = 0;
        _pendingReconnectGeneration = default;
        _lastResyncStamp = 0;
        lock (_hashLock) { _checksums.Clear(); }
        _driver!.Start(); // idempotent; normally seeded before READY
        _driver.ResetRemoteInputLiveness();
        _sessionDriverPrepared = false;
        _phase.Start(); // GO: active, not rebuilding, nobody's seat empty
        _preJoinRestoreState = null; // GO committed the imported baseline
        EngageUnpausedClock(); // experimental opt-in; after _phase.Start so ReassertPause agrees

        // We own the frame clock (EmuHawk stays paused), so its loop never pumps sound —
        // hand the adapter EmuHawk's Sound device so it can drive audio after each frame.
        _audioStatsLogged = false;
        _adapter!.AttachMainForm(MainForm as BizHawk.Client.EmuHawk.MainForm);
        _adapter.EnableAudio(MainForm as BizHawk.Client.EmuHawk.MainForm);
        Log(_adapter.AudioReady ? "audio enabled — " + _adapter.AudioDiagnostic
                                : "(note) audio unavailable: " + _adapter.AudioDiagnostic);
        // Ask once, so the answer is in the log rather than discovered as "my autofire stopped".
        _adapter.AdvanceHostInputBookkeeping();
        if (!_adapter.HostInputBookkeepingAvailable)
            Log("(note) could not reach EmuHawk's per-frame input bookkeeping — sticky autofire and " +
                "Virtual Pad clicks will not advance during this session. Ordinary controller input " +
                "is unaffected.");

        // Render resolution isn't a sync setting, so nothing else in the session will ever mention
        // it — and on N64 it is the single most likely reason two peers disagree. Put it in the log
        // where it can be correlated after the fact, not only in a warning after it has gone wrong.
        _videoDiagnostic = _adapter.VideoSettingsDiagnostic();
        if (_videoDiagnostic != null)
            Log($"video: {_videoDiagnostic} — resolution is NOT a sync setting, so it is not checked " +
                "at connect. Above native the plugin resolves its framebuffer back into main RAM, " +
                "and those bytes come from the GPU — which disagrees between machines even when both " +
                "players have identical settings, and shows up as a desync at every checksum.");

        // The digital-stick-override note deliberately does NOT go here. It fires for any N64 setup
        // using BizHawk's default XInput binds, and measurement showed the override usually does not
        // fire at all — so at session start it is an unsolicited warning about a non-problem, which
        // is worse than silence. It lives in the input test, where someone is already asking.

        // One reader and one serialized outbound writer per control link. The writer is what keeps
        // checksums, pings, and especially whole-state resync transfers off EmuHawk's UI thread.
        foreach (var link in _peers)
        {
            StartPeerIo(link);
        }
        _lastPingMs = -1; // send the first ping immediately

        // Real-time pacing: tick often and advance however many frames wall-clock demands,
        // so irregular WinForms-timer firing doesn't run the game slow.
        _frameMs = FrameMs();
        // Raise the OS timer resolution and measure what we actually got BEFORE the pacing clocks
        // start, so the probe's own cost isn't charged to frame zero as debt.
        try { if (!_timerResRaised) { timeBeginPeriod(1); _timerResRaised = true; } } catch { }
        LogTimerGranularity();
        _delayHintShown = false;
        lock (_pingLock) { foreach (var link in _peers) { link.PingMs = -1; link.PingCount = 0; } }
        _pingClock.Restart();
        _paceClock.Restart();
        _schedule.FrameMs = _frameMs;
        _schedule.Restart(0);
        _lastUiRefreshMs = double.NegativeInfinity;
        _lastSlowTickLogMs = double.NegativeInfinity;
        _lastVerboseAudioFrame = -1;
        _lastStallLogMs = double.NegativeInfinity;
        _lastUdpRepunchMs = double.NegativeInfinity;
        _udpWarningActive = false;
        _pacingRebases = 0;
        _fpsClock.Restart(); _fpsCount = 0; _actualFps = -1;
        _pacing.Reset(); _lastPacing = default;
        _lastPacingLogMs = double.NegativeInfinity;
        _stallHint.Reset();
        _rollbackCostHint.Reset();
        _saveRateHint.Reset();
        _audioDevWasUp = true;   // a fresh session starts assuming sound is up; the edge reports otherwise
        _presentHint.Reset();
        _hashDiagLogged = false;
        _lastTickClockMs = -1;
        _lastPresentClockMs = -1;
        // Per-session, not per-window: a spike from a previous session must not be reported as
        // this one's worst case, and the save/rollback baselines belong to the strategy that is
        // about to be created rather than the one that just went away.
        _worstGateSinceLogMs = 0;
        _gateSpikesSinceLog = 0;
        _gcGateWindow0 = _gcGateWindow1 = _gcGateWindow2 = 0;
        _pacingRollbacks.Reset();
        _pacingResim.Reset();
        _pacingSavesTaken.Reset();
        _pacingSavesElided.Reset();
        // A WinForms timer is WM_TIMER, and SetTimer silently raises anything below
        // USER_TIMER_MINIMUM to 10ms — asking for 2 never bought a 2ms cadence, it just hid the
        // real floor. State it honestly: ~10ms is the fastest this mechanism goes, which is still
        // comfortably under a frame period so long as we don't serialize on top of it (see
        // FrameTick — the timer deliberately keeps running while a tick is in flight).
        _frameTimer.Interval = 10;
        _frameTimer.Start();
        // The timer above is only a heartbeat now; UpdateValues is what paces frames. See its
        // remarks for why WM_TIMER cannot do this job however short its interval is set.
        _lastFineTickMs = double.NegativeInfinity;

        Status($"in session — {DescribeMode(mode)}, " +
               $"you are P{_localPort + 1}/{_playerCount}, delay {_sessionDelay}", Color.Green);
        UpdateNetcodeLabel();
        RefreshLiveSettingsUi();
        RefreshPlayersList();
        ConnLog($"session started vs {remoteLabel} — {(mode == SyncMode.Rollback ? "rollback" : "lockstep")}, " +
                $"delay {_sessionDelay}", Color.DarkGreen);
        _disconnectButton.Enabled = true;

        // NAT traversal: a joiner discovers its public (reflexive) mesh endpoint and reports it to the
        // host, which shares it so peers can reach us across NAT. Additive to the LAN candidates, so
        // LAN/localhost play is unaffected whether or not this succeeds. The host is reached at the
        // address joiners connected to, so it doesn't report one.
        if (!_isHost) ShareReflexiveWithHost();
    }

    /// <summary>
    /// Record our public UDP endpoint and release anyone waiting on it. Called from whichever path
    /// discovered it first — the lobby pre-HELLO discovery, the punch flow, or the post-GO refresh.
    /// </summary>
    private void PublishLocalReflexive(IPEndPoint? reflexive)
    {
        if (reflexive != null) _localReflexive = reflexive;
        _reflexiveKnown.Set(); // "the answer is in", including when the answer is "STUN is blocked"
    }

    /// <summary>
    /// Joiner: STUN-discover this mesh socket's public endpoint BEFORE the handshake, so the HELLO
    /// can carry it. The host puts every joiner's candidates into the routes it hands out in
    /// WELCOME, and those routes are what the pre-GO mesh probe punches at — so discovering this
    /// after GO, as this used to, meant every joiner-to-joiner route in the lobby was the
    /// <c>(public IP, local port)</c> guess alone. That guess holds only where the NAT preserves
    /// the source port; where it does not, the edges the delay probe exists to measure never opened
    /// in time and the delay quietly fell back to the host's own TCP reading.
    /// </summary>
    private void StartReflexiveDiscovery(MeshUdpTransport mesh, int attempt)
    {
        new Thread(() =>
        {
            IPEndPoint? reflexive = null;
            try { reflexive = mesh.DiscoverReflexive(TimeSpan.FromSeconds(2.5)); }
            catch when (!IsConnectionAttemptCurrent(attempt)) { return; }
            catch (Exception ex)
            {
                if (IsConnectionAttemptCurrent(attempt))
                    UiLog("(note) UDP address discovery failed: " + ex.Message);
                PublishLocalReflexive(null); // never leave the handshake waiting on a dead answer
                return;
            }
            // Stale: a newer attempt owns _reflexiveKnown now, and has Reset it. Setting it here
            // would release that attempt's AwaitLocalReflexive before ITS discovery answered, so
            // its HELLO would silently carry no public candidate. Say nothing and touch nothing.
            if (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_mesh, mesh)) return;
            PublishLocalReflexive(reflexive);
            if (reflexive == null)
            {
                UiLog("(note) couldn't determine our public UDP endpoint (STUN blocked) — internet peers may be unreachable");
                return;
            }
            UiLog($"our public UDP endpoint is {reflexive} — sharing it for NAT traversal");
        })
        { IsBackground = true, Name = "BizHawkNetplay-stun-mesh" }.Start();
    }

    /// <summary>
    /// Block briefly for the discovery started at bind time. Bounded, because a blocked STUN server
    /// must cost a joiner a moment rather than the session: with no answer the HELLO simply carries
    /// no candidate and the mesh falls back to observed addresses, exactly as it did before.
    /// </summary>
    private IPEndPoint? AwaitLocalReflexive()
    {
        try { _reflexiveKnown.Wait(ReflexiveWaitMs); } catch { }
        return _localReflexive;
    }

    /// <summary>Joiner, post-GO: re-share the public endpoint over the live control link. Reuses
    /// the lobby's discovery when it succeeded — the endpoint is a property of the socket, which
    /// has not changed, so re-running STUN would only add a second round trip and a second answer
    /// to disagree with.</summary>
    private void ShareReflexiveWithHost()
    {
        var mesh = _mesh;
        if (mesh == null) return;
        int attempt = CurrentConnectionAttempt;
        new Thread(() =>
        {
            var reflexive = _localReflexive;
            if (reflexive == null)
            {
                try { reflexive = mesh.DiscoverReflexive(TimeSpan.FromSeconds(2.5)); }
                catch { return; }
                if (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_mesh, mesh)) return;
                if (reflexive == null) return;
                PublishLocalReflexive(reflexive);
                UiLog($"our public UDP endpoint is {reflexive} — sharing it for NAT traversal");
            }
            var endpoint = reflexive;
            BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt) && ReferenceEquals(_mesh, mesh)
                    && _phase.IsActive && _peers.Count > 0)
                    QueueControl(_peers[0], ControlMessageType.Candidate,
                        HandshakeCodec.EncodeEndpoints(new[] { endpoint }));
            });
        })
        { IsBackground = true, Name = "BizHawkNetplay-stun-share" }.Start();
    }

    /// <summary>Shortest gap between two candidate updates this host will act on, per peer.</summary>
    private const int CandidateUpdateIntervalMs = 2000;

    /// <summary>How many candidate updates one peer may have acted on before the rest are ignored.
    /// STUN answers once; a rebinding might genuinely move it a handful of times in a long session.
    /// Past that, a peer is not reporting an address, it is driving a broadcast.</summary>
    private const int MaxCandidateUpdatesPerPeer = 8;

    /// <summary>
    /// Host: record a joiner's reflexive endpoint and re-share the candidate lists.
    ///
    /// Believed only if it matches the address this peer's own control connection arrives from
    /// (see <see cref="ReflexiveCandidate"/>) — every accepted value here is handed to every other
    /// player, probed by all of them four times a second, and used as an input destination when no
    /// direct path is confirmed. Unchecked, this one frame re-aimed the entire session at an
    /// address of the sender's choosing, mid-game, as often as it liked.
    ///
    /// Metered as well as checked: acting on an update costs a PeerList to every peer, so even
    /// truthful churn is bounded rather than trusted to be occasional.
    /// </summary>
    private void OnJoinerCandidate(PeerLink link, IPEndPoint reflexive)
    {
        if (!_phase.IsActive || !_isHost || _phase.AwaitingRejoin) return;
        if (!_peers.Contains(link)) return;                                  // dropped meanwhile
        if (reflexive.Equals(link.ReflexiveEndpoint)) return;               // unchanged

        var observed = (link.Tcp?.Client?.RemoteEndPoint as IPEndPoint)?.Address
                       ?? link.UdpEndpoint?.Address;
        if (!ReflexiveCandidate.IsCredible(reflexive, observed))
        {
            if (Verbose)
                Log($"{link.Label} announced public endpoint {reflexive}, which is not where it " +
                    $"reaches us from ({observed?.ToString() ?? "unknown"}) — ignored");
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (link.CandidateUpdates >= MaxCandidateUpdatesPerPeer) return;
        if (link.LastCandidateTicks != 0
            && (now - link.LastCandidateTicks) < CandidateUpdateIntervalMs * Stopwatch.Frequency / 1000)
            return;
        link.LastCandidateTicks = now;
        link.CandidateUpdates++;

        link.ReflexiveEndpoint = reflexive;
        if (Verbose) Log($"{link.Label} public endpoint {reflexive}");
        RedistributeMesh();
    }

}
