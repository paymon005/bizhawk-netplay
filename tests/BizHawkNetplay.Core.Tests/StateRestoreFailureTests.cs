using System;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// What happens when the core refuses a savestate.
///
/// Every other failure in this codebase is a network condition: a peer goes quiet, a frame is late,
/// a link drops. This one is not recoverable by anything, and the code says so in three places —
/// a named exception, a latch, and a <c>finally</c> that releases the pin before rethrowing — none
/// of which had a test. The path had never been driven once, so "the failure is latched so the next
/// entry point fails immediately" was a comment about code nobody had run.
///
/// It is not hypothetical: a core that rejects its own savestate is what a mismatched core version,
/// a corrupted pooled buffer, or a BizHawk bug looks like from here, and the difference between
/// telling the player "the core failed" and telling them "session error" is the difference between
/// a report they can act on and one they cannot.
/// </summary>
public class StateRestoreFailureTests
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

    /// <summary>
    /// Run with the remote's input arriving just ahead of the sim, so every frame is confirmed and
    /// the checksum path has a boundary it can reach. <paramref name="value"/> is what the remote
    /// holds down the whole time, which makes prediction repeat it — the setup a later
    /// contradiction needs.
    /// </summary>
    private static void RunConfirmed(FrameDriver driver, FakeEmuAdapter emu, QueueTransport transport,
        int frames, byte value = 1)
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

    /// <summary>Run with the remote silent, so the frames advanced here are predictions a later
    /// correction can contradict.</summary>
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
    /// A core that refuses the state returning it to the live frame fails by name, not as a
    /// generic session error, and keeps the core's own exception as the inner one.
    ///
    /// The tool catches this type specifically to avoid prefixing it with "session error:" — a
    /// prefix that would tell the player the network broke when the emulator did.
    /// </summary>
    [Fact]
    public void ACoreThatRefusesTheWayBackFailsByName()
    {
        var transport = new QueueTransport();
        var driver = Build(transport, out var emu, out var rollback);
        RunConfirmed(driver, emu, transport, 40);

        // Refuse ONLY the return trip. The checksum visit loads the boundary state fine and then
        // cannot get home, and that asymmetry is the entire reason this exception exists: the core
        // is now standing somewhere the session does not know about.
        int live = driver.CurrentFrame;
        var core = new InvalidOperationException("core says no");
        emu.LoadFault = handle => handle.Frame == live ? core : null;

        var thrown = Assert.Throws<StateRestoreFailedException>(
            () => rollback.TryConfirmedChecksum(interval: 10, out _, out _));
        Assert.Contains("core state restore failed", thrown.Message);
        Assert.Contains("live frame", thrown.Message);
        Assert.Same(core, thrown.InnerException);
    }

    /// <summary>
    /// The pin is released even though the restore failed.
    ///
    /// Stated in a comment as "the release happens either way — a failed restore is fatal to the
    /// session, but leaking the pooled state on the way out helps nobody". On a heavy core the pin
    /// is the whole console's RAM, and the session's death is not instant: the player sees the
    /// message, and the tool keeps running.
    /// </summary>
    [Fact]
    public void TheLivePinIsReleasedEvenWhenPuttingItBackFails()
    {
        var transport = new QueueTransport();
        var driver = Build(transport, out var emu, out var rollback);
        RunConfirmed(driver, emu, transport, 40);

        int live = driver.CurrentFrame;
        int liveBefore = emu.LiveStates.Count;
        int savesBefore = emu.SaveCount;
        emu.LoadFault = handle => handle.Frame == live ? new InvalidOperationException("no") : null;

        Assert.Throws<StateRestoreFailedException>(
            () => rollback.TryConfirmedChecksum(interval: 10, out _, out _));

        // The pin was genuinely taken — without this the count check below would hold just as
        // happily for a call that never got as far as saving anything.
        Assert.True(emu.SaveCount > savesBefore, "no pin was taken, so nothing was proved about releasing one");
        Assert.Equal(liveBefore, emu.LiveStates.Count);
    }

    /// <summary>
    /// Once stranded, every later entry point refuses immediately rather than doing more work
    /// against a core standing on the wrong frame.
    ///
    /// The latch matters because the failure is not fatal to the PROCESS: the frame timer keeps
    /// firing while the tool tears the session down. Without it, each of those ticks would run
    /// frames, snapshot them and hash them, all describing a timeline no peer shares — and the
    /// checksum comparisons would report a desync, sending everyone into a resync to recover from
    /// a core that cannot load a state.
    /// </summary>
    [Fact]
    public void AStrandedCoreIsRefusedFromThenOnWithoutTouchingIt()
    {
        var transport = new QueueTransport();
        var driver = Build(transport, out var emu, out var rollback);
        RunConfirmed(driver, emu, transport, 40);

        int live = driver.CurrentFrame;
        emu.LoadFault = handle => handle.Frame == live ? new InvalidOperationException("no") : null;
        Assert.Throws<StateRestoreFailedException>(
            () => rollback.TryConfirmedChecksum(interval: 10, out _, out _));

        // Clear the fault entirely: the core would now behave. It must not be asked to, because it
        // is standing on a frame nothing here knows.
        emu.LoadFault = null;
        int loadsBefore = emu.LoadCount, savesBefore = emu.SaveCount, hashesBefore = emu.HashCount;

        var again = Assert.Throws<StateRestoreFailedException>(
            () => rollback.TryConfirmedChecksum(interval: 10, out _, out _));
        Assert.Contains("earlier failed restore", again.Message);

        Assert.Equal(loadsBefore, emu.LoadCount);
        Assert.Equal(savesBefore, emu.SaveCount);
        Assert.Equal(hashesBefore, emu.HashCount);

        // And the driver stops claiming it can run: the gate reads the latch, so a session that
        // has not torn down yet stalls rather than simulating a private timeline.
        Assert.False(rollback.CanRunWithoutStalling(driver.CurrentFrame));
    }

    /// <summary>
    /// A repair that cannot load the base state it must replay from fails the same named way, and
    /// names the frame — the one detail that distinguishes a core rejecting one state from a core
    /// rejecting all of them.
    /// </summary>
    [Fact]
    public void ARepairThatCannotReachItsBaseFailsByNameAndNamesTheFrame()
    {
        var transport = new QueueTransport();
        var driver = Build(transport, out var emu, out var rollback);
        RunConfirmed(driver, emu, transport, 30, value: 1);
        // Silence: the frames advanced here repeat the remote's last input as a prediction.
        RunPredicting(driver, emu, 5);

        int contradicted = driver.CurrentFrame - 3;
        emu.LoadFault = _ => new InvalidOperationException("core says no");
        transport.Enqueue(QueueTransport.Frame(driver, port: 1, contradicted, value: 0));

        var thrown = Assert.Throws<StateRestoreFailedException>(() => driver.OnPreFrame());
        Assert.Contains("core state restore failed", thrown.Message);
        Assert.Contains("rollback repair", thrown.Message);
        Assert.Contains($"savestate for frame", thrown.Message);
    }
}
