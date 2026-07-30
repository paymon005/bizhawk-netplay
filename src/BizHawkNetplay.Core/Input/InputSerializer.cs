using System;

namespace BizHawkNetplay.Core.Input
{
    /// <summary>
    /// Packs and unpacks one port's input against a fixed <see cref="ControllerLayout"/>.
    /// Buttons pack into a little-endian bitfield; each axis packs as an unsigned offset from
    /// its Min in the axis's native byte width. Both peers build an identical serializer from
    /// the layout exchanged at handshake, so the byte format is derived, never hardcoded per game.
    /// </summary>
    public sealed class InputSerializer
    {
        private readonly ControllerLayout _layout;

        public InputSerializer(ControllerLayout layout)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        }

        /// <summary>Fixed serialized size of one port under this layout.</summary>
        public int PayloadSize => _layout.PayloadByteWidth;

        public byte[] Serialize(PortInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Buttons.Length != _layout.Buttons.Count)
                throw new ArgumentException($"Button count {input.Buttons.Length} != layout {_layout.Buttons.Count}");
            if (input.Axes.Length != _layout.Axes.Count)
                throw new ArgumentException($"Axis count {input.Axes.Length} != layout {_layout.Axes.Count}");

            var buffer = new byte[_layout.PayloadByteWidth];
            int offset = 0;

            // Buttons: LSB-first bitfield.
            for (int i = 0; i < input.Buttons.Length; i++)
                if (input.Buttons[i])
                    buffer[offset + (i >> 3)] |= (byte)(1 << (i & 7));
            offset += _layout.ButtonByteWidth;

            // Axes: offset-from-Min, little-endian, native width.
            for (int i = 0; i < input.Axes.Length; i++)
            {
                var axis = _layout.Axes[i];
                int clamped = input.Axes[i];
                if (clamped < axis.Min) clamped = axis.Min;
                else if (clamped > axis.Max) clamped = axis.Max;

                ulong rel = (ulong)((long)clamped - axis.Min);
                int width = axis.ByteWidth;
                for (int b = 0; b < width; b++)
                    buffer[offset + b] = (byte)(rel >> (8 * b));
                offset += width;
            }

            return buffer;
        }

        public PortInput Deserialize(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length != _layout.PayloadByteWidth)
                throw new ArgumentException($"Payload size {payload.Length} != expected {_layout.PayloadByteWidth}");
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
            if (offset < 0 || buffer.Length - offset < _layout.PayloadByteWidth)
                throw new ArgumentOutOfRangeException(nameof(offset),
                    $"Payload of {_layout.PayloadByteWidth} bytes does not fit at offset {offset} of {buffer.Length}");

            int cursor = offset;
            var buttons = new bool[_layout.Buttons.Count];
            for (int i = 0; i < buttons.Length; i++)
                buttons[i] = (buffer[cursor + (i >> 3)] & (1 << (i & 7))) != 0;
            cursor += _layout.ButtonByteWidth;

            var axes = new int[_layout.Axes.Count];
            for (int i = 0; i < axes.Length; i++)
            {
                var axis = _layout.Axes[i];
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
}
