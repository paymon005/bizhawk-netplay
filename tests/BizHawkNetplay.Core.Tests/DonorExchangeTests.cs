using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The host's outstanding majority ask, and every way it can end.
///
/// This was two fields and a timer inside the tool form, with the transitions spread across four
/// methods that each had to remember the others. Two did not, and the result was a redundant
/// full-state resync — sixteen megabytes to every peer on N64, for a divergence already recovered
/// from, with a log line blaming a machine that had answered on time.
///
/// The sequence that produced it is <see cref="ASecondRecoveryWhileWaitingLeavesNothingArmed"/>.
/// It cannot be reached by any unit test of the pieces, only by driving the transitions in order —
/// which is why the state now lives in Core where they can be.
/// </summary>
public class DonorExchangeTests
{
    private static SessionGeneration Gen(int epoch) => new(0xABCDEF, epoch);

    [Fact]
    public void AFreshExchangeIsWaitingForNobody()
    {
        var donor = new DonorExchange();
        Assert.False(donor.IsWaiting);
        Assert.Equal(-1, donor.AwaitingPort);
        Assert.False(donor.Expire(out _));
    }

    [Fact]
    public void BeginRecordsTheSeatAndGeneration()
    {
        var donor = new DonorExchange();
        Assert.True(donor.Begin(2, Gen(5)));
        Assert.True(donor.IsWaiting);
        Assert.Equal(2, donor.AwaitingPort);
        Assert.Equal(Gen(5), donor.AwaitingGeneration);
    }

    [Fact]
    public void OnlyOneAskMayBeInFlight()
    {
        // A second request would be indistinguishable from the first when a reply arrived, and the
        // caller must not send one.
        var donor = new DonorExchange();
        Assert.True(donor.Begin(1, Gen(1)));
        Assert.False(donor.Begin(3, Gen(1)));
        Assert.Equal(1, donor.AwaitingPort);   // the original ask is untouched
    }

    [Fact]
    public void TheAnswerWeAskedForIsAdoptedAndEndsTheWait()
    {
        var donor = new DonorExchange();
        donor.Begin(2, Gen(7));
        Assert.Equal(OfferVerdict.Adopt, donor.Offer(2, Gen(7)));
        Assert.False(donor.IsWaiting);
    }

    [Fact]
    public void AnOfferFromAnotherSeatDoesNotCancelTheRealOne()
    {
        // A peer we did not ask must not be able to end a wait we are relying on — otherwise any
        // admitted peer could make the host fall back to its own state at will.
        var donor = new DonorExchange();
        donor.Begin(2, Gen(7));

        Assert.Equal(OfferVerdict.Unsolicited, donor.Offer(3, Gen(7)));
        Assert.True(donor.IsWaiting);
        Assert.Equal(2, donor.AwaitingPort);

        Assert.Equal(OfferVerdict.Adopt, donor.Offer(2, Gen(7)));   // the real one still lands
    }

    [Fact]
    public void AnOfferWhenNothingWasAskedIsUnsolicited()
    {
        var donor = new DonorExchange();
        Assert.Equal(OfferVerdict.Unsolicited, donor.Offer(2, Gen(7)));
    }

    [Fact]
    public void AStaleAnswerFromTheRightSeatStillEndsTheWait()
    {
        // The half that was missing. The peer HAS answered; its bytes are simply for a generation
        // the session has moved past. Discarding them is right — staying armed afterwards is what
        // let the timeout fire later and recover a second time.
        var donor = new DonorExchange();
        donor.Begin(2, Gen(7));

        Assert.Equal(OfferVerdict.Stale, donor.Offer(2, Gen(8)));
        Assert.False(donor.IsWaiting);
        Assert.False(donor.Expire(out _));   // and the timeout now finds nothing to do
    }

    [Fact]
    public void ASecondRecoveryWhileWaitingLeavesNothingArmed()
    {
        // Defect #3, in order:
        //   1. a desync asks P3 for its state
        //   2. a second desync arrives; the ask is refused (one at a time) and recovery falls
        //      through to the host's own state, advancing the generation
        //   3. P3's now-stale reply arrives
        //   4. the timeout fires
        // Step 4 used to run a whole second resync. Cancel at step 2 is what makes it a no-op.
        var donor = new DonorExchange();
        donor.Begin(3, Gen(4));

        Assert.False(donor.Begin(3, Gen(5)));   // step 2: refused...
        donor.Cancel();                          // ...and the fallback cancels what it supersedes

        Assert.Equal(OfferVerdict.Unsolicited, donor.Offer(3, Gen(4)));  // step 3
        Assert.False(donor.Expire(out _));                               // step 4: nothing to do
    }

    [Fact]
    public void ExpireReportsTheSeatOnceAndOnlyOnce()
    {
        // The timer can fire after something else ended the wait; only the first expiry is real.
        var donor = new DonorExchange();
        donor.Begin(1, Gen(2));

        Assert.True(donor.Expire(out int port));
        Assert.Equal(1, port);
        Assert.False(donor.Expire(out _));
        Assert.False(donor.IsWaiting);
    }

    [Fact]
    public void CancelIsIdempotent()
    {
        // Called from session teardown, from the fallback path, and from the offer path — some of
        // which overlap. Cancelling twice must not be different from cancelling once.
        var donor = new DonorExchange();
        donor.Begin(2, Gen(3));
        donor.Cancel();
        donor.Cancel();
        Assert.False(donor.IsWaiting);
        Assert.Equal(OfferVerdict.Unsolicited, donor.Offer(2, Gen(3)));
    }

    [Fact]
    public void ANegativeSeatIsNeverWaitedOn()
    {
        // -1 is the "nobody" sentinel; accepting it as a seat would make IsWaiting lie.
        var donor = new DonorExchange();
        Assert.False(donor.Begin(-1, Gen(1)));
        Assert.False(donor.IsWaiting);
    }

    [Fact]
    public void AnExchangeCanBeReusedForTheNextDesync()
    {
        var donor = new DonorExchange();
        donor.Begin(1, Gen(1));
        Assert.Equal(OfferVerdict.Adopt, donor.Offer(1, Gen(1)));

        Assert.True(donor.Begin(2, Gen(2)));
        Assert.Equal(2, donor.AwaitingPort);
        Assert.Equal(OfferVerdict.Adopt, donor.Offer(2, Gen(2)));
    }
}
