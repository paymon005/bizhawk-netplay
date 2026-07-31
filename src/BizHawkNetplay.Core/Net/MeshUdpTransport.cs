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
    private readonly Thread _rxThread;
    private readonly Thread _punchThread;
    private volatile bool _running = true;
    private volatile RouteTable _routeTable = RouteTable.Empty;
    // Host only: peers to echo every other peer's input to, because their direct legs never opened.
    private volatile PeerRoute[] _relayRoutes = [];

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

    private MeshUdpTransport(int localPort)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
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
    /// Compatibility API for callers that have a flat endpoint list. Every unique endpoint is treated
    /// as its own logical peer, preserving the old send-to-every-endpoint behavior.
    /// </summary>
    public void SetPeers(IEnumerable<IPEndPoint> peers)
    {
        if (peers == null) throw new ArgumentNullException(nameof(peers));
        var routes = new List<PeerRoute>();
        var seen = new HashSet<IPEndPoint>();
        int remotePort = 0;
        foreach (var endpoint in peers)
        {
            if (endpoint == null)
                throw new ArgumentException("Peer endpoints cannot contain null", nameof(peers));
            if (seen.Add(endpoint))
                routes.Add(new PeerRoute(remotePort++, new[] { endpoint }));
        }
        SetPeerRoutes(routes);
    }

    /// <summary>
    /// Replace the logical peer routes. Candidates are de-duplicated globally as well as within each
    /// route, so an endpoint advertised twice is probed and sent to only once. Repeated entries for the
    /// same remote port are merged in their original order.
    /// </summary>
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
        foreach (var k in _alive.Keys.ToArray()) if (!keep.Contains(k)) _alive.TryRemove(k, out _);
        foreach (var k in _lastPunch.Keys.ToArray()) if (!keep.Contains(k)) _lastPunch.TryRemove(k, out _);
        foreach (var k in _rtt.Keys.ToArray()) if (!keep.Contains(k)) _rtt.TryRemove(k, out _);
        foreach (var k in _rttWindows.Keys.ToArray()) if (!keep.Contains(k)) _rttWindows.TryRemove(k, out _);
        foreach (var kv in _lastSelected.ToArray()) if (!keep.Contains(kv.Value)) _lastSelected.TryRemove(kv.Key, out _);
    }

    public void Send(byte[] datagram)
    {
        if (datagram == null) throw new ArgumentNullException(nameof(datagram));
        var framed = Frame(TInput, datagram);
        var table = _routeTable;
        long now = Clock.ElapsedMilliseconds;
        foreach (var route in table.Routes)
        {
            var endpoint = SelectSendCandidate(route, now);
            if (endpoint != null)
            {
                SendFramed(framed, endpoint);
                continue;
            }
            // No path to this peer has EVER been confirmed (fresh session, punch still in
            // flight). Guessing one candidate can blackhole the whole opening of the session
            // toward a NAT'd peer — the pre-NAT candidate silently ate the first ~300ms of
            // input in the first real-internet test. Send to every candidate until the first
            // ack picks a winner.
            foreach (var candidate in route.Candidates) SendFramed(framed, candidate);
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
        var routes = _routeTable.Routes;
        totalRoutes = routes.Length;
        foreach (var route in routes)
        {
            double bestMedian = double.MaxValue, bestHigh = 0;
            bool measured = false;
            foreach (var endpoint in route.Candidates)
            {
                if (!TryGetRttStats(endpoint, out double candidateMedian, out double candidateHigh)) continue;
                if (!measured || candidateMedian < bestMedian)
                {
                    bestMedian = candidateMedian;
                    bestHigh = candidateHigh;
                }
                measured = true;
            }
            if (!measured) continue;
            measuredRoutes++;
            if (bestMedian > medianMs) medianMs = bestMedian;
            if (bestHigh > highMs) highMs = bestHigh;
        }
        return measuredRoutes > 0;
    }

    private IPEndPoint? SelectSendCandidate(PeerRoute route, long now)
    {
        IPEndPoint? firstFresh = null, bestFresh = null;
        IPEndPoint? firstLive = null, bestLive = null;
        double bestFreshRtt = double.MaxValue, bestLiveRtt = double.MaxValue;
        foreach (var endpoint in route.Candidates)
        {
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
        // does NOT work when the reflexive path was carrying the session.
        if (_lastSelected.TryGetValue(route.RemotePort, out var last) && route.Candidates.Contains(last))
            return last;

        // Nothing has ever worked for this peer — no single candidate is a safe guess. Return
        // null so the caller broadcasts to every candidate until the punch confirms one.
        return null;
    }

    private bool TryGetBestLiveRtt(PeerRoute route, long now, out double bestRtt)
    {
        bestRtt = double.MaxValue;
        bool found = false;
        foreach (var endpoint in route.Candidates)
        {
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

    /// <summary>Snapshot of the candidate endpoints with a currently-open direct path.</summary>
    public IReadOnlyList<IPEndPoint> AliveEndpoints()
    {
        long now = Clock.ElapsedMilliseconds;
        return _routeTable.Endpoints.Where(endpoint => IsEndpointAlive(endpoint, now)).ToArray();
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
        return _controlStreams.GetOrAdd(peer,
            ep => new ReliableUdpStream(seg => SendFramed(Frame(TCtrlSeg, seg), ep)));
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
    /// failover candidate while the silent peer is re-probed.</summary>
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
                var endpoints = _routeTable.Endpoints;
                foreach (var p in endpoints)
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
                        SendFramed(Frame(TPunch, BitConverter.GetBytes(now)), p);
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
        while (_running)
        {
            int n;
            try { n = _socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException) { if (!_running) break; continue; }
            catch (ObjectDisposedException) { break; }

            // While a reflexive discovery is in flight, a STUN response arrives here (it isn't
            // MAGIC-framed, so it would otherwise be dropped below). Only parsed while pending.
            var stunTxn = _pendingStunTxn;
            if (stunTxn != null && n >= 20)
            {
                var pkt = new byte[n];
                Buffer.BlockCopy(buffer, 0, pkt, 0, n);
                var refl = StunClient.ParseResponse(pkt, stunTxn);
                if (refl != null) { _reflexive = refl; _stunEvent.Set(); continue; }
            }

            if (n < HeaderSize) continue;
            if (buffer[0] != Magic[0] || buffer[1] != Magic[1] ||
                buffer[2] != Magic[2] || buffer[3] != Magic[3]) continue;
            if (buffer[4] != Version) continue;
            byte type = buffer[5];

            var source = (IPEndPoint)from;
            if (!_routeTable.TryResolve(source, out var known)) continue; // pin to known peers

            _alive[known] = Clock.ElapsedMilliseconds; // any framed packet proves the path is up

            if (type == TPunch)
            {
                // Echo the probe's timestamp back untouched so the sender can time the round trip.
                // A peer on an older build sends an empty probe and gets an empty ack — the RTT is
                // simply never measured there, and the caller falls back to the control-channel ping.
                var echo = n >= HeaderSize + 8 ? new byte[8] : [];
                if (echo.Length == 8) Buffer.BlockCopy(buffer, HeaderSize, echo, 0, 8);
                SendFramed(Frame(TPunchAck, echo), known); // answer so the peer confirms us too
                continue;
            }
            if (type == TPunchAck)
            {
                if (n >= HeaderSize + 8)
                {
                    long sentAt = BitConverter.ToInt64(buffer, HeaderSize);
                    double rtt = Clock.ElapsedMilliseconds - sentAt;
                    // Guard against a stale/garbled echo outliving a clock restart.
                    if (rtt >= 0 && rtt < 10_000) RecordRtt(known, rtt);
                }
                continue; // liveness already recorded above
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
                continue;
            }

            if (type != TInput) continue; // unknown type from a future build — ignore
            var payload = new byte[n - HeaderSize];
            Buffer.BlockCopy(buffer, HeaderSize, payload, 0, payload.Length);
            EnqueueInput(payload);
            RelayInput(buffer, n, known);
        }
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
    private void RelayInput(byte[] buffer, int n, IPEndPoint from)
    {
        var routes = _relayRoutes;
        if (routes.Length == 0) return;

        byte[]? copy = null; // only allocated when there is actually something to forward
        long now = Clock.ElapsedMilliseconds;
        foreach (var route in routes)
        {
            // Never bounce a datagram back to the peer it came from.
            bool isSender = false;
            foreach (var candidate in route.Candidates) if (candidate.Equals(from)) { isSender = true; break; }
            if (isSender) continue;

            var endpoint = SelectSendCandidate(route, now);
            if (endpoint == null) continue; // no live path to relay over yet; direct copies still flow
            copy ??= Slice(buffer, n);
            SendFramed(copy, endpoint);
        }

        static byte[] Slice(byte[] source, int length)
        {
            var slice = new byte[length];
            Buffer.BlockCopy(source, 0, slice, 0, length);
            return slice;
        }
    }

    private void SendFramed(byte[] framed, IPEndPoint to)
    {
        try { _socket.SendTo(framed, to); }
        catch (SocketException) { /* transient; the input channel tolerates loss */ }
        catch (ObjectDisposedException) { /* shutting down */ }
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
