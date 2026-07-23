using System;
using System.Collections.Generic;
using System.Linq;

namespace BizHawkNetplay.Core.Input
{
    /// <summary>
    /// The state of one controller port for one frame: button pressed-flags and axis values,
    /// indexed positionally against a <see cref="ControllerLayout"/>. Immutable.
    /// </summary>
    public sealed class PortInput
    {
        public PortInput(bool[] buttons, int[] axes)
        {
            Buttons = buttons ?? throw new ArgumentNullException(nameof(buttons));
            Axes = axes ?? throw new ArgumentNullException(nameof(axes));
        }

        public bool[] Buttons { get; }
        public int[] Axes { get; }

        /// <summary>A neutral (all-released, axes-at-neutral) input for the given layout.</summary>
        public static PortInput Neutral(ControllerLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            var buttons = new bool[layout.Buttons.Count];
            var axes = new int[layout.Axes.Count];
            for (int i = 0; i < axes.Length; i++) axes[i] = layout.Axes[i].Neutral;
            return new PortInput(buttons, axes);
        }

        public bool ValueEquals(PortInput other)
        {
            if (other == null) return false;
            if (Buttons.Length != other.Buttons.Length || Axes.Length != other.Axes.Length) return false;
            for (int i = 0; i < Buttons.Length; i++) if (Buttons[i] != other.Buttons[i]) return false;
            for (int i = 0; i < Axes.Length; i++) if (Axes[i] != other.Axes[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// Inputs for every port for a single simulation frame. What
    /// <c>IEmuAdapter.SetInputs</c> applies wholesale each frame — local ports carry the
    /// delayed synchronized value, remote ports the received value. Nothing else may reach
    /// the core.
    /// </summary>
    public sealed class InputSet
    {
        public InputSet(int frame, PortInput[] ports)
        {
            Frame = frame;
            Ports = ports ?? throw new ArgumentNullException(nameof(ports));
        }

        /// <summary>Simulation frame these inputs apply to.</summary>
        public int Frame { get; }

        /// <summary>One entry per controller port, in port order.</summary>
        public PortInput[] Ports { get; }

        public static InputSet AllNeutral(int frame, IReadOnlyList<ControllerLayout> portLayouts)
        {
            var ports = portLayouts.Select(PortInput.Neutral).ToArray();
            return new InputSet(frame, ports);
        }
    }
}
