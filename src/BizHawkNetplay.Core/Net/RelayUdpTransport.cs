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
    /// The host's UDP hub for 3–4 player host-relay sessions. It is the <see cref="ITransport"/> the
    /// host's own <see cref="Sync.FrameDriver"/> uses, and simultaneously the relay that lets peers who
    /// only know the host still receive each other's inputs:
    ///
    /// - <see cref="Send"/> (the host's own port datagram) goes to every peer.
    /// - Each datagram received from a peer is (a) queued for the host's own FrameDriver to drain and
    ///   (b) forwarded verbatim to every OTHER peer — so peer A's inputs reach peers B and C without
    ///   A and B/C needing any direct connection.
    ///
    /// Non-host peers keep using the plain point-to-point <see cref="UdpTransport"/> pointed at the
    /// host; from their side the host looks like a single peer that happens to speak every port. Peers
    /// are pinned to their known endpoints, so off-path datagrams are ignored. For a 2-player session
    /// this degenerates to a single peer and behaves exactly like point-to-point.
    /// </summary>
    public sealed class RelayUdpTransport : ITransport, IDisposable
    {
        private static readonly byte[] Magic = { (byte)'B', (byte)'H', (byte)'N', (byte)'P' };
        private const byte Version = 1;
        private const int HeaderSize = 5; // MAGIC(4) + version(1)

        private readonly Socket _socket;
        private readonly ConcurrentQueue<byte[]> _inbound = new ConcurrentQueue<byte[]>();
        private readonly Thread _rxThread;
        private volatile bool _running = true;
        private volatile IPEndPoint[] _peers = Array.Empty<IPEndPoint>();

        private RelayUdpTransport(int localPort)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "BizHawkNetplay-UDP-relay" };
            _rxThread.Start();
        }

        public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint).Port;

        public static RelayUdpTransport Bind(int localPort) => new RelayUdpTransport(localPort);

        /// <summary>Set the full set of peer endpoints (IP + their UDP port) the host relays among.</summary>
        public void SetPeers(IEnumerable<IPEndPoint> peers)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            _peers = peers.ToArray();
        }

        public void Send(byte[] datagram)
        {
            if (datagram == null) throw new ArgumentNullException(nameof(datagram));
            var framed = Frame(datagram);
            foreach (var p in _peers) SendFramed(framed, p);
        }

        public bool TryReceive(out byte[] datagram) => _inbound.TryDequeue(out datagram!);

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

                if (n < HeaderSize) continue;
                if (buffer[0] != Magic[0] || buffer[1] != Magic[1] ||
                    buffer[2] != Magic[2] || buffer[3] != Magic[3]) continue;
                if (buffer[4] != Version) continue;

                var peers = _peers;
                // Accept only from a known peer (blocks trivially spoofed off-path input).
                bool known = false;
                for (int i = 0; i < peers.Length; i++) if (peers[i].Equals(from)) { known = true; break; }
                if (!known) continue;

                // (a) hand this peer's input to our own FrameDriver
                var payload = new byte[n - HeaderSize];
                Buffer.BlockCopy(buffer, HeaderSize, payload, 0, payload.Length);
                _inbound.Enqueue(payload);

                // (b) relay the framed datagram verbatim to every other peer
                var relay = new byte[n];
                Buffer.BlockCopy(buffer, 0, relay, 0, n);
                for (int i = 0; i < peers.Length; i++)
                    if (!peers[i].Equals(from)) SendFramed(relay, peers[i]);
            }
        }

        private static byte[] Frame(byte[] datagram)
        {
            var framed = new byte[HeaderSize + datagram.Length];
            Buffer.BlockCopy(Magic, 0, framed, 0, 4);
            framed[4] = Version;
            Buffer.BlockCopy(datagram, 0, framed, HeaderSize, datagram.Length);
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
        }
    }
}
