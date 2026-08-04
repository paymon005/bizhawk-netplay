using System;
using System.Collections.Concurrent;
using System.Linq;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Divergence learning: measure which memory is machine-produced by comparing per-bucket hashes
/// between peers standing on identical states, mask exactly that, and refuse to mask a real
/// desync. The property everything rests on: right after a rebuild the peers are byte-identical,
/// so a disagreeing bucket can only hold bytes some machine rendered for itself.
/// </summary>
public class DivergenceLearnerTests
{
    private const int Buckets = ControlMessageCodec.DivergenceBuckets;

    private static uint[] Vector(uint fill)
    {
        var v = new uint[Buckets];
        for (int i = 0; i < v.Length; i++) v[i] = fill;
        return v;
    }

    private static uint[] VectorWith(uint fill, params (int index, uint value)[] overrides)
    {
        var v = Vector(fill);
        foreach (var (index, value) in overrides) v[index] = value;
        return v;
    }

    [Fact]
    public void IdenticalPeersLearnNothing()
    {
        var learner = new DivergenceLearner(expectedReports: 2);
        for (int round = 1; round <= DivergenceLearner.LearnRounds; round++)
        {
            int frame = round * 300;
            Assert.Equal(DivergenceVerdict.Learning, learner.Record(frame, 0, Vector(7)));
            var verdict = learner.Record(frame, 1, Vector(7));
            if (round < DivergenceLearner.LearnRounds)
                Assert.Equal(DivergenceVerdict.Learning, verdict);
            else
                Assert.Equal(DivergenceVerdict.NothingDiverges, verdict);
        }
        Assert.Equal(0.0, learner.MaskedShare);
    }

    [Fact]
    public void AStableFramebufferFootprintBecomesTheMask_UnionCatchesDoubleBuffering()
    {
        var learner = new DivergenceLearner(expectedReports: 2);
        // Round 1: buckets 10-11 disagree (buffer A being resolved). Round 2: buckets 20-21
        // (buffer B — the pair alternates per boundary). Round 3: buffer A again. The union is
        // what a VI-register read could never see: both halves of the double-buffered pair.
        learner.Record(300, 0, Vector(1));
        learner.Record(300, 1, VectorWith(1, (10, 99), (11, 99)));
        learner.Record(600, 0, Vector(1));
        learner.Record(600, 1, VectorWith(1, (20, 99), (21, 99)));
        learner.Record(900, 0, Vector(1));
        var verdict = learner.Record(900, 1, VectorWith(1, (10, 77), (11, 77)));

        Assert.Equal(DivergenceVerdict.MaskLearned, verdict);
        var mask = learner.MaskBuckets;
        Assert.True(mask[10] && mask[11] && mask[20] && mask[21]);
        Assert.Equal(4, mask.Count(b => b));
    }

    [Fact]
    public void ARealDesyncBlowsTheCapAndIsRefused()
    {
        var learner = new DivergenceLearner(expectedReports: 2);
        // Emulation divergence spreads through memory: most buckets disagree. Masking that would
        // blind the checksum to the very thing it exists to catch.
        var diverged = new uint[Buckets];
        for (int i = 0; i < Buckets; i++) diverged[i] = (uint)(i * 31 + 5);
        learner.Record(300, 0, Vector(1));
        learner.Record(300, 1, diverged);
        learner.Record(600, 0, Vector(1));
        learner.Record(600, 1, diverged);
        learner.Record(900, 0, Vector(1));
        var verdict = learner.Record(900, 1, diverged);

        Assert.Equal(DivergenceVerdict.TooBroadToMask, verdict);
        Assert.True(learner.MaskedShare > DivergenceLearner.MaxMaskedShare);
    }

    [Fact]
    public void ReportsCompleteOutOfOrderAndAcrossPorts()
    {
        // 3 active players; boundaries complete in whatever order the control links deliver.
        var learner = new DivergenceLearner(expectedReports: 3);
        Assert.Equal(DivergenceVerdict.Learning, learner.Record(600, 2, Vector(4)));
        Assert.Equal(DivergenceVerdict.Learning, learner.Record(300, 0, Vector(4)));
        Assert.Equal(DivergenceVerdict.Learning, learner.Record(300, 1, Vector(4)));
        Assert.Equal(DivergenceVerdict.Learning, learner.Record(600, 0, Vector(4)));
        Assert.Equal(DivergenceVerdict.Learning, learner.Record(300, 2, Vector(4))); // round 1 done
        Assert.Equal(DivergenceVerdict.Learning, learner.Record(600, 1, Vector(4))); // round 2 done
        Assert.Equal(DivergenceVerdict.Learning, learner.Record(900, 1, Vector(4)));
        Assert.Equal(DivergenceVerdict.Learning, learner.Record(900, 2, Vector(4)));
        Assert.Equal(DivergenceVerdict.NothingDiverges, learner.Record(900, 0, Vector(4)));
    }

