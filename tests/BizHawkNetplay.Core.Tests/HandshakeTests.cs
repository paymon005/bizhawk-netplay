using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

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

        // A client that WANTS rollback but reports too shallow a probed depth must still be
        // held to lockstep — this isolates the depth conjunct, which the scenario above cannot
        // (there the wantRollback=false conjunct already forces lockstep on its own) (KI-5).
        var (hostCh3, clientCh3, dispose3) = TcpPair();
        try
        {
            var hostTask = Task.Run(() => Handshake.RunHost(
                hostCh3, Id(depth: 2), new SessionPreferences(2, wantRollback: true),
                new byte[10], 47800, forceHostRollback: true));
            var clientParams = Handshake.RunClient(
                clientCh3, Id(depth: 2), new SessionPreferences(2, wantRollback: true), 51000);
            var hostParams = hostTask.GetAwaiter().GetResult();

            Assert.Equal(SyncMode.Lockstep, hostParams.Mode);
            Assert.Equal(SyncMode.Lockstep, clientParams.Mode);
        }
        finally { dispose3(); }
    }

    // ---- the pre-GO mesh round -------------------------------------------------------

    [Fact]
    public void MeshRound_LetsAJoinerToJoinerEdgeRaiseTheDelayNobodyElseCanSee()
    {
        var (hostCh1, clientCh1, d1) = TcpPair();
        var (hostCh2, clientCh2, d2) = TcpPair();
        try
        {
            var generation = Generation(0xBEEFUL, epoch: 2);
            var hostState = new byte[512];
            const int players = 3;
            const int provisionalDelay = 2;

            // The host's own links are fast. The edge BETWEEN the two joiners is not — and it is
            // the one edge no control link on this host touches.
            var j1Mesh = new LobbyMeshSample(new LobbyRttSample(90, 130), measuredEdges: 2, totalEdges: 2);
            var j2Mesh = new LobbyMeshSample(new LobbyRttSample(88, 120), measuredEdges: 2, totalEdges: 2);

            var c1 = Task.Run(() => Handshake.RunClientMulti(
                clientCh1, Id(), new SessionPreferences(1, false), 51001,
                measureMesh: (hostUdpPort, routes) =>
                {
                    Assert.Equal(47800, hostUdpPort);        // the joiner measures the host edge too
                    Assert.Equal(2, Assert.Single(routes).RemotePort);
                    return j1Mesh;
                }));
            var c2 = Task.Run(() => Handshake.RunClientMulti(
                clientCh2, Id(), new SessionPreferences(1, false), 51002,
                measureMesh: (_, __) => j2Mesh));

            var hostPrefs = new SessionPreferences(1, false);
            var g1 = Handshake.HostGreet(hostCh1, Id(), hostPrefs, 47800);
            var g2 = Handshake.HostGreet(hostCh2, Id(), hostPrefs, 47800);

            var j1Ep = new IPEndPoint(IPAddress.Loopback, g1.UdpPort);
            var j2Ep = new IPEndPoint(IPAddress.Loopback, g2.UdpPort);
            Handshake.HostSendStart(hostCh1, 1, players, provisionalDelay, SyncMode.Lockstep, hostState,
                generation, new[] { new PeerRoute(2, new[] { j2Ep }) });
            Handshake.HostSendStart(hostCh2, 2, players, provisionalDelay, SyncMode.Lockstep, hostState,
                generation, new[] { new PeerRoute(1, new[] { j1Ep }) });

            Handshake.HostRequestMeshRtt(hostCh1, generation);
            Handshake.HostRequestMeshRtt(hostCh2, generation);
            var r1 = Handshake.HostWaitMeshRtt(hostCh1, generation);
            var r2 = Handshake.HostWaitMeshRtt(hostCh2, generation);

            Assert.True(r1.HasMeasurement);
            Assert.True(r1.IsComplete);
            Assert.Equal(90, r1.Rtt.MedianMs, 3);
            Assert.Equal(40, r1.Rtt.JitterMs, 3);
            Assert.Equal(88, r2.Rtt.MedianMs, 3);

            double worstRtt = Math.Max(r1.Rtt.MedianMs, r2.Rtt.MedianMs);
            double worstJitter = Math.Max(r1.Rtt.JitterMs, r2.Rtt.JitterMs);
            int finalDelay = LobbyDelayPolicy.Choose(worstRtt, frameMs: 16.64, SyncMode.Lockstep,
                manualFloor: provisionalDelay, automaticMaximum: 20, jitterMs: worstJitter).Frames;
            Assert.True(finalDelay > provisionalDelay,
                "the joiner-to-joiner edge should have bought delay the host could not have measured");

            Handshake.HostSendInputDelay(hostCh1, generation, finalDelay);
            Handshake.HostSendInputDelay(hostCh2, generation, finalDelay);
            Handshake.HostRequestReady(hostCh1, generation);
            Handshake.HostRequestReady(hostCh2, generation);
            Handshake.HostWaitReady(hostCh1, generation);
            Handshake.HostWaitReady(hostCh2, generation);
            Handshake.HostSendGo(hostCh1, generation);
            Handshake.HostSendGo(hostCh2, generation);

            var p1 = c1.GetAwaiter().GetResult();
            var p2 = c2.GetAwaiter().GetResult();

            // Both joiners built their params on the FINAL delay, not the one WELCOME carried.
            Assert.Equal(finalDelay, p1.InputDelay);
            Assert.Equal(finalDelay, p2.InputDelay);
        }
        finally { d1(); d2(); }
    }

    [Fact]
    public void MeshRound_AJoinerThatMeasuredNothingSaysSoRatherThanReportingZero()
    {
        var (hostCh, clientCh, dispose) = TcpPair();
        try
        {
            var generation = Generation(0xC0DEUL, epoch: 1);
            var client = Task.Run(() => Handshake.RunClientMulti(
                clientCh, Id(), new SessionPreferences(1, false), 51001,
                measureMesh: (_, __) => new LobbyMeshSample(default, measuredEdges: 0, totalEdges: 3)));

            Handshake.HostGreet(hostCh, Id(), new SessionPreferences(1, false), 47800);
            Handshake.HostSendStart(hostCh, 1, 2, 3, SyncMode.Lockstep, new byte[8], generation);
            Handshake.HostRequestMeshRtt(hostCh, generation);
            var report = Handshake.HostWaitMeshRtt(hostCh, generation);

            // Zero round-trip and "nothing answered" are the same bytes on the wire but must not
            // be the same claim: read as a measurement, an unpunched mesh looks like a perfect one.
            Assert.False(report.HasMeasurement);
            Assert.False(report.IsComplete);
            Assert.Equal(0, report.MeasuredEdges);
            Assert.Equal(3, report.TotalEdges);

            Handshake.HostSendInputDelay(hostCh, generation, 3);
            Handshake.HostRequestReady(hostCh, generation);
            Handshake.HostWaitReady(hostCh, generation);
            Handshake.HostSendGo(hostCh, generation);
            Assert.Equal(3, client.GetAwaiter().GetResult().InputDelay);
        }
        finally { dispose(); }
    }

    [Fact]
    public void MeshRound_RefusesAReportFromAnotherGeneration()
    {
        var (hostCh, clientCh, dispose) = TcpPair();
        try
        {
            var expected = Generation(0x1111UL, epoch: 4);
            var stale = Generation(0x1111UL, epoch: 3);
            Task.Run(() => clientCh.Send(ControlMessageType.MeshRtt,
                ControlMessageCodec.EncodeMeshRtt(stale, 10, 12, 1, 1))).GetAwaiter().GetResult();

            var ex = Assert.Throws<HandshakeException>(() => Handshake.HostWaitMeshRtt(hostCh, expected));
            Assert.Contains("generation mismatch", ex.Message);
        }
        finally { dispose(); }
    }

    [Fact]
    public void MeshRound_ClientRefusesADelayThatArrivesAfterItCommittedToOne()
    {
        var (hostCh, clientCh, dispose) = TcpPair();
        try
        {
            var generation = Generation(0x2222UL, epoch: 1);
            var client = Task.Run(() => Handshake.RunClientMulti(
                clientCh, Id(), new SessionPreferences(1, false), 51001));

            Handshake.HostGreet(hostCh, Id(), new SessionPreferences(1, false), 47800);
            Handshake.HostSendStart(hostCh, 1, 2, 2, SyncMode.Lockstep, new byte[8], generation);
            Handshake.HostRequestReady(hostCh, generation);
            Handshake.HostWaitReady(hostCh, generation);
            // READY means the driver is already built for a delay. Changing it now would put this
            // peer on a different timeline from the rest of the lobby.
            Handshake.HostSendInputDelay(hostCh, generation, 9);
            Handshake.HostSendGo(hostCh, generation);

            var ex = Assert.Throws<HandshakeException>(() => client.GetAwaiter().GetResult());
            Assert.Contains("after READY", ex.Message);
        }
        finally { dispose(); }
    }

    /// <summary>
    /// A joiner's public endpoint reaches the OTHER joiners in WELCOME, before anyone has started
    /// playing. The mesh routes handed out in the lobby are what the pre-GO delay probe punches at,
    /// and until this the only candidate they carried was (public IP, local port) — a guess that
    /// holds only where the NAT preserves the source port. Where it doesn't, the joiner-to-joiner
    /// edges never opened in time and the delay silently fell back to the host's own TCP reading.
    /// </summary>
    [Fact]
    public void AJoinersPublicEndpointReachesTheOtherJoinersInWelcome()
    {
        var (hostCh1, clientCh1, d1) = TcpPair();
        var (hostCh2, clientCh2, d2) = TcpPair();
        try
        {
            var generation = Generation(0x4A01UL, epoch: 1);
            var j1Public = new IPEndPoint(IPAddress.Parse("203.0.113.11"), 40011);
            var j2Public = new IPEndPoint(IPAddress.Parse("198.51.100.22"), 40022);

            var c1 = Task.Run(() => Handshake.RunClientMulti(clientCh1, Id(),
                new SessionPreferences(1, false), 51001, localReflexive: j1Public));
            // The second joiner's STUN was blocked — it must still be admitted, just without a
            // candidate of its own.
            var c2 = Task.Run(() => Handshake.RunClientMulti(clientCh2, Id(),
                new SessionPreferences(1, false), 51002));

            var hostPrefs = new SessionPreferences(1, false);
            var g1 = Handshake.HostGreet(hostCh1, Id(), hostPrefs, 47800);
            var g2 = Handshake.HostGreet(hostCh2, Id(), hostPrefs, 47800);

            Assert.Equal(j1Public, g1.Reflexive);
            Assert.Null(g2.Reflexive);

            // What the host would build from those greetings: the observed endpoint first (it is
            // right on a LAN and costs nothing to try), the public one behind it.
            var j1Route = new PeerRoute(1, new[] { new IPEndPoint(IPAddress.Loopback, g1.UdpPort), j1Public });
            var j2Route = new PeerRoute(2, new[] { new IPEndPoint(IPAddress.Loopback, g2.UdpPort) });

            Handshake.HostSendStart(hostCh1, 1, 3, 2, SyncMode.Lockstep, new byte[8], generation,
                new[] { j2Route });
            Handshake.HostSendStart(hostCh2, 2, 3, 2, SyncMode.Lockstep, new byte[8], generation,
                new[] { j1Route });
            Handshake.HostRequestReady(hostCh1, generation);
            Handshake.HostRequestReady(hostCh2, generation);
            Handshake.HostWaitReady(hostCh1, generation);
            Handshake.HostWaitReady(hostCh2, generation);
            Handshake.HostSendGo(hostCh1, generation);
            Handshake.HostSendGo(hostCh2, generation);

            var p1 = c1.GetAwaiter().GetResult();
            var p2 = c2.GetAwaiter().GetResult();

            // P2 can punch at P1's public address from the lobby onward — the whole point.
            var routeToJ1 = Assert.Single(p2.PeerRoutes);
            Assert.Equal(1, routeToJ1.RemotePort);
            Assert.Contains(j1Public, routeToJ1.Candidates);
            Assert.Equal(2, routeToJ1.Candidates.Count);

            // And a peer with no candidate of its own is described honestly rather than guessed at.
            var routeToJ2 = Assert.Single(p1.PeerRoutes);
            Assert.Equal(2, routeToJ2.RemotePort);
            Assert.Single(routeToJ2.Candidates);
        }
        finally { d1(); d2(); }
    }

    [Fact]
    public void AMalformedOrAbsentPublicEndpointIsNotFatalToTheHandshake()
    {
        // The HELLO is peer-supplied and STUN can be blocked, so "no candidate" and "nonsense
        // candidate" must both land as null rather than refusing a joiner who can still play.
        var nonce = SessionAuth.NewNonce();
        var body = HandshakeCodec.Encode(Id(), new SessionPreferences(2, true), 51000, nonce,
            new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40009));
        var mangled = System.Text.Encoding.UTF8.GetString(body).Replace("refl=203.0.113.9:40009", "refl=not-an-endpoint");

        var (_, _, udpPort, decodedNonce, reflexive) =
            HandshakeCodec.Decode(System.Text.Encoding.UTF8.GetBytes(mangled));
        Assert.Equal(51000, udpPort);
        Assert.Equal(nonce, decodedNonce);
        Assert.Null(reflexive);

        var (_, _, _, _, missing) = HandshakeCodec.Decode(
            HandshakeCodec.Encode(Id(), new SessionPreferences(2, true), 51000, nonce));
        Assert.Null(missing);
    }

    // ---- surviving someone else's departure -----------------------------------------

    /// <summary>
    /// A joiner that is already READY keeps its seat when a DIFFERENT joiner leaves before GO. The
    /// host reissues the assignment, and this one re-prepares against the new parameters rather than
    /// having the whole lobby fail around it. Observed the other way round: a healthy P2 lost its
    /// session because P3 closed its window mid-handshake.
    /// </summary>
    [Fact]
    public void ARepeatedWelcomeRestartsTheLobbyWithoutReshippingTheState()
    {
        var (hostCh, clientCh, dispose) = TcpPair();
        try
        {
            var generation = Generation(0x5150UL, epoch: 1);
            var hostState = new byte[4096];
            new Random(7).NextBytes(hostState);
            var prepared = new List<(int port, int players, int delay)>();

            var client = Task.Run(() => Handshake.RunClientMulti(
                clientCh, Id(), new SessionPreferences(1, false), 51001,
                beforeReady: ready => prepared.Add((ready.LocalPort, ready.PlayerCount, ready.InputDelay))));

            Handshake.HostGreet(hostCh, Id(), new SessionPreferences(1, false), 47800);

            // First attempt: a 4-player lobby, this joiner is P3 (port 2).
            Handshake.HostSendStart(hostCh, assignedPort: 2, playerCount: 4, inputDelay: 2,
                SyncMode.Lockstep, hostState, generation);
            Handshake.HostRequestReady(hostCh, generation);
            Handshake.HostWaitReady(hostCh, generation);

            // P2 vanishes. The survivor moves up a port, the lobby refills, and the delay settles
            // somewhere else — all of which reaches it as a bare WELCOME with no state behind it.
            Handshake.HostSendAssignment(hostCh, assignedPort: 1, playerCount: 4, inputDelay: 2,
                SyncMode.Lockstep, generation);
            Handshake.HostSendInputDelay(hostCh, generation, 5);
            Handshake.HostRequestReady(hostCh, generation);
            Handshake.HostWaitReady(hostCh, generation);
            Handshake.HostSendGo(hostCh, generation);

            var session = client.GetAwaiter().GetResult();

            // It prepared twice, and the second preparation is the one that starts.
            Assert.Equal(2, prepared.Count);
            Assert.Equal((2, 4, 2), prepared[0]);
            Assert.Equal((1, 4, 5), prepared[1]);
            Assert.Equal(1, session.LocalPort);
            Assert.Equal(5, session.InputDelay);

            // The state it holds is the one shipped the first time round — the restart cost a
            // control frame, not another 4KiB (or several MiB on a real core).
            Assert.Equal(hostState, session.InitialState);
        }
        finally { dispose(); }
    }

    [Fact]
    public void ARestartedLobbyStillRefusesASecondStateTransfer()
    {
        var (hostCh, clientCh, dispose) = TcpPair();
        try
        {
            var generation = Generation(0x5151UL, epoch: 1);
            var client = Task.Run(() => Handshake.RunClientMulti(
                clientCh, Id(), new SessionPreferences(1, false), 51001));

            Handshake.HostGreet(hostCh, Id(), new SessionPreferences(1, false), 47800);
            Handshake.HostSendStart(hostCh, 1, 2, 2, SyncMode.Lockstep, new byte[16], generation);
            // A restart reissues the assignment only. A host that ships state twice has lost track
            // of what this joiner holds, and quietly taking the second copy would hide that.
            Handshake.HostSendStart(hostCh, 1, 2, 2, SyncMode.Lockstep, new byte[16], generation);

            var ex = Assert.Throws<HandshakeException>(() => client.GetAwaiter().GetResult());
            Assert.Contains("STATE more than once", ex.Message);
        }
        finally { dispose(); }
    }
}
