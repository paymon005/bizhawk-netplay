using System;
using System.Collections.Generic;
using System.Linq;
using BizHawk.Client.Common;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Input;
using CoreLayout = BizHawkNetplay.Core.Input.ControllerLayout;
using CoreAxisSpec = BizHawkNetplay.Core.Input.AxisSpec;

namespace BizHawkNetplay.Tool;

/// <summary>
/// Reading the local pad, and the per-port tables that make reading it cheap.
///
/// The capture path itself is deliberately thin: it asks BizHawk what the pad resolves to rather
/// than re-deriving the bind arithmetic, because a copy of someone else's algorithm kept in step by
/// hand had already drifted once. What is left here is the bookkeeping that copy used to need —
/// bindings, layouts, key names — most of it precomputed, since <see cref="ReadLocalInput"/> runs
/// every frame and must not be doing string work.
/// </summary>
internal sealed partial class EmuHawkAdapter
{
    private InputSetController? _controller; // reused every frame; see Controller()
    private readonly string[][] _bindings; // [port][buttonIndex] -> host-input binding string
    private readonly AnalogBind?[][] _analogBinds; // [port][axisIndex] -> host analog binding (null if unbound)
    private readonly bool[][] _axisReversed;      // [port][axisIndex] -> core axis IsReversed flag
    // [assignedPort][sourcePort] — whether reading the source port's bindings for the assigned
    // port's layout maps control-for-control. See BuildRemapCompatibility.
    private readonly bool[][] _remapCompatible;
    // Layout control names with the "P<n> " prefix removed, which is the form Joypad.Get keys its
    // dictionary by. Precomputed: ReadLocalInput runs every frame and must not be doing string work.
    private readonly string[][] _padButtonKeys;
    private readonly string[][] _padAxisKeys;
    // The N64 stick gate: the stick is round, and the constraint clamps the (X,Y) VECTOR to radius
    // 127, without which a diagonal reaches ~180 — 42% past anything the hardware can produce, so a
    // game reading stick MAGNITUDE saturates its top bucket at about 70% deflection.
    //
    // Reported by the input test but no longer applied here: capture reads the end of EmuHawk's own
    // controller chain, and RunControllerChain applies it to ActiveController before the value can
    // reach Joypad.Get. Still worth showing, since with the setting off a diagonal really does
    // exceed what the hardware could send.
    private readonly bool _useCircularAnalogConstraint;

    /// <summary>True if we found the user's controller bindings (needed to capture input).</summary>
    public bool HasBindings
    {
        get
        {
            foreach (var port in _bindings)
                foreach (var b in port)
                    if (!string.IsNullOrEmpty(b)) return true;
            return false;
        }
    }

    /// <summary>
    /// Ports a session may actually use. This counted layout slots, including empty ones that
    /// serialize to zero bytes — see <see cref="UsablePorts"/> for what that cost a 3-player NES
    /// session. Every caller wants the usable figure: the player-count cap, the host's minimum
    /// check, and the per-port layout dump all break in the same way on a slot that carries no input.
    /// </summary>
    public int PortCount => UsablePorts(_layouts);

    /// <summary>Total layout slots the core declared, usable or not. Diagnostic only — reported at
    /// session start so a core that declares more ports than it populates is visible.</summary>
    public int DeclaredPortCount => _layouts.Length;

    public CoreLayout GetControllerLayout(int port) => _layouts[port];

    /// <summary>
    /// DIAGNOSTIC: when true, ReadLocalInput returns neutral and never touches the pad. Proved
    /// the no-input session holds sync (isolating netcode/core from input). Left as a toggle
    /// for future debugging; normal play reads the real pad.
    /// </summary>
    public static bool ForceNeutralInput = false;

    /// <summary>
    /// Which EmuHawk controller port's bindings to actually read the local pad from. -1 (default)
    /// reads the assigned port's own bindings. Set it to e.g. 0 so a player assigned P2/P3/P4 still
    /// uses their normal P1 controls with no rebinding — same-console ports share button order, so
    /// the source bindings map onto the assigned port's layout by index.
    /// </summary>
    public int InputSourcePort { get; set; } = -1;

