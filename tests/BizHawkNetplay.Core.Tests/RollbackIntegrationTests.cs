using System;
using System.Collections.Generic;
using System.Linq;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// End-to-end rollback over two fake cores wired by a latency-injecting loopback — no sockets,
/// no EmuHawk. Rollback's whole reason to exist is hiding latency, so these drive the strategy
/// under real one-way delay (and loss) and prove three things:
///   (1) Correctness — every <em>finalized</em> frame (all real inputs arrived) applied exactly the
///       inputs an ideal lockstep run would have, verified against an analytic oracle. Prediction
///       may momentarily show the wrong thing, but it is always corrected before it becomes final.
///   (2) It works — rollback keeps advancing under delay where lockstep would sit and stall.
///   (3) It is honest — predictions that hold cost zero rollbacks; only contradictions do; and the
///       savestate ring stays bounded (no per-frame leak).
/// </summary>
public class RollbackIntegrationTests
{
    private const int Delay = 2;
    private const int Redundancy = 8;
    private const int MaxRollback = 16;

    private static PortInput Btn(bool a, bool b = false)
    {
        var arr = new bool[8];
        arr[0] = a;
        arr[1] = b;
        return new PortInput(arr, Array.Empty<int>());
    }

    private static PortInput Neutral() => Btn(false, false);

    // Distinct, frequently-changing input scripts so predictions are often contradicted under delay.
    private static readonly Func<int, PortInput>[] Scripts =
    {
        frame => Btn((frame % 7) < 3, (frame % 11) == 0),
        frame => Btn((frame % 5) == 0, (frame % 13) < 4),
    };

    /// <summary>Shared wall clock (in ticks) the latency links compare delivery times against.</summary>
    private sealed class Clock { public long Tick; }

    /// <summary>Loopback that holds each datagram for a fixed one-way latency (in ticks) before it
    /// becomes receivable; optional drop predicate layers loss on top.</summary>
    private sealed class LatencyLink : ITransport
    {
        private readonly Clock _clock;
        private readonly Queue<(long at, byte[] data)> _inbound = new Queue<(long, byte[])>();
        private LatencyLink _peer = null!;
        private readonly Func<byte[], bool>? _drop;
        public int Latency;

        private LatencyLink(Clock clock, int latency, Func<byte[], bool>? drop)
        {
            _clock = clock;
            Latency = latency;
            _drop = drop;
        }

        public static (LatencyLink a, LatencyLink b) Pair(Clock clock, int latency,
            Func<byte[], bool>? dropA = null, Func<byte[], bool>? dropB = null)
        {
            var a = new LatencyLink(clock, latency, dropA);
            var b = new LatencyLink(clock, latency, dropB);
            a._peer = b;
            b._peer = a;
            return (a, b);
        }

        public void Send(byte[] datagram)
        {
            if (_drop != null && _drop(datagram)) return;
            _peer._inbound.Enqueue((_clock.Tick + _peer.Latency, (byte[])datagram.Clone()));
        }

        public bool TryReceive(out byte[] datagram)
        {
            if (_inbound.Count > 0 && _inbound.Peek().at <= _clock.Tick)
            {
                datagram = _inbound.Dequeue().data;
                return true;
            }
            datagram = null!;
            return false;
        }
    }

    private sealed class Instance
    {
        public FakeEmuAdapter Emu = null!;
        public FrameDriver Driver = null!;
        public RollbackStrategy Rollback = null!;
        public int Stalls;

        public void Step()
        {
            if (Driver.OnPreFrame() == FrameStep.Ran)
            {
                Emu.AdvanceAppliedFrame();
                Driver.OnPostFrame();
            }
            else Stalls++;
        }
    }

    private static Instance BuildRollback(ITransport t, int localPort, double frameMs = 0,
        RollbackTuning? tuning = null, int maxRollback = MaxRollback)
    {
        var emu = new FakeEmuAdapter(portCount: 2) { LocalInputScript = Scripts[localPort] };
        var driver = new FrameDriver(emu, t,
            p => new RollbackStrategy(p, emu, localPort, maxRollback, frameMs, tuning),
            localPort: localPort, delay: Delay, redundancy: Redundancy, rollbackWindow: maxRollback);
        var inst = new Instance { Emu = emu, Driver = driver, Rollback = (RollbackStrategy)driver.Strategy };
        driver.Start();
        return inst;
    }

    /// <summary>Elision on, with checksum anchors at the interval the test polls.</summary>
    private static RollbackTuning Eliding(int checksumInterval = 0) => new RollbackTuning
    {
        ElideConfirmedSaves = true,
        ChecksumAnchorInterval = checksumInterval,
    };

    private static Instance BuildLockstep(ITransport t, int localPort)
    {
        var emu = new FakeEmuAdapter(portCount: 2) { LocalInputScript = Scripts[localPort] };
        var driver = new FrameDriver(emu, t, p => new LockstepStrategy(p),
            localPort: localPort, delay: Delay, redundancy: Redundancy);
        var inst = new Instance { Emu = emu, Driver = driver, Rollback = null! };
        driver.Start();
        return inst;
    }

