using System;
using BizHawkNetplay.Core.Input;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// An axis at rest reads its own Neutral, which is not always zero.
///
/// This is the contract the Tool's InputSetController now depends on: on a four-port core hosting
/// two players, the two empty seats are written every single frame, and writing 0 to an unsigned
/// axis — a stick or trigger declared 0..255 resting at 128 — pins it at full deflection for the
/// life of the session. That directly contradicts the README's "unused ports read neutral", and it
/// is invisible on the systems most played, because a signed N64/analog stick happens to rest at 0.
///
/// InputSetController itself cannot be reached from here: it is a BizHawk IController living in the
/// net48-only Tool project, and this suite also targets net10.0, so referencing it would either
/// break that target or make `dotnet test` require a BizHawk install. What is testable is the Core
/// side it defers to, so that is what is pinned.
/// </summary>
public class NeutralAxisTests
{
    /// <summary>The shape that exposes the bug: unsigned range, non-zero rest, zero is an extreme.</summary>
    private static ControllerLayout UnsignedStickLayout() => new(
        new[] { "P2 A", "P2 B" },
        new[]
        {
            new AxisSpec("P2 X Axis", 0, 255, 128),
            new AxisSpec("P2 Y Axis", 0, 255, 128),
            new AxisSpec("P2 Trigger", 0, 255, 0), // rests at an end — neutral is not always mid-range
        });

    [Fact]
    public void NeutralPortRestsAtEachAxisNeutral_NotZero()
    {
        var port = PortInput.Neutral(UnsignedStickLayout());

        Assert.Equal(new[] { 128, 128, 0 }, port.Axes);
        Assert.All(port.Buttons, b => Assert.False(b));
    }

    [Fact]
    public void AllNeutralFillsEveryPort_IncludingSeatsNobodyIsSitting()
    {
        // Four layouts, which is the case that matters: a 4-port core with 2 players leaves two
        // ports that nothing ever writes a real value to.
        var layouts = new[]
        {
            UnsignedStickLayout(), UnsignedStickLayout(), UnsignedStickLayout(), UnsignedStickLayout(),
        };

        var set = InputSet.AllNeutral(frame: 0, layouts);

        Assert.Equal(4, set.Ports.Length);
        foreach (var port in set.Ports) Assert.Equal(new[] { 128, 128, 0 }, port.Axes);
    }

    [Fact]
    public void ANeutralValueSurvivesTheWire()
    {
        // Guards the other half: neutral is only useful if it round-trips as itself. 128 in a
        // 0..255 axis packs as offset-from-Min, so a regression to signed handling would show here.
        var layout = UnsignedStickLayout();
        var serializer = new InputSerializer(layout);

        var restored = serializer.Deserialize(serializer.Serialize(PortInput.Neutral(layout)));

        Assert.Equal(new[] { 128, 128, 0 }, restored.Axes);
    }

    [Fact]
    public void ZeroIsAFullDeflectionOnSuchAnAxis()
    {
        // States the bug's consequence as an assertion, so the reason this file exists cannot be
        // read as pedantry: on this axis, 0 is not "no input", it is as far as the stick goes.
        var spec = new AxisSpec("P1 X Axis", 0, 255, 128);

        Assert.NotEqual(0, spec.Neutral);
        Assert.Equal(spec.Min, 0);
        Assert.True(Math.Abs(0 - spec.Neutral) == 128, "zero is a full-scale deflection here");
    }
}
