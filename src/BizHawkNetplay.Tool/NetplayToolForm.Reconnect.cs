using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    // --- State used only by this file (everything shared stays in NetplayToolForm.cs) ---
    private int _reconnectPort = -1;           // controller port waiting to be refilled
    private long _reconnectStartedStamp;
    private Thread? _reconnectThread;

    // ------------------------------------------------------------------ reconnect

    /// <summary>
    /// A joiner left cleanly (Bye) in a 3+ player session: retire its link, vacate its seat, and
    /// rebuild the survivors onto one baseline with the seat empty from frame 0. The rebuild is
    /// the same authoritative-state flow a settings change uses — a vacate must never be applied
    /// mid-timeline, because peers hear about it at different frames and the frames in between
    /// would disagree about the leaver's input with nobody left to send the correction (see
    /// InputPipeline.Vacate). Caller has already checked role, player count, and recovery phase.
    /// </summary>
    private void OnPeerLeftGracefully(PeerLink link)
    {
        int port = link.RemotePort;
        _peers.Remove(link);
        // Same retirement OnPeerLinkLost performs, for the same reasons (KI-4: a writer left
        // spinning on its signal forever).
        _retiredLinks.Add(link);
        link.WriterRunning = false;
        try { link.OutboundSignal.Set(); } catch { }
        try { link.Tcp?.Close(); } catch { }
        if (link.ControlStream != null)
        {
            try { link.ControlStream.Dispose(); } catch { }
            _mesh?.CloseControl(link.UdpEndpoint);
        }

        MarkSeatVacated(port);
        RedistributeMesh(); // forget the leaver's endpoints; survivors get corrected routes
        ConnLog($"{link.Label} left the session — their seat stays empty and play continues with " +
                $"{1 + _peers.Count} of {_playerCount} players.", Color.DarkSlateBlue);

        // Tell every survivor BEFORE the rebuild traffic. The per-link writer preserves order, so
        // each one holds the vacate when the new baseline lands and rebuilds with the seat empty.
        var vacatedBody = ControlMessageCodec.EncodeSeatVacated(CurrentGeneration, port);
        foreach (var survivor in _peers)
            QueueControl(survivor, ControlMessageType.SeatVacated, vacatedBody);

        // A deliberate reconfiguration, not a desync — it must not spend the resync budget.
        ShipAuthoritativeState($"P{port + 1} left", isSettingsChange: true);
    }

    /// <summary>
    /// The rejoin wait expired with survivors still standing: vacate the seat and resume them,
    /// instead of ending a session that two or three people are still in. The survivors have been
    /// frozen since the drop, holding this generation's BEGIN and waiting for its state — so the
    /// held baseline ships now, preceded by the vacate, and the ordinary applied/resume barrier
    /// releases everyone. Ending remains the outcome when nobody is left to continue with.
    /// </summary>
    private void VacateSeatAfterRejoinTimeout(int port)
    {
        if (!_phase.IsActive || !_isHost || !_phase.AwaitingRejoin) return;
        if (_peers.Count == 0 || _playerCount <= 2)
        {
            EndSession("no rejoin within the timeout");
            return;
        }
        var state = _reconnectState;
        var generation = _reconnectGeneration;
        if (state == null || !generation.IsValid || generation != CurrentGeneration)
        {
            EndSession("no rejoin within the timeout");
            return;
        }

        MarkSeatVacated(port); // the frozen driver stops watching the port; safe at frame 0
        _phase.EndAwaitingRejoin(); // the accept loop exits on this; IsRebuilding keeps frames held
        _reconnectPort = -1;
        _reconnectState = null;
        _reconnectGeneration = default;
        _reconnectThread = null;
        RedistributeMesh();
        ConnLog($"P{port + 1} did not return within {ReconnectTimeoutSeconds:F0}s — their seat is " +
                $"now empty and play continues with {1 + _peers.Count} of {_playerCount} players.",
            Color.DarkOrange);

        int attempt = CurrentConnectionAttempt;
        var vacatedBody = ControlMessageCodec.EncodeSeatVacated(generation, port);
        var stateBody = ControlMessageCodec.EncodeStatePayload(generation, state);
        foreach (var survivor in _peers)
        {
            QueueControl(survivor, ControlMessageType.SeatVacated, vacatedBody);
            GraceForStateTransfer(survivor, state.Length);
            survivor.AwaitingAppliedEpoch = generation.Epoch;
            Interlocked.Exchange(ref survivor.AppliedDeadlineTicks, StateApplyDeadlineTicks(state.Length));
            if (!QueueControl(survivor, ControlMessageType.Resync, stateBody, ok =>
                {
                    if (!ok) BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt) && _phase.IsActive
                            && CurrentGeneration == generation)
                            EndSession("post-timeout resync transfer failed");
                    });
                }))
            {
                EndSession("post-timeout resync transfer could not be queued");
                return;
            }
        }
    }

    /// <summary>
    /// Host: a joiner says a mesh leg went dead mid-session. If both of this host's own legs to
    /// the pair are proven, carry the pair — the same rescue the lobby installs for a leg that
    /// never opened, arriving live instead. The verdict is Core's (<see cref="RelayFailover"/>);
    /// this supplies the transport measurements and executes it. A pair, once installed, stays
    /// for the session (anti-oscillation — see the policy's remarks), and the 8-second watchdog
    /// remains the backstop for the legs no relay can reach.
    /// </summary>
    private void OnInputOutage(PeerLink link, SessionGeneration generation, int silentPort)
    {
        var mesh = _mesh;
        var silentPeer = _peers.Find(p => p.RemotePort == silentPort);
        var verdict = RelayFailover.Judge(
            _isHost, _phase.IsActive, generation == CurrentGeneration,
            link.RemotePort, silentPort, _playerCount,
            _vacatedPorts, _relayPairs,
            reporterLegAlive: mesh != null && LinkHasLiveMeshPath(mesh, link),
            silentLegAlive: mesh != null && silentPeer != null && LinkHasLiveMeshPath(mesh, silentPeer));

        switch (verdict)
        {
            case RelayFailoverVerdict.Install:
                _relayPairs.Add(Pair(link.RemotePort, silentPort));
                RefreshRelayRoutes();
                double routeMs = RelayedRouteRttMs(mesh);
                ConnLog($"the direct path between {link.Label} and P{silentPort + 1} died " +
                        $"mid-session — relaying that leg through this host from now on" +
                        (routeMs > 0 ? $" (~{routeMs:F0}ms round trip)" : "") +
                        ". If stalls appear, raise the input delay to cover the relayed route.",
                    Color.DarkOrange);
                break;
            case RelayFailoverVerdict.NoHostLeg:
                // Nothing to rescue with: a relay to that pair runs over the very leg that is
                // down. Say so once; the watchdog owns the outcome.
                ConnLog($"{link.Label} reports no input from P{silentPort + 1}, but this host has " +
                        "no proven UDP path to carry a relay over — if the leg does not recover, " +
                        "the session will end on the input watchdog.", Color.Firebrick);
                break;
            // AlreadyCarried is the other end of a leg failed over moments ago — expected, quiet.
            // Refuse is a stale or malformed report — also quiet.
        }
    }

    /// <summary>
    /// A peer's control link dropped unexpectedly (not a clean Bye). The host holds the session
    /// open and waits for it to rejoin into the same port; a joiner that lost the host just ends
    /// (the host is the hub — the user rejoins with the Join button). One drop at a time.
    /// </summary>
    private void OnPeerLinkLost(PeerLink link, string why)
    {
        if (!_phase.IsActive) return;
        if (!_peers.Contains(link)) return; // reader/writer can both report the same broken link

        // A punched link has no TCP to re-accept a rejoin on, so the 60-second hold cannot help
        // it. That is a reason not to WAIT for them — not a reason to end a session three other
        // people are still in. At 3+ players the seat is vacated and play continues, exactly as a
        // graceful leave does; only at 2 players (where continuing means playing alone) or as a
        // joiner losing the host does the session end.
        if (link.Tcp == null)
        {
            if (_isHost && _playerCount > 2 && _peers.Count >= 2
                && !_phase.IsRebuilding && !_phase.AwaitingRejoin)
            {
                ConnLog($"{link.Label}'s punched link dropped ({why}) — a punched link has no rejoin " +
                        "path, so their seat is vacated and play continues.", Color.DarkOrange);
                OnPeerLeftGracefully(link);
                return;
            }
            EndSession($"lost connection to {link.Label}: {why} (punched link — no TCP rejoin path)");
            return;
        }

        // What losing a peer means in each recovery phase is decided in Core: RecoveryPolicy.
        switch (RecoveryPolicy.OnPeerLost(_isHost, _phase.IsRebuilding, _phase.AwaitingRejoin))
        {
            case PeerLossAction.EndSessionJoinerLostHost:
                EndSession($"lost connection to {link.Label}: {why} — click Join to reconnect");
                return;
            case PeerLossAction.EndSessionDropDuringResync:
                // Some survivors may still be on the prior epoch, so advancing again would make
                // the reconnect BEGIN skip an epoch for them. End cleanly instead of creating an
                // ambiguous nested state barrier.
                EndSession($"{link.Label} dropped during resync: {why}");
                return;
            case PeerLossAction.EndSessionSecondDropDuringReconnect:
                EndSession($"a second peer ({link.Label}) dropped during a reconnect: {why}");
                return;
        }

        _phase.BeginAwaitingRejoin();
        _reconnectPort = link.RemotePort;
        _reconnectStartedStamp = MonotonicNow();
        RefreshLiveSettingsUi(); // no settings change while a seat is empty and waiting

        _peers.Remove(link);
        // The link leaves _peers here, so TeardownNetwork's reaping would never see it again —
        // shut its writer down now or the thread spins on OutboundSignal forever (KI-4). Retiring
        // it rather than dropping it on the floor is what lets teardown still join its threads and
        // dispose its signal; a rejoin arrives as a NEW PeerLink, so this one is finished either way.
        _retiredLinks.Add(link);
        link.WriterRunning = false;
        try { link.OutboundSignal.Set(); } catch { }
        try { link.Tcp?.Close(); } catch { }
        try
        {
            // Capture the boundary immediately and advance exactly once. Survivors receive BEGIN
            // now, so they freeze instead of timing out their UDP input while the host waits up to
            // a minute for the missing player to return.
            var state = _adapter!.ExportState();
            var generation = AdvanceGeneration();
            _reconnectState = state;
            _reconnectGeneration = generation;
            _phase.BeginRebuild(RebuildReason.PeerLoss);
            RebuildDriver();
            RedistributeMesh(); // remove the dead endpoint from host and survivor route tables

            // The session's parameters are unchanged by someone dropping — but they ride along all
            // the same, so a survivor always rebuilds under what the host has just stated.
            var begin = ControlMessageCodec.EncodeResyncBegin(generation, state.Length,
                _sessionDelay, _mode, waitSeconds: (int)ReconnectTimeoutSeconds);
            foreach (var survivor in _peers)
            {
                if (!QueueControl(survivor, ControlMessageType.ResyncBegin, begin))
                {
                    // Nearly always the rest of a shared outage arriving: peers behind one
                    // connection drop together, so the survivor we are freezing is already gone.
                    // Name it and say which of QueueControl's two refusals this was — the old
                    // wording reported the step that failed and left the cause to be guessed at.
                    EndSession(survivor.WriterRunning
                        ? $"could not hold the session for a rejoin — {survivor.Label}'s control " +
                          "channel is backed up"
                        : $"could not hold the session for a rejoin — {survivor.Label} is gone too " +
                          "(peers sharing one connection drop together; only one seat at a time " +
                          "can be held open)");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            EndSession("could not establish reconnect boundary: " + ex.Message);
            return;
        }

        ConnLog($"{link.Label} dropped ({why}) — holding the session; waiting up to " +
            $"{ReconnectTimeoutSeconds:F0}s for a rejoin on TCP {_hostTcpPort}…", Color.DarkOrange);
        Status($"P{_reconnectPort + 1} dropped — waiting to rejoin…", Color.DarkOrange);

        int attempt = CurrentConnectionAttempt;
        _reconnectThread = new Thread(() => ReconnectAcceptLoop(_reconnectPort, attempt))
        { IsBackground = true, Name = "BizHawkNetplay-reconnect" };
        _reconnectThread.Start();
    }

    /// <summary>All candidate UDP endpoints of the given links (LAN plus reflexive/public where
    /// known), optionally excluding one — the peer set the mesh sends to and accepts from. The mesh
    /// tolerates dead candidates, so including both lets the same session work on LAN and over NAT.</summary>
    private static List<PeerRoute> RoutesExcept(IReadOnlyList<PeerLink> links, PeerLink? except)
    {
        var routes = new List<PeerRoute>();
        foreach (var l in links)
        {
            if (ReferenceEquals(l, except)) continue;
            var candidates = new List<IPEndPoint> { l.UdpEndpoint };
            if (l.ReflexiveEndpoint != null) candidates.Add(l.ReflexiveEndpoint);
            routes.Add(new PeerRoute(l.RemotePort, candidates));
        }
        return routes;
    }

    /// <summary>
    /// Host: this session's mesh tokens, one per seat, minted on first use.
    ///
    /// Keyed by controller port rather than by player because a token identifies the SEAT: when
    /// someone drops and rejoins they come back on a new address, which is precisely the moment
    /// their packets are unrecognisable and the token is what makes them placeable again. Cleared
    /// when the session ends, so tokens never outlive the channel that authenticated them.
    /// </summary>
    private readonly Dictionary<int, byte[]> _portTokens = new();

    private byte[] TokenForPort(int port)
    {
        if (!_portTokens.TryGetValue(port, out var token))
            _portTokens[port] = token = SessionAuth.NewMeshToken();
        return token;
    }

    /// <summary>
    /// The session's full pair-key table, minted once and kept for as long as the session lives.
    ///
    /// Minted for every seat the session could hold rather than for the seats currently filled: a
    /// rejoining player and a seat renumbering both have to find their keys already there, and a
    /// key minted late is a key some peer was told about and another was not.
    /// </summary>
    private MeshPairKeyring? _pairKeys;

    private MeshPairKeyring PairKeys(int players)
        => _pairKeys ??= MeshPairKeyring.Mint(Math.Max(players, HandshakeCodec.MaxPlayers));

    /// <summary>Host: the mesh identity handed to the peer in <paramref name="port"/> — its own
    /// token plus every other seat's, including seat 0 (us), which is never listed as a route, and
    /// the pair keys for the pairs this seat is in and no others.</summary>
    private MeshTokens TokensFor(int port, int players)
    {
        var peers = new Dictionary<int, byte[]>();
        for (int p = 0; p < players; p++)
            if (p != port) peers[p] = TokenForPort(p);
        // Seat 0 is us, and only us: the host keeps the WHOLE table because it is the only node that
        // relays, and relaying means re-tagging a datagram for a destination the author could not
        // reach. Every other seat gets the narrow view — that narrowing is the security property.
        var pairs = port == 0 ? PairKeys(players) : PairKeys(players).For(port);
        return new MeshTokens(TokenForPort(port), peers, pairs);
    }

    /// <summary>Host: point our mesh at every currently-connected joiner's candidate endpoints.</summary>
    private void UpdateMeshPeers()
    {
        if (_mesh == null) return;
        try { _mesh.SetPeerRoutes(RoutesExcept(_peers, null)); } catch { }
        RefreshRelayRoutes();
    }

    /// <summary>
    /// Rebuild the relay's physical routes from the logical ports it was installed for.
    ///
    /// Relay routes used to be a snapshot taken once in the lobby, and nothing refreshed them. A peer
    /// that dropped and rejoined comes back on a NEW endpoint — that is the whole reason
    /// <see cref="RedistributeMesh"/> exists — so the relay was left aimed at an address nobody was
    /// listening on, and a peer that left for good was still being sent copies. Keeping the decision
    /// as PORT numbers and re-resolving them here means the relay follows the same candidate updates
    /// everything else does.
    /// </summary>
    private void RefreshRelayRoutes()
    {
        if (_mesh == null) return;
        if (_relayPairs.Count == 0)
        {
            try { _mesh.SetRelayRoutes([]); _mesh.SetRelayPairs(null); } catch { }
            return;
        }
        // Every port that appears in a carried pair needs a resolvable route — input flows both
        // ways along a broken edge. The pair filter is what keeps a player's OTHER, working legs
        // off the relay.
        var destinations = new HashSet<int>();
        foreach (var (a, b) in _relayPairs) { destinations.Add(a); destinations.Add(b); }
        var live = _peers.FindAll(p => destinations.Contains(p.RemotePort));
        try
        {
            _mesh.SetRelayRoutes(RoutesExcept(live, null));
            _mesh.SetRelayPairs(_relayPairs);
        }
        catch { }
    }

    /// <summary>Host: re-point our own mesh and re-send each joiner its candidate peer list (used
    /// whenever the candidate set changes — a reflexive candidate arrives, or someone rejoins).</summary>
    private void RedistributeMesh()
    {
        UpdateMeshPeers();
        foreach (var l in _peers)
        {
            QueueControl(l, ControlMessageType.PeerList,
                HandshakeCodec.EncodeRoutes(RoutesExcept(_peers, l)));
        }
    }

    /// <summary>Joiner: point our mesh at the host (peer 0) plus every other joiner we've been told about.</summary>
    private void ApplyJoinerMesh()
    {
        if (_mesh == null || _peers.Count == 0) return;
        var routes = new List<PeerRoute>
        {
            new(_peers[0].RemotePort, new[] { _peers[0].UdpEndpoint }) // the host
        };
        routes.AddRange(_meshOthers);
        try { _mesh.SetPeerRoutes(routes); } catch { }
    }

    /// <summary>
    /// Host reconnect listener (background thread): reopen the TCP port and wait for the dropped
    /// player to reconnect. Re-greet — which re-validates ROM/core/layout still match — then hand
    /// off to the UI thread to welcome them back. Gives up (ends the session) after the timeout.
    /// </summary>
    private void ReconnectAcceptLoop(int freedPort, int attempt)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, _hostTcpPort);
            listener.Start();
            while (_phase.IsActive && _phase.AwaitingRejoin && IsConnectionAttemptCurrent(attempt))
            {
                if (MonotonicElapsedSeconds(_reconnectStartedStamp) > ReconnectTimeoutSeconds)
                {
                    BeginInvokeUi(() =>
                    {
                        // With survivors still standing the seat is vacated and play resumes; the
                        // session only ends when nobody is left to continue with (or at 2 players,
                        // where "continue" would mean playing alone).
                        if (IsConnectionAttemptCurrent(attempt) && _phase.AwaitingRejoin)
                            VacateSeatAfterRejoinTimeout(freedPort);
                    });
                    return;
                }
                if (!listener.Pending()) { Thread.Sleep(100); continue; }

                var tcp = listener.AcceptTcpClient();
                if (!IsConnectionAttemptCurrent(attempt)) { try { tcp.Close(); } catch { } return; }
                if (!TrackHandshakeClient(tcp, attempt)) { try { tcp.Close(); } catch { } return; }
                try { tcp.NoDelay = true; } catch { }
                try { tcp.ReceiveTimeout = HandshakeReceiveTimeoutMs; } catch { } // a silent rejoiner can't wedge the wait
                var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
                var channel = new ControlChannel(tcp.GetStream());
                try
                {
                    double remainingSeconds = ReconnectTimeoutSeconds
                        - MonotonicElapsedSeconds(_reconnectStartedStamp);
                    int greetDeadlineMs = Math.Max(1, Math.Min(HandshakeReceiveTimeoutMs,
                        (int)Math.Ceiling(remainingSeconds * 1000.0)));
                    var greet = WithAbsoluteSocketDeadline(tcp, greetDeadlineMs,
                        () => Handshake.HostGreet(channel, _hostIdentity!, _hostPrefs!, _hostUdpPort));
                    if (_mode == SyncMode.Rollback
                        && (!greet.Prefs.WantRollback
                            || greet.Id.MaxRollbackDepth < ProbeResult.RollbackDepthThreshold))
                        throw new HandshakeException(
                            "rejoining peer no longer reports the rollback capability required by this session");
                    try { tcp.ReceiveTimeout = 0; } catch { } // handshake done: restore blocking reads
                    var udpEp = new IPEndPoint(remoteIp, greet.UdpPort);
                    BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt))
                            CompleteReconnect(tcp, channel, remoteIp, udpEp, freedPort, greet, attempt);
                        else { UntrackHandshakeClient(tcp); try { tcp.Close(); } catch { } }
                    });
                    return; // one rejoin fills the slot
                }
                catch (Exception ex)
                {
                    // Rejected (e.g. wrong ROM/core) — refuse this one and keep waiting for a valid rejoin.
                    UiConnLog($"rejected a rejoin attempt: {ex.Message}", Color.Firebrick);
                    UntrackHandshakeClient(tcp);
                    try { tcp.Close(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt) && _phase.AwaitingRejoin)
                    EndSession("reconnect listener failed: " + ex.Message);
            });
        }
        finally { try { listener?.Stop(); } catch { } }
    }

    /// <summary>
    /// UI thread: capture the current authoritative state, then hand the potentially blocking
    /// welcome/state transfer to a background thread. Simulation is already held for reconnect.
    /// </summary>
    private void CompleteReconnect(TcpClient tcp, ControlChannel channel, IPAddress remoteIp,
        IPEndPoint udpEp, int freedPort, Handshake.JoinerGreeting greet, int attempt)
    {
        if (!IsConnectionAttemptCurrent(attempt) || !_phase.IsActive || !_phase.AwaitingRejoin)
        { UntrackHandshakeClient(tcp); try { tcp.Close(); } catch { } return; }
        try
        {
            _greetingTcp = tcp; // teardown can abort the background state/barrier transfer
            var state = _reconnectState
                ?? throw new InvalidOperationException("reconnect baseline is unavailable");
            var generation = _reconnectGeneration;
            if (!generation.IsValid || generation != CurrentGeneration)
                throw new InvalidOperationException("reconnect generation is no longer current");
            var meshPeers = RoutesExcept(_peers, null);
            // Minted here rather than on the sending thread below: the seat's token is the same one
            // the survivors already hold, and _portTokens stays single-threaded that way.
            var rejoinTokens = TokensFor(freedPort, _playerCount);
            // Snapshotted on the UI thread for the same reason as the tokens: the rejoiner must
            // build its driver with the already-empty seats vacated, or its own watchdogs read the
            // silence there as a broken link.
            var rejoinVacated = new List<int>(_vacatedPorts);
            Status($"P{freedPort + 1} rejoined — sending epoch {generation.Epoch} " +
                $"({state.Length / 1024}KiB)…", Color.DarkOrange);

            // The rejoiner's mesh peers = every current survivor (it reaches the host directly). It
            // adopts this state + mesh via Welcome and rebuilds fresh on its own side.
            new Thread(() =>
            {
                try
                {
                    ConfigureStateTransferTimeouts(tcp, state.Length);
                    Handshake.HostSendWelcome(channel, freedPort, _playerCount, _sessionDelay, _mode, state,
                        generation, meshPeers, rejoinTokens, rejoinVacated, _checksumInterval);
                    Handshake.HostWaitReady(channel, generation);
                    try { tcp.ReceiveTimeout = 0; tcp.SendTimeout = 0; } catch { }
                    BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt))
                            FinishReconnect(tcp, channel, remoteIp, udpEp, freedPort, state,
                                generation, greet, attempt);
                        else { UntrackHandshakeClient(tcp); try { tcp.Close(); } catch { } }
                    });
                }
                catch (Exception ex)
                {
                    try { tcp.Close(); } catch { }
                    UntrackHandshakeClient(tcp);
                    BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt) && _phase.IsActive)
                            EndSession("reconnect state transfer failed: " + ex.Message);
                    });
                }
            }) { IsBackground = true, Name = "BizHawkNetplay-reconnect-state" }.Start();
        }
        catch (Exception ex) { EndSession("reconnect failed: " + ex.Message); }
    }

    private void FinishReconnect(TcpClient tcp, ControlChannel channel, IPAddress remoteIp,
        IPEndPoint udpEp, int freedPort, byte[] state, SessionGeneration generation,
        Handshake.JoinerGreeting greet, int attempt)
    {
        if (!IsConnectionAttemptCurrent(attempt) || !_phase.IsActive || !_phase.AwaitingRejoin)
        { UntrackHandshakeClient(tcp); try { tcp.Close(); } catch { } return; }
        if (generation != CurrentGeneration)
        { UntrackHandshakeClient(tcp); try { tcp.Close(); } catch { } return; }
        try
        {
            // Same credibility rule as the lobby greets: the announced endpoint is handed to every
            // other player and probed by all of them, so believe it only if it matches where the
            // rejoiner actually reached us from.
            bool reflexiveCredible = ReflexiveCandidate.IsCredible(greet.Reflexive, remoteIp);
            if (greet.Reflexive != null && !reflexiveCredible)
                Log($"ignoring the public endpoint the rejoiner announced ({greet.Reflexive}) — " +
                    $"it is not the address they reached us from ({remoteIp})");
            var link = new PeerLink
            {
                Tcp = tcp, Control = channel, RemotePort = freedPort, Greeting = greet,
                UdpEndpoint = udpEp, ReflexiveEndpoint = reflexiveCredible ? greet.Reflexive : null,
                Label = $"P{freedPort + 1} ({remoteIp})",
            };
            // Bring each survivor up to date: refresh its mesh with the rejoiner's endpoint, then
            // resync it to the same state. Do not release the rejoiner with GO until those queued
            // state writes complete, otherwise the first peer can run while another is still loading.
            var allPeers = new List<PeerLink>(_peers) { link };
            var survivors = new List<PeerLink>(_peers);
            _pendingReconnectLink = link;
            _pendingReconnectStateLength = state.Length;
            _pendingReconnectGeneration = generation;
            var stateBody = ControlMessageCodec.EncodeStatePayload(generation, state);
            if (survivors.Count == 0)
            {
                ReleaseReconnectedPeer(link, state.Length, generation);
                return;
            }
            foreach (var survivor in survivors)
            {
                GraceForStateTransfer(survivor, state.Length); // same leash: a big frame is inbound
                survivor.AwaitingAppliedEpoch = generation.Epoch;
                Interlocked.Exchange(ref survivor.AppliedDeadlineTicks, StateApplyDeadlineTicks(state.Length));
                QueueControl(survivor, ControlMessageType.PeerList,
                    HandshakeCodec.EncodeRoutes(RoutesExcept(allPeers, survivor)));
                if (!QueueControl(survivor, ControlMessageType.Resync, stateBody, ok =>
                    {
                        if (!ok) BeginInvokeUi(() =>
                        {
                            if (IsConnectionAttemptCurrent(attempt) && _phase.IsActive
                                && CurrentGeneration == generation)
                                EndSession("reconnect resync transfer failed");
                        });
                    }))
                {
                    EndSession("reconnect resync transfer could not be queued");
                    return;
                }
            }
        }
        catch (Exception ex) { EndSession("reconnect failed: " + ex.Message); }
    }

    private void ReleaseReconnectedPeer(PeerLink link, int stateLength, SessionGeneration generation)
    {
        if (generation != CurrentGeneration || !_phase.TryQueueResume()) return;
        int attempt = CurrentConnectionAttempt;

        // Survivors leave their resync wait only after all of them — and the rejoiner waiting in
        // READY/GO — have applied this generation. Flush their RESUME markers first, then release
        // the rejoiner's handshake off-thread before its live reader starts consuming the channel.
        QueueResyncResumeToPeers(generation, resumesOk => BeginInvokeUi(() =>
        {
            if (!IsConnectionAttemptCurrent(attempt) || !_phase.IsActive || !_phase.AwaitingRejoin)
            {
                UntrackHandshakeClient(link.Tcp);
                try { link.Tcp?.Close(); } catch { }
                return;
            }
            if (!resumesOk) { EndSession("reconnect resume transfer failed"); return; }

            new Thread(() =>
            {
                try
                {
                    Handshake.HostSendGo(link.Control, generation);
                    BeginInvokeUi(() =>
                    {
                        if (!IsConnectionAttemptCurrent(attempt) || !_phase.IsActive
                            || !_phase.AwaitingRejoin || generation != CurrentGeneration)
                        { UntrackHandshakeClient(link.Tcp); try { link.Tcp?.Close(); } catch { } return; }
                        _peers.Add(link);
                        ReapRetiredLinks();   // the dropped seat's old link is finished for good now
                        UntrackHandshakeClient(link.Tcp);
                        _greetingTcp = null;
                        _reconnectState = null;
                        _reconnectGeneration = default;
                        _pendingReconnectLink = null;
                        _pendingReconnectStateLength = 0;
                        _pendingReconnectGeneration = default;
                        UpdateMeshPeers();
                        StartPeerIo(link);
                        _driver?.ResetRemoteInputLiveness();
                        _phase.EndAwaitingRejoin();
                        _phase.EndRebuild();
                        _reconnectPort = -1;
                        _resyncCount = 0;
                        RebaseFrameSchedule();
                        RefreshLiveSettingsUi();
                        ConnLog($"{link.Label} reconnected — epoch {generation.Epoch}, " +
                            $"{stateLength / 1024}KiB baseline synchronized; resuming", Color.DarkGreen);
                        Status($"reconnected P{link.RemotePort + 1} — resuming", Color.Green);
                    });
                }
                catch (Exception ex)
                {
                    try { link.Tcp?.Close(); } catch { }
                    UntrackHandshakeClient(link.Tcp);
                    BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt) && _phase.IsActive)
                            EndSession("reconnect GO failed: " + ex.Message);
                    });
                }
            }) { IsBackground = true, Name = "BizHawkNetplay-reconnect-go" }.Start();
        }));
    }

    private void FailSession(string reason)
    {
        bool wasActive = _phase.IsActive;
        _pendingJoinIp = null; // a failed connect shouldn't land in the recent-IPs list
        ConnLog("connection failed: " + reason, Color.Firebrick);
        StopFramePacing();
        _phase.Stop();
        try { if (_timerResRaised) { timeEndPeriod(1); _timerResRaised = false; } } catch { }
        TeardownNetwork();
        if (!wasActive) RestorePreJoinState();
        // Same lines EndSession clears, for the same reasons: a failed attempt still minted seat
        // tokens, and tokens must not outlive the control channel that authenticated them. Leaving
        // them behind let the next session reuse them via TokenForPort.
        _relayPairs.Clear();
        _portTokens.Clear();
        _pairKeys = null;      // and neither may the keys that made input provable
        _vacatedPorts.Clear();
        _vacatedCount = 0;
        try { _adapter?.DisableAudio(); } catch { } // restore EmuHawk's normal audio wiring
        ApplySessionHostOwnership(false);
        RestorePauseState(); // undo the freeze from OnGo, unless it was already paused before it
        ResetPunchUi();
        SetBusy(false);
        Status("Idle.", Color.DimGray);
    }

    /// <summary>
    /// Nothing to tear down: no session, no lobby, no in-flight join, no held ownership.
    ///
    /// Ownership is in this list because it is not implied by owning a socket — it is taken at the
    /// first pause, before the listener or transport exists, so leaving it out could strand the
    /// frame advance blocked with nothing left to explain why. Shared by EndSession's fast path and
    /// by AskSaveChanges' should-I-even-prompt check, so the two can never disagree about what
    /// counts as "something is going on".
    /// </summary>
    private bool SessionMachineryIdle =>
        !_phase.IsActive && _listener == null && _joiningTcp == null && _greetingTcp == null
        && _peers.Count == 0 && _retiredLinks.Count == 0 && !_hostOwnershipHeld && !_pausedByUs
        && !HasHandshakeClients() && _transport == null && _preJoinRestoreState == null;

    /// <summary>
    /// Release retired links whose threads have finished. Teardown reaps whatever is left, but a
    /// session that cycles through drops and rejoins used to accumulate one dead link per cycle
    /// until it ended — a closed TcpClient, an undisposed kernel event, two dead Thread objects —
    /// which is exactly the leak the retired list was introduced to close, one level up. Links
    /// whose writer is still flushing are left for teardown; this must never block the UI thread.
    /// </summary>
    private void ReapRetiredLinks()
    {
        for (int i = _retiredLinks.Count - 1; i >= 0; i--)
        {
            var link = _retiredLinks[i];
            if (link.Reader is { IsAlive: true } || link.Writer is { IsAlive: true }) continue;
            _retiredLinks.RemoveAt(i);
            try { link.OutboundSignal.Dispose(); } catch { }
        }
    }

    private void EndSession(string reason)
    {
        if (SessionMachineryIdle) { SetBusy(false); return; }
        bool wasActive = _phase.IsActive;
        StopFramePacing();

        // Preserve a clean "friend left" signal without doing socket I/O on the UI thread. Give
        // the per-peer writers one very short opportunity; a state transfer or dead link is closed
        // immediately after the deadline instead of making Disconnect appear frozen.
        if (_phase.IsActive && _peers.Count > 0)
        {
            var bye = new CountdownEvent(_peers.Count);
            foreach (var link in _peers)
                QueueControl(link, ControlMessageType.Bye, [], _ => { try { bye.Signal(); } catch { } });
            try { bye.Wait(50); } catch { }
            try { bye.Dispose(); } catch { }
        }

        _phase.Stop();
        _simUnresponsive = false; _simUnresponsiveCheck.Checked = false; // clear the diagnostic
        try { if (_timerResRaised) { timeEndPeriod(1); _timerResRaised = false; } } catch { }

        TeardownNetwork();
        if (!wasActive) RestorePreJoinState();

        try { _adapter?.DisableAudio(); } catch { } // restore EmuHawk's normal audio wiring
        ApplySessionHostOwnership(false); // restore focus prefs, frame-advance block and rewind
        // Only unpause if we were the ones who paused it. Someone who paused EmuHawk deliberately
        // and then joined a session used to get it running again when the session ended.
        RestorePauseState();
        lock (_hashLock) { _checksums.Clear(); }

        StopDonorTimeout();  // no majority ask outlives the session that sent it
        _awaitingDonorPort = -1;
        _relayPairs.Clear(); // a fresh session re-measures; nothing from the last one should carry
        _portTokens.Clear(); // tokens must not outlive the control channel that authenticated them
        _pairKeys = null;    // and neither may the keys that made input provable
        _vacatedPorts.Clear(); // vacated seats belong to the session that lost them
        _vacatedCount = 0;
        _netcodeLabel.Text = "Netcode in use: —";
        _netcodeLabel.ForeColor = Color.DimGray;
        SetLobbyPhase("", Color.DimGray); // back to "Not connected", and stop flashing
        RefreshPlayersList(); // session inactive now → clears the list
        ResetPunchUi();

        ConnLog("session ended: " + reason, Color.DimGray);
        _logFile?.Write($"=== session end — {reason} ==={Environment.NewLine}");
        _logFile?.Flush();
        Status("Idle.", Color.DimGray);
        SetBusy(false);
    }

    private void RestorePreJoinState()
    {
        var state = _preJoinRestoreState;
        _preJoinRestoreState = null;
        if (state == null || _adapter == null) return;
        try
        {
            _adapter.ImportState(state);
            Log("restored the pre-join emulator state after the start barrier was canceled");
        }
        catch (Exception ex) { Log("(warning) could not restore the pre-join state: " + ex.Message); }
    }

    private void TeardownNetwork()
    {
        InvalidateConnectionAttempt();
        // Remove any UPnP forward we added, off-thread (it's a router round-trip).
        var upnp = _upnpMapping;
        _upnpMapping = null;
        if (upnp != null)
            new Thread(() => { try { upnp.Remove(TimeSpan.FromSeconds(2)); } catch { } })
            { IsBackground = true, Name = "BizHawkNetplay-upnp" }.Start();

        // Stop any in-flight reconnect wait first; its loop exits once these flags clear.
        _phase.EndAwaitingRejoin();
        _reconnectState = null;
        _reconnectGeneration = default;
        _pendingReconnectLink = null;
        _pendingReconnectStateLength = 0;
        _pendingReconnectGeneration = default;
        var reconnect = _reconnectThread;
        _reconnectThread = null;
        _reconnectPort = -1;

        try { _listener?.Stop(); } catch { }
        _listener = null;
        try { _joiningTcp?.Close(); } catch { } // unblock a join connect that's still dialing
        _joiningTcp = null;
        try { _greetingTcp?.Close(); } catch { } // abort a joiner we're blocked greeting (Disconnect mid-handshake)
        _greetingTcp = null;

        _lifecycle.RejectAndCloseAll(); // refuse new handshake sockets, close all in-flight ones

        var peers = new List<PeerLink>(_peers);
        _peers.Clear();
        peers.AddRange(_retiredLinks);   // reaped on the same terms; see _retiredLinks
        _retiredLinks.Clear();
        foreach (var link in peers)
        {
            link.WriterRunning = false;
            try { link.OutboundSignal.Set(); } catch { }
        }
        foreach (var link in peers) { try { link.Tcp?.Close(); } catch { } }

        try { (_transport as IDisposable)?.Dispose(); } catch { }
        try { _driver?.Dispose(); } catch { } // release the rollback ring's savestates (into the pool)
        // AFTER the dispose above, which is what refills the pool. The ring's worth of savestate
        // buffers is real memory — on a heavy core, hundreds of MiB of large object heap — so hand
        // it back rather than holding it across a long idle between sessions.
        try { _adapter?.ClearStatePool(); } catch { }
        _transport = null; _mesh = null;
        // The endpoint belonged to that socket; the next session binds a new one.
        _localReflexive = null;
        try { _reflexiveKnown.Reset(); } catch { }
        _lobbyPunchTargets.Clear();
        _punchDoorOpen = false;
        while (_punchAdmissions.TryDequeue(out var admission))
        {
            try { admission.Control.Dispose(); } catch { }
        }
        _driver = null;
        _sessionDriverPrepared = false;

        foreach (var link in peers)
        {
            var reader = link.Reader;
            if (reader != null && reader.IsAlive && reader != Thread.CurrentThread)
            {
                try { reader.Join(300); } catch { }
            }
            var writer = link.Writer;
            if (writer != null && writer.IsAlive && writer != Thread.CurrentThread)
            {
                try { writer.Join(300); } catch { }
            }
            try { link.OutboundSignal.Dispose(); } catch { }
        }

        if (reconnect != null && reconnect.IsAlive && reconnect != Thread.CurrentThread)
        {
            try { reconnect.Join(400); } catch { } // it polls the flags every 100ms, so this returns quickly
        }
    }

}
