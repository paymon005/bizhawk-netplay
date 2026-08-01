using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;
using static BizHawkNetplay.Core.Tests.Net48Compat;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Exercises the reliable-over-UDP stream directly and, crucially, end-to-end: the real
/// <see cref="Handshake"/> runs over a pair of these streams across a lossy, reordering link,
/// proving the punch path reuses the TCP control channel unmodified.
/// </summary>
public class ReliableUdpStreamTests
{
    /// <summary>
    /// An async datagram link between two reliable streams with injectable loss and reordering.
    /// Each direction has a worker thread that pulls a random queued datagram (reorder) and either
    /// drops it or delivers it to the far stream's OnDatagram.
    /// </summary>
    private sealed class LossyLink : IDisposable
    {
        private readonly Random _rng;
        private readonly double _drop;
        private readonly object _l = new();
        private readonly List<byte[]> _aToB = new();
        private readonly List<byte[]> _bToA = new();
        private ReliableUdpStream _a = null!, _b = null!;
        private volatile bool _run = true;
        private Thread _t1 = null!, _t2 = null!;

        public LossyLink(int seed, double drop) { _rng = new Random(seed); _drop = drop; }

        public (ReliableUdpStream a, ReliableUdpStream b) Connect()
        {
            _a = new ReliableUdpStream(seg => Enqueue(_aToB, seg));
            _b = new ReliableUdpStream(seg => Enqueue(_bToA, seg));
            _t1 = new Thread(() => Pump(_aToB, () => _b)) { IsBackground = true };
            _t2 = new Thread(() => Pump(_bToA, () => _a)) { IsBackground = true };
            _t1.Start(); _t2.Start();
            return (_a, _b);
        }

        private void Enqueue(List<byte[]> q, byte[] seg)
        {
            lock (_l) q.Add((byte[])seg.Clone());
        }

        private void Pump(List<byte[]> q, Func<ReliableUdpStream> target)
        {
            while (_run)
            {
                byte[]? seg = null;
                lock (_l)
                {
                    if (q.Count > 0)
                    {
                        int idx = _rng.Next(q.Count); // pull out of order
                        seg = q[idx];
                        q.RemoveAt(idx);
                    }
                }
                if (seg == null) { Thread.Sleep(1); continue; }
                if (_rng.NextDouble() < _drop) continue; // dropped
                target().OnDatagram(seg);
            }
        }

        public void Dispose() { _run = false; try { _a?.Dispose(); _b?.Dispose(); } catch { } }
    }

