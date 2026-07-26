using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
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

        [Fact]
        public void UsesActualConsoleFrameDuration()
        {
            var fiftyHz = LobbyDelayPolicy.Choose(100, 20, SyncMode.Rollback, 1, 20);
            var sixtyHz = LobbyDelayPolicy.Choose(100, Frame60, SyncMode.Rollback, 1, 20);

            Assert.Equal(4, fiftyHz.Frames); // ceil(50/20) + one frame
            Assert.Equal(4, sixtyHz.Frames); // ceil(50/16.67) + one frame
        }
    }
}
