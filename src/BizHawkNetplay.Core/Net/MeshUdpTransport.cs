using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// Full-mesh UDP transport for the input hot path. Unlike a host-relay star (where the host forwards
/// each peer's datagram to the others, two hops peer-to-peer), every peer here sends its own input
/// directly to every other — one hop, which is what keeps rollbacks shallow with 3–4 players. It is
/// the <see cref="ITransport"/> both the host and the joiners use; the control channel stays a star.
///
/// <b>Host-as-rendezvous connectivity checks.</b> On top of plain delivery, the mesh runs lightweight
/// ICE-lite punching: the host brokers everyone's candidate endpoints (LAN + STUN-reflexive) over the
/// control channel, and this transport actively probes each candidate with PUNCH datagrams until a
/// direct path is confirmed — opening NAT mappings without port-forwarding on cone NAT. The probes
/// double as a <b>keepalive</b>: during a lockstep stall no input flows, so an idle NAT mapping would
/// otherwise expire (~30 s) and silently kill the path — and the ping watchdog only watches the TCP
/// control link, not this one. <see cref="IsEndpointAlive"/> reports which candidates have answered,
/// so the UI can show whether a peer's direct link is up.
///
/// Datagrams carry a <c>MAGIC + version + type</c> envelope and are pinned to a known peer endpoint
/// (foreign/off-path packets dropped before the codec sees them). Symmetric NAT, where a peer's real
/// source port differs from the candidate it advertised, still needs a relay — not built here.
/// </summary>
public sealed class MeshUdpTransport : ITransport, IDisposable
{
    private static readonly byte[] Magic = [(byte)'B', (byte)'H', (byte)'N', (byte)'P'];
    private const byte Version = 2; // bumped: datagrams now carry a type byte (input vs punch)
    private const int HeaderSize = 6; // MAGIC(4) + version(1) + type(1)

    private const byte TInput = 0x10;
    private const byte TCtrlSeg = 0x20; // reliable control-stream segment (punched-joiner admission)
    private const byte TPunch = 0x30;
    private const byte TPunchAck = 0x31;
    // "I am the peer holding this token, and this is the address you actually see me at." The one
    // frame accepted from an endpoint we were never told about — see LearnFromHello.
    private const byte THello = 0x40;
    private const int TokenBytes = 16;

    private const int PunchTickMs = 250;     // probe cadence while a candidate is unconfirmed
    private const int KeepaliveMs = 1000;    // re-probe cadence once a candidate is alive (holds the NAT mapping)
    // Lobby-measurement cadence. The steady-state cadences above exist to hold NAT mappings open,
    // not to characterize a link: at 250ms a one-second window yields four samples per edge, which
    // is too few to separate a link's settled cost from its jitter. A short burst before GO buys a
    // proper sample set on the path input will actually ride.
    private const int BurstTickMs = 60;
    private const int RttWindowSamples = 24; // ~1.4s of burst per candidate
    private const int AliveWindowMs = 8000;  // no traffic for this long => the path is considered down again
    // Send-path selection is stricter than plain liveness: with keepalive acks arriving at least
    // every ~1.25s on a healthy path (and input at frame rate on the active one), a candidate not
    // heard from in this long has very likely died — fail input over to a sibling that is still
    // answering instead of waiting out the full alive window on a black hole.
    private const int FreshWindowMs = 2500;

    private readonly Socket _socket;
    private readonly ConcurrentQueue<byte[]> _inbound = new();
    /// <summary>Backlog ceiling: ~8s of four-player input, far above anything a healthy session
    /// reaches, and low enough that the memory behind it stays bounded. See EnqueueInput.</summary>
    private const int MaxInboundBacklog = 1024;
    private int _inboundDepth;
    private int _inboundPeak;
    private long _inboundDropped;
    private long _receiveFaults;
    private volatile string? _lastReceiveFault;
    private readonly Thread _rxThread;
    private readonly Thread _punchThread;
    private volatile bool _running = true;
    private volatile RouteTable _routeTable = RouteTable.Empty;
    // Host only: peers to echo every other peer's input to, because their direct legs never opened.
    private volatile PeerRoute[] _relayRoutes = [];

    // --- endpoint learning (the symmetric-NAT fix) ---------------------------------------------
    // Our own token, announced in THello, and the tokens we will accept from others. Both are
    // distributed over the authenticated control channel, so possessing one is proof of membership.
    private volatile byte[]? _localToken;
    private readonly ConcurrentDictionary<int, byte[]> _peerTokens = new();       // remotePort -> token
    // Where each peer ACTUALLY reaches us from, once it has proved who it is. A symmetric NAT gives
    // a different public port per destination, so this is frequently not any address it advertised.
    private readonly ConcurrentDictionary<int, IPEndPoint> _learnedByPort = new();
    private readonly ConcurrentDictionary<IPEndPoint, int> _learnedByEndpoint = new();
    // When each learned endpoint was first recorded, so one that never answers a probe can be
    // dropped instead of being probed for the rest of the session.
    private readonly ConcurrentDictionary<IPEndPoint, long> _learnedAt = new();

    /// <summary>How long a learned endpoint may go without answering a probe before it is forgotten.
    /// A real peer acks within a round trip of the next 250ms punch; this only has to be longer than
    /// that, and short enough that an address which was never a peer stops being probed quickly.</summary>
    private const int UnprovenLearnExpiryMs = 10_000;

    /// <summary>
    /// Blinds the timestamp carried in a punch probe.
    ///
    /// The probe's payload exists to measure a round trip: we stamp it, the peer echoes it
    /// untouched, we subtract. Sent in the clear, that value is a process-uptime millisecond
    /// counter — guessable within the ten-second window the ack is accepted in, which would let
    /// someone who never received a probe produce an ack for one and pass the liveness proof above.
    /// XORing with a per-instance random value costs nothing, changes no wire format (the peer
    /// echoes eight opaque bytes either way, so an unchanged build interoperates in both
    /// directions), and makes the echo unforgeable without having actually received the probe.
    /// </summary>
    private readonly long _punchSalt;

    /// <summary>An immutable routing snapshot, atomically replaced when rendezvous data changes.</summary>
    private sealed class RouteTable
    {
        // Spelled out rather than `new([], [])`: with both the type and the element types elided
        // there is nothing left on the line to say what is being constructed.
        public static readonly RouteTable Empty =
            new(Array.Empty<PeerRoute>(), Array.Empty<IPEndPoint>());

        private readonly Dictionary<IPEndPoint, IPEndPoint> _knownEndpoints;

        public RouteTable(PeerRoute[] routes, IPEndPoint[] endpoints)
        {
            Routes = routes;
            Endpoints = endpoints;
            _knownEndpoints = new Dictionary<IPEndPoint, IPEndPoint>();
            foreach (var endpoint in endpoints) _knownEndpoints[endpoint] = endpoint;
        }

        public PeerRoute[] Routes { get; }
        public IPEndPoint[] Endpoints { get; }

        public bool TryResolve(IPEndPoint endpoint, out IPEndPoint known) =>
            _knownEndpoints.TryGetValue(endpoint, out known!);
    }

