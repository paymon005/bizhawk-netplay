using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The full-mesh UDP transport over the loopback interface: every peer sends directly to every
/// other (no host relay). Proves delivery to all peers, endpoint pinning, and — end to end — a
/// 3-player lockstep session running deterministically across three live meshed sockets.
/// </summary>
public class MeshUdpTransportTests
{
    private static IPEndPoint Loop(int port) => new(IPAddress.Loopback, port);

    [Fact]
    public void PeerRoute_DeDuplicatesCandidatesInOrder()
    {
        var first = Loop(47800);
        var second = Loop(47801);
        var route = new PeerRoute(3, new[] { first, Loop(47800), second, Loop(47801) });

        Assert.Equal(3, route.RemotePort);
        Assert.Equal(new[] { first, second }, route.Candidates);
    }

    [Fact]
    public void EachPeerReceivesFromEveryOther()
    {
        var a = MeshUdpTransport.Bind(0);
        var b = MeshUdpTransport.Bind(0);
        var c = MeshUdpTransport.Bind(0);
        try
        {
            a.SetPeers(new[] { Loop(b.LocalPort), Loop(c.LocalPort) });
            b.SetPeers(new[] { Loop(a.LocalPort), Loop(c.LocalPort) });
            c.SetPeers(new[] { Loop(a.LocalPort), Loop(b.LocalPort) });

            a.Send([42]); // one send should reach BOTH other peers

            Assert.Equal(new byte[] { 42 }, WaitRecv(b));
            Assert.Equal(new byte[] { 42 }, WaitRecv(c));
        }
        finally { a.Dispose(); b.Dispose(); c.Dispose(); }
    }

    [Fact]
    public void SetPeerRoutes_GloballyDeduplicatesCandidates()
    {
        var sender = MeshUdpTransport.Bind(0);
        var receiver = MeshUdpTransport.Bind(0);
        try
        {
            var receiverEndpoint = Loop(receiver.LocalPort);
            sender.SetPeerRoutes(new[]
            {
                new PeerRoute(1, new[] { receiverEndpoint }),
                // The same socket cannot be two logical destinations. Keep the first ownership and
                // do not duplicate the input send merely because rendezvous advertised it twice.
                new PeerRoute(2, new[] { Loop(receiver.LocalPort) }),
            });
            receiver.SetPeers(new[] { Loop(sender.LocalPort) });

            sender.Send([7]);
            Assert.Equal(new byte[] { 7 }, WaitRecv(receiver));
            AssertNoRecv(receiver);
        }
        finally { sender.Dispose(); receiver.Dispose(); }
    }

