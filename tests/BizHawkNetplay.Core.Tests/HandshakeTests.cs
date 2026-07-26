using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BizHawkNetplay.Core.Net;
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

        private static SessionGeneration Generation(ulong sessionId = 0x1234UL, int epoch = 1) =>
            new SessionGeneration(sessionId, epoch);

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
                Assert.True(hostParams.Generation.IsValid);
                Assert.Equal(1, hostParams.Generation.Epoch);
                Assert.Equal(hostParams.Generation, clientParams.Generation);
            }
            finally { dispose(); }
        }

        [Fact]
        public void LobbyProbeCanRaiseDelayBeforeEitherDriverIsPrepared()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                double measuredRtt = -1;
                var hostTask = Task.Run(() => Handshake.RunHost(
                    hostCh, Id(), new SessionPreferences(1, wantRollback: true), new byte[32], 47800,
                    selectInputDelay: (channel, mode, floor) =>
                    {
                        Assert.Equal(SyncMode.Rollback, mode);
                        Assert.Equal(1, floor);
                        measuredRtt = Handshake.MeasureLobbyRoundTrip(channel, samples: 3);
                        return 4;
                    }));

                var client = Handshake.RunClient(
                    clientCh, Id(), new SessionPreferences(1, wantRollback: true), 51000);
                var host = hostTask.GetAwaiter().GetResult();

                Assert.True(measuredRtt >= 0);
                Assert.Equal(4, host.InputDelay);
                Assert.Equal(4, client.InputDelay);
            }
            finally { dispose(); }
        }

        [Fact]
        public void TwoPlayerHandshake_WaitsForPostApplyCallbackBeforeReady()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            using var callbackEntered = new ManualResetEventSlim();
            using var releaseApply = new ManualResetEventSlim();
            using var hostPrepared = new ManualResetEventSlim();
            using var releaseGo = new ManualResetEventSlim();
            var generation = Generation(0x7777UL, epoch: 3);
            var hostState = new byte[4096];
            try
            {
                var host = Task.Run(() => Handshake.RunHost(
                    hostCh, Id(), new SessionPreferences(2, false), hostState, 47800, generation,
                    beforeGo: _ =>
                    {
                        hostPrepared.Set();
                        Assert.True(releaseGo.Wait(TimeSpan.FromSeconds(5)));
                    }));
                var client = Task.Run(() => Handshake.RunClient(
                    clientCh, Id(), new SessionPreferences(2, false), 51000,
                    beforeReady: p =>
                    {
                        Assert.Equal(hostState, p.InitialState);
                        Assert.Equal(generation, p.Generation);
                        callbackEntered.Set();
                        Assert.True(releaseApply.Wait(TimeSpan.FromSeconds(5)));
                    }));

                Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(host.IsCompleted); // host cannot observe READY until application returns
                Assert.False(client.IsCompleted);

                releaseApply.Set();
                Assert.True(hostPrepared.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(client.IsCompleted); // client remains behind GO until host preparation finishes
                releaseGo.Set();
                Assert.Equal(generation, client.GetAwaiter().GetResult().Generation);
                Assert.Equal(generation, host.GetAwaiter().GetResult().Generation);
            }
            finally
            {
                releaseApply.Set();
                releaseGo.Set();
                dispose();
            }
        }

        [Fact]
        public void WelcomeCodec_RoundTripsGenerationAndGroupedRoutes()
        {
            var generation = Generation(ulong.MaxValue, epoch: 17);
            var lan = new IPEndPoint(IPAddress.Parse("192.168.1.40"), 51002);
            var reflexive = new IPEndPoint(IPAddress.Parse("2001:db8::40"), 61002);
            var body = HandshakeCodec.EncodeWelcome(assignedPort: 1, playerCount: 4, inputDelay: 5,
                SyncMode.Rollback, generation, new[]
                {
                    new PeerRoute(2, new[] { lan, reflexive }),
                    new PeerRoute(2, new[] { lan }), // repeated group/candidate is coalesced
                    new PeerRoute(3, Array.Empty<IPEndPoint>()),
                });

            var (port, players, delay, mode, decodedGeneration, routes) = HandshakeCodec.DecodeWelcome(body);
            Assert.Equal(1, port);
            Assert.Equal(4, players);
            Assert.Equal(5, delay);
            Assert.Equal(SyncMode.Rollback, mode);
            Assert.Equal(generation, decodedGeneration);
            Assert.Collection(routes,
                r =>
                {
                    Assert.Equal(2, r.RemotePort);
                    Assert.Equal(new[] { lan, reflexive }, r.Candidates);
                },
                r =>
                {
                    Assert.Equal(3, r.RemotePort);
                    Assert.Empty(r.Candidates);
                });
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
        public void MatchingPassword_CompletesHandshake()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                var hostState = new byte[1024];
                new Random(7).NextBytes(hostState);
                var hostTask = Task.Run(() =>
                    Handshake.RunHost(hostCh, Id(), new SessionPreferences(2, false, "hunter2"), hostState, 47800));
                var clientParams = Handshake.RunClient(clientCh, Id(), new SessionPreferences(2, false, "hunter2"), 51000);
                hostTask.GetAwaiter().GetResult();
                Assert.Equal(hostState, clientParams.InitialState); // password matched -> state transferred
            }
            finally { dispose(); }
        }

        [Fact]
        public void WrongPassword_RejectsBothSides_AndSendsNoState()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                var hostTask = Task.Run(() =>
                    Handshake.RunHost(hostCh, Id(), new SessionPreferences(2, false, "hunter2"), new byte[10], 47800));
                var clientEx = Assert.Throws<HandshakeException>(() =>
                    Handshake.RunClient(clientCh, Id(), new SessionPreferences(2, false, "letmein"), 51000));
                var hostEx = Assert.Throws<HandshakeException>(() => hostTask.GetAwaiter().GetResult());
                Assert.Contains("password", clientEx.Message);
                Assert.Contains("password", hostEx.Message);
            }
            finally { dispose(); }
        }

        /// <summary>
        /// A refused joiner must cost only that joiner's connection: the host greets a wrong-password
        /// attempt, it fails, and the very next connection with the right password still completes.
        /// The tool's accept loop relies on this to keep hosting through a typo'd password instead of
        /// making the host tear down and re-host.
        /// </summary>
        [Fact]
        public void WrongPasswordGreet_DoesNotPoisonTheNextJoiner()
        {
            var (badHostCh, badClientCh, d1) = TcpPair();
            var (hostCh, clientCh, d2) = TcpPair();
            try
            {
                var hostPrefs = new SessionPreferences(2, false, "hunter2");
                var hostState = new byte[1000];
                new Random(7).NextBytes(hostState);

                // First attempt: wrong password. Both ends refuse, and the host survives it.
                var badClient = Task.Run(() =>
                    Handshake.RunClientMulti(badClientCh, Id(), new SessionPreferences(2, false, "letmein"), 51001));
                var hostEx = Assert.Throws<HandshakeException>(() => Handshake.HostGreet(badHostCh, Id(), hostPrefs, 47800));
                Assert.Contains("password", hostEx.Message);
                Assert.Throws<HandshakeException>(() => badClient.GetAwaiter().GetResult());
                d1(); // the host drops just this connection

                // Second attempt on a fresh connection with the right password: fully accepted.
                var goodClient = Task.Run(() =>
                    Handshake.RunClientMulti(clientCh, Id(), new SessionPreferences(2, false, "hunter2"), 51002));
                var greet = Handshake.HostGreet(hostCh, Id(), hostPrefs, 47800);
                var generation = Generation();
                Handshake.HostSendWelcome(hostCh, 1, 2, 2, SyncMode.Lockstep, hostState, generation);
                Handshake.HostWaitReady(hostCh, generation);
                Handshake.HostSendGo(hostCh, generation);

                var p = goodClient.GetAwaiter().GetResult();
                Assert.Equal(51002, greet.UdpPort);
                Assert.Equal(1, p.LocalPort);
                Assert.Equal(hostState, p.InitialState);
            }
            finally { d1(); d2(); }
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

                // Lobby auto-selection probes every authenticated link while all joiners are still
                // waiting for WELCOME; neither client may mistake the probe for start data.
                Assert.True(Handshake.MeasureLobbyRoundTrip(hostCh1, samples: 2) >= 0);
                Assert.True(Handshake.MeasureLobbyRoundTrip(hostCh2, samples: 2) >= 0);

                // Authoritative delay is the max over everyone: max(3, 2, 5) = 5.
                int delay = Math.Max(hostPrefs.InputDelay, Math.Max(g1.Prefs.InputDelay, g2.Prefs.InputDelay));
                const int players = 3;
                // Each joiner is told the OTHER joiner's UDP endpoint for the direct mesh.
                var j1Ep = new IPEndPoint(IPAddress.Loopback, g1.UdpPort);
                var j2Ep = new IPEndPoint(IPAddress.Loopback, g2.UdpPort);
                var generation = Generation();
                Handshake.HostSendWelcome(hostCh1, 1, players, delay, SyncMode.Lockstep, hostState, generation,
                    new[] { new PeerRoute(remotePort: 2, new[] { j2Ep }) });
                Handshake.HostSendWelcome(hostCh2, 2, players, delay, SyncMode.Lockstep, hostState, generation,
                    new[] { new PeerRoute(remotePort: 1, new[] { j1Ep }) });
                Handshake.HostWaitReady(hostCh1, generation);
                Handshake.HostWaitReady(hostCh2, generation);
                Handshake.HostSendGo(hostCh1, generation);
                Handshake.HostSendGo(hostCh2, generation);

                var p1 = c1.GetAwaiter().GetResult();
                var p2 = c2.GetAwaiter().GetResult();

                Assert.Equal(51001, g1.UdpPort);
                Assert.Equal(51002, g2.UdpPort);

                // Mesh endpoints reached each joiner: P1 learns P2's, P2 learns P1's.
                Assert.Equal(new[] { j2Ep }, p1.MeshPeers);
                Assert.Equal(new[] { j1Ep }, p2.MeshPeers);
                Assert.Equal(2, Assert.Single(p1.PeerRoutes).RemotePort);
                Assert.Equal(j2Ep, Assert.Single(Assert.Single(p1.PeerRoutes).Candidates));
                Assert.Equal(1, Assert.Single(p2.PeerRoutes).RemotePort);
                Assert.Equal(j1Ep, Assert.Single(Assert.Single(p2.PeerRoutes).Candidates));
                Assert.Equal(generation, p1.Generation);
                Assert.Equal(generation, p2.Generation);

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
        public void MultiPlayerReadyBarrier_DoesNotReleaseClientBeforeGo()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                SessionParams? applied = null;
                var client = Task.Run(() => Handshake.RunClientMulti(
                    clientCh, Id(), new SessionPreferences(2, false), 51001,
                    beforeReady: p => applied = p));
                var greeting = Handshake.HostGreet(hostCh, Id(), new SessionPreferences(2, false), 47800);

                var generation = Generation();
                Handshake.HostSendWelcome(hostCh, assignedPort: 1, playerCount: 2, inputDelay: 2,
                    mode: SyncMode.Lockstep, state: new byte[1024], generation);
                Handshake.HostWaitReady(hostCh, generation);

                Assert.NotNull(applied);
                Assert.Equal(1024, applied!.InitialState!.Length);
                Assert.Equal(generation, applied.Generation);
                Assert.False(client.IsCompleted);
                Handshake.HostSendGo(hostCh, generation);
                Assert.Equal(1, client.GetAwaiter().GetResult().LocalPort);
                Assert.Equal(51001, greeting.UdpPort);
            }
            finally { dispose(); }
        }

        [Fact]
        public void HostReadyBarrier_RejectsWrongGeneration()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                var expected = Generation(0x2222UL, epoch: 4);
                clientCh.Send(ControlMessageType.Ready, HandshakeCodec.EncodeGeneration(expected.Next()));

                var ex = Assert.Throws<HandshakeException>(() => Handshake.HostWaitReady(hostCh, expected));
                Assert.Contains("READY generation mismatch", ex.Message);
            }
            finally { dispose(); }
        }

        [Fact]
        public void ClientReadyBarrier_RejectsWrongGenerationGo()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                var generation = Generation(0x3333UL, epoch: 2);
                var client = Task.Run(() => Handshake.RunClientMulti(
                    clientCh, Id(), new SessionPreferences(2, false), 51001));
                Handshake.HostGreet(hostCh, Id(), new SessionPreferences(2, false), 47800);
                Handshake.HostSendWelcome(hostCh, 1, 2, 2, SyncMode.Lockstep,
                    new byte[128], generation);
                Handshake.HostWaitReady(hostCh, generation);
                hostCh.Send(ControlMessageType.Go, HandshakeCodec.EncodeGeneration(generation.Next()));

                var ex = Assert.Throws<HandshakeException>(() => client.GetAwaiter().GetResult());
                Assert.Contains("GO generation mismatch", ex.Message);
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
                    Handshake.RunHost(hostCh, Id(depth: 30), new SessionPreferences(2, wantRollback: true), new byte[10], 47800));
                var clientParams = Handshake.RunClient(clientCh, Id(depth: 2), new SessionPreferences(2, wantRollback: true), 51000);
                var hostParams = hostTask.GetAwaiter().GetResult();

                Assert.Equal(SyncMode.Lockstep, hostParams.Mode);
                Assert.Equal(SyncMode.Lockstep, clientParams.Mode);
            }
            finally { dispose(); }
        }

        [Fact]
        public void ForcedRollback_BypassesOnlyHostProbe_NotClientCapability()
        {
            var (hostCh, clientCh, dispose) = TcpPair();
            try
            {
                var hostTask = Task.Run(() => Handshake.RunHost(
                    hostCh, Id(depth: 2), new SessionPreferences(2, wantRollback: true),
                    new byte[10], 47800, forceHostRollback: true));
                var clientParams = Handshake.RunClient(
                    clientCh, Id(depth: 30), new SessionPreferences(2, wantRollback: true), 51000);
                var hostParams = hostTask.GetAwaiter().GetResult();

                Assert.Equal(SyncMode.Rollback, hostParams.Mode);
                Assert.Equal(SyncMode.Rollback, clientParams.Mode);
            }
            finally { dispose(); }

            var (hostCh2, clientCh2, dispose2) = TcpPair();
            try
            {
                var hostTask = Task.Run(() => Handshake.RunHost(
                    hostCh2, Id(depth: 2), new SessionPreferences(2, wantRollback: true),
                    new byte[10], 47800, forceHostRollback: true));
                var clientParams = Handshake.RunClient(
                    clientCh2, Id(depth: 2), new SessionPreferences(2, wantRollback: false), 51000);
                var hostParams = hostTask.GetAwaiter().GetResult();

                Assert.Equal(SyncMode.Lockstep, hostParams.Mode);
                Assert.Equal(SyncMode.Lockstep, clientParams.Mode);
            }
            finally { dispose2(); }
        }
    }
}
