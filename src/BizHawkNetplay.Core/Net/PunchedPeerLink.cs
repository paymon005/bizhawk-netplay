using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// A single NAT-punched UDP link to one peer — the transport behind the "UDP Punch" join path,
    /// modelled on RemotePlay: two peers behind NAT swap STUN-discovered "connect codes" out of band,
    /// then punch outbound toward each other so both routers open a mapping and a direct datagram path
    /// forms with no port-forwarding. One socket carries everything, demultiplexed by a type byte:
    /// <list type="bullet">
    ///   <item><description><b>INPUT</b> — the unreliable input hot path, exposed via <see cref="ITransport"/>.</description></item>
    ///   <item><description><b>CTRL</b> — the reliable control stream (<see cref="ReliableUdpStream"/>), so the
    ///     existing handshake / state transfer / checksums run unchanged over <see cref="Control"/>.</description></item>
    ///   <item><description><b>PUNCH</b> — hole-punch probes exchanged until the path opens.</description></item>
    /// </list>
    /// Two-player only: manual code exchange doesn't scale to a full mesh. On symmetric NAT the peer's
    /// source port won't match the code, so this adopts the address a punch actually arrives from as the
    /// confirmed peer — which rescues port-restricted cones but still can't beat true symmetric NAT.
    /// </summary>
    public sealed class PunchedPeerLink : ITransport, IDisposable
    {
        private static readonly byte[] Magic = { (byte)'B', (byte)'H', (byte)'N', (byte)'P' };
        private const byte Version = 1;
        private const int HeaderSize = 6; // MAGIC(4) + version(1) + type(1)

        private const byte TInput = 0x10;
        private const byte TCtrl = 0x20;
        private const byte TPunch = 0x30;
        private const byte TPunchAck = 0x31;

        private readonly Socket _socket;
        private readonly ConcurrentQueue<byte[]> _inbound = new ConcurrentQueue<byte[]>();
        private readonly ReliableUdpStream _control;
        private readonly Thread _rxThread;
        private volatile bool _running = true;

        private volatile IPEndPoint? _peer;        // confirmed peer (send target + accept filter)
        private readonly ManualResetEventSlim _connected = new ManualResetEventSlim(false);

        // STUN reflexive discovery on this same socket (so the reported port is the one peers reach).
        private volatile byte[]? _pendingStunTxn;
        private volatile IPEndPoint? _reflexive;
        private readonly ManualResetEventSlim _stunEvent = new ManualResetEventSlim(false);

        private PunchedPeerLink(int localPort)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
            _control = new ReliableUdpStream(SendCtrl);
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "BizHawkNetplay-punch" };
            _rxThread.Start();
        }

        public static PunchedPeerLink Bind(int localPort = 0) => new PunchedPeerLink(localPort);

        /// <summary>The local UDP port actually bound.</summary>
        public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint).Port;

        /// <summary>The confirmed peer endpoint once the punch succeeds (null before then).</summary>
        public IPEndPoint? PeerEndpoint => _peer;

        /// <summary>The reliable control stream — wrap in a ControlChannel to run the handshake over UDP.</summary>
        public Stream Control => _control;

        // ---------------------------------------------------------------- STUN

        /// <summary>Discover this socket's public (reflexive) endpoint — the value shown as our connect code.</summary>
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

        // ---------------------------------------------------------------- punch

        /// <summary>
        /// Punch toward the peer's connect-code endpoint until a punch arrives back (proving the path is
        /// open) or the timeout elapses. Returns true and pins <see cref="PeerEndpoint"/> on success.
        /// </summary>
        public bool Punch(IPEndPoint peerHint, TimeSpan timeout)
        {
            if (peerHint == null) throw new ArgumentNullException(nameof(peerHint));
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var probe = Frame(TPunch, Array.Empty<byte>());
            while (elapsed.Elapsed < timeout && _running)
            {
                if (_peer == null) { try { _socket.SendTo(probe, peerHint); } catch { } }
                if (_connected.Wait(250)) return true;
            }
            return _peer != null;
        }

        /// <summary>
        /// Passively wait for any peer's punch to confirm the path, sending no probes — the
        /// listening half of the asymmetric flow, where the other side (which has our code) does
        /// the active punching. Useful when this side is reachable (forwarded, UPnP, or friendly
        /// NAT): the session forms without this side ever needing the peer's code.
        /// </summary>
        public bool WaitForPunch(TimeSpan timeout)
        {
            if (_peer != null) return true;
            try { return _connected.Wait(timeout) || _peer != null; }
            catch (ObjectDisposedException) { return _peer != null; }
        }

        // ---------------------------------------------------------------- ITransport (input)

        public void Send(byte[] datagram)
        {
            if (datagram == null) throw new ArgumentNullException(nameof(datagram));
            var peer = _peer;
            if (peer == null) return;
            SendFramed(Frame(TInput, datagram), peer);
        }

        public bool TryReceive(out byte[] datagram) => _inbound.TryDequeue(out datagram!);

        private void SendCtrl(byte[] segment)
        {
            var peer = _peer;
            if (peer == null) return; // nothing is written to Control until after the punch confirms a peer
            SendFramed(Frame(TCtrl, segment), peer);
        }

        // ---------------------------------------------------------------- receive loop

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

                // A STUN response (not MAGIC-framed) while a discovery is in flight.
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
                var src = (IPEndPoint)from;

                if (type == TPunch || type == TPunchAck)
                {
                    // Adopt the address the punch actually arrives from as the confirmed peer (survives a
                    // NAT that maps a different port than the code predicted), then answer so the peer
                    // also transitions. First confirmation releases anyone waiting in Punch().
                    if (_peer == null) _peer = src;
                    if (type == TPunch) { try { _socket.SendTo(Frame(TPunchAck, Array.Empty<byte>()), src); } catch { } }
                    _connected.Set();
                    continue;
                }

                // Data channels are pinned to the confirmed peer (drops off-path/spoofed input).
                var peer = _peer;
                if (peer == null || !peer.Equals(src)) continue;

                var payload = new byte[n - HeaderSize];
                Buffer.BlockCopy(buffer, HeaderSize, payload, 0, payload.Length);
                if (type == TInput) _inbound.Enqueue(payload);
                else if (type == TCtrl) _control.OnDatagram(payload);
            }
        }

        // ---------------------------------------------------------------- helpers

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
            catch (SocketException) { /* transient; input tolerates loss, control retransmits */ }
            catch (ObjectDisposedException) { /* shutting down */ }
        }

        public void Dispose()
        {
            _running = false;
            try { _control.Dispose(); } catch { }
            try { _socket.Dispose(); } catch { }
            try { if (_rxThread.IsAlive) _rxThread.Join(500); } catch { }
            try { _connected.Dispose(); } catch { }
            try { _stunEvent.Dispose(); } catch { }
        }
    }
}
