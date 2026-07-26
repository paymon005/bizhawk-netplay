using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// The full-mesh UDP transport over the loopback interface: every peer sends directly to every
    /// other (no host relay). Proves delivery to all peers, endpoint pinning, and — end to end — a
    /// 3-player lockstep session running deterministically across three live meshed sockets.
    /// </summary>
    public class MeshUdpTransportTests
    {
        private static IPEndPoint Loop(int port) => new IPEndPoint(IPAddress.Loopback, port);

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

                a.Send(new byte[] { 42 }); // one send should reach BOTH other peers

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

                sender.Send(new byte[] { 7 });
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

                sender.Send(new byte[] { 10 });
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

                sender.Send(new byte[] { 11 });
                Assert.Equal(new byte[] { 11 }, WaitRecv(backup));
                Assert.Equal(new byte[] { 11 }, WaitRecv(otherPeer));
                Assert.True(sender.TryGetWorstRttMs(out worst));
                Assert.True(worst > otherRtt, "failover should report the live backup, not stale fast-path RTT");

                // Leave only the dead endpoint configured. Its historical RTT is still present, but a
                // route with no live candidate must not produce a UDP RTT measurement.
                sender.SetPeerRoutes(new[] { new PeerRoute(1, new[] { fastEndpoint }) });
                Assert.True(sender.TryGetRttMs(fastEndpoint, out _));
                Assert.False(sender.TryGetWorstRttMs(out _));
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

                sender.Send(new byte[] { 32 });
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
                    sender.Send(new byte[] { 33 });
                    if (backup.TryReceive(out var got) && got.Length == 1 && got[0] == 33) failedOver = true;
                    else Thread.Sleep(100);
                }
                Assert.True(failedOver,
                    $"input stayed pinned to the dead candidate for {sw.ElapsedMilliseconds}ms");
            }
            finally { sender.Dispose(); fast.Dispose(); backup.Dispose(); }
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

                sender.Send(new byte[] { 40 });
                Assert.Equal(new byte[] { 40 }, WaitRecv(working));

                sender.RequestRepunch(1);
                sender.Send(new byte[] { 41 }); // liveness just cleared — must still reach the peer
                Assert.Equal(new byte[] { 41 }, WaitRecv(working));
            }
            finally { sender.Dispose(); unreachableFirst.Dispose(); working.Dispose(); }
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
                stranger.Send(new byte[] { 9, 9 });

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
            return new PortInput(arr, Array.Empty<int>());
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
}
