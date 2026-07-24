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
    /// Full-mesh UDP transport for the input hot path. Unlike <see cref="RelayUdpTransport"/> (a star
    /// where the host forwards each peer's datagram to the others, two hops peer-to-peer), every peer
    /// here sends its own input directly to every other peer — one hop, which is what keeps rollbacks
    /// shallow with 3–4 players. It is the <see cref="ITransport"/> both the host and the joiners use;
    /// the control channel stays a star (host-coordinated), and this only carries input.
    ///
    /// <see cref="Send"/> transmits to every known peer; the receive loop accepts datagrams only from a
    /// known peer endpoint (pinning out trivially spoofed off-path input) and queues them for the local
    /// FrameDriver — it never forwards. For a 2-player session the peer set is a single endpoint and
    /// this behaves exactly like point-to-point. Same <c>MAGIC + version</c> envelope as the other
    /// transports, so a foreign or wrong-version packet is dropped before the codec sees it.
    /// </summary>
    public sealed class MeshUdpTransport : ITransport, IDisposable
    {
        private static readonly byte[] Magic = { (byte)'B', (byte)'H', (byte)'N', (byte)'P' };
        private const byte Version = 1;
        private const int HeaderSize = 5; // MAGIC(4) + version(1)

        private readonly Socket _socket;
        private readonly ConcurrentQueue<byte[]> _inbound = new ConcurrentQueue<byte[]>();
        private readonly Thread _rxThread;
        private volatile bool _running = true;
        private volatile IPEndPoint[] _peers = Array.Empty<IPEndPoint>();

        private MeshUdpTransport(int localPort)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "BizHawkNetplay-UDP-mesh" };
            _rxThread.Start();
        }

        /// <summary>The local UDP port actually bound (read this when binding to port 0).</summary>
        public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint).Port;

        public static MeshUdpTransport Bind(int localPort) => new MeshUdpTransport(localPort);

        /// <summary>Set the full set of peer endpoints (IP + UDP port) to send to and accept from.</summary>
        public void SetPeers(IEnumerable<IPEndPoint> peers)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            _peers = peers.ToArray();
        }

        public void Send(byte[] datagram)
        {
            if (datagram == null) throw new ArgumentNullException(nameof(datagram));
            var framed = Frame(datagram);
            var peers = _peers;
            foreach (var p in peers) SendFramed(framed, p);
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
                bool known = false;
                for (int i = 0; i < peers.Length; i++) if (peers[i].Equals(from)) { known = true; break; }
                if (!known) continue; // pin to known peers (blocks off-path input)

                var payload = new byte[n - HeaderSize];
                Buffer.BlockCopy(buffer, HeaderSize, payload, 0, payload.Length);
                _inbound.Enqueue(payload);
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
