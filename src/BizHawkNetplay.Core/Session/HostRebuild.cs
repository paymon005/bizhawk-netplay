using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Session;

/// <summary>Where a host's authoritative-state rebuild has got to.</summary>
public enum RebuildStep
{
    /// <summary>Nothing in flight.</summary>
    Idle,

    /// <summary>Claimed, and the emulator state is being captured on the frame thread.</summary>
    Capturing,

    /// <summary>Captured; the state is being compressed off-thread. The rebuild stays claimed
    /// throughout, which is why the claim has to be taken before the capture rather than after.</summary>
    Packing,

    /// <summary>Handed to the peers' writers; nobody has acknowledged yet.</summary>
    Distributing,

    /// <summary>On the wire, waiting for every peer to say it imported this epoch.</summary>
    AwaitingApply,

    /// <summary>Everyone applied; the RESUME markers are on their way.</summary>
    Resuming,
}

/// <summary>
/// The host's authoritative-state rebuild, as a sequence that refuses to be driven wrongly.
///
/// <b>What it covers.</b> One operation, used for two reasons that are mechanically identical: a
/// desync recovery, and a deliberate settings change. Capture a state, advance the generation,
/// compress it off the frame thread, hand it to every peer, wait for all of them to import it, then
/// release everyone together.
///
/// <b>Why it is a type.</b> The sequence was six methods in the tool form, three of which ran on
/// other threads and hopped back, and every hop repeated the same three-part question by hand:
/// <i>is this session still the one I started in, is it still active, and is this still the
/// generation I was working on?</i> Written out five times, it is five chances to write it four
/// times. It is now <see cref="IsCurrent"/>, asked once per step, and the step order itself is
/// checked rather than assumed — a continuation arriving out of order is refused instead of
/// silently doing the next thing to whatever state it finds.
///
/// The failure that motivates the strictness: two authoritative baselines racing on a generation
/// the peers were never told about. Every peer would import one of them, the checksums would
/// disagree forever, and the log would blame the emulation.
///
/// <b>What it does NOT do.</b> No I/O. It says what may happen next and remembers what is
/// outstanding; the caller captures, compresses, writes to sockets and narrates. It also does not
/// cover the RECONNECT rebuild, which shares the apply barrier but not this sequence — that one's
/// state is captured at the moment of the drop, is already packed by the time it is needed, and
/// releases through a different path because a rejoining peer has a handshake to finish.
///
/// <b>Threading.</b> Single writer, like <see cref="SessionPhase"/>: every method is called from
/// the UI thread, including the continuations that off-thread work marshals back.
/// </summary>
public sealed class HostRebuild
{
    private readonly SessionPhase _phase;
    private readonly ApplyBarrier _barrier;
    private readonly ResyncBudget _budget;

    private int _attempt;
    private SessionGeneration _generation;

