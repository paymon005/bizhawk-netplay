using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The invariants rollback's comments assert, driven rather than reasoned about.
///
/// <c>HasBaseWithinReach</c> claims "every predicted frame either has a base within N-1 frames, or
/// becomes one itself". A violation is not a wrong answer — it is
/// <c>InvalidOperationException("Rollback target frame N has no base in the ring")</c> thrown out of
/// the frame loop, which the session turns into a dead session naming a determinism bug. The claim
/// is only interesting where the three things that thin the ring overlap: sparse keyframes, elision
/// of confirmed frames, and the prune window. These drive all three at once, with real datagrams
/// arriving in patterns chosen to leave awkward gaps.
/// </summary>
public class RollbackInvariantTests
{
    private const int Delay = 2;
    private const int Redundancy = 8;

    private sealed class QueueTransport : ITransport
    {
        private readonly Queue<byte[]> _inbound = new();
        public void Enqueue(byte[] datagram) => _inbound.Enqueue(datagram);
        public void Send(byte[] datagram) { }
        public bool TryReceive(out byte[] datagram)
        {
            if (_inbound.Count == 0) { datagram = null!; return false; }
            datagram = _inbound.Dequeue();
            return true;
        }
    }

    private static FrameDriver Build(QueueTransport transport, int maxRollback,
        int keyframeInterval, bool elide, out FakeEmuAdapter emu)
    {
        var adapter = new FakeEmuAdapter(portCount: 2) { LocalInputScript = _ => null! };
        emu = adapter;
        var tuning = new RollbackTuning
        {
            ElideConfirmedSaves = elide,
            KeyframeInterval = keyframeInterval,
            // Deliberately not a multiple of any keyframe spacing under test, so anchors land
            // between keyframes rather than on top of them.
            ChecksumAnchorInterval = 37,
        };
        var driver = new FrameDriver(adapter, transport,
            p => new RollbackStrategy(p, adapter, localPort: 0, maxRollback, frameMs: 0, tuning),
            localPort: 0, delay: Delay, redundancy: Redundancy, rollbackWindow: maxRollback);
        driver.Start();
        return driver;
    }

    /// <summary>One frame of remote input, as a real datagram for the driver's own codec.</summary>
    private static byte[] RemoteFrame(FrameDriver driver, int frame, byte value)
    {
        int size = driver.Codec.PayloadSizeFor(1);
        var payload = new byte[size];
        if (size > 0) payload[0] = value;
        return driver.Codec.EncodeInput(1,
            new List<KeyValuePair<int, byte[]>> { new(frame, payload) });
    }

    /// <summary>
    /// Run with the remote port's input arriving late, out of order, and contradicting predictions
    /// at random depths. The assertion is that nothing throws: the ring always had a base to repair
    /// from, whatever the pattern left behind.
    /// </summary>
    [Theory]
    [InlineData(1, 8, false)]    // every predicted frame anchored, no elision: the simple case
    [InlineData(1, 8, true)]     // elision on
    [InlineData(3, 8, true)]     // sparse keyframes + elision: bases are genuinely scarce
    [InlineData(5, 12, true)]
    [InlineData(7, 16, true)]    // spacing approaching the ring depth
    public void ARepairAlwaysFindsABaseWhateverTheArrivalPattern(
        int keyframeInterval, int maxRollback, bool elide)
    {
        var transport = new QueueTransport();
        var driver = Build(transport, maxRollback, keyframeInterval, elide, out var emu);

        // Fixed seed so a failure reproduces exactly.
        var rng = new Random(20260805);
        var held = new List<int>();
        int produced = 0;

        for (int tick = 0; tick < 500; tick++)
        {
            // The remote produces frames roughly in step with us, but we hold them back.
            while (produced <= driver.CurrentFrame + Delay) held.Add(produced++);

            // Release a random subset, in a random order — the awkward part.
            int release = rng.Next(0, 3);
            for (int r = 0; r < release && held.Count > 0; r++)
            {
                int idx = rng.Next(held.Count);
                int frame = held[idx];
                held.RemoveAt(idx);
                transport.Enqueue(RemoteFrame(driver, frame, (byte)(frame % 4 == 0 ? 1 : 0)));
            }

            if (driver.OnPreFrame() == FrameStep.Stalled) continue;
            emu.AdvanceAppliedFrame();
            driver.OnPostFrame();
        }

        // Reaching here without an exception IS the assertion. The frame floor guards against a run
        // that stalled immediately and therefore proved nothing about base reachability.
        Assert.True(driver.CurrentFrame > 100,
            $"the sim only reached frame {driver.CurrentFrame} — it stalled rather than exercising " +
            "the ring, so this run proved nothing");
    }

