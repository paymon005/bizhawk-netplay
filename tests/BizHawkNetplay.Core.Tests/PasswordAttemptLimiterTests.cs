using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// How often one address may make the host check a password.
///
/// The cost being metered is one PBKDF2 derivation — about a second on the .NET Framework build
/// that ships — and the host's accept loop is serial, so the harm is not mainly CPU: it is a lobby
/// nobody can join while a stranger who does not know the password keeps knocking.
///
/// The failure mode on the other side is worse than the attack, which is why the burst is generous:
/// a limiter that turned real players away would break joining to prevent an inconvenience.
/// </summary>
public class PasswordAttemptLimiterTests
{
    private const string Somebody = "203.0.113.7";

    [Fact]
    public void AFirstAttemptFromAnUnknownAddressIsAllowed()
    {
        var limiter = new PasswordAttemptLimiter();
        Assert.True(limiter.TryBeginAttempt(Somebody, 0));
        Assert.Equal(0, limiter.Refused);
    }

    /// <summary>
    /// A whole lobby arriving from one household address at once gets through.
    ///
    /// Four players behind one public IP joining within seconds of each other is ordinary, and a
    /// couple of them will mistype. That is the burst the allowance is sized for.
    /// </summary>
    [Fact]
    public void AWholeLobbyFromOneAddressGetsThrough()
    {
        var limiter = new PasswordAttemptLimiter();
        for (int i = 0; i < PasswordAttemptLimiter.BurstAllowance; i++)
            Assert.True(limiter.TryBeginAttempt(Somebody, 0), $"attempt {i + 1} was refused");
    }

    [Fact]
    public void PastTheBurstTheAddressIsRefused()
    {
        var limiter = new PasswordAttemptLimiter();
        for (int i = 0; i < PasswordAttemptLimiter.BurstAllowance; i++)
            limiter.TryBeginAttempt(Somebody, 0);

        Assert.False(limiter.TryBeginAttempt(Somebody, 0));
        Assert.False(limiter.TryBeginAttempt(Somebody, 0));
        Assert.Equal(2, limiter.Refused);
    }

    [Fact]
    public void TheAllowanceComesBackWithTime()
    {
        var limiter = new PasswordAttemptLimiter();
        for (int i = 0; i < PasswordAttemptLimiter.BurstAllowance; i++)
            limiter.TryBeginAttempt(Somebody, 0);
        Assert.False(limiter.TryBeginAttempt(Somebody, 0));

        // One attempt back every five seconds.
        Assert.False(limiter.TryBeginAttempt(Somebody, 4));
        Assert.True(limiter.TryBeginAttempt(Somebody, 5));
        Assert.False(limiter.TryBeginAttempt(Somebody, 5));
    }

    [Fact]
    public void TheAllowanceNeverRefillsPastItsBurst()
    {
        var limiter = new PasswordAttemptLimiter();
        limiter.TryBeginAttempt(Somebody, 0);
        // An hour later it is back to a full burst and no more — otherwise a patient attacker
        // banks capacity and spends it all at once.
        for (int i = 0; i < PasswordAttemptLimiter.BurstAllowance; i++)
            Assert.True(limiter.TryBeginAttempt(Somebody, 3600), $"attempt {i + 1} refused");
        Assert.False(limiter.TryBeginAttempt(Somebody, 3600));
    }

    /// <summary>
    /// Metering is per address. One player hammering the door must not close it on everyone else,
    /// which would turn a nuisance into the outage it was trying to cause.
    /// </summary>
    [Fact]
    public void OneNoisyAddressDoesNotMeterAnother()
    {
        var limiter = new PasswordAttemptLimiter();
        for (int i = 0; i < PasswordAttemptLimiter.BurstAllowance + 5; i++)
            limiter.TryBeginAttempt(Somebody, 0);
        Assert.False(limiter.TryBeginAttempt(Somebody, 0));

        Assert.True(limiter.TryBeginAttempt("198.51.100.2", 0));
    }

    /// <summary>
    /// Getting in forgives what it took. Someone who mistyped four times is a player, not a threat,
    /// and should not arrive at their next session already half-metered.
    /// </summary>
    [Fact]
    public void SucceedingRestoresTheFullAllowance()
    {
        var limiter = new PasswordAttemptLimiter();
        for (int i = 0; i < PasswordAttemptLimiter.BurstAllowance; i++)
            limiter.TryBeginAttempt(Somebody, 0);
        Assert.False(limiter.TryBeginAttempt(Somebody, 0));

        limiter.RecordSuccess(Somebody);
        for (int i = 0; i < PasswordAttemptLimiter.BurstAllowance; i++)
            Assert.True(limiter.TryBeginAttempt(Somebody, 0));
    }

