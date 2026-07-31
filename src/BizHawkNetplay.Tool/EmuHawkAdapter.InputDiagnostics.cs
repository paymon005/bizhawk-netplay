using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BizHawk.Client.Common;

namespace BizHawkNetplay.Tool;

/// <summary>
/// The input test and its analog half — what the pad is bound to, what the host reports right now,
/// and what BizHawk resolves that to.
///
/// Kept apart from the capture path deliberately. None of this runs during a session; it exists
/// because "the stick only ever gives min or max" stayed a matter of opinion for as long as every
/// stage that could explain it was invisible. Its job is to make the candidate explanations
/// separate on sight rather than by argument, which means it is allowed to be verbose in a way the
/// per-frame path never is.
/// </summary>
internal sealed partial class EmuHawkAdapter
{
    /// <summary>
    /// One-shot input diagnostic for the UI: what controller/bindings we found, and what host
    /// inputs are pressed right now vs how they resolve to P1 buttons.
    /// </summary>
    public string DescribeInputState()
    {
        var sb = new StringBuilder();
        sb.Append("controllerDef='").Append(_emulator.ControllerDefinition.Name)
          .Append("', system='").Append(_emulator.SystemId).Append("'\n");
        sb.Append("bindings: ").Append(HasBindings ? "FOUND" : "MISSING");
        if (!string.IsNullOrEmpty(BindingDiagnostic)) sb.Append("  ").Append(BindingDiagnostic);
        sb.Append('\n');

        if (_layouts.Length > 0)
        {
            var l0 = _layouts[0];
            for (int i = 0; i < l0.Buttons.Count && i < 8; i++)
                sb.Append("  ").Append(l0.Buttons[i]).Append(" <- '").Append(_bindings[0][i]).Append("'\n");
        }

        IReadOnlyList<string> pressed;
        try { pressed = _apis.Input.GetPressedButtons(); }
        catch (Exception ex) { return sb.Append("GetPressedButtons error: ").Append(ex.Message).ToString(); }
        sb.Append("pressed host inputs now: [").Append(string.Join(", ", pressed)).Append("]\n");

        if (_layouts.Length > 0)
        {
            var set = new HashSet<string>(pressed);
            var l0 = _layouts[0];
            var on = new List<string>();
            for (int i = 0; i < l0.Buttons.Count; i++)
                if (EvaluateBinding(_bindings[0][i], set)) on.Add(l0.Buttons[i]);
            sb.Append("resolved P1 pressed: [").Append(string.Join(", ", on)).Append("]");
        }
        AppendAnalogState(sb);
        return sb.ToString();
    }

