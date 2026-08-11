using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The fold that lets two players take turns on one joystick. Shaped after the case that forced it:
/// Atari 7800 Robotron reads controller 1 as the movement stick and controller 2 as the fire stick,
/// for both players, so a seat-2 player was holding the aim stick and could never walk.
/// </summary>
public class SharedControlFoldTests
{
    // Port 0 as the adapter builds it for a 7800: the pad's own controls, then the console tail
    // (Select/Reset) appended after them. Port 1 is the pad alone.
    private static ControllerLayout PadWithConsoleTail() => new(
        new[] { "P1 Up", "P1 Down", "P1 Left", "P1 Right", "Select", "Reset" },
        Array.Empty<AxisSpec>());

    private static ControllerLayout Pad() => new(
        new[] { "P2 Up", "P2 Down", "P2 Left", "P2 Right" },
        Array.Empty<AxisSpec>());

    private static SharedControlFold TailedFold(params bool[] foldable) => new(
        new[] { PadWithConsoleTail(), Pad() },
        new[] { 4, 4 },   // port 0's leading run of real pad buttons is 4; the tail is the rest
        new[] { 0, 0 },
        foldable.Length > 0 ? foldable : new[] { true, true });

    private static PortInput Buttons(params bool[] pressed) => new(pressed, Array.Empty<int>());

    [Fact]
    public void SeatTwosStick_ReachesControllerOne()
    {
        var fold = TailedFold();
        // Nobody on port 0, seat 2 holding Left: on Robotron this is the joiner's turn.
        var folded = fold.Apply(new InputSet(7, new[]
        {
            Buttons(false, false, false, false, false, false),
            Buttons(false, false, true, false),
        }));

        Assert.True(folded.Ports[0].Buttons[2]);              // Left arrives on controller 1
        Assert.All(folded.Ports[1].Buttons, b => Assert.False(b)); // and controller 2 is silent
        Assert.Equal(7, folded.Frame);
    }

    [Fact]
    public void BothSeats_MergeByOr()
    {
        var fold = TailedFold();
        var folded = fold.Apply(new InputSet(1, new[]
        {
            Buttons(true, false, false, false, false, false),  // host: Up
            Buttons(false, false, false, true),                // joiner: Right
        }));

        Assert.True(folded.Ports[0].Buttons[0]);
        Assert.True(folded.Ports[0].Buttons[3]);
    }

    [Fact]
    public void ConsoleTail_StaysTheHosts()
    {
        var fold = TailedFold();
        // The joiner's pad has four buttons; port 0's Select and Reset sit at indices 4 and 5, which
        // is past anything a pad seat can reach. If the merge were bounded by array length instead
        // of by the player run, a joiner holding a direction would be resetting everyone's console.
        var folded = fold.Apply(new InputSet(1, new[]
        {
            Buttons(false, false, false, false, true, false),  // host presses Select
            Buttons(true, true, true, true),                   // joiner leans on every direction
        }));

        Assert.True(folded.Ports[0].Buttons[4]);   // Select: the host's, and still pressed
        Assert.False(folded.Ports[0].Buttons[5]);  // Reset: nobody pressed it, and nobody can
    }

    [Fact]
    public void ShorterSeatArray_DoesNotOverrun()
    {
        // Same shapes, but ask the fold to believe port 0's player run is longer than the seat's
        // array — the defensive path if a layout ever disagreed with its own counts.
        var fold = new SharedControlFold(
            new[] { PadWithConsoleTail(), Pad() },
            new[] { 6, 4 },
            new[] { 0, 0 },
            new[] { true, true });

        var folded = fold.Apply(new InputSet(1, new[]
        {
            Buttons(false, false, false, false, false, false),
            Buttons(true, false, false, false),
        }));

        Assert.True(folded.Ports[0].Buttons[0]);
    }

    // --- axes -----------------------------------------------------------------

    private static SharedControlFold AxisFold(int neutral = 128)
    {
        var layout = new ControllerLayout(
            Array.Empty<string>(),
            new[] { new AxisSpec("X", 0, 255, neutral) });
        return new SharedControlFold(
            new[] { layout, layout }, new[] { 0, 0 }, new[] { 1, 1 }, new[] { true, true });
    }

    private static PortInput Axis(int value) => new(Array.Empty<bool>(), new[] { value });

