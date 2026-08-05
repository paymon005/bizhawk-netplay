using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The arithmetic between timing constants, asserted rather than commented.
///
/// This is the class of defect the FIN linger belonged to. The constant was 4000ms... no, it was
/// 2000ms, and beside it a comment claiming room for "a burst per RTO plus a few FIN re-drives".
/// The arithmetic never supported it: a tail needing two RTO rounds spent 1500ms of the linger and
/// then wanted 1500ms more for the FIN, out of 2000. The FIN went out once. Nothing failed — every
/// test still passed, because every test asserted an outcome under conditions where one attempt was
/// enough, and the comment was the only thing that had ever checked the sum.
///
/// So these assert RELATIONSHIPS. A constant tuned without its partner fails here, at the line
/// naming the partner, instead of shipping as a session that ends with the wrong reason on a link
/// nobody local can reproduce.
/// </summary>
public class ScheduleContractTests
{
    // ---------------------------------------------------------------- close and repair

    /// <summary>
    /// The linger leaves room for several FIN attempts, not one.
    ///
    /// A lost 5-byte FIN means the peer never sees EOF: it blocks until its own read timeout and
    /// reports a network fault for a stream that closed cleanly. One attempt is one packet loss
    /// away from that, so the linger has to buy repeated tries — and the re-drive runs on its own
    /// clock precisely so the data backoff cannot ration them.
    /// </summary>
    [Fact]
    public void TheLingerBuysAtLeastEightFinAttempts()
    {
        // Nothing fires faster than the loop's tick, so a cadence below it is really the tick.
        int cadence = System.Math.Max(ReliableUdpStream.FinRedriveMs, ReliableUdpStream.RetransmitTickMs);
        int attempts = ReliableUdpStream.CloseLingerMs / cadence;
        Assert.True(attempts >= 8,
            $"the linger is {ReliableUdpStream.CloseLingerMs}ms at a {cadence}ms cadence — " +
            $"{attempts} FIN attempts. One loss short of the peer reporting a network fault for a " +
            "clean close.");
    }

    /// <summary>
    /// The FIN's clock is independent of the data backoff, and materially faster than it.
    ///
    /// This is the property the original bug violated by SHARING the clock. Sharing it again would
    /// leave both constants individually reasonable, so what has to be pinned is that the FIN
    /// cadence is well inside the RTO ceiling — otherwise "its own clock" buys nothing.
    /// </summary>
    [Fact]
    public void TheFinCadenceIsNotRationedByTheDataBackoff()
    {
        Assert.True(ReliableUdpStream.FinRedriveMs * 2 <= ReliableUdpStream.RtoMaxMs,
            $"a FIN every {ReliableUdpStream.FinRedriveMs}ms against an RTO ceiling of " +
            $"{ReliableUdpStream.RtoMaxMs}ms is not an independent clock in any useful sense");
    }

    /// <summary>
    /// The linger also covers the data tail it shares the window with.
    ///
    /// The FIN is only useful once the data it follows has landed — the receiver holds the FIN
    /// sequence and reports EOF when the stream reaches it. A linger that expires while the tail is
    /// still being repaired ends the close with data outstanding, so it has to be worth at least a
    /// couple of full backed-off RTO rounds on top of the FIN attempts.
    /// </summary>
    [Fact]
    public void TheLingerOutlastsSeveralBackedOffRtoRounds()
    {
        Assert.True(ReliableUdpStream.CloseLingerMs >= 2 * ReliableUdpStream.RtoMaxMs,
            $"a {ReliableUdpStream.CloseLingerMs}ms linger against a {ReliableUdpStream.RtoMaxMs}ms " +
            "RTO ceiling gives the tail barely one repair round before the close gives up");
    }

    /// <summary>
    /// A fault takes long enough that ordinary loss cannot produce one.
    ///
    /// <c>DeadRetries</c> consecutive fruitless base retransmits ends the stream. At the backoff
    /// ceiling that has to be several seconds, or a link that stutters through one bad moment is
    /// declared dead — and the message the player gets is a network fault rather than a stall they
    /// would have ridden out.
    /// </summary>
    [Fact]
    public void DeclaringALinkDeadTakesSecondsOfSilenceNotOneBadMoment()
    {
        double slowestSeconds = ReliableUdpStream.DeadRetries * ReliableUdpStream.RtoMaxMs / 1000.0;
        double fastestSeconds = ReliableUdpStream.DeadRetries * ReliableUdpStream.RtoStartMs / 1000.0;
        Assert.True(fastestSeconds >= 10,
            $"even at the STARTING RTO a fault takes only {fastestSeconds:F1}s — too quick to " +
            "distinguish a bad moment from a dead peer");
        Assert.True(slowestSeconds <= 120,
            $"at the backoff ceiling a dead peer is not noticed for {slowestSeconds:F0}s, which is " +
            "long past the point the player has given up on the session themselves");
    }

