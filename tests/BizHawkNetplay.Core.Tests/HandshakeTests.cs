using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// Runs the full handshake over a real localhost TCP connection: host and client on separate
    /// threads exchange identities, negotiate, and transfer initial state through the framed
    /// control channel.
    /// </summary>
    public class HandshakeTests
    {
        private static PeerIdentity Id(string rom = "ROMHASH", int depth = 20) =>
            new PeerIdentity(1, rom, "GPGX", "2.11.1.0", "SYNC1", new[] { "L0", "L1" }, true, depth);

        private static (ControlChannel host, ControlChannel client, Action dispose) TcpPair()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var clientTcp = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync();
            clientTcp.Connect(IPAddress.Loopback, port);
            var hostTcp = acceptTask.GetAwaiter().GetResult();
            listener.Stop();

            var host = new ControlChannel(hostTcp.GetStream());
            var client = new ControlChannel(clientTcp.GetStream());
            return (host, client, () => { hostTcp.Close(); clientTcp.Close(); });
        }

        [Fact]
        public void SuccessfulHandshake_AgreesParamsAndTransfersState()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                var hostState = new byte[50_000];
                new Random(1234).NextBytes(hostState);

                var hostTask = Task.Run(() =>
                    Handshake.RunHost(hostCh, Id(), new SessionPreferences(3, wantRollback: true), hostState, 47800));
                var clientParams = Handshake.RunClient(clientCh, Id(), new SessionPreferences(2, wantRollback: true), 51000);
                var hostParams = hostTask.GetAwaiter().GetResult();

                // Both sides derived the same negotiated parameters.
                Assert.Equal(SyncMode.Rollback, hostParams.Mode);
                Assert.Equal(SyncMode.Rollback, clientParams.Mode);
                Assert.Equal(3, hostParams.InputDelay);   // max(3,2)
                Assert.Equal(3, clientParams.InputDelay);

                // Port roles.
                Assert.Equal(0, hostParams.LocalPort);
                Assert.Equal(1, hostParams.RemotePort);
                Assert.Equal(1, clientParams.LocalPort);
                Assert.Equal(0, clientParams.RemotePort);

                // State transferred byte-for-byte to the client; host keeps its own.
                Assert.Null(hostParams.InitialState);
                Assert.Equal(hostState, clientParams.InitialState);

                // UDP ports were exchanged so each side knows where to send inputs.
                Assert.Equal(51000, hostParams.RemoteUdpPort);
                Assert.Equal(47800, clientParams.RemoteUdpPort);
            }
            finally { dispose(); }
        }

        [Fact]
        public void RomMismatch_RejectsBothSides()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                var hostTask = Task.Run(() =>
                    Handshake.RunHost(hostCh, Id(rom: "HOSTROM"), new SessionPreferences(2, false), new byte[10], 47800));

                var clientEx = Assert.Throws<HandshakeException>(() =>
                    Handshake.RunClient(clientCh, Id(rom: "CLIENTROM"), new SessionPreferences(2, false), 51000));
                var hostEx = Assert.Throws<HandshakeException>(() => hostTask.GetAwaiter().GetResult());

                Assert.Contains("ROM", clientEx.Message);
                Assert.Contains("ROM", hostEx.Message);
            }
            finally { dispose(); }
        }

        [Fact]
        public void MultiPlayerHandshake_AssignsPortsAndAuthoritativeDelay()
        {
            var (hostCh1, clientCh1, d1) = TcpPair();
            var (hostCh2, clientCh2, d2) = TcpPair();
            try
            {
                var hostState = new byte[2000];
                new Random(99).NextBytes(hostState);

                // Both joiners run their client handshakes concurrently while the host greets each.
                var c1 = Task.Run(() => Handshake.RunClientMulti(clientCh1, Id(), new SessionPreferences(2, false), 51001));
                var c2 = Task.Run(() => Handshake.RunClientMulti(clientCh2, Id(), new SessionPreferences(5, false), 51002));

                var hostPrefs = new SessionPreferences(3, false);
                var g1 = Handshake.HostGreet(hostCh1, Id(), hostPrefs, 47800);
                var g2 = Handshake.HostGreet(hostCh2, Id(), hostPrefs, 47800);

                // Authoritative delay is the max over everyone: max(3, 2, 5) = 5.
                int delay = Math.Max(hostPrefs.InputDelay, Math.Max(g1.Prefs.InputDelay, g2.Prefs.InputDelay));
                const int players = 3;
                // Each joiner is told the OTHER joiner's UDP endpoint for the direct mesh.
                var j1Ep = new IPEndPoint(IPAddress.Loopback, g1.UdpPort);
                var j2Ep = new IPEndPoint(IPAddress.Loopback, g2.UdpPort);
                Handshake.HostSendWelcome(hostCh1, 1, players, delay, SyncMode.Lockstep, hostState, new[] { j2Ep });
                Handshake.HostSendWelcome(hostCh2, 2, players, delay, SyncMode.Lockstep, hostState, new[] { j1Ep });

                var p1 = c1.GetAwaiter().GetResult();
                var p2 = c2.GetAwaiter().GetResult();

                Assert.Equal(51001, g1.UdpPort);
                Assert.Equal(51002, g2.UdpPort);

                // Mesh endpoints reached each joiner: P1 learns P2's, P2 learns P1's.
                Assert.Equal(new[] { j2Ep }, p1.MeshPeers);
                Assert.Equal(new[] { j1Ep }, p2.MeshPeers);

                Assert.Equal(1, p1.LocalPort);
                Assert.Equal(2, p2.LocalPort);
                Assert.Equal(3, p1.PlayerCount);
                Assert.Equal(3, p2.PlayerCount);
                Assert.Equal(5, p1.InputDelay);
                Assert.Equal(5, p2.InputDelay);
                Assert.Equal(hostState, p1.InitialState);
                Assert.Equal(hostState, p2.InitialState);
                Assert.Equal(47800, p1.RemoteUdpPort);
                Assert.Equal(47800, p2.RemoteUdpPort);
            }
            finally { d1(); d2(); }
        }

        [Fact]
        public void RollbackDowngrades_WhenClientShallow()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                var hostTask = Task.Run(() =>
                    Handshake.RunHost(hostCh, Id(depth: 30), new SessionPreferences(2, wantRollback: true), new byte[10], 47800));
                var clientParams = Handshake.RunClient(clientCh, Id(depth: 2), new SessionPreferences(2, wantRollback: true), 51000);
                var hostParams = hostTask.GetAwaiter().GetResult();

                Assert.Equal(SyncMode.Lockstep, hostParams.Mode);
                Assert.Equal(SyncMode.Lockstep, clientParams.Mode);
            }
            finally { dispose(); }
        }
    }
}
