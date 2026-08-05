using System.Collections.Generic;
using System.Net;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Which of a peer's addresses input goes to, and what counts as a path being up.
///
/// This is the most consequential decision in the transport and it had no test, because reaching it
/// meant real sockets, real NAT behaviour and real timing. Pick a dead address and that peer's
/// input stops with no error anywhere — the session simply starts stalling and rolling back, and
/// every diagnostic says the network is fine.
///
/// The clock is a parameter here, which is the whole reason these cases can exist: "heard from
/// 3 seconds ago" is an argument rather than a wait.
/// </summary>
public class MeshLinkQualityTests
{
    private static IPEndPoint Ep(int port) => new(IPAddress.Parse("10.0.0.1"), port);
    private static readonly IPEndPoint Lan = Ep(4000);
    private static readonly IPEndPoint Reflexive = Ep(4001);
    private static readonly IPEndPoint Learned = new(IPAddress.Parse("203.0.113.9"), 51234);

    private static IReadOnlyList<IPEndPoint> Candidates(params IPEndPoint[] eps) => eps;

    // ---------------------------------------------------------------- liveness

    [Fact]
    public void NothingIsAliveUntilSomethingArrives()
    {
        var q = new MeshLinkQuality();
        Assert.False(q.IsAlive(Lan, 0));
        Assert.False(q.IsFresh(Lan, 0));
        Assert.False(q.HasBeenHeard(Lan));
    }