    /// <summary>
    /// A timeout burst can re-drive the whole window the same loss just shrank it to.
    ///
    /// Stated in the code as "the equality is a COUPLING, not a coincidence". Lower the burst
    /// without lowering the floor and a timeout stops covering the window it is repairing, so
    /// recovery degrades to one segment per RTO exactly when the link is worst.
    /// </summary>
    [Fact]
    public void ATimeoutBurstCoversTheSmallestWindowLossCanProduce()
    {
        Assert.True(ReliableUdpStream.RtoBurst >= ReliableUdpStream.MinWindow,
            $"a burst of {ReliableUdpStream.RtoBurst} cannot re-drive a floor window of " +
            $"{ReliableUdpStream.MinWindow}");
        Assert.True(ReliableUdpStream.MinWindow <= ReliableUdpStream.Window,
            "the loss floor is above the ceiling, so the window can never back off at all");
    }

    // ---------------------------------------------------------------- gap retransmission

    /// <summary>
    /// A gap request is answered from history that still holds the frames it asks for.
    ///
    /// The request names <c>frontier + 1</c>, and the peer serves it out of the retransmit ring.
    /// The gap path exists for holes the redundant window has already slid past, so the ring has to
    /// reach much further back than that window — otherwise the one case the path was written for
    /// is the one it cannot answer, and both peers sit at their prediction caps asking and
    /// refusing.
    /// </summary>
    [Theory]
    [InlineData(2, 5)]      // low delay, modest redundancy
    [InlineData(4, 9)]      // R = 2D + 1
    [InlineData(10, 21)]
    [InlineData(20, 41)]    // the UI's ceiling
    public void TheRetransmitHistoryOutreachesTheRedundantWindowManyTimesOver(int delay, int redundancy)
    {
        Assert.True(redundancy >= 2 * delay + 1,
            "this case does not model a legal configuration; R must be at least 2D+1");
        Assert.True(FrameDriver.RetransmitKeepFrames >= 4 * redundancy,
            $"a {FrameDriver.RetransmitKeepFrames}-frame history against a {redundancy}-frame " +
            "redundant window leaves almost nothing the gap path can serve that the window did not " +
            "already cover");
    }

    /// <summary>
    /// The history outlasts the round trip a request has to make.
    ///
    /// A peer notices the hole, waits out its request cadence, sends, and the answer comes back —
    /// all while the frames age out of the sender's ring at 60 per second. At the ~4 seconds the
    /// ring holds, that is comfortable on any link a session survives on; the point of pinning it
    /// is that shrinking the ring to save memory (it is a few payload bytes a frame) would take the
    /// margin away silently.
    /// </summary>
    [Fact]
    public void TheRetransmitHistoryOutlastsARequestRoundTrip()
    {
        const double frameMs = 1000.0 / 60;
        double historyMs = FrameDriver.RetransmitKeepFrames * frameMs;
        // A pessimistic round trip: notice, wait a full cadence, 500ms each way.
        double roundTripMs = FrameDriver.GapRequestIntervalMs + 1000;
        Assert.True(historyMs >= 3 * roundTripMs,
            $"the ring holds {historyMs:F0}ms of input against a {roundTripMs:F0}ms request round " +
            "trip — a peer on a slow link would be asking for frames that are already gone");
    }

    /// <summary>
    /// The serve budget answers a whole stalled peer's backlog within the requester's cadence.
    ///
    /// The budget exists so a flood of requests cannot monopolise the frame timer. It becomes a
    /// freeze if it is tighter than the requests a legitimately stalled peer generates: every
    /// remote port may ask once per cadence, so a window must cover all of them at once.
    /// </summary>
    [Fact]
    public void TheServeBudgetCoversEveryPeerAskingAtOnce()
    {
        Assert.Equal(FrameDriver.GapRequestIntervalMs, FrameDriver.GapServeWindowMs);
        Assert.True(FrameDriver.GapServesPerWindow >= HandshakeCodec.MaxPlayers,
            $"{FrameDriver.GapServesPerWindow} serves per window cannot answer " +
            $"{HandshakeCodec.MaxPlayers} peers each asking once per window — the ones past the " +
            "budget are refused every window and never recover");
    }

