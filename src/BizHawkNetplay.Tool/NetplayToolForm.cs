using System;
using System.Collections.Concurrent;
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
        private const int Protocol = 2; // v2 adds READY/GO and asynchronous resync framing
        private const int DefaultPort = 47800;
        private const int ChecksumInterval = 300; // full-memory hashes are intentionally infrequent (~5s at 60fps)

        public ApiContainer? _apiContainer { get; set; }
        private ApiContainer APIs => _apiContainer!;

        [RequiredService] public IEmulator? _emulator { get; set; }
        [OptionalService] public IStatable? _statable { get; set; }

        // --- UI (assigned once, from the per-tab build methods the constructor calls) ---
        private RadioButton _hostRadio = null!;
        private RadioButton _joinRadio = null!;
        private ComboBox _ipBox = null!;
        private NumericUpDown _portBox = null!;
        private NumericUpDown _playersBox = null!;
        private NumericUpDown _delayBox = null!;
        private Button _goButton = null!;
        private Button _disconnectButton = null!;
        private Button _probeButton = null!;
        private Button _testInputButton = null!;
        private Button _pubAddrButton = null!;
        private CheckBox _verboseCheck = null!;
        private CheckBox _freezeInputCheck = null!;
        private CheckBox _forceDesyncCheck = null!;
        private ComboBox _netcodeCombo = null!;
        private ComboBox _inputSourceCombo = null!;
        private CheckBox _allowNonDetCheck = null!;
        private Label _netcodeLabel = null!;
        private CheckBox _simUnresponsiveCheck = null!;
        private CheckBox _upnpCheck = null!;
        private TextBox _passwordBox = null!;
        private NumericUpDown _simLatencyBox = null!;
        private ListView _playersList = null!;
        private Label _status = null!;
        private Button _punchButton = null!;
        private GroupBox _punchGroup = null!;
        private TextBox _myCodeBox = null!;
        private Button _copyCodeButton = null!;
        private TextBox _peerCodeBox = null!;
        private Button _connectButton = null!;
        private Label _punchStatus = null!;

        private NetplaySettings _settings = null!;     // persisted UI prefs (UPnP, port, delay, netcode, recent IPs)
        private bool _loadingSettings;                  // suppress change-handler saves while applying loaded prefs
        private string? _pendingJoinIp;                 // regular-join IP awaiting a successful connect, then recorded

        private int _simLatencyMs; // diagnostic: artificial one-way UDP delay for this session (0 = off)
        private bool _upnpEnabled;  // host: whether to attempt the UPnP auto-forward (captured from the checkbox)
        private UpnpMapping? _upnpMapping; // host: the router forward we added, removed on session end

        private bool Verbose => _verboseCheck.Checked;

        private int _startEmuFrame; // emulator FrameCount at session start, for drift detection
        private TextBox _log = null!;

        /// <summary>One control link to a peer. Host: one per joiner. Joiner: one (the host).</summary>
        private sealed class PeerLink
        {
            public TcpClient Tcp = null!;
            public ControlChannel Control = null!;
            public int RemotePort;            // the controller port this peer owns (host peer = 0)
            public IPEndPoint UdpEndpoint = null!;      // LAN/observed endpoint (from TCP source + reported port)
            public IPEndPoint? ReflexiveEndpoint;       // public (STUN) endpoint, for NAT traversal; null until reported
            public Thread? Reader;
            public Thread? Writer;
            public readonly ConcurrentQueue<OutboundMessage> Outbound = new ConcurrentQueue<OutboundMessage>();
            public readonly AutoResetEvent OutboundSignal = new AutoResetEvent(false);
            public volatile bool WriterRunning;
            public long QueuedBytes;
            public double PingMs = -1;        // guarded by _pingLock
            public int PingCount;             // guarded by _pingLock
            public long LastRecvTicks;        // UtcNow.Ticks of the last message from this peer (Interlocked)
            public volatile bool ResyncReceiving; // large inbound state frame is allowed to exceed ping timeout
            public bool DirectLogged;         // one-time flag: logged that this peer's direct UDP path opened
            public string Label = "";
        }

        private sealed class OutboundMessage
        {
            public OutboundMessage(ControlMessageType type, byte[] body, Action<bool>? completed)
            {
                Type = type; Body = body; Completed = completed;
            }
            public ControlMessageType Type { get; }
            public byte[] Body { get; }
            public Action<bool>? Completed { get; }
        }

        // --- Session state (all touched on the UI thread except where noted) ---
        private EmuHawkAdapter? _adapter;
        private ITransport? _transport;        // the FrameDriver's input channel (see below)
        private MeshUdpTransport? _mesh;       // direct peer-to-peer UDP: host and joiners both send to all peers
        private List<IPEndPoint> _meshOthers = new List<IPEndPoint>(); // joiner: the non-host peers in our mesh

        // UDP-punch path (2-player, no port-forwarding): one socket does STUN + hole-punch, then carries
        // both the reliable control channel and the input hot path. Set up in two steps (generate our
        // connect code, then punch to the pasted peer code) before the normal session bring-up runs.
        private PunchedPeerLink? _punchLink;
        private volatile bool _punchMode;      // this session connected via UDP punch (no TCP listener / mesh)
        private PeerIdentity? _punchId;         // prepared handshake identity, captured when punch setup began
        private SessionPreferences? _punchPrefs;
        private byte[]? _punchState;            // host only: the initial state to transfer once punched
        private TcpListener? _listener;
        private volatile TcpClient? _joiningTcp; // a join connect still in progress, so Disconnect can close it
        private volatile TcpClient? _greetingTcp; // a joiner we've accepted but are still greeting, so teardown can abort it
        private const int HandshakeReceiveTimeoutMs = 15000; // a joiner that connects but never HELLOs can't wedge the host
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
        private const double ResyncRecoverySeconds = 8.0; // joiner clears its resync counter after this long without another
        private bool _audioStatsLogged; // one-shot audio pipeline diagnostic per session
        private double _lastStallLogMs = double.NegativeInfinity;
        private bool _resyncInProgress;

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
        private const double UdpRepunchAfterSeconds = 1.5;
        private const double UdpLostAfterSeconds = 8.0;
        private double _lastUdpRepunchMs = double.NegativeInfinity;
        private bool _udpWarningActive;

        // Sync mode + rollback config for the live session.
        private enum NetcodeChoice { Automatic = 0, Rollback = 1, Lockstep = 2 }
        private NetcodeChoice _netcodeChoice; // captured from the dropdown at start (host decides the mode)
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
        private const int MaxFramesPerTick = 2;  // WinForms callbacks can arrive ~25ms apart; one frame caps near 40fps
        private const double FrameTickWorkBudgetMs = 8.0;
        private double _nextFrameDueMs;
        private bool _frameTickRunning;
        private double _lastUiRefreshMs = double.NegativeInfinity;
        private double _lastSlowTickLogMs = double.NegativeInfinity;
        private int _lastVerboseAudioFrame = -1;
        private int _pacingRebases;
        private double _lastHashMs;

        // Actual sustained emulation speed, sampled ~2x/sec, so the status bar can flag a CPU-bound
        // instance (the real cause of "lag" on a heavy core) rather than it looking like a netcode fault.
        private readonly System.Diagnostics.Stopwatch _fpsClock = new System.Diagnostics.Stopwatch();
        private int _fpsCount;
        private double _actualFps = -1;

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
            ClientSize = new Size(580, 540);
            MinimumSize = new Size(520, 480);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildConnectionTab());
            tabs.TabPages.Add(BuildPlayersTab());
            tabs.TabPages.Add(BuildDiagnosticsTab());
            tabs.TabPages.Add(BuildLogTab());

            // Status line stays visible under every tab.
            _status = new Label
            {
                Text = "Idle.", Dock = DockStyle.Bottom, Height = 22, ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0),
                BorderStyle = BorderStyle.Fixed3D,
            };

            Controls.Add(tabs);   // fills the area left above the status bar
            Controls.Add(_status);
            ResumeLayout(false);

            _frameTimer = new System.Windows.Forms.Timer();
            _frameTimer.Tick += (_, __) => FrameTick();

            LoadAndApplySettings();
            UpdateEnabled();
        }

        /// <summary>
        /// After the host restores our last window position, make sure we actually landed on a visible
        /// monitor. BizHawk persists tool positions, so a spot saved on a monitor that's since been
        /// disconnected or rearranged leaves the window stranded offscreen. If little of it is on any
        /// screen, re-center on the primary display; an already-visible position is left untouched.
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // The host restores our saved position AFTER OnShown returns (right after Show()), so a fix
            // applied here is immediately overwritten. Defer to the end of the message queue via
            // BeginInvoke — by then the final position is in place and we can pull it back if stranded.
            try { BeginInvoke((Action)MoveOnScreenIfStranded); } catch { MoveOnScreenIfStranded(); }
        }

        /// <summary>If little of the window ended up on any connected monitor — a position saved on a
        /// display that's since been removed or rearranged — re-center it on EmuHawk's screen so it's
        /// visible. A window that's already substantially on-screen is left where the user put it.</summary>
        private void MoveOnScreenIfStranded()
        {
            try
            {
                foreach (var s in Screen.AllScreens)
                {
                    var vis = Rectangle.Intersect(s.WorkingArea, Bounds);
                    if (vis.Width >= 120 && vis.Height >= 60) return; // enough of us is on a real screen
                }
                var host = Owner ?? (MainForm as Control);
                var wa = (host != null ? Screen.FromControl(host) : Screen.PrimaryScreen).WorkingArea;
                int w = Math.Min(Width, wa.Width), h = Math.Min(Height, wa.Height);
                StartPosition = FormStartPosition.Manual;
                Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
            }
            catch { /* positioning is best-effort — never let it break opening the tool */ }
        }

        // ------------------------------------------------------------------ persisted settings

        /// <summary>Load remembered prefs and apply them to the controls, then hook change-to-save.</summary>
        private void LoadAndApplySettings()
        {
            _settings = NetplaySettings.Load();
            _loadingSettings = true;
            try
            {
                _upnpCheck.Checked = _settings.Upnp;
                _portBox.Value = Clamp(_settings.Port, (int)_portBox.Minimum, (int)_portBox.Maximum);
                _playersBox.Value = Clamp(_settings.Players, (int)_playersBox.Minimum, (int)_playersBox.Maximum);
                _delayBox.Value = Clamp(_settings.Delay, (int)_delayBox.Minimum, (int)_delayBox.Maximum);
                if (_settings.Netcode >= 0 && _settings.Netcode < _netcodeCombo.Items.Count)
                    _netcodeCombo.SelectedIndex = _settings.Netcode;
                if (_settings.InputSource >= 0 && _settings.InputSource < _inputSourceCombo.Items.Count)
                    _inputSourceCombo.SelectedIndex = _settings.InputSource;
                RefreshIpDropdown();
                if (_settings.RecentIps.Count > 0) _ipBox.Text = _settings.RecentIps[0]; // last host, ready to re-join
            }
            finally { _loadingSettings = false; }

            // Persist whenever a remembered control changes, so state survives even without starting a session.
            _upnpCheck.CheckedChanged += (_, __) => SaveSettingsFromUi();
            _portBox.ValueChanged += (_, __) => SaveSettingsFromUi();
            _playersBox.ValueChanged += (_, __) => SaveSettingsFromUi();
            _delayBox.ValueChanged += (_, __) => SaveSettingsFromUi();
            _netcodeCombo.SelectedIndexChanged += (_, __) => SaveSettingsFromUi();
            _inputSourceCombo.SelectedIndexChanged += (_, __) => SaveSettingsFromUi();
        }

        private void SaveSettingsFromUi()
        {
            if (_loadingSettings || _settings == null) return;
            _settings.Upnp = _upnpCheck.Checked;
            _settings.Port = (int)_portBox.Value;
            _settings.Players = (int)_playersBox.Value;
            _settings.Delay = (int)_delayBox.Value;
            _settings.Netcode = _netcodeCombo.SelectedIndex;
            _settings.InputSource = _inputSourceCombo.SelectedIndex;
            _settings.Save();
        }

        /// <summary>Record a successfully-joined host IP into the recent list and refresh the dropdown.</summary>
        private void RecordJoinIp(string ip)
        {
            if (_settings == null) return;
            _settings.RecordIp(ip);
            SaveSettingsFromUi(); // also persists the current control values alongside the new IP
            RefreshIpDropdown();
        }

        /// <summary>Repopulate the IP dropdown from the recent list, preserving the typed text.</summary>
        private void RefreshIpDropdown()
        {
            string current = _ipBox.Text;
            _ipBox.BeginUpdate();
            _ipBox.Items.Clear();
            foreach (var ip in _settings.RecentIps) _ipBox.Items.Add(ip);
            _ipBox.EndUpdate();
            _ipBox.Text = current;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>The Connection tab: role, host address/port, delay, rollback, and the start/stop buttons.</summary>
        private TabPage BuildConnectionTab()
        {
            var page = new TabPage("Connection") { Padding = new Padding(8) };

            _hostRadio = new RadioButton { Text = "Host", Checked = true, AutoSize = true, Location = new Point(12, 12) };
            _joinRadio = new RadioButton { Text = "Join", AutoSize = true, Location = new Point(80, 12) };
            _hostRadio.CheckedChanged += (_, __) => UpdateEnabled();

            var ipLabel = new Label { Text = "Host IP:", AutoSize = true, Location = new Point(12, 46) };
            _ipBox = new ComboBox
            {
                Text = "127.0.0.1", Location = new Point(80, 43), Width = 160,
                DropDownStyle = ComboBoxStyle.DropDown, // editable, with a dropdown of recently-used IPs
            };
            var portLabel = new Label { Text = "Port:", AutoSize = true, Location = new Point(260, 46) };
            _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = DefaultPort, Location = new Point(300, 43), Width = 70 };
            var playersLabel = new Label { Text = "Players:", AutoSize = true, Location = new Point(388, 46) };
            _playersBox = new NumericUpDown { Minimum = 2, Maximum = 8, Value = 2, Location = new Point(444, 43), Width = 46 };

            var passwordLabel = new Label { Text = "Password:", AutoSize = true, Location = new Point(12, 78) };
            _passwordBox = new TextBox { Location = new Point(80, 75), Width = 160, UseSystemPasswordChar = true };
            var passwordHint = new Label { Text = "(optional; must match on both ends)", AutoSize = true, Location = new Point(248, 78), ForeColor = Color.DimGray };

            var delayLabel = new Label { Text = "Input delay:", AutoSize = true, Location = new Point(12, 110) };
            _delayBox = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 2, Location = new Point(90, 107), Width = 50 };

            var netcodeSelLabel = new Label { Text = "Netcode:", AutoSize = true, Location = new Point(155, 110) };
            _netcodeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(215, 107), Width = 110 };
            _netcodeCombo.Items.AddRange(new object[] { "Automatic", "Rollback", "Lockstep" });
            _netcodeCombo.SelectedIndex = 0; // Automatic: rollback if the core qualifies, else lockstep

            var inputSrcLabel = new Label { Text = "My controls:", AutoSize = true, Location = new Point(12, 142) };
            _inputSourceCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(90, 139), Width = 130 };
            _inputSourceCombo.Items.AddRange(new object[] { "Use P1 pad", "Use P2 pad", "Use P3 pad", "Use P4 pad", "Assigned port" });
            _inputSourceCombo.SelectedIndex = 0; // default: read your normal P1 controls, whatever port you're assigned

            _upnpCheck = new CheckBox { Text = "Auto-forward host port (UPnP)", AutoSize = true, Checked = true, Location = new Point(240, 141) };

            _goButton = new Button { Text = "Start Hosting", Location = new Point(12, 172), Width = 150 };
            _goButton.Click += (_, __) => OnGo();
            _disconnectButton = new Button { Text = "Disconnect", Location = new Point(172, 172), Width = 110, Enabled = false };
            _disconnectButton.Click += (_, __) => EndSession("disconnected by user");
            _pubAddrButton = new Button { Text = "My public address", Location = new Point(292, 172), Width = 150 };
            _pubAddrButton.Click += (_, __) => ShowPublicAddress();

            _netcodeLabel = new Label
            {
                Text = "Netcode in use: —", Location = new Point(12, 208), Width = 300, Height = 24,
                BorderStyle = BorderStyle.FixedSingle, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0), ForeColor = Color.DimGray,
            };

            page.Controls.AddRange(new Control[]
            {
                _hostRadio, _joinRadio, ipLabel, _ipBox, portLabel, _portBox, playersLabel, _playersBox,
                passwordLabel, _passwordBox, passwordHint, delayLabel, _delayBox,
                netcodeSelLabel, _netcodeCombo, inputSrcLabel, _inputSourceCombo, _upnpCheck,
                _goButton, _disconnectButton, _pubAddrButton, _netcodeLabel, BuildPunchGroup(),
            });
            return page;
        }

        /// <summary>
        /// The "UDP Punch" group: the no-port-forwarding path (2-player). Distinct buttons for a regular
        /// connection (above) versus punch, exactly as requested — punch isn't a silent fallback, it's an
        /// explicit choice that surfaces a connect code you swap with your friend out of band.
        /// </summary>
        private GroupBox BuildPunchGroup()
        {
            _punchGroup = new GroupBox
            {
                Text = "UDP Punch — connect with no port-forwarding (2 players)",
                Location = new Point(12, 244), Size = new Size(544, 176),
            };

            var step1 = new Label
            {
                Text = "1. Click UDP Punch. Your code appears below — send it to your friend:",
                AutoSize = true, Location = new Point(12, 24),
            };
            _myCodeBox = new TextBox
            {
                ReadOnly = true, Location = new Point(12, 46), Width = 240,
                Font = new Font(FontFamily.GenericMonospace, 11f), Text = "",
            };
            _copyCodeButton = new Button { Text = "Copy", Location = new Point(260, 45), Width = 60, Enabled = false };
            _copyCodeButton.Click += (_, __) => CopyMyCode();
            _punchButton = new Button { Text = "UDP Punch", Location = new Point(340, 44), Width = 120 };

            var step2 = new Label
            {
                Text = "2. Paste your friend's code and Connect:",
                AutoSize = true, Location = new Point(12, 84),
            };
            _peerCodeBox = new TextBox { Location = new Point(12, 106), Width = 240, Enabled = false };
            _connectButton = new Button { Text = "Connect", Location = new Point(260, 105), Width = 80, Enabled = false };
            _connectButton.Click += (_, __) => OnPunchConnect();

            _punchStatus = new Label
            {
                Text = "", AutoSize = true, Location = new Point(12, 142), ForeColor = Color.DimGray,
            };

            _punchButton.Click += (_, __) => OnPunchStart();

            _punchGroup.Controls.AddRange(new Control[]
            {
                step1, _myCodeBox, _copyCodeButton, _punchButton,
                step2, _peerCodeBox, _connectButton, _punchStatus,
            });
            return _punchGroup;
        }

        /// <summary>The Diagnostics tab: the capability probe, input test, and the fault-injection toggles.</summary>
        private TabPage BuildDiagnosticsTab()
        {
            var page = new TabPage("Diagnostics") { Padding = new Padding(8) };

            _probeButton = new Button { Text = "Capability Probe", Location = new Point(12, 12), Width = 130 };
            _probeButton.Click += (_, __) => RunProbe();
            _testInputButton = new Button { Text = "Test Input", Location = new Point(152, 12), Width = 130 };
            _testInputButton.Click += (_, __) => RunInputTest();

            _verboseCheck = new CheckBox { Text = "Verbose log", AutoSize = true, Location = new Point(12, 54) };
            _freezeInputCheck = new CheckBox { Text = "Freeze input (diag)", AutoSize = true, Location = new Point(12, 78) };
            _freezeInputCheck.CheckedChanged += (_, __) =>
                EmuHawkAdapter.ForceNeutralInput = _freezeInputCheck.Checked;
            _forceDesyncCheck = new CheckBox { Text = "Force desync (diag)", AutoSize = true, Location = new Point(12, 102) };
            _forceDesyncCheck.CheckedChanged += (_, __) =>
            {
                if (!_forceDesyncCheck.Checked) return;
                _forceDesyncOnce = true;
                _forceDesyncCheck.Checked = false;
                Log(_sessionActive ? "will inject a fake desync at the next checksum (tests resync)"
                                   : "arm this during a session to test resync");
            };

            var simLatencyLabel = new Label { Text = "Sim latency ms:", AutoSize = true, Location = new Point(12, 132) };
            _simLatencyBox = new NumericUpDown { Minimum = 0, Maximum = 500, Increment = 10, Value = 0, Location = new Point(110, 130), Width = 60 };
            _simUnresponsiveCheck = new CheckBox { Text = "Simulate unresponsive (diag)", AutoSize = true, Location = new Point(12, 160) };
            _simUnresponsiveCheck.CheckedChanged += (_, __) =>
            {
                _simUnresponsive = _simUnresponsiveCheck.Checked;
                if (_sessionActive)
                    Log(_simUnresponsive
                        ? "simulating an unresponsive peer — we've stopped answering pings; the other side should drop us in ~3s"
                        : "resumed responding to pings");
            };

            _allowNonDetCheck = new CheckBox
            {
                Text = "Allow non-deterministic core (experimental — may desync)",
                AutoSize = true, Location = new Point(12, 192), Checked = true,
            };

            var nonDetHint = new Label
            {
                Text = "For cores that report non-deterministic but often sync anyway (e.g. N64 with no movie).\nBoth players must enable it. Desync detection still guards you.",
                AutoSize = true, Location = new Point(30, 214), ForeColor = Color.DimGray,
            };

            page.Controls.AddRange(new Control[]
            {
                _probeButton, _testInputButton, _verboseCheck, _freezeInputCheck, _forceDesyncCheck,
                simLatencyLabel, _simLatencyBox, _simUnresponsiveCheck, _allowNonDetCheck, nonDetHint,
            });
            return page;
        }

        /// <summary>The Players tab: a live list of everyone in the session with their address and ping.</summary>
        private TabPage BuildPlayersTab()
        {
            var page = new TabPage("Players");
            _playersList = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            _playersList.Columns.Add("Player", 80);
            _playersList.Columns.Add("Address", 220);
            _playersList.Columns.Add("Ping", 70);
            _playersList.Columns.Add("Link", 120);
            page.Controls.Add(_playersList);
            return page;
        }

        /// <summary>Whether a direct UDP path to this peer is currently confirmed open (mesh punch/keepalive).</summary>
        private bool MeshLinkAlive(PeerLink link)
        {
            if (_punchMode) return true; // the punched link is confirmed before the session starts
            var mesh = _mesh;
            if (mesh == null) return false;
            return (link.UdpEndpoint != null && mesh.IsEndpointAlive(link.UdpEndpoint))
                || (link.ReflexiveEndpoint != null && mesh.IsEndpointAlive(link.ReflexiveEndpoint));
        }

        /// <summary>Human-readable direct-link state for the Players list.</summary>
        private string MeshLinkStatus(PeerLink link)
        {
            if (_punchMode) return "direct (punched)";
            var mesh = _mesh;
            if (mesh == null) return "—";
            if (link.UdpEndpoint != null && mesh.IsEndpointAlive(link.UdpEndpoint)) return "direct";
            if (link.ReflexiveEndpoint != null && mesh.IsEndpointAlive(link.ReflexiveEndpoint)) return "direct (punched)";
            return "connecting…";
        }

        /// <summary>Rebuild the players list from the current peers (self first). Cheap for 2–4 players.</summary>
        private void RefreshPlayersList()
        {
            if (_playersList.IsDisposed) return;
            _playersList.BeginUpdate();
            _playersList.Items.Clear();
            if (_sessionActive)
            {
                var me = new ListViewItem($"P{_localPort + 1} (you)");
                me.SubItems.Add(_isHost ? "this machine (host)" : "this machine");
                me.SubItems.Add("—");
                me.SubItems.Add("—");
                _playersList.Items.Add(me);

                lock (_pingLock)
                {
                    foreach (var link in _peers)
                    {
                        var item = new ListViewItem($"P{link.RemotePort + 1}");
                        item.SubItems.Add(link.UdpEndpoint?.ToString() ?? link.Label);
                        item.SubItems.Add(link.PingMs < 0 ? "…" : $"{link.PingMs + 2 * _simLatencyMs:F0} ms");
                        item.SubItems.Add(MeshLinkStatus(link));
                        _playersList.Items.Add(item);

                        // One-time log when a peer's direct UDP path first confirms (host-as-rendezvous punch).
                        if (MeshLinkAlive(link) && !link.DirectLogged)
                        {
                            link.DirectLogged = true;
                            Log($"{link.Label}: direct UDP path open");
                        }
                    }
                }
            }
            _playersList.EndUpdate();
        }

        /// <summary>The Log tab: the scrolling monospace session log, filling the page.</summary>
        private TabPage BuildLogTab()
        {
            var page = new TabPage("Log");
            _log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f),
            };
            page.Controls.Add(_log);
            return page;
        }

        public override void Restart()
        {
            // ROM load / tool re-init: tear down any live session.
            if (_sessionActive) EndSession("emulator restarted");
            // Invalidate the cached probe depth — the core/ROM may have changed, and a stale (deeper)
            // measurement from a lighter core could wrongly grant rollback to a heavier one.
            _probeDepth = -1;
            UpdateEnabled();
        }

        // ------------------------------------------------------------------ start

        private void OnGo()
        {
            if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
            if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }

            WarnSessionHazards(); // non-blocking heads-up about movies/TAStudio/Lua

            try
            {
                _adapter = new EmuHawkAdapter(APIs, _emulator, _statable);
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
                    Log($"this core has only {portCount} controller ports — hosting {players} players.");

                // Validate the join address BEFORE pausing — otherwise a typo'd IP leaves the emulator
                // frozen on the early return with no session to un-freeze it.
                if (!_hostRadio.Checked && !IPAddress.TryParse(_ipBox.Text.Trim(), out _))
                {
                    Log("Enter a valid host IP."); SetBusy(false); return;
                }

                // Freeze the emulator NOW. Otherwise it keeps free-running between probing/exporting
                // its state and the peers arriving, so the sims start on different frames and desync
                // immediately. Paused here == the frame all peers resume from. (Probing below advances
                // frames invisibly and restores, so it must be paused first.)
                APIs.EmuClient.Pause();

                // Netcode: Automatic prefers rollback but drops to lockstep if the probe fails; Rollback
                // forces it; Lockstep forces lockstep. We "want" rollback unless Lockstep is chosen, and
                // probe accordingly. The host's choice is authoritative for the session's mode.
                _netcodeChoice = (NetcodeChoice)_netcodeCombo.SelectedIndex;
                ApplyHeavyCoreNetcodeDefault();
                bool wantRollback = _netcodeChoice != NetcodeChoice.Lockstep;
                var prefs = new SessionPreferences((int)_delayBox.Value, wantRollback,
                    SessionPreferences.HashPassword(_passwordBox.Text));
                var id = BuildIdentity(_adapter, wantRollback);
                int port = (int)_portBox.Value;
                _simLatencyMs = (int)_simLatencyBox.Value; // diagnostic artificial UDP delay for this session
                _upnpEnabled = _upnpCheck.Checked;         // capture on the UI thread for the host accept thread
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
                    _mesh = MeshUdpTransport.Bind(0); _transport = WrapSimLatency(_mesh);
                    string ip = _ipBox.Text.Trim(); // already validated above, before the pause
                    _pendingJoinIp = ip; // recorded into the recent-IPs list once the connect succeeds
                    StartThread(() => JoinThread(ip, port, id, prefs, _mesh.LocalPort));
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
            if (_punchMode || _sessionActive) { Log("Already connecting — Disconnect first."); return; }

            WarnSessionHazards(); // non-blocking heads-up about movies/TAStudio/Lua

            try
            {
                _adapter = new EmuHawkAdapter(APIs, _emulator, _statable);
                _adapter.InputSourcePort = InputSourceFromCombo(); // read your normal pad, whatever port you're assigned
                if (!_adapter.VerifyDeterministicMode())
                    Log("WARNING: core does not report deterministic emulation — desyncs are likely.");
                if (!_adapter.HasBindings)
                    Log($"WARNING: input may not register — {_adapter.BindingDiagnostic}");

                _isHost = _hostRadio.Checked;
                int players = _adapter.PortCount;
                if (players < 2)
                {
                    Log($"this core exposes only {players} controller port — need at least 2 for netplay.");
                    return;
                }
                // Punch is 2-player only: swapping connect codes by hand doesn't scale to a full mesh.

                APIs.EmuClient.Pause(); // freeze now so the resume frame is fixed before the peer arrives

                _netcodeChoice = (NetcodeChoice)_netcodeCombo.SelectedIndex;
                ApplyHeavyCoreNetcodeDefault();
                bool wantRollback = _netcodeChoice != NetcodeChoice.Lockstep;
                _punchPrefs = new SessionPreferences((int)_delayBox.Value, wantRollback,
                    SessionPreferences.HashPassword(_passwordBox.Text));
                _punchId = BuildIdentity(_adapter, wantRollback);
                _simLatencyMs = (int)_simLatencyBox.Value;
                _punchState = _isHost ? _adapter.ExportState() : null;

                _punchMode = true;
                _punchLink = PunchedPeerLink.Bind(0);
                _transport = WrapSimLatency(_punchLink);
                SetBusy(true);
                _punchButton.Enabled = false;
                _punchStatus.Text = "finding your public address…";
                _punchStatus.ForeColor = Color.DimGray;

                var link = _punchLink;
                int localPort = link.LocalPort;
                new Thread(() =>
                {
                    var reflexive = link.DiscoverReflexive(TimeSpan.FromSeconds(3));
                    string lan = UpnpPortMapper.PrimaryLanIp();
                    IPEndPoint? lanEp = null;
                    try { lanEp = new IPEndPoint(IPAddress.Parse(lan), localPort); } catch { }
                    var loopEp = new IPEndPoint(IPAddress.Loopback, localPort);
                    BeginInvokeUi(() =>
                    {
                        if (!_punchMode) return; // cancelled while discovering
                        string primary = reflexive != null ? ConnectCode.Encode(reflexive)
                                       : lanEp != null ? ConnectCode.Encode(lanEp)
                                       : ConnectCode.Encode(loopEp);
                        _myCodeBox.Text = primary;
                        _copyCodeButton.Enabled = true;
                        _peerCodeBox.Enabled = true;
                        _connectButton.Enabled = true;
                        _punchStatus.Text = reflexive != null
                            ? "share your code, paste your friend's, then Connect."
                            : "STUN unavailable — using a local code (internet peers may be unreachable).";
                        Log(reflexive != null
                            ? $"UDP punch — your public code: {primary}   (endpoint {reflexive})"
                            : "UDP punch — couldn't reach a STUN server; internet peers may be unreachable");
                        if (lanEp != null) Log($"UDP punch — same-LAN code: {ConnectCode.Encode(lanEp)}   ({lanEp})");
                        Log($"UDP punch — same-machine test code: {ConnectCode.Encode(loopEp)}   ({loopEp})");
                        Log("(both ends must use matching code types: public over the internet, LAN on one router, same-machine for local testing)");
                    });
                })
                { IsBackground = true, Name = "BizHawkNetplay-punch-stun" }.Start();
            }
            catch (Exception ex)
            {
                Log("punch setup failed: " + ex.Message);
                EndSession("punch setup failed");
            }
        }

        private void CopyMyCode()
        {
            try { if (!string.IsNullOrEmpty(_myCodeBox.Text)) Clipboard.SetText(_myCodeBox.Text); }
            catch { /* clipboard busy */ }
        }

        /// <summary>
        /// Step 2 of the punch path: punch toward the pasted peer code, then run the normal handshake over
        /// the reliable control stream and hand off to the shared session bring-up. All off the UI thread.
        /// </summary>
        private void OnPunchConnect()
        {
            var link = _punchLink;
            if (!_punchMode || link == null) { Log("Click UDP Punch first."); return; }
            var peer = ConnectCode.TryDecode(_peerCodeBox.Text);
            if (peer == null)
            {
                _punchStatus.Text = "that code doesn't look right — check it and retry.";
                _punchStatus.ForeColor = Color.Firebrick;
                return;
            }

            _connectButton.Enabled = false;
            _peerCodeBox.Enabled = false;
            _punchStatus.Text = $"punching toward {peer}…";
            _punchStatus.ForeColor = Color.DimGray;

            bool isHost = _isHost;
            var id = _punchId!; var prefs = _punchPrefs!; var state = _punchState;
            new Thread(() =>
            {
                bool ok = link.Punch(peer, TimeSpan.FromSeconds(15));
                if (!ok)
                {
                    BeginInvokeUi(() =>
                    {
                        if (!_punchMode) return;
                        _punchStatus.Text = "punch failed — no path opened (the other side may be on symmetric NAT).";
                        _punchStatus.ForeColor = Color.Firebrick;
                        _connectButton.Enabled = true; _peerCodeBox.Enabled = true;
                    });
                    return;
                }
                UiLog($"UDP punch opened a path to {link.PeerEndpoint}");
                try
                {
                    // Punch is symmetric at the transport level — unlike the TCP path (listener vs dialer)
                    // both peers pick their role from a local radio button, so two Hosts (both P1, both own
                    // the state) or two Joins (both wait forever for a state neither sends) are possible.
                    // Trade one reliable byte each way up front, before any ControlChannel framing, and
                    // refuse a same-role pair with a message that says exactly what to change.
                    var reliable = link.Control;
                    byte myRole = (byte)(isHost ? 1 : 0);
                    reliable.WriteByte(myRole);
                    reliable.Flush();
                    int peerRole = reliable.ReadByte();
                    if (peerRole < 0) throw new System.IO.IOException("peer closed during role exchange");
                    if (peerRole == myRole)
                        throw new HandshakeException(isHost
                            ? "both players clicked Host — one of you must Join instead."
                            : "both players clicked Join — one of you must Host instead.");

                    var ch = new ControlChannel(link.Control);
                    var sp = isHost
                        ? Handshake.RunHost(ch, id, prefs, state ?? Array.Empty<byte>(), link.LocalPort)
                        : Handshake.RunClient(ch, id, prefs, link.LocalPort);
                    BeginInvokeUi(() => BeginPunchSession(sp, ch, link.PeerEndpoint!, isHost));
                }
                catch (Exception ex) { BeginInvokeUi(() => FailSession(ex.Message)); }
            })
            { IsBackground = true, Name = "BizHawkNetplay-punch-handshake" }.Start();
        }

        /// <summary>Hand a punched, handshaken link to the shared session bring-up (same as the TCP path).</summary>
        private void BeginPunchSession(SessionParams sp, ControlChannel ch, IPEndPoint peerEp, bool isHost)
        {
            try
            {
                _punchGroup.Enabled = false; // freeze the punch controls for the session's duration
                if (isHost)
                {
                    var link = new PeerLink
                    {
                        Tcp = null!, Control = ch, RemotePort = 1,
                        UdpEndpoint = peerEp, Label = $"P2 ({peerEp.Address})",
                    };
                    BeginSessionHost(new List<PeerLink> { link }, players: 2, delay: sp.InputDelay, mode: sp.Mode);
                }
                else
                {
                    var link = new PeerLink
                    {
                        Tcp = null!, Control = ch, RemotePort = 0,
                        UdpEndpoint = peerEp, Label = $"host ({peerEp.Address})",
                    };
                    BeginSessionJoiner(sp, link); // imports state, sets fields, mesh setup no-ops (punch is 2P)
                }
            }
            catch (Exception ex) { FailSession(ex.Message); }
        }

        private void ResetPunchUi()
        {
            if (_punchGroup.IsDisposed) return;
            _punchGroup.Enabled = true;
            _punchButton.Enabled = true;
            _myCodeBox.Text = "";
            _peerCodeBox.Text = "";
            _copyCodeButton.Enabled = false;
            _peerCodeBox.Enabled = false;
            _connectButton.Enabled = false;
            _punchStatus.Text = "";
            _punchStatus.ForeColor = Color.DimGray;
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

                // Best-effort NAT reachability (UPnP forward + public-address report). Non-fatal.
                TryPublishHostAddress(port);

                var links = new List<PeerLink>();
                var greetings = new List<Handshake.JoinerGreeting>();
                for (int i = 0; i < need; i++)
                {
                    var tcp = _listener.AcceptTcpClient();
                    try { tcp.NoDelay = true; } catch { } // control latency matters for ping + resync
                    try { tcp.ReceiveTimeout = HandshakeReceiveTimeoutMs; } catch { } // bound a silent joiner's HELLO
                    _greetingTcp = tcp; // so Disconnect/teardown can abort a joiner stuck mid-handshake
                    var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                    var channel = new ControlChannel(tcp.GetStream());
                    var greet = Handshake.HostGreet(channel, id, prefs, udpLocalPort);
                    _greetingTcp = null;
                    try { tcp.ReceiveTimeout = 0; } catch { } // handshake done: restore blocking reads for the session
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

                // The host is authoritative on sync mode. Lockstep/Rollback force it; Automatic grants
                // rollback only if every joiner pairwise negotiates to rollback (opted in + cleared the
                // probe depth threshold), else lockstep.
                SyncMode mode;
                if (_netcodeChoice == NetcodeChoice.Lockstep)
                {
                    mode = SyncMode.Lockstep;
                }
                else if (_netcodeChoice == NetcodeChoice.Rollback)
                {
                    mode = SyncMode.Rollback; // forced — bypasses the probe gate
                    UiLog("netcode forced to rollback — bypassing the capability probe (may stutter if a core can't keep up)");
                }
                else // Automatic
                {
                    bool allRollback = greetings.Count >= 1;
                    foreach (var g in greetings)
                        if (SessionNegotiator.Negotiate(id, g.Id, prefs, g.Prefs).Mode != SyncMode.Rollback)
                        { allRollback = false; break; }
                    mode = allRollback ? SyncMode.Rollback : SyncMode.Lockstep;
                }

                // Each joiner gets every OTHER joiner's UDP endpoint so it can build a direct mesh
                // (it reaches the host at the address it connected to, so the host is left off the list).
                foreach (var link in links)
                {
                    try { link.Tcp.ReceiveTimeout = HandshakeReceiveTimeoutMs; } catch { }
                    Handshake.HostSendWelcome(link.Control, link.RemotePort, players, finalDelay, mode, state,
                        CandidatesExcept(links, link), useReadyBarrier: true);
                }

                // Nobody is released while the host is still synchronously shipping a large state to
                // another joiner. Once every control link acknowledges that all start data arrived,
                // send GO to the whole group and bring up the host locally.
                foreach (var link in links) Handshake.HostWaitReady(link.Control);
                foreach (var link in links) Handshake.HostSendGo(link.Control);
                foreach (var link in links) try { link.Tcp.ReceiveTimeout = 0; } catch { }

                // The host sends its own input directly to every joiner.
                _mesh!.SetPeers(CandidatesExcept(links, null));

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
                _joiningTcp = tcp;          // so Disconnect can close a connect that's still blocking
                tcp.Connect(ip, port);
                try { tcp.NoDelay = true; } catch { } // control latency matters for ping + resync
                var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                var channel = new ControlChannel(tcp.GetStream());
                var sp = Handshake.RunClientMulti(channel, id, prefs, udpLocalPort);
                _joiningTcp = null;         // handed off to the session; teardown closes it via the PeerLink now
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
                if (_pendingJoinIp != null) { RecordJoinIp(_pendingJoinIp); _pendingJoinIp = null; } // connect succeeded
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
            _resyncInProgress = false;
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
            _delayHintShown = false;
            lock (_pingLock) { foreach (var link in _peers) { link.PingMs = -1; link.PingCount = 0; } }
            _pingClock.Restart();
            _paceClock.Restart();
            _nextFrameDueMs = 0;
            _lastUiRefreshMs = double.NegativeInfinity;
            _lastSlowTickLogMs = double.NegativeInfinity;
            _lastVerboseAudioFrame = -1;
            _lastStallLogMs = double.NegativeInfinity;
            _lastUdpRepunchMs = double.NegativeInfinity;
            _udpWarningActive = false;
            _pacingRebases = 0;
            _fpsClock.Restart(); _fpsCount = 0; _actualFps = -1;
            try { if (!_timerResRaised) { timeBeginPeriod(1); _timerResRaised = true; } } catch { }
            _frameTimer.Interval = 2;
            _frameTimer.Start();

            Status($"in session — {(mode == SyncMode.Rollback ? "rollback" : "lockstep")}, " +
                   $"you are P{_localPort + 1}/{_playerCount}, delay {_sessionDelay}", Color.Green);
            _netcodeLabel.Text = "Netcode in use: " + (mode == SyncMode.Rollback ? "Rollback" : "Lockstep");
            _netcodeLabel.ForeColor = mode == SyncMode.Rollback ? Color.DarkGreen : Color.DarkSlateBlue;
            RefreshPlayersList();
            Log($"session started vs {remoteLabel}");
            _disconnectButton.Enabled = true;

            // NAT traversal: a joiner discovers its public (reflexive) mesh endpoint and reports it to the
            // host, which shares it so peers can reach us across NAT. Additive to the LAN candidates, so
            // LAN/localhost play is unaffected whether or not this succeeds. The host is reached at the
            // address joiners connected to, so it doesn't report one.
            if (!_isHost && !_punchMode) StartReflexiveDiscovery();
        }

        /// <summary>Joiner: off-thread, STUN-discover our mesh socket's public endpoint and send it to the host.</summary>
        private void StartReflexiveDiscovery()
        {
            var mesh = _mesh;
            if (mesh == null) return;
            new Thread(() =>
            {
                var reflexive = mesh.DiscoverReflexive(TimeSpan.FromSeconds(2.5));
                if (reflexive == null)
                {
                    UiLog("(note) couldn't determine our public UDP endpoint (STUN blocked) — internet peers may be unreachable");
                    return;
                }
                UiLog($"our public UDP endpoint is {reflexive} — sharing it for NAT traversal");
                BeginInvokeUi(() =>
                {
                    if (_sessionActive && _peers.Count > 0)
                        QueueControl(_peers[0], ControlMessageType.Candidate,
                            HandshakeCodec.EncodeEndpoints(new[] { reflexive }));
                });
            })
            { IsBackground = true, Name = "BizHawkNetplay-stun-mesh" }.Start();
        }

        /// <summary>Host: record a joiner's reflexive endpoint and re-share the candidate lists.</summary>
        private void OnJoinerCandidate(PeerLink link, IPEndPoint reflexive)
        {
            if (!_sessionActive || !_isHost || _awaitingReconnect) return;
            if (!_peers.Contains(link)) return;                                  // dropped meanwhile
            if (reflexive.Equals(link.ReflexiveEndpoint)) return;               // unchanged
            link.ReflexiveEndpoint = reflexive;
            if (Verbose) Log($"{link.Label} public endpoint {reflexive}");
            RedistributeMesh();
        }

        private void FrameTick()
        {
            if (!_sessionActive || _driver == null) return;
            if (_frameTickRunning) return;

            _frameTickRunning = true;
            _frameTimer.Stop();
            var tickWatch = System.Diagnostics.Stopwatch.StartNew();
            double coreMs = 0, gateMs = 0, renderMs = 0;
            int packetsDrained = 0;
            int frameForTelemetry = _driver.CurrentFrame;
            _lastHashMs = 0;
            try
            {
                // Keep the audio device fed every tick, independent of how many frames we step this
                // tick (or none, during a stall) — the ring buffer decouples playback from stepping.
                _adapter?.PumpAudio();

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

                // Frozen while a dropped peer is being waited on — don't advance until the rejoin
                // resyncs everyone. Sticky pause and drift validation above must still run here.
                if (_awaitingReconnect)
                {
                    MaybeSendPing();
                    CheckLinkTimeouts();
                    return;
                }

                // Drain once per callback; FrameDriver caps the number of datagrams consumed. This is
                // deliberately separate from input sends, so Pump + Capture cannot duplicate a packet.
                _driver.PumpNetwork();
                packetsDrained = _driver.LastPacketsDrained;

                // State capture/import stays on this thread, but whole-state transfer runs on each
                // peer's writer thread. Hold the new baseline while that transfer is in flight.
                if (_resyncInProgress)
                {
                    if (_isHost) _driver.ResendLocalInputIfDue();
                    MaybeSendPing();
                    CheckLinkTimeouts();
                    return;
                }

                double nowMs = _paceClock.Elapsed.TotalMilliseconds;
                if (nowMs - _nextFrameDueMs > 3.0 * _frameMs)
                {
                    // Discard wall-clock debt, not emulated frames. Chasing a large hitch indefinitely
                    // is what starves WinForms presentation on slow cores.
                    _nextFrameDueMs = nowMs;
                    _pacingRebases++;
                }

                bool steppedThisTick = false;
                int framesThisTick = 0;
                while (framesThisTick < MaxFramesPerTick && nowMs + 0.25 >= _nextFrameDueMs)
                {
                    // Normally this loop runs once. A second frame compensates for an irregular ~25ms
                    // WinForms callback without reviving the old eight-frame catch-up bursts. Never start
                    // that second frame after the callback has already consumed its UI work budget.
                    if (framesThisTick > 0 && tickWatch.Elapsed.TotalMilliseconds >= FrameTickWorkBudgetMs) break;

                    _driver.CaptureLocalInput(); // capture local pad (paused-safe, via IInputApi) + send
                    var phase = System.Diagnostics.Stopwatch.StartNew();
                    if (!_driver.CurrentFrameReady())
                    {
                        gateMs += phase.Elapsed.TotalMilliseconds;
                        _driver.ResendLocalInputIfDue();
                        if (Verbose && nowMs - _lastStallLogMs >= 1000)
                        {
                            _lastStallLogMs = nowMs;
                            Log($"stalling at frame {_driver.CurrentFrame} — waiting for remote input");
                        }
                        break;
                    }
                    else
                    {
                        gateMs += phase.Elapsed.TotalMilliseconds; // includes rollback repair
                        phase.Restart();
                        _adapter!.AdvanceFrame(_driver.CurrentInputs(), renderVideo: true);
                        coreMs += phase.Elapsed.TotalMilliseconds;
                        _driver.CompleteFrame();
                        steppedThisTick = true;
                        framesThisTick++;
                        _nextFrameDueMs += _frameMs;
                        MaybeSendChecksum();
                        _fpsCount++;
                    }
                }

                // We hold EmuHawk paused, so its own run loop never presents the frames we advance here —
                // a paused window just keeps showing whatever its swapchain last held, which is why the
                // host's picture froze while the core, audio and netplay all kept running. Present the
                // latest frame ourselves, once per tick (the video twin of PumpAudio above).
                if (steppedThisTick)
                {
                    var phase = System.Diagnostics.Stopwatch.StartNew();
                    _adapter!.PresentVideo();
                    renderMs = phase.Elapsed.TotalMilliseconds;
                }

                // Liveness runs every tick, independent of stepping (so a stall doesn't stop our pings
                // and a dead link is still detected while we're waiting on it).
                MaybeSendPing();
                CheckLinkTimeouts();
                CheckUdpInputProgress();
                if (!_sessionActive || _driver == null) return;

                // Joiner: the host clears its resync counter once checksums re-agree, but a joiner gets no
                // such signal. Decay ours after running well past the last resync without another one —
                // otherwise a run of successful recoveries would eventually trip the "persistent desync"
                // give-up limit on a perfectly healthy joiner.
                if (!_isHost && _resyncCount > 0 && !_awaitingReconnect
                    && (DateTime.UtcNow - _lastResync).TotalSeconds > ResyncRecoverySeconds)
                {
                    _resyncCount = 0;
                    Log("back in sync — recovery confirmed");
                }

                // One-shot audio pipeline snapshot ~2s in, so a single test shows where sound breaks.
                if (!_audioStatsLogged && _driver.CurrentFrame >= 120)
                {
                    _audioStatsLogged = true;
                    Log(_adapter!.AudioStats());
                }
                else if (Verbose && _driver.CurrentFrame % 300 == 0 && _driver.CurrentFrame > 0
                    && _driver.CurrentFrame != _lastVerboseAudioFrame)
                {
                    _lastVerboseAudioFrame = _driver.CurrentFrame;
                    Log(_adapter!.AudioStats());
                }

                UpdateSessionUi(nowMs);
            }
            catch (Exception ex) { EndSession("session error: " + ex.Message); }
            finally
            {
                tickWatch.Stop();
                double elapsed = tickWatch.Elapsed.TotalMilliseconds;
                double clockMs = _paceClock.Elapsed.TotalMilliseconds;
                if (_sessionActive && elapsed >= Math.Max(12.0, _frameMs * 0.75)
                    && clockMs - _lastSlowTickLogMs >= 1000)
                {
                    _lastSlowTickLogMs = clockMs;
                    Log($"slow tick {elapsed:F1}ms at frame {frameForTelemetry}: core {coreMs:F1}, " +
                        $"rollback/gate {gateMs:F1}, hash {_lastHashMs:F1}, present {renderMs:F1}, " +
                        $"UDP drained {packetsDrained}, pacing rebases {_pacingRebases}");
                }
                _frameTickRunning = false;
                if (_sessionActive && _driver != null) _frameTimer.Start();
            }
        }

        private void UpdateSessionUi(double nowMs)
        {
            if (_driver == null || nowMs - _lastUiRefreshMs < 250) return;
            _lastUiRefreshMs = nowMs;

            double ping = -1;
            lock (_pingLock) { foreach (var link in _peers) if (link.PingMs > ping) ping = link.PingMs; }
            double effRttMs = (ping < 0 ? 0 : ping) + 2.0 * _simLatencyMs;
            _driver.Strategy.OnPacingReport(new PacingInfo(effRttMs, 0, 0));

            string pingStr = ping < 0 ? ""
                : $" — ping {effRttMs:F0}ms{(_simLatencyMs > 0 ? $" (incl. {2 * _simLatencyMs}ms sim)" : "")}{(_peers.Count > 1 ? " (worst)" : "")}";
            string rbStr = _driver.Strategy is RollbackStrategy rbs
                ? $" — rollback ×{rbs.RollbackCount} (last d{rbs.LastRollbackDepth}, max d{rbs.MaxRollbackDepthSeen}, tsync {rbs.TimeSyncStalls})"
                : "";

            if (_fpsClock.ElapsedMilliseconds >= 500)
            {
                _actualFps = _fpsCount * 1000.0 / _fpsClock.ElapsedMilliseconds;
                _fpsCount = 0;
                _fpsClock.Restart();
            }
            double targetFps = _frameMs > 0 ? 1000.0 / _frameMs : 60.0;
            bool cpuBound = _actualFps >= 0 && _actualFps < targetFps * 0.95;
            string speedStr = _actualFps < 0 ? ""
                : $" — {_actualFps:F0}/{targetFps:F0} fps ({_actualFps / targetFps * 100:F0}%{(cpuBound ? ", CPU-bound" : "")})";
            string udpStr = _udpWarningActive ? " — UDP recovering" : "";
            Status($"in session — frame {_driver.CurrentFrame}{speedStr}{pingStr}{rbStr}{udpStr}",
                _udpWarningActive || cpuBound ? Color.DarkOrange : Color.Green);
            RefreshPlayersList();
        }

        private void CheckUdpInputProgress()
        {
            if (_driver == null || _awaitingReconnect || _resyncInProgress) return;
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
                _mesh?.RequestRepunch();
                if (!_udpWarningActive)
                {
                    _udpWarningActive = true;
                    Log($"no UDP input from P{port + 1} for {seconds:F1}s — re-punching the input path");
                }
            }
            if (seconds >= UdpLostAfterSeconds)
                EndSession($"UDP input path lost for P{port + 1} ({seconds:F0}s without input; control link was still alive)");
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
                var hashWatch = System.Diagnostics.Stopwatch.StartNew();
                if (!rb.TryConfirmedChecksum(ChecksumInterval, out frame, out hash)) return;
                _lastHashMs = hashWatch.Elapsed.TotalMilliseconds;
            }
            else
            {
                frame = _driver.CurrentFrame;
                if (frame % ChecksumInterval != 0) return;
                var hashWatch = System.Diagnostics.Stopwatch.StartNew();
                hash = _adapter!.HashMainMemory();
                _lastHashMs = hashWatch.Elapsed.TotalMilliseconds;
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
                QueueControl(_peers[0], ControlMessageType.Checksum, EncodeChecksum(frame, hash));
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
                QueueControl(link, ControlMessageType.Ping, body);
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
            if (_awaitingReconnect || _resyncInProgress) return; // a state transfer may legitimately exceed 3s
            long now = DateTime.UtcNow.Ticks;
            long limit = TimeSpan.FromSeconds(PingTimeoutSeconds).Ticks;
            PeerLink? dead = null;
            foreach (var link in _peers)
            {
                if (link.ResyncReceiving) continue;
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
            // Include the simulated one-way UDP delay (RTT contribution = 2×) — the input actually rides
            // that delayed channel, so the recommendation must reflect it even though the TCP ping doesn't.
            double effWorst = worst + 2.0 * _simLatencyMs;
            string simNote = _simLatencyMs > 0 ? $" (incl. {2 * _simLatencyMs}ms sim)" : "";
            int suggested = (int)Math.Ceiling((effWorst / 2.0) / _frameMs) + 2; // two frames of jitter headroom
            if (suggested > _sessionDelay)
                Log($"worst link ping ~{effWorst:F0}ms{simNote}: input delay {suggested} is recommended for smooth play " +
                    $"(this session is {_sessionDelay}). If it stalls, reconnect with a higher 'Input delay'.");
            else
                Log($"worst link ping ~{effWorst:F0}ms{simNote}: input delay {_sessionDelay} is comfortable for this link.");
        }

        private void StartPeerIo(PeerLink link)
        {
            link.LastRecvTicks = DateTime.UtcNow.Ticks;
            link.WriterRunning = true;
            link.Writer = new Thread(() => PeerWriterLoop(link))
            { IsBackground = true, Name = "BizHawkNetplay-control-writer" };
            link.Reader = new Thread(() => PeerReaderLoop(link))
            { IsBackground = true, Name = "BizHawkNetplay-control-reader" };
            link.Writer.Start();
            link.Reader.Start();
        }

        private const long MaxQueuedControlBytes = 80L * 1024 * 1024;

        /// <summary>Queue one reliable control frame without ever waiting for socket flow control on
        /// EmuHawk's UI thread. A per-peer writer preserves ControlChannel ordering.</summary>
        private bool QueueControl(PeerLink link, ControlMessageType type, byte[] body, Action<bool>? completed = null)
        {
            if (body == null) body = Array.Empty<byte>();
            if (!link.WriterRunning) { completed?.Invoke(false); return false; }
            long bytes = body.LongLength + 5;
            long queued = Interlocked.Add(ref link.QueuedBytes, bytes);
            if (queued > MaxQueuedControlBytes)
            {
                Interlocked.Add(ref link.QueuedBytes, -bytes);
                completed?.Invoke(false);
                return false;
            }
            link.Outbound.Enqueue(new OutboundMessage(type, body, completed));
            link.OutboundSignal.Set();
            return true;
        }

        private void PeerWriterLoop(PeerLink link)
        {
            Exception? failure = null;
            try
            {
                while (link.WriterRunning)
                {
                    if (!link.Outbound.TryDequeue(out var msg))
                    {
                        link.OutboundSignal.WaitOne(250);
                        continue;
                    }
                    try
                    {
                        link.Control.Send(msg.Type, msg.Body);
                        msg.Completed?.Invoke(true);
                    }
                    catch
                    {
                        try { msg.Completed?.Invoke(false); } catch { }
                        throw;
                    }
                    finally { Interlocked.Add(ref link.QueuedBytes, -(msg.Body.LongLength + 5)); }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                link.WriterRunning = false;
                while (link.Outbound.TryDequeue(out var pending))
                {
                    Interlocked.Add(ref link.QueuedBytes, -(pending.Body.LongLength + 5));
                    try { pending.Completed?.Invoke(false); } catch { }
                }
                if (failure != null && _sessionActive)
                    BeginInvokeUi(() => OnPeerLinkLost(link, "control send failed: " + failure.Message));
            }
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
                            QueueControl(link, ControlMessageType.Pong, body);
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
                    else if (type == ControlMessageType.Candidate)
                    {
                        // A joiner reported its public (reflexive) endpoint; record it and re-share the
                        // candidate lists so everyone can reach it across NAT.
                        if (_isHost)
                        {
                            var eps = HandshakeCodec.DecodeEndpoints(body);
                            if (eps.Count > 0)
                                BeginInvokeUi(() => OnJoinerCandidate(link, eps[0]));
                        }
                    }
                    else if (type == ControlMessageType.ResyncBegin)
                    {
                        link.ResyncReceiving = true;
                        if (!_isHost) BeginInvokeUi(() =>
                        {
                            if (!_sessionActive) return;
                            _resyncInProgress = true;
                            Status("receiving authoritative resync state…", Color.DarkOrange);
                        });
                    }
                    else if (type == ControlMessageType.Resync)
                    {
                        link.ResyncReceiving = false;
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
            if (_resyncInProgress) return;
            if ((DateTime.UtcNow - _lastResync).TotalSeconds < ResyncGraceSeconds) return; // just resynced; give it time
            Log($"DESYNC at frame {frame} — peers disagree");
            PerformResyncAsHost();
        }

        /// <summary>
        /// Host recovery: capture an authoritative state on the emulator thread, enqueue its transfer
        /// to each peer's writer, and rebuild from a clean baseline. Simulation stays paused while the
        /// writers handle socket/flow-control waits, but WinForms remains responsive.
        /// Bounded by <see cref="MaxResyncs"/> so a persistent (non-transient) desync gives up instead
        /// of looping; a short grace window debounces repeat triggers for the same desync.
        /// </summary>
        private void PerformResyncAsHost()
        {
            if (!_sessionActive || !_isHost || _resyncInProgress) return;
            if ((DateTime.UtcNow - _lastResync).TotalSeconds < ResyncGraceSeconds) return; // debounce

            if (++_resyncCount > MaxResyncs)
            {
                EndSession($"persistent desync — gave up after {MaxResyncs} resync attempts (likely a determinism bug)");
                return;
            }
            try
            {
                var state = _adapter!.ExportState();
                _resyncInProgress = true;
                RebuildDriver();
                int peerCount = _peers.Count;
                int remaining = peerCount;
                int failed = 0;
                var transferWatch = System.Diagnostics.Stopwatch.StartNew();
                Status($"resync #{_resyncCount}: sending {state.Length / 1024}KiB to {peerCount} peer(s)…", Color.DarkOrange);
                Log($"resync #{_resyncCount}: captured {state.Length / 1024}KiB; transfer queued off the UI thread");

                if (remaining == 0) { _resyncInProgress = false; return; }
                foreach (var link in _peers)
                {
                    QueueControl(link, ControlMessageType.ResyncBegin, Array.Empty<byte>());
                    QueueControl(link, ControlMessageType.Resync, state, ok =>
                    {
                        if (!ok) Interlocked.Exchange(ref failed, 1);
                        if (Interlocked.Decrement(ref remaining) != 0) return;
                        BeginInvokeUi(() =>
                        {
                            if (!_sessionActive) return;
                            if (failed != 0)
                            {
                                _resyncInProgress = false;
                                EndSession("resync transfer failed");
                                return;
                            }
                            _resyncInProgress = false;
                            _paceClock.Restart();
                            _nextFrameDueMs = 0;
                            Log($"resync #{_resyncCount}: all {peerCount} peer transfer(s) complete in " +
                                $"{transferWatch.Elapsed.TotalMilliseconds:F0}ms; resuming");
                        });
                    });
                }
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
                _resyncInProgress = true;
                Status($"applying {state.Length / 1024}KiB host resync…", Color.DarkOrange);
                _adapter!.ImportState(state);
                RebuildDriver();
                _resyncInProgress = false;
                Log($"resync #{_resyncCount}: imported {state.Length / 1024}KiB host state; resuming");
            }
            catch (Exception ex) { _resyncInProgress = false; EndSession("resync apply failed: " + ex.Message); }
        }

        /// <summary>
        /// Rebuild the frame driver from the current core state as a fresh frame-0 baseline: new
        /// pipeline, cleared checksums, reset pacing and drift baseline. In-flight pre-resync UDP
        /// datagrams carry high frame numbers and are dropped by the FrameDriver's far-future guard.
        /// </summary>
        private void RebuildDriver()
        {
            try { _driver?.Dispose(); } catch { } // release the old rollback ring before replacing it
            _driver = CreateDriver();
            _startEmuFrame = APIs.Emulation.FrameCount();
            lock (_hashLock) { _frameHashes.Clear(); }
            _driver.Start();
            _lastResync = DateTime.UtcNow;
            _paceClock.Restart();
            _nextFrameDueMs = 0;
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
                    _localPort, _sessionDelay, redundancy: 8, rollbackWindow: _rollbackDepth, portCount: _playerCount);

            return new FrameDriver(_adapter!, _transport!, p => new LockstepStrategy(p),
                _localPort, _sessionDelay, redundancy: 8, portCount: _playerCount);
        }

        /// <summary>N64 rollback repair can synchronously resimulate a deep state ring on EmuHawk's UI
        /// thread. Until that work is incrementally budgeted, favor responsiveness and reliability by
        /// forcing this heavy system to lockstep even if Rollback was selected explicitly.</summary>
        private void ApplyHeavyCoreNetcodeDefault()
        {
            if (_adapter == null || !string.Equals(_adapter.SystemId, "N64", StringComparison.OrdinalIgnoreCase)) return;
            if (_netcodeChoice != NetcodeChoice.Lockstep)
                Log("N64 uses lockstep for stability — deep rollback can freeze presentation on this core.");
            _netcodeChoice = NetcodeChoice.Lockstep;
        }

        // Reflection flags for reaching EmuHawk internals (Tools/LuaConsole members aren't all public).
        private const System.Reflection.BindingFlags AnyInstance =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        /// <summary>
        /// Log a heads-up (NEVER block) if something that also owns input or the frame clock is active —
        /// a loaded movie (playback/record, incl. TAStudio) or a running Lua script. These can desync
        /// netplay, but we only warn and let the user proceed. Everything here is best-effort and swallows
        /// its own errors: it must never disrupt starting a session (and resolves EmuHawk types by name,
        /// so this file carries no compile-time dependency on them).
        /// </summary>
        private void WarnSessionHazards()
        {
            try
            {
                if (APIs.Movie.IsLoaded())
                {
                    string mode = ""; try { mode = APIs.Movie.Mode() ?? ""; } catch { }
                    bool rec = string.Equals(mode, "RECORD", StringComparison.OrdinalIgnoreCase);
                    Log($"WARNING: a movie is {(rec ? "recording" : "loaded")} (or TAStudio is open) — it injects " +
                        "input and drives frame advance, which will likely desync netplay. Stop it if the session won't sync.");
                }
            }
            catch { /* movie API unavailable — ignore */ }

            try
            {
                int lua = RunningLuaScripts();
                if (lua > 0)
                    Log($"WARNING: {lua} Lua script(s) running — Lua can set input, load states, or call " +
                        "emu.frameadvance and may desync netplay. Stop them (Lua Console → Stop All Scripts) if you see desyncs.");
            }
            catch { /* best-effort — ignore */ }
        }

        /// <summary>Count Lua scripts in the RUNNING state, only if the Lua Console is already open (never
        /// instantiates it). EmuHawk types are resolved by name; any failure returns 0 (a warning-only
        /// heads-up must never disrupt a session).</summary>
        private int RunningLuaScripts()
        {
            try
            {
                object? mf = MainForm;
                if (mf == null) return 0;
                var asm = mf.GetType().Assembly;
                var luaConsoleType = asm.GetType("BizHawk.Client.EmuHawk.LuaConsole");
                if (luaConsoleType == null) return 0;
                var tools = mf.GetType().GetField("Tools", AnyInstance)?.GetValue(mf)
                         ?? mf.GetType().GetProperty("Tools", AnyInstance)?.GetValue(mf);
                if (tools == null) return 0;
                var isLoaded = tools.GetType().GetMethod("IsLoaded", new[] { typeof(Type) });
                if (!(isLoaded?.Invoke(tools, new object[] { luaConsoleType }) as bool? ?? false)) return 0; // closed
                var console = tools.GetType().GetProperty("LuaConsole", AnyInstance)?.GetValue(tools);
                var luaImp = console?.GetType().GetField("LuaImp", AnyInstance)?.GetValue(console)
                          ?? console?.GetType().GetProperty("LuaImp", AnyInstance)?.GetValue(console);
                var scriptList = luaImp?.GetType().GetProperty("ScriptList", AnyInstance)?.GetValue(luaImp)
                    as System.Collections.IEnumerable;
                if (scriptList == null) return 0;
                int running = 0;
                foreach (var file in scriptList)
                {
                    var state = file?.GetType().GetProperty("State", AnyInstance)?.GetValue(file);
                    if (state != null && string.Equals(state.ToString(), "Running", StringComparison.OrdinalIgnoreCase))
                        running++;
                }
                return running;
            }
            catch { return 0; }
        }

        /// <summary>Wrap the input transport in the artificial-latency simulator if the diagnostic is set.</summary>
        private ITransport WrapSimLatency(ITransport inner)
            => _simLatencyMs > 0 ? new LatencySimTransport(inner, _simLatencyMs) : inner;

        // ------------------------------------------------------------------ NAT / reachability

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
        private void TryPublishHostAddress(int port)
        {
            try
            {
                string lan = UpnpPortMapper.PrimaryLanIp();
                if (_upnpEnabled)
                {
                    _upnpMapping = UpnpPortMapper.TryAddPortMapping(port, lan, "BizHawk Netplay", TimeSpan.FromSeconds(2.5));
                    UiLog(_upnpMapping != null
                        ? $"UPnP: forwarded port {port} (TCP+UDP) to {lan} on your router"
                        : $"UPnP: no router accepted a forward — for internet play, forward port {port} (TCP+UDP) to {lan} manually");
                }
                else
                {
                    UiLog($"UPnP auto-forward is off — for internet play, forward port {port} (TCP+UDP) to {lan} manually");
                }

                var pub = StunClient.DiscoverPublicAddress(TimeSpan.FromSeconds(2.0));
                UiLog(pub != null
                    ? $"internet joiners connect to {pub.Address}:{port}  (LAN: {lan}:{port})"
                    : $"couldn't determine your public IP (offline or STUN blocked); LAN joiners use {lan}:{port}");
            }
            catch (Exception ex) { UiLog("(note) NAT setup skipped: " + ex.Message); }
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
            if (!_peers.Contains(link)) return; // reader/writer can both report the same broken link

            // Punch sessions have no TCP listener to re-accept on, so a lost link just ends for both roles.
            if (_punchMode)
            {
                EndSession($"lost connection to {link.Label}: {why}");
                return;
            }

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

        /// <summary>All candidate UDP endpoints of the given links (LAN plus reflexive/public where
        /// known), optionally excluding one — the peer set the mesh sends to and accepts from. The mesh
        /// tolerates dead candidates, so including both lets the same session work on LAN and over NAT.</summary>
        private static List<IPEndPoint> CandidatesExcept(IReadOnlyList<PeerLink> links, PeerLink? except)
        {
            var eps = new List<IPEndPoint>();
            foreach (var l in links)
            {
                if (ReferenceEquals(l, except)) continue;
                eps.Add(l.UdpEndpoint);
                if (l.ReflexiveEndpoint != null) eps.Add(l.ReflexiveEndpoint);
            }
            return eps;
        }

        /// <summary>Host: point our mesh at every currently-connected joiner's candidate endpoints.</summary>
        private void UpdateMeshPeers()
        {
            if (_mesh == null) return;
            try { _mesh.SetPeers(CandidatesExcept(_peers, null)); } catch { }
        }

        /// <summary>Host: re-point our own mesh and re-send each joiner its candidate peer list (used
        /// whenever the candidate set changes — a reflexive candidate arrives, or someone rejoins).</summary>
        private void RedistributeMesh()
        {
            UpdateMeshPeers();
            foreach (var l in _peers)
            {
                QueueControl(l, ControlMessageType.PeerList,
                    HandshakeCodec.EncodeEndpoints(CandidatesExcept(_peers, l)));
            }
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
                    try { tcp.ReceiveTimeout = HandshakeReceiveTimeoutMs; } catch { } // a silent rejoiner can't wedge the wait
                    var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
                    var channel = new ControlChannel(tcp.GetStream());
                    try
                    {
                        var greet = Handshake.HostGreet(channel, _hostIdentity!, _hostPrefs!, _hostUdpPort);
                        try { tcp.ReceiveTimeout = 0; } catch { } // handshake done: restore blocking reads
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
        /// UI thread: capture the current authoritative state, then hand the potentially blocking
        /// welcome/state transfer to a background thread. Simulation is already held for reconnect.
        /// </summary>
        private void CompleteReconnect(TcpClient tcp, ControlChannel channel, IPAddress remoteIp, IPEndPoint udpEp, int freedPort)
        {
            if (!_sessionActive || !_awaitingReconnect) { try { tcp.Close(); } catch { } return; }
            try
            {
                _greetingTcp = tcp; // teardown can abort the background state/barrier transfer
                var state = _adapter!.ExportState();
                var meshPeers = CandidatesExcept(_peers, null);
                Status($"P{freedPort + 1} rejoined — sending {state.Length / 1024}KiB state…", Color.DarkOrange);

                // The rejoiner's mesh peers = every current survivor (it reaches the host directly). It
                // adopts this state + mesh via Welcome and rebuilds fresh on its own side.
                new Thread(() =>
                {
                    try
                    {
                        try { tcp.ReceiveTimeout = HandshakeReceiveTimeoutMs; } catch { }
                        Handshake.HostSendWelcome(channel, freedPort, _playerCount, _sessionDelay, _mode, state,
                            meshPeers, useReadyBarrier: true);
                        Handshake.HostWaitReady(channel);
                        try { tcp.ReceiveTimeout = 0; } catch { }
                        BeginInvokeUi(() => FinishReconnect(tcp, channel, remoteIp, udpEp, freedPort, state));
                    }
                    catch (Exception ex)
                    {
                        try { tcp.Close(); } catch { }
                        BeginInvokeUi(() => { if (_sessionActive) EndSession("reconnect state transfer failed: " + ex.Message); });
                    }
                }) { IsBackground = true, Name = "BizHawkNetplay-reconnect-state" }.Start();
            }
            catch (Exception ex) { EndSession("reconnect failed: " + ex.Message); }
        }

        private void FinishReconnect(TcpClient tcp, ControlChannel channel, IPAddress remoteIp,
            IPEndPoint udpEp, int freedPort, byte[] state)
        {
            if (!_sessionActive || !_awaitingReconnect) { try { tcp.Close(); } catch { } return; }
            try
            {
                var link = new PeerLink
                {
                    Tcp = tcp, Control = channel, RemotePort = freedPort,
                    UdpEndpoint = udpEp, Label = $"P{freedPort + 1} ({remoteIp})",
                };
                // Bring each survivor up to date: refresh its mesh with the rejoiner's endpoint, then
                // resync it to the same state. Do not release the rejoiner with GO until those queued
                // state writes complete, otherwise the first peer can run while another is still loading.
                var allPeers = new List<PeerLink>(_peers) { link };
                var survivors = new List<PeerLink>(_peers);
                _resyncInProgress = true;
                RebuildDriver();
                int remaining = survivors.Count;
                int failed = 0;
                if (remaining == 0)
                {
                    ReleaseReconnectedPeer(link, state.Length);
                    return;
                }
                foreach (var survivor in survivors)
                {
                    QueueControl(survivor, ControlMessageType.PeerList,
                        HandshakeCodec.EncodeEndpoints(CandidatesExcept(allPeers, survivor)));
                    QueueControl(survivor, ControlMessageType.ResyncBegin, Array.Empty<byte>());
                    QueueControl(survivor, ControlMessageType.Resync, state, ok =>
                    {
                        if (!ok) Interlocked.Exchange(ref failed, 1);
                        if (Interlocked.Decrement(ref remaining) != 0) return;
                        BeginInvokeUi(() =>
                        {
                            if (!_sessionActive) { try { tcp.Close(); } catch { } return; }
                            if (failed != 0) { EndSession("reconnect resync transfer failed"); return; }
                            ReleaseReconnectedPeer(link, state.Length);
                        });
                    });
                }
            }
            catch (Exception ex) { EndSession("reconnect failed: " + ex.Message); }
        }

        private void ReleaseReconnectedPeer(PeerLink link, int stateLength)
        {
            // The client is still blocked in the READY/GO handshake, so send GO off-thread before its
            // live reader/writer start consuming this ControlChannel.
            new Thread(() =>
            {
                try
                {
                    Handshake.HostSendGo(link.Control);
                    BeginInvokeUi(() =>
                    {
                        if (!_sessionActive || !_awaitingReconnect) { try { link.Tcp?.Close(); } catch { } return; }
                        _peers.Add(link);
                        _greetingTcp = null;
                        UpdateMeshPeers();
                        StartPeerIo(link);
                        _awaitingReconnect = false;
                        _resyncInProgress = false;
                        _reconnectPort = -1;
                        _resyncCount = 0;
                        _paceClock.Restart();
                        _nextFrameDueMs = 0;
                        Log($"{link.Label} reconnected — {stateLength / 1024}KiB baseline synchronized; resuming");
                        Status($"reconnected P{link.RemotePort + 1} — resuming", Color.Green);
                    });
                }
                catch (Exception ex)
                {
                    try { link.Tcp?.Close(); } catch { }
                    BeginInvokeUi(() => { if (_sessionActive) EndSession("reconnect GO failed: " + ex.Message); });
                }
            }) { IsBackground = true, Name = "BizHawkNetplay-reconnect-go" }.Start();
        }

        private void FailSession(string reason)
        {
            _pendingJoinIp = null; // a failed connect shouldn't land in the recent-IPs list
            Log("connection failed: " + reason);
            TeardownNetwork();
            try { _adapter?.DisableAudio(); } catch { } // restore EmuHawk's normal audio wiring
            ApplyBackgroundConfig(false);
            try { APIs.EmuClient.Unpause(); } catch { } // undo the freeze from OnGo
            ResetPunchUi();
            SetBusy(false);
            Status("Idle.", Color.DimGray);
        }

        private void EndSession(string reason)
        {
            if (!_sessionActive && _listener == null && _peers.Count == 0 && !_punchMode) { SetBusy(false); return; }
            _frameTimer.Stop();

            // Preserve a clean "friend left" signal without doing socket I/O on the UI thread. Give
            // the per-peer writers one very short opportunity; a state transfer or dead link is closed
            // immediately after the deadline instead of making Disconnect appear frozen.
            if (_sessionActive && _peers.Count > 0)
            {
                var bye = new CountdownEvent(_peers.Count);
                foreach (var link in _peers)
                    QueueControl(link, ControlMessageType.Bye, Array.Empty<byte>(), _ => { try { bye.Signal(); } catch { } });
                try { bye.Wait(50); } catch { }
                try { bye.Dispose(); } catch { }
            }

            _sessionActive = false;
            _resyncInProgress = false;
            _simUnresponsive = false; _simUnresponsiveCheck.Checked = false; // clear the diagnostic
            try { if (_timerResRaised) { timeEndPeriod(1); _timerResRaised = false; } } catch { }

            TeardownNetwork();

            try { _adapter?.DisableAudio(); } catch { } // restore EmuHawk's normal audio wiring
            ApplyBackgroundConfig(false); // restore the user's focus/pause preferences
            try { APIs.EmuClient.Unpause(); } catch { }
            lock (_hashLock) { _frameHashes.Clear(); }

            _netcodeLabel.Text = "Netcode in use: —";
            _netcodeLabel.ForeColor = Color.DimGray;
            RefreshPlayersList(); // session inactive now → clears the list
            ResetPunchUi();

            Log("session ended: " + reason);
            Status("Idle.", Color.DimGray);
            SetBusy(false);
        }

        private void TeardownNetwork()
        {
            // Remove any UPnP forward we added, off-thread (it's a router round-trip).
            var upnp = _upnpMapping;
            _upnpMapping = null;
            if (upnp != null)
                new Thread(() => { try { upnp.Remove(TimeSpan.FromSeconds(2)); } catch { } })
                { IsBackground = true, Name = "BizHawkNetplay-upnp" }.Start();

            // Stop any in-flight reconnect wait first; its loop exits once these flags clear.
            _awaitingReconnect = false;
            var reconnect = _reconnectThread;
            _reconnectThread = null;
            _reconnectPort = -1;

            try { _listener?.Stop(); } catch { }
            _listener = null;
            try { _joiningTcp?.Close(); } catch { } // unblock a join connect that's still dialing
            _joiningTcp = null;
            try { _greetingTcp?.Close(); } catch { } // abort a joiner we're blocked greeting (Disconnect mid-handshake)
            _greetingTcp = null;

            var peers = new List<PeerLink>(_peers);
            _peers.Clear();
            foreach (var link in peers)
            {
                link.WriterRunning = false;
                try { link.OutboundSignal.Set(); } catch { }
            }
            foreach (var link in peers) { try { link.Tcp?.Close(); } catch { } }

            try { (_transport as IDisposable)?.Dispose(); } catch { }
            try { _punchLink?.Dispose(); } catch { } // in case _transport is a sim-latency wrapper over it
            try { _driver?.Dispose(); } catch { } // release the rollback ring's savestates
            _transport = null; _mesh = null; _punchLink = null;
            _punchMode = false; _punchState = null;
            _driver = null;

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

            // Determinism gate: normally the core's own report. The experimental override lets a peer
            // assert a core that reports non-deterministic (often just "not requested", e.g. N64 with no
            // movie) is fine to net-play. Both peers must opt in — the negotiator checks both flags — and
            // desync detection still catches a genuine divergence.
            bool deterministic = a.VerifyDeterministicMode();
            if (!deterministic && _allowNonDetCheck.Checked)
            {
                deterministic = true;
                Log("WARNING: overriding the non-deterministic core check (experimental) — both players must " +
                    "enable this, and a truly non-deterministic core will desync and give up.");
            }
            return new PeerIdentity(Protocol, a.RomHash, a.CoreName, a.CoreVersion,
                a.SyncSettingsDigest, layouts, deterministic, maxRollbackDepth: depth);
        }

        /// <summary>Map the "My controls" dropdown to an input-source port: P1..P4 (0..3) or -1 (assigned port).</summary>
        private int InputSourceFromCombo()
        {
            int idx = _inputSourceCombo.SelectedIndex;
            return idx >= 0 && idx <= 3 ? idx : -1; // index 4 ("Assigned port") or none => -1
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
            _playersBox.Enabled = host; // only the host chooses the player count
            _goButton.Text = host ? "Start Hosting" : "Join";
        }

        private void SetBusy(bool busy)
        {
            _goButton.Enabled = !busy;
            _hostRadio.Enabled = _joinRadio.Enabled = !busy;
            _ipBox.Enabled = !busy && _joinRadio.Checked;
            _playersBox.Enabled = !busy && _hostRadio.Checked;
            _portBox.Enabled = _delayBox.Enabled = !busy;
            _netcodeCombo.Enabled = _passwordBox.Enabled = _upnpCheck.Enabled = !busy;
            _inputSourceCombo.Enabled = !busy;
            _probeButton.Enabled = !busy;
            _punchButton.Enabled = !busy;
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
