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
    /// Rollback with more than two players, over an in-memory latency hub that mimics the host-relay
    /// topology's effect (every peer's input reaches every other) with a one-way delay. Each peer must
    /// predict N-1 remote ports at once and reconcile corrections from any of them. The bedrock check
    /// is the same as the 2P rollback tests: every FINALIZED frame (all real inputs arrived) applied
    /// exactly the inputs an ideal all-real run would, on every port — verified against an analytic
    /// oracle. Prediction may momentarily be wrong; it is always corrected before it becomes final.
    /// </summary>
    public class MultiPlayerRollbackTests
    {
        private const int Delay = 2;
        private const int Redundancy = 8;
        private const int MaxRollback = 16;

        private static PortInput Btn(bool a, bool b)
        {
            var arr = new bool[8];
            arr[0] = a;
            arr[1] = b;
            return new PortInput(arr, Array.Empty<int>());
        }

        private static PortInput Neutral() => Btn(false, false);

        // Distinct, frequently-changing script per port so predictions are genuinely contradicted.
        private static Func<int, PortInput> Script(int port) =>
            frame => Btn((frame % (port + 3)) < 2, (frame % (port + 5)) == 0);

        private sealed class Clock { public long Tick; }

        /// <summary>Full-mesh hub with a fixed one-way delay: what any peer sends, every other peer
        /// receives `latency` ticks later — the relay topology's effect, minus the sockets.</summary>
        private sealed class LatencyHub : ITransport
        {
            private readonly Clock _clock;
            private readonly int _latency;
            private readonly Queue<(long at, byte[] data)> _inbound = new Queue<(long, byte[])>();
            private LatencyHub[] _others = Array.Empty<LatencyHub>();

            private LatencyHub(Clock clock, int latency) { _clock = clock; _latency = latency; }

            public static LatencyHub[] Mesh(Clock clock, int n, int latency)
            {
                var hubs = new LatencyHub[n];
                for (int i = 0; i < n; i++) hubs[i] = new LatencyHub(clock, latency);
                for (int i = 0; i < n; i++)
                {
                    var others = new List<LatencyHub>();
                    for (int j = 0; j < n; j++) if (j != i) others.Add(hubs[j]);
                    hubs[i]._others = others.ToArray();
                }
                return hubs;
            }

            public void Send(byte[] datagram)
            {
                foreach (var o in _others)
                    o._inbound.Enqueue((_clock.Tick + _latency, (byte[])datagram.Clone()));
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

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void MultiRemoteRollback_FinalizedFramesStayCorrect(int players)
        {
            const int k = 4;
            const int iters = 500;
            var clock = new Clock();
            var hubs = LatencyHub.Mesh(clock, players, latency: k);

            var inst = new Instance[players];
            for (int i = 0; i < players; i++)
            {
                var emu = new FakeEmuAdapter(portCount: players) { LocalInputScript = Script(i) };
                int port = i;
                var driver = new FrameDriver(emu, hubs[i],
                    p => new RollbackStrategy(p, emu, port, MaxRollback),
                    localPort: port, delay: Delay, redundancy: Redundancy, rollbackWindow: MaxRollback);
                inst[i] = new Instance { Emu = emu, Driver = driver, Rollback = (RollbackStrategy)driver.Strategy };
                driver.Start();
            }

            for (int it = 0; it < iters; it++)
            {
                clock.Tick = it;
                for (int i = 0; i < players; i++) inst[i].Step();
            }

            // All peers kept advancing (rollback hides the relay delay rather than stalling on it).
            int minFrame = int.MaxValue;
            int totalRollbacks = 0;
            for (int i = 0; i < players; i++)
            {
                Assert.True(inst[i].Driver.CurrentFrame >= iters - 20,
                    $"player {i} only reached {inst[i].Driver.CurrentFrame}");
                if (inst[i].Driver.CurrentFrame < minFrame) minFrame = inst[i].Driver.CurrentFrame;
                totalRollbacks += inst[i].Rollback.RollbackCount;
            }
            Assert.True(totalRollbacks > 0, "expected rollbacks to fire under relay latency");

            // Every finalized frame applied exactly the analytic truth, on every port, for every peer.
            int upTo = minFrame - k - Delay - 5;
            Assert.True(upTo > 100, $"not enough finalized progress (upTo={upTo})");
            for (int i = 0; i < players; i++)
                for (int g = 0; g < upTo; g++)
                {
                    Assert.True(inst[i].Emu.LastInputByFrame.TryGetValue(g, out var applied),
                        $"player {i} never ran frame {g}");
                    for (int p = 0; p < players; p++)
                    {
                        var expected = g < Delay ? Neutral() : Script(p)(g - Delay);
                        Assert.True(expected.ValueEquals(applied!.Ports[p]),
                            $"player {i}: finalized input mismatch at frame {g} port {p}");
                    }
                }
        }
    }
}
