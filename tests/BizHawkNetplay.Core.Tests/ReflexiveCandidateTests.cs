using System.Net;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// A peer's claim about its own public endpoint is an instruction to aim this machine — and every
/// other player's — at whatever it names. These pin what makes such a claim believable.
/// </summary>
public class ReflexiveCandidateTests
{
    private static IPEndPoint Ep(string address, int port = 47800) =>
        new(IPAddress.Parse(address), port);

    /// <summary>The working case, and the reason the port is not compared: a symmetric NAT hands
    /// out a fresh public port per destination, so the one STUN reported is not the one we see.</summary>
    [Theory]
    [InlineData(47800)]
    [InlineData(51000)]
    public void TheSameAddressIsBelievedWhateverPortItNames(int port)
    {
        Assert.True(ReflexiveCandidate.IsCredible(Ep("203.0.113.7", port), IPAddress.Parse("203.0.113.7")));
    }

    /// <summary>The attack: any address the sender likes, announced as its own, becomes a punch
    /// target for every player in the session and an input destination for the fallback path.</summary>
    [Fact]
    public void AnAddressThePeerDoesNotReachUsFromIsRefused()
    {
        Assert.False(ReflexiveCandidate.IsCredible(Ep("198.51.100.9"), IPAddress.Parse("203.0.113.7")));
    }

    /// <summary>A joiner on our own LAN announces its public address, which is nothing like the one
    /// we see it at. Refusing costs nothing — the route keeps the LAN endpoint, which is the only
    /// one that was ever going to work here.</summary>
    [Fact]
    public void ALanJoinersPublicAddressIsRefusedRatherThanRouted()
    {
        Assert.False(ReflexiveCandidate.IsCredible(Ep("203.0.113.7"), IPAddress.Parse("192.168.1.5")));
    }

    /// <summary>The mesh socket is IPv4 and can send nowhere else, so a v6 candidate is never a
    /// path to a player no matter who announced it.</summary>
    [Fact]
    public void AnIPv6CandidateIsRefused()
    {
        Assert.False(ReflexiveCandidate.IsCredible(
            new IPEndPoint(IPAddress.Parse("2001:db8::1"), 47800), IPAddress.Parse("203.0.113.7")));
    }

    /// <summary>An IPv4 connection accepted on a dual-stack socket is reported mapped. The peer is
    /// telling the truth; comparing the raw forms would call it a liar.</summary>
    [Fact]
    public void AnIPv4MappedControlAddressStillMatches()
    {
        Assert.True(ReflexiveCandidate.IsCredible(
            Ep("203.0.113.7"), IPAddress.Parse("203.0.113.7").MapToIPv6()));
    }

    [Fact]
    public void NothingAnnouncedIsNotSomethingToRouteTo()
    {
        Assert.False(ReflexiveCandidate.IsCredible(null, IPAddress.Parse("203.0.113.7")));
        Assert.False(ReflexiveCandidate.IsCredible(Ep("203.0.113.7"), null));
    }
}