    [Fact]
    public void Axis_FurthestFromNeutralWins()
    {
        var folded = AxisFold().Apply(new InputSet(1, new[] { Axis(100), Axis(200) }));
        Assert.Equal(200, folded.Ports[0].Axes[0]); // |200-128| = 72 beats |100-128| = 28
    }

    [Fact]
    public void Axis_TieGoesToTheLowestSeat()
    {
        var folded = AxisFold().Apply(new InputSet(1, new[] { Axis(100), Axis(156) }));
        Assert.Equal(100, folded.Ports[0].Axes[0]); // both 28 from rest; port 0 keeps it
    }

    [Fact]
    public void UntouchedAxis_RestsAtItsOwnNeutral()
    {
        // Not zero. On an unsigned axis zero is a full-scale deflection, which is the bug this
        // mirrors from InputSetController.
        var folded = AxisFold().Apply(new InputSet(1, new[] { Axis(128), Axis(128) }));
        Assert.Equal(128, folded.Ports[0].Axes[0]);
        Assert.Equal(128, folded.Ports[1].Axes[0]);
    }

    // --- shape and safety -----------------------------------------------------

    [Fact]
    public void NonFoldablePort_ContributesNothingAndIsHeldNeutral()
    {
        var fold = TailedFold(true, false);
        var folded = fold.Apply(new InputSet(1, new[]
        {
            Buttons(false, false, false, false, false, false),
            Buttons(true, true, true, true),
        }));

        Assert.All(folded.Ports[0].Buttons, b => Assert.False(b));
        Assert.All(folded.Ports[1].Buttons, b => Assert.False(b));
    }

    [Fact]
    public void SetNarrowerThanTheCore_KeepsItsOwnWidth()
    {
        // Two players on a four-port core: the InputSet carries two entries and must keep carrying
        // two, since InputSetController already rests the ports past the end at their own neutral.
        var pad = Pad();
        var fold = new SharedControlFold(
            new[] { PadWithConsoleTail(), pad, pad, pad },
            new[] { 4, 4, 4, 4 }, new[] { 0, 0, 0, 0 },
            new[] { true, true, true, true });

        var folded = fold.Apply(new InputSet(1, new[]
        {
            Buttons(false, false, false, false, false, false),
            Buttons(true, false, false, false),
        }));

        Assert.Equal(2, folded.Ports.Length);
        Assert.True(folded.Ports[0].Buttons[0]);
    }

    [Fact]
    public void Apply_DoesNotMutateItsInput()
    {
        // Rollback keeps the exact PortInput objects it ran and compares them against the pipeline
        // to decide whether a repair is needed. Writing through them would corrupt that comparison,
        // which is a wrong-state bug that no checksum would attribute to the fold.
        var fold = TailedFold();
        var host = Buttons(true, false, false, false, true, false);
        var joiner = Buttons(false, true, false, false);
        var before = new List<bool[]>
        {
            (bool[])host.Buttons.Clone(), (bool[])joiner.Buttons.Clone(),
        };

        fold.Apply(new InputSet(1, new[] { host, joiner }));

        Assert.Equal(before[0], host.Buttons);
        Assert.Equal(before[1], joiner.Buttons);
    }

    [Fact]
    public void AllNeutralIn_AllNeutralOut()
    {
        // The capability probe steps the core with neutral input and never touches a pad; folding
        // must not invent a press for it.
        var fold = TailedFold();
        var folded = fold.Apply(new InputSet(1, new[]
        {
            Buttons(false, false, false, false, false, false),
            Buttons(false, false, false, false),
        }));

        Assert.All(folded.Ports[0].Buttons, b => Assert.False(b));
        Assert.All(folded.Ports[1].Buttons, b => Assert.False(b));
    }

    [Fact]
    public void ReusedBuffers_ReflectOnlyTheLatestFrame()
    {
        // The instance is reused every frame, including every re-simulated frame of a repair. A
        // press left standing from the previous call would be a phantom input on the next.
        var fold = TailedFold();
        fold.Apply(new InputSet(1, new[]
        {
            Buttons(false, false, false, false, false, false),
            Buttons(true, false, false, false),
        }));
        var second = fold.Apply(new InputSet(2, new[]
        {
            Buttons(false, false, false, false, false, false),
            Buttons(false, false, false, false),
        }));

        Assert.All(second.Ports[0].Buttons, b => Assert.False(b));
    }
}