    [Fact]
    public void Routes_SelectBestLiveCandidate_ThenFailOver_AndIgnoreDeadRtt()
    {
        var sender = MeshUdpTransport.Bind(0);
        var fast = MeshUdpTransport.Bind(0);
        var backup = MeshUdpTransport.Bind(0);
        var otherPeer = MeshUdpTransport.Bind(0);
        try
        {
            var senderEndpoint = Loop(sender.LocalPort);
            var fastEndpoint = Loop(fast.LocalPort);
            var backupEndpoint = Loop(backup.LocalPort);
            var otherEndpoint = Loop(otherPeer.LocalPort);
            sender.SetPeerRoutes(new[]
            {
                new PeerRoute(1, new[] { fastEndpoint, backupEndpoint }),
                new PeerRoute(2, new[] { otherEndpoint }),
            });
            sender.RecordRtt(fastEndpoint, 1);
            Assert.False(sender.TryGetWorstRttMs(out _)); // every logical route needs a live measurement
            fast.SetPeers(new[] { senderEndpoint });
            backup.SetPeers(new[] { senderEndpoint });
            otherPeer.SetPeers(new[] { senderEndpoint });

            // Every candidate is actively probed, even though only one candidate per route carries
            // input. Once all are live, make the ordering deterministic for this loopback test.
            WaitUntil(() => sender.IsEndpointAlive(fastEndpoint)
                         && sender.IsEndpointAlive(backupEndpoint)
                         && sender.IsEndpointAlive(otherEndpoint),
                "not every route candidate became live");
            for (int i = 0; i < 12; i++)
            {
                sender.RecordRtt(fastEndpoint, 1);
                sender.RecordRtt(backupEndpoint, 1000);
                sender.RecordRtt(otherEndpoint, 200);
            }

            Assert.True(sender.TryGetRttMs(fastEndpoint, out double fastRtt));
            Assert.True(sender.TryGetRttMs(backupEndpoint, out double backupRtt));
            Assert.True(sender.TryGetRttMs(otherEndpoint, out double otherRtt));
            Assert.True(fastRtt < otherRtt && otherRtt < backupRtt);
            Assert.True(sender.TryGetWorstRttMs(out double worst));
            Assert.InRange(Math.Abs(worst - otherRtt), 0, 0.001); // max(min(fast, backup), other)

            sender.Send([10]);
            Assert.Equal(new byte[] { 10 }, WaitRecv(fast));
            Assert.Equal(new byte[] { 10 }, WaitRecv(otherPeer));
            AssertNoRecv(backup); // one send for P1, through its lowest-RTT live candidate

            // Kill the selected path and clear confirmations. The punch loop reconfirms the two live
            // sockets; the stale low RTT for `fast` remains stored but must not keep that dead route.
            fast.Dispose();
            sender.RequestRepunch();
            WaitUntil(() => !sender.IsEndpointAlive(fastEndpoint)
                         && sender.IsEndpointAlive(backupEndpoint)
                         && sender.IsEndpointAlive(otherEndpoint),
                "live backup route was not selected after repunch");

            sender.Send([11]);
            Assert.Equal(new byte[] { 11 }, WaitRecv(backup));
            Assert.Equal(new byte[] { 11 }, WaitRecv(otherPeer));
            Assert.True(sender.TryGetWorstRttMs(out worst));
            Assert.True(worst > otherRtt, "failover should report the live backup, not stale fast-path RTT");

            // Leave only the dead endpoint configured. A once-measured route that has gone quiet
            // falls back to its stored (stale) measurement rather than failing the aggregate —
            // on a joiner the TCP fallback only covers the host link, so collapsing here would
            // swap a real 180ms peer path for a 15ms host ping mid-recovery (F9). Only a route
            // that was NEVER measured (asserted above, before the punch confirmed anything)
            // makes TryGetWorstRttMs return false.
            sender.SetPeerRoutes(new[] { new PeerRoute(1, new[] { fastEndpoint }) });
            Assert.True(sender.TryGetRttMs(fastEndpoint, out double staleRtt));
            Assert.True(sender.TryGetWorstRttMs(out worst));
            Assert.InRange(Math.Abs(worst - staleRtt), 0, 0.001);
        }
        finally { sender.Dispose(); fast.Dispose(); backup.Dispose(); otherPeer.Dispose(); }
    }

    [Fact]
    public void InputFailsOverToFreshSibling_WhenSelectedPathGoesQuiet()
    {
        // F6: the selected candidate dies *silently* mid-session — its liveness entry and its
        // (stale, lowest) RTT both linger inside the 8s alive window. A sibling candidate that is
        // still answering keepalives must take over the input path well before that window
        // expires; pinned sends into the black hole for the full 8s lose the race against the
        // UDP-lost session watchdog.
        var sender = MeshUdpTransport.Bind(0);
        var fast = MeshUdpTransport.Bind(0);
        var backup = MeshUdpTransport.Bind(0);
        try
        {
            var senderEndpoint = Loop(sender.LocalPort);
            var fastEndpoint = Loop(fast.LocalPort);
            var backupEndpoint = Loop(backup.LocalPort);
            sender.SetPeerRoutes(new[] { new PeerRoute(1, new[] { fastEndpoint, backupEndpoint }) });
            fast.SetPeers(new[] { senderEndpoint });
            backup.SetPeers(new[] { senderEndpoint });
            WaitUntil(() => sender.IsEndpointAlive(fastEndpoint) && sender.IsEndpointAlive(backupEndpoint),
                "both candidates should come alive");
            for (int i = 0; i < 12; i++)
            {
                sender.RecordRtt(fastEndpoint, 1);
                sender.RecordRtt(backupEndpoint, 50);
            }

            sender.Send([32]);
            Assert.Equal(new byte[] { 32 }, WaitRecv(fast));
            AssertNoRecv(backup);

            // The fast path dies without a goodbye. Input must reach the backup once the dead
            // path's freshness lapses — well under the 8s alive window (and far under the 8s
            // session-kill watchdog it would otherwise race).
            fast.Dispose();
            var sw = Stopwatch.StartNew();
            bool failedOver = false;
            while (sw.ElapsedMilliseconds < 4500 && !failedOver)
            {
                sender.Send([33]);
                if (backup.TryReceive(out var got) && got.Length == 1 && got[0] == 33) failedOver = true;
                else Thread.Sleep(100);
            }
            Assert.True(failedOver,
                $"input stayed pinned to the dead candidate for {sw.ElapsedMilliseconds}ms");
        }
        finally { sender.Dispose(); fast.Dispose(); backup.Dispose(); }
    }

