using System;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// KI-17, disc half: a multi-disc set was identified from disc one, so two players holding the same
/// disc 1 and different disc 2s passed every check and diverged on the swap.
/// </summary>
public class DiscIdentityTests
{
    [Fact]
    public void NoDiscsIsNotAMismatch()
    {
        // Most systems have none. Two peers with none must match, and the digest must be empty
        // rather than a hash of nothing that could collide with a real one-disc set.
        Assert.Equal("", DiscIdentity.Digest(null));
        Assert.Equal("", DiscIdentity.Digest(Array.Empty<string>()));
        Assert.Null(DiscIdentity.Mismatch(null, null));
        Assert.Null(DiscIdentity.Mismatch(Array.Empty<string>(), null));
    }

    [Fact]
    public void TheSameSetInTheSameOrderMatches()
    {
        var a = new[] { "AAA", "BBB", "CCC" };
        var b = new[] { "AAA", "BBB", "CCC" };
        Assert.Equal(DiscIdentity.Digest(a), DiscIdentity.Digest(b));
        Assert.Null(DiscIdentity.Mismatch(a, b));
    }

    [Fact]
    public void ADifferentDiscTwoIsCaughtAndNamed()
    {
        // The finding, exactly: disc 1 matches, so the ROM hash — which for a disc game comes from
        // disc one — agreed and nothing else looked.
        var mine = new[] { "SAME", "MINE" };
        var theirs = new[] { "SAME", "THEIRS" };

        var why = DiscIdentity.Mismatch(mine, theirs);
        Assert.NotNull(why);
        Assert.Contains("disc 2 of 2", why!);
        Assert.Contains("earlier discs match", why!);
        Assert.NotEqual(DiscIdentity.Digest(mine), DiscIdentity.Digest(theirs));
    }

    [Fact]
    public void ADifferentDiscOneDoesNotClaimTheEarlierOnesMatched()
    {
        var why = DiscIdentity.Mismatch(new[] { "MINE", "SAME" }, new[] { "THEIRS", "SAME" });
        Assert.Contains("disc 1 of 2", why!);
        Assert.DoesNotContain("earlier discs match", why!);
    }

    [Fact]
    public void ADifferentCountIsItsOwnMessage()
    {
        var why = DiscIdentity.Mismatch(new[] { "A" }, new[] { "A", "B" });
        Assert.Contains("1 disc", why!);
        Assert.Contains("2 discs", why!);
        Assert.Contains("m3u", why!);   // the thing that actually fixes it
    }

    [Fact]
    public void OrderIsPartOfTheIdentity()
    {
        // Unlike the sibling machine domains, a differing order here is real: it is the order the
        // core was handed the discs, so it is what a disc-swap request indexes into. The same discs
        // in a different order load different content on a swap.
        var forwards = new[] { "A", "B" };
        var backwards = new[] { "B", "A" };
        Assert.NotEqual(DiscIdentity.Digest(forwards), DiscIdentity.Digest(backwards));
        Assert.NotNull(DiscIdentity.Mismatch(forwards, backwards));
    }

    [Fact]
    public void TheCountIsHashedSeparatelyFromTheContents()
    {
        // Without it, sets whose hashes happen to concatenate alike could collide. Cheap to
        // prevent, and the kind of thing that is obvious only after it happens.
        Assert.NotEqual(DiscIdentity.Digest(new[] { "AB" }), DiscIdentity.Digest(new[] { "A", "B" }));
    }

    [Fact]
    public void DiscHashesSurviveTheHandshakeRoundTripInOrder()
    {
        var id = new PeerIdentity(1, "ROM", "Nymashock", "2.11.1.0", "SYNC",
            new[] { "L0", "L1" }, true, 20,
            discHashes: new[] { "DISC-ONE", "DISC-TWO", "DISC-THREE" });

        var encoded = HandshakeCodec.Encode(id, new SessionPreferences(2, false), 47800, null);
        var (decoded, _, _, _, _) = HandshakeCodec.Decode(encoded);

        Assert.Equal(new[] { "DISC-ONE", "DISC-TWO", "DISC-THREE" }, decoded.DiscHashes);
    }

    [Fact]
    public void ANegotiationRefusesTheDivergentDiscAndNamesIt()
    {
        var mine = new PeerIdentity(1, "ROM", "Nymashock", "2.11.1.0", "SYNC",
            new[] { "L0", "L1" }, true, 20, discHashes: new[] { "SAME", "MINE" });
        var theirs = new PeerIdentity(1, "ROM", "Nymashock", "2.11.1.0", "SYNC",
            new[] { "L0", "L1" }, true, 20, discHashes: new[] { "SAME", "THEIRS" });

        var r = SessionNegotiator.Negotiate(mine, theirs,
            new SessionPreferences(2, false), new SessionPreferences(2, false));

        Assert.False(r.Accepted);
        Assert.Contains("disc 2 of 2", r.RejectReason);
    }
}