    [Fact]
    public void APathStaysAliveForItsWindowAndNoLonger()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Lan, 1000);
        Assert.True(q.IsAlive(Lan, 1000 + MeshLinkQuality.AliveWindowMs - 1));
        Assert.False(q.IsAlive(Lan, 1000 + MeshLinkQuality.AliveWindowMs));
    }

    /// <summary>
    /// Fresh is strictly stricter than alive, and the gap between them is where failover happens.
    ///
    /// A path that dies mid-session keeps its stale, low RTT and stays inside the alive window for
    /// seconds. Without a stricter question to ask, input stays pinned to it — a wait that races
    /// the UDP-lost session watchdog.
    /// </summary>
    [Fact]
    public void FreshIsStricterThanAlive()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Lan, 0);
        long between = MeshLinkQuality.FreshWindowMs + 1;
        Assert.True(MeshLinkQuality.FreshWindowMs < MeshLinkQuality.AliveWindowMs);
        Assert.True(q.IsAlive(Lan, between));
        Assert.False(q.IsFresh(Lan, between));
    }

    // ---------------------------------------------------------------- selection

    [Fact]
    public void WithNothingConfirmedThereIsNoSafeGuess()
    {
        // Null tells the caller to broadcast to every candidate until a punch confirms one.
        var q = new MeshLinkQuality();
        Assert.Null(q.Select(1, Candidates(Lan, Reflexive), learned: null, 0));
    }

    [Fact]
    public void TheOnlyLiveCandidateIsChosen()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Reflexive, 1000);
        Assert.Same(Reflexive, q.Select(1, Candidates(Lan, Reflexive), null, 1000));
    }

    [Fact]
    public void AmongEquallyFreshCandidatesTheFastestWins()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Lan, 1000);
        q.MarkHeard(Reflexive, 1000);
        q.RecordRtt(Lan, 120);
        q.RecordRtt(Reflexive, 20);
        Assert.Same(Reflexive, q.Select(1, Candidates(Lan, Reflexive), null, 1000));
    }

    /// <summary>
    /// A recently-heard candidate beats a faster one that has gone quiet.
    ///
    /// This is the failover rule, and it is the one an RTT-only ranking gets wrong: the dead path's
    /// last measurement is not merely stale, it is the BEST number in the table, because it was
    /// taken while the path still worked.
    /// </summary>
    [Fact]
    public void ARecentSlowPathBeatsAQuietFastOne()
    {
        var q = new MeshLinkQuality();
        long now = 100_000;
        q.MarkHeard(Lan, now - (MeshLinkQuality.FreshWindowMs + 500));   // alive, not fresh
        q.RecordRtt(Lan, 5);                                             // and it looks fastest
        q.MarkHeard(Reflexive, now);                                     // fresh
        q.RecordRtt(Reflexive, 200);

        Assert.True(q.IsAlive(Lan, now), "the quiet path must still be inside the alive window");
        Assert.Same(Reflexive, q.Select(1, Candidates(Lan, Reflexive), null, now));
    }

    [Fact]
    public void AnUnmeasuredFreshCandidateBeatsAMeasuredStaleOne()
    {
        var q = new MeshLinkQuality();
        long now = 100_000;
        q.MarkHeard(Lan, now - (MeshLinkQuality.FreshWindowMs + 500));
        q.RecordRtt(Lan, 5);
        q.MarkHeard(Reflexive, now);   // fresh, never measured
        Assert.Same(Reflexive, q.Select(1, Candidates(Lan, Reflexive), null, now));
    }

    /// <summary>
    /// A live learned endpoint outranks every advertised candidate.
    ///
    /// For a symmetric-NAT peer none of the advertised candidates can ever work — its router gives
    /// a different public port per destination — so without this the learning would be recorded and
    /// never used, which is the whole point of learning it.
    /// </summary>
    [Fact]
    public void ALiveLearnedEndpointOutranksEverything()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Lan, 1000);
        q.RecordRtt(Lan, 1);            // the advertised path looks perfect
        q.MarkHeard(Learned, 1000);
        q.RecordRtt(Learned, 500);      // and the learned one looks terrible
        Assert.Same(Learned, q.Select(1, Candidates(Lan, Reflexive), Learned, 1000));
    }

    [Fact]
    public void ALearnedEndpointThatHasGoneQuietDoesNotOutrankALivePath()
    {
        var q = new MeshLinkQuality();
        long now = 100_000;
        q.MarkHeard(Learned, now - MeshLinkQuality.AliveWindowMs);   // expired
        q.MarkHeard(Lan, now);
        Assert.Same(Lan, q.Select(1, Candidates(Lan, Reflexive), Learned, now));
    }

    /// <summary>
    /// When liveness lapses entirely, keep sending where it last worked.
    ///
    /// Falling back to "first advertised candidate" is actively wrong on the internet: that is
    /// typically the pre-NAT LAN address, which is exactly the one that does NOT work when the
    /// reflexive path was carrying the session.
    /// </summary>
    [Fact]
    public void AfterLivenessLapsesTheLastWorkingPathIsKept()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Reflexive, 1000);
        Assert.Same(Reflexive, q.Select(1, Candidates(Lan, Reflexive), null, 1000));

        long later = 1000 + MeshLinkQuality.AliveWindowMs;   // everything has expired
        Assert.Same(Reflexive, q.Select(1, Candidates(Lan, Reflexive), null, later));
    }

    /// <summary>
    /// The learned endpoint counts as a valid fallback even though it is never among the
    /// candidates — for a symmetric-NAT peer it is the only address that works, and rejecting it
    /// stopped input to that peer entirely the moment its liveness lapsed.
    /// </summary>
    [Fact]
    public void TheLastWorkingPathMayBeALearnedEndpoint()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Learned, 1000);
        Assert.Same(Learned, q.Select(1, Candidates(Lan), Learned, 1000));

        long later = 1000 + MeshLinkQuality.AliveWindowMs;
        Assert.Same(Learned, q.Select(1, Candidates(Lan), Learned, later));
    }

    /// <summary>
    /// A remembered path that is no longer this peer's is not resurrected. A rejoin can change
    /// every address, and sending to the previous occupant's endpoint is worse than broadcasting.
    /// </summary>
    [Fact]
    public void AFallbackIsRefusedOnceThePathIsNotThisPeersAnyMore()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Reflexive, 1000);
        q.Select(1, Candidates(Lan, Reflexive), null, 1000);

        long later = 1000 + MeshLinkQuality.AliveWindowMs;
        Assert.Null(q.Select(1, Candidates(Ep(9999)), null, later));
    }

    [Fact]
    public void EachPeerRemembersItsOwnPath()
    {
        var q = new MeshLinkQuality();
        var other = Ep(5000);
        q.MarkHeard(Reflexive, 1000);
        q.MarkHeard(other, 1000);
        q.Select(1, Candidates(Reflexive), null, 1000);
        q.Select(2, Candidates(other), null, 1000);

        Assert.True(q.TryGetLastSelected(1, out var forOne));
        Assert.True(q.TryGetLastSelected(2, out var forTwo));
        Assert.Same(Reflexive, forOne);
        Assert.Same(other, forTwo);
    }

    // ---------------------------------------------------------------- measurement

    [Fact]
    public void TheSmoothedFigureMovesTowardNewSamplesRatherThanJumpingToThem()
    {
        var q = new MeshLinkQuality();
        q.RecordRtt(Lan, 100);
        Assert.True(q.TryGetRtt(Lan, out double first));
        Assert.Equal(100, first, 3);

        q.RecordRtt(Lan, 200);
        Assert.True(q.TryGetRtt(Lan, out double second));
        Assert.InRange(second, 100, 200);   // a single spike must not become the reading
    }

    [Fact]
    public void TheSampleWindowReportsASettledCostAndAHighWaterMark()
    {
        var q = new MeshLinkQuality();
        for (int i = 0; i < 20; i++) q.RecordRtt(Lan, 20);
        q.RecordRtt(Lan, 300);                       // one bad packet

        Assert.True(q.TryGetStats(Lan, out double median, out double high));
        Assert.Equal(20, median, 3);                 // the typical packet is unmoved...
        Assert.True(high >= median, "the high-water mark must never read below the median");
    }

    [Fact]
    public void AHighWaterMarkIsNeverBelowItsMedian()
    {
        // The pair is subtracted downstream to price jitter; inverted, that produces a negative
        // jitter and an input delay sized below the link's real requirement.
        var q = new MeshLinkQuality();
        var rng = new System.Random(7);
        for (int i = 0; i < 200; i++)
        {
            q.RecordRtt(Lan, rng.Next(1, 400));
            Assert.True(q.TryGetStats(Lan, out double median, out double high));
            Assert.True(high >= median, $"high {high} < median {median}");
        }
    }

    [Fact]
    public void AWindowKeepsOnlyItsMostRecentSamples()
    {
        var q = new MeshLinkQuality();
        for (int i = 0; i < MeshLinkQuality.RttWindowSamples * 3; i++) q.RecordRtt(Lan, 500);
        for (int i = 0; i < MeshLinkQuality.RttWindowSamples; i++) q.RecordRtt(Lan, 10);

        Assert.True(q.TryGetStats(Lan, out double median, out double high));
        Assert.Equal(10, median, 3);
        Assert.Equal(10, high, 3);
    }

    /// <summary>
    /// A burst starts the sample windows over and makes the next tick probe immediately.
    ///
    /// Both halves matter: measuring a link with samples taken before it was chosen describes the
    /// wrong path, and waiting out a keepalive wastes a second of a window that is only a couple of
    /// seconds long.
    /// </summary>
    [Fact]
    public void ABurstClearsTheWindowsAndTheProbeSchedule()
    {
        var q = new MeshLinkQuality();
        q.RecordRtt(Lan, 42);
        q.MarkPunched(Lan, 500);
        Assert.True(q.TryGetStats(Lan, out _, out _));

        q.BeginBurst(1000, durationMs: 2000);
        Assert.False(q.TryGetStats(Lan, out _, out _));
        Assert.False(q.TryGetLastPunch(Lan, out _));
        Assert.True(q.InBurst(1000));
        Assert.True(q.InBurst(2999));
        Assert.False(q.InBurst(3000));
    }

    [Fact]
    public void NoBurstIsInFlightBeforeOneIsAskedFor()
    {
        var q = new MeshLinkQuality();
        Assert.False(q.InBurst(0));
        Assert.False(q.InBurst(long.MaxValue / 2));
    }

    // ---------------------------------------------------------------- housekeeping

    [Fact]
    public void ForgettingAPathDropsItsLivenessButNotItsMeasurement()
    {
        // A path that comes back is the same path; its measured cost did not stop being true.
        var q = new MeshLinkQuality();
        q.MarkHeard(Lan, 1000);
        q.RecordRtt(Lan, 33);
        q.Forget(Lan);
        Assert.False(q.IsAlive(Lan, 1000));
        Assert.True(q.TryGetRtt(Lan, out _));
    }

    [Fact]
    public void ARepunchMakesEveryPathProveItselfAgain()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Lan, 1000);
        q.MarkHeard(Reflexive, 1000);
        q.MarkPunched(Lan, 1000);
        q.ForgetAllLiveness();
        Assert.False(q.IsAlive(Lan, 1000));
        Assert.False(q.IsAlive(Reflexive, 1000));
        Assert.False(q.TryGetLastPunch(Lan, out _));
    }

    /// <summary>
    /// A route refresh forgets addresses nobody routes to any more.
    ///
    /// Left in place, their measurements would be offered to an aggregate as though they described
    /// a live edge — and a rejoin is exactly when addresses change.
    /// </summary>
    [Fact]
    public void ARouteRefreshDropsEverythingOutsideTheNewSet()
    {
        var q = new MeshLinkQuality();
        q.MarkHeard(Lan, 1000);
        q.RecordRtt(Lan, 10);
        q.MarkPunched(Lan, 1000);
        q.MarkHeard(Reflexive, 1000);
        q.RecordRtt(Reflexive, 20);
        q.Select(1, Candidates(Lan), null, 1000);

        q.RetainOnly(new HashSet<IPEndPoint> { Reflexive });

        Assert.False(q.IsAlive(Lan, 1000));
        Assert.False(q.TryGetRtt(Lan, out _));
        Assert.False(q.TryGetStats(Lan, out _, out _));
        Assert.False(q.TryGetLastPunch(Lan, out _));
        Assert.False(q.TryGetLastSelected(1, out _));

        Assert.True(q.IsAlive(Reflexive, 1000));
        Assert.True(q.TryGetRtt(Reflexive, out _));
    }
}