    public HostRebuild(SessionPhase phase, ApplyBarrier barrier, ResyncBudget budget)
    {
        _phase = phase ?? throw new ArgumentNullException(nameof(phase));
        _barrier = barrier ?? throw new ArgumentNullException(nameof(barrier));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    public RebuildStep Step { get; private set; } = RebuildStep.Idle;

    /// <summary>The generation being distributed. Meaningless before <see cref="Captured"/>.</summary>
    public SessionGeneration Generation => _generation;

    /// <summary>The connection attempt this rebuild belongs to — a session that ends and restarts
    /// gets a new one, and every continuation from the old one is then stale.</summary>
    public int Attempt => _attempt;

    /// <summary>Uncompressed size of the state being shipped, for the deadlines derived from it.</summary>
    public int StateBytes { get; private set; }

    /// <summary>A deliberate change rather than a desync recovery. Only ever about what to say and
    /// what to charge — see <see cref="ResyncBudget.Excuse"/>.</summary>
    public bool IsSettingsChange { get; private set; }

    public bool InFlight => Step != RebuildStep.Idle;

    /// <summary>Seats that have not yet acknowledged this epoch.</summary>
    public IEnumerable<int> Outstanding => _barrier.Outstanding;

    /// <summary>
    /// Whether a continuation from another thread still refers to the rebuild in progress.
    ///
    /// The three-part guard every hop needs, in one place. A session that ended and restarted, a
    /// session that is no longer active, and a generation that has been superseded are all "this
    /// work is for a world that no longer exists" — and the consequence of acting anyway is writing
    /// a state for a dead timeline over a live one.
    /// </summary>
    public bool IsCurrent(int attempt, SessionGeneration generation) =>
        _phase.IsActive && attempt == _attempt && generation == _generation && InFlight;

    /// <summary>
    /// Claim the rebuild, before anything irreversible.
    ///
    /// False when one is already in flight, which is the entire point of claiming: the refusal is
    /// the feature. The claim is taken BEFORE the capture and held across the off-thread pack, so a
    /// second trigger arriving during those hundreds of milliseconds is refused rather than racing.
    /// </summary>
    public bool TryBegin(bool isSettingsChange, int attempt)
    {
        var reason = isSettingsChange ? RebuildReason.SettingsChange : RebuildReason.Desync;
        if (!_phase.BeginRebuild(reason)) return false;
        _attempt = attempt;
        _generation = default;
        StateBytes = 0;
        IsSettingsChange = isSettingsChange;
        Step = RebuildStep.Capturing;
        // A deliberate rebuild costs nothing; a recovery has already been charged by the gate that
        // authorised it. Stated here so the two paths cannot drift apart again.
        if (isSettingsChange) _budget.Excuse();
        return true;
    }

    /// <summary>
    /// The state is captured and the generation has advanced. From here the rebuild has an identity
    /// that continuations can be checked against.
    /// </summary>
    public void Captured(SessionGeneration generation, int stateBytes)
    {
        Expect(RebuildStep.Capturing, nameof(Captured));
        if (!generation.IsValid) throw new ArgumentException(
            "a rebuild distributes a real generation", nameof(generation));
        if (stateBytes < 0) throw new ArgumentOutOfRangeException(nameof(stateBytes));
        _generation = generation;
        StateBytes = stateBytes;
        Step = RebuildStep.Packing;
    }

    /// <summary>
    /// Join the sequence at distribution, for a rebuild whose baseline was captured elsewhere, and
    /// arm the barrier in the same step.
    ///
    /// The post-timeout vacate is the case: a peer dropped, the state and the generation were taken
    /// at that moment, and the survivors have been frozen holding that BEGIN ever since. There is
    /// nothing left to capture or pack — but the wait for every survivor to import it, and the
    /// single release afterwards, are exactly the same, and having them be the same code is the
    /// point. Its phase claim was taken when the peer dropped, so this adopts that claim rather
    /// than making a second one.
    ///
    /// <b>Adopting and distributing are one call on purpose.</b> They were two, and the caller
    /// discarded the first one's refusal and then called the second, which throws unless the first
    /// succeeded — so a state this could merely decline to enter became an unhandled exception in a
    /// UI timer instead. Splitting a decision from the action that depends on it, and trusting every
    /// caller to re-join them, is the same shape of mistake as the resync gate that used to sit
    /// apart from the counter it decided against.
    ///
    /// False — never a throw — when there is no rebuild claimed to adopt, when one is already being
    /// driven here, or when there is nobody to wait for. The caller has a session to end or a
    /// rebuild to finish, and either is better than a stack trace.
    /// </summary>
    public bool TryAdoptAndDistribute(SessionGeneration generation, int stateBytes, int attempt,
        IEnumerable<int> seats)
    {
        if (seats == null) throw new ArgumentNullException(nameof(seats));
        if (!_phase.IsRebuilding || InFlight) return false;
        if (!generation.IsValid) throw new ArgumentException(
            "a rebuild distributes a real generation", nameof(generation));

        _attempt = attempt;
        _generation = generation;
        StateBytes = stateBytes < 0 ? 0 : stateBytes;
        IsSettingsChange = false;
        Step = RebuildStep.Distributing;

        if (TryDistribute(seats)) return true;
        // Nobody to wait on. Leave nothing half-entered: the caller gets the same false it would
        // have got from the adopt half, and the sequence is back where it started.
        Step = RebuildStep.Idle;
        _generation = default;
        StateBytes = 0;
        return false;
    }

    /// <summary>
    /// The off-thread compression finished and handed control back. True when the result is still
    /// worth using — see <see cref="IsCurrent"/> for the three ways it might not be.
    /// </summary>
    public bool TryPacked(int attempt, SessionGeneration generation)
    {
        if (Step != RebuildStep.Packing || !IsCurrent(attempt, generation)) return false;
        Step = RebuildStep.Distributing;
        return true;
    }

    /// <summary>
    /// Wait on these seats before anyone resumes. False when there are none — a solo host has
    /// nobody to wait for, and a barrier that answered "waiting" for an empty set would freeze it
    /// forever with nobody who could ever release it. The caller finishes with
    /// <see cref="Complete"/> in that case.
    /// </summary>
    public bool TryDistribute(IEnumerable<int> seats)
    {
        Expect(RebuildStep.Distributing, nameof(TryDistribute));
        _barrier.Expect(seats, _generation.Epoch);
        if (!_barrier.IsWaiting) return false;
        Step = RebuildStep.AwaitingApply;
        return true;
    }

    /// <summary>
    /// A peer says it imported an epoch. <see cref="ApplyAck.Complete"/> is the release signal and
    /// is returned exactly once; anything stale, duplicated or from a seat we are not waiting on
    /// reads as <see cref="ApplyAck.Ignored"/> and changes nothing.
    /// </summary>
    public ApplyAck Applied(int seat, int epoch)
    {
        if (Step != RebuildStep.AwaitingApply) return ApplyAck.Ignored;
        return _barrier.Applied(seat, epoch);
    }

    /// <summary>
    /// Send the RESUME markers, once. False if they have already gone out — both callers fire on a
    /// peer reporting it applied the baseline, and a report arriving after the release would
    /// otherwise put a second RESUME on the wire for a session that had already resumed.
    /// </summary>
    public bool TryBeginResume()
    {
        if (Step != RebuildStep.AwaitingApply) return false;
        if (!_phase.TryQueueResume()) return false;
        Step = RebuildStep.Resuming;
        return true;
    }

    /// <summary>Everyone is released and the timeline is running again.</summary>
    public void Complete()
    {
        _barrier.Clear();
        _phase.EndRebuild();
        Step = RebuildStep.Idle;
        _generation = default;
        StateBytes = 0;
    }

    /// <summary>
    /// Give up on a rebuild that cannot finish — a capture that threw, a transfer that could not be
    /// queued, a session ending underneath it.
    ///
    /// The phase is cleared either way. A rebuild left claimed after its sequence died is a session
    /// that refuses every future recovery while displaying no reason for it.
    /// </summary>
    public void Abort()
    {
        _barrier.Clear();
        _phase.EndRebuild();
        Step = RebuildStep.Idle;
        _generation = default;
        StateBytes = 0;
    }

    private void Expect(RebuildStep step, string caller)
    {
        if (Step != step)
            throw new InvalidOperationException(
                $"{caller} is only valid at {step}; the rebuild is at {Step}. Driving this sequence " +
                "out of order is how two authoritative baselines end up racing on one generation.");
    }
}
