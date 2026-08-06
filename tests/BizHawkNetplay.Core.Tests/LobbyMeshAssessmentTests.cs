using System.Collections.Generic;
using System.Linq;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// What the lobby concludes from its UDP mesh round: which edges the host must carry, who cannot
/// play, and what the input delay has to cover.
///
/// The decision was a fold written inline across a hundred and forty lines of the tool form,
/// interleaved with the control-channel round trips that fed it. Reaching it meant standing up a
/// real four-machine lobby with real NAT behaviour, so none of it was tested — including the rule
/// that has already been wrong once in shipped code.
/// </summary>
public class LobbyMeshAssessmentTests
{
    private static LobbyMeshSample Report(int measured, int total, params int[] silent) =>
        new(new LobbyRttSample(20, 30), measured, total, silent);

    private static LobbyMeshSample Report(double medianMs, double highMs, int measured, int total,
        params int[] silent) =>
        new(new LobbyRttSample(medianMs, highMs), measured, total, silent);

    private static LobbyMeshAssessment FourPlayer() => new(new[] { 1, 2, 3 });

    // ---------------------------------------------------------------- carrying the right edges

    [Fact]
    public void AFullyConnectedMeshCarriesNothing()
    {
        var a = FourPlayer();
        for (int seat = 1; seat <= 3; seat++) a.AddJoinerReport(seat, Report(measured: 3, total: 3));
        Assert.Empty(a.RelayPairs);
        Assert.Empty(a.IncompleteSeats);
        Assert.True(a.FullyCovered);
    }

