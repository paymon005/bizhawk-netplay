using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// Exercises the real UDP transport over the loopback interface (127.0.0.1) in-process, then
    /// runs a full lockstep session across two live sockets — proving the FrameDriver works over
    /// an actual asynchronous socket path, not just the in-memory loopback.
    /// </summary>
    public class UdpTransportTests
    {
        private static (UdpTransport a, UdpTransport b) ConnectedPair()
        {
            var a = UdpTransport.Bind(0);
            var b = UdpTransport.Bind(0);
            a.SetRemote(new IPEndPoint(IPAddress.Loopback, b.LocalPort));
            b.SetRemote(new IPEndPoint(IPAddress.Loopback, a.LocalPort));
            return (a, b);
        }

        [Fact]
        public void Datagram_RoundTrips_OverLoopback()
        {
            var (a, b) = ConnectedPair();
            try
            {
                var payload = new byte[] { 1, 2, 3, 4, 5 };
                a.Send(payload);

                var sw = Stopwatch.StartNew();
                byte[]? got = null;
                while (sw.ElapsedMilliseconds < 2000 && got == null)
                {
                    if (!b.TryReceive(out got)) Thread.Sleep(1);
                }
                Assert.NotNull(got);
                Assert.Equal(payload, got);
            }
            finally { a.Dispose(); b.Dispose(); }
        }

        [Fact]
        public void ForeignPeer_IsIgnored()
        {
            var a = UdpTransport.Bind(0);
            var b = UdpTransport.Bind(0);
            var stranger = UdpTransport.Bind(0);
            try
            {
                // b only trusts a; a packet from the stranger must be dropped.
                b.SetRemote(new IPEndPoint(IPAddress.Loopback, a.LocalPort));
                stranger.SetRemote(new IPEndPoint(IPAddress.Loopback, b.LocalPort));
                stranger.Send(new byte[] { 9, 9, 9 });

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 300)
                {
                    Assert.False(b.TryReceive(out _), "stranger's datagram should have been dropped");
                    Thread.Sleep(5);
                }
            }
            finally { a.Dispose(); b.Dispose(); stranger.Dispose(); }
        }

        private static PortInput Btn(bool pressed)
        {
            var arr = new bool[8];
            arr[0] = pressed;
            return new PortInput(arr, Array.Empty<int>());
        }

        [Fact]
        public void TwoInstances_Lockstep_OverRealUdp()
        {
            const int Delay = 2, Redundancy = 8, Target = 150;
            var (ta, tb) = ConnectedPair();

            var emuA = new FakeEmuAdapter(portCount: 2) { LocalInputScript = f => Btn((f % 3) == 0) };
            var emuB = new FakeEmuAdapter(portCount: 2) { LocalInputScript = f => Btn((f % 2) == 0) };
            var a = new FrameDriver(emuA, ta, p => new LockstepStrategy(p), 0, Delay, Redundancy);
            var b = new FrameDriver(emuB, tb, p => new LockstepStrategy(p), 1, Delay, Redundancy);
            a.Start();
            b.Start();

            try
            {
                var sw = Stopwatch.StartNew();
                while ((a.CurrentFrame < Target || b.CurrentFrame < Target) && sw.ElapsedMilliseconds < 15000)
                {
                    if (a.CurrentFrame < Target && a.OnPreFrame() == FrameStep.Ran)
                    { emuA.AdvanceAppliedFrame(); a.OnPostFrame(); }
                    if (b.CurrentFrame < Target && b.OnPreFrame() == FrameStep.Ran)
                    { emuB.AdvanceAppliedFrame(); b.OnPostFrame(); }
                    Thread.Sleep(0); // let the rx threads deliver
                }

                Assert.True(a.CurrentFrame >= Target, $"A reached {a.CurrentFrame}/{Target}");
                Assert.True(b.CurrentFrame >= Target, $"B reached {b.CurrentFrame}/{Target}");

                // Determinism proof across the real socket path.
                int common = Math.Min(emuA.AppliedInputs.Count, emuB.AppliedInputs.Count);
                for (int f = 0; f < common; f++)
                    for (int p = 0; p < 2; p++)
                        Assert.True(emuA.AppliedInputs[f].Ports[p].ValueEquals(emuB.AppliedInputs[f].Ports[p]),
                            $"desync at frame {f} port {p}");
                Assert.Equal(emuA.HashMainMemory(), emuB.HashMainMemory());
            }
            finally { ta.Dispose(); tb.Dispose(); }
        }
    }
}
