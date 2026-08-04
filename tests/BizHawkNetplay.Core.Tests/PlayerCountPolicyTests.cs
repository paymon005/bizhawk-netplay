using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// KI-22: the runtime permitted eight players and the product claimed four, and a host crossing
/// between the two was told nothing.
/// </summary>
public class PlayerCountPolicyTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheTestedSizesSayNothing(int players)
    {
        Assert.False(PlayerCountPolicy.IsBeyondVerified(players));
        Assert.Null(PlayerCountPolicy.Advisory(players));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void BeyondFourSaysSoWithoutRefusing(int players)
    {
        // Said, not prevented. Nothing about a fifth player is known to be broken, and capping on
        // suspicion would remove a capability every array and route in the code already supports.
        Assert.True(PlayerCountPolicy.IsBeyondVerified(players));
        var advisory = PlayerCountPolicy.Advisory(players);
        Assert.NotNull(advisory);
        Assert.Contains(players.ToString(), advisory!);
    }

    [Fact]
    public void TheAdvisoryCountsTheMeshRatherThanWavingAtIt()
    {
        // "Untested" on its own invites either ignoring the warning or abandoning a session that
        // would have been fine. The mesh is every pair, so the number is what makes the point.
        var advisory = PlayerCountPolicy.Advisory(8)!;
        Assert.Contains("28", advisory);   // 8 players = 28 edges
        Assert.Contains("6", advisory);    // against 4 players' 6
        Assert.Contains("7", advisory);    // and 7 sends per frame per machine
    }

    [Fact]
    public void ThePermittedCeilingIsStillTheWireBound()
    {
        // The advisory is about confidence, not about capacity: the hard bound that sizes arrays
        // and validates untrusted peer numbers is unchanged, and stays above the tested range.
        Assert.Equal(8, HandshakeCodec.MaxPlayers);
        Assert.True(PlayerCountPolicy.VerifiedPlayers < HandshakeCodec.MaxPlayers);
    }
}
