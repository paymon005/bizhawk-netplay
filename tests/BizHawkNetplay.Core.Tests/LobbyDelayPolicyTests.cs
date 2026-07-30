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
}