    [Fact]
    public void SucceedingFromAnAddressNobodyIsTrackingIsHarmless()
    {
        var limiter = new PasswordAttemptLimiter();
        limiter.RecordSuccess("198.51.100.9");   // e.g. a punched joiner that never took this path
        Assert.True(limiter.TryBeginAttempt("198.51.100.9", 0));
    }

    /// <summary>
    /// The table is keyed by something a remote party chooses, so it is bounded — otherwise it is
    /// its own slow memory leak, reachable by anyone who can open a socket.
    /// </summary>
    [Fact]
    public void TheTableOfTrackedAddressesIsBounded()
    {
        var limiter = new PasswordAttemptLimiter(maxTrackedSources: 16);
        for (int i = 0; i < 500; i++) limiter.TryBeginAttempt($"10.0.{i / 256}.{i % 256}", i);
        Assert.True(limiter.TrackedSources <= 16, $"tracked {limiter.TrackedSources} addresses");
    }

    /// <summary>
    /// Filling the table does not reset a metered address.
    ///
    /// Forgetting an address restores it to a full burst, so evicting a SPENT entry hands back the
    /// allowance it just used — and an attacker with a handful of addresses could refill its own
    /// meter by pushing the table over its bound. The entry with the most allowance left goes
    /// instead, which costs nothing because a fresh bucket starts full.
    ///
    /// The first version of this evicted the least recently seen, and this test caught it: with
    /// several entries seen in the same instant, "oldest" is whatever the dictionary enumerated
    /// first, which was sometimes the one being metered.
    /// </summary>
    [Fact]
    public void FillingTheTableDoesNotResetAMeteredAddress()
    {
        var limiter = new PasswordAttemptLimiter(burst: 2, maxTrackedSources: 4);
        limiter.TryBeginAttempt(Somebody, 0);
        limiter.TryBeginAttempt(Somebody, 0);
        Assert.False(limiter.TryBeginAttempt(Somebody, 0));   // spent

        // Far more addresses than the table holds, all in the same instant — so nothing can be
        // told apart by recency, only by what it has left.
        for (int i = 0; i < 40; i++) limiter.TryBeginAttempt($"10.0.0.{i}", 0);

        Assert.False(limiter.TryBeginAttempt(Somebody, 0),
            "filling the table gave the metered address its allowance back");
    }

    /// <summary>
    /// A clock that goes backwards cannot mint attempts. Not hypothetical: the limiter is fed a
    /// monotonic reading precisely so a system-time change cannot widen it, and this pins that the
    /// arithmetic does not undo that on its own.
    /// </summary>
    [Fact]
    public void TimeGoingBackwardsDoesNotMintAttempts()
    {
        var limiter = new PasswordAttemptLimiter();
        for (int i = 0; i < PasswordAttemptLimiter.BurstAllowance; i++)
            limiter.TryBeginAttempt(Somebody, 1000);
        Assert.False(limiter.TryBeginAttempt(Somebody, 1000));
        Assert.False(limiter.TryBeginAttempt(Somebody, 0));       // an hour earlier
        Assert.False(limiter.TryBeginAttempt(Somebody, -5000));
    }

    [Fact]
    public void ANewLobbyStartsEveryAddressEven()
    {
        var limiter = new PasswordAttemptLimiter();
        for (int i = 0; i < PasswordAttemptLimiter.BurstAllowance + 3; i++)
            limiter.TryBeginAttempt(Somebody, 0);
        Assert.True(limiter.Refused > 0);

        limiter.Clear();
        Assert.True(limiter.TryBeginAttempt(Somebody, 0));
        Assert.Equal(0, limiter.Refused);
        Assert.Equal(0, limiter.TrackedSources - 1);   // only the attempt just made
    }

    /// <summary>
    /// The sustained rate is what actually bounds the damage, so it is stated as a number rather
    /// than left to be inferred: a hostile address gets twelve derivations a minute.
    /// </summary>
    [Fact]
    public void ASustainedFloodIsMeteredToTheRefillRate()
    {
        var limiter = new PasswordAttemptLimiter();
        int allowed = 0;
        // One attempt every 100ms for a minute, from one address.
        for (int tick = 0; tick < 600; tick++)
            if (limiter.TryBeginAttempt(Somebody, tick * 0.1)) allowed++;

        int expected = PasswordAttemptLimiter.BurstAllowance
                     + (int)(60 * PasswordAttemptLimiter.RefillPerSecond);
        Assert.InRange(allowed, expected - 1, expected + 1);
        Assert.True(allowed < 25, $"{allowed} derivations a minute is not metered");
    }
}
