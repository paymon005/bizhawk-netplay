using System;
using System.Collections.Generic;
using System.Net;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Accepting an address nobody advertised, on the strength of a token.
///
/// This is the symmetric-NAT fix, and it is the one place the transport believes something it was
/// told rather than something it observed. Every peer holds every seat's token — that is what makes
/// a rejoin at a new address recognisable — so the token alone does not say which seat is speaking
/// honestly, and the rules that close that gap are what these cover. Getting one wrong is a way any
/// session member can point another member's seat at an address of its choosing and take them off
/// the mesh.
///
/// None of it was reachable without real sockets and real NAT behaviour before.
/// </summary>
public class MeshLearnedEndpointsTests
{
    private static byte[] Token(byte fill)
    {
        var token = new byte[MeshLearnedEndpoints.TokenBytes];
        for (int i = 0; i < token.Length; i++) token[i] = fill;
        return token;
    }

    private static byte[] Hello(byte[] token, int prefix = 0)
    {
        var buffer = new byte[prefix + token.Length];
        Buffer.BlockCopy(token, 0, buffer, prefix, token.Length);
        return buffer;
    }

    private static IPEndPoint Ep(string ip, int port) => new(IPAddress.Parse(ip), port);
    private static readonly IPEndPoint FromP1 = Ep("203.0.113.5", 51234);
    private static readonly IPEndPoint FromP1Again = Ep("203.0.113.5", 60000);

    private static (MeshLinkQuality quality, MeshLearnedEndpoints learned) Build()
    {
        var quality = new MeshLinkQuality();
        var learned = new MeshLearnedEndpoints(quality);
        learned.SetPeerTokens(new Dictionary<int, byte[]> { [1] = Token(0xA1), [2] = Token(0xB2) });
        return (quality, learned);
    }

    // ---------------------------------------------------------------- the token gate

