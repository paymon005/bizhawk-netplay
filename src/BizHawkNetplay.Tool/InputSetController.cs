using System;
using System.Collections.Generic;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Input;
using CoreLayout = BizHawkNetplay.Core.Input.ControllerLayout;

namespace BizHawkNetplay.Tool
{
    /// <summary>
    /// Presents a Core <see cref="InputSet"/> to BizHawk as an <see cref="IController"/> for
    /// <c>IEmulator.FrameAdvance</c>. Flattens per-port positional inputs back to the core's
    /// global button/axis names using the negotiated layouts.
    /// </summary>
    internal sealed class InputSetController : IController
    {
        private static readonly IReadOnlyCollection<(string, int)> NoHaptics = Array.Empty<(string, int)>();

        private readonly Dictionary<string, bool> _buttons = new Dictionary<string, bool>();
        private readonly Dictionary<string, int> _axes = new Dictionary<string, int>();

        public InputSetController(ControllerDefinition definition, IReadOnlyList<CoreLayout> layouts, InputSet inputs)
        {
            Definition = definition;
            for (int p = 0; p < layouts.Count && p < inputs.Ports.Length; p++)
            {
                var layout = layouts[p];
                var port = inputs.Ports[p];
                for (int i = 0; i < layout.Buttons.Count; i++)
                    _buttons[layout.Buttons[i]] = port.Buttons[i];
                for (int j = 0; j < layout.Axes.Count; j++)
                    _axes[layout.Axes[j].Name] = port.Axes[j];
            }
        }

        public ControllerDefinition Definition { get; }

        public bool IsPressed(string button) => _buttons.TryGetValue(button, out var v) && v;

        public int AxisValue(string name) => _axes.TryGetValue(name, out var v) ? v : 0;

        public IReadOnlyCollection<(string Name, int Strength)> GetHapticsSnapshot() => NoHaptics;

        public void SetHapticChannelStrength(string name, int strength) { }
    }
}
