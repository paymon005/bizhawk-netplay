using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BizHawkNetplay.Core.Net
{
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
        private static readonly byte[] Magic = { (byte)'B', (byte)'H', (byte)'N', (byte)'P' };
        private const byte Version = 2; // bumped: datagrams now carry a type byte (input vs punch)
        private const int HeaderSize = 6; // MAGIC(4) + version(1) + type(1)

        private const byte TInput = 0x10;
        private const byte TPunch = 0x30;
        private const byte TPunchAck = 0x31;

        private const int PunchTickMs = 250;     // probe cadence while a candidate is unconfirmed
        private const int KeepaliveMs = 1000;    // re-probe cadence once a candidate is alive (holds the NAT mapping)
        private const int AliveWindowMs = 8000;  // no traffic for this long => the path is considered down again
        // Send-path selection is stricter than plain liveness: with keepalive acks arriving at least
        // every ~1.25s on a healthy path (and input at frame rate on the active one), a candidate not
        // heard from in this long has very likely died — fail input over to a sibling that is still
        // answering instead of waiting out the full alive window on a black hole.
        private const int FreshWindowMs = 2500;

        private readonly Socket _socket;
        private readonly ConcurrentQueue<byte[]> _inbound = new ConcurrentQueue<byte[]>();
        private readonly Thread _rxThread;
        private readonly Thread _punchThread;
        private volatile bool _running = true;
        private volatile RouteTable _routeTable = RouteTable.Empty;

        /// <summary>An immutable routing snapshot, atomically replaced when rendezvous data changes.</summary>
        private sealed class RouteTable
        {
            public static readonly RouteTable Empty =
                new RouteTable(Array.Empty<PeerRoute>(), Array.Empty<IPEndPoint>());

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

        // Per-candidate liveness: endpoint -> last time we heard anything back from it (stopwatch ms).
        private readonly ConcurrentDictionary<IPEndPoint, long> _alive = new ConcurrentDictionary<IPEndPoint, long>();
        private readonly ConcurrentDictionary<IPEndPoint, long> _lastPunch = new ConcurrentDictionary<IPEndPoint, long>();
        private readonly ConcurrentDictionary<IPEndPoint, double> _rtt = new ConcurrentDictionary<IPEndPoint, double>();
        // Last candidate input was actually sent through, per logical peer — the failover anchor
        // while a repunch has the liveness table cleared.
        private readonly ConcurrentDictionary<int, IPEndPoint> _lastSelected = new ConcurrentDictionary<int, IPEndPoint>();
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        // Reflexive-address discovery: while a request is pending, the receive loop watches for the STUN
        // response on this same socket (so the reflexive port is the one the mesh actually uses).
        private volatile byte[]? _pendingStunTxn;
        private volatile IPEndPoint? _reflexive;
        private readonly ManualResetEventSlim _stunEvent = new ManualResetEventSlim(false);

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

        public static MeshUdpTransport Bind(int localPort) => new MeshUdpTransport(localPort);

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

            _routeTable = new RouteTable(normalized, endpoints.ToArray());

            // Forget telemetry for candidates no longer in the set (a rejoin can change addresses).
            var keep = new HashSet<IPEndPoint>(endpoints);
            foreach (var k in _alive.Keys.ToArray()) if (!keep.Contains(k)) _alive.TryRemove(k, out _);
            foreach (var k in _lastPunch.Keys.ToArray()) if (!keep.Contains(k)) _lastPunch.TryRemove(k, out _);
            foreach (var k in _rtt.Keys.ToArray()) if (!keep.Contains(k)) _rtt.TryRemove(k, out _);
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
                if (endpoint != null) SendFramed(framed, endpoint);
            }
        }

        public bool TryReceive(out byte[] datagram) => _inbound.TryDequeue(out datagram!);

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
        /// paths. Measurements for dead/stale candidates are deliberately excluded.
        /// </summary>
        public bool TryGetWorstRttMs(out double rttMs)
        {
            rttMs = -1;
            long now = Clock.ElapsedMilliseconds;
            foreach (var route in _routeTable.Routes)
            {
                // A partial maximum is dangerously optimistic: one fast measured player must not
                // hide another logical peer whose UDP path has no live RTT yet. Let the caller fall
                // back to its complete TCP-per-peer sample set until every route is represented.
                if (route.Candidates.Count == 0 || !TryGetBestLiveRtt(route, now, out double peerRtt))
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

            // Before punching has ever confirmed anything, preserve deterministic forward progress
            // through the first advertised candidate.
            return route.Candidates.Count > 0 ? route.Candidates[0] : null;
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

        /// <summary>Snapshot of the candidate endpoints with a currently-open direct path.</summary>
        public IReadOnlyList<IPEndPoint> AliveEndpoints()
        {
            long now = Clock.ElapsedMilliseconds;
            return _routeTable.Endpoints.Where(endpoint => IsEndpointAlive(endpoint, now)).ToArray();
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
                try
                {
                    long now = Clock.ElapsedMilliseconds;
                    var endpoints = _routeTable.Endpoints;
                    foreach (var p in endpoints)
                    {
                        bool alive = IsEndpointAlive(p);
                        // Probe aggressively until confirmed, then just often enough to hold the mapping.
                        int due = alive ? KeepaliveMs : PunchTickMs;
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
                Thread.Sleep(PunchTickMs);
            }
        }

        private void ReceiveLoop()
        {
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
                    var echo = n >= HeaderSize + 8 ? new byte[8] : Array.Empty<byte>();
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

                // TInput
                var payload = new byte[n - HeaderSize];
                Buffer.BlockCopy(buffer, HeaderSize, payload, 0, payload.Length);
                _inbound.Enqueue(payload);
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

        private void SendFramed(byte[] framed, IPEndPoint to)
        {
            try { _socket.SendTo(framed, to); }
            catch (SocketException) { /* transient; the input channel tolerates loss */ }
            catch (ObjectDisposedException) { /* shutting down */ }
        }

        public void Dispose()
        {
            _running = false;
            try { _socket.Dispose(); } catch { /* ignore */ }
            try { if (_rxThread.IsAlive) _rxThread.Join(500); } catch { /* ignore */ }
            try { if (_punchThread.IsAlive) _punchThread.Join(500); } catch { /* ignore */ }
            try { _stunEvent.Dispose(); } catch { /* ignore */ }
        }
    }
}
