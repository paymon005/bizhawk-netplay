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
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;

namespace BizHawkNetplay.Tool;

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
public sealed partial class NetplayToolForm : ToolFormBase, IExternalToolForm
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
    // 12: the lobby now measures every UDP mesh edge and the host publishes the settled delay in its
    // own control frame (MeshRtt / InputDelay), which an older build neither sends nor expects.
    private const int Protocol = 13;  // v13: resync/reconnect states are deflated on the wire
    private const int DefaultPort = 47800;
    private const int ChecksumInterval = 300; // full-memory hashes are intentionally infrequent (~5s at 60fps)

    /// <summary>
    /// How many frame periods one rollback repair may spend. Requiring it to fit inside a single
    /// period is stricter than the frame tick now needs — the catch-up path absorbs a short overrun
    /// — and on a heavy core that strictness is the difference between a usable prediction horizon
    /// and none. Two periods buys N64 depth 3 where one period allows 1. Repaired frames emit no
    /// audio, but they never did: the sample for a frame is produced by its original (predicted)
    /// run, so a deeper repair costs wall clock, not sound.
    ///
    /// Tied to <see cref="MaxFramesPerTick"/> rather than chosen alongside it, because "the
    /// catch-up path absorbs it" is only true up to that many frames. A repair spending N frame
    /// periods leaves N frames due when it returns, and a tick may run at most
    /// <see cref="MaxFramesPerTick"/> of them — so at equality the next tick clears the debt
    /// exactly, and above it the arrears grow until the pacing rebase discards them, which reads
    /// as "CPU-bound" in the status bar for a core comfortably inside its budget.
    ///
    /// Both were 2, which made the invariant hold by coincidence. It is worth noting what this is
    /// NOT tied to: <see cref="TickBudgetMs"/> is smaller (~28ms against ~33ms here) but governs a
    /// different decision — whether to START another frame in this tick — and a repair already
    /// running is not gated by it. Clamping this to that would cost N64 rollback entirely, for a
    /// conflict that does not exist.
    /// </summary>
    private const double RepairBudgetFrames = MaxFramesPerTick;

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
    private NumericUpDown _delayBox = null!;
    private CheckBox _autoDelayCheck = null!;
    private NumericUpDown _autoDelayMaxBox = null!;
    private Button _goButton = null!;
    private Button _disconnectButton = null!;
    private Button _probeButton = null!;
    private Button _testInputButton = null!;
    private Button _pubAddrButton = null!;
    private ComboBox _netcodeCombo = null!;
    private ComboBox _inputSourceCombo = null!;
    private Label _netcodeLabel = null!;
    private Button _applyLiveButton = null!;
    private RichTextBox _connLog = null!;
    private CheckBox _simUnresponsiveCheck = null!;
    private CheckBox _upnpCheck = null!;
    private TextBox _passwordBox = null!;
    private NumericUpDown _simLatencyBox = null!;
    private Button _punchButton = null!;
    private GroupBox _punchGroup = null!;
    private TextBox _myCodeBox = null!;
    private Button _copyCodeButton = null!;
    private TextBox _peerCodeBox = null!;
    private Button _connectButton = null!;
    private Label _punchStatus = null!;
    private bool _loadingSettings;                  // suppress change-handler saves while applying loaded prefs
    private string? _pendingJoinIp;                 // regular-join IP awaiting a successful connect, then recorded

    private int _simLatencyMs; // diagnostic: artificial one-way UDP delay for this session (0 = off)
    private bool _upnpEnabled;  // host: whether to attempt the UPnP auto-forward (captured from the checkbox)
    private UpnpMapping? _upnpMapping; // host: the router forward we added, removed on session end

    private bool Verbose => _verboseCheck.Checked;

    private int _startEmuFrame; // emulator FrameCount at session start, for drift detection
    private TextBox _log = null!;


    // --- Session state (all touched on the UI thread except where noted) ---
    private EmuHawkAdapter? _adapter;
    private ITransport? _transport;        // the FrameDriver's input channel (see below)
    private MeshUdpTransport? _mesh;       // direct peer-to-peer UDP: host and joiners both send to all peers
    // Our own public UDP endpoint, discovered once per session and reused. Volatile: written by the
    // STUN thread, read by the join thread building its HELLO and by the post-GO share.
    private volatile IPEndPoint? _localReflexive;
    private readonly ManualResetEventSlim _reflexiveKnown = new ManualResetEventSlim(false);
    private List<PeerRoute> _meshOthers = new List<PeerRoute>(); // joiner: grouped routes to non-host peers

    // UDP-punch path (2-player, no port-forwarding): one socket does STUN + hole-punch, then carries
    // both the reliable control channel and the input hot path. Set up in two steps (generate our
    // connect code, then punch to the pasted peer code) before the normal session bring-up runs.

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
    // Odd count, and enough of them that the high-water figure means something: the delay estimate
    // now needs the link's swing as well as its median (see LobbyDelayPolicy).
    private const int LobbyProbeSamples = 9;
    private const int LobbyProbeTimeoutMs = 5000;
    // How long every peer bursts probes across its own UDP edges before GO. Long enough for both
    // ends of a joiner-to-joiner path to punch through NAT and then leave a usable sample set,
    // short enough to vanish into a lobby that already took seconds to ship a savestate.
    private const int MeshProbeWindowMs = 1500;
    // How long a joiner will hold its HELLO waiting for its own public endpoint. STUN normally
    // answers in well under a second; a blocked server must cost this and not the session.
    private const int ReflexiveWaitMs = 3000;
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
    // Session lifecycle as one object; see SessionPhase for why rebuilding and awaiting-a-rejoin
    // are independent rather than two values of one enum.
    private readonly SessionPhase _phase = new SessionPhase();
    private bool _isHost;      // host is authoritative for desync detection + resync
    private int _playerCount = 2;
    private int _localPort;    // our controller port, for rebuilding the driver on resync
    private int _resyncCount;   // resyncs since the last confirmed re-sync (bounds infinite loops)
    // Tells "the emulation drifted once" apart from "these two machines were never comparing the
    // same thing": a real drift agrees for a while first, a systematic mismatch never agrees at all.
    private readonly DesyncTrend _desyncTrend = new DesyncTrend();
    private string? _videoDiagnostic; // resolution/plugin line, quoted back if desyncs turn systematic
    private long _lastResyncStamp; // monotonic timestamp; debounces near-simultaneous resync triggers
    private bool _forceDesyncOnce; // diagnostic: corrupt the next checksum to exercise resync
    private const int MaxResyncs = 6;
    private const double ResyncGraceSeconds = 2.0;
    private const double ResyncRecoverySeconds = 8.0; // joiner clears its resync counter after this long without another
    // Delay is selected before WELCOME and then remains fixed. In rollback it trades local response
    // time for shallower visual corrections; in lockstep it also prevents routine network stalls.
    private bool _audioStatsLogged; // one-shot audio pipeline diagnostic per session
    private double _lastStallLogMs = double.NegativeInfinity;
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

    private readonly System.Diagnostics.Stopwatch _paceClock = new System.Diagnostics.Stopwatch();
    private double _frameMs = 1000.0 / 60.0; // console frame period, drives real-time pacing
    private const int MaxFramesPerTick = 2;  // WinForms callbacks can arrive ~25ms apart; one frame caps near 40fps
    private const double FrameTickWorkBudgetMs = 8.0; // floor for fast cores; see TickBudgetMs
    // The pacing clock's arithmetic: due time, catch-up admission, budget, rebase. See FrameSchedule.
    private readonly FrameSchedule _schedule =
        new FrameSchedule(1000.0 / 60.0, FrameTickWorkBudgetMs, MaxFramesPerTick);
    // Last seen state of EmuHawk's sound device, so the tick can report the transition rather
    // than the aftermath. Starts true: a session that never stops it should say nothing.
    private bool _audioDevWasUp = true;
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
    // One-shot session advisories: say it when the problem is real, say it once, and not for a
    // single bad second. The policy is SustainedTrigger; these only hold the thresholds.
    private readonly SustainedTrigger _stallHint = new SustainedTrigger(StallHintSustainMs);
    private readonly SustainedTrigger _presentHint = new SustainedTrigger(StallHintSustainMs);
    // One-shot "rollback costs more than it can afford here" advisory; fires as soon as the
    // measured cost latches, since it is a property of the core and CPU, not a passing condition.
    private readonly SustainedTrigger _rollbackCostHint = new SustainedTrigger(0);
    private bool _hashDiagLogged; // one-time "which checksum path ran" line per session
    private double _lastTickClockMs = -1; // pace-clock stamp of the previous tick, for gap stats
    private double _lastPresentClockMs = -1; // ...and of the previous present, for judder stats
    private const double StallHintPct = 15.0;      // stalled share of ticks worth complaining about
    private const double StallHintSustainMs = 5000; // ...but only once it persists, not on one burst
    // Presented-vs-advanced share below which the picture is coarse enough to be worth naming. A
    // healthy heavy-core session measured 0.87-0.97 and the max-resolution one 0.53-0.63, so this
    // sits in the gap rather than at either edge of it.
    private const double PresentShareHintFloor = 0.70;

    // Raise the OS timer resolution to 1ms for the session so the WinForms frame timer fires
    // regularly (it's otherwise bound to the ~15ms system tick and jitters), which keeps audio
    // pumps steady and frame pacing smooth. Balanced by timeEndPeriod on session end.
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uMilliseconds);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uMilliseconds);
    private bool _timerResRaised;

    /// <summary>
    /// How close to the frame boundary the fine clock stops waiting and runs the tick. Half a
    /// millisecond is about a sixth of an unthrottled loop iteration, so it costs nothing to ask
    /// for and keeps a frame from being deferred a whole iteration by rounding.
    /// </summary>
    private const double FineClockWakeMarginMs = 0.5;

    /// <summary>
    /// Floor on how often the fine clock may enter the tick. Normally irrelevant — the tick advances
    /// the due time a whole period per frame, so it naturally runs about sixty times a second. It
    /// matters when the tick runs no frame at all (lockstep waiting on remote input): at 3200 loop
    /// iterations a second, without a floor a stall would become a spin through the whole tick body.
    /// </summary>
    private const double FineClockMinSpacingMs = 1.0;

    /// <summary>
    /// How long the fine clock may go quiet before <see cref="_frameTimer"/> resumes driving frames.
    /// Two frame periods: long enough that it never fires during normal play, short enough that a
    /// session which loses its idle time skips rather than stops.
    /// </summary>
    private const double FineClockFallbackMs = 34;

    private double _lastFineTickMs = double.NegativeInfinity;



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
        // Fallback only. While UpdateValues is running frames this does nothing, so the tick count
        // stays one-per-frame and `stall%` keeps meaning what it did. If EmuHawk stops calling in —
        // a modal dialog, a core swap, anything that takes over its loop — this resumes within a
        // frame or two rather than leaving the session with no clock at all.
        _frameTimer.Tick += (_, __) =>
        {
            if (_paceClock.Elapsed.TotalMilliseconds - _lastFineTickMs < FineClockFallbackMs) return;
            _timerTicksWindow++;
            FrameTick();
        };

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

}
