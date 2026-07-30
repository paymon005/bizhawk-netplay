using System;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Sync;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

public class LockstepAndPipelineTests
{
    private static PortInput Btn(bool a) =>
        new([a], []);

    [Fact]
    public void Frontier_StartsEmpty()
    {
        var p = new InputPipeline(2);
        Assert.Equal(-1, p.ConfirmedFrontier(0));
        Assert.Equal(-1, p.MinFrontier());
    }

    [Fact]
    public void Frontier_AdvancesOnlyContiguously()
    {
        var p = new InputPipeline(1);
        p.Add(0, 0, Btn(false));
        p.Add(0, 1, Btn(false));
        Assert.Equal(1, p.ConfirmedFrontier(0));

        // A gap: frame 3 arrives before 2 — frontier must not jump past the hole.
        p.Add(0, 3, Btn(false));
        Assert.Equal(1, p.ConfirmedFrontier(0));

        // Filling the hole advances across the whole contiguous run.
        p.Add(0, 2, Btn(false));
        Assert.Equal(3, p.ConfirmedFrontier(0));
    }

    [Fact]
    public void Add_IsIdempotentForRedundantPackets()
    {
        var p = new InputPipeline(1);
        p.Add(0, 0, Btn(true));
        p.Add(0, 0, Btn(false)); // redundant re-send; first value wins, no frontier corruption
        Assert.Equal(0, p.ConfirmedFrontier(0));
        Assert.True(p.TryGet(0, 0, out var got));
        Assert.True(got.Buttons[0]); // original retained
    }

    [Fact]
    public void MinFrontier_TracksLaggingPort()
    {
        var p = new InputPipeline(2);
        p.Add(0, 0, Btn(false));
        p.Add(0, 1, Btn(false));
        p.Add(1, 0, Btn(false));
        Assert.Equal(1, p.ConfirmedFrontier(0));
        Assert.Equal(0, p.ConfirmedFrontier(1));
        Assert.Equal(0, p.MinFrontier());
    }

    [Fact]
    public void Lockstep_RunsWhenAllPortsConfirmed()
    {
        var p = new InputPipeline(2);
        var s = new LockstepStrategy(p);

        p.Add(0, 0, Btn(true));
        p.Add(1, 0, Btn(false));

        var decision = s.BeginFrame(0);
        Assert.False(decision.Stall);
        Assert.False(s.IsStalled);
        Assert.NotNull(decision.Inputs);
        Assert.Equal(0, decision.Inputs!.Frame);
        Assert.True(decision.Inputs.Ports[0].Buttons[0]);
        Assert.False(decision.Inputs.Ports[1].Buttons[0]);
    }

    [Fact]
    public void Lockstep_StallsWhenAPortMissing()
    {
        var p = new InputPipeline(2);
        var s = new LockstepStrategy(p);

        p.Add(0, 0, Btn(true)); // port 1 has nothing yet

        var decision = s.BeginFrame(0);
        Assert.True(decision.Stall);
        Assert.True(s.IsStalled);
        Assert.Null(decision.Inputs);
    }

    [Fact]
    public void Lockstep_UnstallsAfterMissingInputArrives()
    {
        var p = new InputPipeline(2);
        var s = new LockstepStrategy(p);
        p.Add(0, 0, Btn(true));

        Assert.True(s.BeginFrame(0).Stall);

        // Remote input for the lagging port arrives.
        p.Add(1, 0, Btn(false));

        var again = s.BeginFrame(0);
        Assert.False(again.Stall);
        Assert.False(s.IsStalled);
    }

    [Fact]
    public void Merge_ThrowsIfPortMissing()
    {
        var p = new InputPipeline(2);
        p.Add(0, 5, Btn(false));
        Assert.Throws<InvalidOperationException>(() => p.Merge(5));
    }

    [Fact]
    public void PruneBefore_DropsOldFramesKeepsFrontier()
    {
        var p = new InputPipeline(1);
        for (int f = 0; f <= 10; f++) p.Add(0, f, Btn(false));
        Assert.Equal(10, p.ConfirmedFrontier(0));

        p.PruneBefore(8);
        Assert.False(p.TryGet(0, 5, out _));
        Assert.True(p.TryGet(0, 9, out _));
        Assert.Equal(10, p.ConfirmedFrontier(0)); // frontier is a watermark, unaffected by prune
    }

    [Fact]
    public void PruneBefore_SweepsFramesStrandedBelowAHole()
    {
        // The incremental walk this now does could only ever be safe if a hole in the map does not
        // hide everything under it forever. Frames 0-3 sit below a gap at 4; a boundary that steps
        // past them must still drop them, or a lossy remote port leaks for the whole session.
        var p = new InputPipeline(1);
        for (int f = 0; f <= 3; f++) p.Add(0, f, Btn(false));
        for (int f = 6; f <= 9; f++) p.Add(0, f, Btn(false));   // 4 and 5 never arrived

        for (int boundary = 1; boundary <= 8; boundary++) p.PruneBefore(boundary);

        for (int f = 0; f <= 3; f++) Assert.False(p.TryGet(0, f, out _));
        Assert.False(p.TryGet(0, 7, out _));
        Assert.True(p.TryGet(0, 8, out _));
    }

    [Fact]
    public void PruneBefore_HandlesABoundaryThatJumpsOrGoesBackwards()
    {
        // A resync or a rejoin moves the frame counter in one step, and a rollback repair moves it
        // backwards. Neither may leave the watermark ahead of the map, or everything between the
        // old boundary and the new one is never visited again.
        var p = new InputPipeline(1);
        for (int f = 0; f <= 40; f++) p.Add(0, f, Btn(false));

        p.PruneBefore(2);
        p.PruneBefore(1);        // backwards: nothing new to drop, watermark follows
        p.PruneBefore(400);      // a jump far past anything held
        for (int f = 0; f <= 40; f++) Assert.False(p.TryGet(0, f, out _));

        // And the pipeline still works afterwards.
        p.Add(0, 500, Btn(true));
        p.PruneBefore(500);
        Assert.True(p.TryGet(0, 500, out _));
    }
}
