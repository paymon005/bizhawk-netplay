using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using BizHawk.Client.Common;
using BizHawk.Client.EmuHawk;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;

namespace BizHawkNetplay.Tool
{
    /// <summary>
    /// M1 — 2-player lockstep netplay. Host or join over direct IP, runs the handshake +
    /// initial-state transfer on a background thread, then drives the session by owning the frame
    /// clock: EmuHawk is paused and a UI-thread timer advances exactly one confirmed frame per tick
    /// via <c>DoFrameAdvance</c>. A stall just skips the tick — no reliance on EmuHawk's own frame
    /// loop (which pausing would silence). Desync is checked by trading main-memory hashes over the
    /// reliable control channel.
    /// </summary>
    [ExternalTool("BizHawk Netplay",
        Description = "2-player lockstep netplay over direct IP.")]
    public sealed class NetplayToolForm : ToolFormBase, IExternalToolForm
    {
        private const int Protocol = 1;
        private const int DefaultPort = 47800;
        private const int ChecksumInterval = 60; // frames between desync-detection samples

        public ApiContainer? _apiContainer { get; set; }
        private ApiContainer APIs => _apiContainer!;

        [RequiredService] public IEmulator? _emulator { get; set; }
        [OptionalService] public IStatable? _statable { get; set; }

        // --- UI ---
        private readonly RadioButton _hostRadio;
        private readonly RadioButton _joinRadio;
        private readonly TextBox _ipBox;
        private readonly NumericUpDown _portBox;
        private readonly NumericUpDown _delayBox;
        private readonly Button _goButton;
        private readonly Button _disconnectButton;
        private readonly Button _probeButton;
        private readonly Button _testInputButton;
        private readonly CheckBox _verboseCheck;
        private readonly CheckBox _freezeInputCheck;
        private readonly CheckBox _forceDesyncCheck;
        private readonly CheckBox _rollbackCheck;
        private readonly CheckBox _simUnresponsiveCheck;
        private readonly NumericUpDown _simLatencyBox;
        private readonly Label _status;

        private int _simLatencyMs; // diagnostic: artificial one-way UDP delay for this session (0 = off)

        private bool Verbose => _verboseCheck.Checked;

        private int _startEmuFrame; // emulator FrameCount at session start, for drift detection
        private readonly TextBox _log;

        /// <summary>One control link to a peer. Host: one per joiner. Joiner: one (the host).</summary>
        private sealed class PeerLink
        {
            public TcpClient Tcp = null!;
            public ControlChannel Control = null!;
            public int RemotePort;            // the controller port this peer owns (host peer = 0)
            public IPEndPoint UdpEndpoint = null!;
            public Thread? Reader;
            public double PingMs = -1;        // guarded by _pingLock
            public int PingCount;             // guarded by _pingLock
            public long LastRecvTicks;        // UtcNow.Ticks of the last message from this peer (Interlocked)
            public string Label = "";
        }

        // --- Session state (all touched on the UI thread except where noted) ---
        private EmuHawkAdapter? _adapter;
        private ITransport? _transport;        // the FrameDriver's input channel (see below)
        private MeshUdpTransport? _mesh;       // direct peer-to-peer UDP: host and joiners both send to all peers
        private List<IPEndPoint> _meshOthers = new List<IPEndPoint>(); // joiner: the non-host peers in our mesh
        private TcpListener? _listener;
        private FrameDriver? _driver;
        private readonly List<PeerLink> _peers = new List<PeerLink>();
        private readonly System.Windows.Forms.Timer _frameTimer;
        private volatile bool _sessionActive;
        private bool _isHost;      // host is authoritative for desync detection + resync
        private int _playerCount = 2;
        private int _localPort;    // our controller port, for rebuilding the driver on resync
        private int _resyncCount;   // resyncs since the last confirmed re-sync (bounds infinite loops)
        private DateTime _lastResync = DateTime.MinValue; // debounces near-simultaneous resync triggers
        private bool _forceDesyncOnce; // diagnostic: corrupt the next checksum to exercise resync
        private const int MaxResyncs = 6;
        private const double ResyncGraceSeconds = 2.0;
        private bool _audioStatsLogged; // one-shot audio pipeline diagnostic per session
        private int _stallLog;     // throttles verbose stall messages

        // Desync detection: the host aggregates every peer's checksum for a frame (its own + each
        // joiner's); once it has them all it verifies they agree. Joiners just report to the host.
        private readonly object _hashLock = new object();
        private readonly Dictionary<int, List<uint>> _frameHashes = new Dictionary<int, List<uint>>();

        // Live round-trip time per control link, for connection-quality feedback.
        private readonly System.Diagnostics.Stopwatch _pingClock = new System.Diagnostics.Stopwatch();
        private readonly object _pingLock = new object();
        private int _sessionDelay;    // the input delay this session negotiated
        private bool _delayHintShown; // one-time "raise your delay" hint per session

        // Reconnect: when a joiner unexpectedly drops, the host freezes the session and waits for it to
        // rejoin (into the same port, with the current state) instead of ending. Host-side only — a
        // joiner that loses the host ends and the user rejoins manually. One outstanding drop at a time.
        private volatile bool _awaitingReconnect;
        private int _reconnectPort = -1;           // controller port waiting to be refilled
        private Thread? _reconnectThread;
        private DateTime _reconnectStarted;
        private const double ReconnectTimeoutSeconds = 60.0;
        // Host session context stashed so a rejoiner can be re-greeted with the same identity/params.
        private PeerIdentity? _hostIdentity;
        private SessionPreferences? _hostPrefs;
        private int _hostTcpPort;
        private int _hostUdpPort;

        // Liveness: pings go out on a wall-clock cadence (independent of frame stepping, so a
        // stalled-but-alive peer keeps answering), and a watchdog drops a link that has gone silent —
        // catching a frozen peer or a silent cable-pull that never breaks the TCP connection.
        private double _lastPingMs = -1;                // _pingClock time of the last ping we sent
        private const double PingIntervalMs = 400;      // ~2.5 pings/sec
        private const double PingTimeoutSeconds = 3.0;  // no message for this long => presumed dropped
        private volatile bool _simUnresponsive;         // diagnostic: act frozen (stop ping/pong) to test the watchdog

        // Sync mode + rollback config for the live session.
        private SyncMode _mode = SyncMode.Lockstep;   // negotiated; drives which strategy the driver builds
        private int _probeDepth = -1;                 // cached capability-probe depth (frames); -1 = not measured
        private int _rollbackDepth;                   // this session's savestate-ring depth when in rollback
        private const int RollbackDepthCap = 16;      // clamp the ring so resim cost + memory stay bounded

        // Saved EmuHawk config we override for the session's duration (keep running while unfocused).
        private Config? _config;
        private bool _prevRunInBackground;
        private bool _prevAcceptBackgroundInput;
        private bool _prevAcceptBackgroundInputControllerOnly;
        private bool _configApplied;

        private readonly System.Diagnostics.Stopwatch _paceClock = new System.Diagnostics.Stopwatch();
        private double _frameMs = 1000.0 / 60.0; // console frame period, drives real-time pacing