    // ---------------------------------------------------------------- state transfers

    /// <summary>
    /// The donor wait covers what the session's own model says those bytes take, at every size.
    ///
    /// Expiring early is not a neutral outcome. The fallback resyncs everyone from the state the
    /// checksum evidence says is WRONG — the exact failure majority recovery exists to prevent —
    /// and it then spends the same transfer time distributing it. So there is no cheap early exit
    /// to be had, and a wait shorter than the model's own allowance buys nothing at all.
    ///
    /// This was a flat 15 seconds against a 92-second modelled phase for one N64 state. It looked
    /// generous, because it is generous on a fast link; the floor rate exists precisely for the
    /// links it is not.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(64 * 1024)]           // GB-era state
    [InlineData(2 * 1024 * 1024)]     // Genesis / SNES
    [InlineData(16 * 1024 * 1024)]    // N64
    public void TheDonorWaitCoversTheTransferTheSessionModels(int stateBytes)
    {
        double phaseSeconds = StateTransferBudget.OnePhaseSeconds(stateBytes);
        double waitSeconds = DonorExchange.StateTimeoutSeconds(stateBytes);

        Assert.True(waitSeconds >= phaseSeconds,
            $"a {waitSeconds:F0}s donor wait against a {phaseSeconds:F0}s modelled transfer phase " +
            "gives up on a donor that is behaving, and falls back to the state that is wrong");
        // And never below the fixed grace, which is the part that does not scale with the link:
        // reading and importing a state costs the same on a LAN as on a modem.
        Assert.True(waitSeconds >= StateTransferBudget.GraceBaseSeconds,
            $"a {waitSeconds:F0}s donor wait is under the {StateTransferBudget.GraceBaseSeconds:F0}s " +
            "this same model allows a peer merely to read and import a state it already has");
    }

    /// <summary>
    /// The donor wait is bounded — a donor that dies mid-capture must not desync the session
    /// forever. It is the same bound the survivor deadline uses for the same bytes, and no larger:
    /// waiting past the point everyone else has given up helps nobody.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2 * 1024 * 1024)]
    [InlineData(16 * 1024 * 1024)]
    public void TheDonorWaitIsBoundedByWhatTheRestOfTheSessionWillTolerate(int stateBytes)
    {
        double wait = DonorExchange.StateTimeoutSeconds(stateBytes);
        double survivor = StateTransferBudget.SurvivorReceiveDeadlineSeconds(stateBytes, waitSeconds: 0);
        Assert.True(wait < survivor,
            $"a {wait:F0}s donor wait outlasts the {survivor:F0}s a frozen survivor will wait for " +
            "the state that follows it, so the fallback would arrive after everyone gave up");
        Assert.True(DonorExchange.StateTimeoutMs(stateBytes) > 0, "a wait of zero is not a wait");
    }

    /// <summary>
    /// A survivor waits out the host's whole post-rejoin pipeline, at every player count.
    ///
    /// The three phases are strictly sequential — WELCOME/state to the rejoiner, the rejoiner's
    /// import and READY, then the survivor's own Resync — each individually allowed a full phase by
    /// the host's socket deadlines. A survivor budgeting fewer ends the session while the host is
    /// still inside its own healthy bounds, and it ends it for everyone.
    ///
    /// Player count is a parameter because the phase count being INDEPENDENT of it is a claim, not
    /// an obvious fact: it holds only because survivor transfers run on parallel writer threads.
    /// If that ever changes, the survivor deadline stops covering a full lobby first.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ASurvivorOutwaitsTheHostsWholePipeline(int players)
    {
        const int stateBytes = 16 * 1024 * 1024;
        const int waitSeconds = 10;
        double phase = StateTransferBudget.OnePhaseSeconds(stateBytes);
        double survivor = StateTransferBudget.SurvivorReceiveDeadlineSeconds(stateBytes, waitSeconds);

        // Sequential stages, plus the announced wait, plus slack — and the same figure whatever the
        // lobby size, which is exactly what the parallel-writer claim asserts.
        Assert.True(survivor >= waitSeconds + StateTransferBudget.HostPipelinePhases * phase,
            $"at {players} players a survivor gives up after {survivor:F0}s while the host is still " +
            $"inside {StateTransferBudget.HostPipelinePhases} phases of {phase:F0}s");
        Assert.True(survivor > StateTransferBudget.HostPipelinePhases * phase,
            "the deadline has no slack over the pipeline it models");
    }