    /// <summary>
    /// A correction landing at the deepest frame the accept window still admits finds its base.
    ///
    /// The prune window subtracts the keyframe spacing for exactly this case: a repair aimed at the
    /// oldest reachable frame restarts from a snapshot up to N-1 frames older still, and pruning to
    /// the naive window would throw that base away and turn a legal rollback into the exception.
    /// </summary>
    [Theory]
    [InlineData(1, 6)]
    [InlineData(4, 6)]
    [InlineData(6, 8)]
    public void ACorrectionAtTheDeepestReachableFrameStillHasItsBase(int keyframeInterval, int maxRollback)
    {
        var transport = new QueueTransport();
        var driver = Build(transport, maxRollback, keyframeInterval, elide: true, out var emu);

        // Run out to the prediction cap with the remote silent: every frame past the frontier is a
        // prediction, so the ring is as thin as this tuning ever makes it.
        for (int i = 0; i < 200; i++)
        {
            if (driver.OnPreFrame() == FrameStep.Stalled) break;
            emu.AdvanceAppliedFrame();
            driver.OnPostFrame();
        }
        int stalledAt = driver.CurrentFrame;
        Assert.True(stalledAt > maxRollback,
            $"the sim stalled at frame {stalledAt}, before reaching its prediction cap");

        // Contradict the oldest frame the driver will still accept — the deepest legal correction.
        int deepest = stalledAt - maxRollback;
        transport.Enqueue(RemoteFrame(driver, deepest, 1));

        driver.OnPreFrame();   // throws if the base for `deepest` was pruned away
        Assert.True(driver.CurrentFrame >= deepest);
    }

    /// <summary>
    /// Frames that were fully confirmed when they ran are never rollback targets, which is what
    /// makes eliding their snapshots safe.
    ///
    /// Stated in a comment as "the elided frames are exactly the ones no correction can reach" and
    /// load-bearing for the whole elision feature: if a confirmed frame could be contradicted later,
    /// eliding it would remove the only base a repair could have used.
    /// </summary>
    [Fact]
    public void AFrameThatRanFullyConfirmedIsNeverContradictedLater()
    {
        var transport = new QueueTransport();
        var driver = Build(transport, maxRollback: 8, keyframeInterval: 1, elide: true, out var emu);
        var rollback = (RollbackStrategy)driver.Strategy;

        // Feed the remote's input just ahead of the sim, as a real peer at this delay does, so
        // every frame runs fully confirmed. Dumping the whole run up front does NOT work and the
        // reason is worth stating: the driver drops frames beyond its accept lead, so the tail
        // would be lost, predicted, and then contradicted by the re-delivery below — a rollback
        // caused by the test rather than by the thing under test.
        int fed = 0;
        for (int i = 0; i < 50; i++)
        {
            while (fed <= driver.CurrentFrame + Delay)
            {
                transport.Enqueue(RemoteFrame(driver, fed, (byte)(fed % 3 == 0 ? 1 : 0)));
                fed++;
            }
            if (driver.OnPreFrame() == FrameStep.Stalled) break;
            emu.AdvanceAppliedFrame();
            driver.OnPostFrame();
        }

        Assert.True(driver.CurrentFrame > 20, "the sim did not run far enough to prove anything");

        // Re-deliver every one of those frames, exactly as a redundant window would. None of them
        // can contradict what was applied, because what was applied WAS the real input.
        int before = rollback.RollbackCount;
        for (int frame = 0; frame < driver.CurrentFrame; frame++)
            transport.Enqueue(RemoteFrame(driver, frame, (byte)(frame % 3 == 0 ? 1 : 0)));
        driver.PumpNetwork();
        driver.OnPreFrame();

        Assert.Equal(before, rollback.RollbackCount);
    }
}