    /// <summary>
    /// The controller <c>Joypad.Get</c> reads: the end of EmuHawk's own controller chain, before the
    /// movie layer. Read fresh each time — <c>MovieIn</c> is a settable property and gets re-pointed
    /// when a movie starts — and null whenever the reference is missing, which sends
    /// <see cref="ReadLocalInput"/> back down the dictionary path unchanged.
    /// </summary>
    private IController? MovieInController
    {
        get
        {
            try { return (_movieSession ?? _mainForm?.MovieSession)?.MovieIn; }
            catch { return null; }
        }
    }

    private readonly IMovieSession? _movieSession;

    private ControllerDefinition? _movieInDefinition;
    private bool[]? _movieInCoversPort;

    /// <summary>
    /// Whether that controller actually declares every control in a port's layout.
    ///
    /// <c>ToDictionary</c> builds from the CONTROLLER's own definition and silently omits anything
    /// it does not have, which the dictionary path below then read as neutral. Asking the controller
    /// directly has no such omission step, and what an <see cref="IController"/> does with a name it
    /// does not know is its own business. So the equivalence is checked rather than assumed — once
    /// per definition, since a mid-session change means a core reboot — and a mismatch falls back
    /// to the dictionary instead of guessing.
    /// </summary>
    private bool MovieInCovers(IController controller, int readPort)
    {
        var definition = controller.Definition;
        if (definition == null) return false;
        if (!ReferenceEquals(definition, _movieInDefinition))
        {
            _movieInDefinition = definition;
            var covers = new bool[_layouts.Length];
            for (int p = 0; p < _layouts.Length; p++)
            {
                bool ok = true;
                foreach (var button in _layouts[p].Buttons)
                    if (!definition.BoolButtons.Contains(button)) { ok = false; break; }
                if (ok)
                    foreach (var axis in _layouts[p].Axes)
                        if (!definition.Axes.ContainsKey(axis.Name)) { ok = false; break; }
                covers[p] = ok;
            }
            _movieInCoversPort = covers;
        }
        return _movieInCoversPort != null
            && readPort >= 0 && readPort < _movieInCoversPort.Length
            && _movieInCoversPort[readPort];
    }

    public PortInput ReadLocalInput(int port)
    {
        var layout = _layouts[port];
        if (ForceNeutralInput)
            return PortInput.Neutral(layout);

        // Ask BizHawk what this pad resolves to, rather than working it out again ourselves.
        //
        // Joypad.Get returns MovieIn, which is the END of EmuHawk's own controller chain:
        // ActiveController.LatchFromPhysical (bindings, deadzone, multiplier, axis reversal) OR
        // AutoFire, through the U+D/L+R policy, with the N64 circular constraint applied, XOR the
        // sticky hold/autofire controllers — and then, deliberately, BEFORE the movie layer, which
        // is the difference between Get and GetWithMovie. In other words exactly what local play
        // feeds the core.
        //
        // This used to be a reimplementation: read the raw host coalescers and re-derive the bind
        // math ourselves. That is a copy of someone else's algorithm, kept in step by hand, and
        // it had already drifted — it evaluated button binds against HostInputCoalescer while
        // BizHawk evaluates them against ControllerInputCoalescer, which are different objects.
        // Since the digital branch of an analog bind wins over the stick whenever it fires, and
        // yields exactly +/-1, a disagreement there reads as "the stick only gives min or max".
        //
        // Determinism is unaffected: whatever this resolves to is what we send, so peers replay the
        // value rather than the inputs behind it. Autohold and autofire simply become part of it.
        int src = InputSourcePort;
        bool remap = src >= 0 && src < _layouts.Length && _remapCompatible[port][src];
        int readPort = remap ? src : port;

        var buttons = new bool[layout.Buttons.Count];
        var axes = new int[layout.Axes.Count];

        // Ask that same controller for the values, instead of asking it to build a dictionary of
        // them. JoypadApi.Get(n) is exactly `_movieSession.MovieIn.ToDictionary(n)`, and
        // ToDictionary(n) is IsPressed/AxisValue over every control whose name carries the "P<n> "
        // prefix, re-keyed by the tail. So this reads the same values off the same object; it is
        // the dictionary that is optional, not the resolution. Nothing here re-derives bind
        // arithmetic — that is the mistake the comment above is about, and this is not it.
        //
        // What the dictionary cost was a fresh Dictionary, a boxed value per control and a
        // Substring per control name, every frame — roughly two thousand objects a second on an
        // N64 pad, which is the largest per-frame allocation source left in the session.
        //
        // Remap-compatible layouts line up control-for-control (see LayoutsLineUp), so control i of
        // `port` is control i of `readPort`, under `readPort`'s own full name.
        var direct = MovieInController;
        if (direct != null && MovieInCovers(direct, readPort))
        {
            var readLayout = _layouts[readPort];
            for (int i = 0; i < buttons.Length; i++) buttons[i] = direct.IsPressed(readLayout.Buttons[i]);
            for (int j = 0; j < axes.Length; j++) axes[j] = direct.AxisValue(readLayout.Axes[j].Name);
            return new PortInput(buttons, axes);
        }

        IReadOnlyDictionary<string, object>? pad;
        try { pad = _apis.Joypad.Get(readPort + 1); } // Joypad ports are 1-based
        catch { pad = null; }

        // Keys come back with the "P<n> " prefix stripped, which is why the remap works at all: two
        // ports that describe the same controls produce the same keys. Anything missing rests at
        // that axis's own Neutral rather than 0, which on an unsigned axis is a full deflection.
        var buttonKeys = _padButtonKeys[port];
        var axisKeys = _padAxisKeys[port];
        for (int i = 0; i < buttons.Length; i++)
            buttons[i] = pad != null && pad.TryGetValue(buttonKeys[i], out var b) && b is bool on && on;
        for (int j = 0; j < axes.Length; j++)
            axes[j] = pad != null && pad.TryGetValue(axisKeys[j], out var a) && a is int v
                ? v
                : layout.Axes[j].Neutral;
        return new PortInput(buttons, axes);
    }

