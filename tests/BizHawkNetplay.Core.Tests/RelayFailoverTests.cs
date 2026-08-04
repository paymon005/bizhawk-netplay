using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The live relay failover decision rule: when a joiner reports a dead leg, and what the host does
/// with the report. The verdicts encode the property the feature stands on — a pair is carried
/// only when the host's own legs to BOTH ends are proven, installed once, and never flapped.
/// </summary>
public class RelayFailoverTests
{
    private static readonly int[] NoVacated = [];
    private static readonly (int, int)[] NoPairs = [];

    [Fact]
    public void AJoinerReportsOnlyAfterTheThresholdAndOnlyOnce()
    {
        Assert.False(RelayFailover.ShouldReport(2.9, silentPort: 2, alreadyReported: false));
        Assert.True(RelayFailover.ShouldReport(3.0, silentPort: 2, alreadyReported: false));
        Assert.False(RelayFailover.ShouldReport(10.0, silentPort: 2, alreadyReported: true));
    }

    [Fact]
    public void TheHostLegIsNeverReported()
    {
        // A relay to any pair runs OVER the host leg — a dead host leg has no relay rescue, and
        // reporting it would only spend a message on a fact the watchdog already owns.
        Assert.False(RelayFailover.ShouldReport(10.0, silentPort: 0, alreadyReported: false));
    }

    [Fact]
    public void AProvenPairIsInstalled()
    {
        Assert.Equal(RelayFailoverVerdict.Install, RelayFailover.Judge(
            isHost: true, sessionActive: true, generationCurrent: true,
            reporterPort: 1, silentPort: 2, playerCount: 4,
            NoVacated, NoPairs, reporterLegAlive: true, silentLegAlive: true));
    }

    [Fact]
    public void ACarriedPairIsNotInstalledTwice_FromEitherEnd()
    {
        var carried = new[] { (1, 2) };
        // The other victim of the same dead leg reports moments later, naming the pair backwards.
        Assert.Equal(RelayFailoverVerdict.AlreadyCarried, RelayFailover.Judge(
            true, true, true, reporterPort: 2, silentPort: 1, playerCount: 4,
            NoVacated, carried, true, true));
        Assert.Equal(RelayFailoverVerdict.AlreadyCarried, RelayFailover.Judge(
            true, true, true, reporterPort: 1, silentPort: 2, playerCount: 4,
            NoVacated, carried, true, true));
    }

    [Fact]
    public void ADeadHostLegRefusesTheRescueItCannotPerform()
    {
        Assert.Equal(RelayFailoverVerdict.NoHostLeg, RelayFailover.Judge(
            true, true, true, 1, 2, 4, NoVacated, NoPairs,
            reporterLegAlive: true, silentLegAlive: false));
        Assert.Equal(RelayFailoverVerdict.NoHostLeg, RelayFailover.Judge(
            true, true, true, 1, 2, 4, NoVacated, NoPairs,
            reporterLegAlive: false, silentLegAlive: true));
    }

    [Fact]
    public void StaleOrNonsenseReportsAreRefused()
    {
        // Dead timeline.
        Assert.Equal(RelayFailoverVerdict.Refuse, RelayFailover.Judge(
            true, true, generationCurrent: false, 1, 2, 4, NoVacated, NoPairs, true, true));
        // The host's seat as the silent port (its silence is not a joiner-joiner leg).
        Assert.Equal(RelayFailoverVerdict.Refuse, RelayFailover.Judge(
            true, true, true, 1, 0, 4, NoVacated, NoPairs, true, true));
        // A seat past the session's player count.
        Assert.Equal(RelayFailoverVerdict.Refuse, RelayFailover.Judge(
            true, true, true, 1, 5, 4, NoVacated, NoPairs, true, true));
        // The reporter naming itself.
        Assert.Equal(RelayFailoverVerdict.Refuse, RelayFailover.Judge(
            true, true, true, 2, 2, 4, NoVacated, NoPairs, true, true));
        // A vacated seat sends nothing by design; a report racing the vacate is not an outage.
        Assert.Equal(RelayFailoverVerdict.Refuse, RelayFailover.Judge(
            true, true, true, 1, 2, 4, vacatedPorts: new[] { 2 }, NoPairs, true, true));
    }

    [Fact]
    public void InputOutageRoundTripsOnTheWire()
    {
        var generation = new BizHawkNetplay.Core.Net.SessionGeneration(77UL, 4);
        var body = ControlMessageCodec.EncodeInputOutage(generation, 3);
        Assert.True(ControlMessageCodec.TryDecodeInputOutage(body, out var decoded, out int port));
        Assert.Equal(generation, decoded);
        Assert.Equal(3, port);
        Assert.False(ControlMessageCodec.TryDecodeInputOutage(new byte[4], out _, out _));
    }
}