        // Raise the OS timer resolution to 1ms for the session so the WinForms frame timer fires
        // regularly (it's otherwise bound to the ~15ms system tick and jitters), which keeps audio
        // pumps steady and frame pacing smooth. Balanced by timeEndPeriod on session end.
        [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uMilliseconds);
        [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uMilliseconds);
        private bool _timerResRaised;

        protected override string WindowTitleStatic => "BizHawk Netplay";

        public NetplayToolForm()
        {
            SuspendLayout();
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(460, 320);

            _hostRadio = new RadioButton { Text = "Host", Checked = true, AutoSize = true, Location = new Point(12, 12) };
            _joinRadio = new RadioButton { Text = "Join", AutoSize = true, Location = new Point(80, 12) };
            _hostRadio.CheckedChanged += (_, __) => UpdateEnabled();

            var ipLabel = new Label { Text = "Host IP:", AutoSize = true, Location = new Point(12, 44) };
            _ipBox = new TextBox { Text = "127.0.0.1", Location = new Point(80, 41), Width = 160 };
            var portLabel = new Label { Text = "Port:", AutoSize = true, Location = new Point(260, 44) };
            _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = DefaultPort, Location = new Point(300, 41), Width = 70 };
            var delayLabel = new Label { Text = "Input delay:", AutoSize = true, Location = new Point(12, 76) };
            _delayBox = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 2, Location = new Point(90, 73), Width = 50 };
            _rollbackCheck = new CheckBox
            {
                Text = "Prefer rollback (if core qualifies)", AutoSize = true, Location = new Point(155, 75),
            };

            _goButton = new Button { Text = "Start Hosting", Location = new Point(12, 108), Width = 130 };
            _goButton.Click += (_, __) => OnGo();
            _disconnectButton = new Button { Text = "Disconnect", Location = new Point(150, 108), Width = 110, Enabled = false };
            _disconnectButton.Click += (_, __) => EndSession("disconnected by user");

            _probeButton = new Button { Text = "Capability Probe", Location = new Point(268, 108), Width = 130 };
            _probeButton.Click += (_, __) => RunProbe();

            _testInputButton = new Button { Text = "Test Input", Location = new Point(12, 140), Width = 130 };
            _testInputButton.Click += (_, __) => RunInputTest();

            _verboseCheck = new CheckBox { Text = "Verbose log", AutoSize = true, Location = new Point(410, 100) };
            _freezeInputCheck = new CheckBox { Text = "Freeze input (diag)", AutoSize = true, Location = new Point(410, 122) };
            _freezeInputCheck.CheckedChanged += (_, __) =>
                EmuHawkAdapter.ForceNeutralInput = _freezeInputCheck.Checked;
            _forceDesyncCheck = new CheckBox { Text = "Force desync (diag)", AutoSize = true, Location = new Point(410, 144) };
            _forceDesyncCheck.CheckedChanged += (_, __) =>
            {
                if (!_forceDesyncCheck.Checked) return;
                _forceDesyncOnce = true;
                _forceDesyncCheck.Checked = false;
                Log(_sessionActive ? "will inject a fake desync at the next checksum (tests resync)"
                                   : "arm this during a session to test resync");
            };

            var simLatencyLabel = new Label { Text = "Sim latency ms (diag):", AutoSize = true, Location = new Point(410, 168) };
            _simLatencyBox = new NumericUpDown { Minimum = 0, Maximum = 500, Increment = 10, Value = 0, Location = new Point(410, 186), Width = 60 };
            _simUnresponsiveCheck = new CheckBox { Text = "Simulate unresponsive (diag)", AutoSize = true, Location = new Point(410, 214) };
            _simUnresponsiveCheck.CheckedChanged += (_, __) =>
            {
                _simUnresponsive = _simUnresponsiveCheck.Checked;
                if (_sessionActive)
                    Log(_simUnresponsive
                        ? "simulating an unresponsive peer — we've stopped answering pings; the other side should drop us in ~3s"
                        : "resumed responding to pings");
            };

            _status = new Label { Text = "Idle.", AutoSize = true, Location = new Point(150, 145), ForeColor = Color.DimGray };