    // The real session injects inputs by stepping the core with a controller built from the
    // merged InputSet (AdvanceFrame), not via Joypad.Set — so this is unused.
    /// <summary>
    /// Deliberately empty. A BizHawk core reads its input from the <c>IController</c> handed INTO
    /// <c>FrameAdvance</c>, so there is no "set them now, step later" state to hold — the merged
    /// set travels with the frame in <see cref="AdvanceFrame"/>. See <see cref="IEmuAdapter.SetInputs"/>.
    /// </summary>
    public void SetInputs(InputSet inputs) { }

    /// <summary>
    /// The one controller this adapter presents to the core, refilled for <paramref name="inputs"/>.
    /// Rebuilt only when the core's controller definition or our layouts change identity — which is
    /// what a core reboot does, and the one case where a cached name map would be wrong.
    /// </summary>
    private InputSetController Controller(InputSet inputs)
    {
        var definition = _emulator.ControllerDefinition;
        if (_controller == null || !_controller.Matches(definition, _layouts))
            _controller = new InputSetController(definition, _layouts);
        _controller.Update(inputs);
        return _controller;
    }

    /// <summary>
    /// True if the host-input expression <paramref name="binding"/> is satisfied by the pressed
    /// set. Handles comma-separated alternatives and '+'-separated combos (the common BizHawk
    /// forms); exotic modifiers may not resolve (M2).
    /// </summary>
    private static bool EvaluateBinding(string binding, HashSet<string> pressed)
    {
        if (string.IsNullOrEmpty(binding)) return false;
        foreach (var alt in binding.Split(','))
        {
            var one = alt.Trim();
            if (one.Length == 0) continue;
            bool all = true;
            foreach (var part in one.Split('+'))
            {
                var key = part.Trim();
                if (key.Length == 0) continue;
                if (!pressed.Contains(key)) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    /// <summary>Human-readable note on why bindings were/weren't found (for the UI log).</summary>
    public string BindingDiagnostic { get; private set; } = "";

    private string[][] BuildBindings()
    {
        Dictionary<string, string>? map = null;
        try
        {
            var config = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference;
            if (config != null)
            {
                // Config.AllTrollers is keyed by the controller-definition name, not the system id.
                var defName = _emulator.ControllerDefinition.Name;
                if (!config.AllTrollers.TryGetValue(defName, out map))
                    config.AllTrollers.TryGetValue(_emulator.SystemId, out map);
                if (map == null)
                    BindingDiagnostic = $"no bindings for '{defName}' or '{_emulator.SystemId}'. " +
                        $"available: [{string.Join(", ", config.AllTrollers.Keys)}]";
            }
            else BindingDiagnostic = "config reference unavailable";
        }
        catch (Exception ex) { BindingDiagnostic = "binding lookup error: " + ex.Message; }

        var result = new string[_layouts.Length][];
        for (int p = 0; p < _layouts.Length; p++)
        {
            var layout = _layouts[p];
            var arr = new string[layout.Buttons.Count];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = (map != null && map.TryGetValue(layout.Buttons[i], out var b)) ? b : "";
            result[p] = arr;
        }
        return result;
    }

    /// <summary>Per-axis host analog bindings (host axis + mult/deadzone/button binds), from the user's
    /// EmuHawk analog config. Null entries are unbound axes (captured as neutral).</summary>
    private AnalogBind?[][] BuildAnalogBinds()
    {
        Dictionary<string, AnalogBind>? map = null;
        try
        {
            var config = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference;
            config?.AllTrollersAnalog.TryGetValue(_emulator.ControllerDefinition.Name, out map);
        }
        catch { /* leave unbound -> neutral axes */ }

        var result = new AnalogBind?[_layouts.Length][];
        for (int p = 0; p < _layouts.Length; p++)
        {
            var axes = _layouts[p].Axes;
            var arr = new AnalogBind?[axes.Count];
            for (int j = 0; j < arr.Length; j++)
                arr[j] = (map != null && map.TryGetValue(axes[j].Name, out var b)) ? b : (AnalogBind?)null;
            result[p] = arr;
        }
        return result;
    }

    /// <summary>
    /// EmuHawk's <c>N64UseCircularAnalogConstraint</c>, the setting that decides whether the stick
    /// gate is applied at all. Read by name off the same config object the analog binds come from,
    /// because it is not on any interface we are handed. Defaults to TRUE when it cannot be read:
    /// that is both EmuHawk's own default and the safer direction, since applying the gate can only
    /// bring a value back inside what the hardware could produce.
    /// </summary>
    private bool ReadCircularAnalogConstraintSetting()
    {
        // A plain public property on a type this project references directly — the reflection this
        // used to do was left over from before Config was in reach at construction time.
        try { return _hostConfig?.N64UseCircularAnalogConstraint ?? true; }
        catch { return true; }
    }

    /// <summary>
    /// Which ports' bindings may stand in for which other ports', for the "My controls" remap that
    /// lets a player assigned P3 keep using their P1 pad.
    ///
    /// The old test was that the two ports had the same NUMBER of buttons, while the comment beside
    /// it claimed "the same button set". Those are not the same test, and on a core whose ports are
    /// not identical — a light gun against a pad, a multitap seat with fewer controls — two ports
    /// can match on count and mean entirely different things, silently mapping A to Trigger.
    ///
    /// Compare the control NAMES instead, position for position, with the "P&lt;n&gt; " prefix removed:
    /// that prefix is exactly what BizHawk grouped the ports by in the first place, so what is left
    /// is the port-relative control identity.
    ///
    /// Built once. <see cref="ReadLocalInput"/> runs every frame and must not be doing string work.
    /// </summary>
    private bool[][] BuildRemapCompatibility()
    {
        var m = new bool[_layouts.Length][];
        for (int a = 0; a < _layouts.Length; a++)
        {
            m[a] = new bool[_layouts.Length];
            for (int b = 0; b < _layouts.Length; b++) m[a][b] = LayoutsLineUp(_layouts[a], _layouts[b]);
        }
        return m;
    }

    private static bool LayoutsLineUp(CoreLayout a, CoreLayout b)
    {
        if (a.Buttons.Count != b.Buttons.Count || a.Axes.Count != b.Axes.Count) return false;
        for (int i = 0; i < a.Buttons.Count; i++)
            if (!string.Equals(StripPortPrefix(a.Buttons[i]), StripPortPrefix(b.Buttons[i]), StringComparison.Ordinal))
                return false;
        for (int i = 0; i < a.Axes.Count; i++)
            if (!string.Equals(StripPortPrefix(a.Axes[i].Name), StripPortPrefix(b.Axes[i].Name), StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>"P2 X Axis" -> "X Axis". Mirrors ControllerDefinition's own <c>^P(\d+) </c>, which is
    /// how a control got assigned to a port to begin with.</summary>
    private static string StripPortPrefix(string control)
    {
        if (control.Length < 3 || control[0] != 'P') return control;
        int d = 1;
        while (d < control.Length && control[d] >= '0' && control[d] <= '9') d++;
        return d > 1 && d < control.Length && control[d] == ' ' ? control.Substring(d + 1) : control;
    }

    /// <summary>The core's IsReversed flag per axis (BizHawk applies it when mapping host -> core).</summary>
    private bool[][] BuildAxisReversed()
    {
        var def = _emulator.ControllerDefinition;
        var result = new bool[_layouts.Length][];
        for (int p = 0; p < _layouts.Length; p++)
        {
            var axes = _layouts[p].Axes;
            var arr = new bool[axes.Count];
            for (int j = 0; j < arr.Length; j++) // names come from def.Axes, so the indexer is safe
                arr[j] = def.Axes[axes[j].Name].IsReversed;
            result[p] = arr;
        }
        return result;
    }

    // --- Layout derivation --------------------------------------------------------

    /// <summary>
    /// How many controller ports the loaded core exposes, without building a whole adapter (no
    /// binding/config lookups). The UI needs this before a session to cap the player count — a core
    /// only exposes more than its default ports once a multitap/adapter is enabled in its settings
    /// (Genesis 4-Way Play / Team Player, SNES multitap), which reboots the core and re-reads this.
    /// </summary>
    public static int PortCountOf(IEmulator emulator)
    {
        if (emulator == null) throw new ArgumentNullException(nameof(emulator));
        return UsablePorts(BuildLayouts(emulator.ControllerDefinition));
    }

    /// <summary>
    /// Leading run of ports that can actually carry input — a layout with no buttons and no axes
    /// serializes to zero bytes and is a slot, not a controller.
    ///
    /// <see cref="BuildLayouts"/> allocates one slot per player number it finds anywhere in the
    /// core's flat control list, and those slots are not guaranteed to be populated. QuickNES
    /// reports three, of which the third is empty — so a 3-player session passed the player-count
    /// cap, and then every packet for port 2 was refused on arrival because the receiving side had
    /// nothing to deserialize it into. Both ends agreed there was no input to exchange, no frame
    /// ever advanced, and the session died at frame 0 blaming the UDP path.
    ///
    /// The run is counted from port 0 and stops at the first empty slot, because ports are assigned
    /// contiguously: a usable port sitting behind an unusable one is unreachable anyway.
    /// </summary>
    private static int UsablePorts(CoreLayout[] layouts)
    {
        int usable = 0;
        while (usable < layouts.Length && layouts[usable].PayloadByteWidth > 0) usable++;
        return usable;
    }

    private static CoreLayout[] BuildLayouts(ControllerDefinition def)
    {
        // Group the core's flat button/axis lists by player number into per-port layouts.
        int maxPlayer = 0;
        foreach (var b in def.BoolButtons)
            maxPlayer = Math.Max(maxPlayer, SafePlayerNumber(def, b));
        foreach (var axisName in def.Axes.Keys)
            maxPlayer = Math.Max(maxPlayer, SafePlayerNumber(def, axisName));
        if (maxPlayer < 1) maxPlayer = 1; // system-only cores still expose one nominal port

        var layouts = new CoreLayout[maxPlayer];
        for (int player = 1; player <= maxPlayer; player++)
        {
            var buttons = def.BoolButtons
                .Where(b => SafePlayerNumber(def, b) == player)
                .ToList();
            var axes = def.Axes.Keys
                .Where(a => SafePlayerNumber(def, a) == player)
                .Select(a =>
                {
                    var spec = def.Axes[a];
                    return new CoreAxisSpec(a, spec.Min, spec.Max, spec.Neutral);
                })
                .ToList();
            layouts[player - 1] = new CoreLayout(buttons, axes);
        }
        return layouts;
    }

    private static int SafePlayerNumber(ControllerDefinition def, string control)
    {
        // PlayerNumber is static — it derives the player from the control-name prefix.
        try { return ControllerDefinition.PlayerNumber(control); }
        catch { return 0; }
    }
}
