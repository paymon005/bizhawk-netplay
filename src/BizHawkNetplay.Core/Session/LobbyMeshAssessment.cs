using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// What the lobby's UDP mesh round concluded: which joiner-to-joiner edges the host must carry,
/// who cannot play at all, how much of the mesh was actually measured, and the worst figures a
/// session-wide input delay has to cover.
///
/// <b>Why this is a type.</b> It was a fold written inline across a hundred and forty lines of the
/// tool form, interleaved with the control-channel round trips that produced its inputs and the log
/// lines that explained its outputs. Three of its rules are subtle, all three have a history, and
/// none of them could be reached by a test because reaching them meant standing up a real lobby
/// with real NAT behaviour on four machines.
///
/// The rules:
///
/// <list type="bullet">
/// <item><b>Only the named edges are carried.</b> This used to be all-or-nothing per joiner,
/// because the report carried counts rather than identities — so a joiner short ONE leg got every
/// other player's input relayed to it, and the session's delay was then inflated by a hop its
/// working legs were not taking.</item>
/// <item><b>The host leg is never a relayable pair.</b> A relay to a joiner RUNS over that joiner's
/// host leg. Naming it as a pair to carry would ask the host to forward over the link that is
/// missing, which is a stall rather than a rescue — so a joiner with no host leg is a casualty
/// instead, and its seat reopens.</item>
/// <item><b>An unnamed hole fails toward over-delivery.</b> A report that says edges are missing
/// without naming them should not happen on this protocol. If it does, every one of that joiner's
/// edges is carried — the old wasteful behaviour — because the alternative is a leg silently
/// carried by nobody, which presents as one seat's input never arriving.</item>
/// </list>
///
/// Pure: it is told what the reports said and answers what to do. The control-channel round trips,
/// the relay installation and the words the player reads stay with the caller.
/// </summary>
public sealed class LobbyMeshAssessment
{
    private readonly List<int> _seats;
    private readonly HashSet<(int A, int B)> _relayPairs = new();
    private readonly List<int> _incompleteSeats = new();
    private readonly List<int> _withoutHostLeg = new();
    private readonly HashSet<int> _reported = new();

    /// <param name="joinerSeats">The seats in this lobby other than the host's.</param>
    public LobbyMeshAssessment(IEnumerable<int> joinerSeats)
    {
        if (joinerSeats == null) throw new ArgumentNullException(nameof(joinerSeats));
        _seats = new List<int>(joinerSeats);
    }

    /// <summary>The host's own seat. Named rather than assumed at the comparisons below.</summary>
    public const int HostSeat = 0;

    /// <summary>Edges that produced a round-trip sample, summed over every report.</summary>
    public int MeasuredEdges { get; private set; }

    /// <summary>Edges that existed to be measured. Each logical edge is reported from both ends, so
    /// a four-player mesh's six edges arrive here as twelve.</summary>
    public int TotalEdges { get; private set; }

    /// <summary>Worst settled round trip across every edge that answered.</summary>
    public double WorstRttMs { get; private set; }

    /// <summary>
    /// Worst jitter across every edge that answered — maximised independently of the round trip.
    ///
    /// A steady 80/80 edge beside a swingy 20/70 one has a worst median of 80 and a worst jitter of
    /// 50, and they belong to different edges. Taking them as a pair from one edge would report
    /// zero jitter while one link swung fifty milliseconds.
    /// </summary>
    public double WorstJitterMs { get; private set; }

    /// <summary>The joiner-to-joiner edges the host must forward, as unordered seat pairs.</summary>
    public IReadOnlyCollection<(int A, int B)> RelayPairs => _relayPairs;

    /// <summary>Joiners that could not open every direct leg — the relay's customers.</summary>
    public IReadOnlyList<int> IncompleteSeats => _incompleteSeats;

    /// <summary>Joiners with no two-way UDP path to the host. They cannot play and cannot be
    /// relayed to, so the caller drops them and reopens their seats.</summary>
    public IReadOnlyList<int> SeatsWithoutHostLeg => _withoutHostLeg;

