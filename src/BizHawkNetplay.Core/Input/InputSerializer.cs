using System;

namespace BizHawkNetplay.Core.Input;

/// <summary>
/// Packs and unpacks one port's input against a fixed <see cref="ControllerLayout"/>.
/// Buttons pack into a little-endian bitfield; each axis packs as an unsigned offset from
/// its Min in the axis's native byte width. Both peers build an identical serializer from
/// the layout exchanged at handshake, so the byte format is derived, never hardcoded per game.
/// </summary>
public sealed class InputSerializer
{
    private readonly ControllerLayout _layout;
    // Lifted out of the layout once. Both loops below run per serialized frame and per accepted
    // remote frame, and reaching Axes[i] through IReadOnlyList is an interface call per axis per
    // frame for a set that cannot change after the handshake.
    private readonly AxisSpec[] _axes;
    private readonly int _buttonCount;
    private readonly int _buttonByteWidth;
    private readonly int _payloadSize;

    public InputSerializer(ControllerLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _buttonCount = layout.Buttons.Count;
        _buttonByteWidth = layout.ButtonByteWidth;
        _payloadSize = layout.PayloadByteWidth;
        _axes = new AxisSpec[layout.Axes.Count];
        for (int i = 0; i < _axes.Length; i++) _axes[i] = layout.Axes[i];
    }

    /// <summary>Fixed serialized size of one port under this layout.</summary>
    public int PayloadSize => _payloadSize;

    public byte[] Serialize(PortInput input)
    {
        var buffer = new byte[_payloadSize];
        Serialize(input, buffer, 0);
        return buffer;
    }

    /// <summary>
    /// Pack one port's input directly into a caller-owned buffer.
    ///
    /// The send path holds a ring of the last N frames and rebuilds a datagram from it every frame;
    /// handing it a fresh array per frame — and then copying that array into the datagram, and the
    /// datagram into a framed copy — was three allocations and two copies for bytes whose home was
    /// already known.
    /// </summary>
    public void Serialize(PortInput input, byte[] destination, int offset)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (input.Buttons.Length != _buttonCount)
            throw new ArgumentException($"Button count {input.Buttons.Length} != layout {_buttonCount}");
        if (input.Axes.Length != _axes.Length)
            throw new ArgumentException($"Axis count {input.Axes.Length} != layout {_axes.Length}");
        if (offset < 0 || destination.Length - offset < _payloadSize)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"Payload of {_payloadSize} bytes does not fit at offset {offset} of {destination.Length}");

        // Buttons: LSB-first bitfield. Cleared first — the buffer is reused, so the OR below would
        // otherwise carry a held button forward into a frame that released it.
        for (int b = 0; b < _buttonByteWidth; b++) destination[offset + b] = 0;
        for (int i = 0; i < input.Buttons.Length; i++)
            if (input.Buttons[i])
                destination[offset + (i >> 3)] |= (byte)(1 << (i & 7));
        int cursor = offset + _buttonByteWidth;

        // Axes: offset-from-Min, little-endian, native width.
        for (int i = 0; i < input.Axes.Length; i++)
        {
            var axis = _axes[i];
            int clamped = input.Axes[i];
            if (clamped < axis.Min) clamped = axis.Min;
            else if (clamped > axis.Max) clamped = axis.Max;

            ulong rel = (ulong)((long)clamped - axis.Min);
            int width = axis.ByteWidth;
            for (int b = 0; b < width; b++)
                destination[cursor + b] = (byte)(rel >> (8 * b));
            cursor += width;
        }
    }

    public PortInput Deserialize(byte[] payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (payload.Length != _payloadSize)
            throw new ArgumentException($"Payload size {payload.Length} != expected {_payloadSize}");
        return Deserialize(payload, 0);
    }

    /// <summary>
    /// Unpack one port's input from the middle of a larger buffer. A datagram carries a whole
    /// redundant window back to back, and the receive path drops most of those frames as already
    /// held — copying each one into its own array before deciding that was the waste.
    /// </summary>
    public PortInput Deserialize(byte[] buffer, int offset)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || buffer.Length - offset < _payloadSize)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"Payload of {_payloadSize} bytes does not fit at offset {offset} of {buffer.Length}");

        int cursor = offset;
        var buttons = new bool[_buttonCount];
        for (int i = 0; i < buttons.Length; i++)
            buttons[i] = (buffer[cursor + (i >> 3)] & (1 << (i & 7))) != 0;
        cursor += _buttonByteWidth;

        var axes = new int[_axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            var axis = _axes[i];
            int width = axis.ByteWidth;
            ulong rel = 0;
            for (int b = 0; b < width; b++)
                rel |= (ulong)buffer[cursor + b] << (8 * b);
            axes[i] = (int)((long)rel + axis.Min);
            cursor += width;
        }

        return new PortInput(buttons, axes);
    }
}
