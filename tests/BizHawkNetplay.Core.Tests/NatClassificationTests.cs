using System.Net;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The one network failure this tool cannot work around, and until now could not name. A symmetric
/// NAT hands out a fresh public port per destination, so the address a peer is told to aim at was
/// only ever valid for the STUN server that reported it — every punch candidate is dead before it
/// is tried. The session fails exactly as a bad link fails, silence then a timeout, which is the
/// worst possible presentation of a permanent condition.
/// </summary>
public class NatClassificationTests
{
    private static IPEndPoint Ep(string addr, int port) => new(IPAddress.Parse(addr), port);

    [Fact]
    public void OneMappingForEveryDestinationIsPunchable()
    {
        var m = StunClient.Classify(Ep("203.0.113.7", 50000), Ep("203.0.113.7", 50000));
        Assert.Equal(NatMapping.EndpointIndependent, m);
    }

    [Fact]
    public void ADifferentPortPerDestinationIsSymmetric()
    {
        // The common shape: same public address, fresh port per destination. Carrier-grade NAT.
        var m = StunClient.Classify(Ep("203.0.113.7", 50000), Ep("203.0.113.7", 50001));
        Assert.Equal(NatMapping.Symmetric, m);
    }

    [Fact]
    public void ADifferentAddressPerDestinationIsAlsoSymmetric()
    {
        // Rarer, but a NAT pool behaves the same way for our purposes: the peer cannot aim.
        var m = StunClient.Classify(Ep("203.0.113.7", 50000), Ep("198.51.100.4", 50000));
        Assert.Equal(NatMapping.Symmetric, m);
    }

    [Fact]
    public void AMissingAnswerIsUnknown_NeverAPassingVerdict()
    {
        // Failing open here would tell a player their router is fine on the evidence of a timeout,
        // which is the same mistake as advising a control that cannot change the outcome.
        Assert.Equal(NatMapping.Unknown, StunClient.Classify(null, Ep("203.0.113.7", 50000)));
        Assert.Equal(NatMapping.Unknown, StunClient.Classify(Ep("203.0.113.7", 50000), null));
        Assert.Equal(NatMapping.Unknown, StunClient.Classify(null, null));
    }

    [Fact]
    public void TheSymmetricMessageNamesWhatTheyCanActuallyDo()
    {
        var r = new NatReport(NatMapping.Symmetric, Ep("203.0.113.7", 50000), Ep("203.0.113.7", 50001));
        string s = r.Describe();

        Assert.Contains("SYMMETRIC", s);
        Assert.Contains("50000", s);
        Assert.Contains("50001", s);
        // It must not overstate the damage: joining a forwarded host still works, because the
        // joiner opens that path itself. Only punch and the joiner-to-joiner legs are lost.
        Assert.Contains("still works", s);
        Assert.Contains("forwarded a port", s);
        // The actions available, and honesty that the one that would fix the rest does not exist.
        Assert.Contains("forward a UDP port and host", s);
        Assert.Contains("not built", s);
        Assert.True(r.IsSymmetric);
    }

    [Fact]
    public void AnUnknownVerdictSaysSoRatherThanImplyingOne()
    {
        string s = default(NatReport).Describe();
        Assert.Contains("not a verdict", s);
        Assert.DoesNotContain("SYMMETRIC", s);
        Assert.False(default(NatReport).IsSymmetric);
    }
}