    private static void Run(Clock clock, Instance a, Instance b, int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            clock.Tick = i;
            a.Step();
            b.Step();
        }
    }

    /// <summary>Every frame below the finalized frontier must hold the exact inputs an ideal
    /// (all-real-input) run would apply. Prediction is allowed above it, never below.</summary>
    private static void AssertFinalizedCorrect(Instance inst, int upTo)
    {
        Assert.True(upTo > 100, $"not enough finalized progress to be meaningful (upTo={upTo})");
        for (int g = 0; g < upTo; g++)
        {
            Assert.True(inst.Emu.LastInputByFrame.TryGetValue(g, out var applied), $"frame {g} never ran");
            for (int p = 0; p < Scripts.Length; p++)
            {
                var expected = g < Delay ? Neutral() : Scripts[p](g - Delay);
                Assert.True(expected.ValueEquals(applied!.Ports[p]),
                    $"finalized input mismatch at frame {g} port {p}");
            }
        }
    }

    [Fact]
    public void ZeroLatency_RollbackReducesToLockstep_NoRollbacks()
    {
        // With instant delivery every remote input is present before its frame runs, so rollback
        // never predicts: it must behave exactly like lockstep — full speed, zero corrections.
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: 0);
        var a = BuildRollback(ta, 0);
        var b = BuildRollback(tb, 1);

        Run(clock, a, b, 320);

        Assert.Equal(a.Driver.CurrentFrame, b.Driver.CurrentFrame);
        Assert.True(a.Driver.CurrentFrame >= 300, $"reached only {a.Driver.CurrentFrame}");
        Assert.Equal(0, a.Rollback.RollbackCount);
        Assert.Equal(0, b.Rollback.RollbackCount);
        Assert.Equal(0, a.Stalls);
        Assert.Equal(0, b.Stalls);
        AssertFinalizedCorrect(a, a.Driver.CurrentFrame - 2);
        AssertFinalizedCorrect(b, b.Driver.CurrentFrame - 2);
        Assert.Equal(a.Emu.HashMainMemory(), b.Emu.HashMainMemory());
    }

    [Fact]
    public void ZeroLatency_ConfirmedFramesTakeNoSavestates()
    {
        // The point of elision: with every input present before its frame runs, no frame can ever
        // be a rollback target, so the ring should cost nothing at all. This is the steady state on
        // any link whose input delay covers its latency — i.e. the case rollback normally runs in.
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: 0);
        var a = BuildRollback(ta, 0, tuning: Eliding());
        var b = BuildRollback(tb, 1, tuning: Eliding());

        Run(clock, a, b, 320);

        Assert.True(a.Driver.CurrentFrame >= 300, $"reached only {a.Driver.CurrentFrame}");
        Assert.Equal(0, a.Rollback.RollbackCount);
        // Frames before the first inputs land are genuinely unconfirmed and are still anchored;
        // everything after must be free. Without elision this run takes a save every single frame.
        Assert.True(a.Rollback.SavesTaken <= Delay + 2,
            $"expected the ring to go quiet, took {a.Rollback.SavesTaken} saves");
        Assert.True(a.Rollback.SavesElided > 300 - Delay - 2,
            $"expected nearly every frame elided, got {a.Rollback.SavesElided}");
        AssertFinalizedCorrect(a, a.Driver.CurrentFrame - 2);
        Assert.Equal(a.Emu.HashMainMemory(), b.Emu.HashMainMemory());
    }

    [Fact]
    public void Elision_IsUnobservable_UnderLatencyAndLoss()
    {
        // The real proof: elision may not change a single applied input or the resulting state,
        // however much correction traffic is flying around. Same seed, same scripts, same link —
        // only the savestate policy differs, and the two runs must be indistinguishable.
        const int k = 5;
        const int iters = 500;

        (Dictionary<int, InputSet> inputs, uint hash, int rollbacks) RunWith(RollbackTuning? tuning)
        {
            var clock = new Clock();
            var (ta, tb) = LatencyLink.Pair(clock, latency: k,
                dropA: d => d.Length % 7 == 3, dropB: d => d.Length % 11 == 5);
            var a = BuildRollback(ta, 0, tuning: tuning);
            var b = BuildRollback(tb, 1, tuning: tuning);
            Run(clock, a, b, iters);
            return (a.Emu.LastInputByFrame, a.Emu.HashMainMemory(), a.Rollback.RollbackCount);
        }

        var plain = RunWith(null);
        var elided = RunWith(Eliding());

        Assert.True(plain.rollbacks > 0, "the scenario must actually exercise corrections");
        Assert.Equal(plain.rollbacks, elided.rollbacks);
        Assert.Equal(plain.hash, elided.hash);
        Assert.Equal(plain.inputs.Count, elided.inputs.Count);
        foreach (var kv in plain.inputs)
        {
            Assert.True(elided.inputs.TryGetValue(kv.Key, out var other), $"frame {kv.Key} missing");
            for (int p = 0; p < Scripts.Length; p++)
                Assert.True(kv.Value.Ports[p].ValueEquals(other!.Ports[p]),
                    $"elision changed the input applied at frame {kv.Key} port {p}");
        }
    }

    /// <summary>Elision on, plus snapshots only every <paramref name="every"/>th predicted frame.</summary>
    private static RollbackTuning Keyframed(int every, int checksumInterval = 0) => new RollbackTuning
    {
        ElideConfirmedSaves = true,
        ChecksumAnchorInterval = checksumInterval,
        KeyframeInterval = every,
    };

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SparseKeyframes_AreUnobservable_UnderLatencyAndLoss(int every)
    {
        // The load-bearing test. Snapshotting one predicted frame in N means a repair restarts from
        // further back and replays more frames than it strictly needs — which must land in exactly
        // the same place, or the whole idea is a desync generator. Same seed, same scripts, same
        // link, same drops; only the snapshot policy differs.
        const int k = 5;
        const int iters = 500;

        // Peer hashes are deliberately NOT compared: under latency the two sit at different frames
        // with different predicted tails, so they agree on finalized frames and nowhere else.
        // AssertFinalizedCorrect is the oracle for that; this compares one peer across policies.
        (Dictionary<int, InputSet> inputs, uint hash, int rollbacks, int saves, long resim, long walked,
         FakeEmuAdapter adapter) RunWith(RollbackTuning t)
        {
            var clock = new Clock();
            var (ta, tb) = LatencyLink.Pair(clock, latency: k,
                dropA: d => d.Length % 7 == 3, dropB: d => d.Length % 11 == 5);
            var a = BuildRollback(ta, 0, tuning: t);
            var b = BuildRollback(tb, 1, tuning: t);
            Run(clock, a, b, iters);
            return (a.Emu.LastInputByFrame, a.Emu.HashMainMemory(), a.Rollback.RollbackCount,
                    a.Rollback.SavesTaken, a.Rollback.FramesResimulated, a.Rollback.FramesWalkedBack,
                    a.Emu);
        }

        var dense = RunWith(Eliding());
        var sparse = RunWith(Keyframed(every));

        // Guards that the run actually exercised the new path. Skipped snapshots is the right
        // measure, not walk-back: at N=2 this scenario's corrections happen to land on keyframes
        // every time and walk back zero frames, which is a property of where the drops fall rather
        // than of the policy. Frames re-simulated may therefore stay equal, never fall.
        // The whole suite runs against an adapter that reuses released state buffers and poisons
        // them on the way back, mirroring what the real one now does to keep 26.5MB/s of whole-core
        // snapshots off the Large Object Heap. That coverage is only worth anything if buffers are
        // genuinely being handed back and rewritten, so assert it where snapshot churn is highest.
        Assert.True(dense.adapter.SaveCount > dense.adapter.StateBuffersAllocated,
            $"the pool never recycled: {dense.adapter.SaveCount} saves from " +
            $"{dense.adapter.StateBuffersAllocated} buffers — reuse is untested");

        Assert.True(dense.rollbacks > 0, "the scenario must actually exercise corrections");
        Assert.Equal(0, dense.walked);   // every predicted frame is its own base
        Assert.True(sparse.saves < dense.saves,
            $"keyframes every {every} skipped no snapshots ({sparse.saves} against {dense.saves})");
        Assert.True(sparse.resim >= dense.resim, "walking back can never re-simulate fewer frames");

        Assert.Equal(dense.rollbacks, sparse.rollbacks);
        Assert.Equal(dense.hash, sparse.hash);
        Assert.Equal(dense.inputs.Count, sparse.inputs.Count);
        foreach (var kv in dense.inputs)
        {
            Assert.True(sparse.inputs.TryGetValue(kv.Key, out var other), $"frame {kv.Key} missing");
            for (int p = 0; p < Scripts.Length; p++)
                Assert.True(kv.Value.Ports[p].ValueEquals(other!.Ports[p]),
                    $"keyframing changed the input applied at frame {kv.Key} port {p}");
        }
    }

    [Fact]
    public void SparseKeyframes_TradeSnapshotsForReSimulatedFrames()
    {
        // The trade the whole change exists to make, in the units it is made in: snapshots down,
        // re-simulated frames up. On N64 a snapshot is 6.82ms against a 2.41ms frame, so paying
        // fewer than three frames per snapshot avoided is the win.
        const int iters = 500;

        (int saves, long resim) RunWith(RollbackTuning t)
        {
            var clock = new Clock();
            var (ta, tb) = LatencyLink.Pair(clock, latency: 5,
                dropA: d => d.Length % 7 == 3, dropB: d => d.Length % 11 == 5);
            var a = BuildRollback(ta, 0, tuning: t);
            var b = BuildRollback(tb, 1, tuning: t);
            Run(clock, a, b, iters);
            return (a.Rollback.SavesTaken, a.Rollback.FramesResimulated);
        }

        var dense = RunWith(Eliding());
        var sparse = RunWith(Keyframed(2));

        Assert.True(sparse.saves < dense.saves,
            $"expected fewer snapshots, got {sparse.saves} against {dense.saves}");
        long framesPaid = sparse.resim - dense.resim;
        int savesAvoided = dense.saves - sparse.saves;
        Assert.True(framesPaid < savesAvoided * 3,
            $"paid {framesPaid} extra frames to avoid {savesAvoided} snapshots — worse than N64's ratio");
    }

    [Fact]
    public void SparseKeyframes_KeepABaseReachableAcrossRunsOfConfirmedFrames()
    {
        // Anchoring on `frame % N == 0` would be wrong, and this is the case that catches it: only
        // frames still carrying a prediction are candidates, so confirmed runs punch holes in the
        // sequence and a modulo rule can skip its own keyframe. Every predicted frame must have a
        // base within N-1 frames of it regardless. Bursty loss produces exactly that alternation of
        // confirmed and predicted stretches; a miss surfaces as the ring exception, not a bad hash.
        // Loss is counted rather than derived from datagram contents: a content-based predicate
        // drops every retransmission of the same datagram too, which redundancy cannot recover
        // from, and the session stalls instead of exercising the alternation this is about.
        const int k = 6;
        const int iters = 700;
        int ca = 0, cb = 0;
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: k,
            dropA: _ => (++ca % 4) == 0, dropB: _ => (++cb % 4) == 0);
        var a = BuildRollback(ta, 0, tuning: Keyframed(3, checksumInterval: 0));
        var b = BuildRollback(tb, 1, tuning: Keyframed(3, checksumInterval: 0));

        Run(clock, a, b, iters);

        Assert.True(a.Rollback.RollbackCount > 0, "no corrections ran, so nothing was proved");
        Assert.True(a.Rollback.FramesWalkedBack > 0, "no walk-back happened, so nothing was proved");
        int upTo = Math.Min(a.Driver.CurrentFrame, b.Driver.CurrentFrame) - k - Redundancy - Delay - 6;
        AssertFinalizedCorrect(a, upTo);
        AssertFinalizedCorrect(b, upTo);
    }

    [Fact]
    public void SparseKeyframes_SurviveACorrectionAtTheEdgeOfTheRing()
    {
        // A correction landing at the prediction horizon restarts from a snapshot up to N-1 frames
        // older still. If the prune window is not widened to match, that base has already been
        // released and ExecutePendingRollback throws. A shallow ring puts the horizon and the prune
        // floor close enough together for the difference to bite.
        const int k = 4;
        const int iters = 600;
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: k);
        var a = BuildRollback(ta, 0, tuning: Keyframed(4), maxRollback: 5);
        var b = BuildRollback(tb, 1, tuning: Keyframed(4), maxRollback: 5);

        Run(clock, a, b, iters);

        Assert.True(a.Rollback.RollbackCount > 0, "no corrections ran, so nothing was proved");
        int upTo = Math.Min(a.Driver.CurrentFrame, b.Driver.CurrentFrame) - k - Delay - 4;
        AssertFinalizedCorrect(a, upTo);
        AssertFinalizedCorrect(b, upTo);
    }

    [Fact]
    public void SparseKeyframes_DefaultToTheOriginalEveryFrameBehaviour()
    {
        // An omitted or nonsensical interval must be exactly the old behaviour, not an approximation
        // of it -- every existing caller and test depends on that.
        const int iters = 400;

        (uint hash, int saves, long walked) RunWith(RollbackTuning t)
        {
            var clock = new Clock();
            var (ta, tb) = LatencyLink.Pair(clock, latency: 5, dropA: d => d.Length % 7 == 3);
            var a = BuildRollback(ta, 0, tuning: t);
            var b = BuildRollback(tb, 1, tuning: t);
            Run(clock, a, b, iters);
            return (a.Emu.HashMainMemory(), a.Rollback.SavesTaken, a.Rollback.FramesWalkedBack);
        }

        var baseline = RunWith(Eliding());
        foreach (var interval in new[] { 0, 1 })
        {
            var same = RunWith(Keyframed(interval));
            Assert.Equal(baseline.hash, same.hash);
            Assert.Equal(baseline.saves, same.saves);
            Assert.Equal(0L, same.walked);
        }
    }

    [Fact]
    public void SparseKeyframes_StillAnchorEveryChecksumBoundary()
    {
        // Checksum anchors outrank the keyframe spacing: they are forced regardless of where the
        // interval happens to fall, or desync detection goes quiet on exactly the frames it needs.
        const int interval = 20;
        const int iters = 600;
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: 5);
        var a = BuildRollback(ta, 0, tuning: Keyframed(3, checksumInterval: interval));
        var b = BuildRollback(tb, 1, tuning: Keyframed(3, checksumInterval: interval));

        int checksums = 0;
        for (int i = 0; i < iters; i++)
        {
            clock.Tick = i;
            a.Step(); b.Step();
            if (a.Rollback.TryConfirmedChecksum(interval, out var fa, out var ha)
                && b.Rollback.TryConfirmedChecksum(interval, out var fb, out var hb))
            {
                Assert.Equal(fa, fb);
                Assert.Equal(ha, hb);
                checksums++;
            }
        }

        Assert.True(checksums > 10, $"expected checksums to keep flowing, got {checksums}");
    }

    [Fact]
    public void Elision_DoesNotStrandAppliedInputs()
    {
        // _applied used to be pruned as a passenger of the state ring. With most frames no longer
        // having a state, riding along would leak an InputSet per frame for the whole session.
        const int iters = 600;
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: 5);
        var a = BuildRollback(ta, 0, tuning: Eliding());
        var b = BuildRollback(tb, 1, tuning: Eliding());

        Run(clock, a, b, iters);

        Assert.True(a.Emu.LiveStates.Count <= MaxRollback + 6,
            $"ring leaked: {a.Emu.LiveStates.Count} live states");
        Assert.True(a.Rollback.AppliedCount <= MaxRollback + 8,
            $"applied-input map leaked: {a.Rollback.AppliedCount} entries after {iters} frames");
    }

    [Fact]
    public void Latency_TriggersRollbacks_ButFinalizedFramesStayCorrect()
    {
        const int k = 5;
        const int iters = 500;
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: k);
        var a = BuildRollback(ta, 0);
        var b = BuildRollback(tb, 1);

        Run(clock, a, b, iters);

        // Rollback hides the delay: both advance the full run rather than stalling behind it.
        Assert.True(a.Driver.CurrentFrame >= iters - 5 && b.Driver.CurrentFrame >= iters - 5,
            $"progress A={a.Driver.CurrentFrame} B={b.Driver.CurrentFrame}");
        // Real delay + changing inputs means predictions genuinely get contradicted.
        Assert.True(a.Rollback.RollbackCount > 0, "expected rollbacks to fire under latency");
        Assert.True(b.Rollback.RollbackCount > 0, "expected rollbacks to fire under latency");

        // Everything below the confirmed frontier is provably correct despite the mispredictions.
        int upTo = Math.Min(a.Driver.CurrentFrame, b.Driver.CurrentFrame) - k - Delay - 4;
        AssertFinalizedCorrect(a, upTo);
        AssertFinalizedCorrect(b, upTo);
    }

    [Fact]
    public void Latency_RollbackOutrunsLockstep()
    {
        const int k = 5;
        const int iters = 300;

        var rc = new Clock();
        var (ra, rb) = LatencyLink.Pair(rc, latency: k);
        var rollA = BuildRollback(ra, 0);
        var rollB = BuildRollback(rb, 1);
        Run(rc, rollA, rollB, iters);

        var lc = new Clock();
        var (la, lb) = LatencyLink.Pair(lc, latency: k);
        var lockA = BuildLockstep(la, 0);
        var lockB = BuildLockstep(lb, 1);
        Run(lc, lockA, lockB, iters);

        // The whole point: under the same delay, rollback runs to the present while lockstep is
        // dragged back by having to wait for every remote input.
        Assert.True(rollA.Driver.CurrentFrame > lockA.Driver.CurrentFrame + k,
            $"rollback {rollA.Driver.CurrentFrame} should clearly outrun lockstep {lockA.Driver.CurrentFrame}");
        Assert.True(lockA.Stalls > 0, "lockstep should have stalled under latency");
    }

    [Fact]
    public void HoldingPredictions_CostOnlyAStartupTransient()
    {
        // Both players hold a constant input. Repeat-last prediction is right as soon as the first
        // real input lands, so once past the neutral-seed→constant transition rollback stops
        // re-simulating entirely: only a tiny bounded startup transient remains, never ongoing work.
        const int k = 6;
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: k);
        var a = BuildRollback(ta, 0);
        var b = BuildRollback(tb, 1);
        a.Emu.LocalInputScript = _ => Btn(true, true);
        b.Emu.LocalInputScript = _ => Btn(true, false);

        for (int i = 0; i < 300; i++) { clock.Tick = i; a.Step(); b.Step(); }

        Assert.True(a.Driver.CurrentFrame >= 290, $"reached only {a.Driver.CurrentFrame}");
        // At most the seed→constant transition, and it resolves in one shot then never recurs.
        Assert.True(a.Rollback.RollbackCount <= 2, $"expected a tiny transient, got {a.Rollback.RollbackCount}");
        Assert.True(b.Rollback.RollbackCount <= 2, $"expected a tiny transient, got {b.Rollback.RollbackCount}");
        // The transient is bounded by the ring, not the run length — no ongoing resimulation.
        Assert.True(a.Rollback.FramesResimulated <= 2 * MaxRollback,
            $"resimulation should be a one-time transient, got {a.Rollback.FramesResimulated}");
    }

    [Fact]
    public void Latency_WithLoss_StaysCorrect()
    {
        // Latency plus every 4th datagram dropped (redundancy still covers it). Corrections may be
        // both late and lossy, yet the finalized prefix must remain exactly right.
        const int k = 4;
        const int iters = 600;
        int ca = 0, cb = 0;
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: k,
            dropA: _ => (++ca % 4) == 0,
            dropB: _ => (++cb % 4) == 0);
        var a = BuildRollback(ta, 0);
        var b = BuildRollback(tb, 1);

        Run(clock, a, b, iters);

        int upTo = Math.Min(a.Driver.CurrentFrame, b.Driver.CurrentFrame) - k - Redundancy - Delay - 6;
        AssertFinalizedCorrect(a, upTo);
        AssertFinalizedCorrect(b, upTo);
    }

    [Fact]
    public void OneWayLossBurst_RecoversViaGapRetransmission_InsteadOfFreezingForever()
    {
        // The F4 scenario: a one-way burst drops MORE consecutive datagrams than the redundant
        // window covers, so the frame the receiver still needs slides out of the sender's live
        // window and — without the gap-request path — would never be sent again. Both peers then
        // hit their prediction caps and freeze permanently. With retransmission the starved peer
        // asks for the missing run and both sides resume.
        const int k = 4;
        var clock = new Clock();
        bool burst = false;
        var (ta, tb) = LatencyLink.Pair(clock, latency: k, dropA: _ => burst);
        var a = BuildRollback(ta, 0);
        var b = BuildRollback(tb, 1);

        // Phase 1: healthy play.
        long tick = 0;
        for (int i = 0; i < 100; i++) { clock.Tick = ++tick; a.Step(); b.Step(); }
        Assert.True(b.Driver.CurrentFrame >= 90, $"warmup made no progress ({b.Driver.CurrentFrame})");

        // Phase 2: every datagram A sends is lost for well over a window's worth of frames.
        // B's frontier for A pins just below the first lost frame; both peers run to their caps.
        burst = true;
        for (int i = 0; i < 80; i++) { clock.Tick = ++tick; a.Step(); b.Step(); }
        Assert.True(b.Driver.IsStalled, "B should be cap-stalled while starved of A's input");

        // Phase 3: the link heals. Gap requests are wall-clock throttled (50 ms per port), so tick
        // with a real sleep until both sides have clearly resumed. Without the retransmit path this
        // loop runs to exhaustion with both drivers frozen at their caps.
        burst = false;
        int targetA = a.Driver.CurrentFrame + 60;
        int targetB = b.Driver.CurrentFrame + 60;
        for (int i = 0; i < 5000 && (a.Driver.CurrentFrame < targetA || b.Driver.CurrentFrame < targetB); i++)
        {
            clock.Tick = ++tick;
            a.Step();
            b.Step();
            System.Threading.Thread.Sleep(1);
        }

        Assert.True(a.Driver.CurrentFrame >= targetA && b.Driver.CurrentFrame >= targetB,
            $"stuck after loss burst: A={a.Driver.CurrentFrame}/{targetA} B={b.Driver.CurrentFrame}/{targetB} " +
            $"(A stalled={a.Driver.IsStalled}, B stalled={b.Driver.IsStalled})");

        // The repaired timeline must be byte-identical to an ideal lockstep run.
        int upTo = Math.Min(a.Driver.CurrentFrame, b.Driver.CurrentFrame) - k - Redundancy - Delay - 6;
        AssertFinalizedCorrect(a, upTo);
        AssertFinalizedCorrect(b, upTo);
    }

    [Fact]
    public void EarlyOneWayLoss_WithTimeSync_RecoversInsteadOfMutualSoftCapFreeze()
    {
        // The first real-internet session (host log: "time-sync yield at frame 17" forever):
        // the opening ~300ms of host→joiner input vanished into a pre-NAT candidate before the
        // punch confirmed a path. The hole then slid out of the sender's resend window — but
        // with time-sync active both peers stall at their SOFT caps (~latency+2 frames past the
        // frontier), far shallower than the depth-based gap-request trigger, so neither side
        // ever requested the missing frames and both froze forever, resends keeping the
        // liveness watchdog quiet. The hole-beyond-window trigger must break this deadlock.
        const int k = 3;
        const double frameMs = 16.0;
        var clock = new Clock();
        bool burst = true; // A→B traffic lost from the very first datagram
        var (ta, tb) = LatencyLink.Pair(clock, latency: k, dropA: _ => burst);
        var a = BuildRollback(ta, 0, frameMs);
        var b = BuildRollback(tb, 1, frameMs);
        // Lobby-measured RTT narrows both soft caps, exactly like the real session.
        a.Rollback.OnPacingReport(new PacingInfo(2 * k * frameMs, 0));
        b.Rollback.OnPacingReport(new PacingInfo(2 * k * frameMs, 0));

        long tick = 0;
        for (int i = 0; i < 15; i++) { clock.Tick = ++tick; a.Step(); b.Step(); }
        burst = false; // the punch confirms; the path opens — but the early frames are gone

        int targetA = a.Driver.CurrentFrame + 120;
        int targetB = b.Driver.CurrentFrame + 120;
        for (int i = 0; i < 5000 && (a.Driver.CurrentFrame < targetA || b.Driver.CurrentFrame < targetB); i++)
        {
            clock.Tick = ++tick;
            a.Step();
            b.Step();
            System.Threading.Thread.Sleep(1); // gap requests are wall-clock throttled
        }

        Assert.True(a.Driver.CurrentFrame >= targetA && b.Driver.CurrentFrame >= targetB,
            $"mutual soft-cap freeze after early one-way loss: A={a.Driver.CurrentFrame}/{targetA} " +
            $"B={b.Driver.CurrentFrame}/{targetB} (A stalled={a.Driver.IsStalled}, B stalled={b.Driver.IsStalled})");

        int upTo = Math.Min(a.Driver.CurrentFrame, b.Driver.CurrentFrame) - k - Redundancy - Delay - 6;
        AssertFinalizedCorrect(a, upTo);
        AssertFinalizedCorrect(b, upTo);
    }

    [Fact]
    public void UnrepairableHole_IsReported_SoTheSessionCanEndInsteadOfFreezing()
    {
        // KI-9: when a hole slides out of the peer's resend window AND the gap requests that
        // would repair it are lost too, nothing can ever unfreeze the session — but the frozen
        // windows keep arriving, so the arrival-based watchdog stays quiet. The driver must
        // surface the persisting hole so the tool can end with a clear error. When requests DO
        // get through, the hole must clear and never be reported.
        const int k = 3;
        const double frameMs = 16.0;
        var clock = new Clock();
        bool burst = true;
        bool suppressRequests = true; // B's 18-byte type-2 gap requests to A are lost
        var (ta, tb) = LatencyLink.Pair(clock, latency: k,
            dropA: _ => burst,
            dropB: d => suppressRequests && d.Length == 18 && d[0] == 2);
        var a = BuildRollback(ta, 0, frameMs);
        var b = BuildRollback(tb, 1, frameMs);
        a.Rollback.OnPacingReport(new PacingInfo(2 * k * frameMs, 0));
        b.Rollback.OnPacingReport(new PacingInfo(2 * k * frameMs, 0));

        long tick = 0;
        for (int i = 0; i < 15; i++) { clock.Tick = ++tick; a.Step(); b.Step(); }
        burst = false; // path heals, but the hole is beyond A's window and requests are dead

        // The hole must be reported with a growing age (wall-clock; hence the real sleeps).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan stuck = TimeSpan.Zero;
        int holePort = -1;
        while (sw.ElapsedMilliseconds < 5000 && stuck.TotalMilliseconds < 300)
        {
            clock.Tick = ++tick;
            a.Step();
            b.Step();
            b.Driver.TryGetUnrepairedHole(out holePort, out stuck);
            System.Threading.Thread.Sleep(1);
        }
        Assert.True(stuck.TotalMilliseconds >= 300,
            $"unrepaired hole never reported (port {holePort}, stuck {stuck.TotalMilliseconds:F0}ms)");
        Assert.Equal(0, holePort); // the hole is in A's input, as seen by B
        Assert.False(a.Driver.TryGetUnrepairedHole(out _, out _), "A has no hole — B->A traffic was clean");

        // Let the requests through: retransmission repairs the hole and the report clears.
        suppressRequests = false;
        int targetB = b.Driver.CurrentFrame + 60;
        for (int i = 0; i < 5000 && b.Driver.CurrentFrame < targetB; i++)
        {
            clock.Tick = ++tick;
            a.Step();
            b.Step();
            System.Threading.Thread.Sleep(1);
        }
        Assert.True(b.Driver.CurrentFrame >= targetB, "session did not recover once requests flowed");
        Assert.False(b.Driver.TryGetUnrepairedHole(out _, out _), "hole report must clear after repair");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedChecksums_AlignAndAgreeAcrossPeers(bool elide)
    {
        // Rollback can't checksum the live frame (it may be a prediction), so it checksums the
        // newest *final* interval boundary. Both peers must (a) produce checksums for the same
        // boundary frames despite predicting independently, and (b) agree on every shared one —
        // that is exactly what lets the host catch a real desync.
        //
        // The elide:true case is the regression guard for the trap in that arrangement: the state a
        // checksum reads sits inside the finalized region, which is precisely what elision drops.
        // Get the anchor wrong and this test goes to zero shared checksums — silently, which is how
        // it would fail in the wild too.
        const int k = 4;
        const int iters = 500;
        const int interval = 30;
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: k);
        var a = BuildRollback(ta, 0, tuning: elide ? Eliding(interval) : null);
        var b = BuildRollback(tb, 1, tuning: elide ? Eliding(interval) : null);

        var ha = new Dictionary<int, uint>();
        var hb = new Dictionary<int, uint>();
        for (int i = 0; i < iters; i++)
        {
            clock.Tick = i;
            a.Step();
            b.Step();
            if (a.Rollback.TryConfirmedChecksum(interval, out var fa, out var va)) ha[fa] = va;
            if (b.Rollback.TryConfirmedChecksum(interval, out var fb, out var vb)) hb[fb] = vb;
        }

        int shared = 0;
        foreach (var kv in ha)
            if (hb.TryGetValue(kv.Key, out var other))
            {
                shared++;
                Assert.True(kv.Value == other, $"checksum disagreement at boundary {kv.Key}");
            }
        Assert.True(shared >= 10, $"expected many aligned confirmed checksums, got {shared}");
    }

    [Fact]
    public void ConfirmedChecksums_SurviveAShallowRing()
    {
        // A heavy core qualifies at the minimum depth, which is a ring far shallower than anything
        // that could exist before rollback was opened up to such cores — and a shallow ring means a
        // narrow prune window, which is what the checksum's anchor has to survive. Covering the
        // configuration rather than a specific bug: the anchor does currently survive on the
        // arithmetic alone, so this passes with or without the retention clause in Prune. What it
        // catches is the whole arrangement breaking, and it would do so silently otherwise —
        // checksums just stop being produced.
        const int k = 4;
        const int iters = 500;
        const int interval = 30;
        int shallowRing = ProbeResult.RollbackDepthThreshold; // the shallowest ring that can qualify

        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: k);
        var a = BuildRollback(ta, 0, tuning: Eliding(interval), maxRollback: shallowRing);
        var b = BuildRollback(tb, 1, tuning: Eliding(interval), maxRollback: shallowRing);

        var ha = new Dictionary<int, uint>();
        var hb = new Dictionary<int, uint>();
        for (int i = 0; i < iters; i++)
        {
            clock.Tick = i;
            a.Step();
            b.Step();
            if (a.Rollback.TryConfirmedChecksum(interval, out var fa, out var va)) ha[fa] = va;
            if (b.Rollback.TryConfirmedChecksum(interval, out var fb, out var vb)) hb[fb] = vb;
        }

        int shared = 0;
        foreach (var kv in ha)
            if (hb.TryGetValue(kv.Key, out var other))
            {
                shared++;
                Assert.True(kv.Value == other, $"checksum disagreement at boundary {kv.Key}");
            }
        Assert.True(shared >= 5, $"shallow ring lost its checksum anchors: only {shared} shared");
    }

    [Fact]
    public void ConfirmedChecksums_AreHashedWhereTheCoreAlreadyStands()
    {
        // The checksum reads the state entering an anchor — which is exactly where the core is
        // standing when that anchor is saved. Hashing it there costs the hash and nothing else;
        // coming back for it later costs a save, a load, the hash, and a load to return, measured
        // on N64 as 18.4ms against the 7.2ms the hash alone costs. This pins the cheap route: one
        // hash per boundary, no state traffic at all beyond the anchors themselves.
        const int interval = 30;
        const int iters = 320;

        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: 0);
        var a = BuildRollback(ta, 0, tuning: Eliding(interval));
        var b = BuildRollback(tb, 1, tuning: Eliding(interval));

        int boundaries = 0;
        for (int i = 0; i < iters; i++)
        {
            clock.Tick = i;
            a.Step();
            b.Step();
            if (a.Rollback.TryConfirmedChecksum(interval, out _, out _)) boundaries++;
            b.Rollback.TryConfirmedChecksum(interval, out _, out _);
        }

        Assert.True(boundaries >= 5, $"only {boundaries} boundaries reported");
        Assert.Equal(boundaries, a.Rollback.ChecksumsFromAnchor);
        Assert.Equal(0, a.Rollback.ChecksumsByVisit);
        // One hash per boundary and not one more, and — the part that was costing the hitch — no
        // save or load anywhere except the anchor snapshots themselves.
        Assert.Equal(boundaries, a.Emu.HashCount);
        Assert.Equal(a.Rollback.SavesTaken, a.Emu.SaveCount);
        Assert.Equal(0, a.Emu.LoadCount);
    }

    [Fact]
    public void ConfirmedChecksums_StayCorrectWhenARepairRewritesTheAnchor()
    {
        // The cached hash describes a state a repair can destroy. When a correction re-simulates
        // the anchor frame the cache is dropped rather than recomputed — recomputing would fold a
        // full memory hash into the timed re-simulation and inflate the per-frame repair cost that
        // governs how far we predict — so the checksum falls back to fetching that one itself.
        // Both routes must produce the same number, which is what peer agreement here proves.
        const int k = 4;
        const int interval = 5; // short enough that ordinary rollbacks land on anchors constantly
        const int iters = 400;

        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: k);
        var a = BuildRollback(ta, 0, tuning: Eliding(interval));
        var b = BuildRollback(tb, 1, tuning: Eliding(interval));

        var ha = new Dictionary<int, uint>();
        var hb = new Dictionary<int, uint>();
        for (int i = 0; i < iters; i++)
        {
            clock.Tick = i;
            a.Step();
            b.Step();
            if (a.Rollback.TryConfirmedChecksum(interval, out var fa, out var va)) ha[fa] = va;
            if (b.Rollback.TryConfirmedChecksum(interval, out var fb, out var vb)) hb[fb] = vb;
        }

        Assert.True(a.Rollback.RollbackCount > 0, "no repairs ran, so nothing was exercised");
        Assert.True(a.Rollback.ChecksumsByVisit > 0,
            "the fallback never ran — this test would pass without ever leaving the fast path");
        Assert.True(a.Rollback.ChecksumsFromAnchor > 0, "the fast path never ran either");

        int shared = 0;
        foreach (var kv in ha)
            if (hb.TryGetValue(kv.Key, out var other))
            {
                shared++;
                Assert.True(kv.Value == other, $"checksum disagreement at boundary {kv.Key}");
            }
        Assert.True(shared >= 10, $"only {shared} boundaries compared");
    }

    [Fact]
    public void TimeSync_BoundsRollbackDepthUnderClockSkew()
    {
        // Instance A's clock runs faster than B's (stepped twice per B step). Without time-sync A
        // predicts ever-further ahead of B's real inputs and rolls back toward the hard ring cap.
        // With time-sync it holds itself back once its lead exceeds ~the latency, so peak rollback
        // depth stays shallow (near the soft cap) — the whole point of the valve.
        const int k = 3;
        const double frameMs = 16.0;
        double rtt = 2 * k * frameMs; // k frames each way -> soft cap ~= k + margin(2) = 5

        (int maxDepth, int tsyncStalls) RunSkewed(bool timeSync)
        {
            var clock = new Clock();
            var (ta, tb) = LatencyLink.Pair(clock, latency: k);
            var a = BuildRollback(ta, 0, timeSync ? frameMs : 0);
            var b = BuildRollback(tb, 1, timeSync ? frameMs : 0);
            if (timeSync)
            {
                a.Rollback.OnPacingReport(new PacingInfo(rtt, 0));
                b.Rollback.OnPacingReport(new PacingInfo(rtt, 0));
            }
            for (int i = 0; i < 400; i++)
            {
                clock.Tick = i;
                a.Step();
                a.Step(); // A's clock runs twice as fast as B's
                b.Step();
            }
            return (a.Rollback.MaxRollbackDepthSeen, a.Rollback.TimeSyncStalls);
        }

        var withSync = RunSkewed(true);
        var without = RunSkewed(false);

        Assert.True(withSync.tsyncStalls > 0, "time-sync valve should have engaged under skew");
        Assert.True(without.maxDepth > withSync.maxDepth,
            $"time-sync should reduce peak rollback depth (with={withSync.maxDepth}, without={without.maxDepth})");
        Assert.True(withSync.maxDepth <= k + 3,
            $"time-sync should bound depth near the soft cap, got {withSync.maxDepth}");
    }

    [Fact]
    public void CostCap_TrimsPredictionWhenRepairsMeasureExpensive()
    {
        // A depth in frames can't bound a freeze when a frame's cost varies — which on a heavy core
        // it does. Here every repair is scripted to cost far more than the budget allows, so the
        // strategy must notice and stop predicting as far ahead, rather than keep booking work it
        // cannot afford. Correctness is not negotiable while it does that.
        const int k = 8;
        const int iters = 600;

        (RollbackStrategy strategy, Instance a, Instance b) RunWith(bool budgeted)
        {
            RollbackTuning Tune() => new RollbackTuning
            {
                ElideConfirmedSaves = true,
                // Every repair is scripted to "take" 100ms against a 5ms allowance.
                RepairBudgetMs = budgeted ? 5.0 : 0,
                Clock = new ManualClock(Enumerable.Repeat(100.0, 20000)),
            };
            var clock = new Clock();
            var (ta, tb) = LatencyLink.Pair(clock, latency: k);
            var a = BuildRollback(ta, 0, tuning: Tune());
            var b = BuildRollback(tb, 1, tuning: Tune());
            Run(clock, a, b, iters);
            return (a.Rollback, a, b);
        }

        var free = RunWith(budgeted: false);
        var capped = RunWith(budgeted: true);

        Assert.True(free.strategy.RollbackCount > 0, "the scenario must actually exercise repairs");
        Assert.True(capped.strategy.CostStalls > 0, "expected the cost ceiling to yield frames");
        Assert.True(capped.strategy.CostCap < MaxRollback,
            $"cost cap should have tightened below the ring depth, stayed at {capped.strategy.CostCap}");
        // Peak depth can't be the signal here: the cap is learned FROM the first repair, so both
        // runs share that one and reach the same high-water mark. Total re-simulated frames is the
        // aggregate over everything after it, which is exactly the work the cap exists to bound.
        Assert.True(capped.strategy.FramesResimulated < free.strategy.FramesResimulated,
            $"cost cap should reduce total re-simulation (capped={capped.strategy.FramesResimulated}, " +
            $"free={free.strategy.FramesResimulated})");
        // The +1 is the cap's own boundary, not the keyframe walkback. Splitting depth from
        // replayed frames made that testable: with the walkback removed from the depth this still
        // measured 3 against a cap of 2, so the slack is in how the cap is enforced — it bounds
        // how far prediction may RUN, and the correction that arrives for the frame just past the
        // horizon is one deeper than the horizon itself.
        Assert.True(capped.strategy.LastRollbackDepth <= capped.strategy.CostCap + 1,
            $"a settled repair ran {capped.strategy.LastRollbackDepth} deep against a cap of " +
            $"{capped.strategy.CostCap}");
        // Depth and walkback are disjoint and together account for every frame replayed. This is
        // the invariant the status line broke by reporting a d3+1wb correction as "d4+1wb".
        Assert.Equal(capped.strategy.LastRollbackDepth + capped.strategy.LastRollbackWalkback,
            capped.strategy.LastReplayedFrames);
        // The cap may only ever narrow prediction — never the ring, and never correctness.
        Assert.Equal(MaxRollback, capped.strategy.MaxRollback);
        int upTo = Math.Min(capped.a.Driver.CurrentFrame, capped.b.Driver.CurrentFrame) - k - Delay - 4;
        AssertFinalizedCorrect(capped.a, upTo);
        AssertFinalizedCorrect(capped.b, upTo);
    }

    [Fact]
    public void SavestateRing_StaysBounded()
    {
        const int k = 5;
        const int iters = 600;
        var clock = new Clock();
        var (ta, tb) = LatencyLink.Pair(clock, latency: k);
        var a = BuildRollback(ta, 0);
        var b = BuildRollback(tb, 1);

        Run(clock, a, b, iters);

        // We saved hundreds of states but must be holding only ~MaxRollback live at a time.
        Assert.True(a.Emu.SaveCount > iters, $"expected many saves, got {a.Emu.SaveCount}");
        Assert.True(a.Emu.LiveStates.Count <= MaxRollback + 6,
            $"ring leaked: {a.Emu.LiveStates.Count} live states");
        Assert.True(a.Emu.ReleaseCount > iters - MaxRollback - 20, "old states should have been released");
    }
}
