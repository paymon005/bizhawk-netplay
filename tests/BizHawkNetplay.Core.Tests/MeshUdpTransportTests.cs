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
    }
}
