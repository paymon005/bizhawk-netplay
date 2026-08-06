using System;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// What the rollback ring holds after something goes wrong mid-update.
///
/// Every other test of this layer asks whether the right answer comes out when the parts work. This
/// one asks what is left behind when a part fails partway — which is a different question, and the
/// one that produces silent corruption rather than a visible error.
///
/// The ring's entries are pooled buffers. Releasing one hands it back for the next save to refill,
/// so an entry that survives its own release does not merely point at stale bytes: it points at
/// bytes that will shortly belong to a DIFFERENT frame. Restoring from it puts the core somewhere
/// nobody asked for, with nothing raised and nothing logged.
/// </summary>
public class RollbackRingLifetimeTests
{
    private const int Delay = 2;

    private static FrameDriver Build(QueueTransport transport, out FakeEmuAdapter emu,
        out RollbackStrategy rollback)
    {
        var adapter = new FakeEmuAdapter(portCount: 2) { LocalInputScript = _ => null! };
        emu = adapter;
        RollbackStrategy? built = null;
        var driver = new FrameDriver(adapter, transport,
            p =>
            {
                built = new RollbackStrategy(p, adapter, localPort: 0, maxRollback: 8, frameMs: 0,
                    new RollbackTuning { KeyframeInterval = 1, ChecksumAnchorInterval = 0 });
                return built;
            },
            localPort: 0, delay: Delay, redundancy: 4, rollbackWindow: 8);
        driver.Start();
        rollback = built!;
        return driver;
    }

    private static void RunConfirmed(FrameDriver driver, FakeEmuAdapter emu, QueueTransport transport,
        int frames, byte value)
    {
        int fed = 0;
        for (int i = 0; i < frames; i++)
        {
            while (fed <= driver.CurrentFrame + Delay)
                transport.Enqueue(QueueTransport.Frame(driver, port: 1, fed++, value));
            if (driver.OnPreFrame() == FrameStep.Stalled) return;
            emu.AdvanceAppliedFrame();
            driver.OnPostFrame();
        }
    }

    private static void RunPredicting(FrameDriver driver, FakeEmuAdapter emu, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            if (driver.OnPreFrame() == FrameStep.Stalled) return;
            emu.AdvanceAppliedFrame();
            driver.OnPostFrame();
        }
    }

    /// <summary>
    /// A save that fails must not leave the ring holding a state it has already given back.
    ///
    /// The window: a repair re-snapshots the frames it replays, so a save can land on a frame that
    /// already has an entry. Releasing the old one before taking the new one means a throw in
    /// between leaves the dictionary pointing at a handle now sitting in the pool — and the very
    /// next save pops that buffer and fills it with another frame's bytes.
    ///
    /// A MISSING entry, by contrast, is already handled: the repair walks back for an older base
    /// and, failing that, throws the named "no base in the ring". So the whole fix is to fail
    /// toward missing rather than toward dangling, which costs nothing.
    /// </summary>
    [Fact]
    public void ASaveThatThrowsLeavesNoReleasedStateInTheRing()
    {
        var transport = new QueueTransport();
        var driver = Build(transport, out var emu, out var rollback);

        // Run confirmed, then predict, so a correction below forces a repair that re-snapshots
        // frames the ring already holds — the only path that saves over an existing entry.
        RunConfirmed(driver, emu, transport, 30, value: 1);
        RunPredicting(driver, emu, 5);

        // A repair re-snapshots the frames it replays, and those frames already have entries — the
        // probe that motivated this test measured eight such overwriting saves in a healthy run.
        // Failing partway through one is what leaves the ring holding a handle it gave back.
        int saves = 0;
        emu.SaveFault = () => ++saves == 2 ? new InvalidOperationException("the core could not save") : null;

        int contradicted = driver.CurrentFrame - 3;
        transport.Enqueue(QueueTransport.Frame(driver, port: 1, contradicted, value: 0));
        try { driver.OnPreFrame(); } catch (InvalidOperationException) { /* the save fault, expected */ }

        emu.SaveFault = null;

        // Tear the ring down and see what it thought it still owned.
        //
        // Dispose releases every entry in the ring. An entry left pointing at a handle that was
        // already given back shows up here as a release of something already released — which the
        // adapter shrugs off, exactly as the real one does, and exactly why this is worth counting
        // rather than trusting. Whatever still held that reference could equally have LOADED it,
        // and by then the pool has refilled the buffer with another frame's bytes.
        rollback.Dispose();

        Assert.Equal(0, emu.ReleasesOfAlreadyReleasedState);
        Assert.Equal(0, emu.LoadsOfReleasedState);
    }

    /// <summary>
    /// A save that fails does not cost a pooled buffer.
    ///
    /// The buffer is taken before the core is asked to write into it, so a throw in between leaves
    /// it owned by nobody unless the save path hands it back. On N64 that is sixteen megabytes per
    /// failure, re-allocated on the next save — the large-object-heap churn the pool exists to
    /// remove, arriving during a failure rather than in the steady state.
    ///
    /// <b>What this does and does not prove.</b> It pins the contract against the test double. The
    /// shipping adapter is a separate implementation that no test can reach, so its matching fix
    /// rests on reading rather than on this. Worth having anyway: the double faulted BEFORE taking
    /// its buffer until now, which modelled a save path that could not lose one — so nothing here
    /// could ever have expressed the question, let alone answered it.
    /// </summary>
    [Fact]
    public void ASaveThatThrowsDoesNotCostAPooledBuffer()
    {
        var transport = new QueueTransport();
        var driver = Build(transport, out var emu, out _);
        RunConfirmed(driver, emu, transport, 40, value: 1);

        int allocatedBefore = emu.StateBuffersAllocated;
        int poolBefore = emu.StatePoolSize;
        Assert.True(poolBefore > 0, "nothing had been released yet, so there was no pool to lose from");

        for (int i = 0; i < 20; i++)
        {
            emu.SaveFault = () => new InvalidOperationException("the core could not save");
            Assert.Throws<InvalidOperationException>(() => emu.SaveStateToMemory());
        }
        emu.SaveFault = null;

        Assert.Equal(poolBefore, emu.StatePoolSize);
        Assert.Equal(allocatedBefore, emu.StateBuffersAllocated);
    }

    /// <summary>
    /// The ordinary run never loads a state it has released either.
    ///
    /// The detector above is only worth having if it reads zero when nothing is wrong, so this
    /// exercises the same machinery — repairs, elision, pruning — with no fault injected at all.
    /// </summary>
    [Fact]
    public void AHealthyRunNeverLoadsAReleasedState()
    {
        var transport = new QueueTransport();
        var driver = Build(transport, out var emu, out var rollback);
        var rng = new Random(0x21140);

        RunConfirmed(driver, emu, transport, 40, value: 1);
        for (int round = 0; round < 20; round++)
        {
            RunPredicting(driver, emu, 3);
            int target = Math.Max(0, driver.CurrentFrame - rng.Next(1, 5));
            transport.Enqueue(QueueTransport.Frame(driver, port: 1, target, (byte)(round % 2)));
            driver.PumpNetwork();
            if (driver.OnPreFrame() == FrameStep.Stalled) continue;
            emu.AdvanceAppliedFrame();
            driver.OnPostFrame();
        }

        Assert.Equal(0, emu.LoadsOfReleasedState);
        Assert.True(rollback.RollbackCount > 0, "no repair ran, so the ring was never read under one");
    }
}
