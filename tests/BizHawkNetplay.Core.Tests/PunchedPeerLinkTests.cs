using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// Drives two <see cref="PunchedPeerLink"/> instances over real localhost UDP sockets: they punch
    /// each other, run the actual handshake over the reliable control stream, and pass input over the
    /// unreliable channel — the whole punch session end-to-end, minus a real NAT (which loopback lacks).
    /// </summary>
    public class PunchedPeerLinkTests
    {
        [Fact]
        public void TwoLinks_PunchEachOther_AndConfirmPeers()
        {
            using var a = PunchedPeerLink.Bind(0);
            using var b = PunchedPeerLink.Bind(0);
            var aHint = new IPEndPoint(IPAddress.Loopback, b.LocalPort);
            var bHint = new IPEndPoint(IPAddress.Loopback, a.LocalPort);

            var pa = Task.Run(() => a.Punch(aHint, TimeSpan.FromSeconds(5)));
            var pb = Task.Run(() => b.Punch(bHint, TimeSpan.FromSeconds(5)));

            Assert.True(pa.Result, "A failed to punch");
            Assert.True(pb.Result, "B failed to punch");
            Assert.Equal(a.LocalPort, b.PeerEndpoint!.Port);
            Assert.Equal(b.LocalPort, a.PeerEndpoint!.Port);
        }

        [Fact]
        public void PassiveSide_AutoAdoptsAnActivePuncher_WithoutTheirCode()
        {
            // The asymmetric (RemotePlay-style) flow: the listener shared its code and just waits —
            // it never learns the other side's endpoint out of band. The active side punches toward
            // the code; the listener adopts whoever's punch arrives and confirms the path.
            using var listener = PunchedPeerLink.Bind(0);
            using var active = PunchedPeerLink.Bind(0);

            var waiting = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(5))
                    if (listener.WaitForPunch(TimeSpan.FromMilliseconds(100))) return true;
                return false;
            });
            bool punched = active.Punch(new IPEndPoint(IPAddress.Loopback, listener.LocalPort), TimeSpan.FromSeconds(5));

            Assert.True(punched, "active side failed to punch");
            Assert.True(waiting.Result, "listener never adopted the puncher");
            Assert.Equal(active.LocalPort, listener.PeerEndpoint!.Port);
            Assert.Equal(listener.LocalPort, active.PeerEndpoint!.Port);
        }

        [Fact]
        public void FullPunchSession_HandshakeOverControl_AndInputOverTransport()
        {
            using var host = PunchedPeerLink.Bind(0);
            using var joiner = PunchedPeerLink.Bind(0);

            var pHost = Task.Run(() => host.Punch(new IPEndPoint(IPAddress.Loopback, joiner.LocalPort), TimeSpan.FromSeconds(5)));
            var pJoin = Task.Run(() => joiner.Punch(new IPEndPoint(IPAddress.Loopback, host.LocalPort), TimeSpan.FromSeconds(5)));
            Assert.True(pHost.Result && pJoin.Result, "punch failed");

            // Handshake + state transfer over the reliable control stream.
            var hostCh = new ControlChannel(host.Control);
            var joinCh = new ControlChannel(joiner.Control);
            var state = new byte[80_000];
            new Random(321).NextBytes(state);
            PeerIdentity Id() => new PeerIdentity(1, "ROM", "GPGX", "2.11.1.0", "SYNC", new[] { "L0", "L1" }, true, 20);

            var hostTask = Task.Run(() => Handshake.RunHost(hostCh, Id(), new SessionPreferences(2, false), state, host.LocalPort));
            var joinParams = Handshake.RunClient(joinCh, Id(), new SessionPreferences(2, false), joiner.LocalPort);
            hostTask.GetAwaiter().GetResult();
            Assert.Equal(state, joinParams.InitialState);

            // Input over the unreliable transport face (retry to tolerate expected UDP loss).
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            byte[] got = null!;
            for (int i = 0; i < 50 && got == null; i++)
            {
                host.Send(payload);
                Thread.Sleep(10);
                joiner.TryReceive(out got!);
            }
            Assert.Equal(payload, got);
        }

        [Fact]
        public void PunchTimesOut_WhenPeerSilent()
        {
            using var a = PunchedPeerLink.Bind(0);
            // Punch toward a port nobody is listening on: no reply, so it must give up and report false.
            bool ok = a.Punch(new IPEndPoint(IPAddress.Loopback, 1), TimeSpan.FromMilliseconds(600));
            Assert.False(ok);
            Assert.Null(a.PeerEndpoint);
        }
    }
}
