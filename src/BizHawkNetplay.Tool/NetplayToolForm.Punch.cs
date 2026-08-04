using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    // --- State used only by this file (everything shared stays in NetplayToolForm.cs) ---
    private PeerIdentity? _punchId;         // prepared handshake identity, captured when punch setup began
    private SessionPreferences? _punchPrefs;

    // ------------------------------------------------------------------ UDP punch (no port-forwarding)

    /// <summary>
    /// Step 1 of the punch path: prepare the session exactly as <see cref="OnGo"/> does, bind the
    /// punch socket, and STUN-discover our connect code off-thread. The chosen role (Host/Join) still
    /// decides who owns the initial state and is P1 — the punch is symmetric at the transport level.
    /// </summary>
    private void OnPunchStart()
    {
        if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
        if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }
        if (_phase.IsActive || _transport != null) { Log("Already connecting — Disconnect first."); return; }
        StartLogFile(); // a punch attempt is worth a file whether or not it succeeds
        if (_hostRadio.Checked)
        {
            // Defensive: the button is hidden for hosts (they just Start Hosting and paste codes).
            _punchStatus.Text = "hosts don't punch — click Start Hosting, then paste each joiner's code here.";
            _punchStatus.ForeColor = Color.Firebrick;
            return;
        }
        if (!HostAddress.TryParse(_ipBox.Text, (int)_portBox.Value, out var hostIp, out int hostPort)
            || hostIp == null)
        {
            _punchStatus.Text = "enter the host's IP in the connection section above, then click UDP Punch.";
            _punchStatus.ForeColor = Color.Firebrick;
            return;
        }

        if (SessionHazardsBlockStart()) return; // active movie refuses; Lua only warns

        try
        {
            _adapter = new EmuHawkAdapter(APIs, _emulator, _statable, Config, MovieSession);
            _adapter.InputSourcePort = InputSourceFromCombo(); // read your normal pad, whatever port you're assigned
            if (!_adapter.VerifyDeterministicMode())
                Log("WARNING: core does not report deterministic emulation — desyncs are likely.");
            if (!_adapter.HasBindings)
                Log($"WARNING: input may not register — {_adapter.BindingDiagnostic}");

            // Same refusal as the ordinary start path: a core the checksum cannot fully see is not
            // one to netplay, whichever transport is carrying it.
            var coverageGap = _adapter.MainMemoryCoverageGap();
            if (coverageGap != null)
            {
                _punchStatus.Text = "this core emulates several machines — see the log.";
                _punchStatus.ForeColor = Color.Firebrick;
                ConnLog(coverageGap, Color.Firebrick);
                return;
            }

            _isHost = false;
            int players = _adapter.PortCount;
            if (players < 2)
            {
                Log($"this core exposes only {players} controller port — need at least 2 for netplay.");
                return;
            }

            // Freeze now so the resume frame is fixed before the state arrives — and refuse if the
            // timeline guards could not be taken, on the same terms as the ordinary start path.
            if (!PauseForSession())
            {
                _punchStatus.Text = "could not take ownership of the emulator — see the log.";
                _punchStatus.ForeColor = Color.Firebrick;
                ConnLog(OwnershipRefusal ?? "could not take ownership of the emulator timeline.",
                    Color.Firebrick);
                return;
            }

            _netcodeChoice = (NetcodeChoice)_netcodeCombo.SelectedIndex;
            _punchPrefs = LocalPreferences(isHost: false); // punching is always the joining side
            _punchId = BuildIdentity(_adapter, _punchPrefs.WantRollback);
            _simLatencyMs = (int)_simLatencyBox.Value;

            StartPunchJoin(new IPEndPoint(hostIp, hostPort));
        }
        catch (Exception ex)
        {
            Log("punch setup failed: " + ex.Message);
            EndSession("punch setup failed");
        }
    }

    /// <summary>
    /// Joiner-side punch join (RemotePlay-style): punch the host's known endpoint from the SAME
    /// mesh socket the session will use, show our code for the host to paste, and once the path
    /// confirms run the ordinary join handshake over a reliable control stream on that socket.
    /// From the session's point of view this is a normal joiner — mesh input, N players, resync.
    /// </summary>
    private void StartPunchJoin(IPEndPoint host)
    {
        int attempt = BeginConnectionAttempt();
        AllowHandshakeClients();
        var mesh = MeshUdpTransport.Bind(0);
        _mesh = mesh;
        _transport = WrapSimLatency(mesh);
        mesh.SetPeerRoutes(new List<PeerRoute> { new(0, new[] { host }) });
        _localReflexive = null;                       // new socket, new answer
        try { _reflexiveKnown.Reset(); } catch { }
        SetBusy(true);
        _punchButton.Enabled = false;
        _punchStatus.Text = "finding your public address…";
        _punchStatus.ForeColor = Color.DimGray;
        ClassifyNatInBackground(attempt); // says WHY, in the case where punching cannot work
        var id = _punchId!;
        var prefs = _punchPrefs!;

        new Thread(() =>
        {
            try
            {
                IPEndPoint? reflexive = null;
                try { reflexive = mesh.DiscoverReflexive(TimeSpan.FromSeconds(3)); }
                catch { /* best-effort; the LAN fallback below still works on one router */ }
                if (!IsConnectionAttemptCurrent(attempt)) return;
                PublishLocalReflexive(reflexive); // also our HELLO's mesh candidate
                IPEndPoint? lanEp = null;
                try { lanEp = new IPEndPoint(IPAddress.Parse(UpnpPortMapper.PrimaryLanIp()), mesh.LocalPort); }
                catch { }
                var codeEp = reflexive ?? lanEp ?? new IPEndPoint(IPAddress.Loopback, mesh.LocalPort);
                string code = ConnectCode.Encode(codeEp);
                BeginInvokeUi(() =>
                {
                    if (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_mesh, mesh)) return;
                    _myCodeBox.Text = code;
                    _copyCodeButton.Enabled = true;
                    _punchStatus.Text = $"punching {host} — send your code to the host and wait…";
                    Log($"UDP punch join — your code: {code}   (endpoint {codeEp})");
                });

                // The mesh's punch loop probes the host continuously (opening our NAT the whole
                // time); the path confirms whenever the host pastes our code — or immediately,
                // if the host's port is reachable.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (IsConnectionAttemptCurrent(attempt) && sw.Elapsed.TotalSeconds < PunchPatienceSeconds
                       && !mesh.IsEndpointAlive(host))
                    Thread.Sleep(100);
                if (!IsConnectionAttemptCurrent(attempt)) return;
                if (!mesh.IsEndpointAlive(host))
                    throw new TimeoutException(
                        $"no punch answer from {host} after {PunchPatienceSeconds / 60} minutes — " +
                        "did the host paste your code?");

                var control = mesh.OpenControl(host);
                if (!_lifecycle.Track(control, attempt)) return;
                var channel = new ControlChannel(control);
                var peerLink = new PeerLink
                {
                    Tcp = null!,
                    ControlStream = control,
                    Control = channel,
                    RemotePort = 0,
                    UdpEndpoint = host, // the punched path IS the working endpoint
                    Label = $"host ({host.Address})",
                };
                bool initialStateApplied = false;
                int preparations = 0;
                try { control.ReadTimeout = HandshakeReceiveTimeoutMs; } catch { }
                // Bound the whole auth phase, as JoinThread does — a per-read timeout alone lets a
                // hostile listener hold this thread with a byte dribbled before every timeout.
                using var greetDeadline = new AbsoluteSocketDeadline(control, HandshakeReceiveTimeoutMs);
                var sp = Handshake.RunClientMulti(channel, id, prefs, mesh.LocalPort, beforeReady: ready =>
                {
                    if (++preparations > 1)
                        UiConnLog($"the host restarted the lobby — someone else dropped out before the " +
                                  $"start. Re-preparing as P{ready.LocalPort + 1} of {ready.PlayerCount}; " +
                                  "your connection is fine.", Color.DarkOrange);
                    InvokeUiBlocking(() =>
                    {
                        if (!IsConnectionAttemptCurrent(attempt)) throw new OperationCanceledException();
                        PrepareSessionJoiner(ready, peerLink);
                    });
                    initialStateApplied = true;
                }, measureMesh: (_, peerRoutes, tokens) => MeasureJoinerMesh(host, peerRoutes, tokens),
                   localReflexive: AwaitLocalReflexive(),
                   afterGreet: () =>
                {
                    if (!greetDeadline.TryComplete())
                        throw new TimeoutException("host authentication exceeded the 15-second deadline");
                    // Auth done. The lobby wait is legitimately unbounded (the host may wait
                    // minutes for other players); started frames must still finish.
                    try { control.ReadTimeout = Timeout.Infinite; } catch { }
                    channel.BodyReadTimeoutMs = len =>
                        StateTransferBudget.SocketTimeoutMs(len, HandshakeReceiveTimeoutMs);
                });
                BeginInvokeUi(() =>
                {
                    if (IsConnectionAttemptCurrent(attempt)) BeginSessionJoiner(sp, peerLink, initialStateApplied);
                    else { _lifecycle.Untrack(control); try { control.Dispose(); } catch { } }
                });
            }
            catch (Exception ex)
            {
                if (IsConnectionAttemptCurrent(attempt)) BeginInvokeUi(() =>
                {
                    if (IsConnectionAttemptCurrent(attempt)) FailSession(ex.Message);
                });
            }
        })
        { IsBackground = true, Name = "BizHawkNetplay-punch-join" }.Start();
    }

    /// <summary>
    /// Host-side punch admission (RemotePlay-style): the hosting lobby is already up; pasting a
    /// joiner's connect code makes the mesh punch toward it from the SAME socket the session
    /// will use, and hands the confirmed control stream to the lobby thread, which greets it
    /// exactly like a TCP accept.
    /// </summary>
    private void StartPunchAdmit(IPEndPoint joiner)
    {
        int attempt = CurrentConnectionAttempt;
        var mesh = _mesh;
        if (mesh == null) return;
        if (!_lobbyPunchTargets.Contains(joiner)) _lobbyPunchTargets.Add(joiner);
        // Merged, not replaced: the lobby may already be up with real joiner routes installed, and
        // this admission must not evict them. Placeholder ports live above every real seat; the
        // real routes are set at GO. See MeshUdpTransport.AddPunchTargets.
        try { mesh.AddPunchTargets(_lobbyPunchTargets); } catch { }
        _punchStatus.Text = $"punching toward {joiner}…";
        _punchStatus.ForeColor = Color.DimGray;
        ConnLog($"punching toward {joiner} — they join the lobby when the path opens…", Color.DarkSlateBlue);

        new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (IsConnectionAttemptCurrent(attempt) && ReferenceEquals(_mesh, mesh)
                   && sw.Elapsed.TotalSeconds < PunchPatienceSeconds && !mesh.IsEndpointAlive(joiner))
                Thread.Sleep(100);
            if (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_mesh, mesh)) return;
            if (!mesh.IsEndpointAlive(joiner))
            {
                BeginInvokeUi(() =>
                {
                    if (!IsConnectionAttemptCurrent(attempt)) return;
                    _punchStatus.Text = $"no punch answer from {joiner} — check the code, and that they clicked UDP Punch.";
                    _punchStatus.ForeColor = Color.Firebrick;
                });
                return;
            }
            var control = mesh.OpenControl(joiner);
            if (!_lifecycle.Track(control, attempt)) return; // teardown won the race; mesh disposal closes it

            // The lobby drains this queue exactly once, at GO. A punch that confirms after that
            // has no seat: enqueueing it would hold the stream open all session while the peer
            // dies on a misleading read timeout. Refuse promptly instead — and re-check after the
            // enqueue, draining our own late entry if the door closed in between (racing drains
            // are safe: TryDequeue is atomic, so each admission is refused exactly once).
            void RefusePunch(PunchAdmission admission)
            {
                _lifecycle.Untrack(admission.Control);
                try { admission.Control.Dispose(); } catch { }
                mesh.CloseControl(admission.Endpoint);
            }
            var ours = new PunchAdmission { Endpoint = joiner, Control = control };
            if (!_punchDoorOpen)
            {
                RefusePunch(ours);
                BeginInvokeUi(() =>
                {
                    if (!IsConnectionAttemptCurrent(attempt)) return;
                    _punchStatus.Text = $"punched {joiner}, but the lobby had already filled — no seat for them.";
                    _punchStatus.ForeColor = Color.Firebrick;
                });
                return;
            }
            _punchAdmissions.Enqueue(ours);
            if (!_punchDoorOpen)
                while (_punchAdmissions.TryDequeue(out var leftover)) RefusePunch(leftover);
            BeginInvokeUi(() =>
            {
                if (!IsConnectionAttemptCurrent(attempt)) return;
                _punchStatus.Text = $"punched {joiner} — admitting…";
                _punchStatus.ForeColor = Color.DimGray;
                _peerCodeBox.Text = ""; // ready for the next joiner's code
            });
        })
        { IsBackground = true, Name = "BizHawkNetplay-punch-admit" }.Start();
    }

    /// <summary>A punch target: a connect code, an <c>ip:port</c>, or a bare IP — the port then
    /// defaults to the Port box, the RemotePlay convention where the host listens on the
    /// well-known port and only its IP needs sharing.</summary>
    private IPEndPoint? TryParsePunchTarget(string? text)
    {
        var target = ConnectCode.TryParseTarget(text);
        if (target != null) return target;
        if (string.IsNullOrWhiteSpace(text)) return null;
        return IPAddress.TryParse(text!.Trim(), out var ip)
            && ip.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(ip, (int)_portBox.Value)
            : null;
    }

    private void CopyMyCode()
    {
        try { if (!string.IsNullOrEmpty(_myCodeBox.Text)) Clipboard.SetText(_myCodeBox.Text); }
        catch { /* clipboard busy */ }
    }

    /// <summary>The host's Admit button: while the hosting lobby waits, a pasted joiner code
    /// punches toward them and they enter the lobby exactly like a TCP accept.</summary>
    private void OnPunchAdmit()
    {
        var peer = TryParsePunchTarget(_peerCodeBox.Text);
        if (peer == null)
        {
            _punchStatus.Text = "that doesn't look like a connect code, an ip:port, or an IP — check it and retry.";
            _punchStatus.ForeColor = Color.Firebrick;
            return;
        }
        if (_listener == null || _phase.IsActive)
        {
            _punchStatus.Text = "click Start Hosting first — codes are pasted while the lobby is waiting for players.";
            _punchStatus.ForeColor = Color.Firebrick;
            return;
        }
        StartPunchAdmit(peer);
    }

    private void ResetPunchUi()
    {
        if (_punchGroup.IsDisposed) return;
        _punchGroup.Enabled = true;
        _punchButton.Enabled = true;
        _myCodeBox.Text = "";
        _peerCodeBox.Text = "";
        _copyCodeButton.Enabled = false;
        _punchStatus.Text = "";
        _punchStatus.ForeColor = Color.DimGray;
        UpdatePunchUiForRole();
    }

    // ------------------------------------------------------------------ NAT / reachability

    /// <summary>
    /// Classify this machine's NAT off-thread and report it, without blocking whatever asked.
    ///
    /// Deliberately advisory rather than a gate. A verdict costs two STUN round-trips and can be
    /// wrong in the player's favour — a router that looked symmetric to two servers may still be
    /// punchable — so it never refuses a connection. It exists because the failure it names is
    /// otherwise indistinguishable from an ordinary bad link: the punch is sent, nothing answers,
    /// and the session times out with a message about the network. The player retries, changes
    /// nothing that could matter, and retries again. One line at the moment they press the button
    /// turns that into a decision they can act on.
    /// </summary>
    private void ClassifyNatInBackground(int attempt, bool reportUnknown = false)
    {
        new Thread(() =>
        {
            var report = StunClient.ClassifyMapping(TimeSpan.FromSeconds(3));
            BeginInvokeUi(() =>
            {
                if (attempt >= 0 && !IsConnectionAttemptCurrent(attempt)) return;
                // Mid-connect, a non-verdict is noise on top of a busy log; asked for directly, it
                // is the answer to the question and has to be said.
                if (report.Mapping == NatMapping.Unknown && !reportUnknown) return;
                Log(report.Describe());
                if (report.IsSymmetric)
                    Status("symmetric NAT — punching can't open a path; see the log.", Color.Firebrick);
            });
        })
        { IsBackground = true, Name = "BizHawkNetplay-nat" }.Start();
    }

    /// <summary>Look up and log our public (reflexive) address via STUN, plus our LAN IP. Off-thread
    /// (STUN does a UDP round-trip to a public server).</summary>
    private void ShowPublicAddress()
    {
        // Feedback lands on the status bar (visible under every tab) as well as the Log — otherwise,
        // from the Connection tab, the button looks like it does nothing. Report the address friends
        // actually dial (public IP + the port you'd forward), not the throwaway STUN probe's port.
        int port = (int)_portBox.Value;
        _pubAddrButton.Enabled = false;
        _pubAddrButton.Text = "Looking up…";
        Status("looking up your public address…", Color.DimGray);
        Log("looking up your public address…");
        // Whoever presses this is asking "can people reach me?", and the mapping type is half of
        // that answer — the half a public IP alone looks like it settles and doesn't.
        ClassifyNatInBackground(attempt: -1, reportUnknown: true);
        new Thread(() =>
        {
            var pub = StunClient.DiscoverPublicAddress(TimeSpan.FromSeconds(2.5));
            string lan = UpnpPortMapper.PrimaryLanIp();
            BeginInvokeUi(() =>
            {
                if (!_pubAddrButton.IsDisposed) { _pubAddrButton.Enabled = true; _pubAddrButton.Text = "My public address"; }
                if (pub != null)
                {
                    Status($"Public IP {pub.Address} — friends connect to {pub.Address}:{port} (forward port {port})", Color.DarkGreen);
                    Log($"public IP {pub.Address}; for internet play, forward port {port} (TCP+UDP) and give friends {pub.Address}:{port}   (LAN: {lan}:{port})");
                    try { Clipboard.SetText($"{pub.Address}:{port}"); Log("(copied to clipboard)"); } catch { }
                }
                else
                {
                    Status("Couldn't reach a STUN server (offline or UDP blocked).", Color.Firebrick);
                    Log($"couldn't reach a STUN server (offline or UDP blocked); LAN IP {lan}:{port}");
                }
            });
        })
        { IsBackground = true, Name = "BizHawkNetplay-stun" }.Start();
    }

    /// <summary>
    /// Host, off the accept thread: best-effort UPnP-forward our port and report the address internet
    /// joiners should use. Non-fatal — LAN/localhost play needs none of it. The mapping is removed on
    /// session end.
    /// </summary>
    private void TryPublishHostAddress(int port, int attempt)
    {
        try
        {
            string lan = UpnpPortMapper.PrimaryLanIp();
            if (_upnpEnabled)
            {
                var mapping = UpnpPortMapper.TryAddPortMapping(
                    port, lan, "BizHawk Netplay", TimeSpan.FromSeconds(2.5));
                if (!IsConnectionAttemptCurrent(attempt))
                {
                    try { mapping?.Remove(TimeSpan.FromSeconds(2)); } catch { }
                    return;
                }
                _upnpMapping = mapping;
                if (!IsConnectionAttemptCurrent(attempt))
                {
                    if (ReferenceEquals(_upnpMapping, mapping)) _upnpMapping = null;
                    try { mapping?.Remove(TimeSpan.FromSeconds(2)); } catch { }
                    return;
                }
                UiLog(mapping != null
                    ? $"UPnP: forwarded port {port} (TCP+UDP) to {lan} on your router"
                    : $"UPnP: no router accepted a forward — for internet play, forward port {port} (TCP+UDP) to {lan} manually");
            }
            else
            {
                UiLog($"UPnP auto-forward is off — for internet play, forward port {port} (TCP+UDP) to {lan} manually");
            }

            var pub = StunClient.DiscoverPublicAddress(TimeSpan.FromSeconds(2.0));
            if (!IsConnectionAttemptCurrent(attempt)) return;
            UiLog(pub != null
                ? $"internet joiners connect to {pub.Address}:{port}  (LAN: {lan}:{port})"
                : $"couldn't determine your public IP (offline or STUN blocked); LAN joiners use {lan}:{port}");
        }
        catch (Exception ex)
        {
            if (IsConnectionAttemptCurrent(attempt)) UiLog("(note) NAT setup skipped: " + ex.Message);
        }
    }

}
