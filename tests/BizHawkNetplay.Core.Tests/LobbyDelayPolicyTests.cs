using System.Collections.Generic;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

public class LobbyDelayPolicyTests
{
    private const double Frame60 = 1000.0 / 60.0;

    [Theory]
    [InlineData(100, SyncMode.Rollback, 4)]
    [InlineData(150, SyncMode.Rollback, 6)]
    [InlineData(200, SyncMode.Rollback, 7)]
    [InlineData(100, SyncMode.Lockstep, 5)]
    [InlineData(150, SyncMode.Lockstep, 7)]
    [InlineData(200, SyncMode.Lockstep, 8)]
    public void RecommendationCoversOneWayLatencyAndModeHeadroom(
        double rttMs, SyncMode mode, int expected)
    {
        var choice = LobbyDelayPolicy.Choose(rttMs, Frame60, mode, manualFloor: 1,
            automaticMaximum: 20);

        Assert.True(choice.HasEstimate);
        Assert.False(choice.WasCapped);
        Assert.Equal(expected, choice.Frames);
        Assert.Equal(expected, choice.AutomaticFrames);
    }

    [Fact]
    public void AutomaticMaximumCapsOnlyTheCalculatedIncrease()
    {
        var capped = LobbyDelayPolicy.Choose(200, Frame60, SyncMode.Rollback,
            manualFloor: 1, automaticMaximum: 5);
        Assert.Equal(5, capped.Frames);
        Assert.Equal(7, capped.AutomaticFrames);
        Assert.True(capped.WasCapped);

        var explicitRequestWins = LobbyDelayPolicy.Choose(200, Frame60, SyncMode.Rollback,
            manualFloor: 9, automaticMaximum: 5);
        Assert.Equal(9, explicitRequestWins.Frames);
        Assert.False(explicitRequestWins.WasCapped);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidEstimateKeepsManualFloor(double rttMs)
    {
        var choice = LobbyDelayPolicy.Choose(rttMs, Frame60, SyncMode.Rollback,
            manualFloor: 3, automaticMaximum: 8);

        Assert.False(choice.HasEstimate);
        Assert.Equal(3, choice.Frames);
    }

    [Theory]
    [InlineData(100, SyncMode.Rollback, 4)]
    [InlineData(150, SyncMode.Rollback, 6)]
    [InlineData(100, SyncMode.Lockstep, 5)]
    [InlineData(200, SyncMode.Lockstep, 8)]
    public void ZeroJitterReproducesTheMedianOnlyResult(double rttMs, SyncMode mode, int expected)
    {
        // The jitter term is additive and defaulted, so every pre-existing caller must land on
        // exactly the delay it landed on before.
        Assert.Equal(expected,
            LobbyDelayPolicy.Choose(rttMs, Frame60, mode, 1, 20).Frames);
        Assert.Equal(expected,
            LobbyDelayPolicy.Choose(rttMs, Frame60, mode, 1, 20, jitterMs: 0).Frames);
    }

    [Theory]
    [InlineData(SyncMode.Lockstep, 7)] // ceil(80/16.67) = 5, plus two frames of headroom
    [InlineData(SyncMode.Rollback, 6)] // ...plus one for rollback
    public void JitterBuysHeadroomTheMedianAloneWouldMiss(SyncMode mode, int expected)
    {
        // A link whose median says 100ms but which swings 30ms above that stalls on the swing,
        // not the median — the delay has to cover the late packet.
        var steady = LobbyDelayPolicy.Choose(100, Frame60, mode, 1, 20);
        var jittery = LobbyDelayPolicy.Choose(100, Frame60, mode, 1, 20, jitterMs: 30);

        Assert.True(jittery.Frames > steady.Frames);
        Assert.Equal(expected, jittery.Frames);
    }

    [Fact]
    public void AutomaticMaximumStillCapsAJitterInflatedRecommendation()
    {
        var choice = LobbyDelayPolicy.Choose(200, Frame60, SyncMode.Rollback,
            manualFloor: 1, automaticMaximum: 5, jitterMs: 30);

        Assert.Equal(9, choice.AutomaticFrames); // ceil(130/16.67) = 8, plus one
        Assert.Equal(5, choice.Frames);
        Assert.True(choice.WasCapped);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5)]
    public void NonsenseJitterDegradesToTheMedianOnlyEstimate(double jitterMs)
    {
        var choice = LobbyDelayPolicy.Choose(100, Frame60, SyncMode.Lockstep, 1, 20, jitterMs);

        Assert.True(choice.HasEstimate);
        Assert.Equal(5, choice.Frames);
    }

    [Fact]
    public void RttSampleNeverReportsNegativeJitter()
    {
        // A high-water below the median is nonsense; clamp rather than let it subtract delay.
        var inverted = new LobbyRttSample(medianMs: 40, highMs: 25);
        Assert.Equal(40, inverted.HighMs);
        Assert.Equal(0, inverted.JitterMs);

        var normal = new LobbyRttSample(medianMs: 40, highMs: 75);
        Assert.Equal(35, normal.JitterMs);
    }

    [Fact]
    public void UsesActualConsoleFrameDuration()
    {
        // RTT 150ms is chosen because the two frame rates land on DIFFERENT delays — at RTT
        // 100ms both round to 4, so a regression to hard-coded 60Hz would have passed (KI-5).
        var fiftyHz = LobbyDelayPolicy.Choose(150, 20, SyncMode.Rollback, 1, 20);
        var sixtyHz = LobbyDelayPolicy.Choose(150, Frame60, SyncMode.Rollback, 1, 20);

        Assert.Equal(5, fiftyHz.Frames); // ceil(75/20) + one frame = 4 + 1
        Assert.Equal(6, sixtyHz.Frames); // ceil(75/16.67) + one frame = 5 + 1
    }

