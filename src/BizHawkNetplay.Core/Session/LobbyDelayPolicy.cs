using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Session;

/// <summary>The host's pre-session input-delay choice and the details needed to explain it.</summary>
public readonly struct LobbyDelayChoice
{
    public LobbyDelayChoice(int frames, int automaticFrames, bool hasEstimate, bool wasCapped)
    {
        Frames = frames;
        AutomaticFrames = automaticFrames;
        HasEstimate = hasEstimate;
        WasCapped = wasCapped;
    }

    /// <summary>Final session delay after applying the manual floor and automatic maximum.</summary>
    public int Frames { get; }

    /// <summary>Uncapped delay suggested by the RTT estimate.</summary>
    public int AutomaticFrames { get; }

    public bool HasEstimate { get; }
    public bool WasCapped { get; }
}

/// <summary>A lobby round-trip measurement: the settled figure plus how much it moved.</summary>
public readonly struct LobbyRttSample
{
    public LobbyRttSample(double medianMs, double highMs)
    {
        MedianMs = medianMs;
        HighMs = highMs < medianMs ? medianMs : highMs;
    }

    /// <summary>Median round-trip — the settled cost of the link.</summary>
    public double MedianMs { get; }

    /// <summary>High-water round-trip across the probe window, never below the median.</summary>
    public double HighMs { get; }

    /// <summary>How far the link swings above its settled cost. This, not the median, is what
    /// decides whether lockstep stalls: a frame is late if <em>this</em> packet was slow.</summary>
    public double JitterMs => HighMs - MedianMs;
}

/// <summary>
/// One peer's report of its own UDP mesh before GO: its worst edge, and how much of the mesh that
/// figure actually covers. The host's control links reach only the joiners, so on a 4-player mesh
/// it can measure 3 of the 6 edges by itself and only ever over TCP; the joiner-to-joiner edges
/// exist solely in reports like this one.
/// </summary>
public readonly struct LobbyMeshSample
{
    public static readonly LobbyMeshSample None = default;

    public LobbyMeshSample(LobbyRttSample rtt, int measuredEdges, int totalEdges,
        IReadOnlyList<int>? silentPorts = null)
    {
        Rtt = rtt;
        MeasuredEdges = measuredEdges < 0 ? 0 : measuredEdges;
        TotalEdges = totalEdges < MeasuredEdges ? MeasuredEdges : totalEdges;
        _silentPorts = silentPorts;
    }

    /// <summary>Worst edge's settled and high-water round-trip, on the UDP path input rides.</summary>
    public LobbyRttSample Rtt { get; }

    /// <summary>How many of this peer's edges answered a probe.</summary>
    public int MeasuredEdges { get; }

    /// <summary>How many edges this peer has in total.</summary>
    public int TotalEdges { get; }

    private readonly IReadOnlyList<int>? _silentPorts;

    /// <summary>
    /// The remote ports of the edges that did NOT answer — the identities behind the counts.
    /// Port 0 is the host. The counts alone forced the host's relay to be all-or-nothing: a joiner
    /// short even one edge got everything relayed to it, and the delay was inflated by a relay hop
    /// legs that worked were not taking. Naming the silent edges lets the relay cover exactly the
    /// broken pairs.
    /// </summary>
    public IReadOnlyList<int> SilentPorts => _silentPorts ?? Array.Empty<int>();

    /// <summary>False when nothing answered — the caller must then fall back to its own probes
    /// rather than treat a zero as a fast link.</summary>
    public bool HasMeasurement => MeasuredEdges > 0;

    /// <summary>True when every edge reported. A partial mesh still yields a usable lower bound,
    /// but it is a lower bound and worth naming as one.</summary>
    public bool IsComplete => TotalEdges > 0 && MeasuredEdges >= TotalEdges;
}

/// <summary>
/// Converts a settled lobby RTT into an input delay before the frame driver is constructed.
/// The manual/negotiated value is a floor; <paramref name="automaticMaximum"/> limits only the
/// automatic increase, so a peer explicitly asking for more delay is never silently overridden.
/// </summary>
public static class LobbyDelayPolicy
{
    public static LobbyDelayChoice Choose(
        double roundTripMs, double frameMs, SyncMode mode, int manualFloor, int automaticMaximum,
        double jitterMs = 0)
    {
        manualFloor = Clamp(manualFloor, 1, HandshakeCodec.MaxInputDelay);
        automaticMaximum = Clamp(automaticMaximum, 1, HandshakeCodec.MaxInputDelay);

        if (double.IsNaN(roundTripMs) || double.IsInfinity(roundTripMs) || roundTripMs < 0
            || double.IsNaN(frameMs) || double.IsInfinity(frameMs) || frameMs <= 0)
            return new LobbyDelayChoice(manualFloor, manualFloor, hasEstimate: false, wasCapped: false);

        // A missing or nonsensical jitter figure degrades to the old median-only behaviour rather
        // than poisoning the estimate.
        if (double.IsNaN(jitterMs) || double.IsInfinity(jitterMs) || jitterMs < 0) jitterMs = 0;

        // Delay has to cover the one-way latency of the WORST recent packet, not the average one:
        // lockstep stalls on whichever input actually arrives late, so a median-only estimate
        // systematically under-delays a jittery link and turns every spike into a stall.
        // Adding the full round-trip jitter to the one-way figure is deliberately conservative —
        // it buys roughly two one-way jitter widths of margin, and the automatic maximum still caps it.
        int oneWayFrames = (int)Math.Ceiling(((roundTripMs + 2.0 * jitterMs) / 2.0) / frameMs);
        // Rollback gets one frame of jitter/pump headroom so normal variance remains hidden.
        // Lockstep cannot predict, so retain the existing two-frame scheduling headroom.
        int headroom = mode == SyncMode.Rollback ? 1 : 2;
        int automatic = Clamp(oneWayFrames + headroom, 1, HandshakeCodec.MaxInputDelay);
        int cappedAutomatic = Math.Min(automatic, automaticMaximum);
        int final = Math.Max(manualFloor, cappedAutomatic);
        return new LobbyDelayChoice(final, automatic, hasEstimate: true,
            wasCapped: automatic > automaticMaximum && final < automatic);
    }

