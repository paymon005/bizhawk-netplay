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
        private const int KeepaliveMs = 3000;    // re-probe cadence once a candidate is alive (holds the NAT mapping)
        private const int AliveWindowMs = 8000;  // no traffic for this long => the path is considered down again

        private readonly Socket _socket;
        private readonly ConcurrentQueue<byte[]> _inbound = new ConcurrentQueue<byte[]>();
        private readonly Thread _rxThread;
        private readonly Thread _punchThread;
        private volatile bool _running = true;
        private volatile IPEndPoint[] _peers = Array.Empty<IPEndPoint>();

        // Per-candidate liveness: endpoint -> last time we heard anything back from it (stopwatch ms).
        private readonly ConcurrentDictionary<IPEndPoint, long> _alive = new ConcurrentDictionary<IPEndPoint, long>();
        private readonly ConcurrentDictionary<IPEndPoint, long> _lastPunch = new ConcurrentDictionary<IPEndPoint, long>();
        private readonly ConcurrentDictionary<IPEndPoint, double> _rtt = new ConcurrentDictionary<IPEndPoint, double>();
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

        /// <summary>Set the full set of peer endpoints (IP + UDP port) to send to and accept from. Adding
        /// a new candidate arms the punch loop for it; dropped candidates stop being probed.</summary>
        public void SetPeers(IEnumerable<IPEndPoint> peers)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            var arr = peers.ToArray();
            _peers = arr;
            // Forget liveness for candidates no longer in the set (a rejoin can change addresses).
            var keep = new HashSet<IPEndPoint>(arr);
            foreach (var k in _alive.Keys.ToArray()) if (!keep.Contains(k)) _alive.TryRemove(k, out _);
            foreach (var k in _lastPunch.Keys.ToArray()) if (!keep.Contains(k)) _lastPunch.TryRemove(k, out _);
            foreach (var k in _rtt.Keys.ToArray()) if (!keep.Contains(k)) _rtt.TryRemove(k, out _);
        }

        public void Send(byte[] datagram)
        {
            if (datagram == null) throw new ArgumentNullException(nameof(datagram));
            var framed = Frame(TInput, datagram);
            var peers = _peers;
            foreach (var p in peers) SendFramed(framed, p);
        }

        public bool TryReceive(out byte[] datagram) => _inbound.TryDequeue(out datagram!);

        /// <summary>True if this candidate endpoint has answered a probe or sent input recently — i.e. a
        /// direct UDP path to it is currently open.</summary>
        public bool IsEndpointAlive(IPEndPoint endpoint)
            => endpoint != null && _alive.TryGetValue(endpoint, out var t) && Clock.ElapsedMilliseconds - t < AliveWindowMs;

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

        /// <summary>The worst measured RTT over the endpoints with a live path; false if none measured.</summary>
        public bool TryGetWorstRttMs(out double rttMs)
        {
            rttMs = -1;
            foreach (var p in _peers)
                if (_rtt.TryGetValue(p, out double v) && v > rttMs) rttMs = v;
            return rttMs >= 0;
        }

        private void RecordRtt(IPEndPoint endpoint, double sample)
        {
            // Same EMA shape as the control-channel ping, so the two readings are comparable.
            _rtt.AddOrUpdate(endpoint, sample, (_, prev) => 0.8 * prev + 0.2 * sample);
        }

        /// <summary>Snapshot of the candidate endpoints with a currently-open direct path.</summary>
        public IReadOnlyList<IPEndPoint> AliveEndpoints()
        {
            long now = Clock.ElapsedMilliseconds;
            return _alive.Where(kv => now - kv.Value < AliveWindowMs).Select(kv => kv.Key).ToArray();
        }

        /// <summary>Forget the current path confirmations and make the punch loop probe every candidate
        /// immediately. Used when control traffic is healthy but input progress has gone quiet.</summary>
        public void RequestRepunch()
        {
            _alive.Clear();
            _lastPunch.Clear();
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
                var server = StunClient.ResolveV4(host, port);
                if (server == null) continue;

                var req = StunClient.BuildRequest(out var txn);
                _reflexive = null;
                _stunEvent.Reset();
                _pendingStunTxn = txn;
                try { _socket.SendTo(req, server); }
                catch { _pendingStunTxn = null; continue; }

                bool got = _stunEvent.Wait(perServer);
                _pendingStunTxn = null;
                if (got && _reflexive != null) return _reflexive;
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
                    var peers = _peers;
                    foreach (var p in peers)
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

                var peers = _peers;
                IPEndPoint? known = null;
                for (int i = 0; i < peers.Length; i++) if (peers[i].Equals(from)) { known = peers[i]; break; }
                if (known == null) continue; // pin to known peers (blocks off-path input)

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
