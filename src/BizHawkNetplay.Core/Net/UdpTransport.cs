using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BizHawkNetplay.Core.Net
{
    /// <summary>
    /// Real UDP implementation of <see cref="ITransport"/> for the unreliable input channel.
    /// A background thread does the only blocking socket I/O and drops each received payload into
    /// a lock-free queue that the FrameDriver drains at the top of a frame — the single
    /// thread-boundary the design allows. Every datagram is wrapped in a small
    /// <c>MAGIC + version</c> envelope (adapted from RemotePlay) so foreign or wrong-version
    /// packets are rejected before they reach the codec.
    ///
    /// Once a remote is set the peer address is pinned: datagrams from any other source are
    /// ignored, which blocks trivially spoofed input from an off-path sender.
    /// </summary>
    public sealed class UdpTransport : ITransport, IDisposable
    {
        private static readonly byte[] Magic = { (byte)'B', (byte)'H', (byte)'N', (byte)'P' };
        private const byte Version = 1;
        private const int HeaderSize = 5; // MAGIC(4) + version(1)

        private readonly Socket _socket;
        private readonly ConcurrentQueue<byte[]> _inbound = new ConcurrentQueue<byte[]>();
        private readonly Thread _rxThread;
        private volatile bool _running = true;

        private volatile EndPoint? _remote;

        private UdpTransport(int localPort)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "BizHawkNetplay-UDP-rx" };
            _rxThread.Start();
        }

        /// <summary>The local UDP port actually bound (read this when binding to port 0).</summary>
        public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint).Port;

        /// <summary>Bind a local UDP port (0 = ephemeral). Set the peer with <see cref="SetRemote"/>.</summary>
        public static UdpTransport Bind(int localPort) => new UdpTransport(localPort);

        /// <summary>Bind and target a known peer in one step (direct-IP host/join).</summary>
        public static UdpTransport Create(int localPort, IPEndPoint remote)
        {
            var t = new UdpTransport(localPort);
            t.SetRemote(remote);
            return t;
        }

        /// <summary>Point outbound sends at the peer and pin inbound to its exact endpoint (ip:port).</summary>
        public void SetRemote(IPEndPoint remote)
        {
            _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        }

        public void Send(byte[] datagram)
        {
            if (datagram == null) throw new ArgumentNullException(nameof(datagram));
            var remote = _remote;
            if (remote == null) throw new InvalidOperationException("Remote peer not set");
            var framed = new byte[HeaderSize + datagram.Length];
            Buffer.BlockCopy(Magic, 0, framed, 0, 4);
            framed[4] = Version;
            Buffer.BlockCopy(datagram, 0, framed, HeaderSize, datagram.Length);
            try { _socket.SendTo(framed, remote); }
            catch (SocketException) { /* transient; the input channel tolerates loss */ }
            catch (ObjectDisposedException) { /* shutting down */ }
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

                var pin = _remote;
                if (pin != null && !from.Equals(pin)) continue; // pin to the peer's exact ip:port

                var payload = new byte[n - HeaderSize];
                Buffer.BlockCopy(buffer, HeaderSize, payload, 0, payload.Length);
                _inbound.Enqueue(payload);
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _socket.Dispose(); } catch { /* ignore */ }
            try { if (_rxThread.IsAlive) _rxThread.Join(500); } catch { /* ignore */ }
        }
    }
}
