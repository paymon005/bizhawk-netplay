using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    /// <summary>
    /// Host desync detection: gather each peer's checksum for a frame (our own + every joiner's).
    /// Once all <see cref="_playerCount"/> are in, they must agree; if not, resync everyone. Called
    /// from the UI thread (our own hash) and from reader threads (joiners'), hence the lock.
    /// </summary>
    private void RecordChecksum(int attempt, SessionGeneration generation, int sourcePort, int frame, uint hash)
    {
        ChecksumOutcome outcome;
        DesyncPartition? partition;
        lock (_hashLock)
        {
            if (!IsConnectionAttemptCurrent(attempt) || !_phase.IsActive || !_isHost
                || generation != CurrentGeneration) return;
            // Vacated seats never report; waiting for the full seat count would leave every frame
            // pending forever the moment someone left for good.
            outcome = _checksums.Record(generation, sourcePort, frame, hash, _playerCount,
                _playerCount - _vacatedCount);
            partition = _checksums.LastMismatch; // read under the lock that produced it
        }
        if (outcome == ChecksumOutcome.Pending) return;
        if (outcome == ChecksumOutcome.Mismatch)
        {
            // During the divergence-learning window a mismatch IS the measurement, not the
            // emergency: right after a rebuild the peers are byte-identical, so a disagreeing
            // boundary here means machine-produced bytes (GPU write-back) — exactly what the
            // learner is collecting, and what the mask it produces will exclude. Resyncing now
            // would just restart the same learning from another identical baseline, forever —
            // the resync loop that above-native N64 used to be. A REAL desync in this window is
            // caught by the learner's own share cap (TooBroadToMask ends the suppression with a
            // verdict), and by the very next boundary past the window regardless.
            if (DivergenceLearner.IsLearnFrame(frame, _checksumInterval))
            {
                if (Verbose) BeginInvokeUi(() =>
                    Log($"checksum frame {frame}: peers disagree during the learning window — " +
                        "measuring which memory is machine-produced rather than resyncing"));
                return;
            }
            BeginInvokeUi(() =>
            {
                if (IsConnectionAttemptCurrent(attempt) && CurrentGeneration == generation)
                    OnHostDesync(frame, partition);
            });
            return;
        }

        // Every agreement goes through ONE branch, and recording it is unconditional. It used to sit
        // inside the `Verbose` logging branch — and Verbose is off by default — so an ordinary
        // session never recorded a single agreement and the second desync of the run was announced
        // as a systematic mismatch that resyncing could not clear. The logging is what varies here;
        // the bookkeeping never does.
        BeginInvokeUi(() =>
        {
            if (!IsConnectionAttemptCurrent(attempt) || CurrentGeneration != generation) return;
            _desyncTrend.RecordAgreement();
            if (_resyncCount != 0)
            {
                _resyncCount = 0;
                Log("back in sync — recovery confirmed");
            }
            else if (Verbose) Log($"checksum frame {frame}: all {_playerCount} agree");
        });
    }

    // --- divergence learning (KI-14/KI-15): measure what is machine-produced, then mask it ----

    /// <summary>Host only; rebuilt for every generation, since every rebuild stands the peers on
    /// freshly identical memory — the perfect learning baseline. Guarded by _hashLock beside the
    /// checksum ledger it works with. Null when fewer than two active players.</summary>
    private DivergenceLearner? _divergenceLearner;
    private bool _divergencePublished; // the learner's verdict is terminal; act on it once

    /// <summary>Fresh learner for a fresh generation (host), and no stale mask anywhere: frame
    /// numbers restart at zero on a rebuild, so every peer clears at the same boundary and the
    /// learn round that every rebuild begins decides what replaces it.</summary>
    private void ResetDivergenceLearning()
    {
        try { _adapter?.ClearLearnedExclusion(); } catch { }
        lock (_hashLock)
        {
            int active = _playerCount - _vacatedCount;
            _divergenceLearner = _isHost && active >= 2 ? new DivergenceLearner(active) : null;
            _divergencePublished = false;
        }
    }

    /// <summary>Host: fold one peer's (or our own) bucket vector into the learner, and act once
    /// on its verdict. Runs from reader threads and the UI thread, like the checksum ledger.</summary>
    private void RecordDivergence(int attempt, SessionGeneration generation, int port, int frame,
        uint[] buckets)
    {
        DivergenceVerdict verdict;
        DivergenceLearner? learner;
        lock (_hashLock)
        {
            if (!IsConnectionAttemptCurrent(attempt) || !_phase.IsActive || !_isHost
                || generation != CurrentGeneration) return;
            learner = _divergenceLearner;
            if (learner == null || _divergencePublished) return;
            verdict = learner.Record(frame, port, buckets);
            if (verdict == DivergenceVerdict.Learning) return;
            _divergencePublished = true;
        }
        BeginInvokeUi(() =>
        {
            if (IsConnectionAttemptCurrent(attempt) && _phase.IsActive
                && CurrentGeneration == generation)
                OnDivergenceLearned(generation, learner!, verdict);
        });
    }

    /// <summary>How many checksum intervals past the learn window the mask takes effect — margin
    /// for control-channel lag plus rollback's anchors, which hash a boundary up to a ring's
    /// depth before it is reported. Every peer switches at the same frame.</summary>
    private const int MaskEffectiveMargin = 2;

    private void OnDivergenceLearned(SessionGeneration generation, DivergenceLearner learner,
        DivergenceVerdict verdict)
    {
        switch (verdict)
        {
            case DivergenceVerdict.NothingDiverges:
                // The native-resolution case, and most cores always: nothing machine-dependent,
                // nothing masked, nothing changes. One quiet line so a log can prove it ran.
                Log($"divergence learning: all {ControlMessageCodec.DivergenceBuckets} buckets " +
                    $"agreed across {learner.LearnedFrames.Count} boundaries — nothing here is " +
                    "machine-produced, the checksum keeps reading everything.");
                break;

            case DivergenceVerdict.MaskLearned:
            {
                var mask = learner.MaskBuckets;
                var ranges = DivergenceLearner.MaskRanges(mask, _adapter?.MainMemorySize ?? 0);
                string where = DescribeMaskRanges(ranges);

                // Measuring is always free; EXCLUDING is a trade, and the gate decides whether
                // this session may make it. See MaskPolicy — the short version is that a game
                // reading its own framebuffer would have real divergence hidden, so masking is
                // opt-in and never happens at native resolution.
                var policy = DivergenceLearner.ChoosePolicy(
                    _aboveNativeCheck.Checked, _adapter?.IsAboveNativeResolution());
                if (policy == MaskPolicy.DiagnosticOnly)
                {
                    ConnLog($"divergence measured: {learner.MaskedShare:P0} of main RAM disagrees " +
                            $"between peers on otherwise-identical states{where} — video write-back, " +
                            "not emulation drift. Nothing is being excluded: the checksum still reads " +
                            "everything, which is the strongest detection and the right default. " +
                            "Above native this WILL keep reading as a desync — 'Allow above-native " +
                            "N64' on the Diagnostics tab is the opt-in, and native resolution is the " +
                            "setting that needs no trade at all.", Color.DarkOrange);
                    break;
                }

                int effectiveFrom =
                    (DivergenceLearner.LearnRounds + MaskEffectiveMargin) * _checksumInterval;
                try { _adapter?.SetLearnedExclusion(mask, effectiveFrom); } catch { }
                var body = ControlMessageCodec.EncodeExclusionMask(generation, effectiveFrom, mask);
                foreach (var link in _peers)
                    QueueControl(link, ControlMessageType.ExclusionMask, body);
                ConnLog($"measured which memory is machine-produced: {learner.MaskedShare:P0} of " +
                        $"main RAM{where} disagreed between peers on otherwise-identical states — " +
                        $"video write-back, not a desync. Excluded from checksum frame " +
                        $"{effectiveFrom} on (EXPERIMENTAL, you opted in): the rest of memory is " +
                        "compared honestly, and a game that reads its own framebuffer could now " +
                        "diverge there without being caught.", Color.DarkOrange);
                break;
            }

            case DivergenceVerdict.TooBroadToMask:
                // Real divergence spreads through memory; a framebuffer does not. Refuse to learn
                // a mask that would blind the checksum, and let the next boundary past the learn
                // window fire the ordinary desync recovery.
                ConnLog($"divergence learning refused a mask: {learner.MaskedShare:P0} of main " +
                        "RAM disagreed between peers, which is far more than video write-back can " +
                        "explain — that is a real desync. Normal desync recovery resumes from the " +
                        "next checksum.", Color.Firebrick);
                break;
        }
    }

    /// <summary>The masked ranges as addresses, which is KI-15's whole point: a desync report that
    /// names WHERE, not merely that two numbers differ. Empty when the size is unknown.</summary>
    private static string DescribeMaskRanges(List<(long Start, long EndExclusive)> ranges)
    {
        if (ranges.Count == 0) return "";
        var named = new List<string>();
        for (int i = 0; i < ranges.Count && i < 4; i++)
            named.Add($"0x{ranges[i].Start:X}-0x{ranges[i].EndExclusive:X}");
        return " (" + string.Join(", ", named)
            + (ranges.Count > 4 ? $", and {ranges.Count - 4} more" : "") + ")";
    }

    /// <summary>Joiner: adopt the host's measured exclusion mask (or clear, when all-false).</summary>
    private void OnExclusionMask(SessionGeneration generation, int effectiveFrom, bool[] mask)
    {
        if (!_phase.IsActive || _isHost || generation != CurrentGeneration) return;
        int set = 0;
        foreach (bool b in mask) if (b) set++;

        // The host only sends a mask when its own gate opened, but this peer applies the mask to
        // ITS memory — so it checks its own gate too. A joiner at native resolution, or one that
        // did not opt in, keeps hashing everything: the strongest detection is never taken away
        // from a machine that did not ask to trade it. The hash folds the mask's identity, so a
        // peer that declines is loudly incomparable rather than quietly wrong.
        var policy = DivergenceLearner.ChoosePolicy(
            _aboveNativeCheck.Checked, _adapter?.IsAboveNativeResolution());
        if (set > 0 && policy == MaskPolicy.DiagnosticOnly)
        {
            ConnLog("the host measured machine-produced memory and is excluding it, but this " +
                    "machine has not opted in (or is at native resolution), so it keeps checksumming " +
                    "everything — which will read as a desync against the host. Match the setting " +
                    "on both machines: either both allow above-native N64, or both run native.",
                Color.Firebrick);
            return;
        }

        try { _adapter?.SetLearnedExclusion(mask, effectiveFrom); } catch { return; }
        if (set > 0)
            ConnLog($"the host measured which memory is machine-produced (video write-back): " +
                    $"{set} of {mask.Length} buckets are excluded from checksum frame " +
                    $"{effectiveFrom} on (EXPERIMENTAL). A game that reads its own framebuffer " +
                    "could now diverge there without being caught.", Color.DarkOrange);
    }

    private void OnHostDesync(int frame, DesyncPartition? partition = null)
    {
        if (_phase.IsRebuilding) return;
        if (MonotonicElapsedSeconds(_lastResyncStamp) < ResyncGraceSeconds) return; // just resynced; give it time
        Log(partition == null
            ? $"DESYNC at frame {frame} — peers disagree"
            : $"DESYNC at frame {frame} — {partition.Describe()}");

        // Recovery is about to overwrite every peer with THIS machine's state. When this machine
        // is the outlier that is the wrong state, and it is worth saying so out loud rather than
        // letting a log show three agreeing players quietly adopting one disagreeing one. Choosing
        // a different authority needs a majority-reconstruction protocol (a wire change); naming
        // the case does not, and until then the player can act on it — the usual causes are local
        // and theirs to clear.
        int donor = MajorityRecovery.SelectDonor(partition, _deferToMajorityCheck.Checked);
        if (donor >= 0)
        {
            ConnLog(MajorityRecovery.Describe(partition!, donor), Color.DarkOrange);
            if (RequestStateFromDonor(donor)) return;   // resync resumes when the state arrives
            // Falling through is deliberate: the donor is unreachable or already busy, and the
            // session still has to converge on something. Today's behaviour is the fallback.
            ConnLog($"could not ask P{donor + 1} for its state — recovering from this machine's " +
                    "own state instead, which on this evidence is the wrong one.", Color.Firebrick);
        }
        else if (partition is { HostIsOutvoted: true })
        {
            ConnLog(MajorityRecovery.DescribeDeclined(partition), Color.Firebrick);
        }
        // A divergence that recurs at EVERY interval, with no agreeing checksum in between, is not
        // the emulation drifting — a real drift would sync fine for a while first. It means the two
        // machines are comparing memory that was never going to match. On N64 the usual cause is
        // above-native video resolution: the plugin resolves its framebuffer back into RDRAM, and
        // those bytes come from the GPU rather than the emulated core, so they differ per machine
        // and land inside the region the checksum reads. Resyncing cannot fix that, and will keep
        // shipping a 16MiB state every interval until it gives up.
        if (_desyncTrend.RecordDesync())
            ConnLog("every checksum since this session began has disagreed, with none agreeing in " +
                "between — that is a systematic mismatch, not emulation drift, and resyncing will " +
                "not clear it. On N64 the usual cause is running the video plugin above native " +
                "resolution: it resolves the framebuffer back into RDRAM, and those bytes come from " +
                "your GPU rather than the emulated core, so they cannot match your opponent's. Drop " +
                "to native resolution on BOTH machines." +
                (_videoDiagnostic != null ? $" This session is running {_videoDiagnostic}." : ""),
                Color.Firebrick);
        PerformResyncAsHost();
    }

    // --- majority-aware recovery (KI-20, opt-in) -------------------------------------------------

    /// <summary>The seat the host asked for a state, or -1 when it is not waiting for one. Also the
    /// gate on <see cref="OnStateOffer"/>: an offer from anyone else, or when nothing was asked, is
    /// a peer trying to hand the host bytes it never requested.</summary>
    private int _awaitingDonorPort = -1;
    private SessionGeneration _awaitingDonorGeneration;

    /// <summary>
    /// Ask the majority's chosen machine for its state. True when the request went out, in which
    /// case recovery continues in <see cref="OnStateOffer"/>; false means the caller should fall
    /// back to recovering from the host's own state, because the session still has to converge.
    /// </summary>
    private bool RequestStateFromDonor(int donorPort)
    {
        if (!_isHost || _phase.IsRebuilding) return false;
        if (_awaitingDonorPort >= 0) return false;   // one ask in flight at a time
        PeerLink? donor = null;
        foreach (var link in _peers) if (link.RemotePort == donorPort) { donor = link; break; }
        if (donor == null) return false;

        var generation = CurrentGeneration;
        if (!QueueControl(donor, ControlMessageType.StateRequest,
                ControlMessageCodec.EncodeStateRequest(generation)))
            return false;

        _awaitingDonorPort = donorPort;
        _awaitingDonorGeneration = generation;
        // Bounded, because a donor that never answers must not leave the session desynced forever
        // waiting on it. On expiry the ordinary host-authoritative resync runs, which is exactly
        // what would have happened had this never been attempted.
        StartDonorTimeout();
        Status($"asking P{donorPort + 1} for the majority's state…", Color.DarkOrange);
        return true;
    }

    private System.Windows.Forms.Timer? _donorTimer;

    private void StartDonorTimeout()
    {
        StopDonorTimeout();
        _donorTimer = new System.Windows.Forms.Timer { Interval = DonorStateTimeoutMs };
        _donorTimer.Tick += (_, __) =>
        {
            StopDonorTimeout();
            if (_awaitingDonorPort < 0) return;
            int port = _awaitingDonorPort;
            _awaitingDonorPort = -1;
            ConnLog($"P{port + 1} did not send its state within " +
                    $"{DonorStateTimeoutMs / 1000}s — recovering from this machine's own state " +
                    "instead, which on the checksum evidence is the wrong one.", Color.Firebrick);
            PerformResyncAsHost();
        };
        _donorTimer.Start();
    }

    private void StopDonorTimeout()
    {
        try { _donorTimer?.Stop(); _donorTimer?.Dispose(); } catch { }
        _donorTimer = null;
    }

    /// <summary>
    /// Joiner: the host has asked for our state because our group outvoted it. Export and send.
    ///
    /// Exporting is the same capture the host does for its own resync, on the same thread, so it
    /// carries the same cost and the same guarantees. No frame is named — see
    /// <c>ControlMessageCodec.EncodeStateRequest</c> for why asking for a specific one would be
    /// worse than useless.
    /// </summary>
    private void OnStateRequested(SessionGeneration generation)
    {
        if (_isHost || !_phase.IsActive || generation != CurrentGeneration) return;
        if (_peers.Count == 0) return;
        try
        {
            var state = _adapter!.ExportState();
            ConnLog($"the host asked for this machine's state ({state.Length / 1024}KiB) — the " +
                    "other players agreed with us and not with it, so this becomes the session's " +
                    "state.", Color.DarkSlateBlue);
            QueueControl(_peers[0], ControlMessageType.StateOffer,
                ControlMessageCodec.EncodeStateOffer(generation, StateCompression.Pack(state)));
        }
        catch (Exception ex)
        {
            // Nothing to send and nothing to do: the host's timeout falls back to its own state.
            Log("(warning) could not export a state for the host's majority request: " + ex.Message);
        }
    }

    /// <summary>
    /// Host: the donor's state arrived. Adopt it, then run the ordinary resync so every peer —
    /// including the donor and including this machine — converges on it.
    ///
    /// This is the only place a host loads a peer's savestate, and every guard here is about that.
    /// It must be the seat we asked, in the generation we asked it in, while we are still waiting;
    /// anything else is discarded unread. See <see cref="MajorityRecovery"/> for why the whole
    /// feature is opt-in, and <see cref="StateImportTrust"/> for what loading one means.
    /// </summary>
    private void OnStateOffer(PeerLink link, SessionGeneration generation, byte[] packed)
    {
        if (!_isHost || _awaitingDonorPort < 0) return;
        if (link.RemotePort != _awaitingDonorPort || generation != _awaitingDonorGeneration) return;
        StopDonorTimeout();
        _awaitingDonorPort = -1;

        if (!StateCompression.TryUnpack(packed, ControlMessageCodec.MaxStateBytes, out var state))
        {
            ConnLog($"P{link.RemotePort + 1} sent a malformed or oversized state — recovering from " +
                    "this machine's own instead.", Color.Firebrick);
            PerformResyncAsHost();
            return;
        }

        try
        {
            LogStateImportTrust();   // the host is importing now too, which it never used to
            _adapter!.ImportState(state);
            ConnLog($"adopted P{link.RemotePort + 1}'s state ({state.Length / 1024}KiB) — " +
                    "distributing it to everyone as the session's.", Color.DarkOrange);
        }
        catch (Exception ex)
        {
            // A failed import can leave this machine anywhere, so the resync below is not optional:
            // it re-captures whatever state we are now in and makes it authoritative, which is the
            // same convergence the session would have had without any of this.
            ConnLog("could not load the majority's state (" + ex.Message +
                    ") — recovering from this machine's own instead.", Color.Firebrick);
        }
        PerformResyncAsHost();
    }

    /// <summary>
    /// Host recovery: capture an authoritative state on the emulator thread, enqueue its transfer
    /// to each peer's writer, and rebuild from a clean baseline. Simulation stays paused while the
    /// writers handle socket/flow-control waits, but WinForms remains responsive.
    /// Bounded by <see cref="MaxResyncs"/> so a persistent (non-transient) desync gives up instead
    /// of looping; a short grace window debounces repeat triggers for the same desync.
    /// </summary>
    private void PerformResyncAsHost()
    {
        if (!_phase.IsActive || !_isHost) return;
        var gate = RecoveryPolicy.GateResync(_phase.IsRebuilding,
            MonotonicElapsedSeconds(_lastResyncStamp), ResyncGraceSeconds, _resyncCount + 1, MaxResyncs);
        if (gate == ResyncGate.AlreadyInProgress || gate == ResyncGate.Debounced) return;
        _resyncCount++;
        if (gate == ResyncGate.GiveUp)
        {
            EndSession($"persistent desync — gave up after {MaxResyncs} resync attempts (likely a determinism bug)");
            return;
        }
        ShipAuthoritativeState($"resync #{_resyncCount}", isSettingsChange: false);
    }

    /// <summary>
    /// Rebuild the whole session on a fresh generation from this host's current state and current
    /// <see cref="_sessionDelay"/>/<see cref="_mode"/>: capture on the emulator thread, enqueue the
    /// transfer to each peer's writer, and rebuild locally from the same baseline. Simulation stays
    /// paused while the writers handle socket/flow-control waits, but WinForms remains responsive.
    ///
    /// Shared by desync recovery and by a deliberate settings change, because they are the same
    /// operation: both need every peer to land on one state, at one generation, under one set of
    /// parameters. The only difference is what gets said and what gets counted.
    /// </summary>
    /// <summary>
    /// What actually goes on the wire versus what was captured. Worth a line because the freeze
    /// every player sits through is proportional to the first number, not the second, and until
    /// now they were the same number — so a slow rebuild on a heavy core had no way to show
    /// whether the link or the state size was responsible.
    /// </summary>
    private static string DescribeStateTransfer(int stateBytes, int wireBytes)
    {
        if (stateBytes <= 0) return $"{wireBytes} B on the wire";
        int pct = (int)Math.Round(100.0 * wireBytes / stateBytes);
        return pct >= 98
            ? $"{wireBytes / 1024}KiB on the wire (incompressible)"
            : $"{wireBytes / 1024}KiB on the wire ({pct}% of it)";
    }

    private void ShipAuthoritativeState(string label, bool isSettingsChange)
    {
        int attempt = CurrentConnectionAttempt;
        try
        {
            // Claim the rebuild BEFORE anything irreversible. BeginRebuild refusing a second
            // rebuild while one is in flight is the point of the phase flag — but the refusal was
            // being discarded, after the generation had already advanced. Every current caller
            // checks IsRebuilding first on the UI thread, so this is unreachable today; the pack
            // thread now holds the rebuild open for hundreds of milliseconds longer than it used
            // to, and the failure a careless fourth caller would produce — two authoritative
            // baselines racing on a generation peers were never told about — is the worst one in
            // this file. Cheap insurance, honestly labelled.
            if (!_phase.BeginRebuild(isSettingsChange ? RebuildReason.SettingsChange : RebuildReason.Desync))
            {
                Log($"{label}: a rebuild is already in flight — refused");
                return;
            }
            var state = _adapter!.ExportState();
            var generation = AdvanceGeneration();
            RebuildDriver();
            RefreshLiveSettingsUi();
            int peerCount = _peers.Count;
            var generationBody = ControlMessageCodec.EncodeResyncBegin(generation, state.Length,
                _sessionDelay, _mode, waitSeconds: 0, isSettingsChange: isSettingsChange);
            Status($"{label}: sending epoch {generation.Epoch} " +
                $"({state.Length / 1024}KiB) to {peerCount} peer(s)…", Color.DarkOrange);

            if (peerCount == 0)
            {
                _phase.EndRebuild();
                RebaseFrameSchedule();
                RefreshLiveSettingsUi();
                return;
            }

            // Only the capture above needs this thread. Deflating the state does not, and on a heavy
            // core it is a few hundred milliseconds of the UI thread — every resync, every settings
            // change, every savestate load — during which EmuHawk is simply frozen. The peers are
            // already waiting and still ponging normally, so spending that time off-thread costs the
            // transfer nothing and gives the window back to WinForms.
            new Thread(() =>
            {
                byte[] stateBody;
                try { stateBody = ControlMessageCodec.EncodeStatePayload(generation, state); }
                catch (Exception ex)
                {
                    BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt) && _phase.IsActive
                            && CurrentGeneration == generation)
                            EndSession("resync failed: " + ex.Message);
                    });
                    return;
                }
                BeginInvokeUi(() => QueueAuthoritativeState(
                    label, attempt, generation, state.Length, generationBody, stateBody));
            })
            { IsBackground = true, Name = "BizHawkNetplay-state-pack" }.Start();
        }
        catch (Exception ex) { EndSession("resync failed: " + ex.Message); }
    }

    /// <summary>
    /// Hand the packed authoritative state to each peer's writer. Runs on the UI thread once
    /// <see cref="ShipAuthoritativeState"/>'s packing thread has finished.
    ///
    /// The per-peer leashes are set HERE rather than before packing, so the transfer deadline covers
    /// the transfer rather than the compression that preceded it.
    /// </summary>
    private void QueueAuthoritativeState(string label, int attempt, SessionGeneration generation,
        int stateLength, byte[] generationBody, byte[] stateBody)
    {
        if (!IsConnectionAttemptCurrent(attempt) || !_phase.IsActive
            || CurrentGeneration != generation) return;

        Log($"{label}: captured {stateLength / 1024}KiB for epoch {generation.Epoch}, " +
            $"{DescribeStateTransfer(stateLength, stateBody.Length)}; " +
            "waiting for every peer to import it");

        if (_peers.Count == 0)
        {
            _phase.EndRebuild();
            RebaseFrameSchedule();
            RefreshLiveSettingsUi();
            return;
        }
        foreach (var link in _peers)
        {
            GraceForStateTransfer(link, stateLength); // it can't pong while its reader consumes the frame
            link.AwaitingAppliedEpoch = generation.Epoch;
            Interlocked.Exchange(ref link.AppliedDeadlineTicks, StateApplyDeadlineTicks(stateLength));
            if (!QueueControl(link, ControlMessageType.ResyncBegin, generationBody)
                || !QueueControl(link, ControlMessageType.Resync, stateBody, ok =>
                {
                    if (!ok) BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt) && _phase.IsActive
                            && CurrentGeneration == generation)
                            EndSession("resync state transfer failed");
                    });
                }))
            {
                EndSession("resync state transfer could not be queued");
                return;
            }
        }
    }

    /// <summary>
    /// Host: change the netcode and/or input delay of a running session. The session does not end
    /// and nobody reconnects — everyone is rebuilt onto one state at a new generation under the new
    /// parameters, which is exactly what a desync resync already does. Costs a brief pause and one
    /// savestate transfer.
    /// </summary>
    /// <summary>Session-shaped wrapper over <see cref="LobbyDelayPolicy.DelayForModeChange"/> — it
    /// holds the reasoning and the measurements; this supplies the live link figure and says what it
    /// did. Never lowers what the host asked for.</summary>
    private int DelayFloorForModeChange(SyncMode requestedMode, int requestedDelay)
    {
        double worst = WorstPingMs(out _);
        int raised = LobbyDelayPolicy.DelayForModeChange(_mode, requestedMode, requestedDelay,
            worst, _frameMs);
        if (raised == requestedDelay) return requestedDelay;

        ConnLog($"{DescribeMode(requestedMode)} needs more input delay than {DescribeMode(_mode)} on " +
                $"this link (~{worst:F0}ms): raising {requestedDelay} → {raised} with the change, or it " +
                "would stall waiting for input it cannot get in time. Lower it afterwards if it feels " +
                "over-delayed.", Color.DarkSlateBlue);
        return raised;
    }

    private void ApplyLiveSettingsAsHost()
    {
        if (!_phase.IsActive || !_isHost) return;
        if (_phase.IsRebuilding || _phase.AwaitingRejoin)
        {
            ConnLog("the session is already rebuilding — try the settings change again once it settles.",
                Color.DarkOrange);
            return;
        }
        if (_adapter == null) return;

        _netcodeChoice = (NetcodeChoice)_netcodeCombo.SelectedIndex;
        int requestedDelay = Clamp((int)_delayBox.Value, 1, HandshakeCodec.MaxInputDelay);
        // The same decision the lobby makes, on the same evidence: this host's taste, this core's
        // measured replay behaviour, and every peer's advertised capability. A peer that cannot roll
        // back is not outvoted mid-session any more than it would be at the start.
        var prefs = LocalPreferences(isHost: true);
        // A session started on Lockstep never paid for the rollback probe, so asking for rollback
        // now is the moment it gets measured. It save-states around itself, but it does step the
        // core for a second or so — say that rather than letting the tool look hung.
        if (prefs.WantRollback && _probeDepth < 0)
        {
            Status("measuring this machine's rollback depth…", Color.DarkOrange);
            ConnLog("this session started on lockstep, so this machine has not measured its rollback " +
                    "depth yet — doing that now. It costs about a second and the picture may jump.",
                Color.DarkSlateBlue);
        }
        var identity = BuildIdentity(_adapter, prefs.WantRollback);
        var requestedMode = ChooseSyncMode(_peers, identity, prefs);
        requestedDelay = DelayFloorForModeChange(requestedMode, requestedDelay);

        if (requestedDelay == _sessionDelay && requestedMode == _mode)
        {
            ConnLog($"nothing to change — the session is already running {DescribeMode(_mode)} at " +
                    $"input delay {_sessionDelay}.", Color.DimGray);
            return;
        }

        string what = requestedMode != _mode && requestedDelay != _sessionDelay
            ? $"netcode {DescribeMode(_mode)} → {DescribeMode(requestedMode)} and input delay " +
              $"{_sessionDelay} → {requestedDelay}"
            : requestedMode != _mode
                ? $"netcode {DescribeMode(_mode)} → {DescribeMode(requestedMode)}"
                : $"input delay {_sessionDelay} → {requestedDelay}";
        ConnLog($"changing {what} — everyone stays connected through a brief pause while the new " +
                "baseline is shared.", Color.DarkSlateBlue);

        _sessionDelay = requestedDelay;
        _mode = requestedMode;
        if (_mode == SyncMode.Rollback) ConfigureRollbackDepth();
        ShipAuthoritativeState("settings change", isSettingsChange: true);
        UpdateNetcodeLabel();
    }

    private static string DescribeMode(SyncMode mode) => mode == SyncMode.Rollback ? "rollback" : "lockstep";

    /// <summary>Joiner: adopt the host's authoritative state and acknowledge only after the
    /// emulator import and generation-bound driver rebuild have both completed. The parameters
    /// come from the host's ResyncBegin, so a rebuild always lands on the delay and mode the host
    /// just stated rather than whatever this peer happened to be running.</summary>
    private void ApplyResyncAsJoiner(SessionGeneration generation, byte[] state,
        int inputDelay, SyncMode mode, bool isSettingsChange)
    {
        if (!_phase.IsActive || _isHost || generation != CurrentGeneration.Next()) return;
        int attempt = CurrentConnectionAttempt;
        // A deliberate change is not evidence of a determinism bug, so it must not spend the budget
        // that exists to catch one — otherwise six delay tweaks would end the session.
        if (!isSettingsChange && ++_resyncCount > MaxResyncs)
        {
            EndSession($"persistent desync — gave up after {MaxResyncs} resync attempts (likely a determinism bug)");
            return;
        }
        try
        {
            _phase.BeginRebuild(isSettingsChange ? RebuildReason.SettingsChange : RebuildReason.Desync);
            // Always say something for a deliberate rebuild, not only when the parameters moved. A
            // host savestate load rides this same flag — it is deliberate and must not spend the
            // desync budget — but changes neither delay nor mode, so it used to arrive as an
            // unexplained jump. The wire cannot tell the two apart without a protocol bump that is
            // not worth spending on a sentence, so say which one it looks like.
            if (isSettingsChange)
                ConnLog(inputDelay != _sessionDelay || mode != _mode
                        ? $"the host changed the session to {DescribeMode(mode)} at input delay " +
                          $"{inputDelay} — staying connected through the rebuild."
                        : "the host rebuilt the session from its own state — a savestate load, or an " +
                          "Apply that changed nothing. Staying connected through the rebuild.",
                    Color.DarkSlateBlue);
            Status($"applying {state.Length / 1024}KiB host resync epoch {generation.Epoch}…",
                Color.DarkOrange);
            LogStateImportTrust();   // a resync is the other path that loads the host's bytes
            _adapter!.ImportState(state);
            SetGeneration(generation);
            _sessionDelay = inputDelay;
            _mode = mode;
            if (_mode == SyncMode.Rollback) ConfigureRollbackDepth();
            RebuildDriver();
            UpdateNetcodeLabel();
            if (_peers.Count == 0 || !QueueControl(_peers[0], ControlMessageType.ResyncApplied,
                HandshakeCodec.EncodeGeneration(generation), ok =>
                {
                    if (!ok) BeginInvokeUi(() =>
                    {
                        if (IsConnectionAttemptCurrent(attempt) && _phase.IsActive
                            && CurrentGeneration == generation)
                            EndSession("could not acknowledge applied resync state");
                    });
                }))
            {
                EndSession("could not queue the applied-state acknowledgement");
                return;
            }
            Log($"resync #{_resyncCount}: imported epoch {generation.Epoch} " +
                $"({state.Length / 1024}KiB); waiting for the host to release all peers");
        }
        catch (Exception ex) { EndSession("resync apply failed: " + ex.Message); }
    }

    private void OnPeerResyncApplied(PeerLink link, SessionGeneration generation)
    {
        if (!_phase.IsActive || !_isHost || generation != CurrentGeneration || !_peers.Contains(link)) return;
        if (link.AwaitingAppliedEpoch != generation.Epoch) return; // stale or duplicate acknowledgement

        link.AwaitingAppliedEpoch = 0;
        Interlocked.Exchange(ref link.AppliedDeadlineTicks, 0);
        Interlocked.Exchange(ref link.TimeoutGraceUntilTicks, 0);
        if (Verbose) Log($"{link.Label} applied resync epoch {generation.Epoch}");

        foreach (var peer in _peers)
            if (peer.AwaitingAppliedEpoch == generation.Epoch) return;

        if (_pendingReconnectLink != null && _pendingReconnectGeneration == generation)
            ReleaseReconnectedPeer(_pendingReconnectLink, _pendingReconnectStateLength, generation);
        else
            ReleaseResyncAsHost(generation);
    }

    private void ReleaseResyncAsHost(SessionGeneration generation)
    {
        if (generation != CurrentGeneration || !_phase.TryQueueResume()) return;
        int attempt = CurrentConnectionAttempt;
        QueueResyncResumeToPeers(generation, ok => BeginInvokeUi(() =>
        {
            if (!IsConnectionAttemptCurrent(attempt) || !_phase.IsActive
                || CurrentGeneration != generation) return;
            if (!ok) { EndSession("resync resume transfer failed"); return; }
            _driver?.ResetRemoteInputLiveness();
            _phase.EndRebuild();
            RebaseFrameSchedule();
            RefreshLiveSettingsUi();
            Status($"in session — {DescribeMode(_mode)}, you are P{_localPort + 1}/{_playerCount}, " +
                   $"delay {_sessionDelay}", Color.Green);
            Log($"every peer applied epoch {generation.Epoch}; resuming");
        }));
    }

    private void ResumeResyncAsJoiner(SessionGeneration generation)
    {
        if (!_phase.IsActive || _isHost || generation != CurrentGeneration || !_phase.IsRebuilding) return;
        _driver?.ResetRemoteInputLiveness();
        _phase.EndRebuild();
        RebaseFrameSchedule();
        RefreshLiveSettingsUi();
        Status($"in session — {DescribeMode(_mode)}, you are P{_localPort + 1}/{_playerCount}, " +
               $"delay {_sessionDelay}", Color.Green);
        Log($"every peer applied epoch {generation.Epoch}; resuming");
    }

    private void QueueResyncResumeToPeers(SessionGeneration generation, Action<bool> completed)
    {
        var peers = new List<PeerLink>(_peers);
        if (peers.Count == 0) { completed(true); return; }
        int remaining = peers.Count;
        int failed = 0;
        var body = HandshakeCodec.EncodeGeneration(generation);
        foreach (var peer in peers)
        {
            QueueControl(peer, ControlMessageType.ResyncResume, body, ok =>
            {
                if (!ok) Interlocked.Exchange(ref failed, 1);
                if (Interlocked.Decrement(ref remaining) == 0)
                    completed(failed == 0);
            });
        }
    }

    private void BeginInvokePeer(PeerLink link, Action action)
    {
        int attempt = link.Attempt;
        BeginInvokeUi(() =>
        {
            if (IsConnectionAttemptCurrent(attempt) && _peers.Contains(link)) action();
        });
    }

    /// <summary>
    /// Rebuild the frame driver from the current core state as a fresh frame-0 baseline: new
    /// pipeline, cleared checksums, reset pacing and drift baseline. In-flight pre-resync UDP
    /// datagrams carry the prior generation and are rejected before their frame data is decoded.
    /// </summary>
    private void RebuildDriver()
    {
        try { _driver?.Dispose(); } catch { } // release the old rollback ring before replacing it
        _driver = CreateDriver();
        _startEmuFrame = APIs.Emulation.FrameCount();
        lock (_hashLock) { _checksums.Clear(); }
        // Every rebuild is a fresh identical baseline — the moment divergence learning restarts.
        ResetDivergenceLearning();
        _driver.Start();
        _lastResyncStamp = MonotonicNow();
        RebaseFrameSchedule();
    }

    /// <summary>Discard wall-clock debt after a protocol pause without changing the monotonic
    /// clock used by UI, logging, and UDP-recovery timestamps.</summary>
    private void RebaseFrameSchedule()
    {
        if (!_paceClock.IsRunning) _paceClock.Start();
        // A checksum owed by the timeline that just ended must not be taken against the new one:
        // the strategy is replaced by a rebuild, and the frame it described no longer exists.
        _checksumDue = false;
        _schedule.RebaseTo(_paceClock.Elapsed.TotalMilliseconds);
        // The pause froze stepping but not the FPS sample clock — restart the sample so the
        // first post-resume status line doesn't read ~0 fps and flash "CPU-bound" (KI-7).
        _fpsClock.Restart();
        _fpsCount = 0;
        _actualFps = -1;
        // Same reason for the pacing window: ticks that elapsed while frozen aren't frame ticks,
        // and counting them would show a stall rate the running session never had.
        _pacing.Reset();
        _lastPacing = default;
        // And for the gap stamps, which Reset() cannot reach: left standing, the first tick after
        // the freeze fed the entire freeze — seconds, on a heavy-core state transfer — into the
        // tick and present interval stats, so one resync put a multi-thousand-ms max in the gap
        // column, guaranteed one judder count, and inflated the very mean the presentation hint
        // quotes back to the user as advice.
        _lastTickClockMs = -1;
        _lastPresentClockMs = -1;
        _stallHint.RestartWindow();
        _presentHint.RestartWindow();
        _saveRateHint.RestartWindow();
    }

    private SessionGeneration CurrentGeneration
    {
        get { lock (_generationLock) return _generation; }
    }

    private void SetGeneration(SessionGeneration generation)
    {
        if (!generation.IsValid) throw new ArgumentOutOfRangeException(nameof(generation));
        lock (_generationLock)
        {
            _generation = generation;
            lock (_pingLock)
            {
                _frameAdvantage.Reset();
                foreach (var link in _peers)
                {
                    link.LocalAdvantage = 0;
                    link.RemoteAdvantage = 0;
                    link.AdvantageKnown = false;
                    link.PacingSendSequence = 0;
                    link.LastReceivedPacingSequence = 0;
                    link.AwaitingAppliedEpoch = 0;
                    Interlocked.Exchange(ref link.AppliedDeadlineTicks, 0);
                }
            }
        }
    }

    private SessionGeneration AdvanceGeneration()
    {
        var next = CurrentGeneration.Next();
        SetGeneration(next);
        return next;
    }

    /// <summary>
    /// Build the frame driver for the negotiated <see cref="_mode"/>: rollback plugs the
    /// <see cref="RollbackStrategy"/> (with its savestate ring depth) in behind the same seam
    /// lockstep uses, and widens the driver's network window to the ring depth so late corrections
    /// reach the pipeline. Used for both session start and resync rebuilds so they never diverge.
    /// </summary>
    private FrameDriver CreateDriver()
    {
        var driver = _mode == SyncMode.Rollback
            ? new FrameDriver(_adapter!, _transport!,
                p => new RollbackStrategy(p, _adapter!, _localPort, _rollbackDepth, FrameMs(),
                    RollbackTuningForSession()),
                _localPort, _sessionDelay, redundancy: 8, rollbackWindow: _rollbackDepth,
                portCount: _playerCount, generation: CurrentGeneration)
            : new FrameDriver(_adapter!, _transport!, p => new LockstepStrategy(p),
                _localPort, _sessionDelay, redundancy: 8, portCount: _playerCount,
                generation: CurrentGeneration);

        // Re-vacate the empty seats on every rebuild, while the driver is still fresh — the one
        // moment a vacate is safe (see InputPipeline.Vacate). Without this, the first resync after
        // a player left would resurrect their seat as a port the watchdogs wait on forever.
        foreach (int port in _vacatedPorts)
            if (port != _localPort && port < _playerCount) driver.VacatePort(port);
        return driver;
    }

    /// <summary>
    /// Record that a seat's player is gone for good, and keep every consumer consistent: the live
    /// driver stops watching the port (safe — every caller reaches this either in the same UI
    /// callback that immediately rebuilds, or with the driver frozen at frame 0), the relay stops
    /// carrying its legs, and the checksum quorum shrinks so frames still resolve.
    /// </summary>
    private void MarkSeatVacated(int port)
    {
        if (port == _localPort || port < 0 || port >= _playerCount) return;
        if (!_vacatedPorts.Add(port)) return;
        _vacatedCount = _vacatedPorts.Count;
        _relayPairs.RemoveWhere(pair => pair.A == port || pair.B == port);
        _portTokens.Remove(port); // no rejoin path into a vacated seat; the token dies with it
        try { _driver?.VacatePort(port); } catch { /* a rebuild re-applies it regardless */ }
        // The learner's quorum changed; a learner waiting on the departed seat's reports would
        // never complete. The rebuild that follows every vacate resets it again harmlessly.
        ResetDivergenceLearning();
    }

    /// <summary>Joiner: the host says a seat is permanently empty. Arrives ordered ahead of the
    /// rebuild that makes it true, so the set is in place when the new baseline lands.</summary>
    private void OnSeatVacated(SessionGeneration generation, int port)
    {
        if (!_phase.IsActive || _isHost) return;
        // Decided at the host's then-current generation; by the time it lands ours is either the
        // same (leave flow: sent before the BEGIN) or one behind (timeout flow: our BEGIN already
        // announced the next). Anything else is a dead timeline's copy.
        if (generation != CurrentGeneration && generation != CurrentGeneration.Next()) return;
        if (port == _localPort || port < 0 || port >= _playerCount) return;
        MarkSeatVacated(port);
        _meshOthers.RemoveAll(route => route.RemotePort == port);
        ApplyJoinerMesh();
        ConnLog($"P{port + 1} left the session — their seat stays empty and play continues with " +
                $"{_playerCount - _vacatedCount} of {_playerCount} players.", Color.DarkSlateBlue);
    }

    /// <summary>
    /// How this peer spends its savestate budget. Purely local — see <see cref="RollbackTuning"/>
    /// for why none of it is negotiated. The anchor interval MUST track
    /// <see cref="_checksumInterval"/>, since eliding snapshots would otherwise take the checksum's
    /// own state with them and stop desync detection without saying so.
    /// </summary>
    private RollbackTuning RollbackTuningForSession() => new()
    {
        ElideConfirmedSaves = true,
        ChecksumAnchorInterval = _checksumInterval,
        KeyframeInterval = _sessionKeyframeInterval,
        RepairBudgetMs = RepairBudgetFrames * FrameMs(),
        SeedRepairPerFrameMs = _probeRepairPerFrameMs,
        Clock = new StopwatchClock(),
    };

    /// <summary>
    /// How many predicted frames apart this session's snapshots sit, solved by the capability
    /// probe from THIS core's measured save and frame costs
    /// (<see cref="CapabilityProbe.SolveKeyframeInterval"/>).
    ///
    /// The snapshot is what a repair spends its budget on: measured on N64 over eight stationary
    /// runs, a repaired frame costs 9.24ms of which 6.82ms is the snapshot and 2.41ms is the
    /// frame. Spacing them apart costs at most one extra re-simulated frame per correction and
    /// buys prediction depth — worth about 17ms of hideable one-way latency per frame gained.
    ///
    /// It was the constant 2 for every core, which is N64's answer: that ratio is nearly 3:1 there
    /// and well under 1:1 on the Hawk cores, where the walk-back is pure loss. The probe measures
    /// both terms anyway and the depth model already takes the spacing exactly, so the machine
    /// picks its own. The value MUST be the one the probe solved against — a depth checked at one
    /// spacing says nothing about a repair performed at another — so it is written only there.
    ///
    /// 1 until a probe has run, which is also what a lockstep-only session (that never pays for
    /// one) correctly gets: snapshot every predicted frame, the original behaviour.
    /// </summary>
    private int _sessionKeyframeInterval = 1;

    // Reflection flags for reaching EmuHawk internals (Tools/LuaConsole members aren't all public).
    private const System.Reflection.BindingFlags AnyInstance =
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    /// <summary>
    /// Refuse to start under an active movie; warn (never block) about Lua.
    ///
    /// The movie case used to be a warning too, and that was the wrong severity. The session steps
    /// the core through IEmulator.FrameAdvance directly, so MovieSession.HandleFrameBefore/After —
    /// which MainForm calls around ITS core step — never run. A RECORDING movie therefore silently
    /// stops recording the frames actually being emulated: the file that comes out is garbage, and
    /// nothing says so. A movie in PLAYBACK is the mirror image: the movie layer sits downstream of
    /// the controller netplay reads, so the session captures the user's live pad while the user
    /// believes the movie is driving. Neither is a session that "might desync" — it is a movie
    /// being quietly ruined or quietly ignored, so it is refused with the reason named. A FINISHED
    /// movie injects nothing and only warns.
    ///
    /// Lua stays a warning: scripts can interfere but usually don't, and blocking every session for
    /// an idle overlay script would cost more than it saves. Everything here is best-effort and
    /// swallows its own errors (EmuHawk types resolved by name; no compile-time dependency).
    /// </summary>
    /// <returns>True if the session must not start.</returns>
    private bool SessionHazardsBlockStart()
    {
        try
        {
            if (APIs.Movie.IsLoaded())
            {
                string mode = ""; try { mode = APIs.Movie.Mode() ?? ""; } catch { }
                bool rec = string.Equals(mode, "RECORD", StringComparison.OrdinalIgnoreCase);
                bool play = string.Equals(mode, "PLAY", StringComparison.OrdinalIgnoreCase);
                if (rec || play)
                {
                    ConnLog(rec
                        ? "a movie is RECORDING (or TAStudio is open). Netplay steps the core itself, " +
                          "so the recorder would never see the frames actually played and the file " +
                          "would be garbage. Stop the movie (Movie → Stop), then start again."
                        : "a movie is PLAYING (or TAStudio is open). Netplay reads the controller, not " +
                          "the movie, so the session would ignore the playback you are watching. Stop " +
                          "the movie (Movie → Stop), then start again.", Color.Firebrick);
                    return true;
                }
                Log("note: a finished movie is loaded — harmless, but Movie → Stop clears it.");
            }
        }
        catch { /* movie API unavailable — ignore */ }

        try
        {
            // Same argument as the movie above, and the same gate is responsible: MainForm's
            // AvFrameAdvance is inside the frame-advance block a session suppresses, so an AVI/WAV
            // capture records nothing at all — silently, producing a valid-looking empty file.
            if (AviCaptureActive())
            {
                ConnLog("an A/V capture is running (File → AVI/WAV Writer). Netplay steps the core " +
                        "itself, so the writer would never see the frames actually played and the " +
                        "file would come out empty. Stop the capture, then start again.",
                    Color.Firebrick);
                return true;
            }
        }
        catch { /* capture state unreachable — not a reason to refuse a session */ }

        try
        {
            int lua = RunningLuaScripts();
            if (lua > 0)
                Log($"WARNING: {lua} Lua script(s) running — Lua can set input, load states, or call " +
                    "emu.frameadvance and may desync netplay. Stop them (Lua Console → Stop All Scripts) if you see desyncs.");
        }
        catch { /* best-effort — ignore */ }
        return false;
    }

    /// <summary>Whether EmuHawk is writing an A/V capture. The field is private, so this is
    /// by-name reflection like the Lua probe below; any failure reports "no capture", because a
    /// diagnostic that cannot read the state must not be the thing that refuses a session.</summary>
    private bool AviCaptureActive()
    {
        try
        {
            object? mf = MainForm;
            if (mf == null) return false;
            var field = mf.GetType().GetField("_currAviWriter", AnyInstance);
            return field?.GetValue(mf) != null;
        }
        catch { return false; }
    }

    /// <summary>Count Lua scripts in the RUNNING state, only if the Lua Console is already open (never
    /// instantiates it). EmuHawk types are resolved by name; any failure returns 0 (a warning-only
    /// heads-up must never disrupt a session).</summary>
    private int RunningLuaScripts()
    {
        try
        {
            object? mf = MainForm;
            if (mf == null) return 0;
            var asm = mf.GetType().Assembly;
            var luaConsoleType = asm.GetType("BizHawk.Client.EmuHawk.LuaConsole");
            if (luaConsoleType == null) return 0;
            var tools = mf.GetType().GetField("Tools", AnyInstance)?.GetValue(mf)
                     ?? mf.GetType().GetProperty("Tools", AnyInstance)?.GetValue(mf);
            if (tools == null) return 0;
            var isLoaded = tools.GetType().GetMethod("IsLoaded", [typeof(Type)]);
            if (!(isLoaded?.Invoke(tools, [luaConsoleType]) as bool? ?? false)) return 0; // closed
            var console = tools.GetType().GetProperty("LuaConsole", AnyInstance)?.GetValue(tools);
            var luaImp = console?.GetType().GetField("LuaImp", AnyInstance)?.GetValue(console)
                      ?? console?.GetType().GetProperty("LuaImp", AnyInstance)?.GetValue(console);
            var scriptList = luaImp?.GetType().GetProperty("ScriptList", AnyInstance)?.GetValue(luaImp)
                as System.Collections.IEnumerable;
            if (scriptList == null) return 0;
            int running = 0;
            foreach (var file in scriptList)
            {
                var state = file?.GetType().GetProperty("State", AnyInstance)?.GetValue(file);
                if (state != null && string.Equals(state.ToString(), "Running", StringComparison.OrdinalIgnoreCase))
                    running++;
            }
            return running;
        }
        catch { return 0; }
    }

    /// <summary>Wrap the input transport in the artificial-latency simulator if the diagnostic is set.</summary>
    private ITransport WrapSimLatency(ITransport inner)
        => _simLatencyMs > 0 ? new LatencySimTransport(inner, _simLatencyMs) : inner;

}
