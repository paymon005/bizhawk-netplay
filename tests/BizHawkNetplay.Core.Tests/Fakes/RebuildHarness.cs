using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Core.Tests.Fakes;

/// <summary>
/// A host and N joiners driven through a whole authoritative-state rebuild.
///
/// <b>What this is.</b> The sequence a desync recovery actually runs — claim, capture, pack,
/// distribute, wait for every peer, release — with the emulator, the sockets and the threads
/// replaced by counters. Every decision along the way is the real Core type: the same
/// <see cref="HostRebuild"/>, <see cref="SessionPhase"/>, <see cref="ApplyBarrier"/> and
/// <see cref="ResyncBudget"/> the tool form composes.
///
/// <b>What it is NOT.</b> There are no sockets here, so it proves nothing about framing, transfer
/// deadlines or writer threads — those have their own tests. What it proves is the thing no unit
/// test of the pieces could: that the pieces, driven in the order the form drives them, converge —
/// and that they refuse to converge wrongly when a peer answers twice, answers late, answers for
/// the wrong epoch, or never answers at all.
///
/// The pack step is explicit rather than threaded, because the interesting property is not that it
/// happens on another thread — it is that the world may have MOVED while it did.
/// </summary>
public sealed class RebuildHarness
{
    /// <summary>One joiner, as much of it as this sequence can see: which epoch it has imported,
    /// and whether it is still in the session.</summary>
    public sealed class Joiner
    {
        internal Joiner(int seat) => Seat = seat;

        public int Seat { get; }

        /// <summary>The epoch this peer has imported and acknowledged. 0 until the first rebuild.</summary>
        public int AppliedEpoch { get; internal set; }

        /// <summary>Set when the host released it — the joiner side's EndRebuild.</summary>
        public bool Resumed { get; internal set; }

        /// <summary>The delay and mode the host stated in the BEGIN this peer last acted on. A
        /// rebuild always lands a peer on the parameters the host just named, never on whatever it
        /// happened to be running.</summary>
        public int Delay { get; internal set; }
        public SyncMode Mode { get; internal set; }
    }

    private readonly List<Joiner> _joiners = new();
    private readonly List<string> _sent = new();

    public RebuildHarness(int joiners, int attempt = 1)
    {
        Phase = new SessionPhase();
        Barrier = new ApplyBarrier();
        Budget = new ResyncBudget();
        Rebuild = new HostRebuild(Phase, Barrier, Budget);
        Attempt = attempt;
        Phase.Start();
        Generation = new SessionGeneration(0xC0FFEE, 1);
        for (int seat = 1; seat <= joiners; seat++) _joiners.Add(new Joiner(seat));
    }

    public SessionPhase Phase { get; }
    public ApplyBarrier Barrier { get; }
    public ResyncBudget Budget { get; }
    public HostRebuild Rebuild { get; }

    public SessionGeneration Generation { get; private set; }
    public int Attempt { get; private set; }
    public IReadOnlyList<Joiner> Joiners => _joiners;

    /// <summary>Session-wide parameters, which every rebuild restates.</summary>
    public int Delay { get; set; } = 3;
    public SyncMode Mode { get; set; } = SyncMode.Rollback;

    /// <summary>Simulated state size, so the deadline model has something real to price.</summary>
    public int StateBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>What went to the peers, in order — the wire, as far as this sequence has one.</summary>
    public IReadOnlyList<string> Sent => _sent;

    /// <summary>Set to make the next capture throw, as a core refusing to export can.</summary>
    public Exception? CaptureFault { get; set; }

    // ---------------------------------------------------------------- driving the host

    /// <summary>The host's gate: is a recovery allowed to start at all. Mirrors
    /// <c>PerformResyncAsHost</c>.</summary>
    public ResyncGate GateDesync(double secondsSinceLastResync = 60.0) =>
        Budget.Gate(Phase.IsRebuilding, secondsSinceLastResync, graceSeconds: 2.0);

