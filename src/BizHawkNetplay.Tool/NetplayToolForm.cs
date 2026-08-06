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
using BizHawkNetplay.Core.Diag;
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
// Grey the menu entry out until a ROM is loaded: every entry point here needs a core
// (PortCountOf, ControllerDefinition, IStatable), and against the NullEmulator they are all
// meaningless. Menu-only — a ROM closed under an OPEN tool still goes through Restart().
[ExternalToolApplicability.AnyRomLoaded]
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
    // 13: resync/reconnect states are deflated on the wire.
    // 14: WELCOME carries per-seat mesh tokens, and peers announce themselves with them over UDP —
    // an older build sends no token, so its packets stay unroutable to anyone whose NAT rewrote
    // the source port, which is silent one-way input loss rather than a refusal. Hence the bump.
    // 15: the desync checksum reads memory differently on some cores. Waterbox domains (Snes9x,
    // Ares64, melonDS, the Nyma cores) moved from a 1/16 stride sample to the whole domain via
    // BulkPeekByte, and byte-array domains (the Hawk cores) hash their backing array directly —
    // both produce a different value than v14 computed for the same state, so a mixed pair would
    // report a phantom desync every interval. Same rule as v10: the hash is wire contract.
    // 16: the host's opening HELLO is a challenge (protocol version + nonce) and its identity
    // follows only once the joiner's password proof has verified. A v15 peer sends its whole
    // identity up front and expects the same back, so the two disagree about the message sequence
    // rather than about a value — which is exactly what the version check exists to catch first.
    // 17: the joiner's opening HELLO is the mirror — an intro (version, nonce, UDP port,
    // reflexive), with its identity following only once the HOST's proof has verified. The v16
    // asymmetry meant a joiner tricked into dialing a stranger still handed over its ROM hash and
    // sync fields (a filesystem path, hence a Windows username on N64) before anything was proved.
    // 18: three wire contracts moved at once. The mesh report names its silent edges (count byte +
    // port list after the fixed prefix), so the relay can carry exactly the broken pairs; port 0's
    // input payload carries the console controls (Reset/Select/Pause/FDS) appended after the host
    // pad's own; and the strided checksum's sampling offset is bit-mixed, so a v17 peer hashes a
    // different slice of the same RAM. Any one of the three would desync or misparse a mixed pair.
    // 19: the desync checksum changed which bytes it reads, twice over. A delegate-wrapped domain
    // whose peek closes over a pointer — N64's RDRAM — is now memcpied and hashed whole instead of
    // sampled one word at a time, so a v18 peer hashes a quarter of the RAM this one hashes all of;
    // and the span the video hardware is scanning out is skipped on every path, which is what lets
    // N64 run above native resolution without disagreeing at every checksum. Either alone would
    // make a mixed pair report a desync that is not there.
    // 20: a session outlives its players, a dead leg gets relayed live, and every post-auth
    // control frame is authenticated. A graceful leave or an expired rejoin wait vacates the seat
    // instead of ending the session (SeatVacated, type 23, travels ahead of the rebuild; WELCOME
    // grows a `vacated=` line for rejoiners; the checksum quorum shrinks to the living). A
    // joiner-to-joiner leg that dies mid-game is reported (InputOutage, type 24) and the host
    // carries the pair instead of the watchdog ending the session. And the PBKDF2 key the password
    // proofs already derived is kept: every frame after AUTH carries a truncated HMAC bound to its
    // direction and position, so an on-path party without the password can no longer inject,
    // replay, reorder or tamper — the KI-13 network fix. A v19 peer sends none of it and would
    // fail every integrity check, which is precisely what the version refusal is for.
    // 21: the checksum's exclusions are measured instead of guessed. During the first boundaries
    // of every generation peers exchange per-bucket hashes (DivergenceReport, type 25) — possible
    // because a rebuild makes everyone byte-identical, so a disagreeing bucket can only be
    // machine-produced bytes — and the host publishes the union as an exclusion mask
    // (ExclusionMask, type 26) that every checksum from a stated frame on must skip. The hash's
    // seed also changed shape (it folds a range LIST and the mask identity, replacing v20's single
    // span), so a v20 peer computes different values for identical states: a mixed pair would
    // report a desync that is not there, which is what the version refusal pre-empts.
    // 22: input datagrams name and prove their author. The host mints one key per unordered pair of
    // seats and hands each peer only the pairs it belongs to (pk= lines in WELCOME), so the seat
    // byte a peer writes is no longer a claim any member could make about any other — see
    // MeshPairKeyring. The UDP envelope grew an author byte and an 8-byte tag, and the host re-tags
    // what it relays, so a v21 peer's datagrams are unreadable to a v22 peer and vice versa. That is
    // a silent total input loss rather than a desync, so the version refusal matters more here than
    // usual.
    // 23: identity grew a per-disc list (disc= lines — a multi-disc set was identified from disc one
    // alone), and recovery grew a way to defer to the majority. StateRequest (27) and StateOffer
    // (28) let an outvoted host adopt the majority's state instead of overwriting three correct
    // machines with its own; a v22 peer has no handler for either, so an outvoted v23 host would
    // wait out its donor timeout and fall back — degraded rather than broken, but the disc lines a
    // v22 peer never sends would let a genuinely different disc 2 through, which is the reason to
    // refuse rather than tolerate the mix.
    //
    // 24: two values that cross the wire changed, neither of them a message. The desync checksum's
    // fold runs in eight independent lanes now, which is 7.7x on the framework the tool ships on
    // and a different number for the same memory — a v23 peer would report a desync that is not
    // there, at every interval, with nothing in the message shape to notice it. And the password
    // KDF moved off SHA-1 to SHA-256, changing the derived key both sides prove against, so a
    // mixed pair simply cannot authenticate. Both were owed a bump and neither justified one
    // alone; spending a single break on the pair is the whole reason they shipped together.
    private const int Protocol = 24;
    private const int DefaultPort = 47800;

    /// <summary>
    /// The session's checksum cadence. Once a hard-coded 300 (~5s at 60fps), sized for the era
    /// when a full-memory hash was a 7-38ms hitch; the fast hash paths made that five seconds of
    /// deliberate detection latency for a cost that no longer exists. The host measures one hash
    /// at session start and lets <see cref="ChecksumCadence"/> choose, then publishes the figure
    /// in WELCOME — it is a session AGREEMENT, since peers quantize checksums to interval
    /// boundaries and mismatched intervals would never complete a comparison. Written on the UI
    /// thread before the lobby thread starts, and by the joiner when WELCOME lands.
    /// </summary>
    private int _checksumInterval = ChecksumCadence.DefaultIntervalFrames;

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
    // Height follows the Connection tab's content, which now ends at y=534 (the connection log,
    // pushed down by the lobby status box above it). Same reasoning as the width: a minimum that
    // does not cover the content it is meant to protect just lets the window clip it.
    private const int DesignClientWidth = 600;
    private const int DesignClientHeight = 660;
    private const int MinClientWidth = 580;
    private const int MinClientHeight = 600;

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
    // The lobby status box: state on top (flashes whenever the game is not advancing), netcode and
    // delay beneath. See UpdateLobbyStatus.
    private Panel _lobbyPanel = null!;
    private Label _lobbyStateLabel = null!;
    private System.Windows.Forms.Timer _lobbyTimer = null!;
    private bool _lobbyFlashOn;                       // which half of the flash cycle we are in
    // Set by the lobby/join paths, which know things no session state records — that we are dialling
    // out, or how many seats are still empty. Empty once a session is running or nothing is going on.
    private string _lobbyPhaseText = "";
    private Color _lobbyPhaseColor = Color.DimGray;
    private Button _applyLiveButton = null!;
    private RichTextBox _connLog = null!;
    private Label _logFileLabel = null!;
    private CheckBox _simUnresponsiveCheck = null!;
    private Button _analogWatchButton = null!;
    private System.Windows.Forms.Timer? _analogWatchTimer;
    private const int AnalogWatchIntervalMs = 50;
    private const int AnalogWatchSamples = 100;   // 5 seconds
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
    // Both volatile: the punch and reconnect threads test `ReferenceEquals(_mesh, mesh)` as their
    // this-session-is-still-mine guard, and the UI thread is what nulls them at teardown.
    private volatile ITransport? _transport;        // the FrameDriver's input channel (see below)
    private volatile MeshUdpTransport? _mesh;       // direct peer-to-peer UDP: host and joiners both send to all peers
    // Our own public UDP endpoint, discovered once per session and reused. Volatile: written by the
    // STUN thread, read by the join thread building its HELLO and by the post-GO share.
    private volatile IPEndPoint? _localReflexive;
    private readonly ManualResetEventSlim _reflexiveKnown = new(false);
    private List<PeerRoute> _meshOthers = new(); // joiner: grouped routes to non-host peers

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
    private readonly ConcurrentQueue<PunchAdmission> _punchAdmissions = new();
    // The lobby is the queue's only consumer, and it drains leftovers exactly once at GO. A punch
    // that confirms after that drain would otherwise enqueue an admission nobody ever dequeues —
    // its stream held open all session, its peer dying on a misleading read timeout. Written by
    // the lobby thread (open at lobby start, closed at GO) and teardown; read by punch workers.
    private volatile bool _punchDoorOpen;
    private readonly List<IPEndPoint> _lobbyPunchTargets = new();
    // volatile: the accept thread reads this as its teardown signal (null => Disconnect stopped us),
    // and it's written from the UI thread. Every other field read off the UI thread is volatile too;
    // the ones guarded by a lock instead (_peers' PingMs under _pingLock, _checksums under
    // _hashLock) say so where they are declared.
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
    private readonly List<PeerLink> _peers = new();
    // Links that left _peers before teardown (a peer dropped and we are waiting for it to return).
    // TeardownNetwork only ever reaped _peers, so these kept their reader/writer threads unjoined
    // and their OutboundSignal undisposed — one handle per drop/rejoin cycle.
    private readonly List<PeerLink> _retiredLinks = new();
    private readonly System.Windows.Forms.Timer _frameTimer;
    // Session lifecycle as one object; see SessionPhase for why rebuilding and awaiting-a-rejoin
    // are independent rather than two values of one enum.
    private readonly SessionPhase _phase = new();
    // Volatile: both are written on the UI thread when a session is prepared and read by the
    // control-reader threads on every message (PeerReaderLoop branches on _isHost).
    private volatile bool _isHost;      // host is authoritative for desync detection + resync
    private volatile int _playerCount = 2;
    private int _localPort;    // our controller port, for rebuilding the driver on resync
    // How many recoveries this session has left, and every way that allowance comes back. The
    // counter used to be an int here, spent under two different rules and cleared under four.
    private readonly ResyncBudget _resyncBudget = new();
    // Who still owes an "I applied that epoch" acknowledgement. Replaces a per-peer field written
    // in three loops and read back by a fourth that walked every link looking for one still set.
    private readonly ApplyBarrier _applyBarrier = new();
    // How often one address may make this host check a password. Verifying a proof is a PBKDF2
    // derivation — about a second on this build — and the accept loop is serial, so a stranger
    // who cannot pass can otherwise hold the door shut against players who can.
    private readonly PasswordAttemptLimiter _joinAttempts = new();
    // Tells "the emulation drifted once" apart from "these two machines were never comparing the
    // same thing": a real drift agrees for a while first, a systematic mismatch never agrees at all.
    private readonly DesyncTrend _desyncTrend = new();
    private string? _videoDiagnostic; // resolution/plugin line, quoted back if desyncs turn systematic
    private long _lastResyncStamp; // monotonic timestamp; debounces near-simultaneous resync triggers
    private bool _forceDesyncOnce; // diagnostic: corrupt the next checksum to exercise resync
    private const double ResyncGraceSeconds = 2.0;
    // The size of the last state this machine exported, which is the only estimate it has of what a
    // donor is about to send: same core, same game, same frame. Refreshed by every export, so the
    // donor wait is sized from a real figure rather than a guess. 0 until the first export, which
    // OnePhaseSeconds reads as "just the fixed grace".
    private int _ownStateBytes;

    /// <summary>Export a state and remember how big it was. Every export in the form goes through
    /// here, so the size is never stale for want of one call site remembering.</summary>
    private byte[] ExportOwnState()
    {
        var state = _adapter!.ExportState();
        _ownStateBytes = state.Length;
        return state;
    }
    // Delay is selected before WELCOME and then remains fixed. In rollback it trades local response
    // time for shallower visual corrections; in lockstep it also prevents routine network stalls.
    private bool _audioStatsLogged; // one-shot audio pipeline diagnostic per session
    private double _lastStallLogMs = double.NegativeInfinity;
    private readonly object _generationLock = new();
    private SessionGeneration _generation = SessionGeneration.Legacy;
    private readonly FrameAdvantageTracker _frameAdvantage = new();

    // Desync detection: the host aggregates every peer's checksum for a frame (its own + each
    // joiner's); once it has them all it verifies they agree. Joiners just report to the host.
    // The aggregation rules live in Core (ChecksumLedger); the lock serializes UI + reader threads.
    private readonly object _hashLock = new();
    private readonly ChecksumLedger _checksums = new();

    // Live round-trip time per control link, for connection-quality feedback.
    private readonly System.Diagnostics.Stopwatch _pingClock = new();
    private readonly object _pingLock = new();
    // Scratch for RefreshPlayersList, so reading the pings out of _pingLock costs no allocation.
    // Grown, never shrunk; a session has a handful of peers.
    private double[] _pingSnapshot = new double[4];
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
    // Host: the joiner-to-joiner edges that did not open, as unordered port pairs, so input on those
    // legs — and only those — is relayed. Kept as ports rather than routes because endpoints change
    // on a rejoin — see RefreshRelayRoutes.
    private readonly HashSet<(int A, int B)> _relayPairs = [];
    // Seats whose players left for good — a graceful leave, or a rejoin wait that expired. The
    // session carries on around them: their ports read neutral forever, and every rebuilt driver
    // re-vacates them (see MarkSeatVacated / CreateDriver). UI-thread only; the count below is the
    // copy the control-reader threads read for the checksum quorum.
    private readonly HashSet<int> _vacatedPorts = [];
    private volatile int _vacatedCount;
    // Volatile: written on the UI thread when hosting starts, read by the reconnect accept loop when
    // it re-greets a returning joiner.
    private volatile PeerIdentity? _hostIdentity;
    private volatile SessionPreferences? _hostPrefs;
    private volatile int _hostTcpPort;
    private volatile int _hostUdpPort;

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
    // What the probe measured one repaired frame to cost, at this session's keyframe spacing.
    // 0 until a probe has run (a lockstep-only session never pays for one). Seeds the rollback
    // strategy's cost cap so a cold session does not discover the figure by hitching on it.
    private double _probeRepairPerFrameMs;
    // What the probe timed one whole-core savestate at. Only used to say whether the snapshot
    // rate is worth a word (see MaybeHintSaveRate) — on a light core it never is.
    private double _probeSaveMs;
    // Whether the core reproduced the same memory on replay. Unlike depth this is a correctness
    // result, not a performance one, so forcing Rollback does not get to override it.
    private bool _replayDeterministic = true;
    private int _rollbackDepth;                   // this session's savestate-ring depth when in rollback
    private const int RollbackDepthCap = 16;      // clamp the ring so resim cost + memory stay bounded

    // Saved EmuHawk config we override for the session's duration (keep running while unfocused).

    private readonly System.Diagnostics.Stopwatch _paceClock = new();
    private double _frameMs = 1000.0 / 60.0; // console frame period, drives real-time pacing
    private const int MaxFramesPerTick = 2;  // WinForms callbacks can arrive ~25ms apart; one frame caps near 40fps
    private const double FrameTickWorkBudgetMs = 8.0; // floor for fast cores; see TickBudgetMs
    // The pacing clock's arithmetic: due time, catch-up admission, budget, rebase. See FrameSchedule.
    private readonly FrameSchedule _schedule =
        new(1000.0 / 60.0, FrameTickWorkBudgetMs, MaxFramesPerTick);
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
    private readonly System.Diagnostics.Stopwatch _fpsClock = new();
    private int _fpsCount;
    private double _actualFps = -1;

    // Advanced fps alone can't tell a slow core from a stalling link from pacing debt being
    // discarded — all three read as "under 60". These carry the breakdown that separates them.
    private readonly PacingStats _pacing = new();
    private PacingSummary _lastPacing;
    private double _lastPacingLogMs = double.NegativeInfinity;
    // One-shot session advisories: say it when the problem is real, say it once, and not for a
    // single bad second. The policy is SustainedTrigger; these only hold the thresholds.
    private readonly SustainedTrigger _stallHint = new(StallHintSustainMs);
    private readonly SustainedTrigger _presentHint = new(StallHintSustainMs);
    // One-shot "rollback costs more than it can afford here" advisory; fires as soon as the
    // measured cost latches, since it is a property of the core and CPU, not a passing condition.
    private readonly SustainedTrigger _rollbackCostHint = new(0);
    // One-shot "the snapshot rate is what is costing you" advisory. Sustained like the stall hint
    // rather than latched: the elided share moves with the link, and one bad second is not a
    // reason to tell someone to change their input delay.
    private readonly SustainedTrigger _saveRateHint = new(StallHintSustainMs);
    // A savestate cheap enough that taking one every other frame does not matter — GPGX measures
    // ~0.4ms, N64 ~6ms, so this sits well clear of the light cores it must never nag about.
    private const double SaveRateHintCostMs = 2.0;
    // Elided share below which snapshots are effectively being taken continuously. A session whose
    // delay covers the link sits near 1.0; the failing case measured close to 0.
    private const double SaveRateHintElidedShare = 0.25;
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
    /// the due time a whole period per frame, so it naturally runs about sixty times a second, and
    /// the host loop cannot offer more than ~66 while paused anyway (see UpdateValues). It is kept
    /// because the floor is the cheap guard, not because the loop is fast: were the paused-throttle
    /// sleep ever to change upstream, a stall would otherwise become a spin through the whole tick
    /// body rather than a wait.
    /// </summary>
    private const double FineClockMinSpacingMs = 1.0;

    /// <summary>
    /// How long the fine clock may go quiet before <see cref="_frameTimer"/> resumes driving frames.
    /// Two frame periods: long enough that it never fires during normal play, short enough that a
    /// session which loses its idle time skips rather than stops.
    /// </summary>
    private const double FineClockFallbackMs = 34;

    private double _lastFineTickMs = double.NegativeInfinity;

    /// <summary>
    /// This launch's log file, mirroring the Log tab so it can be sent to someone afterwards.
    ///
    /// Opened before any UI exists, because <see cref="Log"/> is reachable from the constructor
    /// onward and the earliest lines — which core loaded, which ports it exposes — are the ones a
    /// mismatch report needs. Null only if the file could not be created, which every call site
    /// tolerates.
    /// </summary>
    private RotatingLogFile? _logFile;

    /// <summary>This build's version, from the assembly stamp (see the csproj). Reported in the log
    /// header so a log someone sends can be read against the code that produced it.</summary>
    private static string ToolVersion
    {
        get
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute
                    .GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                // The SDK appends "+<commit>" when the build knows one; the version is the part before it.
                var text = info?.InformationalVersion ?? asm.GetName().Version?.ToString();
                int plus = text?.IndexOf('+') ?? -1;
                return plus > 0 ? text!.Substring(0, plus) : text ?? "unknown";
            }
            catch { return "unknown"; }
        }
    }

    protected override string WindowTitleStatic => "BizHawk Netplay";

    public NetplayToolForm()
    {
        // Composed rather than field-initialised: it drives the phase, the barrier and the budget,
        // and a C# field initialiser cannot reach its siblings.
        _rebuild = new HostRebuild(_phase, _applyBarrier, _resyncBudget);
        _logFile = SessionLog.Prepare(
            $"BizHawk Netplay v{ToolVersion} — protocol {Protocol}{Environment.NewLine}" +
            $"log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local time, UTC{DateTimeOffset.Now.Offset.Hours:+00;-00}:00)" +
            Environment.NewLine);
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

        // Every control exists by now; from here the input poll thread reads a cached answer
        // instead of walking live focus state. See BlocksInputWhenFocused.
        HookFocusTracking(this);
        RecomputeBlocksInput();

        _frameTimer = new System.Windows.Forms.Timer();
        // Fallback only. While UpdateValues is running frames this does nothing, so the tick count
        // stays one-per-frame and `stall%` keeps meaning what it did. If EmuHawk stops calling in —
        // a modal dialog, a core swap, anything that takes over its loop — this resumes within a
        // frame or two rather than leaving the session with no clock at all.
        _frameTimer.Tick += (_, __) =>
        {
            if (_paceClock.Elapsed.TotalMilliseconds - _lastFineTickMs < FineClockFallbackMs) return;
            _timerTicksWindow++;
            FrameTick(fromFineClock: false);
        };

        // Drives the lobby status box: re-derives the state and advances the flash. Deliberately
        // independent of the frame clock — the states most worth showing (dialling out, waiting on
        // the host, holding a seat for a rejoin) are exactly the ones where no frames are running,
        // so a status driven by the frame tick would freeze precisely when it had something to say.
        _lobbyTimer = new System.Windows.Forms.Timer { Interval = 450 };
        _lobbyTimer.Tick += (_, __) =>
        {
            _lobbyFlashOn = !_lobbyFlashOn;
            UpdateLobbyStatus();
            // Piggy-backs on a timer that already runs whether or not a session does, which is the
            // property that matters: the log has to reach disk during a stalled session too, and the
            // frame clock is precisely what stops in that case.
            _logFile?.Flush();
        };
        _lobbyTimer.Start();

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

    /// <summary>
    /// Stop this window from killing the controller whenever it has focus.
    ///
    /// BizHawk decides whether to accept host input from the ACTIVE FORM's type, and the rule for
    /// ours is unconditional: "IExternalToolForm => AllowInput.None" (MainForm.cs:648). Not gated on
    /// AcceptBackgroundInput, not gated on anything — click this window and Input.HandleAxis starts
    /// swallowing every pad update, so the axis values freeze at whatever they last held. That is
    /// why the input test returned the same reading twelve times running, and why the analog watch
    /// saw a stick that never moved.
    ///
    /// It is not only a diagnostics problem. During a session it means clicking this window stops
    /// your controller, which is a fair description of a bug reported here earlier and blamed on
    /// several other things first.
    ///
    /// The switch is ordered, and FormBase with BlocksInputWhenFocused false is matched BEFORE the
    /// external-tool case (line 644), so overriding this gets us AllowInput.All instead. TAStudio
    /// does the same; VirtualPads does the conditional version this copies.
    ///
    /// Conditional, because AllowInput.All also routes keystrokes to EmuHawk's hotkeys: while an
    /// editable field has focus we block, so typing an IP or a password goes to the box and nowhere
    /// else. Read-only boxes — the log, the connection status — deliberately do NOT block, since
    /// reading the log during a session must not cost you your controller.
    /// </summary>
    /// <summary>
    /// One more constraint the property itself can't show: BizHawk evaluates this on its INPUT
    /// POLL THREAD, every ~2ms — Input.EnqueueNewEvents calls the AllowInput lambda from
    /// UpdateThreadProc, with BizHawk's own source remarking "WE SHOULD NOT BE SO NAIVELY TOUCHING
    /// MAINFORM FROM THE INPUTTHREAD". Walking live WinForms focus state from there is an
    /// unsynchronised read of a chain the UI thread mutates, up to 500 times a second. So the walk
    /// runs on the UI thread, on focus events, into a volatile bool the poll thread reads.
    /// </summary>
    public override bool BlocksInputWhenFocused => _blocksInputWhenFocused;

    private volatile bool _blocksInputWhenFocused;

    /// <summary>Recompute on the UI thread which the input poll thread then reads. Hooked to
    /// GotFocus/LostFocus of every control (see <see cref="HookFocusTracking"/>).</summary>
    private void RecomputeBlocksInput()
    {
        // Form.ActiveControl is the active child of THIS container; the focused leaf may be
        // several containers down (tab page -> panel -> box), so walk to it.
        Control? focused = ActiveControl;
        while (focused is IContainerControl container && container.ActiveControl != null)
            focused = container.ActiveControl;
        _blocksInputWhenFocused = focused is NumericUpDown or ComboBox
            || (focused is TextBoxBase box && !box.ReadOnly);
    }

    /// <summary>Attach the recompute to every control's focus events, including ones added later.
    /// GotFocus/LostFocus rather than Enter/Leave because the focused leaf is often an internal
    /// child (NumericUpDown's inner edit box) that Enter/Leave never names.</summary>
    private void HookFocusTracking(Control root)
    {
        root.GotFocus += (_, __) => RecomputeBlocksInput();
        root.LostFocus += (_, __) => RecomputeBlocksInput();
        root.ControlAdded += (_, e) => HookFocusTracking(e.Control);
        foreach (Control child in root.Controls) HookFocusTracking(child);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Closing the tool is also a Disconnect, including the pre-session lobby/state-transfer
        // phase. Otherwise accepted sockets and the paused emulator outlive the disposed form.
        try { EndSession("tool closed"); } catch { try { TeardownNetwork(); } catch { } }
        // Not parented to the form, so nothing else would ever stop it — a running timer whose Tick
        // touches disposed labels is the classic way a closed tool keeps throwing.
        try { _lobbyTimer.Stop(); _lobbyTimer.Dispose(); } catch { }
        // Same hazard, same fix: the frame timer is not parented either, and its Tick closes over
        // this form. Stopping it was the only thing keeping a stray tick off disposed controls.
        try { _frameTimer.Stop(); _frameTimer.Dispose(); } catch { }
        StopAnalogWatch();
        try { _tips.Dispose(); } catch { }
        try { _reflexiveKnown.Dispose(); } catch { }
        // Last, so everything the teardown above logged is on disk before the handle goes.
        try { _logFile?.Dispose(); } catch { }
        _logFile = null;
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
