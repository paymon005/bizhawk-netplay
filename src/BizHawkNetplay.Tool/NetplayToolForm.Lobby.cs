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
    // ------------------------------------------------------------------ start

    private void OnGo()
    {
        if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
        if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }

        StartLogFile(); // from here on there is something worth keeping a file of

        if (SessionHazardsBlockStart()) return; // active movie refuses; Lua only warns

        try
        {
            _adapter = new EmuHawkAdapter(APIs, _emulator, _statable, Config, MovieSession);
            _adapter.InputSourcePort = InputSourceFromCombo(); // read your normal pad, whatever port you're assigned
            if (!_adapter.VerifyDeterministicMode())
                Log("WARNING: core does not report deterministic emulation — desyncs are likely.");
            if (!_adapter.HasBindings)
                Log($"WARNING: input may not register — {_adapter.BindingDiagnostic}");

            _isHost = _hostRadio.Checked;
            int portCount = _adapter.PortCount; // controller ports the core exposes (N64 = 4, Genesis = 2…)
            if (_hostRadio.Checked && portCount < 2)
            {
                Log($"this core exposes only {portCount} controller port — configure at least 2 controllers to host netplay.");
                SetBusy(false); return;
            }
            // The host picks how many of those ports to actually fill (e.g. 2-player on an N64's 4);
            // the rest read neutral. Joiners take the count from the host's Welcome, so only the host
            // reads the box here. Clamp to what the core supports.
            int players = _hostRadio.Checked ? Math.Min(Math.Max(2, (int)_playersBox.Value), portCount) : portCount;
            if (_hostRadio.Checked && (int)_playersBox.Value > portCount)
            {
                // Belt-and-braces: RefreshPlayerLimit normally keeps the box at or below this, so
                // reaching here means the core changed under us. Never clamp silently — an
                // unexplained "waiting for 1 player(s)" after asking for 4 is the confusing case.
                // Name the declared count too when it is higher. A core that declares a port it
                // never populates (QuickNES declares three, the third empty) used to pass this cap
                // and then fail at frame 0 with every packet for that port refused on arrival —
                // which read as a lost UDP path rather than as an unusable controller.
                string declared = _adapter.DeclaredPortCount > portCount
                    ? $" (it declares {_adapter.DeclaredPortCount} but only {portCount} carry input — " +
                      "a core can name a port it never wires up; on NES try NesHawk rather than QuickNES)"
                    : "";
                ConnLog($"hosting {players} players, not {(int)_playersBox.Value} — this core exposes only " +
                        $"{portCount} usable controller port(s){declared}. Enable the core's " +
                        "multitap/adapter (Genesis: 4-Way Play or Team Player; SNES: multitap; " +
                        "NES: Four Score) for more.", Color.DarkOrange);
                RefreshPlayerLimit();
            }

            // Validate the join address BEFORE pausing — otherwise a typo'd IP leaves the emulator
            // frozen on the early return with no session to un-freeze it. The box takes either a
            // bare IP or "ip:port" (what a host usually reads out), and a port typed there wins
            // over the Port box — which we update so the UI still shows the port we're dialing.
            IPAddress? joinIp = null;
            if (!_hostRadio.Checked)
            {
                if (!HostAddress.TryParse(_ipBox.Text, (int)_portBox.Value, out joinIp, out int joinPort))
                {
                    ConnLog("Enter a valid host address — an IP (1.2.3.4) or IP:port (1.2.3.4:47800).",
                        Color.Firebrick);
                    SetBusy(false); return;
                }
                // Everything below the socket layer is IPv4 (the host binds IPAddress.Any, connect
                // codes pack 4 bytes) — say so plainly instead of failing later in a socket error.
                if (joinIp!.AddressFamily != AddressFamily.InterNetwork)
                {
                    ConnLog("IPv6 host addresses aren't supported — use the host's IPv4 address.", Color.Firebrick);
                    SetBusy(false); return;
                }
                if (joinPort != (int)_portBox.Value)
                {
                    Log($"using port {joinPort} from the host address (was {(int)_portBox.Value})");
                    _portBox.Value = joinPort; // read back below as the port we dial
                }
            }

            // Freeze the emulator NOW. Otherwise it keeps free-running between probing/exporting
            // its state and the peers arriving, so the sims start on different frames and desync
            // immediately. Paused here == the frame all peers resume from. (Probing below advances
            // frames invisibly and restores, so it must be paused first.)
            PauseForSession();

            // Netcode: Automatic prefers rollback but drops to lockstep if the probe fails; Rollback
            // forces it; Lockstep forces lockstep. We "want" rollback unless Lockstep is chosen, and
            // probe accordingly. The host's choice is authoritative for the session's mode.
            _netcodeChoice = (NetcodeChoice)_netcodeCombo.SelectedIndex;
            var prefs = LocalPreferences(_hostRadio.Checked);
            bool wantRollback = prefs.WantRollback;
            var id = BuildIdentity(_adapter, wantRollback);
            int port = (int)_portBox.Value;
            bool autoDelay = _hostRadio.Checked && _autoDelayCheck.Checked;
            int autoDelayMax = (int)_autoDelayMaxBox.Value;
            double lobbyFrameMs = FrameMs();
            _simLatencyMs = (int)_simLatencyBox.Value; // diagnostic artificial UDP delay for this session
            _upnpEnabled = _upnpCheck.Checked;         // capture on the UI thread for the host accept thread
            if (_simLatencyMs > 0)
                Log($"simulating {_simLatencyMs}ms one-way UDP latency (~{2 * _simLatencyMs}ms RTT) — diagnostic");

            int attempt = BeginConnectionAttempt();
            SetBusy(true);
            AllowHandshakeClients();
            if (_hostRadio.Checked)
            {
                _mesh = MeshUdpTransport.Bind(port); _transport = WrapSimLatency(_mesh);
                var state = _adapter.ExportState();
                // Not reporting the compressed size here: it would mean deflating the whole state a
                // second time on the UI thread just to print a percentage. The resync line reports
                // the ratio, and it is the same state.
                Log($"exported {state.Length / 1024}KiB initial state; hosting {players} players");
                StartThread(() => HostThread(port, id, prefs, state, _mesh.LocalPort, players,
                    autoDelay, autoDelayMax, lobbyFrameMs, _simLatencyMs, attempt));
                // RemotePlay-style punch admission: while the lobby waits, a NAT'd joiner's
                // pasted connect code admits them with no port-forwarding on their side.
                _lobbyPunchTargets.Clear();
                _connectButton.Enabled = true;
                _punchStatus.Text = "hosting — paste a joiner's punch code here to admit them without port-forwarding.";
                _punchStatus.ForeColor = Color.DimGray;
            }
            else
            {
                _mesh = MeshUdpTransport.Bind(0); _transport = WrapSimLatency(_mesh);
                // Start finding our public endpoint NOW, so the HELLO can carry it and the host can
                // put it in the routes it hands every other joiner. It runs while the TCP connect
                // and the lobby wait happen, so it usually costs nothing at all. Cleared first:
                // the answer belongs to a socket, and this is a new one.
                _localReflexive = null;
                try { _reflexiveKnown.Reset(); } catch { }
                StartReflexiveDiscovery(_mesh, attempt);
                string ip = joinIp!.ToString(); // parsed above, before the pause
                // Remember the address WITH its port: the dropdown's whole job is to let you rejoin
                // the same host, which a bare IP can't do once the port isn't the default any more.
                _pendingJoinIp = HostAddress.Format(joinIp, port); // recorded once the connect succeeds
                StartThread(() => JoinThread(ip, port, id, prefs, _mesh.LocalPort, attempt));
            }
        }
        catch (Exception ex)
        {
            // We may already have paused the emulator and bound a transport above; FailSession
            // unpauses, tears down the transport, and clears busy — a bare SetBusy(false) would
            // leave EmuHawk frozen (e.g. the UDP port was in use, or state export threw).
            FailSession("start failed: " + ex.Message);
        }
    }

    private void HostThread(int port, PeerIdentity id, SessionPreferences prefs, byte[] state,
        int udpLocalPort, int players, bool autoDelay, int autoDelayMax, double lobbyFrameMs,
        int simulatedOneWayMs, int attempt)
    {
        if (!IsConnectionAttemptCurrent(attempt)) return;
        // Remember what a rejoiner needs to be greeted with if a peer later drops.
        _hostIdentity = id; _hostPrefs = prefs; _hostTcpPort = port; _hostUdpPort = udpLocalPort;
        TcpListener? hostListener = null;
        try
        {
            hostListener = new TcpListener(IPAddress.Any, port);
            _listener = hostListener;
            if (!IsConnectionAttemptCurrent(attempt)) { hostListener.Stop(); return; }
            hostListener.Start();
            _punchDoorOpen = true; // punch confirmations may enqueue admissions from here to GO
            int need = players - 1;
            UiConnLog($"hosting a {players}-player session on TCP+UDP {port} — you are P1, " +
                      $"waiting for {need} more to join…", Color.DarkSlateBlue);
            UiLobbyPhase($"Hosting on port {port} — waiting for {need} more player(s) to join…",
                Color.DarkSlateBlue);

            // Best-effort NAT reachability (UPnP forward + public-address report). Non-fatal.
            TryPublishHostAddress(port, attempt);

            var links = new List<PeerLink>();
            // One generation for the whole lobby, including any restart: a joiner that survives
            // someone else's fumbled join keeps the state and the timeline it already has.
            var generation = new SessionGeneration(SessionAuth.NewSessionId(), 1);
            int finalDelay;
            SyncMode mode;

            // Fill the lobby, then try to start it. A joiner that dies between the greet and GO used
            // to throw straight out of here and take the whole session down: a healthy P2 lost its
            // lobby because P3 closed its window mid-handshake, and the host had to re-host from
            // scratch. That blast radius is wrong on its own and gets likelier with every extra
            // player, so a casualty now costs its own seat and nothing else — the door reopens and
            // the survivors wait for a replacement.
            while (true)
            {
                while (links.Count < need)
                {
                    if (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_listener, hostListener)) return;

                    // A punched joiner admitted from the UI (a pasted connect code) enters this SAME
                    // lobby as a TCP accept would: same greet, same WELCOME/READY/GO — no TCP on its
                    // link. This is what makes punch admission N-player for free.
                    if (_punchAdmissions.TryDequeue(out var admission))
                    {
                        GreetPunchedJoiner(admission, id, prefs, udpLocalPort, links, need, attempt);
                        continue;
                    }
                    if (!hostListener.Pending()) { Thread.Sleep(50); continue; }

                    TcpClient tcp;
                    try { tcp = hostListener.AcceptTcpClient(); }
                    catch when (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_listener, hostListener))
                    { return; } // teardown stopped the listener, not a failure
                    if (!IsConnectionAttemptCurrent(attempt)) { try { tcp.Close(); } catch { } return; }

                    try { tcp.NoDelay = true; } catch { } // control latency matters for ping + resync
                    try { tcp.ReceiveTimeout = HandshakeReceiveTimeoutMs; } catch { } // bound a silent joiner's HELLO
                    if (!TrackHandshakeClient(tcp, attempt)) { try { tcp.Close(); } catch { } return; }
                    _greetingTcp = tcp; // so Disconnect/teardown can abort a joiner stuck mid-handshake
                    var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                    var channel = new ControlChannel(tcp.GetStream());

                    Handshake.JoinerGreeting greet;
                    try
                    {
                        greet = WithAbsoluteSocketDeadline(tcp, HandshakeReceiveTimeoutMs,
                            () => Handshake.HostGreet(channel, id, prefs, udpLocalPort));
                    }
                    catch (Exception ex)
                    {
                        // One joiner failing the greet — wrong session password, wrong ROM/core, a HELLO
                        // that never arrived — is that joiner's problem, not the session's. Refusing them
                        // used to take the whole host down with it, so a typo'd password meant re-hosting;
                        // drop just this connection and keep the door open (same policy as a rejoin).
                        if (ReferenceEquals(_greetingTcp, tcp)) _greetingTcp = null;
                        UntrackHandshakeClient(tcp);
                        try { tcp.Close(); } catch { }
                        if (!IsConnectionAttemptCurrent(attempt)
                            || !ReferenceEquals(_listener, hostListener)) return;
                        UiConnLog($"refused a join from {remoteIp}: {ex.Message} — still hosting, " +
                                  $"waiting for {need - links.Count} player(s)", Color.Firebrick);
                        continue;
                    }

                    if (ReferenceEquals(_greetingTcp, tcp)) _greetingTcp = null;
                    try { tcp.ReceiveTimeout = 0; } catch { } // handshake done: restore blocking reads for the session
                    int assignedPort = links.Count + 1;
                    bool reflexiveCredible = ReflexiveCandidate.IsCredible(greet.Reflexive, remoteIp);
                    if (greet.Reflexive != null && !reflexiveCredible)
                        UiConnLog(
                            $"ignoring the public endpoint P{assignedPort + 1} announced ({greet.Reflexive}) — " +
                            $"it is not the address they reached us from ({remoteIp}); using that instead",
                            Color.DarkGoldenrod);
                    links.Add(new PeerLink
                    {
                        Tcp = tcp,
                        Control = channel,
                        RemotePort = assignedPort,
                        Greeting = greet,
                        UdpEndpoint = new IPEndPoint(remoteIp, greet.UdpPort),
                        // Known before WELCOME now, so the routes handed to every OTHER joiner
                        // carry a real punchable candidate rather than a port-preservation guess.
                        // Believed only if it matches where this joiner actually reached us from —
                        // this address gets handed to every other player and probed by all of them,
                        // so an unchecked one aims the whole session at whoever it names.
                        ReflexiveEndpoint = reflexiveCredible ? greet.Reflexive : null,
                        Label = $"P{assignedPort + 1} ({remoteIp})",
                    });
                    UiConnLog($"P{assignedPort + 1} joined from {remoteIp} ({links.Count}/{need})", Color.DarkGreen);
                    UiLobbyPhase(links.Count >= need
                            ? "All players in — starting the session…"
                            : $"Hosting — {links.Count} of {need} joined, waiting for {need - links.Count} more…",
                        Color.DarkSlateBlue);
                }
                if (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_listener, hostListener)) return;

                // The host decides the authoritative delay (max anyone asked) and the sync mode, both
                // from whoever is actually in the lobby right now — a replacement's preferences count
                // exactly as much as the player they replaced.
                finalDelay = prefs.InputDelay;
                foreach (var link in links)
                    finalDelay = Math.Max(finalDelay, link.Greeting!.Prefs.InputDelay);
                mode = ChooseSyncMode(links, id, prefs);

                if (TryStartLobby(links, state, players, need, generation, mode, autoDelay, autoDelayMax,
                        lobbyFrameMs, simulatedOneWayMs, attempt, ref finalDelay))
                    break;
                if (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_listener, hostListener)) return;
            }

            // Everyone is READY. Close the door only now — until this point a replacement may still
            // have been needed.
            try { hostListener.Stop(); } catch { }
            if (ReferenceEquals(_listener, hostListener)) _listener = null;
            // Close the punch door BEFORE draining: a punch worker that checks the door after this
            // either refuses outright or drains its own late enqueue — either way no admission
            // outlives this point. A code pasted just as the lobby filled has no seat — close its
            // stream cleanly.
            _punchDoorOpen = false;
            while (_punchAdmissions.TryDequeue(out var leftover))
            {
                _lifecycle.Untrack(leftover.Control);
                try { leftover.Control.Dispose(); } catch { }
                _mesh?.CloseControl(leftover.Endpoint);
            }
            if (!IsConnectionAttemptCurrent(attempt)) return;

            InvokeUiBlocking(() =>
            {
                if (!IsConnectionAttemptCurrent(attempt)) throw new OperationCanceledException();
                PrepareSessionHost(links, players, finalDelay, mode, generation);
            });
            foreach (var link in links) Handshake.HostSendGo(link.Control, generation);
            foreach (var link in links) RestoreSessionControlTimeouts(link);

            BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt))
                    BeginSessionHost(links, players, finalDelay, mode, generation);
            });
        }
        catch (Exception ex)
        {
            if (IsConnectionAttemptCurrent(attempt)) BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt)) FailSession(ex.Message);
            });
        }
        finally
        {
            if (hostListener != null && !IsConnectionAttemptCurrent(attempt))
            {
                try { hostListener.Stop(); } catch { }
                // If teardown ran between our token check and `_listener = hostListener`, its
                // null-out happened first and the field still points at this dead listener.
                // Clear it — only if it is still ours — so the next EndSession's idle fast-path
                // isn't fooled into a spurious full teardown (which would also un-pause an
                // emulator the user may have deliberately paused). CAS, not check-then-assign:
                // a newer attempt may have already installed its own listener.
#pragma warning disable 0420 // Interlocked on a volatile field is the intended usage here
                Interlocked.CompareExchange(ref _listener, null, hostListener);
#pragma warning restore 0420
            }
        }
    }

    /// <summary>
    /// The host is authoritative on sync mode. Lockstep/Rollback force it; Automatic grants rollback
    /// only if every joiner pairwise negotiates to rollback (opted in + cleared the probe depth
    /// threshold), else lockstep. Decided from whoever is in the lobby at the moment it starts, so a
    /// replacement joiner's answer counts like anyone else's.
    /// </summary>
    private SyncMode ChooseSyncMode(List<PeerLink> links, PeerIdentity id, SessionPreferences prefs)
    {
        if (_netcodeChoice == NetcodeChoice.Lockstep) return SyncMode.Lockstep;

        if (_netcodeChoice == NetcodeChoice.Rollback && !_replayDeterministic)
        {
            // The Rollback pick overrides the probe's PERFORMANCE recommendation. It does not
            // override a core that provably cannot replay: that is not a matter of taste, and
            // honouring it would guarantee the desyncs rollback exists to avoid.
            UiLog("rollback was forced, but this core failed the probe's replay check — using " +
                  "lockstep, which never reloads state. Forcing it would desync on every correction.");
            return SyncMode.Lockstep;
        }

        if (_netcodeChoice == NetcodeChoice.Rollback)
        {
            foreach (var link in links)
            {
                // No greeting means this peer's capability was never established — which is not the
                // same as it being capable. Rollback needs every peer to be able to replay; assuming
                // it of a peer we cannot ask would desync exactly the ones we know least about.
                var g = link.Greeting;
                if (g == null || !g.Prefs.WantRollback
                    || g.Id.MaxRollbackDepth < ProbeResult.RollbackDepthThreshold)
                {
                    UiLog("rollback was forced locally, but a joiner reported rollback unavailable; using lockstep");
                    return SyncMode.Lockstep;
                }
            }
            UiLog("netcode forced to rollback — bypassing only the host's local probe recommendation");
            return SyncMode.Rollback; // bypass only this host's local recommendation
        }

        // Automatic
        if (links.Count == 0) return SyncMode.Lockstep;
        foreach (var link in links)
        {
            var g = link.Greeting;
            if (g == null || SessionNegotiator.Negotiate(id, g.Id, prefs, g.Prefs).Mode != SyncMode.Rollback)
                return SyncMode.Lockstep;
        }
        return SyncMode.Rollback;
    }

    /// <summary>
    /// Everything between a full lobby and GO: the control-link ping probe, WELCOME + state, the
    /// mesh round, the settled delay, and the READY barrier.
    ///
    /// Returns true once every joiner has acknowledged READY. Returns false if any of them died on
    /// the way — those links are closed and removed, the survivors keep their seats and the state
    /// they already hold, and the caller reopens the lobby for a replacement. Also returns false if
    /// the whole attempt was torn down, which the caller distinguishes by its own token check.
    /// </summary>
    private bool TryStartLobby(List<PeerLink> links, byte[] state, int players, int need,
        SessionGeneration generation, SyncMode mode, bool autoDelay, int autoDelayMax,
        double lobbyFrameMs, int simulatedOneWayMs, int attempt, ref int finalDelay)
    {
        var casualties = new List<PeerLink>();
        int delay = finalDelay;   // a ref parameter cannot be captured by the lambdas below
        double worstRttMs = -1;
        double worstJitterMs = 0;

        if (autoDelay)
        {
            UiConnLog($"measuring lobby ping ({LobbyProbeSamples} samples per player)…",
                Color.DarkSlateBlue);
            RunStartPhase(links, casualties, attempt, "we measured its lobby ping", link =>
            {
                var sample = ProbeLobbyRtt(link);
                if (sample.MedianMs > worstRttMs) worstRttMs = sample.MedianMs;
                // Worst median and worst jitter are tracked independently: one session-wide
                // delay has to cover every link on both counts, so the safe figure is the
                // worst of each even when they come from different players.
                if (sample.JitterMs > worstJitterMs) worstJitterMs = sample.JitterMs;
            });
            if (!DropCasualties(links, casualties, need, attempt)) return false;
        }

        // Each joiner gets every OTHER joiner's UDP endpoint so it can build a direct mesh
        // (it reaches the host at the address it connected to, so the host is left off the list).
        // Trust the negotiated endpoints before asking clients to prepare their drivers: their
        // pre-READY neutral windows can then queue instead of being rejected as foreign UDP.
        _mesh!.SetPeerRoutes(RoutesExcept(links, null));
        _mesh!.ApplyTokens(TokensFor(0, players));
        // WELCOME + state, but NOT the READY request: the routes WELCOME carries are what every
        // joiner needs before it can punch and measure the edges this machine cannot see, and
        // READY is the point of no return for the delay each driver gets built with.
        RunStartPhase(links, casualties, attempt, "we sent it the session start data", link =>
        {
            ConfigureStateTransferTimeouts(link, state.Length);
            if (link.HoldsState)
            {
                // A survivor of a restart already has the state, and the host has not stepped the
                // core since exporting it — only the assignment and the routes have changed.
                Handshake.HostSendAssignment(link.Control, link.RemotePort, players, delay, mode,
                    generation, RoutesExcept(links, link), TokensFor(link.RemotePort, players));
                return;
            }
            Handshake.HostSendStart(link.Control, link.RemotePort, players, delay, mode, state,
                generation, RoutesExcept(links, link), TokensFor(link.RemotePort, players));
            link.HoldsState = true;
        });
        if (!DropCasualties(links, casualties, need, attempt)) return false;

        // ALWAYS, not only when the delay is being chosen automatically. This round is what opens
        // the joiner-to-joiner edges, decides which of them need relaying, and proves every player
        // has a UDP path to this host at all — routing questions, not latency ones. Gating it on
        // the "Auto from ping" checkbox meant turning that checkbox off silently disabled the mesh
        // relay, so a session with an unopenable edge started with no viable route for it and no
        // way to say so. Only the delay ARITHMETIC below is a preference.
        MeasureLobbyMesh(links, casualties, generation, players, attempt, autoDelay,
            ref worstRttMs, ref worstJitterMs);
        if (!DropCasualties(links, casualties, need, attempt)) return false;

        if (autoDelay)
            delay = SelectLobbyDelay(delay, autoDelayMax, mode, worstRttMs,
                lobbyFrameMs, simulatedOneWayMs, players, worstJitterMs);

        // Publish the settled figure before anyone builds a driver. Sent unconditionally, so the
        // delay every peer runs is one number decided in one place rather than each end's own
        // reading of WELCOME.
        RunStartPhase(links, casualties, attempt, "we published the session's input delay", link =>
        {
            Handshake.HostSendInputDelay(link.Control, generation, delay);
            Handshake.HostRequestReady(link.Control, generation);
        });

        // Nobody is released while the host is still synchronously shipping a large state to
        // another joiner. Only once every control link acknowledges that all start data arrived
        // does the caller prepare its own driver and send GO to the whole group.
        //
        // Deliberately no early exit above: a casualty in the phase above does not un-ask everyone
        // else for READY, and their acknowledgements are already in flight. Leaving one unread
        // would put the next start attempt's ping probe in front of a stale READY and cost a
        // healthy player their seat for a mistake made on another connection entirely.
        RunStartPhase(links, casualties, attempt, "we waited for it to finish preparing", link =>
            Handshake.HostWaitReady(link.Control, generation));
        if (!DropCasualties(links, casualties, need, attempt)) return false;

        finalDelay = delay;
        return true;
    }

    /// <summary>Run one step of the start sequence against every surviving link, recording the ones
    /// whose control link died rather than letting the first casualty end the lobby.</summary>
    private void RunStartPhase(List<PeerLink> links, List<PeerLink> casualties, int attempt,
        string phase, Action<PeerLink> step)
    {
        foreach (var link in links)
        {
            if (!IsConnectionAttemptCurrent(attempt)) return;
            if (casualties.Contains(link)) continue;
            try { step(link); }
            catch (Exception ex)
            {
                if (!IsConnectionAttemptCurrent(attempt)) return; // teardown, not this joiner's doing
                casualties.Add(link);
                UiConnLog($"{link.Label} dropped out while {phase}: {ex.Message}", Color.Firebrick);
            }
        }
    }

    /// <summary>
    /// Close and forget the joiners that died during a start attempt, renumber the survivors into
    /// the freed ports, and reopen the lobby. Returns true when nobody died and the start sequence
    /// may continue; false when the caller must go back and refill.
    /// </summary>
    private bool DropCasualties(List<PeerLink> links, List<PeerLink> casualties, int need, int attempt)
    {
        if (!IsConnectionAttemptCurrent(attempt)) return false;
        if (casualties.Count == 0) return true;

        foreach (var link in casualties)
        {
            links.Remove(link);
            UntrackHandshakeResources(link);
            try { link.Tcp?.Close(); } catch { }
            if (link.ControlStream != null)
            {
                try { link.ControlStream.Dispose(); } catch { }
                _mesh?.CloseControl(link.UdpEndpoint); // punched link: release its reliable stream
            }
        }
        casualties.Clear();

        // Ports are positional, so a survivor behind a departed joiner moves up. Nobody has been
        // released yet — the next WELCOME carries the corrected assignment — so this is safe here
        // and only here.
        //
        // Every seat that changes occupant must also change its mesh token. The token identifies
        // the SEAT, and every peer that heard the old occupant announce it has that occupant's
        // endpoint bound to the seat. A renumbered survivor is still in the session, still
        // answering from that endpoint — so the binding stays "alive" and the seat's next genuine
        // occupant can never rebind it: their direct traffic goes to the old occupant's machine
        // for the rest of the session, and the leg limps along on relay. Rotating the token
        // retires the binding instead (SetPeerTokens drops learned endpoints whose seat token
        // changed), and the next start attempt distributes the fresh set with its WELCOMEs.
        // Seats whose occupant did not move keep their token: nothing about them changed.
        var stableSeats = new HashSet<int>();
        for (int i = 0; i < links.Count; i++)
            if (links[i].RemotePort == i + 1) stableSeats.Add(i + 1);
        for (int port = 1; port <= need; port++)
            if (!stableSeats.Contains(port)) _portTokens.Remove(port);

        for (int i = 0; i < links.Count; i++)
        {
            var link = links[i];
            int assignedPort = i + 1;
            if (link.RemotePort == assignedPort) continue;
            link.RemotePort = assignedPort;
            link.Label = $"P{assignedPort + 1} ({link.UdpEndpoint.Address})";
        }

        UiConnLog($"still hosting — the remaining player(s) keep their place while we wait for " +
                  $"{need - links.Count} more to fill the lobby again.", Color.DarkSlateBlue);
        return false;
    }

    /// <summary>Greet a punched joiner exactly like a TCP accept — over the reliable control
    /// stream on the mesh socket, bounded by its read timeout instead of a socket deadline. A
    /// refused greet (wrong password/ROM/core) costs only that joiner, same as the TCP policy.</summary>
    private void GreetPunchedJoiner(PunchAdmission admission, PeerIdentity id, SessionPreferences prefs,
        int udpLocalPort, List<PeerLink> links, int need, int attempt)
    {
        var channel = new ControlChannel(admission.Control);
        Handshake.JoinerGreeting greet;
        // The per-read timeout alone is the byte-dribble bypass AbsoluteSocketDeadline documents:
        // one byte before every timeout keeps this greeting — and the single lobby thread it runs
        // on — alive forever. The deadline bounds the whole authentication, as the TCP greet's does.
        using var greetDeadline = new AbsoluteSocketDeadline(admission.Control, HandshakeReceiveTimeoutMs);
        try
        {
            try { admission.Control.ReadTimeout = HandshakeReceiveTimeoutMs; } catch { }
            greet = Handshake.HostGreet(channel, id, prefs, udpLocalPort);
            if (!greetDeadline.TryComplete())
                throw new TimeoutException($"authentication exceeded the {HandshakeReceiveTimeoutMs / 1000}-second deadline");
            try { admission.Control.ReadTimeout = Timeout.Infinite; } catch { }
        }
        catch (Exception ex)
        {
            _lifecycle.Untrack(admission.Control);
            try { admission.Control.Dispose(); } catch { }
            _mesh?.CloseControl(admission.Endpoint);
            if (!IsConnectionAttemptCurrent(attempt)) return;
            string why = greetDeadline.Expired
                ? $"authentication exceeded the {HandshakeReceiveTimeoutMs / 1000}-second deadline"
                : ex.Message;
            UiConnLog($"refused a punched join from {admission.Endpoint.Address}: {why} — " +
                      $"still hosting, waiting for {need - links.Count} player(s)", Color.Firebrick);
            return;
        }
        int assignedPort = links.Count + 1;
        // Same credibility rule as the TCP greet above: this address is handed to every other
        // player and probed by all of them, so an unchecked one aims the whole session at
        // whoever it names.
        bool reflexiveCredible = ReflexiveCandidate.IsCredible(greet.Reflexive, admission.Endpoint.Address);
        if (greet.Reflexive != null && !reflexiveCredible)
            UiConnLog(
                $"ignoring the public endpoint P{assignedPort + 1} announced ({greet.Reflexive}) — " +
                $"it is not the address they reached us from ({admission.Endpoint.Address}); using that instead",
                Color.DarkGoldenrod);
        links.Add(new PeerLink
        {
            Tcp = null!,
            ControlStream = admission.Control,
            Control = channel,
            RemotePort = assignedPort,
            Greeting = greet,
            UdpEndpoint = admission.Endpoint, // the punched path IS the peer's working endpoint
            ReflexiveEndpoint = reflexiveCredible ? greet.Reflexive : null,
            Label = $"P{assignedPort + 1} ({admission.Endpoint.Address})",
        });
        UiConnLog($"P{assignedPort + 1} joined via UDP punch from {admission.Endpoint.Address} " +
                  $"({links.Count}/{need})", Color.DarkGreen);
    }

    /// <summary>
    /// The mesh round, run between WELCOME and READY. Every peer bursts probes across its own UDP
    /// edges at the same moment, each joiner reports its worst, and the figures fold into the same
    /// worst-median / worst-jitter pair the control-link probe produced.
    ///
    /// This exists because the host's own links are a star: on a 4-player session it can reach 3 of
    /// the mesh's 6 edges, and only over TCP. A slow or jittery joiner-to-joiner path was invisible
    /// to the delay decision, which is exactly the path that then stalls lockstep or deepens every
    /// rollback repair — and it was invisible in the one topology where it matters most.
    /// </summary>
    private void MeasureLobbyMesh(List<PeerLink> links, List<PeerLink> casualties,
        SessionGeneration generation, int players, int attempt, bool autoDelay,
        ref double worstRttMs, ref double worstJitterMs)
    {
        var mesh = _mesh;
        if (mesh == null || links.Count == 0) return;

        UiConnLog($"measuring the {players}-player UDP mesh directly ({MeshProbeWindowMs}ms)…",
            Color.DarkSlateBlue);
        // Everyone bursts at once: a joiner-to-joiner edge only opens when both ends are knocking,
        // so the host's own burst and the requests below have to overlap, not queue.
        mesh.BeginRttBurst(MeshProbeWindowMs);
        RunStartPhase(links, casualties, attempt, "we asked it to measure its UDP paths",
            link => Handshake.HostRequestMeshRtt(link.Control, generation));

        int measuredEdges = 0, totalEdges = 0;
        var incomplete = new List<PeerLink>();   // joiners that could not open every direct leg
        // The joiner-to-joiner edges that never opened, as unordered port pairs. Each report now
        // NAMES its silent edges, so the relay can carry exactly these instead of everything
        // addressed to an affected joiner — a joiner short one leg used to get all input relayed
        // and the session's delay inflated by a hop its working legs were not taking.
        var relayPairs = new HashSet<(int A, int B)>();
        double rtt = worstRttMs, jitter = worstJitterMs; // ref params cannot be captured below
        RunStartPhase(links, casualties, attempt, "we waited for its mesh measurement", link =>
        {
            var report = Handshake.HostWaitMeshRtt(link.Control, generation);
            totalEdges += report.TotalEdges;
            measuredEdges += report.MeasuredEdges;
            if (report.MeasuredEdges < report.TotalEdges)
            {
                incomplete.Add(link);
                bool namedAny = false;
                foreach (int silentPort in report.SilentPorts)
                {
                    // The host leg is not a relayable pair: a relay to this joiner RUNS over that
                    // leg, and a dead one makes the joiner a casualty below, not a relay customer.
                    if (silentPort == 0 || silentPort == link.RemotePort) continue;
                    relayPairs.Add(Pair(link.RemotePort, silentPort));
                    namedAny = true;
                }
                // Backstop for a report that says edges are missing without naming them — that
                // should not happen on this protocol, but an unnamed hole must fail toward the old
                // over-delivery, not toward a leg silently carried by nobody.
                if (!namedAny && report.SilentPorts.Count == 0)
                    foreach (var other in links)
                        if (!ReferenceEquals(other, link))
                            relayPairs.Add(Pair(link.RemotePort, other.RemotePort));
            }
            if (!report.HasMeasurement)
            {
                UiConnLog($"{link.Label} could not measure any of its {report.TotalEdges} UDP edge(s) — " +
                          "its direct paths have not opened yet, so only the edges that did answer " +
                          "are measured.", Color.DarkOrange);
                return;
            }
            Fold(report.Rtt, ref rtt, ref jitter);
        });
        worstRttMs = rtt;
        worstJitterMs = jitter;
        if (casualties.Count > 0 || !IsConnectionAttemptCurrent(attempt)) return;

        // The host's own edges, on UDP this time. Its burst ran while it was blocked above.
        if (mesh.TryGetWorstRttStats(out double hostMedianMs, out double hostHighMs,
                out int hostMeasured, out int hostTotal))
        {
            measuredEdges += hostMeasured;
            totalEdges += hostTotal;
            Fold(new LobbyRttSample(hostMedianMs, hostHighMs), ref worstRttMs, ref worstJitterMs);
        }
        else totalEdges += links.Count;

        // Each edge is measured from both ends, so the mesh's 6 logical edges arrive as 12 reports.
        // Say what was covered rather than implying the whole mesh was.
        if (measuredEdges >= totalEdges && totalEdges > 0)
            UiConnLog($"mesh measured: all {totalEdges} direct path(s) answered.", Color.DarkGreen);
        else
            UiConnLog($"mesh measured: {measuredEdges} of {totalEdges} direct path(s) answered — the " +
                      "delay below is a lower bound, and a path that opens later may need more.",
                Color.DarkOrange);

        // A player this host cannot exchange UDP with cannot play, and until now the lobby said so
        // and started anyway: the warning was written, READY was requested, GO was sent, and the
        // session sat there with one seat's input never arriving. Relaying cannot rescue it either
        // — the relay runs over the very leg that is missing. So it is a casualty like any other:
        // the seat reopens and the lobby waits for them to come back, which is the one outcome
        // that can actually work.
        var noHostLeg = new List<PeerLink>();
        foreach (var link in links)
            if (!LinkHasLiveMeshPath(mesh, link)) noHostLeg.Add(link);
        if (noHostLeg.Count > 0)
        {
            foreach (var link in noHostLeg)
            {
                casualties.Add(link);
                UiConnLog($"{link.Label} has no two-way UDP path to this host — nothing this host " +
                          "sent ever came back acknowledged — so session traffic cannot flow and " +
                          "there is nothing to relay it over. Since protocol 14 a " +
                          "symmetric NAT alone should no longer do this — they would have announced " +
                          "a token and been recognised at whatever address they really arrive from — " +
                          "so suspect UDP blocked outright, or a router dropping the packet before " +
                          "it leaves. Their seat is open again: have them forward a UDP port and " +
                          "host instead, or play 2-player against a forwarded host.", Color.Firebrick);
            }
            return; // the caller drops them and reopens the lobby
        }

        InstallMeshRelay(links, incomplete, relayPairs);

        // The relay adds a hop, so the legs riding it are NOT covered by the worst direct edge the
        // delay was about to be sized from — and an edge that never opened contributed nothing to
        // that figure in the first place. Fold in what the relayed legs actually cost, from the
        // host's own legs, which are the two hops each is made of.
        if (relayPairs.Count > 0)
        {
            var hostLegs = new Dictionary<int, double>();
            foreach (var edge in mesh.DescribeEdges())
                if (edge.Measured) hostLegs[edge.RemotePort] = edge.MedianMs;
            double relayRttMs = LobbyDelayPolicy.RelayRouteRttMs(hostLegs, relayPairs);
            if (relayRttMs > worstRttMs)
            {
                UiConnLog(autoDelay
                        ? $"the relayed route costs ~{relayRttMs:F0}ms round trip against the worst " +
                          $"direct path's ~{worstRttMs:F0}ms — sizing the delay from the relayed " +
                          "figure, since that is the one those players are actually using."
                        : $"the relayed route costs ~{relayRttMs:F0}ms round trip against the worst " +
                          $"direct path's ~{worstRttMs:F0}ms. Automatic delay is off, so nothing " +
                          "sizes to that figure — check the manual delay covers it.",
                    Color.DarkOrange);
                worstRttMs = relayRttMs;
            }
        }

        static void Fold(LobbyRttSample sample, ref double rtt, ref double jitter)
        {
            if (sample.MedianMs > rtt) rtt = sample.MedianMs;
            if (sample.JitterMs > jitter) jitter = sample.JitterMs;
        }
    }

    /// <summary>An unordered port pair, normalised so (2,4) and (4,2) are the same edge.</summary>
    private static (int A, int B) Pair(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>Whether ANY endpoint of this peer has completed a round trip on UDP — i.e. whether
    /// the host has a real mesh path to it, as distinct from the TCP control link it arrived on.
    /// The learned endpoint counts: for a symmetric-NAT peer it is the ONLY address that will ever
    /// answer, so leaving it out would flag exactly the peers the tokens just rescued.
    ///
    /// Round-trip proof, not mere liveness: for an advertised endpoint ANY inbound datagram marks
    /// it alive — including the peer's own punches — so a one-way path (joiner reaches host, host
    /// cannot reach joiner) used to pass this gate, and the session started with a relay running
    /// over the very leg that was broken. An RTT sample exists only when a punch WE sent came back
    /// acknowledged, which is the property the relay actually needs. Alive still matters: an old
    /// sample from a path that has since died is history, not a route.</summary>
    private static bool LinkHasLiveMeshPath(MeshUdpTransport mesh, PeerLink link) =>
        HasProvenPath(mesh, link.UdpEndpoint)
        || (link.ReflexiveEndpoint != null && HasProvenPath(mesh, link.ReflexiveEndpoint))
        || (mesh.TryGetLearnedEndpoint(link.RemotePort, out var learned) && HasProvenPath(mesh, learned));

    private static bool HasProvenPath(MeshUdpTransport mesh, IPEndPoint endpoint) =>
        mesh.IsEndpointAlive(endpoint) && mesh.TryGetRttMs(endpoint, out _);

    /// <summary>
    /// Have the host forward input to joiners whose direct legs to the other joiners never opened.
    ///
    /// The host is already the rendezvous every joiner reached, so it can relay with no external
    /// server: no TURN, no third party, nothing to run. The cost is one extra hop on the affected
    /// legs and a little host uplink.
    ///
    /// Since protocol 14 a symmetric-NAT peer usually has a host leg to relay over: it announces a
    /// token, and whoever receives it binds that seat to wherever the packet really came from. What
    /// remains here is the case where even that did not happen — the peer never got a packet through
    /// in either direction — and relaying into a peer we cannot reach is a stall, not a rescue, so
    /// it is said out loud rather than papered over.
    ///
    /// Decided once, here, from the mesh round — not failed over live. A start-of-session decision is
    /// deterministic, shows up in the log, and cannot oscillate under packet loss; live failover is a
    /// harder problem and deliberately left alone.
    ///
    /// Carries exactly the edges the reports named. This used to be all-or-nothing per joiner —
    /// the report carried counts, not identities, so a joiner short ONE leg got every player's
    /// input relayed to it and the session's delay was inflated by a hop its working legs were not
    /// taking. Duplicates on the named legs are still free — input is keyed by (port, frame), so a
    /// relayed copy arriving beside a late direct one is discarded.
    /// </summary>
    private void InstallMeshRelay(List<PeerLink> links, List<PeerLink> incomplete,
        HashSet<(int A, int B)> pairs)
    {
        // Stored as PORT PAIRS, not as the routes themselves: endpoints change when someone
        // rejoins, and RefreshRelayRoutes re-resolves these against the live peer list every time
        // that happens.
        //
        // Decided here on the lobby thread but RESOLVED later, on the UI thread, by
        // PrepareSessionHost. _peers belongs to the UI thread and is not filled until then, so
        // resolving here would resolve against an empty list — which is what this used to do,
        // leaving the relay holding nothing while the log below claimed it was carrying players.
        InvokeUiBlocking(() =>
        {
            _relayPairs.Clear();
            foreach (var p in pairs) _relayPairs.Add(p);
        });

        if (incomplete.Count == 0) return;
        if (pairs.Count == 0)
        {
            UiConnLog("a direct UDP path did not answer in the measurement window, but no " +
                      "joiner-to-joiner leg is affected — there is nothing for the host to relay, " +
                      "and every player's host leg has been checked and is live.",
                Color.DarkOrange);
            return;
        }

        // Every player still here has a live host leg — MeasureLobbyMesh makes that a precondition
        // rather than a warning, because a relay over a leg that does not exist is a stall dressed
        // up as a rescue. So this can now claim what it delivers without qualification.
        var named = new List<string>();
        foreach (var (a, b) in pairs) named.Add($"P{a + 1}↔P{b + 1}");
        UiConnLog($"relaying input through this host for {pairs.Count} joiner-to-joiner leg(s) that " +
                  $"did not open ({string.Join(", ", named)}). Those players stay in the session; " +
                  "input on the named legs takes one extra hop.", Color.DarkOrange);
    }

    /// <summary>
    /// Joiner half of the mesh round: point the mesh at the host and every other joiner, burst
    /// probes over those paths, and report the worst edge back. Installing the routes here rather
    /// than at READY is what gives the punch time to complete before anyone commits to a delay;
    /// the same set is installed again by <see cref="ApplyJoinerMesh"/> a moment later, which
    /// leaves the confirmations and samples taken here intact.
    /// </summary>
    private LobbyMeshSample MeasureJoinerMesh(
        IPEndPoint hostEndpoint, IReadOnlyList<PeerRoute> peerRoutes, MeshTokens tokens)
    {
        UiConnLog($"measuring my {peerRoutes.Count + 1} direct UDP path(s) ({MeshProbeWindowMs}ms)…",
            Color.DarkSlateBlue);
        // Tokens before probes: an edge whose far side only ever sees us at an address we were
        // never able to advertise is exactly the edge this measurement would otherwise write off.
        _mesh?.ApplyTokens(tokens);
        var sample = TakeJoinerMeshSample(hostEndpoint, peerRoutes);
        UiConnLog(sample.HasMeasurement
            ? $"my worst direct path: ~{sample.Rtt.MedianMs:F0}ms (±{sample.Rtt.JitterMs:F0}ms), " +
              $"{sample.MeasuredEdges}/{sample.TotalEdges} path(s) answered"
            : $"none of my {sample.TotalEdges} direct path(s) answered in time — the host will size " +
              "the delay from the paths that did",
            sample.IsComplete ? Color.DarkGreen : Color.DarkOrange);
        DescribeMeshEdges();
        return sample;
    }

    /// <summary>
    /// Name the edges the count above only tallied: who answered, who did not, and who could only
    /// be reached at an address they were never able to advertise.
    ///
    /// "2/3 answered" tells a player their session is about to be worse without telling them which
    /// player to look at — and the silent edge is the one that decides whether this is a mesh
    /// problem or one peer's router.
    /// </summary>
    private void DescribeMeshEdges()
    {
        var edges = _mesh?.DescribeEdges();
        if (edges == null || edges.Count == 0) return;

        var silent = new List<string>();
        var learned = new List<string>();
        foreach (var edge in edges)
        {
            string who = edge.RemotePort == 0 ? "the host" : $"P{edge.RemotePort + 1}";
            if (!edge.Measured) { silent.Add(who); continue; }
            if (edge.ViaLearnedEndpoint) learned.Add($"{who} (~{edge.MedianMs:F0}ms)");
        }

        if (learned.Count > 0)
            UiConnLog($"reached at a learned address: {string.Join(", ", learned)} — that peer is behind " +
                      "a symmetric NAT, and the address it advertised was never going to work",
                Color.DarkSlateBlue);
        if (silent.Count > 0)
            UiConnLog($"no direct path answered to: {string.Join(", ", silent)} — input to and from " +
                      (silent.Count == 1 ? "that player" : "those players") +
                      " may have to go the long way round via the host",
                Color.DarkOrange);
    }

    private LobbyMeshSample TakeJoinerMeshSample(IPEndPoint hostEndpoint, IReadOnlyList<PeerRoute> peerRoutes)
    {
        var mesh = _mesh;
        if (mesh == null) return LobbyMeshSample.None;

        var routes = new List<PeerRoute> { new(0, new[] { hostEndpoint }) };
        routes.AddRange(peerRoutes);
        try { mesh.SetPeerRoutes(routes); }
        catch { return LobbyMeshSample.None; }

        mesh.BeginRttBurst(MeshProbeWindowMs);
        Thread.Sleep(MeshProbeWindowMs);
        // Name the silent edges in the report itself, not only in this side's log: the host can
        // then relay exactly the broken legs instead of everything addressed to this joiner.
        var silentPorts = new List<int>();
        foreach (var edge in mesh.DescribeEdges())
            if (!edge.Measured) silentPorts.Add(edge.RemotePort);
        if (!mesh.TryGetWorstRttStats(out double medianMs, out double highMs,
                out int measuredRoutes, out int totalRoutes))
            return new LobbyMeshSample(default, 0, routes.Count, silentPorts);
        return new LobbyMeshSample(new LobbyRttSample(medianMs, highMs), measuredRoutes, totalRoutes,
            silentPorts);
    }

    /// <summary>Lobby RTT probe with the deadline on whichever pipe the link actually uses.</summary>
    private static LobbyRttSample ProbeLobbyRtt(PeerLink link)
    {
        if (link.Tcp != null)
        {
            int oldReceive = 0, oldSend = 0;
            try
            {
                oldReceive = link.Tcp.ReceiveTimeout;
                oldSend = link.Tcp.SendTimeout;
                link.Tcp.ReceiveTimeout = LobbyProbeTimeoutMs;
                link.Tcp.SendTimeout = LobbyProbeTimeoutMs;
                return Handshake.MeasureLobbyRtt(link.Control, LobbyProbeSamples);
            }
            finally
            {
                try { link.Tcp.ReceiveTimeout = oldReceive; link.Tcp.SendTimeout = oldSend; } catch { }
            }
        }
        if (link.ControlStream is { CanTimeout: true } stream)
        {
            int old = stream.ReadTimeout;
            try
            {
                stream.ReadTimeout = LobbyProbeTimeoutMs;
                return Handshake.MeasureLobbyRtt(link.Control, LobbyProbeSamples);
            }
            finally
            {
                try { stream.ReadTimeout = old > 0 ? old : Timeout.Infinite; } catch { }
            }
        }
        return Handshake.MeasureLobbyRtt(link.Control, LobbyProbeSamples);
    }

    /// <summary>State-transfer deadline on whichever pipe the link uses (TCP socket timeouts, or
    /// the punched control stream's read timeout).</summary>
    private static void ConfigureStateTransferTimeouts(PeerLink link, int stateBytes)
    {
        if (link.Tcp != null) { ConfigureStateTransferTimeouts(link.Tcp, stateBytes); return; }
        if (link.ControlStream is { CanTimeout: true } stream)
        {
            try { stream.ReadTimeout = StateTransferTimeoutMs(stateBytes); } catch { }
        }
    }

    /// <summary>Post-GO: idle reads go unbounded on every link type, and any frame that has
    /// started arriving stays bounded by its declared size — TCP and punched links alike.</summary>
    private static void RestoreSessionControlTimeouts(PeerLink link)
    {
        if (link.Tcp != null)
        {
            try { link.Tcp.ReceiveTimeout = 0; link.Tcp.SendTimeout = 0; } catch { }
        }
        else if (link.ControlStream is { CanTimeout: true } stream)
        {
            try { stream.ReadTimeout = Timeout.Infinite; } catch { }
        }
        link.Control.BodyReadTimeoutMs = len =>
            StateTransferBudget.SocketTimeoutMs(len, HandshakeReceiveTimeoutMs);
    }

    /// <summary>Release whatever handshake resource teardown was tracking for this link (the TCP
    /// socket, or a punched link's control stream) — the session owns it from here.</summary>
    private void UntrackHandshakeResources(PeerLink link)
    {
        UntrackHandshakeClient(link.Tcp);
        if (link.ControlStream != null) _lifecycle.Untrack(link.ControlStream);
    }

    private void JoinThread(string ip, int port, PeerIdentity id, SessionPreferences prefs,
        int udpLocalPort, int attempt)
    {
        if (!IsConnectionAttemptCurrent(attempt)) return;
        TcpClient? tcp = null;
        try
        {
            UiConnLog($"connecting to {ip}:{port}…", Color.DarkSlateBlue);
            UiLobbyPhase($"Establishing connection to {ip}:{port}…", Color.DarkSlateBlue);
            tcp = new TcpClient();
            _joiningTcp = tcp;          // so Disconnect can close a connect that's still blocking
            if (!IsConnectionAttemptCurrent(attempt)) { tcp.Close(); return; }
            if (!TrackHandshakeClient(tcp, attempt)) { try { tcp.Close(); } catch { } return; }
            try { tcp.ReceiveTimeout = HandshakeReceiveTimeoutMs; } catch { }
            tcp.Connect(ip, port);
            try { tcp.NoDelay = true; } catch { } // control latency matters for ping + resync
            var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
            var channel = new ControlChannel(tcp.GetStream());
            bool initialStateApplied = false;
            PeerLink? preparedLink = null;
            SessionParams sp;
            using (var greetDeadline = new AbsoluteSocketDeadline(tcp, HandshakeReceiveTimeoutMs))
            {
                try
                {
                    int preparations = 0;
                    sp = Handshake.RunClientMulti(channel, id, prefs, udpLocalPort, beforeReady: ready =>
                    {
                        if (++preparations > 1)
                            UiConnLog($"the host restarted the lobby — someone else dropped out before " +
                                      $"the start. Re-preparing as P{ready.LocalPort + 1} of " +
                                      $"{ready.PlayerCount}; your connection is fine.", Color.DarkOrange);
                        InvokeUiBlocking(() =>
                        {
                            if (!IsConnectionAttemptCurrent(attempt)) throw new OperationCanceledException();
                            preparedLink = new PeerLink
                            {
                                Tcp = tcp,
                                Control = channel,
                                RemotePort = 0,
                                UdpEndpoint = new IPEndPoint(remoteIp, ready.RemoteUdpPort),
                                Label = $"host ({remoteIp})",
                            };
                            PrepareSessionJoiner(ready, preparedLink);
                        });
                        initialStateApplied = true;
                    }, afterGreet: () =>
                    {
                        if (!greetDeadline.TryComplete())
                            throw new TimeoutException("host authentication exceeded the 15-second deadline");
                        // A 3–4 player host may legitimately wait minutes for the remaining lobby slots.
                        // The short timeout protects only HELLO/auth; Disconnect remains able to cancel
                        // this now-unbounded lobby wait through the tracked socket. The IDLE wait is
                        // unbounded, but any frame that has STARTED arriving — the WELCOME/state
                        // included — must keep flowing at the modeled floor rate: a host that dies
                        // mid-transfer fails the join instead of hanging it forever (KI-2).
                        try { tcp.ReceiveTimeout = 0; } catch { }
                        channel.BodyReadTimeoutMs = len =>
                            StateTransferBudget.SocketTimeoutMs(len, HandshakeReceiveTimeoutMs);

                        // Say so. Everything above this point is the only part of joining that has a
                        // deadline; from here the wait is deliberately unbounded because the host may
                        // still be short a player. A joiner was given no signal at all that its
                        // connection had succeeded, so a perfectly healthy 3-player lobby looked
                        // identical to a failed connect for however long the last slot took to fill.
                        UiConnLog("connected and authenticated — waiting for the host to fill the " +
                                  "lobby and start. This can take a while in a 3-4 player session; " +
                                  "Disconnect still cancels.", Color.DarkGreen);
                        UiLobbyPhase("Connected — waiting for the host to fill the lobby and start…",
                            Color.DarkGreen);
                    }, measureMesh: (hostUdpPort, peerRoutes, tokens) =>
                        MeasureJoinerMesh(new IPEndPoint(remoteIp, hostUdpPort), peerRoutes, tokens),
                       localReflexive: AwaitLocalReflexive());
                }
                catch (Exception ex) when (greetDeadline.Expired)
                {
                    throw new TimeoutException("host authentication exceeded the 15-second deadline", ex);
                }
            }
            if (ReferenceEquals(_joiningTcp, tcp)) _joiningTcp = null;
            var link = preparedLink ?? throw new HandshakeException("client READY preparation did not complete");
            BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt)) BeginSessionJoiner(sp, link, initialStateApplied);
                else { UntrackHandshakeClient(tcp); try { tcp.Close(); } catch { } }
            });
        }
        catch (Exception ex)
        {
            if (tcp != null && ReferenceEquals(_joiningTcp, tcp)) _joiningTcp = null;
            if (IsConnectionAttemptCurrent(attempt)) BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt)) FailSession(ex.Message);
            });
            else { UntrackHandshakeClient(tcp); try { tcp?.Close(); } catch { } }
        }
    }

}
