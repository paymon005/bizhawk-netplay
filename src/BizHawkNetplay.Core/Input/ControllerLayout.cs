using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BizHawkNetplay.Core.Input
{
    /// <summary>
    /// A single analog control's range, sourced from a BizHawk axis constraint.
    /// The min/max determine how many bytes the value serializes to on the wire.
    /// </summary>
    public sealed class AxisSpec
    {
        public AxisSpec(string name, int min, int max, int neutral)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Axis name required", nameof(name));
            if (max < min) throw new ArgumentException($"Axis '{name}' has max < min ({max} < {min})", nameof(max));
            Name = name;
            Min = min;
            Max = max;
            Neutral = neutral;
        }

        public string Name { get; }
        public int Min { get; }
        public int Max { get; }
        public int Neutral { get; }

        /// <summary>Bytes needed to hold any value in [Min, Max], packed as an offset from Min.</summary>
        public int ByteWidth
        {
            get
            {
                long span = (long)Max - Min; // number of distinct values minus one
                if (span <= byte.MaxValue) return 1;
                if (span <= ushort.MaxValue) return 2;
                if (span <= uint.MaxValue) return 4;
                return 8;
            }
        }
    }

    /// <summary>
    /// The complete control surface of one controller port: an ordered set of digital
    /// buttons and analog axes. Derived at session start from the core's
    /// ControllerDefinition and exchanged between peers. Both sides derive an identical
    /// compact serialization from it — this is where the "works on any core" property lives.
    /// Order is significant and must match on both peers; the <see cref="Digest"/> guards that.
    /// </summary>
    public sealed class ControllerLayout
    {
        public ControllerLayout(IReadOnlyList<string> buttons, IReadOnlyList<AxisSpec> axes)
        {
            Buttons = buttons ?? throw new ArgumentNullException(nameof(buttons));
            Axes = axes ?? throw new ArgumentNullException(nameof(axes));
        }

        /// <summary>Ordered digital button names (e.g. "P1 Up", "P1 A").</summary>
        public IReadOnlyList<string> Buttons { get; }

        /// <summary>Ordered analog axes.</summary>
        public IReadOnlyList<AxisSpec> Axes { get; }

        /// <summary>Bytes a packed button bitfield occupies for this layout.</summary>
        public int ButtonByteWidth => (Buttons.Count + 7) / 8;

        /// <summary>Total serialized size of one port's input under this layout.</summary>
        public int PayloadByteWidth => ButtonByteWidth + Axes.Sum(a => a.ByteWidth);

        /// <summary>
        /// Stable content hash. Any difference in button/axis names, order, or axis ranges
        /// yields a different digest, so a layout mismatch is caught at handshake rather than
        /// surfacing later as a silent desync.
        /// </summary>
        public string Digest
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var b in Buttons) sb.Append('B').Append(b).Append('\n');
                foreach (var a in Axes)
                    sb.Append('A').Append(a.Name).Append(':')
                      .Append(a.Min.ToString(CultureInfo.InvariantCulture)).Append(':')
                      .Append(a.Max.ToString(CultureInfo.InvariantCulture)).Append(':')
                      .Append(a.Neutral.ToString(CultureInfo.InvariantCulture)).Append('\n');
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
            }
        }
    }
}