    /// <summary>Claim, capture and advance the generation. False when a rebuild is already in
    /// flight — the refusal that stops two baselines racing.</summary>
    public bool Begin(bool isSettingsChange = false)
    {
        if (!Rebuild.TryBegin(isSettingsChange, Attempt)) return false;
        try
        {
            if (CaptureFault != null) throw CaptureFault;
            Generation = Generation.Next();
            Rebuild.Captured(Generation, StateBytes);
            return true;
        }
        catch
        {
            Rebuild.Abort();
            throw;
        }
    }

    /// <summary>The off-thread compression returned. <paramref name="attempt"/> and
    /// <paramref name="generation"/> are what the pack thread CAPTURED, which is the whole point:
    /// they may no longer describe the world.</summary>
    public bool Packed(int? attempt = null, SessionGeneration? generation = null) =>
        Rebuild.TryPacked(attempt ?? Attempt, generation ?? Generation);

    /// <summary>Hand the baseline to every peer. False when there is nobody to wait for.</summary>
    public bool Distribute()
    {
        var seats = new List<int>();
        foreach (var joiner in _joiners) seats.Add(joiner.Seat);
        if (!Rebuild.TryDistribute(seats)) return false;
        foreach (var joiner in _joiners)
        {
            joiner.Resumed = false;
            _sent.Add($"begin->{joiner.Seat}@{Generation.Epoch}");
            _sent.Add($"state->{joiner.Seat}@{Generation.Epoch}");
        }
        return true;
    }

    /// <summary>A joiner imports the baseline and acknowledges it — the joiner half of the round
    /// trip. Returns the host's verdict on that acknowledgement.</summary>
    public ApplyAck Apply(int seat, int? epoch = null)
    {
        int at = epoch ?? Generation.Epoch;
        var joiner = Find(seat);
        if (joiner != null && at == Generation.Epoch)
        {
            joiner.AppliedEpoch = at;
            joiner.Delay = Delay;
            joiner.Mode = Mode;
        }
        var ack = Rebuild.Applied(seat, at);
        if (ack == ApplyAck.Complete) Release();
        return ack;
    }

    /// <summary>Send the RESUME markers and finish, once. Mirrors <c>ReleaseResyncAsHost</c>.</summary>
    public bool Release()
    {
        if (!Rebuild.TryBeginResume()) return false;
        foreach (var joiner in _joiners)
        {
            _sent.Add($"resume->{joiner.Seat}@{Generation.Epoch}");
            joiner.Resumed = true;
        }
        Rebuild.Complete();
        return true;
    }

    /// <summary>Everything from the gate to the release, for the cases whose interest is elsewhere.</summary>
    public bool RunWholeRebuild(bool isSettingsChange = false)
    {
        // A settings change does not pass the desync gate at all — the form calls
        // ShipAuthoritativeState directly for it. Gating it here would charge the recovery budget
        // for a deliberate reconfiguration, which is the exact confusion the two paths exist to
        // keep apart.
        if (!isSettingsChange && GateDesync() != ResyncGate.Start) return false;
        if (!Begin(isSettingsChange)) return false;
        if (!Packed()) return false;
        if (!Distribute()) { Rebuild.Complete(); return true; }
        foreach (var joiner in new List<Joiner>(_joiners)) Apply(joiner.Seat);
        return true;
    }

    // ---------------------------------------------------------------- the session moving on

    /// <summary>The session ended and restarted — every continuation from the old one is stale.</summary>
    public void RestartSession()
    {
        Attempt++;
        Rebuild.Abort();
        Phase.Start();
    }

    /// <summary>The session ended under a rebuild in flight.</summary>
    public void EndSession()
    {
        Rebuild.Abort();
        Phase.Stop();
    }

    /// <summary>A peer left. The barrier is not told directly — every drop path in the form either
    /// ends the session or advances the generation, and the rebuild that follows re-arms it.</summary>
    public void DropJoiner(int seat) => _joiners.RemoveAll(j => j.Seat == seat);

    /// <summary>Every peer stands on the generation the host last distributed.</summary>
    public bool AllConverged()
    {
        foreach (var joiner in _joiners)
            if (joiner.AppliedEpoch != Generation.Epoch || !joiner.Resumed) return false;
        return true;
    }

    private Joiner? Find(int seat)
    {
        foreach (var joiner in _joiners) if (joiner.Seat == seat) return joiner;
        return null;
    }
}
