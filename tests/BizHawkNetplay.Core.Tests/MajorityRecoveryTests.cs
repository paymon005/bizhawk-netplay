using System.Collections.Generic;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// KI-20: recovery distributes the host's state, so a lone-diverged host overwrites the players who
/// were right. v0.32.1 made that visible; this is the part that can act on it.
/// </summary>
public class MajorityRecoveryTests
{
    private static DesyncPartition Split(params (int Port, uint Hash)[] reports)
    {
        var map = new Dictionary<int, uint>();
        foreach (var r in reports) map[r.Port] = r.Hash;
        return DesyncPartition.FromReports(frame: 300, map);
    }

    [Fact]
    public void ADivergedHostDefersToTheLowestPortInTheMajority()
    {
        // Three machines agree, the host does not. The donor is port 1 — the lowest port in the
        // agreeing group, chosen by a rule both ends can compute rather than by merit. Reported out
        // of order on purpose: the grouping sorts, so the answer must not depend on arrival order.
        var partition = Split((0, 0xBBBBBBBB), (2, 0xAAAAAAAA), (1, 0xAAAAAAAA), (3, 0xAAAAAAAA));

        Assert.True(partition.HostIsOutvoted);
        Assert.Equal(1, partition.ChooseDonor());
        Assert.Equal(1, MajorityRecovery.SelectDonor(partition, optedIn: true));
    }

    [Fact]
    public void WithoutTheOptInTheHostStaysAuthoritative()
    {
        // Off by default: deferring means the host loads a peer's savestate, which is a trust
        // decision rather than a setting. See MajorityRecovery for the whole argument.
        var partition = Split((0, 0xBBBBBBBB), (1, 0xAAAAAAAA), (2, 0xAAAAAAAA), (3, 0xAAAAAAAA));
        Assert.Equal(-1, MajorityRecovery.SelectDonor(partition, optedIn: false));
    }

    [Fact]
    public void ATieIsNotAMajorityAndNobodyIsDeferredTo()
    {
        // Two against two. Handing authority to one half would invent a verdict the evidence does
        // not support — the same reasoning HostIsOutvoted already applies.
        var partition = Split((0, 0xAAAAAAAA), (1, 0xAAAAAAAA), (2, 0xBBBBBBBB), (3, 0xBBBBBBBB));
        Assert.False(partition.HostIsOutvoted);
        Assert.Equal(-1, partition.ChooseDonor());
        Assert.Equal(-1, MajorityRecovery.SelectDonor(partition, optedIn: true));
    }

    [Fact]
    public void TwoPlayersNeverProduceADonor()
    {
        // With two machines there is no majority to defer to, whichever one is wrong.
        var partition = Split((0, 0xAAAAAAAA), (1, 0xBBBBBBBB));
        Assert.Equal(-1, MajorityRecovery.SelectDonor(partition, optedIn: true));
    }

    [Fact]
    public void AHostInTheMajorityKeepsAuthority()
    {
        var partition = Split((0, 0xAAAAAAAA), (1, 0xAAAAAAAA), (2, 0xAAAAAAAA), (3, 0xBBBBBBBB));
        Assert.False(partition.HostIsOutvoted);
        Assert.Equal(-1, MajorityRecovery.SelectDonor(partition, optedIn: true));
    }

    [Fact]
    public void ThreeWaySplitWithNoMajorityDefersToNobody()
    {
        // Everyone disagrees with everyone. The largest group is one machine, which is not a
        // majority over the host's one machine, so there is nothing to defer to.
        var partition = Split((0, 0xAAAAAAAA), (1, 0xBBBBBBBB), (2, 0xCCCCCCCC));
        Assert.Equal(-1, MajorityRecovery.SelectDonor(partition, optedIn: true));
    }

    [Fact]
    public void TheDeclinedMessageNamesTheSettingThatWouldChangeIt()
    {
        var partition = Split((0, 0xBBBBBBBB), (1, 0xAAAAAAAA), (2, 0xAAAAAAAA));
        var declined = MajorityRecovery.DescribeDeclined(partition);
        Assert.Contains("ONLY one reporting", declined);
        Assert.Contains("Defer to the majority", declined);
        // Since v0.36.0 the setting is ON by default, so reaching this message means somebody
        // deliberately turned it off. The message therefore points at the setting they changed —
        // it used to sell the feature and explain why it was not simply on, which would now be
        // telling a host something it already decided.
        Assert.Contains("on by default", declined);
        Assert.Contains("unticked", declined);
    }

    [Fact]
    public void TheDeferredMessageSaysWhoseStateIsAboutToWin()
    {
        var partition = Split((0, 0xBBBBBBBB), (1, 0xAAAAAAAA), (2, 0xAAAAAAAA));
        var describe = MajorityRecovery.Describe(partition, donor: 1);
        Assert.Contains("P2's state becomes the session's", describe);
        Assert.Contains("local to the host", describe);
    }

    // ---------------------------------------------------------------- wire

    [Fact]
    public void AStateRequestCarriesItsGenerationAndNothingElse()
    {
        var generation = new SessionGeneration(0xDEADBEEF, 7);
        var body = ControlMessageCodec.EncodeStateRequest(generation);

        Assert.True(ControlMessageCodec.TryDecodeStateRequest(body, out var decoded));
        Assert.Equal(generation, decoded);
        // No frame: the donor is running, so any frame the host could name is already behind it.
        Assert.Equal(ControlMessageCodec.GenerationSize, body.Length);
    }

    [Fact]
    public void AStateOfferRoundTripsItsPayloadUntouched()
    {
        var generation = new SessionGeneration(0xFEEDFACE, 3);
        var packed = StateCompression.Pack(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var body = ControlMessageCodec.EncodeStateOffer(generation, packed);

        Assert.True(ControlMessageCodec.TryDecodeStateOffer(body, out var decoded, out var back));
        Assert.Equal(generation, decoded);
        Assert.Equal(packed, back);
        // Unpacking is the caller's, with its own bound, so one place decides how large a state may
        // be rather than two that could disagree.
        Assert.True(StateCompression.TryUnpack(back, ControlMessageCodec.MaxStateBytes, out var state));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, state);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(11)]
    public void ATruncatedStateOfferIsRefusedRatherThanRead(int length)
    {
        Assert.False(ControlMessageCodec.TryDecodeStateOffer(new byte[length], out _, out _));
    }

    [Fact]
    public void AnEmptyStateOfferDecodesToAnEmptyPayloadRatherThanThrowing()
    {
        // Exactly a generation and no state. Untrusted input: it must decode to something the
        // caller then fails to unpack, not throw out of the reader thread.
        var body = ControlMessageCodec.EncodeStateOffer(new SessionGeneration(1, 1), []);
        Assert.True(ControlMessageCodec.TryDecodeStateOffer(body, out _, out var packed));
        Assert.Empty(packed);
        Assert.False(StateCompression.TryUnpack(packed, ControlMessageCodec.MaxStateBytes, out _));
    }
}