    [Fact]
    public void BeforeAnyConfirmation_InputBroadcastsToEveryCandidate()
    {
        // Session start, punch still in flight: guessing one candidate can blackhole the whole
        // opening toward a NAT'd peer (the first real-internet session lost ~300ms of input to
        // the unreachable pre-NAT candidate this way). Until a path confirms, input must go to
        // every candidate — whichever one is real receives from frame zero.
        var sender = MeshUdpTransport.Bind(0);
        var unreachableFirst = MeshUdpTransport.Bind(0);
        var reachable = MeshUdpTransport.Bind(0);
        try
        {
            var unreachableEndpoint = Loop(unreachableFirst.LocalPort);
            var reachableEndpoint = Loop(reachable.LocalPort);
            unreachableFirst.Dispose(); // the pre-NAT address: nothing there
            sender.SetPeerRoutes(new[] { new PeerRoute(1, new[] { unreachableEndpoint, reachableEndpoint }) });
            reachable.SetPeers(new[] { Loop(sender.LocalPort) });

            // Send immediately — deliberately BEFORE waiting for any liveness confirmation.
            sender.Send([50]);
            Assert.Equal(new byte[] { 50 }, WaitRecv(reachable));
        }
        finally { sender.Dispose(); unreachableFirst.Dispose(); reachable.Dispose(); }
    }

    [Fact]
    public void RepunchFallsBackToLastKnownGoodCandidate_NotFirstAdvertised()
    {
        // F8: RequestRepunch clears the liveness table while re-probing a silent peer. With
        // nothing confirmed, input must keep riding the last path that actually worked — not
        // fall back to the first advertised candidate, which for an internet peer is typically
        // the unreachable pre-NAT address.
        var sender = MeshUdpTransport.Bind(0);
        var unreachableFirst = MeshUdpTransport.Bind(0);
        var working = MeshUdpTransport.Bind(0);
        try
        {
            var senderEndpoint = Loop(sender.LocalPort);
            var unreachableEndpoint = Loop(unreachableFirst.LocalPort);
            var workingEndpoint = Loop(working.LocalPort);
            unreachableFirst.Dispose(); // plays the dead pre-NAT candidate: never answers
            sender.SetPeerRoutes(new[] { new PeerRoute(1, new[] { unreachableEndpoint, workingEndpoint }) });
            working.SetPeers(new[] { senderEndpoint });
            WaitUntil(() => sender.IsEndpointAlive(workingEndpoint), "working path never confirmed");

            sender.Send([40]);
            Assert.Equal(new byte[] { 40 }, WaitRecv(working));

            sender.RequestRepunch(1);
            sender.Send([41]); // liveness just cleared — must still reach the peer
            Assert.Equal(new byte[] { 41 }, WaitRecv(working));
        }
        finally { sender.Dispose(); unreachableFirst.Dispose(); working.Dispose(); }
    }

    [Fact]
    public void PunchedJoiner_RunsTheFullHandshake_OverMeshControlStreams()
    {
        // The unified punch-join transport: joiner targets the host's known endpoint, host adds
        // the joiner's endpoint (the "pasted code"), the mesh punch confirms both ways, and the
        // ordinary handshake — state transfer included — runs over reliable control streams
        // carried on the SAME sockets the session's input will use. No TCP anywhere.
        var host = MeshUdpTransport.Bind(0);
        var joiner = MeshUdpTransport.Bind(0);
        try
        {
            var hostEndpoint = Loop(host.LocalPort);
            var joinerEndpoint = Loop(joiner.LocalPort);
            joiner.SetPeerRoutes(new[] { new PeerRoute(0, new[] { hostEndpoint }) });
            host.SetPeerRoutes(new[] { new PeerRoute(1, new[] { joinerEndpoint }) });
            WaitUntil(() => host.IsEndpointAlive(joinerEndpoint) && joiner.IsEndpointAlive(hostEndpoint),
                "mesh punch never confirmed both ways");

            var hostCh = new ControlChannel(host.OpenControl(joinerEndpoint));
            var joinCh = new ControlChannel(joiner.OpenControl(hostEndpoint));
            var state = new byte[60_000];
            new Random(7).NextBytes(state);
            PeerIdentity Id() => new(1, "ROM", "GPGX", "2.11.1.0", "SYNC", new[] { "L0", "L1" }, true, 20);

            var hostTask = System.Threading.Tasks.Task.Run(() =>
                Handshake.RunHost(hostCh, Id(), new SessionPreferences(2, false), state, host.LocalPort));
            var joinParams = Handshake.RunClient(joinCh, Id(), new SessionPreferences(2, false), joiner.LocalPort);
            hostTask.GetAwaiter().GetResult();

            Assert.Equal(state, joinParams.InitialState);
            Assert.Equal(host.LocalPort, joinParams.RemoteUdpPort);
        }
        finally { host.Dispose(); joiner.Dispose(); }
    }