    // --- mid-session netcode change ------------------------------------------------------------

    [Fact]
    public void SwitchingToLockstepRaisesADelayChosenForRollback()
    {
        // The measured case: 4 players, 65ms link, PAL 20ms frames. Rollback ran clean at delay 2;
        // the same link as lockstep at delay 2 stalled 94-100% of ticks for ~1600 frames because the
        // live settings change never re-asked what the new mode needed.
        int raised = LobbyDelayPolicy.DelayForModeChange(SyncMode.Rollback, SyncMode.Lockstep,
            requestedDelay: 2, roundTripMs: 65, frameMs: 20);

        Assert.True(raised > 2, $"lockstep on a 65ms link needs more than delay 2, got {raised}");
        Assert.Equal(LobbyDelayPolicy.Choose(65, 20, SyncMode.Lockstep, 1,
            HandshakeCodec.MaxInputDelay).Frames, raised);
    }

    [Fact]
    public void ItNeverLowersWhatTheHostAskedFor()
    {
        // Going the other way, rollback needs less — but the delay is the player's preference and a
        // netcode change is not permission to spend it. Only raising is ours to do.
        int kept = LobbyDelayPolicy.DelayForModeChange(SyncMode.Lockstep, SyncMode.Rollback,
            requestedDelay: 8, roundTripMs: 65, frameMs: 20);
        Assert.Equal(8, kept);
    }

    [Fact]
    public void ADelayOnlyChangeIsLeftAlone()
    {
        // Same mode either side: the host is adjusting delay deliberately and must not be overridden,
        // in either direction.
        foreach (var mode in new[] { SyncMode.Rollback, SyncMode.Lockstep })
        {
            Assert.Equal(1, LobbyDelayPolicy.DelayForModeChange(mode, mode, 1, 200, 20));
            Assert.Equal(9, LobbyDelayPolicy.DelayForModeChange(mode, mode, 9, 5, 20));
        }
    }

    /// <summary>
    /// A relayed seat's input takes two hops, and the lobby only ever measured direct edges — an
    /// edge that never opened (which is why the relay exists) contributed nothing at all. So the
    /// delay was sized from the worst DIRECT path while the affected players were not using one.
    /// The relayed route's equivalent round-trip is the two host legs added together.
    /// </summary>
    [Fact]
    public void RelayRouteCostsBothHostLegs()
    {
        // P1 is relayed. Its own leg is 40ms; the far seats are 60ms and 20ms. The worst route it
        // can be given is 40 + 60, which is well past the 60ms worst DIRECT edge.
        var legs = new Dictionary<int, double> { [1] = 40, [2] = 60, [3] = 20 };
        Assert.Equal(100, LobbyDelayPolicy.RelayRouteRttMs(legs, new[] { 1 }));

        // A seat is never relayed to itself, so a lone relayed seat with no far end costs nothing.
        Assert.Equal(0, LobbyDelayPolicy.RelayRouteRttMs(
            new Dictionary<int, double> { [1] = 40 }, new[] { 1 }));

        // Nothing relayed: the session pays nothing for this.
        Assert.Equal(0, LobbyDelayPolicy.RelayRouteRttMs(legs, new int[0]));
    }

    [Fact]
    public void RelayRouteIgnoresLegsItCannotTrust()
    {
        // An unmeasured or nonsensical leg must not be folded in as though it were a measurement,
        // and must not poison the routes that WERE measured.
        var legs = new Dictionary<int, double>
        {
            [1] = 30,
            [2] = -1,                        // never answered
            [3] = double.NaN,
            [4] = double.PositiveInfinity,
            [5] = 50,
        };
        Assert.Equal(80, LobbyDelayPolicy.RelayRouteRttMs(legs, new[] { 1 }));
        // A relayed seat whose OWN leg is unmeasured contributes nothing rather than a wild number.
        Assert.Equal(0, LobbyDelayPolicy.RelayRouteRttMs(legs, new[] { 2 }));
        Assert.Equal(0, LobbyDelayPolicy.RelayRouteRttMs(null!, new[] { 1 }));
        Assert.Equal(0, LobbyDelayPolicy.RelayRouteRttMs(legs, null!));
    }

    /// <summary>The point of measuring it: a relayed route buys real frames of delay that the worst
    /// direct edge alone would not have.</summary>
    [Fact]
    public void RelayRouteRaisesTheChosenDelay()
    {
        var legs = new Dictionary<int, double> { [1] = 40, [2] = 60 };
        double direct = 60;
        double relayed = LobbyDelayPolicy.RelayRouteRttMs(legs, new[] { 1 });

        int fromDirect = LobbyDelayPolicy.Choose(direct, Frame60, SyncMode.Rollback, 1, 20).Frames;
        int fromRelayed = LobbyDelayPolicy.Choose(relayed, Frame60, SyncMode.Rollback, 1, 20).Frames;
        Assert.True(fromRelayed > fromDirect,
            $"relayed route ({relayed}ms) should need more delay than the worst direct edge " +
            $"({direct}ms), got {fromRelayed} vs {fromDirect}");
    }

    [Fact]
    public void WithNothingMeasuredItInventsNothing()
    {
        // Before any ping lands, WorstPingMs reports -1. Guessing a floor from that would be worse
        // than leaving the host's number alone.
        foreach (double rtt in new[] { -1.0, double.NaN, double.PositiveInfinity })
            Assert.Equal(3, LobbyDelayPolicy.DelayForModeChange(SyncMode.Rollback, SyncMode.Lockstep,
                requestedDelay: 3, roundTripMs: rtt, frameMs: 20));
    }
}