    [Fact]
    public void TheLearnWindowIsTheFirstBoundariesOnly()
    {
        Assert.False(DivergenceLearner.IsLearnFrame(0, 300));    // frame 0 is the import itself
        Assert.True(DivergenceLearner.IsLearnFrame(300, 300));
        Assert.True(DivergenceLearner.IsLearnFrame(900, 300));
        Assert.False(DivergenceLearner.IsLearnFrame(1200, 300));
        Assert.False(DivergenceLearner.IsLearnFrame(300, 0));    // no interval, no window
    }

    [Fact]
    public void MaskRangesAreWordAlignedContiguousRuns()
    {
        var mask = new bool[Buckets];
        mask[3] = mask[4] = mask[5] = true;
        mask[9] = true;
        const long size = 8 * 1024 * 1024;
        long span = DivergenceLearner.BucketSpan(size, Buckets);
        Assert.Equal(0, span % 4);

        var ranges = DivergenceLearner.MaskRanges(mask, size);
        Assert.Equal(2, ranges.Count);
        Assert.Equal((3 * span, 6 * span), ranges[0]);
        Assert.Equal((9 * span, 10 * span), ranges[1]);
    }

    [Fact]
    public void WireRoundTrips()
    {
        var generation = new SessionGeneration(5UL, 2);
        var vector = VectorWith(3, (0, 1u), (255, 0xFFFFFFFFu));
        var report = ControlMessageCodec.EncodeDivergenceReport(generation, 600, vector);
        Assert.True(ControlMessageCodec.TryDecodeDivergenceReport(report, out var g1, out int f1, out var v1));
        Assert.Equal(generation, g1);
        Assert.Equal(600, f1);
        Assert.Equal(vector, v1);

        var mask = new bool[Buckets];
        mask[0] = mask[7] = mask[128] = mask[255] = true;
        var body = ControlMessageCodec.EncodeExclusionMask(generation, 1500, mask);
        Assert.True(ControlMessageCodec.TryDecodeExclusionMask(body, out var g2, out int from, out var m2));
        Assert.Equal(generation, g2);
        Assert.Equal(1500, from);
        Assert.Equal(mask, m2);

        Assert.False(ControlMessageCodec.TryDecodeDivergenceReport(new byte[7], out _, out _, out _));
        Assert.False(ControlMessageCodec.TryDecodeExclusionMask(new byte[7], out _, out _, out _));
    }

    // ---- the rollback bucket path -----------------------------------------------------------

    private sealed class Hub : ITransport
    {
        private readonly ConcurrentQueue<byte[]> _inbound = new();
        private ConcurrentQueue<byte[]>[] _others = [];
        public void Connect(Hub[] all) =>
            _others = [.. all.Where(t => !ReferenceEquals(t, this)).Select(t => t._inbound)];
        public void Send(byte[] datagram) { foreach (var q in _others) q.Enqueue(datagram); }
        public bool TryReceive(out byte[] datagram) => _inbound.TryDequeue(out datagram!);
    }

    private static PortInput Btn(bool pressed)
    {
        var b = new bool[8];
        b[0] = pressed;
        return new PortInput(b, []);
    }

    [Fact]
    public void TwoRollbackPeersProduceIdenticalBoundaryHashesAndBuckets()
    {
        const int interval = 100;
        var hubs = new[] { new Hub(), new Hub() };
        foreach (var hub in hubs) hub.Connect(hubs);
        var session = new (FrameDriver driver, FakeEmuAdapter emu)[2];
        for (int i = 0; i < 2; i++)
        {
            var emu = new FakeEmuAdapter(portCount: 2);
            int port = i;
            emu.LocalInputScript = frame => Btn((frame % (port + 2)) == 0);
            var driver = new FrameDriver(emu, hubs[i],
                p => new RollbackStrategy(p, emu, port, maxRollback: 4, frameMs: 0,
                    new RollbackTuning
                    {
                        ElideConfirmedSaves = true,
                        ChecksumAnchorInterval = interval,
                    }),
                localPort: i, delay: 2, redundancy: 8, rollbackWindow: 4);
            driver.Start();
            session[i] = (driver, emu);
        }

        for (int step = 0; step < 320; step++)
            foreach (var (driver, emu) in session)
                if (driver.OnPreFrame() == FrameStep.Ran)
                {
                    emu.AdvanceAppliedFrame();
                    driver.OnPostFrame();
                }

        var sinks = new[] { new uint[Buckets], new uint[Buckets] };
        var frames = new int[2];
        var hashes = new uint[2];
        for (int i = 0; i < 2; i++)
        {
            var rb = (RollbackStrategy)session[i].driver.Strategy;
            Assert.True(rb.TryConfirmedChecksumWithBuckets(interval, sinks[i],
                out frames[i], out hashes[i], out bool filled),
                $"peer {i} produced no confirmed boundary");
            Assert.True(filled, $"peer {i}'s adapter reported no buckets");
        }

        // Same boundary, same hash, same vector — the pair the host compares, off the very state
        // the checksum describes (the visit is forced; the anchor cache cannot supply buckets).
        Assert.Equal(frames[0], frames[1]);
        Assert.Equal(hashes[0], hashes[1]);
        Assert.Equal(sinks[0], sinks[1]);
    }
}