    /// <summary>
    /// Exactly the named edge is carried, and nothing else.
    ///
    /// This is the rule that was wrong in shipped code: the report used to carry counts rather than
    /// identities, so a joiner short ONE leg got every other player's input relayed to it — and the
    /// session's delay was then sized from a hop its working legs were not taking.
    /// </summary>
    [Fact]
    public void OnlyTheEdgeThatWasNamedIsCarried()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(measured: 2, total: 3, silent: 3));   // P1 cannot reach P3
        a.AddJoinerReport(2, Report(measured: 3, total: 3));
        a.AddJoinerReport(3, Report(measured: 2, total: 3, silent: 1));   // ...and P3 says the same

        Assert.Equal(new[] { (1, 3) }, a.RelayPairs.OrderBy(p => p.A).ToArray());
        Assert.Equal(new[] { 1, 3 }, a.IncompleteSeats.OrderBy(s => s).ToArray());
    }

    [Fact]
    public void AnEdgeReportedFromBothEndsIsOneEdge()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(measured: 2, total: 3, silent: 2));
        a.AddJoinerReport(2, Report(measured: 2, total: 3, silent: 1));
        Assert.Single(a.RelayPairs);
        Assert.Contains((1, 2), a.RelayPairs);
    }

    /// <summary>
    /// The host leg is never a relayable pair.
    ///
    /// A relay to a joiner RUNS over that joiner's host leg, so naming it would ask the host to
    /// forward over the link that is missing — a stall dressed as a rescue. Such a joiner is a
    /// casualty instead, and its seat reopens.
    /// </summary>
    [Fact]
    public void AMissingHostLegIsNeverCarried()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(measured: 2, total: 3, silent: LobbyMeshAssessment.HostSeat));
        Assert.Empty(a.RelayPairs);
        Assert.Equal(new[] { 1 }, a.IncompleteSeats.ToArray());   // still short a leg, just not a relayable one
    }

    [Fact]
    public void ASeatDoesNotRelayToItself()
    {
        var a = FourPlayer();
        a.AddJoinerReport(2, Report(measured: 2, total: 3, silent: new[] { 2, 3 }));
        Assert.Equal(new[] { (2, 3) }, a.RelayPairs.OrderBy(p => p.A).ToArray());
    }

    /// <summary>
    /// A report that says edges are missing but names none falls back to carrying all of that
    /// joiner's edges.
    ///
    /// It should not happen on this protocol. If it does, the choice is between over-delivery —
    /// wasteful, and the behaviour that shipped for a year — and a leg silently carried by nobody,
    /// which presents to the player as one seat's input never arriving. It fails toward the waste.
    /// </summary>
    [Fact]
    public void AnUnnamedHoleFallsBackToCarryingEverythingForThatSeat()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(measured: 1, total: 3));   // "two edges missing", no names
        Assert.Equal(new[] { (1, 2), (1, 3) }, a.RelayPairs.OrderBy(p => p.B).ToArray());
    }

    /// <summary>
    /// A report that names SOME of its silent edges is believed about the rest.
    ///
    /// The backstop applies only to a report that named nothing at all. Inventing the remainder
    /// would carry legs the peer just told us are working, which is the over-delivery the named
    /// pairs were introduced to end.
    /// </summary>
    [Fact]
    public void APartiallyNamedReportIsNotPaddedOut()
    {
        var a = FourPlayer();
        // Claims two edges missing but names one. Believe the name.
        a.AddJoinerReport(1, Report(measured: 1, total: 3, silent: 2));
        Assert.Equal(new[] { (1, 2) }, a.RelayPairs.ToArray());
    }

    // ---------------------------------------------------------------- who cannot play

    [Fact]
    public void SeatsWithNoHostLegAreNamedForTheCallerToDrop()
    {
        var a = FourPlayer();
        a.MarkNoHostLeg(2);
        a.MarkNoHostLeg(2);   // idempotent: the caller may notice twice
        Assert.Equal(new[] { 2 }, a.SeatsWithoutHostLeg.ToArray());
    }

    // ---------------------------------------------------------------- coverage

    [Fact]
    public void CoverageCountsEveryEdgeFromBothEnds()
    {
        var a = FourPlayer();
        for (int seat = 1; seat <= 3; seat++) a.AddJoinerReport(seat, Report(measured: 3, total: 3));
        a.AddHostEdges(measured: 3, total: 3, new LobbyRttSample(10, 12));
        Assert.Equal(12, a.TotalEdges);
        Assert.Equal(12, a.MeasuredEdges);
        Assert.True(a.FullyCovered);
    }

    [Fact]
    public void APartlyOpenMeshIsNotReportedAsFullyCovered()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(measured: 2, total: 3, silent: 3));
        a.AddJoinerReport(2, Report(measured: 3, total: 3));
        a.AddJoinerReport(3, Report(measured: 2, total: 3, silent: 1));
        a.AddHostEdges(measured: 3, total: 3, new LobbyRttSample(10, 12));
        Assert.False(a.FullyCovered);
        Assert.Equal(10, a.MeasuredEdges);
        Assert.Equal(12, a.TotalEdges);
    }

    [Fact]
    public void AMeshNobodyMeasuredIsNotFullyCovered()
    {
        var a = FourPlayer();
        Assert.False(a.FullyCovered);   // nothing reported: not "all zero edges answered"
    }

    // ---------------------------------------------------------------- the figures a delay covers

    [Fact]
    public void TheWorstRoundTripIsTakenAcrossEveryEdgeThatAnswered()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(20, 25, measured: 3, total: 3));
        a.AddJoinerReport(2, Report(90, 95, measured: 3, total: 3));
        a.AddJoinerReport(3, Report(40, 45, measured: 3, total: 3));
        Assert.Equal(90, a.WorstRttMs);
    }

    /// <summary>
    /// Jitter is maximised independently of the round trip, because they can belong to different
    /// edges. A steady 80/80 link beside a swingy 20/70 one needs a delay covering 80ms of latency
    /// AND 50ms of swing; taking the pair from one edge reports zero jitter while a link swings 50.
    /// </summary>
    [Fact]
    public void WorstJitterIsTakenIndependentlyOfWorstRoundTrip()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(80, 80, measured: 3, total: 3));   // slow, steady
        a.AddJoinerReport(2, Report(20, 70, measured: 3, total: 3));   // fast, swingy
        Assert.Equal(80, a.WorstRttMs);
        Assert.Equal(50, a.WorstJitterMs);
    }

    [Fact]
    public void AReportWithNoMeasurementContributesNoFigures()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(999, 999, measured: 0, total: 3));
        Assert.Equal(0, a.WorstRttMs);
        Assert.Equal(0, a.WorstJitterMs);
        Assert.Equal(3, a.TotalEdges);
        Assert.Equal(0, a.MeasuredEdges);
    }

    // ---------------------------------------------------------------- the relayed route's cost

    /// <summary>
    /// A relayed leg is two host hops, so it is not covered by the worst DIRECT edge — and the
    /// edge it replaces contributed nothing to that figure, because it never answered.
    /// </summary>
    [Fact]
    public void ARelayedRouteThatCostsMoreThanEveryDirectPathBecomesTheFigure()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(20, 25, measured: 2, total: 3, silent: 3));
        a.AddJoinerReport(3, Report(20, 25, measured: 2, total: 3, silent: 1));
        Assert.Equal(20, a.WorstRttMs);

        var hostLegs = new Dictionary<int, LobbyRttSample>
        {
            [1] = new(60, 70),
            [3] = new(70, 80),
        };
        Assert.True(a.FoldRelayedRoutes(hostLegs, out double relayed));
        Assert.True(relayed > 20, $"a two-hop route priced at {relayed}ms is not two hops");
        Assert.Equal(relayed, a.WorstRttMs);
    }

    [Fact]
    public void ARelayedRouteCheaperThanTheWorstDirectPathDoesNotBecomeTheFigure()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(300, 310, measured: 2, total: 3, silent: 3));
        a.AddJoinerReport(3, Report(300, 310, measured: 2, total: 3, silent: 1));

        var hostLegs = new Dictionary<int, LobbyRttSample> { [1] = new(5, 6), [3] = new(5, 6) };
        Assert.False(a.FoldRelayedRoutes(hostLegs, out _));
        Assert.Equal(300, a.WorstRttMs);   // the direct path is still the one to cover
    }

    [Fact]
    public void WithNothingRelayedThereIsNoRelayedFigure()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(measured: 3, total: 3));
        Assert.False(a.FoldRelayedRoutes(new Dictionary<int, LobbyRttSample>(), out double relayed));
        Assert.Equal(0, relayed);
    }

    /// <summary>
    /// A relayed route's jitter competes for the session-wide worst even when its round trip does
    /// not — the route swings when EITHER hop swings, and a delay that covers only the latency
    /// stalls on every swing.
    /// </summary>
    [Fact]
    public void RelayedJitterCountsEvenWhenTheRelayedRoundTripDoesNot()
    {
        var a = FourPlayer();
        a.AddJoinerReport(1, Report(300, 305, measured: 2, total: 3, silent: 3));
        a.AddJoinerReport(3, Report(300, 305, measured: 2, total: 3, silent: 1));
        Assert.Equal(5, a.WorstJitterMs);

        // Two cheap but wildly swingy hops: the round trip stays under the direct worst, the swing
        // does not.
        var hostLegs = new Dictionary<int, LobbyRttSample> { [1] = new(20, 90), [3] = new(20, 90) };
        Assert.False(a.FoldRelayedRoutes(hostLegs, out _));
        Assert.True(a.WorstJitterMs > 5,
            $"the relayed route's swing was dropped (jitter still {a.WorstJitterMs}ms)");
    }

    // ---------------------------------------------------------------- two players

    [Fact]
    public void ATwoPlayerLobbyHasNoJoinerToJoinerEdgesToCarry()
    {
        var a = new LobbyMeshAssessment(new[] { 1 });
        a.AddJoinerReport(1, Report(measured: 1, total: 1));
        a.AddHostEdges(measured: 1, total: 1, new LobbyRttSample(30, 35));
        Assert.Empty(a.RelayPairs);
        Assert.True(a.FullyCovered);
        Assert.Equal(30, a.WorstRttMs);
    }

    [Fact]
    public void ATwoPlayerLobbyWithNoHostLegHasNothingToRelayOver()
    {
        // The only edge IS the host leg, so an unnamed hole cannot invent a pair to carry.
        var a = new LobbyMeshAssessment(new[] { 1 });
        a.AddJoinerReport(1, Report(measured: 0, total: 1));
        a.MarkNoHostLeg(1);
        Assert.Empty(a.RelayPairs);
        Assert.Equal(new[] { 1 }, a.SeatsWithoutHostLeg.ToArray());
    }
}
