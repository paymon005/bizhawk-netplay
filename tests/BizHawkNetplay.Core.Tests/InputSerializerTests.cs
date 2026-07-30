using System;
using System.Linq;
using BizHawkNetplay.Core.Input;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

public class InputSerializerTests
{
    private static ControllerLayout NesLayout() => new(
        new[] { "Up", "Down", "Left", "Right", "A", "B", "Start", "Select" },
        Array.Empty<AxisSpec>());

    private static ControllerLayout AnalogLayout() => new(
        new[] { "A", "B" },
        new[]
        {
            new AxisSpec("StickX", -128, 127, 0),   // fits in 1 byte
            new AxisSpec("StickY", 0, 65535, 32768), // needs 2 bytes
            new AxisSpec("Trigger", 0, 1_000_000, 0) // needs 4 bytes
        });

    [Fact]
    public void ButtonOnlyLayout_PacksIntoOneByte()
    {
        var layout = NesLayout();
        Assert.Equal(1, layout.ButtonByteWidth);
        Assert.Equal(1, layout.PayloadByteWidth);
    }

    [Fact]
    public void AxisWidths_DerivedFromRange()
    {
        var layout = AnalogLayout();
        Assert.Equal(1, layout.Axes[0].ByteWidth);
        Assert.Equal(2, layout.Axes[1].ByteWidth);
        Assert.Equal(4, layout.Axes[2].ByteWidth);
        // 1 button byte + 1 + 2 + 4
        Assert.Equal(8, layout.PayloadByteWidth);
    }

    [Fact]
    public void RoundTrip_Buttons()
    {
        var layout = NesLayout();
        var ser = new InputSerializer(layout);
        var input = new PortInput(
            [true, false, false, true, true, false, false, true],
            []);

        var bytes = ser.Serialize(input);
        var back = ser.Deserialize(bytes);

        Assert.True(input.ValueEquals(back));
    }

    [Fact]
    public void RoundTrip_MixedAxes_PreservesExactValues()
    {
        var layout = AnalogLayout();
        var ser = new InputSerializer(layout);
        var input = new PortInput(
            [true, false],
            [-128, 65535, 999_999]);

        var bytes = ser.Serialize(input);
        Assert.Equal(layout.PayloadByteWidth, bytes.Length);
        var back = ser.Deserialize(bytes);

        Assert.True(input.ValueEquals(back));
        Assert.Equal(-128, back.Axes[0]);
        Assert.Equal(65535, back.Axes[1]);
        Assert.Equal(999_999, back.Axes[2]);
    }

    [Fact]
    public void Serialize_ClampsOutOfRangeAxis()
    {
        var layout = AnalogLayout();
        var ser = new InputSerializer(layout);
        var input = new PortInput([false, false], [999, -5, 2_000_000]);

        var back = ser.Deserialize(ser.Serialize(input));

        Assert.Equal(127, back.Axes[0]);   // clamped to max
        Assert.Equal(0, back.Axes[1]);     // clamped to min
        Assert.Equal(1_000_000, back.Axes[2]);
    }

    [Fact]
    public void Digest_DiffersWhenLayoutDiffers()
    {
        var a = NesLayout();
        var b = new ControllerLayout(
            new[] { "Up", "Down", "Left", "Right", "A", "B", "Start" }, // one fewer button
            Array.Empty<AxisSpec>());
        Assert.NotEqual(a.Digest, b.Digest);
    }

    [Fact]
    public void Digest_StableForIdenticalLayout()
    {
        Assert.Equal(NesLayout().Digest, NesLayout().Digest);
    }

    [Fact]
    public void Deserialize_RejectsWrongSize()
    {
        var ser = new InputSerializer(NesLayout());
        Assert.Throws<ArgumentException>(() => ser.Deserialize(new byte[2]));
    }

    [Fact]
    public void DeserializeAtOffset_ReadsOnePayloadOutOfAPackedRun()
    {
        var ser = new InputSerializer(AnalogLayout());
        var first = new PortInput([true, false], [-100, 60000, 999_999]);
        var second = new PortInput([false, true], [42, 7, 0]);

        // Lay the two payloads out back to back, the way a datagram carries a redundant window.
        var packed = ser.Serialize(first).Concat(ser.Serialize(second)).ToArray();
        int width = ser.PayloadSize;

        Assert.True(first.ValueEquals(ser.Deserialize(packed, 0)));
        Assert.True(second.ValueEquals(ser.Deserialize(packed, width)));
    }

    [Fact]
    public void DeserializeAtOffset_RefusesAPayloadThatWouldRunOffTheEnd()
    {
        var ser = new InputSerializer(NesLayout());
        var buffer = new byte[3];
        Assert.Throws<ArgumentOutOfRangeException>(() => ser.Deserialize(buffer, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ser.Deserialize(buffer, 3));
        Assert.Throws<ArgumentNullException>(() => ser.Deserialize(null!, 0));
    }
}