    /// <summary>
    /// The analog half of the input test. Buttons were the only thing this reported, which is why
    /// "the N64 stick only gives min or max, never medium" stayed a matter of opinion: every stage
    /// that could explain it was invisible.
    ///
    /// It prints the whole chain per axis — the bind, the live host reading, and the core value
    /// BizHawk's controller chain resolves them to — so the three candidate explanations separate
    /// on sight rather than by argument:
    ///
    /// * <c>host=</c> absent, or the raw dictionary empty, means the pad's axes are not reaching
    ///   <c>GetPressedAxes</c> at all. Then analog is dead and the only thing that can still move
    ///   the axis is a button bind, which is worth stating because of the next point.
    /// * <c>BUTTON BIND ACTIVE</c> means the value came from the digital branch, which by
    ///   construction yields exactly ±1 and therefore exactly Min or Max. That branch producing
    ///   every non-neutral reading IS the reported symptom, so it is called out rather than left to
    ///   be inferred from a number that looks plausible.
    /// * a smoothly varying <c>host=</c> with a smoothly varying result means capture is fine and
    ///   the loss is downstream — sampling cadence, not resolution.
    ///
    /// Read it while holding the stick at roughly half deflection; that is the case in dispute.
    /// </summary>
    private void AppendAnalogState(StringBuilder sb)
    {
        if (_layouts.Length == 0 || _layouts[0].Axes.Count == 0)
        {
            sb.Append("\nanalog: this core exposes no axes.");
            return;
        }

        IReadOnlyDictionary<string, int>? hostAxes;
        try { hostAxes = _apis.Input.GetPressedAxes(); }
        catch (Exception ex)
        {
            sb.Append("\nanalog: GetPressedAxes threw: ").Append(ex.Message);
            return;
        }

        var layout = _layouts[0];
        sb.Append("\nanalog: ").Append(layout.Axes.Count).Append(" axis/axes on P1, circular constraint ")
          .Append(_useCircularAnalogConstraint ? "ON" : "OFF")
          .Append(", GetPressedAxes reported ").Append(hostAxes == null ? 0 : hostAxes.Count)
          .Append(" host axis/axes\n");

        // The raw dictionary, verbatim. If the stick is missing from here, nothing downstream can
        // recover it, and no amount of staring at the resolved value would say so.
        if (hostAxes != null && hostAxes.Count > 0)
        {
            sb.Append("  host axes now: [");
            bool first = true;
            foreach (var kv in hostAxes)
            {
                if (!first) sb.Append(", ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
                first = false;
            }
            sb.Append("]\n");
        }

        string? stickOverride = DigitalStickOverrideDiagnostic();
        if (stickOverride != null) sb.Append("  !! ").Append(stickOverride).Append('\n');

        var pressed = new HashSet<string>(_apis.Input.GetPressedButtons());
        IReadOnlyDictionary<string, object>? padAxes;
        try { padAxes = _apis.Joypad.Get(1); }
        catch { padAxes = null; }
        for (int j = 0; j < layout.Axes.Count; j++)
        {
            var spec = layout.Axes[j];
            var bindN = _analogBinds[0][j];
            sb.Append("  '").Append(spec.Name).Append("' [").Append(spec.Min).Append("..").Append(spec.Max)
              .Append(" neutral=").Append(spec.Neutral).Append(']');
            if (_axisReversed[0][j]) sb.Append(" reversed");

            if (bindN == null) { sb.Append("  <- UNBOUND (always neutral)\n"); continue; }
            var bind = bindN.Value;

            sb.Append("\n      bind '").Append(bind.Value).Append("' mult=")
              .Append(bind.Mult.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" deadzone=").Append(bind.Deadzone.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" btn+='").Append(bind.ButtonBindPositive).Append("' btn-='")
              .Append(bind.ButtonBindNegative).Append("'\n");

            bool plus = !string.IsNullOrEmpty(bind.ButtonBindPositive)
                        && EvaluateBinding(bind.ButtonBindPositive, pressed);
            bool minus = !string.IsNullOrEmpty(bind.ButtonBindNegative)
                         && EvaluateBinding(bind.ButtonBindNegative, pressed);
            int raw = 0;
            bool haveRaw = hostAxes != null && !string.IsNullOrEmpty(bind.Value)
                           && hostAxes.TryGetValue(bind.Value, out raw);
            sb.Append("      host=").Append(haveRaw ? raw.ToString(CultureInfo.InvariantCulture) : "ABSENT")
              .Append(haveRaw ? $" ({raw / 10000f:0.000} of full)" : " (bind name not in the dictionary above)")
              .Append('\n');

            // What is ACTUALLY sent: BizHawk's own resolved value, the end of its controller chain.
            int fromPad = padAxes != null && padAxes.TryGetValue(_padAxisKeys[0][j], out var pv) && pv is int pi
                ? pi
                : int.MinValue;
            sb.Append("      BizHawk resolves it to ")
              .Append(fromPad == int.MinValue ? "ABSENT" : fromPad.ToString(CultureInfo.InvariantCulture))
              .Append("   <- this is what gets sent\n");

            // Capture no longer asks for that dictionary; it reads the same controller directly,
            // which is the same call ToDictionary would have made on the way to building it. The two
            // are printed together so the claim is checkable against real hardware and a real pad,
            // rather than only against BizHawk's source.
            var direct = MovieInController;
            int fromDirect = direct != null && MovieInCovers(direct, 0)
                ? direct.AxisValue(spec.Name)
                : int.MinValue;
            if (fromDirect != fromPad)
                sb.Append("      !! the capture path reads ")
                  .Append(fromDirect == int.MinValue
                      ? "NOTHING here, so it is falling back to the dictionary"
                      : fromDirect.ToString(CultureInfo.InvariantCulture))
                  .Append(" — this must match the line above.\n");

            // The digital branch wins over the stick whenever it fires, exactly as BizHawk's
            // LatchFromPhysical does — and it can only ever produce full deflection.
            if (plus ^ minus)
                sb.Append("      !! BUTTON BIND ACTIVE (")
                  .Append(plus ? "positive" : "negative")
                  .Append(") — the stick is being ignored this instant and the value is forced to ")
                  .Append(plus ? "Max" : "Min").Append(", never anything between.\n");
            else if (!haveRaw)
                sb.Append("      !! no host reading for this axis — it can only ever read neutral, " +
                         "or full deflection if a button bind fires.\n");
        }
    }

    /// <summary>
    /// The N64's digital stick directions, when they are bound at the same time as the analog stick.
    ///
    /// The core resolves the two by letting the BUTTON win outright — N64Input.GetStickValues reads
    /// "if A Left pressed, x = -128; else if A Right pressed, x = +127; ELSE use the X Axis". So a
    /// binding like "P1 A Left &lt;- X1 LStickLeft", which is what BizHawk's own default XInput layout
    /// gives you, means pushing the stick past the digital threshold slams the axis to full
    /// deflection and throws the analog reading away. Below the threshold the deadzone eats what is
    /// left, so the stick reads minimum or maximum and nothing in between.
    ///
    /// Nothing in netplay causes it and nothing in netplay can fix it — local play behaves the same
    /// — but it presents as "the netplay stick is broken", so the session says it plainly. Returns
    /// null when there is nothing to warn about.
    /// </summary>
    public string? DigitalStickOverrideDiagnostic()
    {
        try
        {
            if (!string.Equals(_emulator.SystemId, "N64", StringComparison.OrdinalIgnoreCase)) return null;
            if (_layouts.Length == 0) return null;

            var bound = new List<string>();
            for (int i = 0; i < _layouts[0].Buttons.Count; i++)
            {
                string name = _layouts[0].Buttons[i];
                if (!name.EndsWith(" A Up", StringComparison.Ordinal)
                    && !name.EndsWith(" A Down", StringComparison.Ordinal)
                    && !name.EndsWith(" A Left", StringComparison.Ordinal)
                    && !name.EndsWith(" A Right", StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(_bindings[0][i])) bound.Add(StripPortPrefix(name));
            }
            if (bound.Count == 0) return null;

            // Only a problem alongside a bound analog stick; on a keyboard-only setup the digital
            // directions are the whole point.
            bool analogBound = false;
            for (int j = 0; j < _analogBinds[0].Length; j++)
                if (_analogBinds[0][j] is { } b && !string.IsNullOrEmpty(b.Value)) analogBound = true;
            if (!analogBound) return null;

            return $"P1 has both the analog stick and the digital stick directions bound " +
                   $"({string.Join(", ", bound)}). Worth knowing, but not necessarily a problem: the " +
                   "N64 core lets a digital direction win outright when it fires, forcing the stick to " +
                   "full deflection and discarding the analog value. Whether it fires at all depends " +
                   "on what those four are bound TO. Use Diagnostics > Watch Analog to check — if the " +
                   "stick covers its range smoothly there, this is not affecting you; if the values " +
                   "jump from small ones straight to the extreme, clear these four in " +
                   "Config > Controllers. Either way it is a BizHawk controller-config matter rather " +
                   "than a netplay one, and local play behaves the same.";
        }
        catch { return null; }
    }

    /// <summary>
    /// One instantaneous reading per P1 axis: the raw host value behind the bind, and the value
    /// BizHawk resolves it to (which is what a session sends).
    ///
    /// Exists because the input test could not answer the question it was built for. It samples when
    /// you click it, and you cannot hold a stick at half deflection and click a button on another
    /// window at the same time — so every dump it produced was the stick's resting position, to the
    /// digit. Sampling over seconds is the only way to see the travel.
    /// </summary>
    public (string Name, int Raw, int Resolved)[] SampleAnalog()
    {
        if (_layouts.Length == 0 || _layouts[0].Axes.Count == 0) return [];
        var layout = _layouts[0];

        IReadOnlyDictionary<string, int>? hostAxes;
        try { hostAxes = _apis.Input.GetPressedAxes(); } catch { hostAxes = null; }
        IReadOnlyDictionary<string, object>? pad;
        try { pad = _apis.Joypad.Get(1); } catch { pad = null; }

        var result = new (string, int, int)[layout.Axes.Count];
        for (int j = 0; j < layout.Axes.Count; j++)
        {
            var spec = layout.Axes[j];
            int raw = 0;
            if (_analogBinds[0][j] is { } bind && hostAxes != null && !string.IsNullOrEmpty(bind.Value))
                hostAxes.TryGetValue(bind.Value, out raw);
            int resolved = pad != null && pad.TryGetValue(_padAxisKeys[0][j], out var v) && v is int n
                ? n
                : spec.Neutral;
            result[j] = (spec.Name, raw, resolved);
        }
        return result;
    }
}
