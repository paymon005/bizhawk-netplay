using System;

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

    public LobbyMeshSample(LobbyRttSample rtt, int measuredEdges, int totalEdges)
    {
        Rtt = rtt;
        MeasuredEdges = measuredEdges < 0 ? 0 : measuredEdges;
        TotalEdges = totalEdges < MeasuredEdges ? MeasuredEdges : totalEdges;
    }

    /// <summary>Worst edge's settled and high-water round-trip, on the UDP path input rides.</summary>
    public LobbyRttSample Rtt { get; }

    /// <summary>How many of this peer's edges answered a probe.</summary>
    public int MeasuredEdges { get; }

    /// <summary>How many edges this peer has in total.</summary>
    public int TotalEdges { get; }

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

    private static int Clamp(int value, int minimum, int maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;
}
