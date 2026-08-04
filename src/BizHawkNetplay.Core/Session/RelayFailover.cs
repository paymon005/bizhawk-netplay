using System.Collections.Generic;

namespace BizHawkNetplay.Core.Session;

/// <summary>What the host should do with one joiner's report that a mesh leg has gone silent.</summary>
public enum RelayFailoverVerdict
{
    /// <summary>The report does not describe a leg this host can or should carry — wrong role,
    /// dead timeline, a seat that does not exist, or the reporter naming itself.</summary>
    Refuse,
    /// <summary>The named pair is already riding the relay; the report is the other end of a leg
    /// that was failed over moments ago, or a repeat. Nothing to do, nothing to log loudly.</summary>
    AlreadyCarried,
    /// <summary>The leg cannot be rescued: one of the host's own legs to the pair is down, and a
    /// relay runs OVER those legs. The existing watchdog owns this outcome.</summary>
    NoHostLeg,
    /// <summary>Install the pair: forward input between these two seats over the host's legs.</summary>
    Install,
}

/// <summary>
/// The decision rule behind live relay failover, extracted so it is testable without a form.
///
/// The lobby decides relay pairs once, at session start, from the mesh measurement — deliberately,
/// because a start-of-session decision is deterministic and cannot oscillate. What that leaves
/// uncovered is the leg that dies MID-session: the host cannot see a joiner-to-joiner path fail
/// (its own legs are fine), so the peer starving on the dead leg reports it, and the host answers
/// by carrying the pair exactly as it would have had the leg never opened.
///
/// The anti-oscillation property carries over: a pair, once installed, stays installed for the
/// session. Input is keyed by (port, frame), so a relayed copy arriving beside a revived direct
/// one is discarded — a leg that comes back simply makes the relay redundant, never harmful, and
/// tearing it back down under packet loss is precisely the flapping this refuses to do.
/// </summary>
public static class RelayFailover
{
    /// <summary>
    /// Silence on a remote port before a joiner reports the leg to the host, in seconds. Sits
    /// between the repunch threshold (1.5s — transient loss usually recovers there) and the
    /// session-ending watchdog (8s), so the relay has ~5 seconds to carry input before the
    /// backstop fires — and the backstop still owns the case the relay cannot cover.
    /// </summary>
    public const double ReportAfterSeconds = 3.0;

    /// <summary>
    /// Whether a joiner should report a silent port to the host right now. Pure gate over the
    /// caller's own measurements: long enough silence, a seat the relay could actually help
    /// (the HOST's own leg cannot be relayed — the relay runs over it), and not already reported
    /// (the host installs permanently, so once is enough; the latch clears when input recovers).
    /// </summary>
    public static bool ShouldReport(double silenceSeconds, int silentPort, bool alreadyReported) =>
        silenceSeconds >= ReportAfterSeconds && silentPort != 0 && !alreadyReported;

    /// <summary>
    /// The host's verdict on one report. <paramref name="hostLegAlive"/> answers "does this host
    /// have a proven two-way UDP path to seat N right now" — the property a relay needs of BOTH
    /// ends, supplied by the caller because only the transport can measure it.
    /// </summary>
    public static RelayFailoverVerdict Judge(
        bool isHost, bool sessionActive, bool generationCurrent,
        int reporterPort, int silentPort, int playerCount,
        IReadOnlyCollection<int> vacatedPorts,
        IReadOnlyCollection<(int A, int B)> carriedPairs,
        bool reporterLegAlive, bool silentLegAlive)
    {
        if (!isHost || !sessionActive || !generationCurrent) return RelayFailoverVerdict.Refuse;
        if (silentPort <= 0 || silentPort >= playerCount) return RelayFailoverVerdict.Refuse;
        if (reporterPort <= 0 || reporterPort >= playerCount) return RelayFailoverVerdict.Refuse;
        if (silentPort == reporterPort) return RelayFailoverVerdict.Refuse;
        // An empty seat sends nothing by design; its silence is not an outage. A stale report can
        // race the vacate, so this is a real case rather than paranoia.
        if (Contains(vacatedPorts, silentPort) || Contains(vacatedPorts, reporterPort))
            return RelayFailoverVerdict.Refuse;

        var pair = reporterPort < silentPort
            ? (reporterPort, silentPort)
            : (silentPort, reporterPort);
        foreach (var carried in carriedPairs)
            if (carried == pair) return RelayFailoverVerdict.AlreadyCarried;

        return reporterLegAlive && silentLegAlive
            ? RelayFailoverVerdict.Install
            : RelayFailoverVerdict.NoHostLeg;
    }

    private static bool Contains(IReadOnlyCollection<int> set, int value)
    {
        foreach (int entry in set)
            if (entry == value) return true;
        return false;
    }
}
