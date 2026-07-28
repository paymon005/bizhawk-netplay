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
        // v11 changes what the advertised rollback depth MEANS (measured against the model the session
        // actually runs — snapshots elided on confirmed frames, a repair allowed two frame periods) and
        // the threshold peers compare it against. Both ends must agree on both, or one could negotiate
        // rollback while the other negotiated lockstep.
        //
        // v10 hashed main memory with FNV-1a over 32-bit words, sampled with a rotating stride on
        // domains too large to read whole. That value also crosses the wire, so the same rule applies:
        // a version bump turns a build mismatch into a clean refusal at the handshake instead of a
        // phantom desync every interval.
        private const int Protocol = 11;
        private const int DefaultPort = 47800;
        private const int ChecksumInterval = 300; // full-memory hashes are intentionally infrequent (~5s at 60fps)

        /// <summary>
        /// How many frame periods one rollback repair may spend. Requiring it to fit inside a single
        /// period is stricter than the frame tick now needs — the catch-up path absorbs a short overrun
        /// — and on a heavy core that strictness is the difference between a usable prediction horizon
        /// and none. Two periods buys N64 depth 3 where one period allows 1. Repaired frames emit no
        /// audio, but they never did: the sample for a frame is produced by its original (predicted)
        /// run, so a deeper repair costs wall clock, not sound.
        /// </summary>
        private const double RepairBudgetFrames = 2.0;

        /// <summary>At or below this ring depth, rollback is working but has little room — worth saying
        /// so once, since the user chose a mode whose whole point is hiding latency.</summary>
        private const int ShallowRollbackDepth = 4;

        /// <summary>Shallowest ring worth building. Below this rollback predicts nothing useful, but a
        /// ring of 1 would leave no room for the correction that is already in flight.</summary>
        private const int MinRollbackRing = 2;

        // Window size at 96 DPI, in client pixels. The Connection tab is the widest: its connection log
        // spans x=12..556 inside an 8px page padding, so anything under ~572 clips it horizontally. The
        // old minimum was 520 — narrower than the content it was meant to protect — which let the window
        // be dragged (or restored by BizHawk at a remembered size) into clipping the boxes on the right.
        private const int DesignClientWidth = 600;
        private const int DesignClientHeight = 620;
        private const int MinClientWidth = 580;
        private const int MinClientHeight = 560;

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
        private Label _playersHint = null!;
        private NumericUpDown _delayBox = null!;
        private CheckBox _autoDelayCheck = null!;
        private NumericUpDown _autoDelayMaxBox = null!;
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
        private Label _netcodeLabel = null!;
        private RichTextBox _connLog = null!;
        private CheckBox _simUnresponsiveCheck = null!;
        private CheckBox _upnpCheck = null!;
        private TextBox _passwordBox = null!;
        private NumericUpDown _simLatencyBox = null!;
        private ListView _playersList = null!;
        private Label _status = null!;
        private Button _punchButton = null!;
        private Label _punchInstructions = null!;
        private Label _myCodeLabel = null!;
        private Label _peerCodeLabel = null!;
        private GroupBox _punchGroup = null!;
        private TextBox _myCodeBox = null!;
        private Button _copyCodeButton = null!;
        private TextBox _peerCodeBox = null!;
        private Button _connectButton = null!;
        private Label _punchStatus = null!;
        private readonly ToolTip _tips = new ToolTip(); // owns a native window — disposed with the form
        private NetplaySettings _settings = null!;     // persisted UI prefs (UPnP, port, delay, netcode, recent IPs)
        private bool _loadingSettings;                  // suppress change-handler saves while applying loaded prefs
        private string? _pendingJoinIp;                 // regular-join IP awaiting a successful connect, then recorded

        private int _simLatencyMs; // diagnostic: artificial one-way UDP delay for this session (0 = off)
        private bool _upnpEnabled;  // host: whether to attempt the UPnP auto-forward (captured from the checkbox)
        private UpnpMapping? _upnpMapping; // host: the router forward we added, removed on session end

        private bool Verbose => _verboseCheck.Checked;

        private int _startEmuFrame; // emulator FrameCount at session start, for drift detection
        private TextBox _log = null!;
        private int _logLines;      // lines currently in _log, tracked so trimming needn't split its text

        /// <summary>One control link to a peer. Host: one per joiner. Joiner: one (the host).</summary>
        private sealed class PeerLink
        {
            public TcpClient Tcp = null!;      // null for a punched link (control rides the mesh socket)
            public System.IO.Stream? ControlStream; // punched links: the reliable stream under Control
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
            public int Attempt;               // connection-attempt token for stale reader/writer callbacks
            public double PingMs = -1;        // guarded by _pingLock
            public int PingCount;             // guarded by _pingLock
            public long LastRecvTicks;        // Stopwatch ticks of the last message from this peer (Interlocked)
            public volatile bool ResyncReceiving; // large inbound state frame is allowed to exceed ping timeout
            public int ReceivingResyncEpoch;      // expected generation while ResyncReceiving is true
            public int ReceivingResyncBytes;      // declared state size, checked against the completed frame
            public long ResyncReceiveDeadlineTicks; // bounds BEGIN-without-a-complete-state stalls
            public long TimeoutGraceUntilTicks;   // we sent this peer a whole state: its reader is busy consuming
                                                  // that frame and can't pong until it lands (Interlocked)
            // Frame-advantage exchange (ControlMessageType.Pacing), guarded by _pingLock. Advantage
            // measured locally is inflated by one-way latency; subtracting the peer's own measurement
            // cancels that term, which is why both numbers have to travel.
            public int LocalAdvantage;            // our frame minus theirs, as of their last report
            public int RemoteAdvantage;           // the same quantity as they measured it
            public bool AdvantageKnown;           // false until a peer on a build that reports has answered
            public int PacingSendSequence;         // our monotonically increasing wire sample id
            public int LastReceivedPacingSequence; // peer sample most recently incorporated
            public int AwaitingAppliedEpoch;       // host barrier: non-zero until this peer applies that epoch
            public long AppliedDeadlineTicks;      // bounds a peer that stays alive but never applies state
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

        /// <summary>
        /// A socket receive timeout applies to each individual read, so a peer can otherwise keep a
        /// greeting alive forever by sending one byte just before every timeout. This timer bounds the
        /// whole authentication phase and closes the socket to unblock a pending read at the deadline.
        /// </summary>
        private sealed class AbsoluteSocketDeadline : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly System.Threading.Timer _timer;
            // 0 = armed, 1 = completed/disarmed, 2 = expired and owns closing the socket.
            private int _state;

            public AbsoluteSocketDeadline(TcpClient tcp, int timeoutMs)
            {
                _tcp = tcp;
                _timer = new System.Threading.Timer(_ => Expire(), null, timeoutMs, Timeout.Infinite);
            }

            public bool Expired => Volatile.Read(ref _state) == 2;

            public bool TryComplete()
            {
                int previous = Interlocked.CompareExchange(ref _state, 1, 0);
                if (previous == 0)
                    try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                return previous != 2;
            }

            private void Expire()
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
                try { _tcp.Close(); } catch { }
            }

            public void Dispose()
            {
                TryComplete();
                try { _timer.Dispose(); } catch { }
            }
        }

        // --- Session state (all touched on the UI thread except where noted) ---
        private EmuHawkAdapter? _adapter;
        private ITransport? _transport;        // the FrameDriver's input channel (see below)
        private MeshUdpTransport? _mesh;       // direct peer-to-peer UDP: host and joiners both send to all peers
        private List<PeerRoute> _meshOthers = new List<PeerRoute>(); // joiner: grouped routes to non-host peers

        // UDP-punch path (2-player, no port-forwarding): one socket does STUN + hole-punch, then carries
        // both the reliable control channel and the input hot path. Set up in two steps (generate our
        // connect code, then punch to the pasted peer code) before the normal session bring-up runs.
        private PeerIdentity? _punchId;         // prepared handshake identity, captured when punch setup began
        private SessionPreferences? _punchPrefs;

        // Punched joiners admitted into a normal hosted lobby (RemotePlay-style): the UI-side punch
        // worker confirms the path and enqueues the confirmed control stream; the lobby thread greets
        // it exactly like a TCP accept. Targets are UI-thread only; the queue crosses to the lobby.
        private sealed class PunchAdmission
        {
            public IPEndPoint Endpoint = null!;
            public System.IO.Stream Control = null!;
        }
        private readonly ConcurrentQueue<PunchAdmission> _punchAdmissions = new ConcurrentQueue<PunchAdmission>();
        private readonly List<IPEndPoint> _lobbyPunchTargets = new List<IPEndPoint>();
        // volatile: the accept thread reads this as its teardown signal (null => Disconnect stopped us),
        // and it's written from the UI thread. Every other cross-thread field here is volatile too.
        private volatile TcpListener? _listener;
        private volatile TcpClient? _joiningTcp; // a join connect still in progress, so Disconnect can close it
        private volatile TcpClient? _greetingTcp; // a joiner we've accepted but are still greeting, so teardown can abort it
        // Attempt tokens + tracked handshake sockets live in Core (ConnectionLifecycle), which
        // atomically closes the accept-vs-teardown registration race.
        private readonly ConnectionLifecycle _lifecycle = new ConnectionLifecycle();
        private const int HandshakeReceiveTimeoutMs = 15000; // a joiner that connects but never HELLOs can't wedge the host
        // Odd count, and enough of them that the high-water figure means something: the delay estimate
        // now needs the link's swing as well as its median (see LobbyDelayPolicy).
        private const int LobbyProbeSamples = 9;
        private const int LobbyProbeTimeoutMs = 5000;
        // How long a started punch keeps knocking. Long on purpose: the asymmetric flow means one
        // side starts minutes before the other finishes reading a text message.
        private const int PunchPatienceSeconds = 300;
        private const int ConnLogMaxLines = 200; // connection-log history cap, trimmed back to ConnLogKeepLines
        private const int ConnLogKeepLines = 120;
        private const int LogMaxLines = 5000;    // Log-tab cap; generous (it's the diagnostic record) but bounded
        private const int LogKeepLines = 3000;   // ...so one trim covers 2000 appends
        private FrameDriver? _driver;
        private bool _sessionDriverPrepared; // built/started before READY, activated only after GO
        private byte[]? _preJoinRestoreState; // restored if pre-READY import never reaches GO
        private readonly List<PeerLink> _peers = new List<PeerLink>();
        private readonly System.Windows.Forms.Timer _frameTimer;
        private volatile bool _sessionActive;
        private bool _isHost;      // host is authoritative for desync detection + resync
        private int _playerCount = 2;
        private int _localPort;    // our controller port, for rebuilding the driver on resync
        private int _resyncCount;   // resyncs since the last confirmed re-sync (bounds infinite loops)
        // Tells "the emulation drifted once" apart from "these two machines were never comparing the
        // same thing": a real drift agrees for a while first, a systematic mismatch never agrees at all.
        private bool _agreedSinceResync;
        private int _desyncsWithoutAgreement;
        private long _lastResyncStamp; // monotonic timestamp; debounces near-simultaneous resync triggers
        private bool _forceDesyncOnce; // diagnostic: corrupt the next checksum to exercise resync
        private const int MaxResyncs = 6;
        private const double ResyncGraceSeconds = 2.0;
        private const double ResyncRecoverySeconds = 8.0; // joiner clears its resync counter after this long without another
        // Delay is selected before WELCOME and then remains fixed. In rollback it trades local response
        // time for shallower visual corrections; in lockstep it also prevents routine network stalls.
        private bool _audioStatsLogged; // one-shot audio pipeline diagnostic per session
        private double _lastStallLogMs = double.NegativeInfinity;
        private bool _resyncInProgress;
        private bool _resyncReleaseQueued;
        private readonly object _generationLock = new object();
        private SessionGeneration _generation = SessionGeneration.Legacy;
        private readonly FrameAdvantageTracker _frameAdvantage = new FrameAdvantageTracker();

        // Desync detection: the host aggregates every peer's checksum for a frame (its own + each
        // joiner's); once it has them all it verifies they agree. Joiners just report to the host.
        // The aggregation rules live in Core (ChecksumLedger); the lock serializes UI + reader threads.
        private readonly object _hashLock = new object();
        private readonly ChecksumLedger _checksums = new ChecksumLedger();

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
        private long _reconnectStartedStamp;
        private byte[]? _reconnectState; // authoritative baseline captured at the instant the peer drops
        private SessionGeneration _reconnectGeneration;
        private PeerLink? _pendingReconnectLink; // READY, but held outside _peers until every survivor applies
        private int _pendingReconnectStateLength;
        private SessionGeneration _pendingReconnectGeneration;
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
        // A peer we've just sent a whole state to goes quiet while its reader consumes that frame, so it
        // gets a longer leash — but only that peer, and only for as long as the transfer could plausibly
        // take. Scaled by payload: a flat timeout would either hang on a dead peer or shoot a live one
        // mid-transfer (an N64 state is megabytes; on a weak uplink that is minutes, not seconds).
        // State-transfer deadline math lives in Core (StateTransferBudget) so its invariants are tested.
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
        // Whether the core reproduced the same memory on replay. Unlike depth this is a correctness
        // result, not a performance one, so forcing Rollback does not get to override it.
        private bool _replayDeterministic = true;
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
        private const double FrameTickWorkBudgetMs = 8.0; // floor for fast cores; see TickBudgetMs
        private double _nextFrameDueMs;
        private double _recentCoreFrameMs; // conservative rolling cost used before committing a hidden first frame
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

        // Advanced fps alone can't tell a slow core from a stalling link from pacing debt being
        // discarded — all three read as "under 60". These carry the breakdown that separates them.
        private readonly PacingStats _pacing = new PacingStats();
        private PacingSummary _lastPacing;
        private double _lastPacingLogMs = double.NegativeInfinity;
        private double _stallHintSinceMs = double.NegativeInfinity;
        private bool _stallHintShown; // one-time "your link is stalling" hint per session
        private bool _hashDiagLogged; // one-time "which checksum path ran" line per session
        private double _lastTickClockMs = -1; // pace-clock stamp of the previous tick, for gap stats
        private const double StallHintPct = 15.0;      // stalled share of ticks worth complaining about
        private const double StallHintSustainMs = 5000; // ...but only once it persists, not on one burst

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
            // Every control on every tab is positioned in hardcoded pixels, laid out against a 96-DPI
            // screen. Without an auto-scale mode WinForms leaves those coordinates alone while the OS
            // hands the process a larger font, so at 125% and up the labels grow into their neighbours
            // and the right-hand controls run off the edge — text and boxes visibly clipped.
            //
            // Dpi mode rather than Font mode on purpose: it scales purely by the DPI ratio, so at 100%
            // the factor is exactly 1 and the layout is untouched. Font mode would need the design-time
            // font metrics declared correctly, and getting that guess wrong would shift the layout for
            // everyone currently seeing it render fine.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(DesignClientWidth, DesignClientHeight);

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
        /// <summary>
        /// Apply the window's minimum size once the real DPI is known.
        ///
        /// <see cref="Form.MinimumSize"/> is one of the few properties WinForms deliberately does NOT
        /// auto-scale, so setting it in the constructor would pin a 96-DPI number onto a 144-DPI window
        /// and let it be dragged down to two-thirds of the size its content needs. The handle is the
        /// first point <see cref="Control.DeviceDpi"/> is meaningful, and it accounts for the border and
        /// title bar so the figures above can stay in client pixels like every other coordinate here.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            float scale = DeviceDpi / 96f;
            if (scale <= 0) scale = 1f;
            // Border + title bar, already in device pixels. Guarded because a minimum that came out
            // under the content would reintroduce exactly the clipping this exists to prevent.
            int chromeW = Math.Max(0, Size.Width - ClientSize.Width);
            int chromeH = Math.Max(0, Size.Height - ClientSize.Height);
            MinimumSize = new Size(
                (int)Math.Ceiling(MinClientWidth * scale) + chromeW,
                (int)Math.Ceiling(MinClientHeight * scale) + chromeH);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Closing the tool is also a Disconnect, including the pre-session lobby/state-transfer
            // phase. Otherwise accepted sockets and the paused emulator outlive the disposed form.
            try { EndSession("tool closed"); } catch { try { TeardownNetwork(); } catch { } }
            try { _tips.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Seed the connection log here rather than in the constructor: RichTextBox coloring forces
            // handle creation, which we'd rather not trigger while the form is still being built.
            if (_connLog.TextLength == 0) ConnLog("Ready — pick Host or Join, then Start.", Color.DimGray);
            RefreshPlayerLimit(); // in case the tool opened with a core already loaded
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
                _autoDelayCheck.Checked = _settings.AutoDelay;
                _autoDelayMaxBox.Value = Clamp(_settings.AutoDelayMax,
                    (int)_autoDelayMaxBox.Minimum, (int)_autoDelayMaxBox.Maximum);
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
            _autoDelayCheck.CheckedChanged += (_, __) => { UpdateEnabled(); SaveSettingsFromUi(); };
            _autoDelayMaxBox.ValueChanged += (_, __) => SaveSettingsFromUi();
            _netcodeCombo.SelectedIndexChanged += (_, __) => SaveSettingsFromUi();
            _inputSourceCombo.SelectedIndexChanged += (_, __) => SaveSettingsFromUi();
        }

        /// <summary>
        /// Cap the Players box at the loaded core's controller-port count and show that ceiling next to
        /// it. A session can only fill ports the core actually exposes — Genesis is 2 until you enable
        /// the 4-Way Play / Team Player adapter, N64 is 4 natively — and picking 4 on a 2-port core used
        /// to be accepted, silently clamped, and only explained by a line in the Log tab.
        ///
        /// The clamp deliberately does NOT overwrite the remembered preference: someone who wants 4
        /// players and switches from Genesis to N64 gets their 4 back rather than being stuck at the
        /// lowest core they ever loaded. Called on every core/ROM change (<see cref="Restart"/>), which
        /// is also when enabling a multitap in the core's sync settings takes effect.
        /// </summary>
        private void RefreshPlayerLimit()
        {
            int max = 8;          // no core loaded yet: leave the box's own ceiling in place
            bool known = false;
            try
            {
                if (_emulator != null) { max = Math.Max(2, EmuHawkAdapter.PortCountOf(_emulator)); known = true; }
            }
            catch { /* odd core definition — fall back to the unrestricted box */ }

            _loadingSettings = true; // a programmatic clamp must not persist over the user's choice
            try
            {
                int want = _settings != null ? _settings.Players : (int)_playersBox.Value;
                _playersBox.Maximum = max;
                _playersBox.Value = Clamp(want, (int)_playersBox.Minimum, max);
                _playersHint.Text = known ? $"of {max}" : "";
            }
            finally { _loadingSettings = false; }
        }

        private void SaveSettingsFromUi()
        {
            if (_loadingSettings || _settings == null) return;
            _settings.Upnp = _upnpCheck.Checked;
            _settings.Port = (int)_portBox.Value;
            _settings.Players = (int)_playersBox.Value;
            _settings.Delay = (int)_delayBox.Value;
            _settings.AutoDelay = _autoDelayCheck.Checked;
            _settings.AutoDelayMax = (int)_autoDelayMaxBox.Value;
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
            _tips.SetToolTip(_ipBox,
                "The host's address: 1.2.3.4 or 1.2.3.4:47800.\r\n" +
                "A port typed here overrides the Port box.");
            var portLabel = new Label { Text = "Port:", AutoSize = true, Location = new Point(260, 46) };
            _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = DefaultPort, Location = new Point(300, 43), Width = 70 };
            var playersLabel = new Label { Text = "Players:", AutoSize = true, Location = new Point(388, 46) };
            _playersBox = new NumericUpDown { Minimum = 2, Maximum = 8, Value = 2, Location = new Point(444, 43), Width = 46 };
            // The ceiling is the core's controller-port count, so say what it is instead of letting
            // someone pick 4 and only find out at start time that the core exposes 2 (see RefreshPlayerLimit).
            _playersHint = new Label { Text = "", AutoSize = true, Location = new Point(494, 46), ForeColor = Color.DimGray };

            var passwordLabel = new Label { Text = "Password:", AutoSize = true, Location = new Point(12, 78) };
            _passwordBox = new TextBox { Location = new Point(80, 75), Width = 160, UseSystemPasswordChar = true };
            var passwordHint = new Label { Text = "(optional; must match on both ends)", AutoSize = true, Location = new Point(248, 78), ForeColor = Color.DimGray };

            var delayLabel = new Label { Text = "Input delay:", AutoSize = true, Location = new Point(12, 110) };
            // This is always honored as a manual floor. Auto may raise it before WELCOME, but never
            // changes the running timeline or lowers a value explicitly requested by either player.
            _delayBox = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 1, Location = new Point(90, 107), Width = 50 };
            _autoDelayCheck = new CheckBox
            {
                Text = "Auto from ping", AutoSize = true, Checked = true, Location = new Point(150, 109),
            };
            var autoDelayMaxLabel = new Label { Text = "Max:", AutoSize = true, Location = new Point(270, 110) };
            _autoDelayMaxBox = new NumericUpDown
            {
                Minimum = 1, Maximum = 20, Value = 8, Location = new Point(306, 107), Width = 45,
            };
            _tips.SetToolTip(_delayBox,
                "Fixed input delay, or the minimum when Auto is enabled.\r\n" +
                "Each frame reduces typical rollback correction but adds one frame of local response time.");
            _tips.SetToolTip(_autoDelayCheck,
                "Host only: measure every player's lobby ping and choose delay before play starts.\r\n" +
                "The chosen delay stays fixed for the entire session.");
            _tips.SetToolTip(_autoDelayMaxBox,
                "Largest delay Auto may choose. Explicit player delay requests are still honored.");

            var netcodeSelLabel = new Label { Text = "Netcode:", AutoSize = true, Location = new Point(366, 110) };
            _netcodeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(426, 107), Width = 120 };
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

            // Connection log: the did-I-get-in answer, on the tab you're already looking at. The Log tab
            // carries the full diagnostic firehose, which is the wrong place to learn that your password
            // was wrong — only connection-lifecycle events land here, color-coded (red = refused/failed,
            // green = connected). See ConnLog.
            var connLogLabel = new Label { Text = "Connection status:", AutoSize = true, Location = new Point(12, 348) };
            _connLog = new RichTextBox
            {
                Location = new Point(12, 366), Size = new Size(544, 92),
                ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Vertical, TabStop = false, DetectUrls = false,
            };

            _netcodeLabel = new Label
            {
                Text = "Netcode in use: —", Location = new Point(12, 466), Width = 300, Height = 24,
                BorderStyle = BorderStyle.FixedSingle, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0), ForeColor = Color.DimGray,
            };

            page.Controls.AddRange(new Control[]
            {
                _hostRadio, _joinRadio, ipLabel, _ipBox, portLabel, _portBox, playersLabel, _playersBox, _playersHint,
                passwordLabel, _passwordBox, passwordHint, delayLabel, _delayBox, _autoDelayCheck,
                autoDelayMaxLabel, _autoDelayMaxBox,
                netcodeSelLabel, _netcodeCombo, inputSrcLabel, _inputSourceCombo, _upnpCheck,
                _goButton, _disconnectButton, _pubAddrButton,
                connLogLabel, _connLog, _netcodeLabel, BuildPunchGroup(),
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
                Text = "UDP Punch — play without port-forwarding",
                Location = new Point(12, 200), Size = new Size(544, 140),
            };

            // The group shows only what YOUR role needs (see UpdatePunchUiForRole): a joiner
            // punches and sends the code; a host pastes codes into its waiting lobby.
            _punchInstructions = new Label
            {
                Text = "", AutoSize = true, Location = new Point(12, 22), ForeColor = Color.DimGray,
            };

            // Joiner row.
            _punchButton = new Button { Text = "UDP Punch", Location = new Point(12, 66), Width = 110 };
            _punchButton.Click += (_, __) => OnPunchStart();
            _myCodeLabel = new Label { Text = "Your code:", AutoSize = true, Location = new Point(136, 71) };
            _myCodeBox = new TextBox
            {
                ReadOnly = true, Location = new Point(206, 68), Width = 200,
                Font = new Font(FontFamily.GenericMonospace, 11f), Text = "",
            };
            _copyCodeButton = new Button { Text = "Copy", Location = new Point(414, 67), Width = 60, Enabled = false };
            _copyCodeButton.Click += (_, __) => CopyMyCode();

            // Host row (same vertical slot — only one row is ever visible).
            _peerCodeLabel = new Label { Text = "Joiner's code:", AutoSize = true, Location = new Point(12, 71) };
            _peerCodeBox = new TextBox { Location = new Point(102, 68), Width = 240 };
            _connectButton = new Button { Text = "Admit", Location = new Point(350, 67), Width = 80 };
            _connectButton.Click += (_, __) => OnPunchAdmit();

            _punchStatus = new Label
            {
                Text = "", AutoSize = true, Location = new Point(12, 104), ForeColor = Color.DimGray,
            };

            _punchGroup.Controls.AddRange(new Control[]
            {
                _punchInstructions, _punchButton, _myCodeLabel, _myCodeBox, _copyCodeButton,
                _peerCodeLabel, _peerCodeBox, _connectButton, _punchStatus,
            });
            UpdatePunchUiForRole();
            return _punchGroup;
        }

        /// <summary>Show only the punch controls the selected role uses — a joiner punches and
        /// sends a code; a host pastes codes into its waiting lobby.</summary>
        private void UpdatePunchUiForRole()
        {
            if (_punchGroup == null || _punchGroup.IsDisposed) return;
            bool host = _hostRadio.Checked;
            _punchInstructions.Text = host
                ? "A player who can't reach you: they pick Join, enter your IP, and click UDP Punch.\nWhile your lobby is waiting, paste the code they send you:"
                : "Can't reach the host? Enter the host's IP above as usual, then click UDP Punch.\nSend the code it shows to the host, and stay put — it connects when they paste it.";
            _punchButton.Visible = !host;
            _myCodeLabel.Visible = !host;
            _myCodeBox.Visible = !host;
            _copyCodeButton.Visible = !host;
            _peerCodeLabel.Visible = host;
            _peerCodeBox.Visible = host;
            _connectButton.Visible = host;
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

            page.Controls.AddRange(new Control[]
            {
                _probeButton, _testInputButton, _verboseCheck, _freezeInputCheck, _forceDesyncCheck,
                simLatencyLabel, _simLatencyBox, _simUnresponsiveCheck,
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
            var mesh = _mesh;
            if (mesh == null) return false;
            return (link.UdpEndpoint != null && mesh.IsEndpointAlive(link.UdpEndpoint))
                || (link.ReflexiveEndpoint != null && mesh.IsEndpointAlive(link.ReflexiveEndpoint));
        }

        /// <summary>Human-readable direct-link state for the Players list.</summary>
        private string MeshLinkStatus(PeerLink link)
        {
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
            // ROM load / tool re-init: also tear down a lobby, join, or state transfer. Those phases
            // have already paused the emulator and captured an adapter for the old core even though
            // _sessionActive is still false.
            EndSession("emulator restarted");
            // Invalidate the cached probe depth — the core/ROM may have changed, and a stale (deeper)
            // measurement from a lighter core could wrongly grant rollback to a heavier one.
            _probeDepth = -1;
            RefreshPlayerLimit(); // the new core may expose a different number of controller ports
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
                {
                    // Belt-and-braces: RefreshPlayerLimit normally keeps the box at or below this, so
                    // reaching here means the core changed under us. Never clamp silently — an
                    // unexplained "waiting for 1 player(s)" after asking for 4 is the confusing case.
                    ConnLog($"hosting {players} players, not {(int)_playersBox.Value} — this core exposes only " +
                            $"{portCount} controller port(s). Enable the core's multitap/adapter " +
                            "(Genesis: 4-Way Play or Team Player) for more.", Color.DarkOrange);
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
                APIs.EmuClient.Pause();

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
            if (_sessionActive || _transport != null) { Log("Already connecting — Disconnect first."); return; }
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

            WarnSessionHazards(); // non-blocking heads-up about movies/TAStudio/Lua

            try
            {
                _adapter = new EmuHawkAdapter(APIs, _emulator, _statable);
                _adapter.InputSourcePort = InputSourceFromCombo(); // read your normal pad, whatever port you're assigned
                if (!_adapter.VerifyDeterministicMode())
                    Log("WARNING: core does not report deterministic emulation — desyncs are likely.");
                if (!_adapter.HasBindings)
                    Log($"WARNING: input may not register — {_adapter.BindingDiagnostic}");

                _isHost = false;
                int players = _adapter.PortCount;
                if (players < 2)
                {
                    Log($"this core exposes only {players} controller port — need at least 2 for netplay.");
                    return;
                }

                APIs.EmuClient.Pause(); // freeze now so the resume frame is fixed before the state arrives

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
            mesh.SetPeerRoutes(new List<PeerRoute> { new PeerRoute(0, new[] { host }) });
            SetBusy(true);
            _punchButton.Enabled = false;
            _punchStatus.Text = "finding your public address…";
            _punchStatus.ForeColor = Color.DimGray;
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
                    try { control.ReadTimeout = HandshakeReceiveTimeoutMs; } catch { }
                    var sp = Handshake.RunClientMulti(channel, id, prefs, mesh.LocalPort, beforeReady: ready =>
                    {
                        InvokeUiBlocking(() =>
                        {
                            if (!IsConnectionAttemptCurrent(attempt)) throw new OperationCanceledException();
                            PrepareSessionJoiner(ready, peerLink);
                        });
                        initialStateApplied = true;
                    }, afterGreet: () =>
                    {
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
            var routes = new List<PeerRoute>();
            for (int i = 0; i < _lobbyPunchTargets.Count; i++)
                routes.Add(new PeerRoute(1 + i, new[] { _lobbyPunchTargets[i] })); // placeholder ports; real routes are set at GO
            try { mesh.SetPeerRoutes(routes); } catch { }
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
                _punchAdmissions.Enqueue(new PunchAdmission { Endpoint = joiner, Control = control });
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
            if (_listener == null || _sessionActive)
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
                int need = players - 1;
                UiConnLog($"hosting a {players}-player session on TCP+UDP {port} — you are P1, " +
                          $"waiting for {need} more to join…", Color.DarkSlateBlue);

                // Best-effort NAT reachability (UPnP forward + public-address report). Non-fatal.
                TryPublishHostAddress(port, attempt);

                var links = new List<PeerLink>();
                var greetings = new List<Handshake.JoinerGreeting>();
                while (links.Count < need)
                {
                    if (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_listener, hostListener)) return;

                    // A punched joiner admitted from the UI (a pasted connect code) enters this SAME
                    // lobby as a TCP accept would: same greet, same WELCOME/READY/GO — no TCP on its
                    // link. This is what makes punch admission N-player for free.
                    if (_punchAdmissions.TryDequeue(out var admission))
                    {
                        GreetPunchedJoiner(admission, id, prefs, udpLocalPort, links, greetings, need, attempt);
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
                    links.Add(new PeerLink
                    {
                        Tcp = tcp,
                        Control = channel,
                        RemotePort = assignedPort,
                        UdpEndpoint = new IPEndPoint(remoteIp, greet.UdpPort),
                        Label = $"P{assignedPort + 1} ({remoteIp})",
                    });
                    greetings.Add(greet);
                    UiConnLog($"P{assignedPort + 1} joined from {remoteIp} ({links.Count}/{need})", Color.DarkGreen);
                }
                try { hostListener.Stop(); } catch { }
                if (ReferenceEquals(_listener, hostListener)) _listener = null;
                // A code pasted just as the lobby filled has no seat — close its stream cleanly.
                while (_punchAdmissions.TryDequeue(out var leftover))
                {
                    _lifecycle.Untrack(leftover.Control);
                    try { leftover.Control.Dispose(); } catch { }
                    _mesh?.CloseControl(leftover.Endpoint);
                }
                if (!IsConnectionAttemptCurrent(attempt)) return;

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
                else if (_netcodeChoice == NetcodeChoice.Rollback && !_replayDeterministic)
                {
                    // The Rollback pick overrides the probe's PERFORMANCE recommendation. It does not
                    // override a core that provably cannot replay: that is not a matter of taste, and
                    // honouring it would guarantee the desyncs rollback exists to avoid.
                    mode = SyncMode.Lockstep;
                    UiLog("rollback was forced, but this core failed the probe's replay check — using " +
                          "lockstep, which never reloads state. Forcing it would desync on every correction.");
                }
                else if (_netcodeChoice == NetcodeChoice.Rollback)
                {
                    Handshake.JoinerGreeting? incapable = null;
                    foreach (var g in greetings)
                    {
                        if (!g.Prefs.WantRollback || g.Id.MaxRollbackDepth < ProbeResult.RollbackDepthThreshold)
                        { incapable = g; break; }
                    }
                    if (incapable != null)
                    {
                        mode = SyncMode.Lockstep;
                        UiLog("rollback was forced locally, but a joiner reported rollback unavailable; using lockstep");
                    }
                    else
                    {
                        mode = SyncMode.Rollback; // bypass only this host's local recommendation
                        UiLog("netcode forced to rollback — bypassing only the host's local probe recommendation");
                    }
                }
                else // Automatic
                {
                    bool allRollback = greetings.Count >= 1;
                    foreach (var g in greetings)
                        if (SessionNegotiator.Negotiate(id, g.Id, prefs, g.Prefs).Mode != SyncMode.Rollback)
                        { allRollback = false; break; }
                    mode = allRollback ? SyncMode.Rollback : SyncMode.Lockstep;
                }

                if (autoDelay)
                {
                    UiConnLog($"measuring lobby ping ({LobbyProbeSamples} samples per player)…",
                        Color.DarkSlateBlue);
                    double worstRttMs = -1;
                    double worstJitterMs = 0;
                    foreach (var link in links)
                    {
                        if (!IsConnectionAttemptCurrent(attempt)) return;
                        var sample = ProbeLobbyRtt(link);
                        if (sample.MedianMs > worstRttMs) worstRttMs = sample.MedianMs;
                        // Worst median and worst jitter are tracked independently: one session-wide
                        // delay has to cover every link on both counts, so the safe figure is the
                        // worst of each even when they come from different players.
                        if (sample.JitterMs > worstJitterMs) worstJitterMs = sample.JitterMs;
                    }
                    finalDelay = SelectLobbyDelay(finalDelay, autoDelayMax, mode, worstRttMs,
                        lobbyFrameMs, simulatedOneWayMs, players, worstJitterMs);
                }

                // Each joiner gets every OTHER joiner's UDP endpoint so it can build a direct mesh
                // (it reaches the host at the address it connected to, so the host is left off the list).
                var generation = new SessionGeneration(SessionAuth.NewSessionId(), 1);
                // Trust the negotiated endpoints before asking clients to prepare their drivers: their
                // pre-READY neutral windows can then queue instead of being rejected as foreign UDP.
                _mesh!.SetPeerRoutes(RoutesExcept(links, null));
                foreach (var link in links)
                {
                    ConfigureStateTransferTimeouts(link, state.Length);
                    Handshake.HostSendWelcome(link.Control, link.RemotePort, players, finalDelay, mode, state,
                        generation, RoutesExcept(links, link));
                }

                // Nobody is released while the host is still synchronously shipping a large state to
                // another joiner. Once every control link acknowledges that all start data arrived,
                // prepare the host's own generation-bound driver, then send GO to the whole group.
                foreach (var link in links) Handshake.HostWaitReady(link.Control, generation);
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

        /// <summary>Greet a punched joiner exactly like a TCP accept — over the reliable control
        /// stream on the mesh socket, bounded by its read timeout instead of a socket deadline. A
        /// refused greet (wrong password/ROM/core) costs only that joiner, same as the TCP policy.</summary>
        private void GreetPunchedJoiner(PunchAdmission admission, PeerIdentity id, SessionPreferences prefs,
            int udpLocalPort, List<PeerLink> links, List<Handshake.JoinerGreeting> greetings, int need, int attempt)
        {
            var channel = new ControlChannel(admission.Control);
            Handshake.JoinerGreeting greet;
            try
            {
                try { admission.Control.ReadTimeout = HandshakeReceiveTimeoutMs; } catch { }
                greet = Handshake.HostGreet(channel, id, prefs, udpLocalPort);
                try { admission.Control.ReadTimeout = Timeout.Infinite; } catch { }
            }
            catch (Exception ex)
            {
                _lifecycle.Untrack(admission.Control);
                try { admission.Control.Dispose(); } catch { }
                _mesh?.CloseControl(admission.Endpoint);
                if (!IsConnectionAttemptCurrent(attempt)) return;
                UiConnLog($"refused a punched join from {admission.Endpoint.Address}: {ex.Message} — " +
                          $"still hosting, waiting for {need - links.Count} player(s)", Color.Firebrick);
                return;
            }
            int assignedPort = links.Count + 1;
            links.Add(new PeerLink
            {
                Tcp = null!,
                ControlStream = admission.Control,
                Control = channel,
                RemotePort = assignedPort,
                UdpEndpoint = admission.Endpoint, // the punched path IS the peer's working endpoint
                Label = $"P{assignedPort + 1} ({admission.Endpoint.Address})",
            });
            greetings.Add(greet);
            UiConnLog($"P{assignedPort + 1} joined via UDP punch from {admission.Endpoint.Address} " +
                      $"({links.Count}/{need})", Color.DarkGreen);
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
                        sp = Handshake.RunClientMulti(channel, id, prefs, udpLocalPort, beforeReady: ready =>
                        {
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
                        });
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

        /// <summary>Construct and seed the exact generation-bound driver before READY. It may publish
        /// neutral input, but no frame clock or control reader is activated until GO.</summary>
        private void PrepareSessionDriver(SyncMode mode)
        {
            _mode = mode;
            if (mode == SyncMode.Rollback)
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
            try { _driver?.Dispose(); } catch { }
            _driver = CreateDriver();
            _startEmuFrame = APIs.Emulation.FrameCount();
            _driver.Start();
            _sessionDriverPrepared = true;
        }

        /// <summary>The role-independent post-GO activation: audio, control I/O, and frame pacing.</summary>
        private void BeginSessionCommon(SyncMode mode, string remoteLabel)
        {
            if (!DriverPreparedFor(CurrentGeneration, mode)) PrepareSessionDriver(mode);

            ApplyBackgroundConfig(true); // don't let EmuHawk pause/ignore input when unfocused
            try { APIs.EmuClient.EnableRewind(false); } catch { } // rewind would jump the frame count -> desync
            APIs.EmuClient.Pause(); // we own the clock now
            _startEmuFrame = APIs.Emulation.FrameCount(); // baseline for frame-advance drift checks
            _resyncCount = 0;
            _agreedSinceResync = false;
            _desyncsWithoutAgreement = 0;
            _resyncInProgress = false;
            _resyncReleaseQueued = false;
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
            _sessionActive = true;
            _preJoinRestoreState = null; // GO committed the imported baseline

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
            // Raise the OS timer resolution and measure what we actually got BEFORE the pacing clocks
            // start, so the probe's own cost isn't charged to frame zero as debt.
            try { if (!_timerResRaised) { timeBeginPeriod(1); _timerResRaised = true; } } catch { }
            LogTimerGranularity();
            _delayHintShown = false;
            lock (_pingLock) { foreach (var link in _peers) { link.PingMs = -1; link.PingCount = 0; } }
            _pingClock.Restart();
            _paceClock.Restart();
            _nextFrameDueMs = 0;
            _recentCoreFrameMs = 0;
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
            _stallHintSinceMs = double.NegativeInfinity;
            _stallHintShown = false;
            _hashDiagLogged = false;
            _lastTickClockMs = -1;
            // A WinForms timer is WM_TIMER, and SetTimer silently raises anything below
            // USER_TIMER_MINIMUM to 10ms — asking for 2 never bought a 2ms cadence, it just hid the
            // real floor. State it honestly: ~10ms is the fastest this mechanism goes, which is still
            // comfortably under a frame period so long as we don't serialize on top of it (see
            // FrameTick — the timer deliberately keeps running while a tick is in flight).
            _frameTimer.Interval = 10;
            _frameTimer.Start();

            Status($"in session — {(mode == SyncMode.Rollback ? "rollback" : "lockstep")}, " +
                   $"you are P{_localPort + 1}/{_playerCount}, delay {_sessionDelay}", Color.Green);
            _netcodeLabel.Text = "Netcode in use: " + (mode == SyncMode.Rollback ? "Rollback" : "Lockstep");
            _netcodeLabel.ForeColor = mode == SyncMode.Rollback ? Color.DarkGreen : Color.DarkSlateBlue;
            RefreshPlayersList();
            ConnLog($"session started vs {remoteLabel} — {(mode == SyncMode.Rollback ? "rollback" : "lockstep")}, " +
                    $"delay {_sessionDelay}", Color.DarkGreen);
            _disconnectButton.Enabled = true;

            // NAT traversal: a joiner discovers its public (reflexive) mesh endpoint and reports it to the
            // host, which shares it so peers can reach us across NAT. Additive to the LAN candidates, so
            // LAN/localhost play is unaffected whether or not this succeeds. The host is reached at the
            // address joiners connected to, so it doesn't report one.
            if (!_isHost) StartReflexiveDiscovery();
        }

        /// <summary>Joiner: off-thread, STUN-discover our mesh socket's public endpoint and send it to the host.</summary>
        private void StartReflexiveDiscovery()
        {
            var mesh = _mesh;
            if (mesh == null) return;
            int attempt = CurrentConnectionAttempt;
            new Thread(() =>
            {
                IPEndPoint? reflexive;
                try { reflexive = mesh.DiscoverReflexive(TimeSpan.FromSeconds(2.5)); }
                catch when (!IsConnectionAttemptCurrent(attempt)) { return; }
                catch (Exception ex)
                {
                    if (IsConnectionAttemptCurrent(attempt))
                        UiLog("(note) UDP address discovery failed: " + ex.Message);
                    return;
                }
                if (!IsConnectionAttemptCurrent(attempt) || !ReferenceEquals(_mesh, mesh)) return;
                if (reflexive == null)
                {
                    UiLog("(note) couldn't determine our public UDP endpoint (STUN blocked) — internet peers may be unreachable");
                    return;
                }
                UiLog($"our public UDP endpoint is {reflexive} — sharing it for NAT traversal");
                BeginInvokeUi(() =>
                {
                    if (IsConnectionAttemptCurrent(attempt) && ReferenceEquals(_mesh, mesh)
                        && _sessionActive && _peers.Count > 0)
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

        /// <summary>
        /// How long one frame-tick callback may spend before it must return to the message loop.
        ///
        /// This has to scale with the console's frame period, not sit at a fixed 8ms. The second-frame
        /// gate below requires <c>elapsed + 2·recentCoreFrameMs &lt; budget</c>, so a flat 8ms made
        /// catch-up unreachable for any core costing more than ~4ms a frame — on N64 (~10-16ms) the
        /// test could never pass. Lost wall-clock time was then never repaid: it accumulated until the
        /// rebase above discarded roughly three frames in one lump, which reads as "CPU-bound" in the
        /// status bar even when the core is comfortably inside budget.
        ///
        /// 1.7 frame periods (~28ms at 60Hz) lets exactly one catch-up frame through while staying
        /// close enough to a frame period that the window never feels unresponsive. The hard
        /// <see cref="MaxFramesPerTick"/> cap, the pessimistic <c>_recentCoreFrameMs</c> estimate and
        /// the mid-burst audio pump are what keep that safe; this only stops the budget from
        /// forbidding the burst outright.
        /// </summary>
        private double TickBudgetMs() => Math.Max(FrameTickWorkBudgetMs, 1.7 * _frameMs);

        /// <summary>
        /// How early the FIRST frame of a tick may run.
        ///
        /// Callbacks do not arrive on a clean 16.7ms cadence — measured gaps run 3ms to 35ms around a
        /// 16.7ms mean, because our WM_TIMER is only delivered when the host pumps its message queue.
        /// Against a strict due-time that pattern is worst-case: the tick that lands early runs no
        /// frame at all, so the one after it finds two due, runs both, and shows only the second. One
        /// picture is lost per pair, which is why presented frames sat near 50 while the core emulated
        /// a steady 60.
        ///
        /// Letting the first frame run up to half a period early lets an early tick take the frame it
        /// nearly earned, turning "none then two" into "one then one". Long-run rate is untouched —
        /// _nextFrameDueMs still advances by exactly one period per frame — so the emulation can never
        /// lead the wall clock by more than this tolerance. Frames two and later stay strict, so a
        /// catch-up burst still requires genuinely accumulated debt.
        /// </summary>
        private double EarlyFrameToleranceMs => _frameMs * 0.5;

        /// <summary>
        /// Report what the OS actually gives us for a short sleep, once per session.
        ///
        /// The frame tick rides WM_TIMER, whose delivery is bound to the system clock tick, and on
        /// Windows 11 <c>timeBeginPeriod</c> is per-process and may be ignored for a window that isn't
        /// in the foreground — which is exactly our case when the second instance has focus. Since a
        /// frame is presented at most once per tick, that granularity is a hard ceiling on presented
        /// fps, so it's worth measuring rather than assuming. Near 1ms means a finer frame clock is
        /// available; near 15ms means WM_TIMER can't do much better than one tick per frame.
        /// </summary>
        private void LogTimerGranularity()
        {
            try
            {
                const int probes = 5;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < probes; i++) Thread.Sleep(1);
                double perSleep = sw.Elapsed.TotalMilliseconds / probes;
                Log($"timer granularity: Sleep(1) averages {perSleep:F2}ms against a " +
                    $"{_frameMs:F2}ms frame period — the frame tick cannot beat this.");
            }
            catch { }
        }

        private void FrameTick()
        {
            if (!_sessionActive || _driver == null) return;
            if (_frameTickRunning) return;

            // Deliberately NOT stopping the timer here. Stopping on entry and restarting in the finally
            // made each period (interval + tick work + message-queue latency) instead of just the
            // interval, because Start() re-arms SetTimer from zero. With ~10ms of enforced interval and
            // a few ms of work that measured at ~26ms — about 38 callbacks a second. Since the frame is
            // presented once per callback, the picture was capped near 38fps while the core happily
            // emulated 60: the "60fps but choppy" report. Left free-running, WM_TIMER re-arms itself and
            // coalesces (never more than one queued), and _frameTickRunning below is what actually keeps
            // a nested message pump from reentering us.
            _frameTickRunning = true;
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
                    if (_resyncInProgress) _driver.ResendLocalInputIfDue();
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
                    // Every peer may rebuild at a different instant. Keep publishing this epoch's
                    // neutral/start window so an early sender is not lost by peers still rejecting
                    // new-generation UDP with their old driver.
                    _driver.ResendLocalInputIfDue();
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
                    _pacing.AddRebase();
                }

                bool steppedThisTick = false;
                bool stalledThisTick = false;
                bool timeSyncThisTick = false;
                int framesThisTick = 0;
                bool committedSecondFrame = false;
                while (framesThisTick < MaxFramesPerTick
                    && nowMs + (framesThisTick == 0 ? EarlyFrameToleranceMs : 0.25) >= _nextFrameDueMs)
                {
                    // Normally this loop runs once. A second frame compensates for an irregular ~25ms
                    // WinForms callback without reviving the old eight-frame catch-up bursts. Never start
                    // that second frame after the callback has already consumed its UI work budget.
                    if (framesThisTick > 0)
                    {
                        if (!committedSecondFrame && tickWatch.Elapsed.TotalMilliseconds >= TickBudgetMs()) break;
                        // A frame of core execution just happened, and packets that landed during it are
                        // already queued. Draining once per tick would judge this frame's readiness on
                        // network state captured before that work — turning an input that did arrive in
                        // time into a stall that costs the whole tick.
                        _driver.PumpNetwork();
                        packetsDrained += _driver.LastPacketsDrained;
                        // A catch-up burst is the longest this callback ever goes without returning to
                        // the message loop, so top the ring up mid-tick rather than only at tick start.
                        _adapter?.PumpAudio();
                    }

                    _driver.CaptureLocalInput(); // capture local pad (paused-safe, via IInputApi) + send
                    var phase = System.Diagnostics.Stopwatch.StartNew();
                    if (!_driver.CurrentFrameReady())
                    {
                        double stallGateMs = phase.Elapsed.TotalMilliseconds;
                        gateMs += stallGateMs;
                        _pacing.AddGate(stallGateMs);
                        stalledThisTick = true;
                        _driver.ResendLocalInputIfDue();
                        bool timeSync = _driver.Strategy is RollbackStrategy stalledRollback
                            && stalledRollback.LastStallWasTimeSync;
                        timeSyncThisTick = timeSync;
                        if (timeSync)
                        {
                            // Advantage debt is denominated in emulated frames, not 2ms timer callbacks.
                            _nextFrameDueMs += _frameMs;
                        }
                        if (Verbose && nowMs - _lastStallLogMs >= 1000)
                        {
                            _lastStallLogMs = nowMs;
                            Log(timeSync
                                ? $"time-sync yield at frame {_driver.CurrentFrame}"
                                : $"stalling at frame {_driver.CurrentFrame} — waiting for remote input");
                        }
                        break;
                    }
                    else
                    {
                        double readyGateMs = phase.Elapsed.TotalMilliseconds; // includes rollback repair
                        gateMs += readyGateMs;
                        _pacing.AddGate(readyGateMs);
                        phase.Restart();
                        // When wall-clock debt already makes a second frame due, the first picture is
                        // throwaway. Skip it only when frame two is input-safe and recent core cost says
                        // both frames fit the UI budget. If one frame unexpectedly spikes after that
                        // commitment, finish the visible second frame once; the conservative rolling
                        // estimate prevents that spike from causing repeated two-frame callbacks.
                        bool secondGateSafe = _driver.Strategy is LockstepStrategy
                            || (_driver.Strategy is RollbackStrategy secondRollback
                                && !secondRollback.HasPendingTimeSyncDebt);
                        bool anotherFrameDue = framesThisTick + 1 < MaxFramesPerTick
                            && nowMs + 0.25 >= _nextFrameDueMs + _frameMs
                            && _recentCoreFrameMs > 0
                            && tickWatch.Elapsed.TotalMilliseconds + 2.0 * _recentCoreFrameMs
                                < TickBudgetMs()
                            && secondGateSafe
                            && _driver.NextFrameFullyConfirmed;
                        if (anotherFrameDue) committedSecondFrame = true;
                        _adapter!.AdvanceFrame(_driver.CurrentInputs(), renderVideo: !anotherFrameDue);
                        double frameCoreMs = phase.Elapsed.TotalMilliseconds;
                        coreMs += frameCoreMs;
                        _pacing.AddFrame(frameCoreMs);
                        _recentCoreFrameMs = _recentCoreFrameMs <= 0
                            ? frameCoreMs
                            : Math.Max(frameCoreMs, _recentCoreFrameMs * 0.9);
                        _driver.CompleteFrame();
                        steppedThisTick = true;
                        framesThisTick++;
                        if (framesThisTick >= 2) committedSecondFrame = false;
                        _nextFrameDueMs += _frameMs;
                        MaybeSendChecksum();
                        _fpsCount++;
                    }
                }

                // Exactly one tick counted per callback, so the stall rate stays a share of ticks.
                // Ticks that returned early above (frozen for a rejoin, mid-resync) are deliberately
                // not counted: they aren't the frame loop, and folding them in would dilute the rate.
                _pacing.AddTick(stalledThisTick, timeSyncThisTick);
                if (_lastTickClockMs >= 0) _pacing.AddTickInterval(nowMs - _lastTickClockMs);
                _lastTickClockMs = nowMs;

                // We hold EmuHawk paused, so its own run loop never presents the frames we advance here —
                // a paused window just keeps showing whatever its swapchain last held, which is why the
                // host's picture froze while the core, audio and netplay all kept running. Present the
                // latest frame ourselves, once per tick (the video twin of PumpAudio above).
                if (steppedThisTick)
                {
                    var phase = System.Diagnostics.Stopwatch.StartNew();
                    _adapter!.PresentVideo();
                    renderMs = phase.Elapsed.TotalMilliseconds;
                    _pacing.AddPresent(renderMs);
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
                    && MonotonicElapsedSeconds(_lastResyncStamp) > ResyncRecoverySeconds)
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
                // No Start() here on purpose: the timer never stopped, and re-arming it would restore
                // the serialization described above. EndSession/OnConnectFailed stop it explicitly.
            }
        }

        private void UpdateSessionUi(double nowMs)
        {
            if (_driver == null || nowMs - _lastUiRefreshMs < 250) return;
            _lastUiRefreshMs = nowMs;

            double ping = WorstPingMs(out bool udpMeasured);
            double effRttMs = (ping < 0 ? 0 : ping) + 2.0 * _simLatencyMs;
            int advantage = ComputeFrameAdvantage(out bool haveAdvantage, out int revision, out bool freshAdvantage);
            _driver.Strategy.OnPacingReport(new PacingInfo(effRttMs, 0, advantage,
                haveAdvantage && freshAdvantage, revision));

            string pingStr = ping < 0 ? ""
                : $" — ping {effRttMs:F0}ms{(udpMeasured ? " udp" : "")}" +
                  $"{(_simLatencyMs > 0 ? $" (incl. {2 * _simLatencyMs}ms sim)" : "")}{(_peers.Count > 1 ? " (worst)" : "")}";
            string rbStr = _driver.Strategy is RollbackStrategy rbs
                ? $" — rollback ×{rbs.RollbackCount} (last d{rbs.LastRollbackDepth}, max d{rbs.MaxRollbackDepthSeen}, tsync {rbs.TimeSyncStalls})"
                : "";

            if (_fpsClock.ElapsedMilliseconds >= 500)
            {
                _actualFps = _fpsCount * 1000.0 / _fpsClock.ElapsedMilliseconds;
                _fpsCount = 0;
                // Summarize before resetting: this is the only place the pacing window rolls over,
                // so the log line below reads the same numbers the status bar just showed.
                _lastPacing = _pacing.Summarize(_fpsClock.Elapsed.TotalMilliseconds);
                _pacing.Reset();
                _fpsClock.Restart();
            }
            double targetFps = _frameMs > 0 ? 1000.0 / _frameMs : 60.0;
            bool cpuBound = _actualFps >= 0 && _actualFps < targetFps * 0.95;
            // Only worth the width when presentation actually fell behind the core — otherwise the two
            // numbers are the same and repeating it just crowds the bar.
            string presentStr = _lastPacing.PresentedFps < _lastPacing.AdvancedFps - 1
                ? $", present {_lastPacing.PresentedFps:F0}"
                : "";
            string speedStr = _actualFps < 0 ? ""
                : $" — {_actualFps:F0}/{targetFps:F0} fps ({_actualFps / targetFps * 100:F0}%{(cpuBound ? ", CPU-bound" : "")}{presentStr})";
            // The number that separates a slow core from a stalling link: high here means waiting on
            // the network (raise input delay), low here with fps under target means CPU or pacing.
            double stallPct = _lastPacing.StallTickPct;
            string stallStr = stallPct >= 5 ? $" — stall {stallPct:F0}%" : "";
            string udpStr = _udpWarningActive ? " — UDP recovering" : "";
            Status($"in session — frame {_driver.CurrentFrame}{speedStr}{pingStr}{rbStr}{stallStr}{udpStr}",
                _udpWarningActive || cpuBound || stallPct >= 25 ? Color.DarkOrange : Color.Green);
            MaybeHintStalling(nowMs);
            LogPacingSummary(nowMs);
            RefreshPlayersList();
        }

        /// <summary>
        /// Say something once when lockstep is actually stalling, regardless of what the ping says.
        /// <see cref="MaybeHintDelay"/> reasons from the worst measured round-trip, but what stalls a
        /// lockstep session is the <em>late</em> packet, not the typical one — a link with a fine
        /// median and a wide swing looks healthy by ping and still waits on remote input constantly.
        /// The measured stall rate catches that case directly.
        ///
        /// It deliberately does NOT claim the delay is the cause. In lockstep, stalling is also how a
        /// fast peer waits for a slow one, so a CPU-bound machine at the other end produces exactly
        /// the same reading — and raising delay would do nothing for it. The message names both.
        /// </summary>
        private void MaybeHintStalling(double nowMs)
        {
            if (_stallHintShown || _mode != SyncMode.Lockstep || _lastPacing.Ticks == 0) return;
            if (_lastPacing.StallTickPct <= StallHintPct)
            {
                _stallHintSinceMs = double.NegativeInfinity; // a single bad window isn't a problem
                return;
            }
            if (double.IsNegativeInfinity(_stallHintSinceMs)) { _stallHintSinceMs = nowMs; return; }
            if (nowMs - _stallHintSinceMs < StallHintSustainMs) return;

            _stallHintShown = true;
            ConnLog($"stalling {_lastPacing.StallTickPct:F0}% of the time waiting on remote input. " +
                $"Either input delay ({_sessionDelay}) isn't covering the link's worst moments — a ping " +
                "that looks fine on average still stalls if it swings — or the other machine can't hold " +
                "full speed and you're waiting for it. Check whether their fps reads CPU-bound: if it " +
                "does, only faster core settings help. If it doesn't, raise the host's Auto max or " +
                "manual floor and reconnect (the running delay stays fixed).",
                Color.DarkOrange);
        }

        /// <summary>
        /// The full pacing breakdown, once a second under Verbose. The status bar has room for two
        /// numbers; this has the rest — notably <c>rebases</c>, which counts how many times the pacing
        /// clock gave up on accumulated debt and discarded frames outright. That is the difference
        /// between a core that genuinely can't make budget (core mean at or above the frame period)
        /// and a schedule that threw away frames the core could have run.
        /// </summary>
        private void LogPacingSummary(double nowMs)
        {
            if (!Verbose || nowMs - _lastPacingLogMs < 1000) return;
            _lastPacingLogMs = nowMs;
            var p = _lastPacing;
            if (p.Ticks == 0) return;
            Log($"pacing: adv {p.AdvancedFps:F0} fps, present {p.PresentedFps:F0}, " +
                $"tick {p.TicksPerSecond:F0}/s (gap min {p.TickGapMinMs:F1} mean {p.TickGapMeanMs:F1} " +
                $"max {p.TickGapMaxMs:F1}ms), " +
                $"core mean {p.CoreMeanMs:F1} p95 {p.CoreP95Ms:F1} max {p.CoreMaxMs:F1}ms, " +
                $"gate mean {p.GateMeanMs:F1} p95 {p.GateP95Ms:F1}ms, " +
                $"present mean {p.PresentMeanMs:F1}ms, " +
                $"stall {p.StallTickPct:F0}% of {p.Ticks} ticks (tsync {p.TimeSyncTickPct:F0}%), " +
                $"rebases {p.Rebases}, budget {TickBudgetMs():F0}ms");
        }

        /// <summary>
        /// Worst round-trip across peers, preferring the mesh's own measurement over the control link's.
        /// Input rides UDP; once the mesh punches direct paths that isn't even the same route as TCP, and
        /// TCP's number is inflated by its queueing and retransmits. Since this figure both advises the
        /// player's input delay and sizes rollback's prediction horizon, measuring the wrong path costs
        /// real latency. Falls back to the TCP ping when no peer's ack carried a timestamp (older build).
        /// </summary>
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
            if (_driver == null || _awaitingReconnect || _resyncInProgress) return;
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
                hash = _adapter!.HashMainMemory(frame);
                _lastHashMs = hashWatch.Elapsed.TotalMilliseconds;
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
            // drop path (whose _sessionActive/_peers guards make it a no-op for a healthy link)
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
                ? " Host-to-player lobby links were measured; direct joiner-to-joiner paths can differ."
                : "";

            UiConnLog($"Auto delay: worst lobby RTT ~{effectiveRttMs:F0}ms{simulated}{jitter} → " +
                $"{choice.Frames} frame(s) for {(mode == SyncMode.Rollback ? "rollback" : "lockstep")}" +
                floor + capped + "." + meshNote,
                choice.WasCapped ? Color.DarkOrange : Color.DarkGreen);
            return choice.Frames;
        }

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
                    $"(this session is {_sessionDelay}). Raise the host's Auto max or manual floor, then reconnect; " +
                    "the running delay stays fixed.",
                    Color.DarkOrange);
            }
            else if (_sessionDelay - suggested >= 2)
            {
                // Only ever nagging upward left people permanently over-delayed: the box is sticky, so a
                // value picked for one bad link keeps costing latency on every good one afterwards.
                double excessMs = (_sessionDelay - suggested) * _frameMs;
                ConnLog($"worst link ping ~{effWorst:F0}ms{simNote}: this link only needs input delay {suggested}, and " +
                    $"the session is running {_sessionDelay} — about {excessMs:F0}ms of extra response time. " +
                    "Lower the host's floor/max for the next session if responsiveness matters more.",
                    Color.DarkOrange);
            }
            else
            {
                ConnLog($"worst link ping ~{effWorst:F0}ms{simNote}: input delay {_sessionDelay} is comfortable for " +
                    "this link.", Color.DimGray);
            }
        }

        private void StartPeerIo(PeerLink link)
        {
            link.Attempt = CurrentConnectionAttempt;
            link.LastRecvTicks = MonotonicNow();
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
                int attempt = link.Attempt;
                if (failure != null && _sessionActive && IsConnectionAttemptCurrent(attempt))
                    BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt))
                            OnPeerLinkLost(link, "control send failed: " + failure.Message);
                    });
            }
        }

        /// <summary>Reader loop for one control link. Dispatch depends on our role.</summary>
        private void PeerReaderLoop(PeerLink link)
        {
            try
            {
                while (_sessionActive && IsConnectionAttemptCurrent(link.Attempt))
                {
                    var (type, body) = link.Control.Receive();
                    Interlocked.Exchange(ref link.LastRecvTicks, MonotonicNow()); // liveness heartbeat
                    if (type == ControlMessageType.Checksum)
                    {
                        // Only the host aggregates; a joiner never receives checksums.
                        var generation = CurrentGeneration;
                        if (_isHost && ControlMessageCodec.TryDecodeChecksum(body, generation, out int frame, out uint hash))
                            RecordChecksum(link.Attempt, generation, link.RemotePort, frame, hash);
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
                            BeginInvokePeer(link, MaybeHintDelay);
                        }
                    }
                    else if (type == ControlMessageType.Pacing)
                    {
                        if (!ControlMessageCodec.TryDecodePacing(body, out var generation,
                            out int sequence, out int acknowledges, out int theirFrame, out int theirAdvantage))
                            continue;
                        if (sequence <= 0) continue;
                        lock (_generationLock)
                        {
                            var driver = _driver;
                            if (generation != _generation || driver == null
                                || driver.Generation != generation) continue;
                            int myFrame = driver.CurrentFrame;
                            lock (_pingLock)
                            {
                                if (sequence <= link.LastReceivedPacingSequence) continue;
                                link.LocalAdvantage = myFrame - theirFrame;
                                link.RemoteAdvantage = theirAdvantage;
                                link.LastReceivedPacingSequence = sequence;
                                // The peer's advantage is initialized only after it acknowledges one of
                                // our reports. This prevents both high-latency peers treating the other's
                                // startup zero as a real measurement and both deciding they are ahead.
                                link.AdvantageKnown = acknowledges > 0
                                    && acknowledges <= link.PacingSendSequence;
                                _frameAdvantage.Record(link.RemotePort, sequence,
                                    link.LocalAdvantage, link.RemoteAdvantage, link.AdvantageKnown);
                            }
                        }
                    }
                    else if (type == ControlMessageType.PeerList)
                    {
                        // Host reshuffled the mesh (e.g. someone rejoined) — update who we send to.
                        if (!_isHost)
                        {
                            var routes = HandshakeCodec.DecodeRoutes(body);
                            BeginInvokePeer(link, () =>
                            {
                                _meshOthers = routes;
                                ApplyJoinerMesh();
                                if (Verbose) Log($"mesh updated: {routes.Count} other peer(s)");
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
                                BeginInvokePeer(link, () => OnJoinerCandidate(link, eps[0]));
                        }
                    }
                    else if (type == ControlMessageType.ResyncBegin)
                    {
                        if (!_isHost && ControlMessageCodec.TryDecodeResyncBegin(body, out var generation, out int stateBytes,
                            out int waitSeconds)
                            && generation == CurrentGeneration.Next())
                        {
                            link.ReceivingResyncEpoch = generation.Epoch;
                            link.ReceivingResyncBytes = stateBytes;
                            Interlocked.Exchange(ref link.ResyncReceiveDeadlineTicks,
                                StateReceiveDeadlineTicks(stateBytes, waitSeconds));
                            link.ResyncReceiving = true; // publish only after the deadline fields are complete
                            BeginInvokePeer(link, () =>
                            {
                                if (!_sessionActive || generation != CurrentGeneration.Next()) return;
                                _resyncInProgress = true;
                                Status($"receiving authoritative resync epoch {generation.Epoch} state…",
                                    Color.DarkOrange);
                            });
                        }
                    }
                    else if (type == ControlMessageType.Resync)
                    {
                        if (!_isHost && link.ResyncReceiving)
                        {
                            int expectedEpoch = link.ReceivingResyncEpoch;
                            int expectedBytes = link.ReceivingResyncBytes;
                            link.ResyncReceiving = false;
                            link.ReceivingResyncEpoch = 0;
                            link.ReceivingResyncBytes = 0;
                            Interlocked.Exchange(ref link.ResyncReceiveDeadlineTicks, 0);
                            if (ControlMessageCodec.TryDecodeStatePayload(body, out var generation, out var state)
                                && generation.Epoch == expectedEpoch && generation == CurrentGeneration.Next()
                                && state.Length == expectedBytes)
                                BeginInvokePeer(link, () => ApplyResyncAsJoiner(generation, state));
                            else
                                BeginInvokePeer(link, () => EndSession("host sent an invalid or incomplete resync state"));
                        }
                    }
                    else if (type == ControlMessageType.ResyncApplied)
                    {
                        if (_isHost && ControlMessageCodec.TryDecodeGeneration(body, out var generation)
                            && generation == CurrentGeneration)
                            BeginInvokePeer(link, () => OnPeerResyncApplied(link, generation));
                    }
                    else if (type == ControlMessageType.ResyncResume)
                    {
                        if (!_isHost && ControlMessageCodec.TryDecodeGeneration(body, out var generation)
                            && generation == CurrentGeneration)
                            BeginInvokePeer(link, () => ResumeResyncAsJoiner(generation));
                    }
                    else if (type == ControlMessageType.Bye)
                    {
                        int attempt = link.Attempt;
                        BeginInvokeUi(() =>
                        {
                            if (IsConnectionAttemptCurrent(attempt) && _peers.Contains(link))
                                EndSession($"{link.Label} left the session");
                        });
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                int attempt = link.Attempt;
                if (_sessionActive && IsConnectionAttemptCurrent(attempt)) BeginInvokeUi(() =>
                {
                    if (IsConnectionAttemptCurrent(attempt)) OnPeerLinkLost(link, ex.Message);
                });
            }
        }

        /// <summary>
        /// Host desync detection: gather each peer's checksum for a frame (our own + every joiner's).
        /// Once all <see cref="_playerCount"/> are in, they must agree; if not, resync everyone. Called
        /// from the UI thread (our own hash) and from reader threads (joiners'), hence the lock.
        /// </summary>
        private void RecordChecksum(int attempt, SessionGeneration generation, int sourcePort, int frame, uint hash)
        {
            ChecksumOutcome outcome;
            lock (_hashLock)
            {
                if (!IsConnectionAttemptCurrent(attempt) || !_sessionActive || !_isHost
                    || generation != CurrentGeneration) return;
                outcome = _checksums.Record(generation, sourcePort, frame, hash, _playerCount);
            }
            if (outcome == ChecksumOutcome.Pending) return;
            if (outcome == ChecksumOutcome.Mismatch) BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt) && CurrentGeneration == generation)
                    OnHostDesync(frame);
            });
            else if (_resyncCount != 0)
                BeginInvokeUi(() =>
                {
                    if (IsConnectionAttemptCurrent(attempt) && CurrentGeneration == generation && _resyncCount != 0)
                    { _resyncCount = 0; Log("back in sync — recovery confirmed"); }
                });
            else if (Verbose)
                BeginInvokeUi(() =>
                {
                    if (IsConnectionAttemptCurrent(attempt) && CurrentGeneration == generation)
                        Log($"checksum frame {frame}: all {_playerCount} agree");
                        _agreedSinceResync = true;
                });
        }

        private void OnHostDesync(int frame)
        {
            if (_resyncInProgress) return;
            if (MonotonicElapsedSeconds(_lastResyncStamp) < ResyncGraceSeconds) return; // just resynced; give it time
            Log($"DESYNC at frame {frame} — peers disagree");
            // A divergence that recurs at EVERY interval, with no agreeing checksum in between, is not
            // the emulation drifting — a real drift would sync fine for a while first. It means the two
            // machines are comparing memory that was never going to match. On N64 the usual cause is
            // above-native video resolution: the plugin resolves its framebuffer back into RDRAM, and
            // those bytes come from the GPU rather than the emulated core, so they differ per machine
            // and land inside the region the checksum reads. Resyncing cannot fix that, and will keep
            // shipping a 16MiB state every interval until it gives up.
            if (!_agreedSinceResync) _desyncsWithoutAgreement++;
            _agreedSinceResync = false;
            if (_desyncsWithoutAgreement == 2)
                ConnLog("every checksum since this session began has disagreed, with none agreeing in " +
                    "between — that is a systematic mismatch, not emulation drift, and resyncing will " +
                    "not clear it. On N64 the usual cause is running the video plugin above native " +
                    "resolution: it resolves the framebuffer back into RDRAM, and those bytes come from " +
                    "your GPU rather than the emulated core, so they cannot match your opponent's. Drop " +
                    "to native resolution on BOTH machines.", Color.Firebrick);
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
            if (!_sessionActive || !_isHost) return;
            var gate = RecoveryPolicy.GateResync(_resyncInProgress,
                MonotonicElapsedSeconds(_lastResyncStamp), ResyncGraceSeconds, _resyncCount + 1, MaxResyncs);
            if (gate == ResyncGate.AlreadyInProgress || gate == ResyncGate.Debounced) return;
            _resyncCount++;
            int attempt = CurrentConnectionAttempt;

            if (gate == ResyncGate.GiveUp)
            {
                EndSession($"persistent desync — gave up after {MaxResyncs} resync attempts (likely a determinism bug)");
                return;
            }
            try
            {
                var state = _adapter!.ExportState();
                var generation = AdvanceGeneration();
                _resyncInProgress = true;
                _resyncReleaseQueued = false;
                RebuildDriver();
                int peerCount = _peers.Count;
                var generationBody = ControlMessageCodec.EncodeResyncBegin(generation, state.Length);
                var stateBody = ControlMessageCodec.EncodeStatePayload(generation, state);
                Status($"resync #{_resyncCount}: sending epoch {generation.Epoch} " +
                    $"({state.Length / 1024}KiB) to {peerCount} peer(s)…", Color.DarkOrange);
                Log($"resync #{_resyncCount}: captured {state.Length / 1024}KiB for epoch " +
                    $"{generation.Epoch}; waiting for every peer to import it");

                if (peerCount == 0)
                {
                    _resyncInProgress = false;
                    RebaseFrameSchedule();
                    return;
                }
                foreach (var link in _peers)
                {
                    GraceForStateTransfer(link, state.Length); // it can't pong while its reader consumes the frame
                    link.AwaitingAppliedEpoch = generation.Epoch;
                    Interlocked.Exchange(ref link.AppliedDeadlineTicks, StateApplyDeadlineTicks(state.Length));
                    if (!QueueControl(link, ControlMessageType.ResyncBegin, generationBody)
                        || !QueueControl(link, ControlMessageType.Resync, stateBody, ok =>
                        {
                            if (!ok) BeginInvokeUi(() =>
                            {
                                if (IsConnectionAttemptCurrent(attempt) && _sessionActive
                                    && CurrentGeneration == generation)
                                    EndSession("resync state transfer failed");
                            });
                        }))
                    {
                        EndSession("resync state transfer could not be queued");
                        return;
                    }
                }
            }
            catch (Exception ex) { EndSession("resync failed: " + ex.Message); }
        }

        /// <summary>Joiner: adopt the host's authoritative state and acknowledge only after the
        /// emulator import and generation-bound driver rebuild have both completed.</summary>
        private void ApplyResyncAsJoiner(SessionGeneration generation, byte[] state)
        {
            if (!_sessionActive || _isHost || generation != CurrentGeneration.Next()) return;
            int attempt = CurrentConnectionAttempt;
            if (++_resyncCount > MaxResyncs)
            {
                EndSession($"persistent desync — gave up after {MaxResyncs} resync attempts (likely a determinism bug)");
                return;
            }
            try
            {
                _resyncInProgress = true;
                Status($"applying {state.Length / 1024}KiB host resync epoch {generation.Epoch}…",
                    Color.DarkOrange);
                _adapter!.ImportState(state);
                SetGeneration(generation);
                RebuildDriver();
                if (_peers.Count == 0 || !QueueControl(_peers[0], ControlMessageType.ResyncApplied,
                    HandshakeCodec.EncodeGeneration(generation), ok =>
                    {
                        if (!ok) BeginInvokeUi(() =>
                        {
                            if (IsConnectionAttemptCurrent(attempt) && _sessionActive
                                && CurrentGeneration == generation)
                                EndSession("could not acknowledge applied resync state");
                        });
                    }))
                {
                    EndSession("could not queue the applied-state acknowledgement");
                    return;
                }
                Log($"resync #{_resyncCount}: imported epoch {generation.Epoch} " +
                    $"({state.Length / 1024}KiB); waiting for the host to release all peers");
            }
            catch (Exception ex) { EndSession("resync apply failed: " + ex.Message); }
        }

        private void OnPeerResyncApplied(PeerLink link, SessionGeneration generation)
        {
            if (!_sessionActive || !_isHost || generation != CurrentGeneration || !_peers.Contains(link)) return;
            if (link.AwaitingAppliedEpoch != generation.Epoch) return; // stale or duplicate acknowledgement

            link.AwaitingAppliedEpoch = 0;
            Interlocked.Exchange(ref link.AppliedDeadlineTicks, 0);
            Interlocked.Exchange(ref link.TimeoutGraceUntilTicks, 0);
            if (Verbose) Log($"{link.Label} applied resync epoch {generation.Epoch}");

            foreach (var peer in _peers)
                if (peer.AwaitingAppliedEpoch == generation.Epoch) return;

            if (_pendingReconnectLink != null && _pendingReconnectGeneration == generation)
                ReleaseReconnectedPeer(_pendingReconnectLink, _pendingReconnectStateLength, generation);
            else
                ReleaseResyncAsHost(generation);
        }

        private void ReleaseResyncAsHost(SessionGeneration generation)
        {
            if (_resyncReleaseQueued || generation != CurrentGeneration) return;
            _resyncReleaseQueued = true;
            int attempt = CurrentConnectionAttempt;
            QueueResyncResumeToPeers(generation, ok => BeginInvokeUi(() =>
            {
                if (!IsConnectionAttemptCurrent(attempt) || !_sessionActive
                    || CurrentGeneration != generation) return;
                if (!ok) { EndSession("resync resume transfer failed"); return; }
                _driver?.ResetRemoteInputLiveness();
                _resyncInProgress = false;
                RebaseFrameSchedule();
                Log($"resync #{_resyncCount}: every peer applied epoch {generation.Epoch}; resuming");
            }));
        }

        private void ResumeResyncAsJoiner(SessionGeneration generation)
        {
            if (!_sessionActive || _isHost || generation != CurrentGeneration || !_resyncInProgress) return;
            _driver?.ResetRemoteInputLiveness();
            _resyncInProgress = false;
            RebaseFrameSchedule();
            Log($"resync #{_resyncCount}: every peer applied epoch {generation.Epoch}; resuming");
        }

        private void QueueResyncResumeToPeers(SessionGeneration generation, Action<bool> completed)
        {
            var peers = new List<PeerLink>(_peers);
            if (peers.Count == 0) { completed(true); return; }
            int remaining = peers.Count;
            int failed = 0;
            var body = HandshakeCodec.EncodeGeneration(generation);
            foreach (var peer in peers)
            {
                QueueControl(peer, ControlMessageType.ResyncResume, body, ok =>
                {
                    if (!ok) Interlocked.Exchange(ref failed, 1);
                    if (Interlocked.Decrement(ref remaining) == 0)
                        completed(failed == 0);
                });
            }
        }

        private void BeginInvokePeer(PeerLink link, Action action)
        {
            int attempt = link.Attempt;
            BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt) && _peers.Contains(link)) action();
            });
        }

        /// <summary>
        /// Rebuild the frame driver from the current core state as a fresh frame-0 baseline: new
        /// pipeline, cleared checksums, reset pacing and drift baseline. In-flight pre-resync UDP
        /// datagrams carry the prior generation and are rejected before their frame data is decoded.
        /// </summary>
        private void RebuildDriver()
        {
            try { _driver?.Dispose(); } catch { } // release the old rollback ring before replacing it
            _driver = CreateDriver();
            _startEmuFrame = APIs.Emulation.FrameCount();
            lock (_hashLock) { _checksums.Clear(); }
            _driver.Start();
            _lastResyncStamp = MonotonicNow();
            RebaseFrameSchedule();
        }

        /// <summary>Discard wall-clock debt after a protocol pause without changing the monotonic
        /// clock used by UI, logging, and UDP-recovery timestamps.</summary>
        private void RebaseFrameSchedule()
        {
            if (!_paceClock.IsRunning) _paceClock.Start();
            _nextFrameDueMs = _paceClock.Elapsed.TotalMilliseconds;
            // The pause froze stepping but not the FPS sample clock — restart the sample so the
            // first post-resume status line doesn't read ~0 fps and flash "CPU-bound" (KI-7).
            _fpsClock.Restart();
            _fpsCount = 0;
            _actualFps = -1;
            // Same reason for the pacing window: ticks that elapsed while frozen aren't frame ticks,
            // and counting them would show a stall rate the running session never had.
            _pacing.Reset();
            _lastPacing = default;
            _stallHintSinceMs = double.NegativeInfinity;
        }

        private SessionGeneration CurrentGeneration
        {
            get { lock (_generationLock) return _generation; }
        }

        private void SetGeneration(SessionGeneration generation)
        {
            if (!generation.IsValid) throw new ArgumentOutOfRangeException(nameof(generation));
            lock (_generationLock)
            {
                _generation = generation;
                lock (_pingLock)
                {
                    _frameAdvantage.Reset();
                    foreach (var link in _peers)
                    {
                        link.LocalAdvantage = 0;
                        link.RemoteAdvantage = 0;
                        link.AdvantageKnown = false;
                        link.PacingSendSequence = 0;
                        link.LastReceivedPacingSequence = 0;
                        link.AwaitingAppliedEpoch = 0;
                        Interlocked.Exchange(ref link.AppliedDeadlineTicks, 0);
                    }
                }
            }
        }

        private SessionGeneration AdvanceGeneration()
        {
            var next = CurrentGeneration.Next();
            SetGeneration(next);
            return next;
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
                    p => new RollbackStrategy(p, _adapter!, _localPort, _rollbackDepth, FrameMs(),
                        RollbackTuningForSession()),
                    _localPort, _sessionDelay, redundancy: 8, rollbackWindow: _rollbackDepth,
                    portCount: _playerCount, generation: CurrentGeneration);

            return new FrameDriver(_adapter!, _transport!, p => new LockstepStrategy(p),
                _localPort, _sessionDelay, redundancy: 8, portCount: _playerCount,
                generation: CurrentGeneration);
        }

        /// <summary>
        /// How this peer spends its savestate budget. Purely local — see <see cref="RollbackTuning"/>
        /// for why none of it is negotiated. The anchor interval MUST track
        /// <see cref="ChecksumInterval"/>, since eliding snapshots would otherwise take the checksum's
        /// own state with them and stop desync detection without saying so.
        /// </summary>
        private RollbackTuning RollbackTuningForSession() => new RollbackTuning
        {
            ElideConfirmedSaves = true,
            ChecksumAnchorInterval = ChecksumInterval,
            RepairBudgetMs = RepairBudgetFrames * FrameMs(),
            Clock = new StopwatchClock(),
        };

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

            // A punched link has no TCP to re-accept a rejoin on — the reconnect wait can't help it,
            // so recovery is a fresh punch, not a 60s hold.
            if (link.Tcp == null)
            {
                EndSession($"lost connection to {link.Label}: {why} (punched link — no TCP rejoin path)");
                return;
            }

            // What losing a peer means in each recovery phase is decided in Core: RecoveryPolicy.
            switch (RecoveryPolicy.OnPeerLost(_isHost, _resyncInProgress, _awaitingReconnect))
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

            _awaitingReconnect = true;
            _reconnectPort = link.RemotePort;
            _reconnectStartedStamp = MonotonicNow();

            _peers.Remove(link);
            // The link leaves _peers here, so TeardownNetwork's reaping will never see it again —
            // shut its writer down now or the thread spins on OutboundSignal forever (KI-4).
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
                _resyncInProgress = true;
                _resyncReleaseQueued = false;
                RebuildDriver();
                RedistributeMesh(); // remove the dead endpoint from host and survivor route tables

                var begin = ControlMessageCodec.EncodeResyncBegin(generation, state.Length, (int)ReconnectTimeoutSeconds);
                foreach (var survivor in _peers)
                {
                    if (!QueueControl(survivor, ControlMessageType.ResyncBegin, begin))
                    {
                        EndSession("could not freeze survivors for reconnect");
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

        /// <summary>Host: point our mesh at every currently-connected joiner's candidate endpoints.</summary>
        private void UpdateMeshPeers()
        {
            if (_mesh == null) return;
            try { _mesh.SetPeerRoutes(RoutesExcept(_peers, null)); } catch { }
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
                new PeerRoute(_peers[0].RemotePort, new[] { _peers[0].UdpEndpoint }) // the host
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
                while (_sessionActive && _awaitingReconnect && IsConnectionAttemptCurrent(attempt))
                {
                    if (MonotonicElapsedSeconds(_reconnectStartedStamp) > ReconnectTimeoutSeconds)
                    {
                        BeginInvokeUi(() =>
                        {
                            if (IsConnectionAttemptCurrent(attempt) && _awaitingReconnect)
                                EndSession("no rejoin within the timeout");
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
                                CompleteReconnect(tcp, channel, remoteIp, udpEp, freedPort, attempt);
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
                    if (IsConnectionAttemptCurrent(attempt) && _awaitingReconnect)
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
            IPEndPoint udpEp, int freedPort, int attempt)
        {
            if (!IsConnectionAttemptCurrent(attempt) || !_sessionActive || !_awaitingReconnect)
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
                            generation, meshPeers);
                        Handshake.HostWaitReady(channel, generation);
                        try { tcp.ReceiveTimeout = 0; tcp.SendTimeout = 0; } catch { }
                        BeginInvokeUi(() =>
                        {
                            if (IsConnectionAttemptCurrent(attempt))
                                FinishReconnect(tcp, channel, remoteIp, udpEp, freedPort, state,
                                    generation, attempt);
                            else { UntrackHandshakeClient(tcp); try { tcp.Close(); } catch { } }
                        });
                    }
                    catch (Exception ex)
                    {
                        try { tcp.Close(); } catch { }
                        UntrackHandshakeClient(tcp);
                        BeginInvokeUi(() =>
                        {
                            if (IsConnectionAttemptCurrent(attempt) && _sessionActive)
                                EndSession("reconnect state transfer failed: " + ex.Message);
                        });
                    }
                }) { IsBackground = true, Name = "BizHawkNetplay-reconnect-state" }.Start();
            }
            catch (Exception ex) { EndSession("reconnect failed: " + ex.Message); }
        }

        private void FinishReconnect(TcpClient tcp, ControlChannel channel, IPAddress remoteIp,
            IPEndPoint udpEp, int freedPort, byte[] state, SessionGeneration generation, int attempt)
        {
            if (!IsConnectionAttemptCurrent(attempt) || !_sessionActive || !_awaitingReconnect)
            { UntrackHandshakeClient(tcp); try { tcp.Close(); } catch { } return; }
            if (generation != CurrentGeneration)
            { UntrackHandshakeClient(tcp); try { tcp.Close(); } catch { } return; }
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
                                if (IsConnectionAttemptCurrent(attempt) && _sessionActive
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
            if (_resyncReleaseQueued || generation != CurrentGeneration) return;
            _resyncReleaseQueued = true;
            int attempt = CurrentConnectionAttempt;

            // Survivors leave their resync wait only after all of them — and the rejoiner waiting in
            // READY/GO — have applied this generation. Flush their RESUME markers first, then release
            // the rejoiner's handshake off-thread before its live reader starts consuming the channel.
            QueueResyncResumeToPeers(generation, resumesOk => BeginInvokeUi(() =>
            {
                if (!IsConnectionAttemptCurrent(attempt) || !_sessionActive || !_awaitingReconnect)
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
                            if (!IsConnectionAttemptCurrent(attempt) || !_sessionActive
                                || !_awaitingReconnect || generation != CurrentGeneration)
                            { UntrackHandshakeClient(link.Tcp); try { link.Tcp?.Close(); } catch { } return; }
                            _peers.Add(link);
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
                            _awaitingReconnect = false;
                            _resyncInProgress = false;
                            _reconnectPort = -1;
                            _resyncCount = 0;
                            RebaseFrameSchedule();
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
                            if (IsConnectionAttemptCurrent(attempt) && _sessionActive)
                                EndSession("reconnect GO failed: " + ex.Message);
                        });
                    }
                }) { IsBackground = true, Name = "BizHawkNetplay-reconnect-go" }.Start();
            }));
        }

        private void FailSession(string reason)
        {
            bool wasActive = _sessionActive;
            _pendingJoinIp = null; // a failed connect shouldn't land in the recent-IPs list
            ConnLog("connection failed: " + reason, Color.Firebrick);
            _frameTimer.Stop();
            _sessionActive = false;
            _resyncInProgress = false;
            _resyncReleaseQueued = false;
            try { if (_timerResRaised) { timeEndPeriod(1); _timerResRaised = false; } } catch { }
            TeardownNetwork();
            if (!wasActive) RestorePreJoinState();
            try { _adapter?.DisableAudio(); } catch { } // restore EmuHawk's normal audio wiring
            ApplyBackgroundConfig(false);
            try { APIs.EmuClient.Unpause(); } catch { } // undo the freeze from OnGo
            ResetPunchUi();
            SetBusy(false);
            Status("Idle.", Color.DimGray);
        }

        private void EndSession(string reason)
        {
            if (!_sessionActive && _listener == null && _joiningTcp == null && _greetingTcp == null
                && _peers.Count == 0
                && !HasHandshakeClients() && _transport == null && _preJoinRestoreState == null)
            { SetBusy(false); return; }
            bool wasActive = _sessionActive;
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
            _resyncReleaseQueued = false;
            _simUnresponsive = false; _simUnresponsiveCheck.Checked = false; // clear the diagnostic
            try { if (_timerResRaised) { timeEndPeriod(1); _timerResRaised = false; } } catch { }

            TeardownNetwork();
            if (!wasActive) RestorePreJoinState();

            try { _adapter?.DisableAudio(); } catch { } // restore EmuHawk's normal audio wiring
            ApplyBackgroundConfig(false); // restore the user's focus/pause preferences
            try { APIs.EmuClient.Unpause(); } catch { }
            lock (_hashLock) { _checksums.Clear(); }

            _netcodeLabel.Text = "Netcode in use: —";
            _netcodeLabel.ForeColor = Color.DimGray;
            RefreshPlayersList(); // session inactive now → clears the list
            ResetPunchUi();

            ConnLog("session ended: " + reason, Color.DimGray);
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
            _awaitingReconnect = false;
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
            foreach (var link in peers)
            {
                link.WriterRunning = false;
                try { link.OutboundSignal.Set(); } catch { }
            }
            foreach (var link in peers) { try { link.Tcp?.Close(); } catch { } }

            try { (_transport as IDisposable)?.Dispose(); } catch { }
            try { _driver?.Dispose(); } catch { } // release the rollback ring's savestates
            _transport = null; _mesh = null;
            _lobbyPunchTargets.Clear();
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
                // MemorySaveState is nullable on the container but guaranteed by the _statable gate above.
                restore = APIs.MemorySaveState!.SaveCoreStateToMemory();
                double budget = FrameMs();
                Log(RunCapabilityProbe(adapter).ToString());
            }
            catch (Exception ex) { Log("probe failed: " + ex.Message); }
            finally
            {
                if (restore != null)
                {
                    try { APIs.MemorySaveState!.LoadCoreStateFromMemory(restore); APIs.MemorySaveState!.DeleteState(restore); }
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

            // Determinism: a core reporting non-deterministic usually means "determinism was not
            // requested" rather than "this will diverge" — N64 with no movie loaded says it and syncs
            // fine in practice. That report is therefore not treated as a refusal; the session runs and
            // the periodic checksum catches a genuine divergence, which is the check that actually
            // proves anything. Formerly a Diagnostics opt-in that defaulted to on, so this is the same
            // behaviour with one less box to tick.
            const bool deterministic = true;
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
        /// <summary>
        /// How many samples the probe should take. This runs synchronously on the UI thread, so the
        /// count is the freeze length: 60 samples is nothing on a light core and most of a second on
        /// N64, where one save alone is 6ms. One timed save tells the two apart, and a median of 12 is
        /// ample for sizing a prediction horizon. Nothing used to reach here on a heavy core, because
        /// the old N64 override short-circuited the probe before it ran.
        /// </summary>
        private static int ProbeSamplesFor(EmuHawkAdapter a)
        {
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var handle = a.SaveStateToMemory();
                double saveMs = timer.Elapsed.TotalMilliseconds;
                a.ReleaseState(handle);
                // 24 rather than 12 on a heavy core: its frame cost swings enough between runs to move
                // the verdict (measured 1.86ms to 3.58ms across consecutive N64 probes, either side of
                // the ~3.44ms boundary), and a median over 12 noisy samples inherits that. Doubling it
                // costs a couple of hundred milliseconds at session start against a 16MiB state export.
                return saveMs > 1.0 ? 24 : 60;
            }
            catch { return 12; }
        }

        /// <summary>
        /// Run the probe against the model the SESSION actually uses.
        ///
        /// Both the Diagnostics button and the pre-session measurement come through here, because they
        /// drifted apart once and it mattered: the button still asked for the original cost model, so
        /// it reported a heavy core as steady 11.5ms / maxDepth 0 while a session — eliding snapshots
        /// on confirmed frames and allowing a repair two frame periods — computed steady 5.5ms and a
        /// workable depth for the very same machine. A diagnostic that answers a question nothing asks
        /// is worse than no diagnostic.
        /// </summary>
        private ProbeResult RunCapabilityProbe(EmuHawkAdapter a)
        {
            double budget = FrameMs();
            return new CapabilityProbe(a, new StopwatchClock(), samples: ProbeSamplesFor(a))
                .Run(budget, budget * 0.25,
                    elideConfirmedSaves: true, repairBudgetMs: RepairBudgetFrames * budget);
        }

        private int MeasureRollbackDepth(EmuHawkAdapter a)
        {
            if (_probeDepth >= 0) return _probeDepth;
            string? restore = null;
            try
            {
                // MemorySaveState is nullable on the container but this only runs for statable cores.
                restore = APIs.MemorySaveState!.SaveCoreStateToMemory();
                double budget = FrameMs();
                // Probe the model the session will actually run: snapshots elided on confirmed frames,
                // and a repair allowed more than one frame period. Measuring the old model and then
                // running a different one is how you get a depth that has nothing to do with the cost.
                var result = RunCapabilityProbe(a);
                _replayDeterministic = result.ReplayDeterministic;
                // A core that does not reproduce from a savestate has no usable depth at all: the work
                // the depth budgets FOR is replaying. Reporting 0 keeps every peer's negotiation honest
                // without needing a second field on the wire.
                _probeDepth = result.ReplayDeterministic ? result.MaxRollbackDepth : 0;
                Log($"rollback probe — {result}");
                if (!result.ReplayDeterministic)
                    ConnLog("this core did not reproduce the same memory when the probe replayed the " +
                        "same inputs from the same savestate. Rollback repair is exactly that operation, " +
                        "so it would desync whenever the link made it predict — and stay perfectly in " +
                        "sync on a connection fast enough that it never had to, which is why this only " +
                        "shows up against a distant opponent. Using lockstep, which never reloads state.",
                        Color.Firebrick);
            }
            catch (Exception ex) { _probeDepth = 0; Log("rollback probe failed, will use lockstep: " + ex.Message); }
            finally
            {
                if (restore != null)
                {
                    try { APIs.MemorySaveState!.LoadCoreStateFromMemory(restore); APIs.MemorySaveState!.DeleteState(restore); }
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

        private void StartThread(Action body) =>
            new Thread(() => body()) { IsBackground = true, Name = "BizHawkNetplay-connect" }.Start();

        private int BeginConnectionAttempt() => _lifecycle.Begin();
        private int CurrentConnectionAttempt => _lifecycle.Current;
        private bool IsConnectionAttemptCurrent(int attempt) => _lifecycle.IsCurrent(attempt);
        private void InvalidateConnectionAttempt() => _lifecycle.Invalidate();
        private void AllowHandshakeClients() => _lifecycle.AcceptNew();
        private bool TrackHandshakeClient(TcpClient? tcp, int attempt) => _lifecycle.Track(tcp, attempt);
        private void UntrackHandshakeClient(TcpClient? tcp) => _lifecycle.Untrack(tcp);
        private bool HasHandshakeClients() => _lifecycle.HasTracked;

        private static long MonotonicNow() => System.Diagnostics.Stopwatch.GetTimestamp();

        private static long MonotonicTicks(double seconds) =>
            (long)Math.Ceiling(Math.Max(0, seconds) * System.Diagnostics.Stopwatch.Frequency);

        private static long MonotonicDeadline(double seconds) =>
            MonotonicNow() + MonotonicTicks(seconds);

        private static double MonotonicElapsedSeconds(long startedAt) => startedAt == 0
            ? double.PositiveInfinity
            : (MonotonicNow() - startedAt) / (double)System.Diagnostics.Stopwatch.Frequency;

        private static int StateTransferTimeoutMs(int stateBytes) =>
            StateTransferBudget.SocketTimeoutMs(stateBytes, HandshakeReceiveTimeoutMs);

        private static void ConfigureStateTransferTimeouts(TcpClient? tcp, int stateBytes)
        {
            if (tcp == null) return;
            int timeout = StateTransferTimeoutMs(stateBytes);
            try { tcp.ReceiveTimeout = timeout; tcp.SendTimeout = timeout; } catch { }
        }

        private static T WithAbsoluteSocketDeadline<T>(TcpClient tcp, int timeoutMs, Func<T> action)
        {
            using (var deadline = new AbsoluteSocketDeadline(tcp, timeoutMs))
            {
                try
                {
                    T result = action();
                    if (!deadline.TryComplete())
                        throw new TimeoutException("peer authentication deadline expired");
                    return result;
                }
                catch (Exception ex) when (deadline.Expired && !(ex is TimeoutException))
                {
                    throw new TimeoutException("peer authentication deadline expired", ex);
                }
            }
        }

        private void UpdateEnabled()
        {
            bool host = _hostRadio.Checked;
            _ipBox.Enabled = !host;
            _playersBox.Enabled = host; // only the host chooses the player count
            _autoDelayCheck.Enabled = host;
            _autoDelayMaxBox.Enabled = host && _autoDelayCheck.Checked;
            // The host settles netcode and delay for the whole session, and UPnP forwards the host's
            // own port. Greyed out rather than hidden so a joiner can still read what they mean —
            // and see the value they'll be joining under is not theirs to set. LocalPreferences is
            // what makes that true rather than merely implied.
            _netcodeCombo.Enabled = host;
            _delayBox.Enabled = host;
            _upnpCheck.Enabled = host;
            _goButton.Text = host ? "Start Hosting" : "Join";
            UpdatePunchUiForRole();
        }

        /// <summary>
        /// What this peer asks the session for.
        ///
        /// A host asks for what its own controls say. A joiner asks for nothing it could impose:
        /// rollback is opted into unconditionally so a stale local dropdown cannot veto the host's
        /// choice, and the delay ask is the floor so a stale local number cannot raise the session's
        /// — the negotiator honours the LARGEST ask, so anything else would let a disabled control go
        /// on quietly deciding things.
        ///
        /// What a joiner still decides is nothing to do with preference: the rollback depth it
        /// advertises is measured on its own machine, and the host refuses rollback outright if any
        /// joiner's is too shallow. Capability is not up for negotiation; taste is the host's.
        /// </summary>
        private SessionPreferences LocalPreferences(bool isHost) =>
            isHost
                ? new SessionPreferences((int)_delayBox.Value,
                    _netcodeChoice != NetcodeChoice.Lockstep, _passwordBox.Text)
                : new SessionPreferences(1, true, _passwordBox.Text);

        private void SetBusy(bool busy)
        {
            _goButton.Enabled = !busy;
            _hostRadio.Enabled = _joinRadio.Enabled = !busy;
            _ipBox.Enabled = !busy && _joinRadio.Checked;
            _playersBox.Enabled = !busy && _hostRadio.Checked;
            _portBox.Enabled = _delayBox.Enabled = !busy;
            _autoDelayCheck.Enabled = !busy && _hostRadio.Checked;
            _autoDelayMaxBox.Enabled = !busy && _hostRadio.Checked && _autoDelayCheck.Checked;
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

        /// <summary>
        /// Append to the Log tab, keeping only the most recent <see cref="LogMaxLines"/> lines.
        ///
        /// The cap is not cosmetic. This is a diagnostic firehose — verbose mode logs checksums, audio
        /// stats and stall notices for as long as you play — and the backing Win32 EDIT control gets
        /// slower to append to as its buffer grows, so an unbounded log quietly taxes the UI thread that
        /// also owns the frame clock. Trimming rewrites the whole control, so it's amortized: it happens
        /// once every (LogMaxLines - LogKeepLines) appends, not on every line.
        /// </summary>
        private void Log(string message)
        {
            if (_log.IsDisposed) return;
            _log.AppendText(message + Environment.NewLine);

            _logLines += 1 + CountNewlines(message); // a single message can carry several lines (e.g. AudioStats)
            if (_logLines <= LogMaxLines) return;

            int cut = IndexAfterNewline(_log.Text, _logLines - LogKeepLines);
            if (cut <= 0) { _logLines = LogKeepLines; return; }
            _log.Text = _log.Text.Substring(cut);
            _logLines = LogKeepLines;
            _log.SelectionStart = _log.TextLength; // setting Text resets the caret; stay pinned to the newest line
            _log.ScrollToCaret();
        }

        private static int CountNewlines(string s)
        {
            int n = 0;
            foreach (char c in s) if (c == '\n') n++;
            return n;
        }

        /// <summary>Index just past the <paramref name="count"/>-th newline, or -1 if there aren't that many.</summary>
        private static int IndexAfterNewline(string s, int count)
        {
            if (count <= 0) return 0;
            int seen = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\n') continue;
                if (++seen == count) return i + 1;
            }
            return -1;
        }

        private void UiLog(string message) => BeginInvokeUi(() => Log(message));

        /// <summary>
        /// Report a connection-lifecycle event — hosting/joining/refused/connected/dropped/ended. It goes
        /// to the Connection tab's box, where someone who just failed to join can actually see it, and to
        /// the full log. Colors carry the verdict: red refused/failed, green connected, orange interrupted.
        /// Per-frame and diagnostic chatter stays on <see cref="Log"/> so this box stays readable.
        /// </summary>
        private void ConnLog(string message, Color color)
        {
            Log(message);
            if (_connLog.IsDisposed) return;

            // Bound the history — a long session can rack up drops, rejoins and resyncs, and an unbounded
            // RichTextBox is a slow leak. Delete the oldest lines by selection so the kept tail retains
            // its coloring (re-appending them as text would flatten it).
            if (_connLog.Lines.Length > ConnLogMaxLines)
            {
                int cut = _connLog.GetFirstCharIndexFromLine(_connLog.Lines.Length - ConnLogKeepLines);
                if (cut > 0) { _connLog.Select(0, cut); _connLog.SelectedText = string.Empty; }
            }

            _connLog.SelectionStart = _connLog.TextLength;
            _connLog.SelectionLength = 0;
            _connLog.SelectionColor = color;
            _connLog.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
            _connLog.SelectionColor = _connLog.ForeColor;
            _connLog.ScrollToCaret(); // newest line stays visible in the small box
        }

        /// <summary>Thread-safe <see cref="ConnLog"/> for the accept/join/reconnect background threads.</summary>
        private void UiConnLog(string message, Color color) => BeginInvokeUi(() => ConnLog(message, color));

        private void InvokeUiBlocking(Action action)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(NetplayToolForm));
            if (!InvokeRequired) { action(); return; }
            Invoke(action);
        }

        private void BeginInvokeUi(Action action)
        {
            if (IsDisposed) return;
            try { BeginInvoke(action); } catch { /* form closing */ }
        }
    }
}