            _log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Location = new Point(12, 170), Size = new Size(536, 238),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font(FontFamily.GenericMonospace, 9f),
            };

            Controls.AddRange(new Control[]
            {
                _hostRadio, _joinRadio, ipLabel, _ipBox, portLabel, _portBox,
                delayLabel, _delayBox, _rollbackCheck, _goButton, _disconnectButton, _probeButton, _testInputButton,
                _verboseCheck, _freezeInputCheck, _forceDesyncCheck, simLatencyLabel, _simLatencyBox,
                _simUnresponsiveCheck, _status, _log,
            });
            ResumeLayout(false);

            _frameTimer = new System.Windows.Forms.Timer();
            _frameTimer.Tick += (_, __) => FrameTick();

            UpdateEnabled();
        }

        public override void Restart()
        {
            // ROM load / tool re-init: tear down any live session.
            if (_sessionActive) EndSession("emulator restarted");
            UpdateEnabled();
        }

        // ------------------------------------------------------------------ start

        private void OnGo()
        {
            if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
            if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }

            try
            {
                _adapter = new EmuHawkAdapter(APIs, _emulator, _statable);
                if (!_adapter.VerifyDeterministicMode())
                    Log("WARNING: core does not report deterministic emulation — desyncs are likely.");
                if (!_adapter.HasBindings)
                    Log($"WARNING: input may not register — {_adapter.BindingDiagnostic}");

                _isHost = _hostRadio.Checked;
                int players = _adapter.PortCount; // one network player per controller port the core exposes
                if (_hostRadio.Checked && players < 2)
                {
                    Log($"this core exposes only {players} controller port — configure at least 2 controllers to host netplay.");
                    SetBusy(false); return;
                }

                // Freeze the emulator NOW. Otherwise it keeps free-running between probing/exporting
                // its state and the peers arriving, so the sims start on different frames and desync
                // immediately. Paused here == the frame all peers resume from. (Probing below advances
                // frames invisibly and restores, so it must be paused first.)
                APIs.EmuClient.Pause();

                // Rollback is offered for any player count (host-relay), gated on the capability probe
                // and every peer opting in — the negotiator has the final say.
                bool wantRollback = _rollbackCheck.Checked;
                var prefs = new SessionPreferences((int)_delayBox.Value, wantRollback);
                var id = BuildIdentity(_adapter, wantRollback);
                int port = (int)_portBox.Value;
                _simLatencyMs = (int)_simLatencyBox.Value; // diagnostic artificial UDP delay for this session
                if (_simLatencyMs > 0)
                    Log($"simulating {_simLatencyMs}ms one-way UDP latency (~{2 * _simLatencyMs}ms RTT) — diagnostic");

                SetBusy(true);
                if (_hostRadio.Checked)
                {
                    _mesh = MeshUdpTransport.Bind(port); _transport = WrapSimLatency(_mesh);
                    var state = _adapter.ExportState();
                    Log($"exported {state.Length / 1024}KiB initial state; hosting {players} players");
                    StartThread(() => HostThread(port, id, prefs, state, _mesh.LocalPort, players));
                }
                else
                {
                    if (!IPAddress.TryParse(_ipBox.Text.Trim(), out var _))
                    { Log("Enter a valid host IP."); SetBusy(false); return; }
                    _mesh = MeshUdpTransport.Bind(0); _transport = WrapSimLatency(_mesh);
                    string ip = _ipBox.Text.Trim();
                    StartThread(() => JoinThread(ip, port, id, prefs, _mesh.LocalPort));
                }
            }
            catch (Exception ex)
            {
                Log("start failed: " + ex.Message);
                SetBusy(false);
            }
        }

        private void HostThread(int port, PeerIdentity id, SessionPreferences prefs, byte[] state, int udpLocalPort, int players)
        {
            // Remember what a rejoiner needs to be greeted with if a peer later drops.
            _hostIdentity = id; _hostPrefs = prefs; _hostTcpPort = port; _hostUdpPort = udpLocalPort;
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                int need = players - 1;
                UiLog($"hosting on TCP+UDP {port} — waiting for {need} player(s) to join…");

                var links = new List<PeerLink>();
                var greetings = new List<Handshake.JoinerGreeting>();
                for (int i = 0; i < need; i++)
                {
                    var tcp = _listener.AcceptTcpClient();
                    try { tcp.NoDelay = true; } catch { } // control latency matters for ping + resync
                    var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                    var channel = new ControlChannel(tcp.GetStream());
                    var greet = Handshake.HostGreet(channel, id, prefs, udpLocalPort);
                    int assignedPort = i + 1;
                    links.Add(new PeerLink
                    {
                        Tcp = tcp,
                        Control = channel,
                        RemotePort = assignedPort,
                        UdpEndpoint = new IPEndPoint(remoteIp, greet.UdpPort),
                        Label = $"P{assignedPort + 1} ({remoteIp})",
                    });
                    greetings.Add(greet);
                    UiLog($"P{assignedPort + 1} joined ({i + 1}/{need})");
                }
                try { _listener.Stop(); } catch { }
                _listener = null;

                // The host decides the authoritative delay (max anyone asked) once everyone's in.
                int finalDelay = prefs.InputDelay;
                foreach (var g in greetings) finalDelay = Math.Max(finalDelay, g.Prefs.InputDelay);

                // The host is authoritative on sync mode too. Grant rollback only if the host opted in
                // AND every joiner pairwise negotiates to rollback (each opted in and cleared the probe
                // depth threshold); if any peer can't or won't, everyone runs lockstep.
                SyncMode mode = SyncMode.Lockstep;
                if (prefs.WantRollback && greetings.Count >= 1)
                {
                    bool allRollback = true;
                    foreach (var g in greetings)
                        if (SessionNegotiator.Negotiate(id, g.Id, prefs, g.Prefs).Mode != SyncMode.Rollback)
                        { allRollback = false; break; }
                    mode = allRollback ? SyncMode.Rollback : SyncMode.Lockstep;
                }

                // Each joiner gets every OTHER joiner's UDP endpoint so it can build a direct mesh
                // (it reaches the host at the address it connected to, so the host is left off the list).
                foreach (var link in links)
                {
                    var others = new List<IPEndPoint>();
                    foreach (var o in links) if (!ReferenceEquals(o, link)) others.Add(o.UdpEndpoint);
                    Handshake.HostSendWelcome(link.Control, link.RemotePort, players, finalDelay, mode, state, others);
                }

                // The host sends its own input directly to every joiner.
                var eps = new List<IPEndPoint>();
                foreach (var link in links) eps.Add(link.UdpEndpoint);
                _mesh!.SetPeers(eps);

                BeginInvokeUi(() => BeginSessionHost(links, players, finalDelay, mode));
            }
            catch (Exception ex) { BeginInvokeUi(() => FailSession(ex.Message)); }
        }

        private void JoinThread(string ip, int port, PeerIdentity id, SessionPreferences prefs, int udpLocalPort)
        {
            try
            {
                UiLog($"connecting to {ip}:{port}…");
                var tcp = new TcpClient();
                tcp.Connect(ip, port);
                try { tcp.NoDelay = true; } catch { } // control latency matters for ping + resync
                var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                var channel = new ControlChannel(tcp.GetStream());
                var sp = Handshake.RunClientMulti(channel, id, prefs, udpLocalPort);
                var link = new PeerLink
                {
                    Tcp = tcp,
                    Control = channel,
                    RemotePort = 0, // the host
                    UdpEndpoint = new IPEndPoint(remoteIp, sp.RemoteUdpPort),
                    Label = $"host ({remoteIp})",
                };
                BeginInvokeUi(() => BeginSessionJoiner(sp, link));
            }
            catch (Exception ex) { BeginInvokeUi(() => FailSession(ex.Message)); }
        }

        // ------------------------------------------------------------------ session

        private void BeginSessionHost(List<PeerLink> links, int players, int delay, SyncMode mode)
        {
            try
            {
                _peers.Clear(); _peers.AddRange(links);
                _isHost = true; _playerCount = players; _sessionDelay = delay; _localPort = 0;
                Log($"emulator frame at start: {APIs.Emulation.FrameCount()}");
                Log($"all {players} players connected — you are P1 (host)");
                BeginSessionCommon(mode, $"{links.Count} peer(s)");
            }
            catch (Exception ex) { FailSession(ex.Message); }
        }

        private void BeginSessionJoiner(SessionParams sp, PeerLink hostLink)
        {
            try
            {
                if (sp.InitialState != null)
                {
                    _adapter!.ImportState(sp.InitialState);
                    Log($"imported {sp.InitialState.Length / 1024}KiB host state");
                }
                _peers.Clear(); _peers.Add(hostLink);
                _isHost = false; _playerCount = sp.PlayerCount; _sessionDelay = sp.InputDelay; _localPort = sp.LocalPort;
                // Direct mesh: send to the host (at the address we connected to) plus every other joiner.
                _meshOthers = new List<IPEndPoint>(sp.MeshPeers);
                ApplyJoinerMesh();
                // Both peers should print the SAME number here; if not, the start is misaligned.
                Log($"emulator frame at start: {APIs.Emulation.FrameCount()}");
                Log($"joined as P{sp.LocalPort + 1} of {sp.PlayerCount}");
                BeginSessionCommon(sp.Mode, hostLink.Label);
            }
            catch (Exception ex) { FailSession(ex.Message); }
        }

        /// <summary>The role-independent session bring-up: driver, audio, per-link readers, pacing.</summary>
        private void BeginSessionCommon(SyncMode mode, string remoteLabel)
        {
            _mode = mode;
            if (mode == SyncMode.Rollback)
            {
                // Ring depth = this peer's probe depth, clamped so resim cost + memory stay bounded.
                // Each peer bounds its own ring independently; correctness never needs them equal.
                int d = _probeDepth > 0 ? _probeDepth : ProbeResult.RollbackDepthThreshold;
                _rollbackDepth = Math.Max(ProbeResult.RollbackDepthThreshold, Math.Min(d, RollbackDepthCap));
                if (_playerCount > 2)
                    Log($"rollback with {_playerCount} players is experimental — inputs relay through the " +
                        "host (two hops), so rollbacks may run deeper. Uncheck 'Prefer rollback' if it feels choppy.");
            }
            _driver = CreateDriver();

            ApplyBackgroundConfig(true); // don't let EmuHawk pause/ignore input when unfocused
            try { APIs.EmuClient.EnableRewind(false); } catch { } // rewind would jump the frame count -> desync
            APIs.EmuClient.Pause(); // we own the clock now
            _startEmuFrame = APIs.Emulation.FrameCount(); // baseline for frame-advance drift checks
            _resyncCount = 0;
            _lastResync = DateTime.MinValue;
            lock (_hashLock) { _frameHashes.Clear(); }
            _driver.Start();
            _sessionActive = true;

            // We own the frame clock (EmuHawk stays paused), so its loop never pumps sound —
            // hand the adapter EmuHawk's Sound device so it can drive audio after each frame.
            _audioStatsLogged = false;
            _adapter!.EnableAudio(MainForm as BizHawk.Client.EmuHawk.MainForm);
            Log(_adapter.AudioReady ? "audio enabled — " + _adapter.AudioDiagnostic
                                    : "(note) audio unavailable: " + _adapter.AudioDiagnostic);

            // One reader thread per control link.
            foreach (var link in _peers)
            {
                var l = link;
                l.LastRecvTicks = DateTime.UtcNow.Ticks; // seed liveness so the watchdog has a baseline
                l.Reader = new Thread(() => PeerReaderLoop(l)) { IsBackground = true, Name = "BizHawkNetplay-control" };
                l.Reader.Start();
            }
            _lastPingMs = -1; // send the first ping immediately

            // Real-time pacing: tick often and advance however many frames wall-clock demands,
            // so irregular WinForms-timer firing doesn't run the game slow.
            _frameMs = FrameMs();
            _delayHintShown = false;
            lock (_pingLock) { foreach (var link in _peers) { link.PingMs = -1; link.PingCount = 0; } }
            _pingClock.Restart();
            _paceClock.Restart();
            try { if (!_timerResRaised) { timeBeginPeriod(1); _timerResRaised = true; } } catch { }
            _frameTimer.Interval = 2;
            _frameTimer.Start();

            Status($"in session — {(mode == SyncMode.Rollback ? "rollback" : "lockstep")}, " +
                   $"you are P{_localPort + 1}/{_playerCount}, delay {_sessionDelay}", Color.Green);
            Log($"session started vs {remoteLabel}");
            _disconnectButton.Enabled = true;
        }

        private void FrameTick()
        {
            if (!_sessionActive || _driver == null) return;
            try
            {
                // Keep the audio device fed every tick, independent of how many frames we step this
                // tick (or none, during a stall) — the ring buffer decouples playback from stepping.
                _adapter?.PumpAudio();

                // Frozen while a dropped peer is being waited on — don't advance until the rejoin
                // resyncs everyone. Audio is already pumped (it drains to silence, which is correct).
                if (_awaitingReconnect) return;

                // Sticky pause: we own the frame clock. If the user (or anything) unpauses EmuHawk,
                // its own loop would advance the core on top of ours and desync — snap it back.
                if (!APIs.EmuClient.IsPaused())
                {
                    APIs.EmuClient.Pause();
                    if (Verbose) Log("re-paused (the session owns the frame clock — don't unpause)");
                }

                // If EmuHawk's own loop slipped in extra core frames (e.g. a brief unpause), our
                // counter and the core have diverged — report it plainly rather than as a desync.
                int emuDelta = APIs.Emulation.FrameCount() - _startEmuFrame;
                if (emuDelta != _driver.CurrentFrame)
                {
                    int diff = emuDelta - _driver.CurrentFrame;
                    string why = diff > 0
                        ? $"EmuHawk advanced {diff} extra frame(s) — did you unpause?"
                        : $"the core's frame count jumped back {-diff} — a rewind/load-state hotkey fired?";
                    EndSession(why + " The tool must own the frame clock; avoid EmuHawk hotkeys during a session.");
                    return;
                }

                // Advance up to where wall-clock says we should be, bounded so a late tick doesn't
                // burst into a huge catch-up. Each frame: drain network, capture local, gate, step.
                int target = (int)(_paceClock.Elapsed.TotalMilliseconds / _frameMs);
                int budget = 8;
                while (_driver.CurrentFrame < target && budget-- > 0)
                {
                    _driver.PumpNetwork();       // drain remote input + resend our redundant window
                    _driver.CaptureLocalInput(); // capture local pad (paused-safe, via IInputApi) + send
                    if (!_driver.CurrentFrameReady())
                    {
                        if (Verbose && (++_stallLog % 30 == 0))
                            Log($"stalling at frame {_driver.CurrentFrame} — waiting for remote input");
                        break; // retry next tick
                    }
                    // Step the core with ONLY our merged inputs — deterministic, bypasses EmuHawk's
                    // input chain and hotkeys.
                    _adapter!.AdvanceFrame(_driver.CurrentInputs());
                    _driver.CompleteFrame();
                    MaybeSendChecksum();
                }

                // Liveness runs every tick, independent of stepping (so a stall doesn't stop our pings
                // and a dead link is still detected while we're waiting on it).
                MaybeSendPing();
                CheckLinkTimeouts();

                // One-shot audio pipeline snapshot ~2s in, so a single test shows where sound breaks.
                if (!_audioStatsLogged && _driver.CurrentFrame >= 120)
                {
                    _audioStatsLogged = true;
                    Log(_adapter!.AudioStats());
                }
                else if (Verbose && _driver.CurrentFrame % 300 == 0 && _driver.CurrentFrame > 0)
                {
                    Log(_adapter!.AudioStats());
                }

                double ping = -1;
                lock (_pingLock) { foreach (var link in _peers) if (link.PingMs > ping) ping = link.PingMs; }

                // Feed the sync strategy a clock/quality report so rollback's time-sync can size its
                // prediction horizon. The sim latency (if any) isn't on the TCP ping path, so fold it
                // into the reported RTT to keep the horizon consistent with the delayed UDP inputs.
                double effRttMs = (ping < 0 ? 0 : ping) + 2.0 * _simLatencyMs;
                _driver.Strategy.OnPacingReport(new PacingInfo(effRttMs, 0, 0));

                string pingStr = ping < 0 ? "" : $" — ping {ping:F0}ms{(_peers.Count > 1 ? " (worst)" : "")}";
                string rbStr = _driver.Strategy is RollbackStrategy rbs
                    ? $" — rollback ×{rbs.RollbackCount} (last d{rbs.LastRollbackDepth}, max d{rbs.MaxRollbackDepthSeen}, tsync {rbs.TimeSyncStalls})"
                    : "";
                Status($"in session — frame {_driver.CurrentFrame}{pingStr}{rbStr}", Color.Green);
            }
            catch (Exception ex) { EndSession("session error: " + ex.Message); }
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
                if (!rb.TryConfirmedChecksum(ChecksumInterval, out frame, out hash)) return;
            }
            else
            {
                frame = _driver.CurrentFrame;
                if (frame % ChecksumInterval != 0) return;
                hash = _adapter!.HashMainMemory();
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
            if (_isHost) RecordChecksum(frame, hash);
            else if (_peers.Count > 0)
            {
                try { _peers[0].Control.Send(ControlMessageType.Checksum, EncodeChecksum(frame, hash)); }
                catch { /* control link gone; the reader loop will end the session */ }
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
            foreach (var link in _peers)
            {
                try { link.Control.Send(ControlMessageType.Ping, body); } catch { }
            }
        }

        /// <summary>
        /// Watchdog: a link that hasn't sent us anything for <see cref="PingTimeoutSeconds"/> is presumed
        /// dropped (frozen peer or a silent cable-pull that never broke TCP) and routed into the same
        /// drop handling as a broken connection. Pings/pongs are serviced on the reader thread regardless
        /// of stepping, so a merely stalled — but alive — peer keeps answering and is never flagged here.
        /// </summary>
        private void CheckLinkTimeouts()
        {
            if (_awaitingReconnect) return; // already holding for a reconnect
            long now = DateTime.UtcNow.Ticks;
            long limit = TimeSpan.FromSeconds(PingTimeoutSeconds).Ticks;
            PeerLink? dead = null;
            foreach (var link in _peers)
            {
                long last = Interlocked.Read(ref link.LastRecvTicks);
                if (last != 0 && now - last > limit) { dead = link; break; }
            }
            if (dead != null)
                OnPeerLinkLost(dead, $"no response for {PingTimeoutSeconds:F0}s (ping timeout)");
        }

        /// <summary>
        /// Once ping is stable, if the negotiated input delay is lower than the worst link's round-trip
        /// really needs, say so once — too-low delay is the usual cause of constant stalling on a real
        /// network. Lockstep needs delay·frameMs to cover the one-way latency (≈ RTT/2).
        /// </summary>
        private void MaybeHintDelay()
        {
            if (_delayHintShown || _peers.Count == 0) return;
            double worst = -1; int minCount = int.MaxValue;
            lock (_pingLock)
            {
                foreach (var link in _peers)
                {
                    if (link.PingMs > worst) worst = link.PingMs;
                    if (link.PingCount < minCount) minCount = link.PingCount;
                }
            }
            if (minCount < 6 || worst < 0) return;
            _delayHintShown = true;
            int suggested = (int)Math.Ceiling((worst / 2.0) / _frameMs) + 1;
            if (suggested > _sessionDelay)
                Log($"worst link ping ~{worst:F0}ms: input delay {suggested} is recommended for smooth play " +
                    $"(this session is {_sessionDelay}). If it stalls, reconnect with a higher 'Input delay'.");
            else
                Log($"worst link ping ~{worst:F0}ms: input delay {_sessionDelay} is comfortable for this link.");
        }

        /// <summary>Reader loop for one control link. Dispatch depends on our role.</summary>
        private void PeerReaderLoop(PeerLink link)
        {
            try
            {
                while (_sessionActive)
                {
                    var (type, body) = link.Control.Receive();
                    Interlocked.Exchange(ref link.LastRecvTicks, DateTime.UtcNow.Ticks); // liveness heartbeat
                    if (type == ControlMessageType.Checksum && body.Length == 8)
                    {
                        // Only the host aggregates; a joiner never receives checksums.
                        if (_isHost) { DecodeChecksum(body, out int frame, out uint hash); RecordChecksum(frame, hash); }
                    }
                    else if (type == ControlMessageType.Ping && body.Length == 8)
                    {
                        if (!_simUnresponsive) // diagnostic: a "frozen" peer stops answering pings
                            try { link.Control.Send(ControlMessageType.Pong, body); } catch { }
                    }
                    else if (type == ControlMessageType.Pong && body.Length == 8)
                    {
                        double t0 = BitConverter.ToDouble(body, 0);
                        double rtt = _pingClock.Elapsed.TotalMilliseconds - t0;
                        if (rtt >= 0)
                        {
                            lock (_pingLock)
                            {
                                link.PingMs = link.PingMs < 0 ? rtt : 0.8 * link.PingMs + 0.2 * rtt;
                                link.PingCount++;
                            }
                            BeginInvokeUi(MaybeHintDelay);
                        }
                    }
                    else if (type == ControlMessageType.PeerList)
                    {
                        // Host reshuffled the mesh (e.g. someone rejoined) — update who we send to.
                        if (!_isHost)
                        {
                            var eps = HandshakeCodec.DecodeEndpoints(body);
                            BeginInvokeUi(() =>
                            {
                                _meshOthers = eps;
                                ApplyJoinerMesh();
                                if (Verbose) Log($"mesh updated: {eps.Count} other peer(s)");
                            });
                        }
                    }
                    else if (type == ControlMessageType.Resync)
                    {
                        var state = body; // authoritative whole-core state from the host
                        if (!_isHost) BeginInvokeUi(() => ApplyResyncAsJoiner(state));
                    }
                    else if (type == ControlMessageType.Bye)
                    {
                        BeginInvokeUi(() => EndSession($"{link.Label} left the session"));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_sessionActive) BeginInvokeUi(() => OnPeerLinkLost(link, ex.Message));
            }
        }

        /// <summary>
        /// Host desync detection: gather each peer's checksum for a frame (our own + every joiner's).
        /// Once all <see cref="_playerCount"/> are in, they must agree; if not, resync everyone. Called
        /// from the UI thread (our own hash) and from reader threads (joiners'), hence the lock.
        /// </summary>
        private void RecordChecksum(int frame, uint hash)
        {
            bool complete = false, mismatch = false;
            lock (_hashLock)
            {
                if (!_frameHashes.TryGetValue(frame, out var list)) { list = new List<uint>(); _frameHashes[frame] = list; }
                list.Add(hash);
                if (list.Count >= _playerCount)
                {
                    complete = true;
                    for (int i = 1; i < list.Count; i++) if (list[i] != list[0]) { mismatch = true; break; }
                    _frameHashes.Remove(frame);
                }
                // Drop very old partial entries (a peer that never reported a frame) to bound memory.
                if (_frameHashes.Count > 32)
                {
                    var stale = new List<int>();
                    foreach (var k in _frameHashes.Keys) if (k < frame - 600) stale.Add(k);
                    foreach (var k in stale) _frameHashes.Remove(k);
                }
            }
            if (!complete) return;
            if (mismatch) BeginInvokeUi(() => OnHostDesync(frame));
            else if (_resyncCount != 0)
                BeginInvokeUi(() => { if (_resyncCount != 0) { _resyncCount = 0; Log("back in sync — recovery confirmed"); } });
            else if (Verbose)
                BeginInvokeUi(() => Log($"checksum frame {frame}: all {_playerCount} agree"));
        }

        private void OnHostDesync(int frame)
        {
            if ((DateTime.UtcNow - _lastResync).TotalSeconds < ResyncGraceSeconds) return; // just resynced; give it time
            Log($"DESYNC at frame {frame} — peers disagree");
            PerformResyncAsHost();
        }

        /// <summary>
        /// Host recovery: snapshot the diverged state to slot 10, broadcast an authoritative state to
        /// every peer, and rebuild from a clean baseline. Joiners adopt it in <see cref="ApplyResyncAsJoiner"/>.
        /// Bounded by <see cref="MaxResyncs"/> so a persistent (non-transient) desync gives up instead
        /// of looping; a short grace window debounces repeat triggers for the same desync.
        /// </summary>
        private void PerformResyncAsHost()
        {
            if (!_sessionActive || !_isHost) return;
            if ((DateTime.UtcNow - _lastResync).TotalSeconds < ResyncGraceSeconds) return; // debounce
            try
            {
                APIs.SaveState.SaveSlot(10, suppressOSD: false);
                Log("saved diverged host state to slot 10 for inspection");
            }
            catch (Exception ex) { Log("(warning) couldn't save slot 10: " + ex.Message); }

            if (++_resyncCount > MaxResyncs)
            {
                EndSession($"persistent desync — gave up after {MaxResyncs} resync attempts (likely a determinism bug)");
                return;
            }
            try
            {
                var state = _adapter!.ExportState();
                SendToAllPeers(ControlMessageType.Resync, state);
                RebuildDriver();
                Log($"resync #{_resyncCount}: sent {state.Length / 1024}KiB host state to {_peers.Count} peer(s); resuming");
            }
            catch (Exception ex) { EndSession("resync failed: " + ex.Message); }
        }

        /// <summary>Joiner: adopt the host's authoritative state and rebuild from a clean baseline.</summary>
        private void ApplyResyncAsJoiner(byte[] state)
        {
            if (!_sessionActive) return;
            if (++_resyncCount > MaxResyncs)
            {
                EndSession($"persistent desync — gave up after {MaxResyncs} resync attempts (likely a determinism bug)");
                return;
            }
            try
            {
                _adapter!.ImportState(state);
                RebuildDriver();
                Log($"resync #{_resyncCount}: imported {state.Length / 1024}KiB host state; resuming");
            }
            catch (Exception ex) { EndSession("resync apply failed: " + ex.Message); }
        }

        /// <summary>
        /// Rebuild the frame driver from the current core state as a fresh frame-0 baseline: new
        /// pipeline, cleared checksums, reset pacing and drift baseline. In-flight pre-resync UDP
        /// datagrams carry high frame numbers and are dropped by the FrameDriver's far-future guard.
        /// </summary>
        private void RebuildDriver()
        {
            _driver = CreateDriver();
            _startEmuFrame = APIs.Emulation.FrameCount();
            lock (_hashLock) { _frameHashes.Clear(); }
            _driver.Start();
            _lastResync = DateTime.UtcNow;
            _paceClock.Restart();
        }

        /// <summary>
        /// Build the frame driver for the negotiated <see cref="_mode"/>: rollback plugs the
        /// <see cref="RollbackStrategy"/> (with its savestate ring depth) in behind the same seam
        /// lockstep uses, and widens the driver's network window to the ring depth so late corrections
        /// reach the pipeline. Used for both session start and resync rebuilds so they never diverge.
        /// </summary>
        private FrameDriver CreateDriver()
        {
            if (_mode == SyncMode.Rollback)
                return new FrameDriver(_adapter!, _transport!,
                    p => new RollbackStrategy(p, _adapter!, _localPort, _rollbackDepth, FrameMs()),
                    _localPort, _sessionDelay, redundancy: 8, rollbackWindow: _rollbackDepth);

            return new FrameDriver(_adapter!, _transport!, p => new LockstepStrategy(p),
                _localPort, _sessionDelay, redundancy: 8);
        }

        /// <summary>Wrap the input transport in the artificial-latency simulator if the diagnostic is set.</summary>
        private ITransport WrapSimLatency(ITransport inner)
            => _simLatencyMs > 0 ? new LatencySimTransport(inner, _simLatencyMs) : inner;

        private void SendToAllPeers(ControlMessageType type, byte[] body)
        {
            foreach (var link in _peers)
            {
                try { link.Control.Send(type, body); } catch { }
            }
        }

        // ------------------------------------------------------------------ reconnect

        /// <summary>
        /// A peer's control link dropped unexpectedly (not a clean Bye). The host holds the session
        /// open and waits for it to rejoin into the same port; a joiner that lost the host just ends
        /// (the host is the hub — the user rejoins with the Join button). One drop at a time.
        /// </summary>
        private void OnPeerLinkLost(PeerLink link, string why)
        {
            if (!_sessionActive) return;

            if (!_isHost)
            {
                EndSession($"lost connection to {link.Label}: {why} — click Join to reconnect");
                return;
            }
            if (_awaitingReconnect)
            {
                EndSession($"a second peer ({link.Label}) dropped during a reconnect: {why}");
                return;
            }

            _awaitingReconnect = true;
            _reconnectPort = link.RemotePort;
            _reconnectStarted = DateTime.UtcNow;

            _peers.Remove(link);
            try { link.Tcp?.Close(); } catch { }
            UpdateMeshPeers(); // stop sending input to the dead endpoint

            Log($"{link.Label} dropped ({why}) — holding the session; waiting up to " +
                $"{ReconnectTimeoutSeconds:F0}s for a rejoin on TCP {_hostTcpPort}…");
            Status($"P{_reconnectPort + 1} dropped — waiting to rejoin…", Color.DarkOrange);

            _reconnectThread = new Thread(() => ReconnectAcceptLoop(_reconnectPort))
            { IsBackground = true, Name = "BizHawkNetplay-reconnect" };
            _reconnectThread.Start();
        }

        /// <summary>Host: point our mesh at every currently-connected joiner's UDP endpoint.</summary>
        private void UpdateMeshPeers()
        {
            if (_mesh == null) return;
            var eps = new List<IPEndPoint>();
            foreach (var l in _peers) eps.Add(l.UdpEndpoint);
            try { _mesh.SetPeers(eps); } catch { }
        }

        /// <summary>Joiner: point our mesh at the host (peer 0) plus every other joiner we've been told about.</summary>
        private void ApplyJoinerMesh()
        {
            if (_mesh == null || _peers.Count == 0) return;
            var eps = new List<IPEndPoint> { _peers[0].UdpEndpoint }; // the host
            eps.AddRange(_meshOthers);
            try { _mesh.SetPeers(eps); } catch { }
        }

        /// <summary>
        /// Host reconnect listener (background thread): reopen the TCP port and wait for the dropped
        /// player to reconnect. Re-greet — which re-validates ROM/core/layout still match — then hand
        /// off to the UI thread to welcome them back. Gives up (ends the session) after the timeout.
        /// </summary>
        private void ReconnectAcceptLoop(int freedPort)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, _hostTcpPort);
                listener.Start();
                while (_sessionActive && _awaitingReconnect)
                {
                    if ((DateTime.UtcNow - _reconnectStarted).TotalSeconds > ReconnectTimeoutSeconds)
                    {
                        BeginInvokeUi(() => { if (_awaitingReconnect) EndSession("no rejoin within the timeout"); });
                        return;
                    }
                    if (!listener.Pending()) { Thread.Sleep(100); continue; }

                    var tcp = listener.AcceptTcpClient();
                    try { tcp.NoDelay = true; } catch { }
                    var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
                    var channel = new ControlChannel(tcp.GetStream());
                    try
                    {
                        var greet = Handshake.HostGreet(channel, _hostIdentity!, _hostPrefs!, _hostUdpPort);
                        var udpEp = new IPEndPoint(remoteIp, greet.UdpPort);
                        BeginInvokeUi(() => CompleteReconnect(tcp, channel, remoteIp, udpEp, freedPort));
                        return; // one rejoin fills the slot
                    }
                    catch (Exception ex)
                    {
                        // Rejected (e.g. wrong ROM/core) — refuse this one and keep waiting for a valid rejoin.
                        UiLog($"rejected a rejoin attempt: {ex.Message}");
                        try { tcp.Close(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                BeginInvokeUi(() => { if (_awaitingReconnect) EndSession("reconnect listener failed: " + ex.Message); });
            }
            finally { try { listener?.Stop(); } catch { } }
        }

        /// <summary>
        /// UI thread: finish a reconnect. Export the current authoritative state once, welcome the
        /// rejoiner back into its old port with it, resync every surviving peer to the same state, and
        /// rebuild our own driver — so everyone lands on a common frame-0 baseline and resumes together.
        /// </summary>
        private void CompleteReconnect(TcpClient tcp, ControlChannel channel, IPAddress remoteIp, IPEndPoint udpEp, int freedPort)
        {
            if (!_sessionActive || !_awaitingReconnect) { try { tcp.Close(); } catch { } return; }
            try
            {
                var state = _adapter!.ExportState();

                // The rejoiner's mesh peers = every current survivor (it reaches the host directly).
                var rejoinerOthers = new List<IPEndPoint>();
                foreach (var l in _peers) rejoinerOthers.Add(l.UdpEndpoint);
                // The rejoiner adopts this state + mesh via Welcome and rebuilds fresh on its own side.
                Handshake.HostSendWelcome(channel, freedPort, _playerCount, _sessionDelay, _mode, state, rejoinerOthers);

                var link = new PeerLink
                {
                    Tcp = tcp, Control = channel, RemotePort = freedPort,
                    UdpEndpoint = udpEp, Label = $"P{freedPort + 1} ({remoteIp})",
                };
                link.LastRecvTicks = DateTime.UtcNow.Ticks; // seed liveness for the rejoined link
                _peers.Add(link);
                UpdateMeshPeers(); // our own mesh now includes the rejoiner
                link.Reader = new Thread(() => PeerReaderLoop(link)) { IsBackground = true, Name = "BizHawkNetplay-control" };
                link.Reader.Start();

                // Bring each survivor up to date: refresh its mesh with the rejoiner's endpoint, then
                // resync it to the same state. (For a 2P session there are no survivors.)
                foreach (var l in _peers)
                {
                    if (ReferenceEquals(l, link)) continue;
                    var others = new List<IPEndPoint>();
                    foreach (var o in _peers) if (!ReferenceEquals(o, l)) others.Add(o.UdpEndpoint);
                    try { l.Control.Send(ControlMessageType.PeerList, HandshakeCodec.EncodeEndpoints(others)); } catch { }
                    try { l.Control.Send(ControlMessageType.Resync, state); } catch { }
                }

                _awaitingReconnect = false;
                _reconnectPort = -1;
                _resyncCount = 0; // a rejoin is a fresh, healthy start — not a desync loop
                RebuildDriver();
                Log($"{link.Label} reconnected — resynced {_peers.Count} peer(s); resuming");
                Status($"reconnected P{freedPort + 1} — resuming", Color.Green);
            }
            catch (Exception ex) { EndSession("reconnect failed: " + ex.Message); }
        }

        private void FailSession(string reason)
        {
            Log("connection failed: " + reason);
            TeardownNetwork();
            try { _adapter?.DisableAudio(); } catch { } // restore EmuHawk's normal audio wiring
            ApplyBackgroundConfig(false);
            try { APIs.EmuClient.Unpause(); } catch { } // undo the freeze from OnGo
            SetBusy(false);
            Status("Idle.", Color.DimGray);
        }

        private void EndSession(string reason)
        {
            if (!_sessionActive && _listener == null && _peers.Count == 0) { SetBusy(false); return; }
            _sessionActive = false;
            _frameTimer.Stop();
            _simUnresponsive = false; _simUnresponsiveCheck.Checked = false; // clear the diagnostic
            try { if (_timerResRaised) { timeEndPeriod(1); _timerResRaised = false; } } catch { }

            try { SendToAllPeers(ControlMessageType.Bye, Array.Empty<byte>()); } catch { }
            TeardownNetwork();

            try { _adapter?.DisableAudio(); } catch { } // restore EmuHawk's normal audio wiring
            ApplyBackgroundConfig(false); // restore the user's focus/pause preferences
            try { APIs.EmuClient.Unpause(); } catch { }
            lock (_hashLock) { _frameHashes.Clear(); }

            Log("session ended: " + reason);
            Status("Idle.", Color.DimGray);
            SetBusy(false);
        }

        private void TeardownNetwork()
        {
            // Stop any in-flight reconnect wait first; its loop exits once these flags clear.
            _awaitingReconnect = false;
            var reconnect = _reconnectThread;
            _reconnectThread = null;
            _reconnectPort = -1;

            try { _listener?.Stop(); } catch { }
            _listener = null;

            var peers = new List<PeerLink>(_peers);
            _peers.Clear();
            foreach (var link in peers) { try { link.Tcp?.Close(); } catch { } }

            try { (_transport as IDisposable)?.Dispose(); } catch { }
            _transport = null; _mesh = null;
            _driver = null;

            foreach (var link in peers)
            {
                var reader = link.Reader;
                if (reader != null && reader.IsAlive && reader != Thread.CurrentThread)
                {
                    try { reader.Join(300); } catch { }
                }
            }

            if (reconnect != null && reconnect.IsAlive && reconnect != Thread.CurrentThread)
            {
                try { reconnect.Join(400); } catch { } // it polls the flags every 100ms, so this returns quickly
            }
        }

        // ------------------------------------------------------------------ probe

        /// <summary>
        /// M0 capability probe, folded in as a diagnostic: times save/load/frame-advance on the
        /// loaded core and reports whether it qualifies for rollback (§5). Saves and restores the
        /// current position so it doesn't disturb play. Only runs when idle.
        /// </summary>
        private void RunProbe()
        {
            if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
            if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }
            if (_sessionActive) { Log("Can't probe during a session."); return; }

            Log("=== capability probe ===");
            string? restore = null;
            try
            {
                var adapter = new EmuHawkAdapter(APIs, _emulator, _statable);
                restore = APIs.MemorySaveState.SaveCoreStateToMemory();
                double budget = FrameMs();
                var probe = new CapabilityProbe(adapter, new StopwatchClock(), samples: 100);
                var result = probe.Run(budget, budget * 0.25);
                Log(result.ToString());
            }
            catch (Exception ex) { Log("probe failed: " + ex.Message); }
            finally
            {
                if (restore != null)
                {
                    try { APIs.MemorySaveState.LoadCoreStateFromMemory(restore); APIs.MemorySaveState.DeleteState(restore); }
                    catch (Exception ex) { Log("(warning) could not restore pre-probe state: " + ex.Message); }
                }
                Log("=== done ===");
            }
        }

        /// <summary>
        /// Dumps what BizHawk sees for input: the controller/binding keys we resolve against, and
        /// the host inputs pressed right now vs how they map to P1. Hold a button and click.
        /// </summary>
        private void RunInputTest()
        {
            if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
            if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }
            try
            {
                var adapter = _adapter ?? new EmuHawkAdapter(APIs, _emulator, _statable);
                Log("=== input test (hold a button while clicking) ===");
                Log(adapter.DescribeInputState());
            }
            catch (Exception ex) { Log("input test failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ helpers

        private PeerIdentity BuildIdentity(EmuHawkAdapter a, bool wantRollback)
        {
            var layouts = new string[a.PortCount];
            for (int p = 0; p < layouts.Length; p++)
                layouts[p] = a.GetControllerLayout(p).Digest;
            // Advertise the core's real rollback depth only when this peer wants rollback; otherwise 0
            // so the negotiator (which needs both peers to opt in) settles on lockstep for free.
            int depth = wantRollback ? MeasureRollbackDepth(a) : 0;
            return new PeerIdentity(Protocol, a.RomHash, a.CoreName, a.CoreVersion,
                a.SyncSettingsDigest, layouts, a.VerifyDeterministicMode(), maxRollbackDepth: depth);
        }

        /// <summary>
        /// Run the capability probe once (cached) to learn how deep a rollback this core can repair
        /// inside one frame budget. Saves and restores the core state so the pre-session position is
        /// untouched. Requires the emulator to already be paused (we advance frames invisibly here).
        /// </summary>
        private int MeasureRollbackDepth(EmuHawkAdapter a)
        {
            if (_probeDepth >= 0) return _probeDepth;
            string? restore = null;
            try
            {
                restore = APIs.MemorySaveState.SaveCoreStateToMemory();
                double budget = FrameMs();
                var result = new CapabilityProbe(a, new StopwatchClock(), samples: 60).Run(budget, budget * 0.25);
                _probeDepth = result.MaxRollbackDepth;
                Log($"rollback probe — {result}");
            }
            catch (Exception ex) { _probeDepth = 0; Log("rollback probe failed, will use lockstep: " + ex.Message); }
            finally
            {
                if (restore != null)
                {
                    try { APIs.MemorySaveState.LoadCoreStateFromMemory(restore); APIs.MemorySaveState.DeleteState(restore); }
                    catch (Exception ex) { Log("(warning) could not restore pre-probe state: " + ex.Message); }
                }
            }
            return _probeDepth;
        }

        /// <summary>
        /// While a session is live, keep EmuHawk running and accepting input even when its window
        /// isn't focused — otherwise two instances on one screen pause each other (only one can be
        /// focused). Restores the user's original settings when the session ends.
        /// </summary>
        private void ApplyBackgroundConfig(bool enable)
        {
            try
            {
                if (enable)
                {
                    _config = (APIs.Emulation as EmulationApi)?.ForbiddenConfigReference;
                    if (_config == null) { Log("(note) couldn't reach config to disable pause-on-unfocus"); return; }
                    _prevRunInBackground = _config.RunInBackground;
                    _prevAcceptBackgroundInput = _config.AcceptBackgroundInput;
                    _prevAcceptBackgroundInputControllerOnly = _config.AcceptBackgroundInputControllerOnly;
                    _config.RunInBackground = true;
                    _config.AcceptBackgroundInput = true;
                    // Controller-only: the unfocused window still reads its gamepad, but background
                    // KEYBOARD is ignored — so typing in another window can't fire an EmuHawk hotkey
                    // (rewind/load-state) that would desync the session.
                    _config.AcceptBackgroundInputControllerOnly = true;
                    _configApplied = true;
                    Log("run-in-background enabled (controller-only) for this session");
                }
                else if (_configApplied && _config != null)
                {
                    _config.RunInBackground = _prevRunInBackground;
                    _config.AcceptBackgroundInput = _prevAcceptBackgroundInput;
                    _config.AcceptBackgroundInputControllerOnly = _prevAcceptBackgroundInputControllerOnly;
                    _configApplied = false;
                }
            }
            catch (Exception ex) { Log("(note) background-config adjust failed: " + ex.Message); }
        }

        private double FrameMs()
        {
            var vp = _emulator!.ServiceProvider.GetService<IVideoProvider>();
            if (vp != null && vp.VsyncNumerator > 0 && vp.VsyncDenominator > 0)
                return 1000.0 * vp.VsyncDenominator / vp.VsyncNumerator;
            return 1000.0 / 60.0;
        }

        private static byte[] EncodeChecksum(int frame, uint hash)
        {
            var b = new byte[8];
            b[0] = (byte)(frame >> 24); b[1] = (byte)(frame >> 16); b[2] = (byte)(frame >> 8); b[3] = (byte)frame;
            b[4] = (byte)(hash >> 24); b[5] = (byte)(hash >> 16); b[6] = (byte)(hash >> 8); b[7] = (byte)hash;
            return b;
        }

        private static void DecodeChecksum(byte[] b, out int frame, out uint hash)
        {
            frame = (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
            hash = ((uint)b[4] << 24) | ((uint)b[5] << 16) | ((uint)b[6] << 8) | b[7];
        }

        private void StartThread(Action body) =>
            new Thread(() => body()) { IsBackground = true, Name = "BizHawkNetplay-connect" }.Start();

        private void UpdateEnabled()
        {
            bool host = _hostRadio.Checked;
            _ipBox.Enabled = !host;
            _goButton.Text = host ? "Start Hosting" : "Join";
        }

        private void SetBusy(bool busy)
        {
            _goButton.Enabled = !busy;
            _hostRadio.Enabled = _joinRadio.Enabled = !busy;
            _ipBox.Enabled = !busy && _joinRadio.Checked;
            _portBox.Enabled = _delayBox.Enabled = !busy;
            _probeButton.Enabled = !busy;
            _disconnectButton.Enabled = busy;
            // _testInputButton stays enabled (useful to check bindings before and during a session)
        }

        private void Status(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }

        private void Log(string message)
        {
            if (_log.IsDisposed) return;
            _log.AppendText(message + Environment.NewLine);
        }

        private void UiLog(string message) => BeginInvokeUi(() => Log(message));

        private void BeginInvokeUi(Action action)
        {
            if (IsDisposed) return;
            try { BeginInvoke(action); } catch { /* form closing */ }
        }
    }
}
