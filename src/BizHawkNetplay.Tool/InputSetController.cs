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

    private readonly Dictionary<string, bool> _buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _axes = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<CoreLayout> _layouts;

    public InputSetController(ControllerDefinition definition, IReadOnlyList<CoreLayout> layouts)
    {
        Definition = definition;
        _layouts = layouts;
        // Seed every key the layouts can produce, so Update only ever overwrites values. A write to
        // an existing key neither rehashes nor grows the table.
        //
        // Neutral, not zero. An axis carries its own resting value and it is not always 0 — an
        // unsigned stick or trigger is typically 0..255 resting at 128, where 0 is a full-scale
        // deflection. Seeding zero held such an axis hard over until something wrote to it.
        foreach (var layout in layouts)
        {
            foreach (var button in layout.Buttons) _buttons[button] = false;
            foreach (var axis in layout.Axes) _axes[axis.Name] = axis.Neutral;
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
            // Same reason as the constructor: an absent port rests at each axis's own Neutral. On a
            // four-port core hosting two players this is every frame for the two empty seats, so
            // zero here meant a 0..255 axis sat at full deflection for the whole session — which is
            // exactly the "unused ports read neutral" promise the README makes.
            for (int j = 0; j < layout.Axes.Count; j++)
                _axes[layout.Axes[j].Name] = port != null ? port.Axes[j] : layout.Axes[j].Neutral;
        }
    }

    public bool IsPressed(string button) => _buttons.TryGetValue(button, out var v) && v;

    /// <summary>
    /// An axis the negotiated layouts never described — a core asking for something outside the
    /// control surface both peers agreed on. Answer with the core's own resting value rather than
    /// zero, which for an unsigned axis is a full-scale deflection. The lookup only runs on the
    /// miss path; every axis the layouts do describe was seeded in the constructor.
    /// </summary>
    public int AxisValue(string name) => _axes.TryGetValue(name, out var v)
        ? v
        : Definition.Axes.TryGetValue(name, out var spec) ? spec.Neutral : 0;

    public IReadOnlyCollection<(string Name, int Strength)> GetHapticsSnapshot() => NoHaptics;

    public void SetHapticChannelStrength(string name, int strength) { }
}