    [Fact]
    public void ControlSegments_BeforeTheStreamIsOpened_AreDroppedButRetransmissionConverges()
    {
        // Security half: segments for an endpoint nobody opened a stream for are dropped with no
        // state allocated. Liveness half: because the sender's reliable layer retransmits, a
        // stream opened LATE (the host pastes the code a moment after the joiner starts talking)
        // still converges — nothing is lost, just delayed.
        var host = MeshUdpTransport.Bind(0);
        var joiner = MeshUdpTransport.Bind(0);
        try
        {
            var hostEndpoint = Loop(host.LocalPort);
            var joinerEndpoint = Loop(joiner.LocalPort);
            joiner.SetPeerRoutes(new[] { new PeerRoute(0, new[] { hostEndpoint }) });
            host.SetPeerRoutes(new[] { new PeerRoute(1, new[] { joinerEndpoint }) });
            WaitUntil(() => joiner.IsEndpointAlive(hostEndpoint), "joiner never punched the host");

            var joinerStream = joiner.OpenControl(hostEndpoint);
            var hello = new byte[] { 1, 2, 3, 4, 5 };
            joinerStream.Write(hello, 0, hello.Length); // host side not opened yet — dropped
            Thread.Sleep(300);

            var hostStream = host.OpenControl(joinerEndpoint); // "the code gets pasted"
            var got = new byte[hello.Length];
            int read = 0;
            var sw = Stopwatch.StartNew();
            while (read < got.Length && sw.ElapsedMilliseconds < 5000)
                read += hostStream.Read(got, read, got.Length - read);

            Assert.Equal(hello.Length, read);
            Assert.Equal(hello, got);
        }
        finally { host.Dispose(); joiner.Dispose(); }
    }