    /// <summary>
    /// One phase always outlasts the socket timeout derived from it.
    ///
    /// <c>SocketTimeoutMs</c> is what the transfer actually runs under; the phase figure is what
    /// every deadline above is budgeted from. A socket that gave up before its own phase expired
    /// would make every budget above it fiction.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(64 * 1024)]
    [InlineData(2 * 1024 * 1024)]
    [InlineData(16 * 1024 * 1024)]
    [InlineData(64 * 1024 * 1024 - 17)]     // ControlMessageCodec.MaxStateBytes
    public void TheSocketTimeoutNeverUndercutsThePhaseItModels(int stateBytes)
    {
        const int floorMs = 15_000;
        int socketMs = StateTransferBudget.SocketTimeoutMs(stateBytes, floorMs);
        double phaseMs = StateTransferBudget.OnePhaseSeconds(stateBytes) * 1000.0;

        Assert.True(socketMs >= phaseMs - 1,
            $"a {socketMs}ms socket timeout for {stateBytes} bytes gives up inside its own " +
            $"{phaseMs:F0}ms phase");
        Assert.True(socketMs >= floorMs, "a tiny state lost the ordinary handshake floor");
    }

    /// <summary>
    /// The apply deadline and the phase are the same figure.
    ///
    /// A peer is excused from the ping watchdog for exactly as long as it is allowed to apply a
    /// state. Two figures here would mean either a peer killed while legitimately importing, or one
    /// left unwatched after its allowance ran out — so they are one function, and this says so.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2 * 1024 * 1024)]
    [InlineData(16 * 1024 * 1024)]
    public void TheApplyAllowanceIsExactlyOnePhase(int stateBytes)
    {
        Assert.Equal(StateTransferBudget.OnePhaseSeconds(stateBytes),
            StateTransferBudget.ApplyDeadlineSeconds(stateBytes));
    }

    /// <summary>
    /// The pessimistic wire-rate floor stays pessimistic.
    ///
    /// Every deadline above scales off it. Raise it toward what a LAN actually does and every
    /// budget shrinks at once — silently, and only on the links that were already the slowest.
    /// 200 KiB/s is roughly a 2 Mbit uplink, which is the shape of connection this has to survive.
    /// </summary>
    [Fact]
    public void TheModelledWireRateIsAFloorRatherThanATypicalRate()
    {
        Assert.True(StateTransferBudget.MinBytesPerSecond <= 512 * 1024,
            $"{StateTransferBudget.MinBytesPerSecond / 1024:F0} KiB/s is a healthy link's rate, not " +
            "a floor — every deadline derived from it would be tight on the links that need them");
        Assert.True(StateTransferBudget.MinBytesPerSecond >= 32 * 1024,
            "a floor this low makes the deadlines so generous that a dead transfer is indistinguishable " +
            "from a slow one");
    }

    // ---------------------------------------------------------------- checksum cadence

    /// <summary>
    /// The checksum interval is coarser than the rollback window.
    ///
    /// Peers quantize to interval boundaries and compare there, and the boundary state has to still
    /// be in the ring when the comparison happens. An interval finer than the window would have
    /// peers reporting boundaries faster than a correction can settle them, so a checksum could
    /// describe a frame a rollback was about to change.
    /// </summary>
    [Fact]
    public void TheChecksumCadenceIsCoarserThanAnyRollbackCanReach()
    {
        Assert.True(ChecksumCadence.DefaultIntervalFrames > 60,
            $"a {ChecksumCadence.DefaultIntervalFrames}-frame checksum interval is inside the " +
            "depth a correction can reach, so a boundary can be reported and then rewritten");
        Assert.True(ChecksumCadence.IsAcceptable(ChecksumCadence.DefaultIntervalFrames),
            "the default interval is not one the negotiation would accept from a peer");
    }
}