    /// <summary>
    /// Equivalent round-trip of the worst RELAYED route, from the host's own legs.
    ///
    /// When a joiner's direct path to another joiner never opens, its input goes through the host
    /// instead — two hops. The lobby measures DIRECT edges, and an edge that never opened
    /// contributes nothing, so the delay was sized from the worst direct path while the affected
    /// players were not using a direct path at all. A relayed one-way is the near leg's one-way
    /// plus the far leg's, so as a round-trip (which is what <see cref="Choose"/> halves again) it
    /// is simply the two host-leg round-trips added together — and that can approach twice the
    /// worst single leg, a whole frame or two of latency the delay never covered.
    ///
    /// Only the legs ACTUALLY being relayed are considered — a pair, not a seat: relaying the edge
    /// between P2 and P4 says nothing about P2's working direct path to P3, and pricing every
    /// pairing of a relayed seat (as this once did) charged the delay for routes nobody rides.
    /// Returns 0 when nothing is relayed or no leg was measured, which folds away harmlessly.
    /// </summary>
    /// <param name="hostLegRttMs">Measured round-trip from the host to each seat, by port.</param>
    /// <param name="relayedPairs">The joiner-to-joiner edges the host is carrying, as port pairs.</param>
    public static double RelayRouteRttMs(
        IReadOnlyDictionary<int, double> hostLegRttMs, IEnumerable<(int A, int B)> relayedPairs)
    {
        if (hostLegRttMs == null || relayedPairs == null) return 0;
        double worst = 0;
        foreach (var (a, b) in relayedPairs)
        {
            if (!hostLegRttMs.TryGetValue(a, out double near) || !IsUsable(near)) continue;
            if (!hostLegRttMs.TryGetValue(b, out double far) || !IsUsable(far)) continue;
            double route = near + far;
            if (route > worst) worst = route;
        }
        return worst;

        static bool IsUsable(double ms) =>
            ms >= 0 && !double.IsNaN(ms) && !double.IsInfinity(ms);
    }

    /// <summary>
    /// The delay a MID-SESSION netcode change should run at, given what the host asked for.
    ///
    /// This exists because <see cref="Choose"/> was only ever consulted in the lobby. The live
    /// settings change took the delay straight from the UI and the mode from the dropdown, so
    /// switching netcode handed the new mode a number chosen for the old one — and lockstep pays the
    /// whole round trip on every frame where rollback hides most of it. A 4-player session on a 65ms
    /// link measured the gap exactly: rollback at delay 2 stalled 0% of ticks, and the same link as
    /// lockstep at delay 2 stalled 94-100% for some 1600 frames. Delay 4 was still 25-67%. 6 was clean.
    ///
    /// Only ever raises, and only when the mode is actually changing. A player who picks a netcode
    /// asked for a netcode, not for a latency budget, and refusing to give them a playable one because
    /// the box still holds the old mode's number is unhelpfully literal. Lowering is a different
    /// matter — that is a preference, and it stays the player's.
    /// </summary>
    /// <param name="roundTripMs">Worst measured round trip, or negative when nothing is measured yet —
    /// in which case the request passes through untouched rather than inventing a figure.</param>
    public static int DelayForModeChange(SyncMode currentMode, SyncMode requestedMode,
        int requestedDelay, double roundTripMs, double frameMs)
    {
        if (requestedMode == currentMode) return requestedDelay;
        if (double.IsNaN(roundTripMs) || double.IsInfinity(roundTripMs) || roundTripMs < 0)
            return requestedDelay;

        var floor = Choose(roundTripMs, frameMs, requestedMode,
            manualFloor: 1, automaticMaximum: HandshakeCodec.MaxInputDelay);
        return floor.HasEstimate && floor.Frames > requestedDelay ? floor.Frames : requestedDelay;
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;
}