    [Fact]
    public void AValidTokenBindsTheSeatToWhereItReallyCameFrom()
    {
        var (_, learned) = Build();
        Assert.True(learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0));
        Assert.True(learned.TryGet(1, out var bound));
        Assert.Equal(FromP1, bound);
        Assert.True(learned.TryGetSeat(FromP1, out int seat));
        Assert.Equal(1, seat);
        Assert.Equal(1, learned.Count);
    }

    [Fact]
    public void AnUnknownTokenBindsNothing()
    {
        var (_, learned) = Build();
        Assert.False(learned.TryLearn(Hello(Token(0xFF)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0));
        Assert.Equal(0, learned.Count);
        Assert.False(learned.IsLearned(FromP1));
    }

    /// <summary>
    /// A single wrong byte is a wrong token. Worth pinning explicitly because the comparison is
    /// hand-written to be constant time, which is exactly the shape of code an off-by-one hides in.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(MeshLearnedEndpoints.TokenBytes - 1)]
    public void ATokenWrongInOneByteIsRefused(int at)
    {
        var (_, learned) = Build();
        var token = Token(0xA1);
        token[at] ^= 0x01;
        Assert.False(learned.TryLearn(Hello(token), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0));
    }

    [Fact]
    public void ATruncatedAnnouncementIsRefusedRatherThanReadPastItsEnd()
    {
        var (_, learned) = Build();
        var buffer = Hello(Token(0xA1));
        for (int available = 0; available < MeshLearnedEndpoints.TokenBytes; available++)
            Assert.False(learned.TryLearn(buffer, 0, available, FromP1, 0));
    }

    [Fact]
    public void TheTokenIsReadAtTheOffsetItActuallySitsAt()
    {
        // The caller passes the header size; reading from 0 would compare header bytes to a token.
        var (_, learned) = Build();
        const int header = 6;
        var buffer = Hello(Token(0xA1), prefix: header);
        Assert.False(learned.TryLearn(buffer, 0, buffer.Length, FromP1, 0));
        Assert.True(learned.TryLearn(buffer, header, buffer.Length, FromP1, 0));
    }

    // ---------------------------------------------------------------- who may move a seat

    [Fact]
    public void RepeatingAKnownBindingIsAcceptedAndChangesNothing()
    {
        var (_, learned) = Build();
        learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0);
        Assert.True(learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 5000));
        Assert.Equal(1, learned.Count);
    }

    /// <summary>
    /// A seat that is demonstrably still answering is not moved on somebody else's say-so.
    ///
    /// Every peer holds every seat's token, so without this rule any session member could announce
    /// P1's token from its own address and take P1 off the mesh — the transport would then send P1's
    /// input to the attacker and nothing to P1.
    /// </summary>
    [Fact]
    public void AnAnsweringSeatIsNotStolen()
    {
        var (quality, learned) = Build();
        learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0);
        quality.MarkHeard(FromP1, 1000);   // it is provably still there

        var thief = Ep("198.51.100.7", 40000);
        Assert.False(learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, thief, 1000));
        Assert.True(learned.TryGet(1, out var still));
        Assert.Equal(FromP1, still);
    }

    /// <summary>
    /// A seat that has gone quiet DOES rebind — which is the case the whole mechanism exists for.
    /// A NAT rebinding mid-session arrives at a seat that has just stopped answering.
    /// </summary>
    [Fact]
    public void ASeatThatHasGoneQuietRebinds()
    {
        var (quality, learned) = Build();
        learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0);
        quality.MarkHeard(FromP1, 0);

        long later = MeshLinkQuality.FreshWindowMs + 1;
        Assert.True(learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1Again, later));
        Assert.True(learned.TryGet(1, out var bound));
        Assert.Equal(FromP1Again, bound);
        // The old address stops being a peer in every table, not just this one.
        Assert.False(learned.IsLearned(FromP1));
        Assert.False(quality.IsAlive(FromP1, later));
    }

    // ---------------------------------------------------------------- retiring claims

    /// <summary>
    /// A binding that never answers a probe is retired.
    ///
    /// Learning is a claim rather than a conclusion. Without expiry, one spoofed announcement has
    /// this node probing a stranger's address for the rest of the session — traffic aimed at a
    /// stranger by a stranger.
    /// </summary>
    [Fact]
    public void AClaimThatNeverAnswersIsForgotten()
    {
        var (_, learned) = Build();
        learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0);

        learned.PruneUnproven(MeshLearnedEndpoints.UnprovenExpiryMs - 1);
        Assert.Equal(1, learned.Count);

        learned.PruneUnproven(MeshLearnedEndpoints.UnprovenExpiryMs);
        Assert.Equal(0, learned.Count);
        Assert.False(learned.IsLearned(FromP1));
    }

    [Fact]
    public void ABindingThatProvedItselfIsKeptForever()
    {
        var (quality, learned) = Build();
        learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0);
        quality.MarkHeard(FromP1, 0);   // it answered a probe once

        learned.PruneUnproven(MeshLearnedEndpoints.UnprovenExpiryMs * 100);
        Assert.Equal(1, learned.Count);
    }

    // ---------------------------------------------------------------- token rotation

    /// <summary>
    /// Rotating a seat's token retires what was learned under the old one.
    ///
    /// Keeping it is not the conservative choice, it is the failure: the old occupant is usually
    /// still in the session on another seat, still answering from that endpoint, so the stale
    /// binding stays alive indefinitely and the seat's next genuine occupant is refused forever.
    /// </summary>
    [Fact]
    public void RotatingASeatsTokenRetiresWhatItLearned()
    {
        var (quality, learned) = Build();
        learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0);
        quality.MarkHeard(FromP1, 0);

        learned.SetPeerTokens(new Dictionary<int, byte[]> { [1] = Token(0xC3), [2] = Token(0xB2) });

        Assert.False(learned.TryGet(1, out _));
        Assert.False(quality.IsAlive(FromP1, 0));   // and it stops counting as a live path
        // The seat's next genuine occupant is now accepted, which the stale binding prevented.
        Assert.True(learned.TryLearn(Hello(Token(0xC3)), 0, MeshLearnedEndpoints.TokenBytes,
            Ep("198.51.100.20", 40000), 0));
    }

    [Fact]
    public void ASeatWhoseTokenDidNotChangeKeepsItsBinding()
    {
        var (_, learned) = Build();
        learned.TryLearn(Hello(Token(0xB2)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0);
        learned.SetPeerTokens(new Dictionary<int, byte[]> { [1] = Token(0xC3), [2] = Token(0xB2) });
        Assert.True(learned.TryGet(2, out var bound));
        Assert.Equal(FromP1, bound);
    }

    [Fact]
    public void ClearingTheTokensRetiresEveryBinding()
    {
        var (_, learned) = Build();
        learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0);
        learned.SetPeerTokens(null);
        Assert.Equal(0, learned.Count);
        Assert.False(learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0));
    }

    // ---------------------------------------------------------------- the local token

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void ATokenOfTheWrongLengthIsNotATokenAtAll(int length)
    {
        var (_, learned) = Build();
        learned.SetLocalToken(new byte[length]);
        Assert.Null(learned.LocalToken);

        learned.SetPeerTokens(new Dictionary<int, byte[]> { [1] = new byte[length] });
        Assert.False(learned.TryLearn(new byte[64], 0, 64, FromP1, 0));
    }

    [Fact]
    public void TheLocalTokenIsCopiedRatherThanAliased()
    {
        // The caller's array comes off the wire and is reused; aliasing it would let a later frame
        // silently change what this node announces itself as.
        var (_, learned) = Build();
        var mine = Token(0x5A);
        learned.SetLocalToken(mine);
        mine[0] = 0x00;
        Assert.Equal(0x5A, learned.LocalToken![0]);
    }

    [Fact]
    public void ASeatLeavingTakesItsBindingWithIt()
    {
        var (_, learned) = Build();
        learned.TryLearn(Hello(Token(0xA1)), 0, MeshLearnedEndpoints.TokenBytes, FromP1, 0);
        learned.ForgetSeat(1);
        Assert.Equal(0, learned.Count);
        Assert.False(learned.IsLearned(FromP1));
    }
}
