using System;
using System.Collections.Generic;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Input;
using CoreLayout = BizHawkNetplay.Core.Input.ControllerLayout;

namespace BizHawkNetplay.Tool;

/// <summary>
/// Presents a Core <see cref="InputSet"/> to BizHawk as an <see cref="IController"/> for
/// <c>IEmulator.FrameAdvance</c>. Flattens per-port positional inputs back to the core's
/// global button/axis names using the negotiated layouts.
///
/// Built once per adapter and refilled per frame rather than constructed per frame. The name
/// mapping never changes while a core is loaded — only the values do — so a fresh instance meant
/// two dictionaries and a full rehash of every button name for every frame the core stepped,
/// including each re-simulated frame of a rollback repair. Those are small, gen-0 allocations; the
/// problem is that they scale with player count and with repair depth, and sustained churn is what
/// promotes objects toward the gen-2 collection that actually hitches.
/// </summary>
internal sealed class InputSetController : IController
{
    private static readonly IReadOnlyCollection<(string, int)> NoHaptics = Array.Empty<(string, int)>();

    private readonly Dictionary<string, bool> _buttons = new Dictionary<string, bool>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _axes = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly IReadOnlyList<CoreLayout> _layouts;

    public InputSetController(ControllerDefinition definition, IReadOnlyList<CoreLayout> layouts)
    {
        Definition = definition;
        _layouts = layouts;
        // Seed every key the layouts can produce, so Update only ever overwrites values. A write to
        // an existing key neither rehashes nor grows the table.
        foreach (var layout in layouts)
        {
            foreach (var button in layout.Buttons) _buttons[button] = false;
            foreach (var axis in layout.Axes) _axes[axis.Name] = 0;
        }
    }

    public ControllerDefinition Definition { get; }

    /// <summary>True if this instance still describes the core in front of it. A reboot rebuilds
    /// the controller definition (peripherals, port count), and a stale name map would silently
    /// feed the new core the old core's buttons.</summary>
    public bool Matches(ControllerDefinition definition, IReadOnlyList<CoreLayout> layouts) =>
        ReferenceEquals(Definition, definition) && ReferenceEquals(_layouts, layouts);

    /// <summary>
    /// Point this controller at one frame's merged inputs. Every port the layouts describe is
    /// written, not just the ones this <see cref="InputSet"/> covers: a reused instance would
    /// otherwise keep presenting the previous frame's buttons for a port that has dropped out of
    /// the set, where a freshly constructed one reported neutral.
    /// </summary>
    public void Update(InputSet inputs)
    {
        for (int p = 0; p < _layouts.Count; p++)
        {
            var layout = _layouts[p];
            var port = p < inputs.Ports.Length ? inputs.Ports[p] : null;
            for (int i = 0; i < layout.Buttons.Count; i++)
                _buttons[layout.Buttons[i]] = port != null && port.Buttons[i];
            for (int j = 0; j < layout.Axes.Count; j++)
                _axes[layout.Axes[j].Name] = port != null ? port.Axes[j] : 0;
        }
    }

    public bool IsPressed(string button) => _buttons.TryGetValue(button, out var v) && v;

    public int AxisValue(string name) => _axes.TryGetValue(name, out var v) ? v : 0;

    public IReadOnlyCollection<(string Name, int Strength)> GetHapticsSnapshot() => NoHaptics;

    public void SetHapticChannelStrength(string name, int strength) { }
}