    [Fact]
    public void ForeignSenderIsIgnored()
    {
        var a = MeshUdpTransport.Bind(0);
        var b = MeshUdpTransport.Bind(0);
        var stranger = MeshUdpTransport.Bind(0);
        try
        {
            a.SetPeers(new[] { Loop(b.LocalPort) }); // a trusts only b
            stranger.SetPeers(new[] { Loop(a.LocalPort) });
            stranger.Send([9, 9]);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 300)
            {
                Assert.False(a.TryReceive(out _), "off-path datagram should be dropped");
                Thread.Sleep(5);
            }
        }
        finally { a.Dispose(); b.Dispose(); stranger.Dispose(); }
    }

    [Fact]
    public void EndpointCodec_RoundTrips_AndSkipsGarbage()
    {
        var eps = new[] { Loop(47800), new IPEndPoint(IPAddress.Parse("192.168.1.5"), 51001) };
        var decoded = HandshakeCodec.DecodeEndpoints(HandshakeCodec.EncodeEndpoints(eps));
        Assert.Equal(2, decoded.Count);
        Assert.Equal(eps[0], decoded[0]);
        Assert.Equal(eps[1], decoded[1]);

        // Malformed lines are skipped, not fatal.
        var messy = System.Text.Encoding.UTF8.GetBytes("127.0.0.1:47800\nnonsense\n:0\n10.0.0.9:52000\n");
        var d2 = HandshakeCodec.DecodeEndpoints(messy);
        Assert.Equal(2, d2.Count);
        Assert.Equal(Loop(47800), d2[0]);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.9"), 52000), d2[1]);
    }

    [Fact]
    public void ActivePunch_ConfirmsDirectPaths_WithoutAnyInput()
    {
        // With no input ever sent, the punch loop alone must open + confirm a direct path both ways
        // (this is the keepalive/rendezvous behaviour that holds NAT mappings during a lockstep stall).
        var a = MeshUdpTransport.Bind(0);
        var b = MeshUdpTransport.Bind(0);
        var c = MeshUdpTransport.Bind(0);
        try
        {
            var aeps = new[] { Loop(b.LocalPort), Loop(c.LocalPort) };
            var beps = new[] { Loop(a.LocalPort), Loop(c.LocalPort) };
            var ceps = new[] { Loop(a.LocalPort), Loop(b.LocalPort) };
            a.SetPeers(aeps); b.SetPeers(beps); c.SetPeers(ceps);

            var sw = Stopwatch.StartNew();
            bool all = false;
            while (sw.ElapsedMilliseconds < 3000 && !all)
            {
                all = a.IsEndpointAlive(aeps[0]) && a.IsEndpointAlive(aeps[1])
                   && b.IsEndpointAlive(beps[0]) && b.IsEndpointAlive(beps[1])
                   && c.IsEndpointAlive(ceps[0]) && c.IsEndpointAlive(ceps[1]);
                if (!all) Thread.Sleep(20);
            }
            Assert.True(all, "active punch did not confirm all direct paths without input");
            Assert.Equal(2, a.AliveEndpoints().Count);
        }
        finally { a.Dispose(); b.Dispose(); c.Dispose(); }
    }

    private static PortInput Btn(bool pressed)
    {
        var arr = new bool[8];
        arr[0] = pressed;
        return new PortInput(arr, []);
    }

    [Fact]
    public void ThreePlayerLockstep_OverRealMesh()
    {
        const int Delay = 2, Redundancy = 8, Target = 150, Players = 3;
        var t = new MeshUdpTransport[Players];
        for (int i = 0; i < Players; i++) t[i] = MeshUdpTransport.Bind(0);
        // Wire the mesh: each peer sends to / accepts from every other.
        for (int i = 0; i < Players; i++)
        {
            var others = new System.Collections.Generic.List<IPEndPoint>();
            for (int j = 0; j < Players; j++) if (j != i) others.Add(Loop(t[j].LocalPort));
            t[i].SetPeers(others);
        }

        var emu = new FakeEmuAdapter[Players];
        var drv = new FrameDriver[Players];
        for (int i = 0; i < Players; i++)
        {
            int port = i;
            emu[i] = new FakeEmuAdapter(portCount: Players) { LocalInputScript = f => Btn((f % (port + 2)) == 0) };
            drv[i] = new FrameDriver(emu[i], t[i], p => new LockstepStrategy(p), port, Delay, Redundancy);
            drv[i].Start();
        }

        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20000)
            {
                bool allDone = true;
                for (int i = 0; i < Players; i++)
                {
                    if (drv[i].CurrentFrame < Target)
                    {
                        allDone = false;
                        if (drv[i].OnPreFrame() == FrameStep.Ran) { emu[i].AdvanceAppliedFrame(); drv[i].OnPostFrame(); }
                    }
                }
                if (allDone) break;
                Thread.Sleep(0);
            }

            for (int i = 0; i < Players; i++)
                Assert.True(drv[i].CurrentFrame >= Target, $"player {i} reached {drv[i].CurrentFrame}/{Target}");

            // Determinism across the real meshed sockets: identical applied inputs + equal memory.
            int common = int.MaxValue;
            for (int i = 0; i < Players; i++) common = Math.Min(common, emu[i].AppliedInputs.Count);
            for (int f = 0; f < common; f++)
                for (int p = 0; p < Players; p++)
                {
                    var reference = emu[0].AppliedInputs[f].Ports[p];
                    for (int i = 1; i < Players; i++)
                        Assert.True(reference.ValueEquals(emu[i].AppliedInputs[f].Ports[p]),
                            $"desync at frame {f} port {p} on player {i}");
                }
            var h0 = emu[0].HashMainMemory();
            for (int i = 1; i < Players; i++) Assert.Equal(h0, emu[i].HashMainMemory());
        }
        finally { for (int i = 0; i < Players; i++) t[i].Dispose(); }
    }

    /// <summary>
    /// The symmetric-NAT fallback: two joiners that cannot see each other still exchange input,
    /// because the host forwards it.
    ///
    /// Modelled by simply never telling A and C about each other — which is what an unpunchable leg
    /// looks like from the transport's side. Both still reach the host, as a symmetric-NAT peer
    /// does, having dialled out to a forwarded port itself.
    /// </summary>
    [Fact]
    public void HostRelaysInputBetweenJoinersThatCannotReachEachOther()
    {
        var host = MeshUdpTransport.Bind(0);
        var a = MeshUdpTransport.Bind(0);
        var c = MeshUdpTransport.Bind(0);
        try
        {
            var toA = new PeerRoute(1, new[] { Loop(a.LocalPort) });
            var toC = new PeerRoute(2, new[] { Loop(c.LocalPort) });
            host.SetPeerRoutes(new[] { toA, toC });
            // The broken mesh: each joiner knows only the host.
            a.SetPeers(new[] { Loop(host.LocalPort) });
            c.SetPeers(new[] { Loop(host.LocalPort) });

            // Without the relay, A's input reaches the host and stops there.
            a.Send([7]);
            Assert.Equal(new byte[] { 7 }, WaitRecv(host));
            AssertNoRecv(c);

            // The host must have heard from both before it can pick a live candidate to relay over.
            c.Send([8]);
            Assert.Equal(new byte[] { 8 }, WaitRecv(host));

            host.SetRelayRoutes(new[] { toA, toC });
            Assert.Equal(2, host.RelayRouteCount);

            a.Send([9]);
            Assert.Equal(new byte[] { 9 }, WaitRecv(host)); // still delivered locally
            Assert.Equal(new byte[] { 9 }, WaitRecv(c));    // ...and forwarded to the unreachable peer
        }
        finally { host.Dispose(); a.Dispose(); c.Dispose(); }
    }

    /// <summary>A relayed datagram must not be bounced back to the peer that sent it — that would be
    /// a duplicate at best and, if anything ever forwarded onward, a loop.</summary>
    [Fact]
    public void RelayNeverEchoesBackToTheSender()
    {
        var host = MeshUdpTransport.Bind(0);
        var a = MeshUdpTransport.Bind(0);
        try
        {
            var toA = new PeerRoute(1, new[] { Loop(a.LocalPort) });
            host.SetPeerRoutes(new[] { toA });
            a.SetPeers(new[] { Loop(host.LocalPort) });

            a.Send([1]);
            Assert.Equal(new byte[] { 1 }, WaitRecv(host)); // host now has a live path to A

            host.SetRelayRoutes(new[] { toA }); // A is its own relay target, and must be skipped
            a.Send([2]);
            Assert.Equal(new byte[] { 2 }, WaitRecv(host));
            AssertNoRecv(a);
        }
        finally { host.Dispose(); a.Dispose(); }
    }

    private static byte[] Token(byte fill) => Enumerable.Repeat(fill, 16).ToArray();

    /// <summary>
    /// The symmetric-NAT case, modelled honestly: the peer's packets arrive from a port nobody was
    /// ever told about, because its router assigns one per destination.
    ///
    /// Simulated by pointing the host at a DIFFERENT address than the one the peer actually sends
    /// from — which is precisely what "advertised the STUN address, arrives from another" means. The
    /// host must reject that traffic until a token identifies it, and accept it afterwards.
    /// </summary>
    [Fact]
    public void AnUnadvertisedEndpointIsLearnedFromItsTokenAndNotBefore()
    {
        var host = MeshUdpTransport.Bind(0);
        var peer = MeshUdpTransport.Bind(0);
        try
        {
            // What the peer advertised: a port it does not send from. Nothing else is routable.
            int wrongPort = peer.LocalPort == 65000 ? 64999 : peer.LocalPort + 1;
            host.SetPeerRoutes(new[] { new PeerRoute(1, new[] { Loop(wrongPort) }) });
            peer.SetPeers(new[] { Loop(host.LocalPort) });

            // Unrecognised source: dropped, exactly as before this existed.
            peer.Send([1]);
            AssertNoRecv(host);

            // The host is told which token belongs to that port; the peer announces it.
            host.SetPeerTokens(new[] { new KeyValuePair<int, byte[]>(1, Token(0xAB)) });
            peer.SetLocalToken(Token(0xAB));

            // The punch loop announces on unconfirmed paths, so this needs no explicit trigger.
            var sw = Stopwatch.StartNew();
            while (host.LearnedEndpointCount == 0 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(10);
            Assert.Equal(1, host.LearnedEndpointCount);
            Assert.True(host.TryGetLearnedEndpoint(1, out var learned));
            Assert.Equal(peer.LocalPort, learned.Port); // where it REALLY comes from, not what it claimed

            // ...and now ordinary input from that endpoint is accepted.
            peer.Send([2]);
            Assert.Equal(new byte[] { 2 }, WaitRecv(host));
        }
        finally { host.Dispose(); peer.Dispose(); }
    }

    /// <summary>A wrong token must buy nothing: the endpoint stays unroutable and its input stays
    /// dropped. Otherwise the pin this replaces would have been traded for no protection at all.</summary>
    [Fact]
    public void AWrongTokenLearnsNothing()
    {
        var host = MeshUdpTransport.Bind(0);
        var peer = MeshUdpTransport.Bind(0);
        try
        {
            int wrongPort = peer.LocalPort == 65000 ? 64999 : peer.LocalPort + 1;
            host.SetPeerRoutes(new[] { new PeerRoute(1, new[] { Loop(wrongPort) }) });
            peer.SetPeers(new[] { Loop(host.LocalPort) });

            host.SetPeerTokens(new[] { new KeyValuePair<int, byte[]>(1, Token(0xAB)) });
            peer.SetLocalToken(Token(0xCD)); // not the one the host will accept

            Thread.Sleep(1200); // several announce cycles
            Assert.Equal(0, host.LearnedEndpointCount);
            peer.Send([3]);
            AssertNoRecv(host);
        }
        finally { host.Dispose(); peer.Dispose(); }
    }

    /// <summary>
    /// Learning is only half the fix. The host must also PROBE the address it learned, or the edge
    /// is a one-way street: input arrives and is answered, but nothing ever measures it, so the
    /// lobby reports a working peer as silent and sizes the delay as though it were absent.
    /// </summary>
    [Fact]
    public void ALearnedPeerIsProbedAndNamedAsLearnedInTheEdgeReport()
    {
        var host = MeshUdpTransport.Bind(0);
        var peer = MeshUdpTransport.Bind(0);
        try
        {
            int wrongPort = peer.LocalPort == 65000 ? 64999 : peer.LocalPort + 1;
            host.SetPeerRoutes(new[] { new PeerRoute(1, new[] { Loop(wrongPort) }) });
            peer.SetPeers(new[] { Loop(host.LocalPort) });
            host.SetPeerTokens(new[] { new KeyValuePair<int, byte[]>(1, Token(0xAB)) });
            peer.SetLocalToken(Token(0xAB));

            host.BeginRttBurst(1500);
            var sw = Stopwatch.StartNew();
            MeshEdgeReport edge = default;
            while (sw.ElapsedMilliseconds < 4000)
            {
                edge = Assert.Single(host.DescribeEdges());
                if (edge.Measured) break;
                Thread.Sleep(20);
            }

            Assert.True(edge.Measured, "the learned edge produced no round-trip sample — it is not being probed");
            Assert.Equal(1, edge.RemotePort);
            Assert.True(edge.ViaLearnedEndpoint,
                "the only address that can answer is the learned one, so the report must say so");
        }
        finally { host.Dispose(); peer.Dispose(); }
    }

    /// <summary>
    /// A learned endpoint belongs to the SEAT, not to that seat's advertised candidates. Route
    /// updates happen constantly — every reflexive candidate that arrives triggers one — and each
    /// would otherwise un-learn every symmetric-NAT peer, which is the one thing they cannot
    /// recover from on their own. A seat that leaves the session is a different matter.
    /// </summary>
    [Fact]
    public void ARouteRefreshKeepsWhatWasLearnedButLosingTheSeatForgetsIt()
    {
        var host = MeshUdpTransport.Bind(0);
        var peer = MeshUdpTransport.Bind(0);
        try
        {
            int wrongPort = peer.LocalPort == 65000 ? 64999 : peer.LocalPort + 1;
            host.SetPeerRoutes(new[] { new PeerRoute(1, new[] { Loop(wrongPort) }) });
            peer.SetPeers(new[] { Loop(host.LocalPort) });
            host.SetPeerTokens(new[] { new KeyValuePair<int, byte[]>(1, Token(0xAB)) });
            peer.SetLocalToken(Token(0xAB));

            var sw = Stopwatch.StartNew();
            while (host.LearnedEndpointCount == 0 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(10);
            Assert.Equal(1, host.LearnedEndpointCount);

            // Same seat, extra candidate — exactly what a late reflexive discovery looks like.
            host.SetPeerRoutes(new[] { new PeerRoute(1, new[] { Loop(wrongPort), Loop(wrongPort + 1) }) });
            Assert.Equal(1, host.LearnedEndpointCount);
            peer.Send([7]);
            Assert.Equal(new byte[] { 7 }, WaitRecv(host));

            // Seat 1 is gone: nothing about it should linger, including where it used to be.
            host.SetPeerRoutes(new[] { new PeerRoute(2, new[] { Loop(wrongPort + 2) }) });
            Assert.Equal(0, host.LearnedEndpointCount);
            Assert.False(host.TryGetLearnedEndpoint(1, out _));
        }
        finally { host.Dispose(); peer.Dispose(); }
    }

    private static byte[] WaitRecv(ITransport t)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            if (t.TryReceive(out var got)) return got;
            Thread.Sleep(1);
        }
        throw new Xunit.Sdk.XunitException("no datagram received within 2s");
    }

    private static void AssertNoRecv(ITransport transport, int durationMs = 250)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            Assert.False(transport.TryReceive(out _), "unexpected duplicate datagram");
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void WorstRttStats_TakeEachPeersBestPathThenTheWorstPeer_AndReportCoverage()
    {
        var sender = MeshUdpTransport.Bind(0);
        try
        {
            var fast = Loop(40001);
            var backup = Loop(40002);
            var other = Loop(40003);
            var silent = Loop(40004);
            sender.SetPeerRoutes(new[]
            {
                new PeerRoute(1, new[] { fast, backup }),
                new PeerRoute(2, new[] { other }),
                new PeerRoute(3, new[] { silent }), // never answers
            });

            foreach (double sample in new double[] { 4, 5, 6, 20 }) sender.RecordRtt(fast, sample);
            foreach (double sample in new double[] { 90, 91, 92, 300 }) sender.RecordRtt(backup, sample);
            foreach (double sample in new double[] { 30, 31, 32, 120 }) sender.RecordRtt(other, sample);

            Assert.True(sender.TryGetWorstRttStats(out double medianMs, out double highMs,
                out int measured, out int total));

            // Peer 1 is reached over its BEST candidate (fast: median 5.5), so its slow sibling
            // must not be what sizes the session; peer 2 is then the worst peer overall.
            Assert.Equal(31.5, medianMs, 3);
            Assert.Equal(120, highMs, 3);

            // Peer 3 answered nothing. Counting it as measured would let an unpunched path read as
            // a fast one — the figure is a lower bound and the counts are how the caller knows.
            Assert.Equal(2, measured);
            Assert.Equal(3, total);
        }
        finally { sender.Dispose(); }
    }

    [Fact]
    public void RttBurst_MeasuresEveryEdgeBeforeAnyInputFlows()
    {
        var a = MeshUdpTransport.Bind(0);
        var b = MeshUdpTransport.Bind(0);
        var c = MeshUdpTransport.Bind(0);
        try
        {
            a.SetPeers(new[] { Loop(b.LocalPort), Loop(c.LocalPort) });
            b.SetPeers(new[] { Loop(a.LocalPort), Loop(c.LocalPort) });
            c.SetPeers(new[] { Loop(a.LocalPort), Loop(b.LocalPort) });

            // Every peer bursts at once, exactly as the lobby round does — an edge only opens when
            // both of its ends are knocking.
            a.BeginRttBurst(500);
            b.BeginRttBurst(500);
            c.BeginRttBurst(500);

            WaitUntil(() => a.TryGetWorstRttStats(out _, out _, out int m, out int t) && m == t && t == 2,
                "the burst did not measure both of this peer's edges");

            Assert.True(a.TryGetWorstRttStats(out double medianMs, out double highMs,
                out int measured, out int total));
            Assert.Equal(2, measured);
            Assert.Equal(2, total);
            Assert.InRange(medianMs, 0, 500);
            Assert.True(highMs >= medianMs, "the high-water mark can never sit below the median");

            // Not one input datagram was sent: the measurement rides the punch/keepalive exchange,
            // which is what makes it available before GO rather than after.
            Assert.False(a.TryReceive(out _));
        }
        finally { a.Dispose(); b.Dispose(); c.Dispose(); }
    }

    [Fact]
    public void RttBurst_StartsEachCandidatesSampleWindowOver()
    {
        var sender = MeshUdpTransport.Bind(0);
        try
        {
            var peer = Loop(40010);
            sender.SetPeerRoutes(new[] { new PeerRoute(1, new[] { peer }) });
            foreach (double sample in new double[] { 500, 510, 520 }) sender.RecordRtt(peer, sample);
            Assert.True(sender.TryGetRttStats(peer, out double before, out _));
            Assert.Equal(510, before, 3);

            // A lobby measurement must describe the link as it is now, not carry in whatever the
            // punch loop happened to see while the session was still filling up.
            sender.BeginRttBurst(0);
            Assert.False(sender.TryGetRttStats(peer, out _, out _));
            Assert.False(sender.TryGetWorstRttStats(out _, out _, out int measured, out int total));
            Assert.Equal(0, measured);
            Assert.Equal(1, total);
        }
        finally { sender.Dispose(); }
    }

    private static void WaitUntil(Func<bool> condition, string message, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new Xunit.Sdk.XunitException(message);
    }
}