    /// <summary>Every edge that existed answered. The delay sized from these figures covers the
    /// whole mesh rather than the part of it that happened to be up.</summary>
    public bool FullyCovered => TotalEdges > 0 && MeasuredEdges >= TotalEdges;

    /// <summary>Fold in one joiner's report of its own edges.</summary>
    public void AddJoinerReport(int seat, LobbyMeshSample report)
    {
        _reported.Add(seat);
        TotalEdges += report.TotalEdges;
        MeasuredEdges += report.MeasuredEdges;

        if (report.MeasuredEdges < report.TotalEdges) RecordIncomplete(seat, report.SilentPorts);
        if (report.HasMeasurement) Fold(report.Rtt);
    }

    /// <summary>Fold in the host's own edges, measured on UDP from this side.</summary>
    public void AddHostEdges(int measured, int total, LobbyRttSample? sample)
    {
        MeasuredEdges += Math.Max(0, measured);
        TotalEdges += Math.Max(0, total);
        if (sample.HasValue) Fold(sample.Value);
    }

    /// <summary>
    /// The host has no proven two-way path to this seat.
    ///
    /// Proven means a probe the host sent came back acknowledged — not merely that something
    /// arrived. Any inbound datagram marks an advertised endpoint alive, including the peer's own
    /// punches, so a one-way path used to pass for a working one and the session started with a
    /// relay running over the very leg that was broken.
    /// </summary>
    public void MarkNoHostLeg(int seat)
    {
        if (!_withoutHostLeg.Contains(seat)) _withoutHostLeg.Add(seat);
    }

    /// <summary>
    /// Fold in what the relayed routes cost, once the direct figures are in.
    ///
    /// A relayed leg is two host hops, so it is not covered by the worst DIRECT edge the delay was
    /// about to be sized from — and an edge that never opened contributed nothing to that figure in
    /// the first place. True when the relayed route is the one the delay must now be sized from,
    /// which is the fact worth telling the player.
    /// </summary>
    public bool FoldRelayedRoutes(IReadOnlyDictionary<int, LobbyRttSample> hostLegs,
        out double relayedRttMs)
    {
        relayedRttMs = 0;
        if (_relayPairs.Count == 0) return false;
        var relayed = LobbyDelayPolicy.RelayRouteStats(hostLegs, _relayPairs);
        relayedRttMs = relayed.MedianMs;
        // The route swings when either hop swings, so its combined jitter competes for the
        // session-wide worst independently of whether its round trip does.
        if (relayed.JitterMs > WorstJitterMs) WorstJitterMs = relayed.JitterMs;
        if (relayedRttMs <= WorstRttMs) return false;
        WorstRttMs = relayedRttMs;
        return true;
    }

    private void RecordIncomplete(int seat, IReadOnlyList<int> silentPorts)
    {
        if (!_incompleteSeats.Contains(seat)) _incompleteSeats.Add(seat);

        bool namedAny = false;
        if (silentPorts != null)
            foreach (int silent in silentPorts)
            {
                // Neither the host leg nor the seat itself is a relayable pair — see the class
                // summary for why the host leg in particular cannot be one.
                if (silent == HostSeat || silent == seat) continue;
                _relayPairs.Add(Pair(seat, silent));
                namedAny = true;
            }

        // The backstop. Only for a report that named NOTHING at all: one that named some of its
        // silent edges has told us what it knows, and inventing the rest would carry legs that
        // are working.
        if (!namedAny && (silentPorts == null || silentPorts.Count == 0))
            foreach (int other in _seats)
                if (other != seat) _relayPairs.Add(Pair(seat, other));
    }

    private void Fold(LobbyRttSample sample)
    {
        if (sample.MedianMs > WorstRttMs) WorstRttMs = sample.MedianMs;
        if (sample.JitterMs > WorstJitterMs) WorstJitterMs = sample.JitterMs;
    }

    /// <summary>An unordered seat pair, normalised so (2,4) and (4,2) are the same edge.</summary>
    public static (int A, int B) Pair(int a, int b) => a < b ? (a, b) : (b, a);
}
