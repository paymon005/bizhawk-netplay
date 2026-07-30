using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using BizHawk.Client.Common;
using BizHawk.Client.EmuHawk;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Probe;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;

namespace BizHawkNetplay.Tool
{
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
            lock (_hashLock)
            {
                if (!IsConnectionAttemptCurrent(attempt) || !_sessionActive || !_isHost
                    || generation != CurrentGeneration) return;
                outcome = _checksums.Record(generation, sourcePort, frame, hash, _playerCount);
            }
            if (outcome == ChecksumOutcome.Pending) return;
            if (outcome == ChecksumOutcome.Mismatch)
            {
                BeginInvokeUi(() =>
                {
                    if (IsConnectionAttemptCurrent(attempt) && CurrentGeneration == generation)
                        OnHostDesync(frame);
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

        private void OnHostDesync(int frame)
        {
            if (_resyncInProgress) return;
            if (MonotonicElapsedSeconds(_lastResyncStamp) < ResyncGraceSeconds) return; // just resynced; give it time
            Log($"DESYNC at frame {frame} — peers disagree");
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

        /// <summary>
        /// Host recovery: capture an authoritative state on the emulator thread, enqueue its transfer
        /// to each peer's writer, and rebuild from a clean baseline. Simulation stays paused while the
        /// writers handle socket/flow-control waits, but WinForms remains responsive.
        /// Bounded by <see cref="MaxResyncs"/> so a persistent (non-transient) desync gives up instead
        /// of looping; a short grace window debounces repeat triggers for the same desync.
        /// </summary>
        private void PerformResyncAsHost()
        {
            if (!_sessionActive || !_isHost) return;
            var gate = RecoveryPolicy.GateResync(_resyncInProgress,
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
        private void ShipAuthoritativeState(string label, bool isSettingsChange)
        {
            int attempt = CurrentConnectionAttempt;
            try
            {
                var state = _adapter!.ExportState();
                var generation = AdvanceGeneration();
                _resyncInProgress = true;
                _resyncReleaseQueued = false;
                RebuildDriver();
                RefreshLiveSettingsUi();
                int peerCount = _peers.Count;
                var generationBody = ControlMessageCodec.EncodeResyncBegin(generation, state.Length,
                    _sessionDelay, _mode, waitSeconds: 0, isSettingsChange: isSettingsChange);
                var stateBody = ControlMessageCodec.EncodeStatePayload(generation, state);
                Status($"{label}: sending epoch {generation.Epoch} " +
                    $"({state.Length / 1024}KiB) to {peerCount} peer(s)…", Color.DarkOrange);
                Log($"{label}: captured {state.Length / 1024}KiB for epoch " +
                    $"{generation.Epoch}; waiting for every peer to import it");

                if (peerCount == 0)
                {
                    _resyncInProgress = false;
                    RebaseFrameSchedule();
                    RefreshLiveSettingsUi();
                    return;
                }
                foreach (var link in _peers)
                {
                    GraceForStateTransfer(link, state.Length); // it can't pong while its reader consumes the frame
                    link.AwaitingAppliedEpoch = generation.Epoch;
                    Interlocked.Exchange(ref link.AppliedDeadlineTicks, StateApplyDeadlineTicks(state.Length));
                    if (!QueueControl(link, ControlMessageType.ResyncBegin, generationBody)
                        || !QueueControl(link, ControlMessageType.Resync, stateBody, ok =>
                        {
                            if (!ok) BeginInvokeUi(() =>
                            {
                                if (IsConnectionAttemptCurrent(attempt) && _sessionActive
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
            catch (Exception ex) { EndSession("resync failed: " + ex.Message); }
        }

        /// <summary>
        /// Host: change the netcode and/or input delay of a running session. The session does not end
        /// and nobody reconnects — everyone is rebuilt onto one state at a new generation under the new
        /// parameters, which is exactly what a desync resync already does. Costs a brief pause and one
        /// savestate transfer.
        /// </summary>
        private void ApplyLiveSettingsAsHost()
        {
            if (!_sessionActive || !_isHost) return;
            if (_resyncInProgress || _awaitingReconnect)
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
            if (!_sessionActive || _isHost || generation != CurrentGeneration.Next()) return;
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
                _resyncInProgress = true;
                if (isSettingsChange && (inputDelay != _sessionDelay || mode != _mode))
                    ConnLog($"the host changed the session to {DescribeMode(mode)} at input delay " +
                            $"{inputDelay} — staying connected through the rebuild.", Color.DarkSlateBlue);
                Status($"applying {state.Length / 1024}KiB host resync epoch {generation.Epoch}…",
                    Color.DarkOrange);
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
                            if (IsConnectionAttemptCurrent(attempt) && _sessionActive
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
            if (!_sessionActive || !_isHost || generation != CurrentGeneration || !_peers.Contains(link)) return;
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
            if (_resyncReleaseQueued || generation != CurrentGeneration) return;
            _resyncReleaseQueued = true;
            int attempt = CurrentConnectionAttempt;
            QueueResyncResumeToPeers(generation, ok => BeginInvokeUi(() =>
            {
                if (!IsConnectionAttemptCurrent(attempt) || !_sessionActive
                    || CurrentGeneration != generation) return;
                if (!ok) { EndSession("resync resume transfer failed"); return; }
                _driver?.ResetRemoteInputLiveness();
                _resyncInProgress = false;
                RebaseFrameSchedule();
                RefreshLiveSettingsUi();
                Status($"in session — {DescribeMode(_mode)}, you are P{_localPort + 1}/{_playerCount}, " +
                       $"delay {_sessionDelay}", Color.Green);
                Log($"every peer applied epoch {generation.Epoch}; resuming");
            }));
        }

        private void ResumeResyncAsJoiner(SessionGeneration generation)
        {
            if (!_sessionActive || _isHost || generation != CurrentGeneration || !_resyncInProgress) return;
            _driver?.ResetRemoteInputLiveness();
            _resyncInProgress = false;
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
            _driver.Start();
            _lastResyncStamp = MonotonicNow();
            RebaseFrameSchedule();
        }

        /// <summary>Discard wall-clock debt after a protocol pause without changing the monotonic
        /// clock used by UI, logging, and UDP-recovery timestamps.</summary>
        private void RebaseFrameSchedule()
        {
            if (!_paceClock.IsRunning) _paceClock.Start();
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
            _stallHint.RestartWindow();
            _presentHint.RestartWindow();
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
            if (_mode == SyncMode.Rollback)
                return new FrameDriver(_adapter!, _transport!,
                    p => new RollbackStrategy(p, _adapter!, _localPort, _rollbackDepth, FrameMs(),
                        RollbackTuningForSession()),
                    _localPort, _sessionDelay, redundancy: 8, rollbackWindow: _rollbackDepth,
                    portCount: _playerCount, generation: CurrentGeneration);

            return new FrameDriver(_adapter!, _transport!, p => new LockstepStrategy(p),
                _localPort, _sessionDelay, redundancy: 8, portCount: _playerCount,
                generation: CurrentGeneration);
        }

        /// <summary>
        /// How this peer spends its savestate budget. Purely local — see <see cref="RollbackTuning"/>
        /// for why none of it is negotiated. The anchor interval MUST track
        /// <see cref="ChecksumInterval"/>, since eliding snapshots would otherwise take the checksum's
        /// own state with them and stop desync detection without saying so.
        /// </summary>
        private RollbackTuning RollbackTuningForSession() => new RollbackTuning
        {
            ElideConfirmedSaves = true,
            ChecksumAnchorInterval = ChecksumInterval,
            KeyframeInterval = RepairKeyframeInterval,
            RepairBudgetMs = RepairBudgetFrames * FrameMs(),
            Clock = new StopwatchClock(),
        };

        /// <summary>
        /// Snapshot every other predicted frame rather than every one.
        ///
        /// The snapshot is what a repair spends its budget on: measured on N64 over eight stationary
        /// runs, a repaired frame costs 9.24ms of which 6.82ms is the snapshot and 2.41ms is the frame.
        /// Halving the snapshots costs at most one extra re-simulated frame per correction, and buys a
        /// frame of prediction depth — worth about 17ms of hideable one-way latency.
        ///
        /// The second effect is the one that shows up as feel rather than as a number. A worst-case
        /// repair at this depth costs ~27ms against ~31.5ms before, which is the first time it fits
        /// inside <see cref="TickBudgetMs"/> (~28.4ms at 60Hz). Until now every max-depth correction
        /// overran its own tick and landed as a hitch.
        ///
        /// Not raised further on purpose: past 3 the walk-back to the nearest keyframe costs more
        /// frames than the snapshots it avoids, and the measured depth goes back down.
        /// </summary>
        private const int RepairKeyframeInterval = 2;

        // Reflection flags for reaching EmuHawk internals (Tools/LuaConsole members aren't all public).
        private const System.Reflection.BindingFlags AnyInstance =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        /// <summary>
        /// Log a heads-up (NEVER block) if something that also owns input or the frame clock is active —
        /// a loaded movie (playback/record, incl. TAStudio) or a running Lua script. These can desync
        /// netplay, but we only warn and let the user proceed. Everything here is best-effort and swallows
        /// its own errors: it must never disrupt starting a session (and resolves EmuHawk types by name,
        /// so this file carries no compile-time dependency on them).
        /// </summary>
        private void WarnSessionHazards()
        {
            try
            {
                if (APIs.Movie.IsLoaded())
                {
                    string mode = ""; try { mode = APIs.Movie.Mode() ?? ""; } catch { }
                    bool rec = string.Equals(mode, "RECORD", StringComparison.OrdinalIgnoreCase);
                    Log($"WARNING: a movie is {(rec ? "recording" : "loaded")} (or TAStudio is open) — it injects " +
                        "input and drives frame advance, which will likely desync netplay. Stop it if the session won't sync.");
                }
            }
            catch { /* movie API unavailable — ignore */ }

            try
            {
                int lua = RunningLuaScripts();
                if (lua > 0)
                    Log($"WARNING: {lua} Lua script(s) running — Lua can set input, load states, or call " +
                        "emu.frameadvance and may desync netplay. Stop them (Lua Console → Stop All Scripts) if you see desyncs.");
            }
            catch { /* best-effort — ignore */ }
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
                var isLoaded = tools.GetType().GetMethod("IsLoaded", new[] { typeof(Type) });
                if (!(isLoaded?.Invoke(tools, new object[] { luaConsoleType }) as bool? ?? false)) return 0; // closed
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
}
