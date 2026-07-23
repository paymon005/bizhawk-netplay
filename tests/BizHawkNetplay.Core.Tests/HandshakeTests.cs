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
                    Handshake.RunHost(hostCh, Id(), new SessionPreferences(3, wantRollback: true), hostState));
                var clientParams = Handshake.RunClient(clientCh, Id(), new SessionPreferences(2, wantRollback: true));
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
                    Handshake.RunHost(hostCh, Id(rom: "HOSTROM"), new SessionPreferences(2, false), new byte[10]));

                var clientEx = Assert.Throws<HandshakeException>(() =>
                    Handshake.RunClient(clientCh, Id(rom: "CLIENTROM"), new SessionPreferences(2, false)));
                var hostEx = Assert.Throws<HandshakeException>(() => hostTask.GetAwaiter().GetResult());

                Assert.Contains("ROM", clientEx.Message);
                Assert.Contains("ROM", hostEx.Message);
            }
            finally { dispose(); }
        }

        [Fact]
        public void RollbackDowngrades_WhenClientShallow()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                var hostTask = Task.Run(() =>
                    Handshake.RunHost(hostCh, Id(depth: 30), new SessionPreferences(2, wantRollback: true), new byte[10]));
                var clientParams = Handshake.RunClient(clientCh, Id(depth: 2), new SessionPreferences(2, wantRollback: true));
                var hostParams = hostTask.GetAwaiter().GetResult();

                Assert.Equal(SyncMode.Lockstep, hostParams.Mode);
                Assert.Equal(SyncMode.Lockstep, clientParams.Mode);
            }
            finally { dispose(); }
        }
    }
}
