using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
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

        private static Instance BuildRollback(ITransport t, int localPort)
        {
            var emu = new FakeEmuAdapter(portCount: 2) { LocalInputScript = Scripts[localPort] };
            var driver = new FrameDriver(emu, t,
                p => new RollbackStrategy(p, emu, localPort, MaxRollback),
                localPort: localPort, delay: Delay, redundancy: Redundancy, rollbackWindow: MaxRollback);
            var inst = new Instance { Emu = emu, Driver = driver, Rollback = (RollbackStrategy)driver.Strategy };
            driver.Start();
            return inst;
        }

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
        public void ConfirmedChecksums_AlignAndAgreeAcrossPeers()
        {
            // Rollback can't checksum the live frame (it may be a prediction), so it checksums the
            // newest *final* interval boundary. Both peers must (a) produce checksums for the same
            // boundary frames despite predicting independently, and (b) agree on every shared one —
            // that is exactly what lets the host catch a real desync.
            const int k = 4;
            const int iters = 500;
            const int interval = 30;
            var clock = new Clock();
            var (ta, tb) = LatencyLink.Pair(clock, latency: k);
            var a = BuildRollback(ta, 0);
            var b = BuildRollback(tb, 1);

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
}
