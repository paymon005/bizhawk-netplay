using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Input;

/// <summary>
/// Folds every seat's input onto controller 1, for games where the players take turns on the same
/// joystick rather than holding one each.
///
/// The tool otherwise gives seat N controller N, which is right for a two-player-simultaneous game
/// and wrong for an alternating one. Atari 7800 Robotron 2084 is the case that forced this: it reads
/// controller 1 as the movement stick and controller 2 as the fire-direction stick — for BOTH
/// players, since two-player is alternating and Atari's manual says "Player 2 uses the left
/// controller for moving". A netplay seat 2 mapped onto controller 2 is therefore holding the aim
/// stick: the character turns and shoots but never walks. Nothing in the layouts, the bindings or
/// the wire is wrong; the seat is simply plugged into the port the game reserves for something else.
///
/// So this is a session AGREEMENT, not a local preference. Every peer folds or none does — a peer
/// that skipped it would feed its core different input on the same frame, which is a desync with
/// nothing in the message shape to notice it. The flag rides WELCOME and the protocol version keeps
/// a peer that predates it out of the session entirely.
///
/// Applied at INJECTION only, downstream of everything that stores or compares per-seat input. The
/// pipeline, the retransmit ring, the mesh authorship and rollback's applied-versus-confirmed
/// comparison all keep seeing raw seat values, which is what lets them go on working unchanged.
/// Since the fold is a pure function of the unfolded set, two folded frames can only differ if the
/// raw frames did — a needed rollback correction can never be missed by folding late.
/// </summary>
public sealed class SharedControlFold
{
    private readonly IReadOnlyList<ControllerLayout> _layouts;
    private readonly IReadOnlyList<int> _playerButtonCount;
    private readonly IReadOnlyList<int> _playerAxisCount;
    private readonly IReadOnlyList<bool> _foldable;

    // Reused every frame. This runs once per live frame AND once per re-simulated frame of every
    // rollback repair — the same reason InputSetController refills one controller instead of
    // building a fresh one. The sole consumer copies the scalars straight out and retains nothing,
    // so handing back the same buffers is safe.
    private readonly bool[] _buttons;
    private readonly int[] _axes;
    private readonly PortInput _merged;
    private readonly PortInput[] _neutral;
    private PortInput[] _ports = Array.Empty<PortInput>();

    public SharedControlFold(
        IReadOnlyList<ControllerLayout> layouts,
        IReadOnlyList<int> playerButtonCount,
        IReadOnlyList<int> playerAxisCount,
        IReadOnlyList<bool> foldable)
    {
        _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
        _playerButtonCount = playerButtonCount ?? throw new ArgumentNullException(nameof(playerButtonCount));
        _playerAxisCount = playerAxisCount ?? throw new ArgumentNullException(nameof(playerAxisCount));
        _foldable = foldable ?? throw new ArgumentNullException(nameof(foldable));
        if (layouts.Count == 0) throw new ArgumentException("At least one port layout is required", nameof(layouts));

        _buttons = new bool[layouts[0].Buttons.Count];
        _axes = new int[layouts[0].Axes.Count];
        _merged = new PortInput(_buttons, _axes);

        // Immutable and never rewritten, so one instance per port serves the whole session.
        _neutral = new PortInput[layouts.Count];
        for (int p = 0; p < layouts.Count; p++) _neutral[p] = PortInput.Neutral(layouts[p]);
    }

    /// <summary>
    /// One frame's inputs with every seat merged onto port 0 and the rest held neutral.
    ///
    /// The result is valid only until the next call — the buffers behind it are reused. Do not store
    /// it. <paramref name="inputs"/> is never mutated: rollback keeps the exact
    /// <see cref="PortInput"/> objects it ran, and writing through them would corrupt the comparison
    /// that decides whether a repair is needed.
    /// </summary>
    public InputSet Apply(InputSet inputs)
    {
        if (inputs == null) throw new ArgumentNullException(nameof(inputs));

        int ports = inputs.Ports.Length;
        // The set's own width, not the layout count: a two-player session on a four-port core
        // carries two entries, and InputSetController already rests the ports past the end at each
        // axis's own Neutral. Widening here would say something the session never agreed.
        if (_ports.Length != ports) _ports = new PortInput[ports];

        int buttonRun = Run(_playerButtonCount, 0, _buttons.Length);
        int axisRun = Run(_playerAxisCount, 0, _axes.Length);
        var own = ports > 0 ? inputs.Ports[0] : null;

        // Port 0's own values first, including the console tail past the player run: the appended
        // Reset/Select/Pause/difficulty controls belong to the machine and to the host who holds
        // port 0, not to any pad. Merging a second seat into them would let a joiner holding a
        // direction reset everyone's console, since the tail sits at indices a pad seat also uses.
        for (int i = 0; i < _buttons.Length; i++)
            _buttons[i] = own != null && i < own.Buttons.Length && own.Buttons[i];
        for (int j = 0; j < _axes.Length; j++)
            _axes[j] = own != null && j < own.Axes.Length ? own.Axes[j] : _layouts[0].Axes[j].Neutral;

        for (int p = 1; p < ports; p++)
        {
            var seat = inputs.Ports[p];
            // A port whose layout does not line up control-for-control with port 0 has no meaning
            // here — its Trigger is not port 0's Trigger. The lobby refuses such a session before it
            // starts; this is the belt to that pair of braces, and it still holds the port neutral
            // rather than leaving one peer folding what another did not.
            if (seat == null || p >= _foldable.Count || !_foldable[p]) continue;

            int n = Math.Min(buttonRun, seat.Buttons.Length);
            for (int i = 0; i < n; i++) _buttons[i] |= seat.Buttons[i];

            int m = Math.Min(axisRun, seat.Axes.Length);
            for (int j = 0; j < m; j++)
            {
                // Furthest from rest wins, and ties go to the lowest seat. Not a sum: two seats
                // pushing an axis opposite ways would otherwise cancel to centre, which is nobody's
                // intent. Only one of them is playing at a time — that is the whole premise of the
                // option — so "whoever is actually moving" is the answer that matches it.
                int neutral = _layouts[0].Axes[j].Neutral;
                if (Distance(seat.Axes[j], neutral) > Distance(_axes[j], neutral))
                    _axes[j] = seat.Axes[j];
            }
        }

        _ports[0] = _merged;
        for (int p = 1; p < ports; p++) _ports[p] = _neutral[Math.Min(p, _neutral.Length - 1)];
        return new InputSet(inputs.Frame, _ports);
    }

    private static long Distance(int value, int neutral)
    {
        long d = (long)value - neutral; // long: an axis may span the whole int range
        return d < 0 ? -d : d;
    }

    /// <summary>The port's leading run of real pad controls, clamped to what the layout actually has.
    /// Clamped rather than checked: this is the per-frame path and a bad count is a wiring bug, not
    /// something worth throwing a frame away over.</summary>
    private static int Run(IReadOnlyList<int> counts, int port, int limit)
    {
        int run = port < counts.Count ? counts[port] : limit;
        return run < 0 ? 0 : run > limit ? limit : run;
    }
}