    /// <summary>
    /// A bounded ring of raw round-trip samples for one candidate, summarized the same way the
    /// control-channel lobby probe summarizes its own: median for the settled cost, nearest-rank
    /// 85th percentile for the high-water mark. Using the same statistic on both transports is what
    /// makes a UDP reading and a TCP reading comparable enough to take the worst of.
    /// </summary>
    private sealed class RttWindow
    {
        private readonly double[] _samples = new double[RttWindowSamples];
        private int _count;
        private int _next;

        public void Add(double sample)
        {
            lock (_samples)
            {
                _samples[_next] = sample;
                _next = (_next + 1) % RttWindowSamples;
                if (_count < RttWindowSamples) _count++;
            }
        }

        public bool TryDescribe(out double medianMs, out double highMs)
        {
            double[] sorted;
            lock (_samples)
            {
                if (_count == 0) { medianMs = 0; highMs = 0; return false; }
                sorted = new double[_count];
                Array.Copy(_samples, sorted, _count);
            }
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            medianMs = sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) / 2.0
                : sorted[middle];
            int rank = (int)Math.Ceiling(0.85 * sorted.Length);
            if (rank < 1) rank = 1;
            if (rank > sorted.Length) rank = sorted.Length;
            highMs = sorted[rank - 1];
            if (highMs < medianMs) highMs = medianMs;
            return true;
        }
    }

    // Per-candidate liveness: endpoint -> last time we heard anything back from it (stopwatch ms).
    private readonly ConcurrentDictionary<IPEndPoint, long> _alive = new();
    private readonly ConcurrentDictionary<IPEndPoint, long> _lastPunch = new();
    private readonly ConcurrentDictionary<IPEndPoint, double> _rtt = new();
    // Raw sample window per candidate, kept alongside the EMA above. The EMA is what send-path
    // selection wants (one smooth number); a delay decision wants the distribution, because what
    // stalls a session is the worst packet rather than the typical one.
    private readonly ConcurrentDictionary<IPEndPoint, RttWindow> _rttWindows =
        new();
    private long _burstUntilMs = long.MinValue;
    // Last candidate input was actually sent through, per logical peer — the failover anchor
    // while a repunch has the liveness table cleared.
    private readonly ConcurrentDictionary<int, IPEndPoint> _lastSelected = new();

    // Reliable control streams carried on this same socket, keyed by peer endpoint — what lets a
    // hole-punched joiner run the ordinary handshake into a normal hosted lobby with no TCP.
    private readonly ConcurrentDictionary<IPEndPoint, ReliableUdpStream> _controlStreams =
        new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    // Reflexive-address discovery: while a request is pending, the receive loop watches for the STUN
    // response on this same socket (so the reflexive port is the one the mesh actually uses).
    private volatile byte[]? _pendingStunTxn;
    private volatile IPEndPoint? _reflexive;
    private readonly ManualResetEventSlim _stunEvent = new(false);

    /// <summary>
    /// Windows-only ioctl that stops a UDP socket from reporting an ICMP port-unreachable as a
    /// receive error. Without it, punching at a peer that is not listening yet — which the punch
    /// loop does every 250ms, by design — makes the NEXT ReceiveFrom throw 10054, so the receive
    /// loop spends session start-up throwing and restarting instead of receiving.
    /// </summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>
    /// Socket buffer size. The default on Windows is ~8KB — a few dozen datagrams — and the kernel
    /// queue is the only thing absorbing a scheduling gap on the receive thread. Datagrams dropped
    /// there are invisible: they never reach InboundDropped, so the loss surfaced as a stall or a
    /// deep rollback and got blamed on the network. MaxInboundBacklog reasons carefully about the
    /// user-space queue; this is the kernel queue in front of it.
    /// </summary>
    private const int SocketBufferBytes = 1 << 18;   // 256 KiB

    private MeshUdpTransport(int localPort)
    {
        var saltBytes = new byte[8];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            rng.GetBytes(saltBytes);
        _punchSalt = BitConverter.ToInt64(saltBytes, 0);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, SocketBufferBytes);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, SocketBufferBytes);
        // Both are best-effort: neither is load-bearing, and a platform that refuses one should not
        // cost the session its transport.
        try { _socket.IOControl(SioUdpConnReset, new byte[4], null); } catch { /* not Windows */ }
        // Makes the codec's 1200-byte cap enforced rather than assumed — a datagram that outgrows it
        // now fails at the sender instead of being fragmented and silently lost to a middlebox.
        try { _socket.DontFragment = true; } catch { /* unsupported here */ }
        _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "BizHawkNetplay-UDP-mesh" };
        _rxThread.Start();
        _punchThread = new Thread(PunchLoop) { IsBackground = true, Name = "BizHawkNetplay-UDP-punch" };
        _punchThread.Start();
    }

    /// <summary>The local UDP port actually bound (read this when binding to port 0).</summary>
    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint).Port;

    public static MeshUdpTransport Bind(int localPort) => new(localPort);

    /// <summary>
    /// Peers whose direct joiner-to-joiner paths did not open, so this node forwards every other
    /// peer's input to them. Host only, decided once at session start; empty is the normal case and
    /// costs a single array length check per received datagram.
    /// </summary>
    public void SetRelayRoutes(IEnumerable<PeerRoute> routes) =>
        _relayRoutes = routes == null ? [] : routes.ToArray();

    /// <summary>How many peers are being relayed to — for the session log, and zero when the mesh
    /// is fully connected.</summary>
    public int RelayRouteCount => _relayRoutes.Length;

    /// <summary>The candidates actually installed for one seat, after filtering and the global cap.
    /// Empty for a seat with no route. Exposed so the filtering rules can be asserted.</summary>
    public IReadOnlyList<IPEndPoint> RouteCandidates(int remotePort)
    {
        foreach (var route in _routeTable.Routes)
            if (route.RemotePort == remotePort) return route.Candidates;
        return Array.Empty<IPEndPoint>();
    }

    /// <summary>
    /// Synthetic port numbers for punch targets admitted while a lobby is already up start here.
    /// Far above any real seat, so a placeholder can never land on — and evict — a port the lobby
    /// has already routed to a joiner.
    /// </summary>
    public const int PunchTargetPortBase = 1000;

    /// <summary>
    /// Add punch targets WITHOUT disturbing the routes already installed.
    ///
    /// <see cref="SetPeerRoutes"/> replaces the table wholesale and forgets the liveness, RTT and
    /// punch history of every endpoint that falls out of it. Host-side punch admission runs while
    /// the lobby is live, so calling that there discarded the joiner routes the lobby had just
    /// installed and reset their measurements — pasting a second connect code undid the first
    /// joiner's progress. Merging keeps every existing endpoint in the set, so nothing is purged.
    /// </summary>
    public void AddPunchTargets(IEnumerable<IPEndPoint> targets)
    {
        if (targets == null) throw new ArgumentNullException(nameof(targets));

        var merged = new List<PeerRoute>(_routeTable.Routes);
        var known = new HashSet<IPEndPoint>();
        int nextPort = PunchTargetPortBase;
        foreach (var route in merged)
        {
            foreach (var candidate in route.Candidates) known.Add(candidate);
            if (route.RemotePort >= nextPort) nextPort = route.RemotePort + 1;
        }

        bool added = false;
        foreach (var target in targets)
        {
            if (target == null)
                throw new ArgumentException("Punch targets cannot contain null", nameof(targets));
            if (!known.Add(target)) continue;   // already routed, under whatever port owns it
            merged.Add(new PeerRoute(nextPort++, new[] { target }));
            added = true;
        }
        if (added) SetPeerRoutes(merged);
    }

    /// <summary>
    /// Replace the logical peer routes. Candidates are de-duplicated globally as well as within each
    /// route, so an endpoint advertised twice is probed and sent to only once. Repeated entries for the
    /// same remote port are merged in their original order.
    /// </summary>
    /// <summary>
    /// An address this socket can actually send to, and should be willing to.
    ///
    /// The socket is bound IPv4, so a v6 candidate makes SendTo raise — and candidates arrive from
    /// the wire, redistributed by the host to every joiner. Multicast, broadcast and 0.0.0.0 parse
    /// as perfectly good endpoints and are never a peer: the punch loop probes every candidate four
    /// times a second and the no-confirmed-path fallback broadcasts input to all of them, so an
    /// unroutable candidate is not merely useless but something the tool can be talked into
    /// pointing at a third party. Filtered here rather than in the codec, because it is this
    /// socket's address family that decides it — the codec stays able to represent what it is given.
    /// </summary>
    private static bool IsRoutableUnicastV4(IPEndPoint endpoint)
    {
        if (endpoint.AddressFamily != AddressFamily.InterNetwork) return false;
        if (endpoint.Port is <= 0 or > 65535) return false;
        var address = endpoint.Address;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast)) return false;
        byte first = address.GetAddressBytes()[0];
        if ((first & 0xF0) == 0xE0) return false;   // 224.0.0.0/4 multicast
        if (first == 255) return false;             // 255.0.0.0/8 — never a unicast peer
        return true;
    }

    /// <summary>
    /// Total candidates the table will hold, across every route. The punch loop and the
    /// no-confirmed-path broadcast both scale with this, so it is bounded rather than trusted —
    /// four players advertising three addresses each is twelve.
    /// </summary>
    private const int MaxRoutedEndpoints = 64;

    public void SetPeerRoutes(IEnumerable<PeerRoute> routes)
    {
        if (routes == null) throw new ArgumentNullException(nameof(routes));

        var portOrder = new List<int>();
        var candidatesByPort = new Dictionary<int, List<IPEndPoint>>();
        var endpoints = new List<IPEndPoint>();
        var globallySeen = new HashSet<IPEndPoint>();
        foreach (var route in routes)
        {
            if (route == null)
                throw new ArgumentException("Peer routes cannot contain null", nameof(routes));
            if (!candidatesByPort.TryGetValue(route.RemotePort, out var candidates))
            {
                candidates = new List<IPEndPoint>();
                candidatesByPort.Add(route.RemotePort, candidates);
                portOrder.Add(route.RemotePort);
            }
            foreach (var endpoint in route.Candidates)
            {
                // Dropped, not thrown: an unroutable or surplus candidate degrades exactly as one
                // that never answers already does, and a route table arriving from a peer must
                // never be able to end the session.
                if (!IsRoutableUnicastV4(endpoint)) continue;
                if (endpoints.Count >= MaxRoutedEndpoints) break;
                if (!globallySeen.Add(endpoint)) continue;
                candidates.Add(endpoint);
                endpoints.Add(endpoint);
            }
        }

        var normalized = new PeerRoute[portOrder.Count];
        for (int i = 0; i < normalized.Length; i++)
        {
            int port = portOrder[i];
            normalized[i] = new PeerRoute(port, candidatesByPort[port]);
        }

        _routeTable = new RouteTable(normalized, [.. endpoints]);

        // Forget telemetry for candidates no longer in the set (a rejoin can change addresses).
        var keep = new HashSet<IPEndPoint>(endpoints);

        // A learned endpoint belongs to a PORT, not to that port's advertised candidates, so it
        // survives a route refresh — otherwise every reflexive candidate that trickled in would
        // un-learn the symmetric-NAT peers, which is the one thing they cannot recover from on
        // their own. It does not survive its port leaving the session.
        var routedPorts = new HashSet<int>(portOrder);
        foreach (var kv in _learnedByPort.ToArray())
        {
            if (routedPorts.Contains(kv.Key)) { keep.Add(kv.Value); continue; }
            _learnedByPort.TryRemove(kv.Key, out _);
            _learnedByEndpoint.TryRemove(kv.Value, out _);
        }
        foreach (var k in _alive.Keys.ToArray()) if (!keep.Contains(k)) _alive.TryRemove(k, out _);
        foreach (var k in _lastPunch.Keys.ToArray()) if (!keep.Contains(k)) _lastPunch.TryRemove(k, out _);
        foreach (var k in _rtt.Keys.ToArray()) if (!keep.Contains(k)) _rtt.TryRemove(k, out _);
        foreach (var k in _rttWindows.Keys.ToArray()) if (!keep.Contains(k)) _rttWindows.TryRemove(k, out _);
        foreach (var kv in _lastSelected.ToArray()) if (!keep.Contains(kv.Value)) _lastSelected.TryRemove(kv.Key, out _);
    }

    // Scratch for framing the per-frame input datagram. Send() runs only on the frame thread
    // (see SendFramed's note), so one buffer suffices — and at 60Hz the copy-to-prepend-6-bytes
    // was the transport's largest steady-state allocation.
    private byte[] _inputFrameScratch = new byte[2048];

    public void Send(byte[] datagram)
    {
        if (datagram == null) throw new ArgumentNullException(nameof(datagram));
        int framedLength = HeaderSize + datagram.Length;
        var framed = _inputFrameScratch;
        if (framed.Length < framedLength) _inputFrameScratch = framed = new byte[framedLength];
        Buffer.BlockCopy(Magic, 0, framed, 0, 4);
        framed[4] = Version;
        framed[5] = TInput;
        Buffer.BlockCopy(datagram, 0, framed, HeaderSize, datagram.Length);
        var table = _routeTable;
        long now = Clock.ElapsedMilliseconds;
        foreach (var route in table.Routes)
        {
            var endpoint = SelectSendCandidate(route, now);
            if (endpoint != null)
            {
                SendFramed(framed, 0, framedLength, endpoint);
                continue;
            }
            // No path to this peer has EVER been confirmed (fresh session, punch still in
            // flight). Guessing one candidate can blackhole the whole opening of the session
            // toward a NAT'd peer — the pre-NAT candidate silently ate the first ~300ms of
            // input in the first real-internet test. Send to every candidate until the first
            // ack picks a winner. Deliberately NOT the learned endpoint: until it answers a
            // probe it is only a claim, and input toward a claim is the amplifier
            // AnAddressThatOnlyClaimsASeatIsProbedButNeverSentInput exists to forbid. A learned
            // endpoint that ever carried input is preserved by the _lastSelected fallback above.
            var candidates = route.Candidates;
            for (int i = 0; i < candidates.Count; i++) SendFramed(framed, 0, framedLength, candidates[i]);
        }
    }

    public bool TryReceive(out byte[] datagram)
    {
        if (!_inbound.TryDequeue(out datagram!)) return false;
        Interlocked.Decrement(ref _inboundDepth);
        return true;
    }

    /// <summary>Deepest the inbound backlog has been this session — the number that says whether
    /// the cap is anywhere near being reached on real links.</summary>
    public int InboundPeakDepth => _inboundPeak;

    /// <summary>Input datagrams discarded because the backlog was full. Any nonzero value here is
    /// worth a line in the log: it means the frame loop stopped draining for long enough to matter.</summary>
    public long InboundDropped => Interlocked.Read(ref _inboundDropped);

    /// <summary>Datagrams whose handling threw. Nonzero means a bug on the receive path, not a bad
    /// network — the distinction the session log could not previously make.</summary>
    public long ReceiveFaults => Interlocked.Read(ref _receiveFaults);

    /// <summary>The most recent receive-path fault, or null if there has never been one.</summary>
    public string? LastReceiveFault => _lastReceiveFault;

    /// <summary>True if this candidate endpoint has answered a probe or sent input recently — i.e. a
    /// direct UDP path to it is currently open.</summary>
    public bool IsEndpointAlive(IPEndPoint endpoint)
        => endpoint != null && IsEndpointAlive(endpoint, Clock.ElapsedMilliseconds);

    private bool IsEndpointAlive(IPEndPoint endpoint, long now) =>
        _alive.TryGetValue(endpoint, out var t) && now - t < AliveWindowMs;

    /// <summary>
    /// Smoothed round-trip time to a candidate, measured by the timestamped punch/ack exchange — i.e.
    /// on the path input actually travels, rather than the TCP control link. False when nothing has
    /// answered yet, or when the peer runs a build whose acks carry no timestamp.
    /// </summary>
    public bool TryGetRttMs(IPEndPoint endpoint, out double rttMs)
    {
        rttMs = 0;
        if (endpoint == null) return false;
        if (!_rtt.TryGetValue(endpoint, out double v)) return false;
        rttMs = v;
        return true;
    }

    /// <summary>
    /// Select each logical peer's lowest-RTT live candidate, then return the worst of those per-peer
    /// paths. A route whose candidates have all gone quiet falls back to its last stored measurement
    /// rather than failing the whole aggregate: on a joiner the TCP fallback only covers the host
    /// link, so collapsing here would report e.g. a 15 ms host ping in place of a 180 ms peer path
    /// and mis-size the rollback soft cap exactly while that peer's path is recovering. Only a route
    /// that has NEVER been measured makes this return false (caller then uses its TCP samples).
    /// </summary>
    public bool TryGetWorstRttMs(out double rttMs)
    {
        rttMs = -1;
        long now = Clock.ElapsedMilliseconds;
        foreach (var route in _routeTable.Routes)
        {
            // A partial maximum is dangerously optimistic: one fast measured player must not
            // hide another logical peer whose UDP path has no RTT at all yet.
            if (route.Candidates.Count == 0 ||
                (!TryGetBestLiveRtt(route, now, out double peerRtt) && !TryGetBestStoredRtt(route, out peerRtt)))
            {
                rttMs = -1;
                return false;
            }
            if (peerRtt > rttMs) rttMs = peerRtt;
        }
        return rttMs >= 0;
    }

    internal void RecordRtt(IPEndPoint endpoint, double sample)
    {
        // Same EMA shape as the control-channel ping, so the two readings are comparable.
        _rtt.AddOrUpdate(endpoint, sample, (_, prev) => 0.8 * prev + 0.2 * sample);
        _rttWindows.GetOrAdd(endpoint, _ => new RttWindow()).Add(sample);
    }

    /// <summary>
    /// Probe every candidate at <see cref="BurstTickMs"/> for the next <paramref name="durationMs"/>
    /// and start each candidate's sample window over. Called once per peer in the lobby, before GO:
    /// the resulting figures are the only measurement of the joiner-to-joiner edges that exists, and
    /// they are taken on the UDP path rather than on the control link.
    /// </summary>
    public void BeginRttBurst(int durationMs)
    {
        if (durationMs < 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
        _rttWindows.Clear();
        _lastPunch.Clear();   // probe on the very next tick rather than waiting out the keepalive
        Interlocked.Exchange(ref _burstUntilMs, Clock.ElapsedMilliseconds + durationMs);
    }

    /// <summary>
    /// Per-candidate view of the burst window: the settled round-trip and its high-water mark on
    /// this exact path. False until at least one probe has been answered.
    /// </summary>
    public bool TryGetRttStats(IPEndPoint endpoint, out double medianMs, out double highMs)
    {
        medianMs = 0;
        highMs = 0;
        return endpoint != null
            && _rttWindows.TryGetValue(endpoint, out var window)
            && window.TryDescribe(out medianMs, out highMs);
    }

    /// <summary>
    /// Aggregate the sample windows the way a session-wide input delay has to be chosen: per logical
    /// peer take its best candidate (the path input would actually use), then take the worst median
    /// and the worst high-water mark across peers — independently, because one delay has to cover
    /// every edge on both counts even when the offenders are different peers.
    ///
    /// <paramref name="measuredRoutes"/>/<paramref name="totalRoutes"/> let the caller say how much
    /// of the mesh the figure actually covers: a route whose punch never completed contributes
    /// nothing, and silently averaging it away would be exactly the blind spot this exists to close.
    /// </summary>
    public bool TryGetWorstRttStats(out double medianMs, out double highMs,
        out int measuredRoutes, out int totalRoutes)
    {
        medianMs = -1;
        highMs = -1;
        measuredRoutes = 0;
        var edges = DescribeEdges();
        totalRoutes = edges.Count;
        foreach (var edge in edges)
        {
            if (!edge.Measured) continue;
            measuredRoutes++;
            if (edge.MedianMs > medianMs) medianMs = edge.MedianMs;
            if (edge.HighMs > highMs) highMs = edge.HighMs;
        }
        return measuredRoutes > 0;
    }

    /// <summary>
    /// One row per logical peer: whether its edge answered, how fast, and whether the address that
    /// worked was one nobody advertised.
    ///
    /// A count of answered edges says how much of the mesh is covered but not who is missing, and
    /// "2/3 answered" is the same sentence whether the silent player is about to have a bad session
    /// or is not in it yet. Naming the port is what lets the log say something actionable.
    /// </summary>
    public IReadOnlyList<MeshEdgeReport> DescribeEdges()
    {
        var routes = _routeTable.Routes;
        var reports = new MeshEdgeReport[routes.Length];
        for (int i = 0; i < routes.Length; i++)
        {
            var route = routes[i];
            bool learned = _learnedByPort.TryGetValue(route.RemotePort, out var learnedEndpoint);
            double bestMedian = 0, bestHigh = 0;
            bool measured = false, viaLearned = false;
            foreach (var endpoint in learned
                         ? route.Candidates.Concat(new[] { learnedEndpoint })
                         : route.Candidates)
            {
                if (!TryGetRttStats(endpoint, out double candidateMedian, out double candidateHigh)) continue;
                if (!measured || candidateMedian < bestMedian)
                {
                    bestMedian = candidateMedian;
                    bestHigh = candidateHigh;
                    viaLearned = learned && endpoint.Equals(learnedEndpoint);
                }
                measured = true;
            }
            reports[i] = new MeshEdgeReport(route.RemotePort, measured, bestMedian, bestHigh, viaLearned);
        }
        return reports;
    }

    /// <summary>Membership test over the advertised candidates, indexed rather than LINQ — this
    /// sits on the per-frame send path.</summary>
    private static bool IsRoutedCandidate(PeerRoute route, IPEndPoint endpoint)
    {
        var candidates = route.Candidates;
        for (int i = 0; i < candidates.Count; i++)
            if (candidates[i].Equals(endpoint)) return true;
        return false;
    }

    private IPEndPoint? SelectSendCandidate(PeerRoute route, long now)
    {
        // A learned endpoint outranks every advertised candidate, because it is the only one we have
        // OBSERVED this peer arriving from. For a symmetric-NAT peer none of the advertised
        // candidates can ever work, so without this the learning would be recorded and never used.
        if (_learnedByPort.TryGetValue(route.RemotePort, out var learned)
            && _alive.TryGetValue(learned, out var learnedHeard) && now - learnedHeard < AliveWindowMs)
        {
            _lastSelected[route.RemotePort] = learned;
            return learned;
        }

        IPEndPoint? firstFresh = null, bestFresh = null;
        IPEndPoint? firstLive = null, bestLive = null;
        double bestFreshRtt = double.MaxValue, bestLiveRtt = double.MaxValue;
        var routeCandidates = route.Candidates;
        for (int i = 0; i < routeCandidates.Count; i++) // indexed: per-frame path, no enumerator box
        {
            var endpoint = routeCandidates[i];
            if (!_alive.TryGetValue(endpoint, out var heard) || now - heard >= AliveWindowMs) continue;
            bool fresh = now - heard < FreshWindowMs;
            if (firstLive == null) firstLive = endpoint;
            if (fresh && firstFresh == null) firstFresh = endpoint;
            if (_rtt.TryGetValue(endpoint, out double rtt) && rtt >= 0)
            {
                if (rtt < bestLiveRtt) { bestLiveRtt = rtt; bestLive = endpoint; }
                if (fresh && rtt < bestFreshRtt) { bestFreshRtt = rtt; bestFresh = endpoint; }
            }
        }

        // Prefer candidates heard from RECENTLY. A path that dies mid-session keeps its (stale,
        // low) RTT and stays inside the alive window for a while; if a sibling candidate is still
        // answering keepalives, input must move there rather than stay pinned to a black hole
        // until the alive window finally expires — which races the UDP-lost session watchdog.
        var chosen = bestFresh ?? firstFresh ?? bestLive ?? firstLive;
        if (chosen != null)
        {
            _lastSelected[route.RemotePort] = chosen;
            return chosen;
        }

        // Nothing is confirmed right now (start-up, or a repunch just cleared the liveness table).
        // Keep sending along the last path that actually worked: for an internet peer the first
        // advertised candidate is typically the pre-NAT address, which is exactly the one that
        // does NOT work when the reflexive path was carrying the session. The learned endpoint
        // counts as valid here even though it is never in Candidates — for a symmetric-NAT peer
        // it is the ONLY address that works, and rejecting it stopped input to that peer entirely
        // the moment its liveness lapsed.
        if (_lastSelected.TryGetValue(route.RemotePort, out var last)
            && (IsRoutedCandidate(route, last)
                || (_learnedByPort.TryGetValue(route.RemotePort, out var learnedLast) && learnedLast.Equals(last))))
            return last;

        // Nothing has ever worked for this peer — no single candidate is a safe guess. Return
        // null so the caller broadcasts to every candidate until the punch confirms one.
        return null;
    }

    private bool TryGetBestLiveRtt(PeerRoute route, long now, out double bestRtt)
    {
        bestRtt = double.MaxValue;
        bool found = false;
        var routeCandidates = route.Candidates;
        for (int i = 0; i < routeCandidates.Count; i++)
        {
            var endpoint = routeCandidates[i];
            if (!IsEndpointAlive(endpoint, now)) continue;
            if (!_rtt.TryGetValue(endpoint, out double rtt) || rtt < 0) continue;
            if (rtt < bestRtt) bestRtt = rtt;
            found = true;
        }
        if (!found) bestRtt = 0;
        return found;
    }

    /// <summary>Last measured RTT for a route regardless of liveness — the stale fallback for a
    /// once-measured path that has gone quiet (repunch in flight, NAT rebind, resync).</summary>
    private bool TryGetBestStoredRtt(PeerRoute route, out double bestRtt)
    {
        bestRtt = double.MaxValue;
        bool found = false;
        foreach (var endpoint in route.Candidates)
        {
            if (!_rtt.TryGetValue(endpoint, out double rtt) || rtt < 0) continue;
            if (rtt < bestRtt) bestRtt = rtt;
            found = true;
        }
        if (!found) bestRtt = 0;
        return found;
    }

    /// <summary>
    /// Open (or get) the reliable, ordered control stream to one peer endpoint, carried on this
    /// same socket demultiplexed by a segment type. Wrap it in a
    /// <see cref="Session.ControlChannel"/> and the ordinary handshake / state transfer /
    /// checksum machinery runs over it unchanged — this is what lets a hole-punched joiner be
    /// admitted into a normal hosted lobby with no TCP anywhere on its link. Inbound segments
    /// are accepted only from known route candidates AND only for endpoints with an open
    /// stream, so an unadmitted stranger cannot open reliable-stream state on the host.
    /// </summary>
    public System.IO.Stream OpenControl(IPEndPoint peer)
    {
        if (peer == null) throw new ArgumentNullException(nameof(peer));
        // Not GetOrAdd with a factory: the factory can run, lose the publication race, and its
        // undisposed loser would keep a retransmit thread waking every 50ms for the process
        // lifetime. Construct outside, publish, and dispose the loser.
        if (_controlStreams.TryGetValue(peer, out var existing)) return existing;
        var created = new ReliableUdpStream(seg => SendFramed(Frame(TCtrlSeg, seg), peer));
        var winner = _controlStreams.GetOrAdd(peer, created);
        if (!ReferenceEquals(winner, created)) { try { created.Dispose(); } catch { } }
        return winner;
    }

    /// <summary>Close and forget one peer's control stream (refused admission, or link death).</summary>
    public void CloseControl(IPEndPoint peer)
    {
        if (peer == null) return;
        if (_controlStreams.TryRemove(peer, out var stream))
        {
            try { stream.Dispose(); } catch { /* teardown is best-effort */ }
        }
    }

    /// <summary>Forget the current path confirmations and make the punch loop probe every candidate
    /// immediately. Used when control traffic is healthy but input progress has gone quiet.</summary>
    public void RequestRepunch()
    {
        _alive.Clear();
        _lastPunch.Clear();
    }

    /// <summary>Forget confirmations only for one logical peer. Healthy routes keep their chosen
    /// failover candidate while the silent peer is re-probed. The learned endpoint is cleared
    /// too — for a symmetric-NAT peer it IS the path in use, and leaving its liveness standing
    /// meant the "re-punching the input path" recovery re-probed everything except the one
    /// address that had gone quiet.</summary>
    public void RequestRepunch(int remotePort)
    {
        foreach (var route in _routeTable.Routes)
        {
            if (route.RemotePort != remotePort) continue;
            foreach (var endpoint in route.Candidates)
            {
                _alive.TryRemove(endpoint, out _);
                _lastPunch.TryRemove(endpoint, out _);
            }
        }
        if (_learnedByPort.TryGetValue(remotePort, out var learned))
        {
            _alive.TryRemove(learned, out _);
            _lastPunch.TryRemove(learned, out _);
        }
    }

    /// <summary>
    /// Discover this socket's public (reflexive) address via STUN, without disturbing the running
    /// receive loop — the response is caught there. Because it's the mesh's own socket, the port
    /// reported is the one peers must send to. Returns null if offline or every server times out.
    /// </summary>
    public IPEndPoint? DiscoverReflexive(TimeSpan timeout)
    {
        int perServer = Math.Max(400, (int)(timeout.TotalMilliseconds / StunClient.Servers.Length));
        foreach (var (host, port) in StunClient.Servers)
        {
            if (!_running) return null;
            var server = StunClient.ResolveV4(host, port);
            if (server == null) continue;

            var req = StunClient.BuildRequest(out var txn);
            try
            {
                _reflexive = null;
                _stunEvent.Reset();
                _pendingStunTxn = txn;
                _socket.SendTo(req, server);
                bool got = _stunEvent.Wait(perServer);
                if (got && _reflexive != null) return _reflexive;
            }
            catch (ObjectDisposedException) { return null; }
            catch (SocketException) { if (!_running) return null; }
            finally { _pendingStunTxn = null; }
        }
        return null;
    }

    private void PunchLoop()
    {
        while (_running)
        {
            bool bursting = false;
            try
            {
                long now = Clock.ElapsedMilliseconds;
                bursting = now < Interlocked.Read(ref _burstUntilMs);
                PruneUnprovenLearned(now);
                // Learned endpoints are probed alongside the advertised ones. Without this a
                // symmetric-NAT peer is a one-way street: we accept its input and reply to it, but
                // never probe it, so nothing ever measures that edge and the lobby reports it silent
                // while it is plainly carrying traffic. It also has to be kept warm like any other.
                foreach (var p in _routeTable.Endpoints.Concat(_learnedByPort.Values).Distinct())
                {
                    bool alive = IsEndpointAlive(p);
                    // Probe aggressively until confirmed, then just often enough to hold the mapping —
                    // unless a lobby measurement is in flight, which wants samples, not mappings.
                    int due = bursting ? BurstTickMs : alive ? KeepaliveMs : PunchTickMs;
                    bool neverSent = !_lastPunch.TryGetValue(p, out var lastSent);
                    if (neverSent || now - lastSent >= due)
                    {
                        // Stamp the probe so its ack measures the round trip on THIS path — the one
                        // input actually rides. The control channel's ping measures TCP, which is a
                        // different route once the mesh is direct, and is inflated by TCP's own
                        // queueing and retransmits.
                        SendFramed(Frame(TPunch, BitConverter.GetBytes(now ^ _punchSalt)), p);
                        // Ride alongside the probe: whatever address our NAT gives this particular
                        // destination, the token lets the far side recognise the packet as ours and
                        // record where we really came from. Only while the path is unconfirmed —
                        // once it answers, the advertised address evidently works and this is noise.
                        var token = _localToken;
                        if (token != null && !alive) SendFramed(Frame(THello, token), p);
                        _lastPunch[p] = now;
                    }
                }
            }
            catch { /* transient; keep the loop alive */ }
            Thread.Sleep(bursting ? BurstTickMs : PunchTickMs);
        }
    }

    private void ReceiveLoop()
    {
        // Must stay above InputPacketCodec.MaxDatagramBytes + HeaderSize (1206) and above the
        // reliable stream's segment size. A datagram larger than this buffer does not truncate: the
        // socket raises a size error, the loop below can only skip it, and the OS has already
        // discarded the data — so the sender's input would vanish permanently while every other
        // signal said the network was fine. The codec's own cap is what makes that unreachable;
        // this is the second wall behind it.
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        // Safety valve: a SocketException normally consumes one queued ICMP error and the next
        // receive proceeds, but a socket that errors PERSISTENTLY (repeated WSAENOBUFS, or
        // continuous ICMP resets where the SIO_UDP_CONNRESET ioctl didn't take) would turn this
        // retry into a 100%-core spin. Successes reset the count; a run of consecutive failures
        // buys a brief sleep, cheap enough to never matter on the bounded case.
        int consecutiveErrors = 0;
        while (_running)
        {
            int n;
            try { n = _socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException)
            {
                if (!_running) break;
                if (++consecutiveErrors >= 50) { consecutiveErrors = 0; Thread.Sleep(10); }
                continue;
            }
            catch (ObjectDisposedException) { break; }
            consecutiveErrors = 0;

            // Everything past the receive itself is guarded, because this is the ONE thread that
            // delivers input, punch acks and control segments. An escape used to end it outright
            // while _running stayed true and Dispose still reported success, so the session died
            // eight seconds later on "UDP input path lost" — a code fault wearing a network fault's
            // clothes. Counted and remembered, so the session log can tell the two apart.
            try { DispatchDatagram(buffer, n, (IPEndPoint)from); }
            catch (Exception ex)
            {
                if (!_running) break;
                Interlocked.Increment(ref _receiveFaults);
                _lastReceiveFault = ex.GetType().Name + ": " + ex.Message;
            }
        }
    }

    /// <summary>
    /// Handle one received datagram. Separated from the loop so that a throw costs one datagram
    /// rather than every datagram: see the guard in <see cref="ReceiveLoop"/>.
    /// </summary>
    private void DispatchDatagram(byte[] buffer, int n, IPEndPoint source)
    {
        // While a reflexive discovery is in flight, a STUN response arrives here (it isn't
        // MAGIC-framed, so it would otherwise be dropped below). Only parsed while pending.
        var stunTxn = _pendingStunTxn;
        if (stunTxn != null && n >= 20)
        {
            var pkt = new byte[n];
            Buffer.BlockCopy(buffer, 0, pkt, 0, n);
            var refl = StunClient.ParseResponse(pkt, stunTxn);
            if (refl != null) { _reflexive = refl; _stunEvent.Set(); return; }
        }

        if (n < HeaderSize) return;
        if (buffer[0] != Magic[0] || buffer[1] != Magic[1] ||
            buffer[2] != Magic[2] || buffer[3] != Magic[3]) return;
        if (buffer[4] != Version) return;
        byte type = buffer[5];

        bool learned = false;
        if (!_routeTable.TryResolve(source, out var known))
        {
            // Already learned: everything from here on is ordinary traffic from a known peer.
            if (_learnedByEndpoint.ContainsKey(source)) { known = source; learned = true; }
            // Otherwise, not an address anyone advertised — and exactly one thing may still be
            // true of it: a peer holding a valid token is telling us this is where it really
            // comes from. That is the symmetric-NAT case, where the address it advertised was
            // only ever valid for the STUN server it asked. Anything else is dropped here, unread.
            else if (type == THello && LearnFromHello(buffer, n, source)) { known = source; learned = true; }
            else return;
        }

        // An advertised endpoint reached us through the authenticated control channel, so any framed
        // packet from one is evidence the path is up. A LEARNED endpoint is different in kind: it is
        // an address a packet NAMED as its own source, and a UDP source address is a claim, not a
        // fact. Treating any framed packet from one as proof of life is what made this an amplifier
        // — a single spoofed 30-byte THello marked the named address alive, and an alive learned
        // endpoint outranks every advertised candidate in SelectSendCandidate, so the full input
        // stream turned on toward a machine that had never said anything.
        //
        // Only a punch ack can settle it: it echoes eight bytes we chose and blinded (see
        // _punchSalt), so producing one means actually receiving our probe at that address. Until
        // then the address is carried as a claim — probed, never sent to, and dropped if it stays
        // silent (PruneUnprovenLearned).
        if (!learned || type == TPunchAck)
            _alive[known] = Clock.ElapsedMilliseconds;

        if (type == TPunch)
        {
            // Echo the probe's timestamp back untouched so the sender can time the round trip.
            // A peer on an older build sends an empty probe and gets an empty ack — the RTT is
            // simply never measured there, and the caller falls back to the control-channel ping.
            var echo = n >= HeaderSize + 8 ? new byte[8] : [];
            if (echo.Length == 8) Buffer.BlockCopy(buffer, HeaderSize, echo, 0, 8);
            SendFramed(Frame(TPunchAck, echo), known); // answer so the peer confirms us too
            return;
        }
        if (type == TPunchAck)
        {
            if (n >= HeaderSize + 8)
            {
                long sentAt = BitConverter.ToInt64(buffer, HeaderSize) ^ _punchSalt;
                double rtt = Clock.ElapsedMilliseconds - sentAt;
                // Guard against a stale/garbled echo outliving a clock restart.
                if (rtt >= 0 && rtt < 10_000) RecordRtt(known, rtt);
            }
            return; // liveness already recorded above
        }

        if (type == TCtrlSeg)
        {
            // Only endpoints someone explicitly opened a stream for get their segments
            // delivered; anything else is dropped before any state is allocated.
            if (_controlStreams.TryGetValue(known, out var stream))
            {
                var seg = new byte[n - HeaderSize];
                Buffer.BlockCopy(buffer, HeaderSize, seg, 0, seg.Length);
                stream.OnDatagram(seg);
            }
            return;
        }

        if (type != TInput) return; // unknown type from a future build — ignore
        var payload = new byte[n - HeaderSize];
        Buffer.BlockCopy(buffer, HeaderSize, payload, 0, payload.Length);
        EnqueueInput(payload);
        RelayInput(buffer, n, known);
    }

    /// <summary>
    /// Hand a received input datagram to the frame loop, dropping the OLDEST if the backlog is at
    /// its cap.
    ///
    /// The queue had no cap, and the frame loop drains at most 128 datagrams per pump. In ordinary
    /// play that is not close: four players produce about 180 datagrams a second against a drain
    /// ceiling near 7,700, so a backlog from a long pause clears in a fraction of a second. What
    /// was missing is a ceiling at all — a machine that sleeps, a peer that floods, a session left
    /// paused — where the only bound was available memory, and the work waiting on the other side
    /// was guaranteed stale.
    ///
    /// Dropping the oldest is the right end to drop from precisely because it is stale: every
    /// datagram already carries a window of recent frames, so a newer one usually contains what an
    /// older one was carrying, and gap retransmission exists for what it does not. Dropping the
    /// newest would discard the only copy of the newest frames and then ask for them back.
    /// </summary>
    private void EnqueueInput(byte[] payload)
    {
        _inbound.Enqueue(payload);
        int depth = Interlocked.Increment(ref _inboundDepth);
        if (depth > _inboundPeak) _inboundPeak = depth;   // stat only; a torn read costs nothing

        while (depth > MaxInboundBacklog && _inbound.TryDequeue(out _))
        {
            depth = Interlocked.Decrement(ref _inboundDepth);
            Interlocked.Increment(ref _inboundDropped);
        }
    }

    private static byte[] Frame(byte type, byte[] payload)
    {
        var framed = new byte[HeaderSize + payload.Length];
        Buffer.BlockCopy(Magic, 0, framed, 0, 4);
        framed[4] = Version;
        framed[5] = type;
        Buffer.BlockCopy(payload, 0, framed, HeaderSize, payload.Length);
        return framed;
    }

    /// <summary>
    /// Echo one peer's input datagram to the peers that cannot receive it directly.
    ///
    /// Only the host ever has relay routes, and the reason this needs no new wire format is that an
    /// input datagram already names its own source: the port is byte [1] of the payload, so whoever
    /// receives it attributes the input to that port regardless of which endpoint it arrived from.
    /// The bytes are forwarded verbatim, envelope and all — nothing here parses the payload, and a
    /// duplicate arriving alongside a direct copy is harmless, since input is keyed by (port, frame).
    ///
    /// A relayed leg costs one extra hop of latency and some host uplink. It is only ever installed
    /// for the joiner-to-joiner edges that failed to open, which in practice means a peer behind a
    /// symmetric NAT — the case where no amount of punching can produce a direct path.
    /// </summary>
    /// <summary>
    /// Forward one peer's datagram to the peers whose direct legs to it never opened.
    ///
    /// Who NOT to send to is read out of the payload, not inferred from the source address. The
    /// input codec puts its own type at payload byte 0 and a port at byte 1, and that port is
    /// authoritative: for input it is the author's seat, for a gap request it is the seat being
    /// asked. Matching on the source ENDPOINT instead — which this used to do — silently failed for
    /// exactly the peers the relay exists for. A symmetric-NAT peer is recognised at a learned
    /// address that by construction appears in no route's candidate list, so the sender test never
    /// matched and the host sent every such peer its own input straight back: doubled traffic on
    /// the weakest link in the session, and halved headroom in the receiver's drain budget.
    ///
    /// No re-relay loop is possible: a forwarded datagram still carries its ORIGINAL author's port,
    /// so no node can bounce it back to the author, and only the host ever holds relay routes.
    /// </summary>
    private void RelayInput(byte[] buffer, int n, IPEndPoint from)
    {
        if (_relayRoutes.Length == 0) return;
        if (n < HeaderSize + 2) return;          // too short to carry the codec's type and port
        byte payloadType = buffer[HeaderSize];
        byte payloadPort = buffer[HeaderSize + 1];
        long now = Clock.ElapsedMilliseconds;

        // A gap request is ADDRESSED: exactly one peer owns the input it asks for, and that peer
        // need not itself be relayed for the requester to be unable to reach it. Forwarding it to
        // the relay set — as the input path does — meant a request from the one relayed peer went
        // only back toward itself, and the peer that could have answered never heard it. The
        // requester then reported a hole retransmission could not repair, and the session ended
        // eight seconds later blaming mismatched builds.
        if (payloadType == RelayedRequestType)
        {
            foreach (var route in _routeTable.Routes)
            {
                if (route.RemotePort != payloadPort) continue;
                var target = SelectSendCandidate(route, now);
                if (target != null) SendFramed(buffer, 0, n, target);
                return;
            }
            return;
        }
        if (payloadType != RelayedInputType) return;   // not something we know how to address

        foreach (var route in _relayRoutes)
        {
            if (route.RemotePort == payloadPort) continue;  // never bounce input back to its author
            var endpoint = SelectSendCandidate(route, now);
            if (endpoint == null) continue; // no live path to relay over yet; direct copies still flow
            SendFramed(buffer, 0, n, endpoint);
        }
    }

    // The input codec's payload type byte, mirrored here so the relay can address a datagram
    // without decoding it. Kept in step with InputPacketCodec's own constants by the round-trip
    // test that sends a real request through a relaying host.
    private const byte RelayedInputType = 1;
    private const byte RelayedRequestType = 2;

    /// <summary>
    /// Our own membership token, announced in every <c>THello</c> so peers can recognise us at
    /// whatever address their side of the network actually sees. Distributed over the authenticated
    /// control channel, so holding one is proof of belonging to this session.
    /// </summary>
    public void SetLocalToken(byte[]? token) =>
        _localToken = token is { Length: TokenBytes } ? (byte[])token.Clone() : null;

    /// <summary>The tokens we will accept, by the controller port that owns each. A peer presenting
    /// one of these is telling us where it really is; anything else stays unroutable.</summary>
    public void SetPeerTokens(IEnumerable<KeyValuePair<int, byte[]>>? tokens)
    {
        _peerTokens.Clear();
        if (tokens == null) return;
        foreach (var kv in tokens)
            if (kv.Value is { Length: TokenBytes }) _peerTokens[kv.Key] = (byte[])kv.Value.Clone();
    }

    /// <summary>Adopt a whole mesh identity at once — who we announce ourselves as and who we will
    /// accept. The two always arrive together from the control channel, so they are applied together.</summary>
    public void ApplyTokens(MeshTokens? tokens)
    {
        SetLocalToken(tokens?.Local);
        SetPeerTokens(tokens?.Peers);
    }

    /// <summary>Endpoints learned from a token that no advertised candidate matched — i.e. peers
    /// whose real address only became knowable by being told. Zero on a well-behaved network.</summary>
    public int LearnedEndpointCount => _learnedByPort.Count;

    /// <summary>The address a peer was learned at, for the session log.</summary>
    public bool TryGetLearnedEndpoint(int remotePort, out IPEndPoint endpoint) =>
        _learnedByPort.TryGetValue(remotePort, out endpoint!);

    /// <summary>
    /// Accept an endpoint nobody advertised, on the strength of a token.
    ///
    /// This is the whole symmetric-NAT fix. Such a router assigns a fresh public port per
    /// DESTINATION, so the address a peer discovered by asking a STUN server is not the address it
    /// reaches us from — and pinning on advertised endpoints alone meant we dropped its packets
    /// unread, forever, on a path that was physically working. The peer cannot know its own
    /// destination-specific port either; only we can see it, which is why it has to be learned here
    /// rather than announced.
    ///
    /// The token is what makes that safe: it is 16 random bytes handed out over the authenticated
    /// control channel, so an off-path attacker guessing one is the same problem as guessing the
    /// session password. Compared in constant time, and a peer may migrate — a NAT rebinding
    /// mid-session is the same event as the first arrival.
    /// </summary>
    private bool LearnFromHello(byte[] buffer, int n, IPEndPoint source)
    {
        if (n < HeaderSize + TokenBytes) return false;
        foreach (var kv in _peerTokens)
        {
            if (!ConstantTimeEquals(kv.Value, buffer, HeaderSize)) continue;

            int port = kv.Key;
            long now = Clock.ElapsedMilliseconds;
            if (_learnedByPort.TryGetValue(port, out var previous))
            {
                if (previous.Equals(source)) return true; // already known, nothing to record
                // A binding that is currently answering is not replaced. Every peer holds every
                // seat's token — that is what makes a rejoin on a new address recognisable — so
                // without this any session member could point another member's seat at an address
                // of its choosing and take them off the mesh. A NAT rebinding, the case this whole
                // path exists for, arrives at a seat that has just gone quiet, which still rebinds.
                if (_alive.TryGetValue(previous, out var heard) && now - heard < FreshWindowMs)
                    return false;
                _learnedByEndpoint.TryRemove(previous, out _);
                _learnedAt.TryRemove(previous, out _);
                _alive.TryRemove(previous, out _);
            }
            _learnedByPort[port] = source;
            _learnedByEndpoint[source] = port;
            _learnedAt[source] = now;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Forget learned endpoints that have never answered a probe.
    ///
    /// Learning is now a claim rather than a conclusion, so something has to retire the claims that
    /// were never true. Without this, one spoofed THello would have this node probing the named
    /// address for the rest of the session — a slow trickle rather than the input stream it used to
    /// turn on, but still traffic aimed at a stranger by a stranger. A genuine peer answers within a
    /// round trip of the next 250ms punch, and one dropped here is re-learned by its next THello.
    /// </summary>
    private void PruneUnprovenLearned(long now)
    {
        if (_learnedByPort.IsEmpty) return;
        foreach (var kv in _learnedByPort)
        {
            var endpoint = kv.Value;
            if (_alive.ContainsKey(endpoint)) continue;               // it proved itself
            if (!_learnedAt.TryGetValue(endpoint, out long at)) continue;
            if (now - at < UnprovenLearnExpiryMs) continue;
            // Racing a fresh learn for the same seat costs at most one re-learn on the next THello,
            // which is 250ms away, so this stays a plain remove rather than a compare-and-swap.
            _learnedByPort.TryRemove(kv.Key, out _);
            _learnedByEndpoint.TryRemove(endpoint, out _);
            _learnedAt.TryRemove(endpoint, out _);
        }
    }

    private static bool ConstantTimeEquals(byte[] expected, byte[] buffer, int offset)
    {
        int diff = 0;
        for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ buffer[offset + i];
        return diff == 0;
    }

    private void SendFramed(byte[] framed, IPEndPoint to) => SendFramed(framed, 0, framed.Length, to);

    /// <summary>
    /// Send an already-framed datagram from within a larger buffer — which is what the relay has,
    /// so it no longer copies each datagram out just to hand it over.
    ///
    /// Catches everything. This is a best-effort unreliable send and there is no failure it should
    /// propagate: it runs on the receive thread (relay) and on the UI thread inside the frame
    /// callback (input), and an escape on either is worse than a dropped datagram. Notably a
    /// candidate of the wrong address family raises ArgumentException rather than SocketException,
    /// which the previous two-type catch would have let through.
    /// </summary>
    private void SendFramed(byte[] framed, int offset, int count, IPEndPoint to)
    {
        try { _socket.SendTo(framed, offset, count, SocketFlags.None, to); }
        catch { /* transient, disposed, or an unroutable candidate — the channel tolerates loss */ }
    }

    public void Dispose()
    {
        _running = false;
        foreach (var stream in _controlStreams.Values)
        {
            try { stream.Dispose(); } catch { /* ignore */ }
        }
        _controlStreams.Clear();
        try { _socket.Dispose(); } catch { /* ignore */ }
        try { if (_rxThread.IsAlive) _rxThread.Join(500); } catch { /* ignore */ }
        try { if (_punchThread.IsAlive) _punchThread.Join(500); } catch { /* ignore */ }
        try { _stunEvent.Dispose(); } catch { /* ignore */ }
    }
}