    [Fact]
    public void ReorderBuffer_DeliversInOrder_NoThreads()
    {
        // Feed DATA segments to a receiver in shuffled order; it must reassemble exactly.
        var captured = new List<byte[]>();
        var sender = new ReliableUdpStream(seg => captured.Add(seg));
        var blob = new byte[20_000];
        new Random(7).NextBytes(blob);
        sender.Write(blob, 0, blob.Length);

        var receiver = new ReliableUdpStream(_ => { }); // ignore its ACKs
        var shuffled = new List<byte[]>(captured);
        var rng = new Random(42);
        for (int i = shuffled.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]); }
        foreach (var seg in shuffled) receiver.OnDatagram(seg);

        var got = ReadExactly(receiver, blob.Length);
        Assert.Equal(blob, got);
    }

    [Fact]
    public void LargeTransfer_OverLossyReorderingLink_ArrivesIntact()
    {
        using var link = new LossyLink(seed: 123, drop: 0.20);
        var (a, b) = link.Connect();

        var blob = new byte[256_000];
        new Random(2024).NextBytes(blob);

        var writer = Task.Run(() => { a.Write(blob, 0, blob.Length); });
        var got = ReadExactly(b, blob.Length);
        writer.Wait(TimeSpan.FromSeconds(60));

        Assert.Equal(blob, got);
    }

    [Fact]
    public void Bidirectional_Simultaneous()
    {
        using var link = new LossyLink(seed: 9, drop: 0.10);
        var (a, b) = link.Connect();

        var msgA = new byte[40_000]; new Random(1).NextBytes(msgA);
        var msgB = new byte[40_000]; new Random(2).NextBytes(msgB);

        var wa = Task.Run(() => a.Write(msgA, 0, msgA.Length));
        var wb = Task.Run(() => b.Write(msgB, 0, msgB.Length));
        var gotB = Task.Run(() => ReadExactly(b, msgA.Length));
        var gotA = Task.Run(() => ReadExactly(a, msgB.Length));

        Assert.True(Task.WaitAll([wa, wb, gotA, gotB], TimeSpan.FromSeconds(60)));
        Assert.Equal(msgA, gotB.Result);
        Assert.Equal(msgB, gotA.Result);
    }

    [Fact]
    public void ReadTimeout_BoundsAPeerThatAcksButNeverSends()
    {
        // The KI-6 mechanism: with nothing of ours unacked, the dead-link detector never fires,
        // so a peer that withholds application bytes used to hold a blocked Read forever. With a
        // read timeout the silence surfaces as an IOException instead.
        using var silent = new ReliableUdpStream(_ => { });
        Assert.True(silent.CanTimeout);
        Assert.Equal(Timeout.Infinite, silent.ReadTimeout); // default behavior unchanged
        Assert.Throws<ArgumentOutOfRangeException>(() => silent.ReadTimeout = 0);

        silent.ReadTimeout = 150;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<System.IO.IOException>(() => silent.Read(new byte[8], 0, 8));
        Assert.InRange(sw.ElapsedMilliseconds, 100, 10_000); // timed out, not instant, not hung
    }

    [Fact]
    public void ReadTimeout_DataArrivingWithinTheWindow_ReadsNormally()
    {
        using var stream = new ReliableUdpStream(_ => { });
        stream.ReadTimeout = 5000;

        // Feed one in-order DATA segment ([type][seq:u32 BE][payload]) while a Read is blocked.
        var seg = new byte[] { 0x01, 0, 0, 0, 0, 42, 43, 44 };
        var feeder = Task.Run(() => { Thread.Sleep(50); stream.OnDatagram(seg); });

        var buffer = new byte[8];
        int n = stream.Read(buffer, 0, buffer.Length);
        feeder.Wait(TimeSpan.FromSeconds(10));

        Assert.Equal(3, n);
        Assert.Equal(new byte[] { 42, 43, 44 }, Slice(buffer, 0, 3));
    }

    [Fact]
    public void RealHandshake_RunsOverThePunchStreams()
    {
        using var link = new LossyLink(seed: 555, drop: 0.15);
        var (hostStream, clientStream) = link.Connect();
        var hostCh = new ControlChannel(hostStream);
        var clientCh = new ControlChannel(clientStream);

        var hostState = new byte[120_000];
        new Random(1234).NextBytes(hostState);

        PeerIdentity Id() => new(1, "ROMHASH", "GPGX", "2.11.1.0", "SYNC1", new[] { "L0", "L1" }, true, 20);

        var hostTask = Task.Run(() =>
            Handshake.RunHost(hostCh, Id(), new SessionPreferences(3, wantRollback: true), hostState, 47800));
        var clientParams = Handshake.RunClient(clientCh, Id(), new SessionPreferences(2, wantRollback: true), 51000);
        var hostParams = hostTask.GetAwaiter().GetResult();

        Assert.Equal(SyncMode.Rollback, clientParams.Mode);
        Assert.Equal(3, clientParams.InputDelay);
        Assert.Equal(hostState, clientParams.InitialState); // ~1 MiB-class transfer survived 15% loss
        Assert.Equal(51000, hostParams.RemoteUdpPort);
        Assert.Equal(47800, clientParams.RemoteUdpPort);
    }

    [Fact]
    public void MalformedAck_BeyondSendWindow_DoesNotHangOrCorrupt()
    {
        // A corrupt/hostile SegAck acking far beyond what we've sent must be ignored: the old code
        // looped _sendBase up to the ack (billions of iterations under the gate = hang) and drove
        // _sendBase past _nextSeq, wedging the sender. The stream must shrug it off and keep working.
        var captured = new List<byte[]>();
        var sender = new ReliableUdpStream(seg => captured.Add(seg));

        var poison = new byte[5];
        poison[0] = 0x02; // SegAck
        poison[1] = poison[2] = poison[3] = poison[4] = 0xFF; // ack = uint.MaxValue

        var fed = Task.Run(() => sender.OnDatagram(poison));
        Assert.True(fed.Wait(TimeSpan.FromSeconds(5)), "OnAck spun on an out-of-range ack (hang)");

        // Still healthy: a normal write goes out and a legitimate cumulative ACK frees the window.
        var blob = new byte[3000];
        new Random(11).NextBytes(blob);
        sender.Write(blob, 0, blob.Length);
        Assert.True(captured.Count > 0, "sender stopped emitting DATA after the bad ack");

        uint highestSeq = 0;
        foreach (var seg in captured)
            if (seg.Length >= 5 && seg[0] == 0x01) // SegData
                highestSeq = Math.Max(highestSeq, ((uint)seg[1] << 24) | ((uint)seg[2] << 16) | ((uint)seg[3] << 8) | seg[4]);

        var ack = new byte[5];
        ack[0] = 0x02;
        uint next = highestSeq + 1;
        ack[1] = (byte)(next >> 24); ack[2] = (byte)(next >> 16); ack[3] = (byte)(next >> 8); ack[4] = (byte)next;
        var acked = Task.Run(() => sender.OnDatagram(ack));
        Assert.True(acked.Wait(TimeSpan.FromSeconds(5)), "a valid follow-up ack hung after the poison ack");
    }

    /// <summary>
    /// A single lost segment must be repaired from the duplicate ACKs its successors provoke, not
    /// by waiting out the retransmit timer.
    ///
    /// The timer starts at 500ms and doubles to 1500, and resends the sixteen lowest unacked
    /// segments each time — so before fast retransmit, every hole cost a full RTO and dragged
    /// fifteen duplicates along with it. A 16MiB state over a lossy punched link is thousands of
    /// holes; at up to 1.5s each the transfer outlives the receiver's budget and the resync fails
    /// on a link that was merely lossy. This asserts the repair lands well inside one RTO, so a
    /// regression to timer-only recovery fails here rather than in a real session.
    /// </summary>
    [Fact]
    public void ASingleLostSegmentIsRepairedWithoutWaitingForTheRetransmitTimer()
    {
        // Hand-driven link: the test decides exactly which segment is lost, so the timing claim is
        // about the repair mechanism rather than about a random draw.
        var toReceiver = new List<byte[]>();
        var toSender = new List<byte[]>();
        var sender = new ReliableUdpStream(seg => { lock (toReceiver) toReceiver.Add((byte[])seg.Clone()); });
        var receiver = new ReliableUdpStream(seg => { lock (toSender) toSender.Add((byte[])seg.Clone()); });
        try
        {
            var payload = new byte[4096];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
            var writer = Task.Run(() => sender.Write(payload, 0, payload.Length));

            List<byte[]> outbound;
            var spin = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                lock (toReceiver) outbound = new List<byte[]>(toReceiver);
                if (outbound.Count >= 4) break;
                Assert.True(spin.ElapsedMilliseconds < 5000, "the sender never queued enough segments");
                Thread.Sleep(1);
            }

            // Drop the FIRST segment; deliver the rest. Each one the receiver takes re-ACKs the
            // still-missing base, which is the duplicate-ACK train fast retransmit reacts to.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 1; i < outbound.Count; i++) receiver.OnDatagram(outbound[i]);
            lock (toSender)
            {
                foreach (var ack in toSender) sender.OnDatagram(ack);
                toSender.Clear();
            }

            // The retransmitted base should now be sitting in the sender's outbox.
            byte[]? repaired = null;
            lock (toReceiver)
                for (int i = outbound.Count; i < toReceiver.Count; i++)
                    if (toReceiver[i].Length == outbound[0].Length
                        && toReceiver[i][0] == outbound[0][0]
                        && toReceiver[i][1] == outbound[0][1] && toReceiver[i][2] == outbound[0][2]
                        && toReceiver[i][3] == outbound[0][3] && toReceiver[i][4] == outbound[0][4])
                    { repaired = toReceiver[i]; break; }

            Assert.True(repaired != null,
                "the lost base was not retransmitted from the duplicate ACKs — only the timer would repair it");
            Assert.True(clock.ElapsedMilliseconds < 400,
                $"repair took {clock.ElapsedMilliseconds}ms, which is timer territory (RTO starts at 500ms)");

            // And it really does repair: the receiver can now read the whole payload back.
            receiver.OnDatagram(repaired!);
            Assert.Equal(payload, ReadExactly(receiver, payload.Length));
            Assert.True(writer.Wait(TimeSpan.FromSeconds(5)), "the writer never completed");
        }
        finally { sender.Dispose(); receiver.Dispose(); }
    }

    private static byte[] ReadExactly(ReliableUdpStream s, int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) throw new Exception($"EOF after {read}/{count}");
            read += n;
        }
        return buf;
    }
}
