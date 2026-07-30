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

            /// <summary>Frames per loop iteration, so peers can be given different clock rates. 1.0 is
            /// the shared-clock case the uniform test uses.</summary>
            public double Rate = 1.0;

            public void Step()
            {
                if (Driver.OnPreFrame() == FrameStep.Ran)
                {
                    Emu.AdvanceAppliedFrame();
                    Driver.OnPostFrame();
                }
                else Stalls++;
            }

            /// <summary>Take however many stepping opportunities this peer's clock rate has earned by
            /// iteration <paramref name="iteration"/>. A peer running 2% fast does genuinely get an
            /// extra frame's opportunity every fiftieth iteration.</summary>
            public void StepFor(int iteration)
            {
                int owed = (int)((iteration + 1) * Rate) - (int)(iteration * Rate);
                for (int i = 0; i < owed; i++) Step();
            }
        }

        /// <summary>Deterministic xorshift, so a failure is reproducible rather than a once-a-week
        /// mystery. Every run of these tests sees exactly the same losses and the same jitter.</summary>
        private sealed class Rng
        {
            private uint _state;
            public Rng(uint seed) { _state = seed == 0 ? 1u : seed; }

            public int Next(int bound)
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return bound <= 1 ? 0 : (int)(_state % (uint)bound);
            }
        }

        /// <summary>One directed edge's character. Directed, because real links are not symmetric —
        /// an upstream-starved player is slow in one direction only.</summary>
        private sealed class Edge
        {
            public int LatencyTicks;
            public int JitterTicks;   // extra delay, uniform over [0, JitterTicks]
            public int LossPercent;
        }

        /// <summary>
        /// A full mesh where every directed edge has its own latency, jitter and loss, and where jitter
        /// may reorder datagrams. The uniform hub above models the one case that never happens: four
        /// players on identical links. What breaks a real session is one bad edge — and on a 4-player
        /// mesh three of the six edges never touch the host at all.
        /// </summary>
        private sealed class AsymmetricMesh
        {
            private readonly Clock _clock;
            private readonly Rng _rng;
            private readonly Edge[,] _edges;
            private readonly List<(long At, byte[] Data)>[] _inbound;

            public AsymmetricMesh(Clock clock, int peers, uint seed)
            {
                _clock = clock;
                _rng = new Rng(seed);
                Count = peers;
                _edges = new Edge[peers, peers];
                _inbound = new List<(long, byte[])>[peers];
                for (int i = 0; i < peers; i++)
                {
                    _inbound[i] = new List<(long, byte[])>();
                    for (int j = 0; j < peers; j++) _edges[i, j] = new Edge();
                }
            }

            public int Count { get; }
            public int Dropped { get; private set; }
            public int Delivered { get; private set; }

            public Edge From(int from, int to) => _edges[from, to];

            /// <summary>Give one edge the same character in both directions.</summary>
            public void Symmetric(int a, int b, int latency, int jitter, int lossPercent)
            {
                foreach (var edge in new[] { _edges[a, b], _edges[b, a] })
                {
                    edge.LatencyTicks = latency;
                    edge.JitterTicks = jitter;
                    edge.LossPercent = lossPercent;
                }
            }

            public ITransport Port(int peer) => new PeerPort(this, peer);

            private void Send(int from, byte[] datagram)
            {
                for (int to = 0; to < Count; to++)
                {
                    if (to == from) continue;
                    var edge = _edges[from, to];
                    if (edge.LossPercent > 0 && _rng.Next(100) < edge.LossPercent) { Dropped++; continue; }
                    long at = _clock.Tick + edge.LatencyTicks + _rng.Next(edge.JitterTicks + 1);
                    _inbound[to].Add((at, (byte[])datagram.Clone()));
                    Delivered++;
                }
            }

            private bool TryReceive(int peer, out byte[] datagram)
            {
                // Earliest-due first. Jitter can make that a different order than the one they were
                // sent in, which is exactly what a real path does and what the redundant window and
                // the pipeline's frame keying have to tolerate.
                var queue = _inbound[peer];
                int best = -1;
                for (int i = 0; i < queue.Count; i++)
                    if (queue[i].At <= _clock.Tick && (best < 0 || queue[i].At < queue[best].At)) best = i;
                if (best < 0) { datagram = null!; return false; }
                datagram = queue[best].Data;
                queue.RemoveAt(best);
                return true;
            }

            private sealed class PeerPort : ITransport
            {
                private readonly AsymmetricMesh _mesh;
                private readonly int _peer;
                public PeerPort(AsymmetricMesh mesh, int peer) { _mesh = mesh; _peer = peer; }
                public void Send(byte[] datagram) => _mesh.Send(_peer, datagram);
                public bool TryReceive(out byte[] datagram) => _mesh.TryReceive(_peer, out datagram);
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

        /// <summary>
        /// The same correctness bedrock, but on a mesh nobody would call ideal: every directed edge has
        /// its own latency, its own jitter, and its own loss, and the four peers run at four slightly
        /// different clock rates. Two of the six edges are joiner-to-joiner and never touch the host.
        ///
        /// The uniform-latency case this file used to test alone cannot fail the way a real 4-player
        /// session fails. It has no worst edge, so nothing is ever waiting on one player in particular;
        /// no reordering, so the redundant window is never asked to tolerate a late arrival landing
        /// behind a newer one; and one shared clock, so no peer ever drifts ahead of the group.
        /// </summary>
        [Fact]
        public void AsymmetricMesh_StaysCorrectAndBoundedUnderJitterLossAndClockSkew()
        {
            const int players = 4;
            const int iters = 900;
            const int redundancy = 12; // covers the burst losses the worst edge below produces
            const int worstLatency = 9;
            const int worstJitter = 4;

            var clock = new Clock();
            var mesh = new AsymmetricMesh(clock, players, seed: 0xC0FFEE);
            // A LAN pair, a distant player, and one edge that is both slow and lossy — and it is a
            // joiner-to-joiner edge (2<->3), the kind the host cannot see from its own links.
            mesh.Symmetric(0, 1, latency: 1, jitter: 1, lossPercent: 0);
            mesh.Symmetric(0, 2, latency: 5, jitter: 2, lossPercent: 3);
            mesh.Symmetric(0, 3, latency: 6, jitter: 2, lossPercent: 3);
            mesh.Symmetric(1, 2, latency: 4, jitter: 2, lossPercent: 2);
            mesh.Symmetric(1, 3, latency: 5, jitter: 3, lossPercent: 5);
            mesh.Symmetric(2, 3, latency: worstLatency, jitter: worstJitter, lossPercent: 18);
            // Asymmetry inside one edge: player 3 is upstream-starved, so its own sends fare worse.
            mesh.From(3, 2).LossPercent = 24;
            mesh.From(3, 1).LatencyTicks = 8;

            var rates = new[] { 1.0, 0.98, 1.02, 0.995 };
            var inst = new Instance[players];
            for (int i = 0; i < players; i++)
            {
                var emu = new FakeEmuAdapter(portCount: players) { LocalInputScript = Script(i) };
                int port = i;
                var driver = new FrameDriver(emu, mesh.Port(i),
                    p => new RollbackStrategy(p, emu, port, MaxRollback),
                    localPort: port, delay: Delay, redundancy: redundancy, rollbackWindow: MaxRollback);
                inst[i] = new Instance
                {
                    Emu = emu,
                    Driver = driver,
                    Rollback = (RollbackStrategy)driver.Strategy,
                    Rate = rates[i],
                };
                driver.Start();
            }

            int worstSpread = 0;
            for (int it = 0; it < iters; it++)
            {
                clock.Tick = it;
                for (int i = 0; i < players; i++) inst[i].StepFor(it);

                int high = int.MinValue, low = int.MaxValue;
                for (int i = 0; i < players; i++)
                {
                    int frame = inst[i].Driver.CurrentFrame;
                    if (frame > high) high = frame;
                    if (frame < low) low = frame;
                }
                if (high - low > worstSpread) worstSpread = high - low;
            }

            // The test must actually have exercised loss, or it is only a latency test wearing a
            // loss test's name.
            Assert.True(mesh.Dropped > 200, $"only {mesh.Dropped} datagrams were dropped");

            int minFrame = int.MaxValue;
            int totalRollbacks = 0;
            for (int i = 0; i < players; i++)
            {
                // Nobody froze. A peer starved by its worst edge is the failure this guards: it would
                // sit at the prediction cap while the rest of the group ran on without it.
                Assert.True(inst[i].Driver.CurrentFrame > iters / 2,
                    $"player {i} only reached frame {inst[i].Driver.CurrentFrame} of {iters} iterations");
                if (inst[i].Driver.CurrentFrame < minFrame) minFrame = inst[i].Driver.CurrentFrame;
                totalRollbacks += inst[i].Rollback.RollbackCount;

                // The savestate ring stays bounded despite the skew: a peer running fast cannot buy
                // itself unbounded memory by predicting further ahead.
                Assert.True(inst[i].Emu.LiveStates.Count <= MaxRollback + 4,
                    $"player {i} holds {inst[i].Emu.LiveStates.Count} live states");
            }
            Assert.True(totalRollbacks > 0, "expected rollbacks under an asymmetric, lossy mesh");

            // Left to their clocks the fastest and slowest peer would have drifted 900 × (1.02 − 0.98)
            // = 36 frames apart by now. The prediction cap is what stops that, so the spread has to
            // stay inside roughly the cap plus the worst edge's own contribution — and, in particular,
            // well under the free-running figure.
            Assert.True(worstSpread <= MaxRollback + Delay + worstLatency,
                $"peers drifted {worstSpread} frames apart");

            // And the bedrock: every finalized frame still applied the analytic truth on every port.
            int upTo = minFrame - worstLatency - worstJitter - Delay - MaxRollback - 5;
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

        /// <summary>
        /// Time-sync trims a peer that is running ahead by making it give frames back. With four peers
        /// and real clock skew every one of them is ahead of somebody, so every one of them is owed a
        /// stall — and if they all take it at the same time the session stops without anything being
        /// wrong with the network. Feed each peer the advantage it genuinely has and prove the group
        /// keeps moving.
        /// </summary>
        [Fact]
        public void MeasuredFrameAdvantage_TrimsTheLeaderWithoutFreezingTheGroup()
        {
            const int players = 4;
            const int iters = 900;
            const double frameMs = 16.64;
            const int reportEvery = 20;

            var clock = new Clock();
            var mesh = new AsymmetricMesh(clock, players, seed: 0x5EED);
            mesh.Symmetric(0, 1, 2, 1, 0);
            mesh.Symmetric(0, 2, 4, 1, 0);
            mesh.Symmetric(0, 3, 6, 2, 0);
            mesh.Symmetric(1, 2, 3, 1, 0);
            mesh.Symmetric(1, 3, 5, 2, 0);
            mesh.Symmetric(2, 3, 7, 2, 0);

            // Each peer's own worst edge, so the round-trip it is told about matches the latency it is
            // actually living with. Time-sync derives its prediction horizon from that figure; feed it
            // a round-trip from a different network and the horizon is wrong in a way that has nothing
            // to do with what the test is trying to show.
            var worstOneWay = new int[players];
            for (int i = 0; i < players; i++)
                for (int j = 0; j < players; j++)
                    if (i != j && mesh.From(j, i).LatencyTicks > worstOneWay[i])
                        worstOneWay[i] = mesh.From(j, i).LatencyTicks;

            var rates = new[] { 1.03, 1.0, 0.97, 0.99 };
            var inst = new Instance[players];
            for (int i = 0; i < players; i++)
            {
                var emu = new FakeEmuAdapter(portCount: players) { LocalInputScript = Script(i) };
                int port = i;
                var driver = new FrameDriver(emu, mesh.Port(i),
                    // frameMs != 0 turns time-sync on: this is the path that can decide to stall.
                    p => new RollbackStrategy(p, emu, port, MaxRollback, frameMs),
                    localPort: port, delay: Delay, redundancy: 12, rollbackWindow: MaxRollback);
                inst[i] = new Instance
                {
                    Emu = emu,
                    Driver = driver,
                    Rollback = (RollbackStrategy)driver.Strategy,
                    Rate = rates[i],
                };
                driver.Start();
            }

            int sequence = 0;
            var progressAtLastReport = new int[players];
            int simultaneousFreezes = 0;
            for (int it = 0; it < iters; it++)
            {
                clock.Tick = it;
                for (int i = 0; i < players; i++) inst[i].StepFor(it);

                if (it % reportEvery != reportEvery - 1) continue;

                sequence++;
                int slowest = int.MaxValue;
                for (int i = 0; i < players; i++)
                    if (inst[i].Driver.CurrentFrame < slowest) slowest = inst[i].Driver.CurrentFrame;

                int stuck = 0;
                for (int i = 0; i < players; i++)
                {
                    int frame = inst[i].Driver.CurrentFrame;
                    if (frame == progressAtLastReport[i]) stuck++;
                    progressAtLastReport[i] = frame;
                    // Each peer is told what is actually true of it: how far ahead of the slowest
                    // player it is running, tagged with a fresh sample id so the report counts once.
                    inst[i].Rollback.OnPacingReport(
                        new PacingInfo(roundTripMs: 2 * worstOneWay[i] * frameMs,
                            frameAdvantage: frame - slowest, hasFrameAdvantage: true, sampleSequence: sequence));
                }
                if (stuck == players) simultaneousFreezes++;
            }

            // Nobody stalled everybody at once for a whole reporting window.
            Assert.Equal(0, simultaneousFreezes);

            // The group runs at about the slowest clock, which is the most it can do — not slower.
            // Four peers each being told they lead somebody could otherwise throttle each other into a
            // pace no member's clock justifies, and that is a session that feels broken while every
            // individual measurement looks reasonable.
            int slowestClockFrames = (int)(rates[2] * iters);
            int floorFrames = (int)(0.9 * slowestClockFrames);
            for (int i = 0; i < players; i++)
                Assert.True(inst[i].Driver.CurrentFrame >= floorFrames,
                    $"player {i} reached frame {inst[i].Driver.CurrentFrame}; the slowest clock alone " +
                    $"would have reached {slowestClockFrames}");

            // And the trimming is aimed: the 3%-fast peer gives frames back, the 3%-slow one is never
            // asked to. A symmetric stall would keep the peers together while helping nobody.
            Assert.True(inst[0].Stalls > inst[2].Stalls,
                $"the fast peer stalled {inst[0].Stalls} times and the slow peer {inst[2].Stalls}");
            int leader = inst[0].Driver.CurrentFrame;
            int laggard = inst[2].Driver.CurrentFrame;
            Assert.True(leader - laggard <= MaxRollback + Delay,
                $"the 3%-fast peer ran {leader - laggard} frames ahead of the 3%-slow one");
        }

        /// <summary>
        /// The sync-layer half of changing netcode or input delay without ending the session: every peer
        /// tears its driver down and stands a new one up with a different mode AND a different delay, on
        /// a new generation, seeded from one shared state — which is what the tool orchestrates when the
        /// host presses Apply, and structurally what a desync resync already did.
        ///
        /// Two things have to hold. The new timeline must be correct from its own frame 0, verified
        /// against the same analytic oracle the rest of this file uses. And the old timeline's datagrams
        /// — which are still in flight at the moment of the switch, carrying frame numbers around where
        /// the new timeline is about to be, and encoding a DIFFERENT delay — must be refused rather than
        /// mixed in.
        /// </summary>
        [Fact]
        public void SwitchingModeAndDelayMidSessionRebuildsOntoACorrectNewTimeline()
        {
            const int players = 3;
            const int firstPhase = 300;
            const int secondPhase = 500;
            // The change a host actually makes: lockstep needs delay to cover the link, rollback hides
            // it by predicting instead — so switching netcode is normally also a delay cut, and the new
            // timeline has to survive being mispredicted from its very first frames.
            const int firstDelay = 5;
            const int secondDelay = 2;
            const int k = 3;

            var clock = new Clock();
            var hubs = LatencyHub.Mesh(clock, players, latency: k);
            var generation = new SessionGeneration(0xA11CE, 1);

            // Phase 1: lockstep at delay 2, the way a cautious host starts.
            var emus = new FakeEmuAdapter[players];
            var inst = new Instance[players];
            for (int i = 0; i < players; i++)
            {
                emus[i] = new FakeEmuAdapter(portCount: players) { LocalInputScript = Script(i) };
                int port = i;
                var driver = new FrameDriver(emus[i], hubs[i], p => new LockstepStrategy(p),
                    localPort: port, delay: firstDelay, redundancy: Redundancy, generation: generation);
                inst[i] = new Instance { Emu = emus[i], Driver = driver };
                driver.Start();
            }
            for (int it = 0; it < firstPhase; it++)
            {
                clock.Tick = it;
                for (int i = 0; i < players; i++) inst[i].Step();
            }
            for (int i = 0; i < players; i++)
                Assert.True(inst[i].Driver.CurrentFrame > firstPhase / 2, $"player {i} never got going");

            // The switch. Peer 0 is the host: its state is authoritative, everyone else adopts it, and
            // every peer rebuilds for rollback at delay 5 on the next generation. Nothing is drained
            // from the hubs — the phase-1 datagrams still in flight are exactly what must be refused.
            var authoritative = emus[0].ExportState();
            int baseFrame = BitConverter.ToInt32(authoritative, 0);
            var rebuilt = generation.Next();
            for (int i = 0; i < players; i++)
            {
                inst[i].Driver.Dispose();
                if (i != 0) emus[i].ImportState(authoritative);
                int port = i;
                var driver = new FrameDriver(emus[i], hubs[i],
                    p => new RollbackStrategy(p, emus[port], port, MaxRollback),
                    localPort: port, delay: secondDelay, redundancy: Redundancy,
                    rollbackWindow: MaxRollback, generation: rebuilt);
                inst[i] = new Instance
                {
                    Emu = emus[i],
                    Driver = driver,
                    Rollback = (RollbackStrategy)driver.Strategy,
                };
                driver.Start();
            }

            for (int it = 0; it < secondPhase; it++)
            {
                clock.Tick = firstPhase + it;
                for (int i = 0; i < players; i++) inst[i].Step();
            }

            int minFrame = int.MaxValue;
            long refused = 0;
            int rollbacks = 0;
            for (int i = 0; i < players; i++)
            {
                Assert.True(inst[i].Driver.CurrentFrame > secondPhase / 2,
                    $"player {i} only reached frame {inst[i].Driver.CurrentFrame} after the switch");
                if (inst[i].Driver.CurrentFrame < minFrame) minFrame = inst[i].Driver.CurrentFrame;
                refused += inst[i].Driver.Codec.RejectedGeneration;
                rollbacks += inst[i].Rollback.RollbackCount;
            }

            // The netcode really did change: the new timeline mispredicts and repairs, which the old
            // one could not do at all. Without this the test would pass on a session that merely
            // rebuilt itself and then never exercised the mode it rebuilt into.
            Assert.True(rollbacks > 0, "the rebuilt rollback timeline never had to repair a prediction");

            // The in-flight old-timeline packets arrived and were refused for their generation. Without
            // this the test would be asserting correctness on a mesh that simply had nothing stale left
            // in it, which proves nothing about the switch.
            Assert.True(refused > 0, "no stale-generation datagrams were refused after the rebuild");

            // Every finalized frame of the NEW timeline applied the analytic truth at the NEW delay.
            // The adapter's frame counter carries across the rebuild (it is the emulator's, not the
            // timeline's), so the oracle reads at baseFrame + the new driver's frame.
            int upTo = minFrame - k - secondDelay - MaxRollback - 5;
            Assert.True(upTo > 100, $"not enough finalized progress after the switch (upTo={upTo})");
            for (int i = 0; i < players; i++)
                for (int g = 0; g < upTo; g++)
                {
                    Assert.True(inst[i].Emu.LastInputByFrame.TryGetValue(baseFrame + g, out var applied),
                        $"player {i} never ran rebuilt frame {g}");
                    for (int p = 0; p < players; p++)
                    {
                        var expected = g < secondDelay
                            ? Neutral()
                            : Script(p)(baseFrame + g - secondDelay);
                        Assert.True(expected.ValueEquals(applied!.Ports[p]),
                            $"player {i}: rebuilt-timeline input mismatch at frame {g} port {p}");
                    }
                }
        }
    }
}
