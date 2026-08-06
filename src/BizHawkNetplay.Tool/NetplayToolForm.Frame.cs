using System;
using System.Drawing;
using System.Threading;
using BizHawk.Client.Common;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    // --- State used only by this file (everything shared stays in NetplayToolForm.cs) ---
    /// <summary>
    /// How often EmuHawk calls into this tool from its own run loop, per pacing window.
    ///
    /// This is the number that decides whether frame pacing is fixable at all. Our tick can only
    /// run when EmuHawk's ProgramRunLoop hands the UI thread over, so that loop's rate is a hard
    /// ceiling on ours. If this reads in the hundreds, WM_TIMER's coalescing is what has been
    /// limiting us to sixty jittery ticks a second and moving the clock here fixes it. If it reads
    /// about sixty, we are inheriting EmuHawk's own cadence and no clock of ours can beat it —
    /// which would be worth saying plainly rather than attempting a fourth mechanism.
    /// </summary>
    private int _emuLoopCallsWindow;
    /// <summary>
    /// Which clock is actually driving frames, counted per pacing window.
    ///
    /// Worth keeping because the first attempt at this — moving the clock to Application.Idle —
    /// changed the measured judder by nothing at all, and it took counters to establish that the
    /// handler had never once run. EmuHawk drives its own loop rather than Application.Run, and
    /// Application.DoEvents does not raise Idle. `timer` staying at zero is now the evidence that
    /// the fine clock is doing its job; a session where it climbs is one where UpdateValues has
    /// stopped arriving and the heartbeat has taken over.
    /// </summary>
    private int _emuLoopTicksWindow;
    private bool _frameTickRunning;
    /// <summary>A frame completed and its checksum has not been taken yet — see where it is set.</summary>
    private bool _checksumDue;
    private int _timerTicksWindow;

    /// <summary>
    /// How long one frame-tick callback may spend before it must return to the message loop.
    ///
    /// This has to scale with the console's frame period, not sit at a fixed 8ms. The second-frame
    /// gate below requires <c>elapsed + 2·recentCoreFrameMs &lt; budget</c>, so a flat 8ms made
    /// catch-up unreachable for any core costing more than ~4ms a frame — on N64 (~10-16ms) the
    /// test could never pass. Lost wall-clock time was then never repaid: it accumulated until the
    /// rebase above discarded roughly three frames in one lump, which reads as "CPU-bound" in the
    /// status bar even when the core is comfortably inside budget.
    ///
    /// 1.7 frame periods (~28ms at 60Hz) lets exactly one catch-up frame through while staying
    /// close enough to a frame period that the window never feels unresponsive. The hard
    /// <see cref="MaxFramesPerTick"/> cap, the pessimistic <c>_recentCoreFrameMs</c> estimate and
    /// the mid-burst audio pump are what keep that safe; this only stops the budget from
    /// forbidding the burst outright.
    /// </summary>
    private double TickBudgetMs() => _schedule.BudgetMs;

    /// <summary>
    /// Report what the OS actually gives us for a short sleep, once per session.
    ///
    /// The frame tick rides WM_TIMER, whose delivery is bound to the system clock tick, and on
    /// Windows 11 <c>timeBeginPeriod</c> is per-process and may be ignored for a window that isn't
    /// in the foreground — which is exactly our case when the second instance has focus. Since a
    /// frame is presented at most once per tick, that granularity is a hard ceiling on presented
    /// fps, so it's worth measuring rather than assuming. Near 1ms means a finer frame clock is
    /// available; near 15ms means WM_TIMER can't do much better than one tick per frame.
    /// </summary>
    private void LogTimerGranularity()
    {
        try
        {
            const int probes = 5;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < probes; i++) Thread.Sleep(1);
            double perSleep = sw.Elapsed.TotalMilliseconds / probes;
            Log($"timer granularity: Sleep(1) averages {perSleep:F2}ms against a " +
                $"{_frameMs:F2}ms frame period — the frame tick cannot beat this.");
        }
        catch { }
    }

    /// <summary>
    /// EmuHawk's own per-loop-iteration callback into external tools, and the session's real frame
    /// clock. Counted as well as used — see <see cref="_emuLoopCallsWindow"/> for why its rate is
    /// the question that matters.
    ///
    /// WM_TIMER cannot pace a frame clock, which is what led here. It is a synthesised,
    /// lowest-priority message: generated only when the queue is otherwise empty, never more than
    /// one queued, delivered on the system tick rather than on demand. `timeBeginPeriod(1)` buys a
    /// 1.1ms Sleep and changes none of that — a session measured ticks arriving 0.6ms to 33ms
    /// apart, averaging 60/s against a 10ms interval that should have produced 100. Frames landed
    /// at arbitrary phase against the 16.688ms boundary, so a tick that ran no frame was followed
    /// by one that ran two and showed only the second: judder measured 15% of presents, peaking at
    /// 28%, on a machine idle 94% of the time — a scheduling failure, not a capacity one.
    /// <see cref="_frameTimer"/> stays running underneath as a heartbeat for the case where this
    /// callback stops arriving, so the worst this can do is behave as it did before.
    /// </summary>
    public override void UpdateValues(ToolFormUpdateType type)
    {
        base.UpdateValues(type);

        // Reassert on EVERY delivery, then treat only General as the clock. This method is not
        // called solely from the run loop's once-per-iteration spot: MainForm also delivers
        // PostFrame after a savestate load, and once per loop iteration while the Rewind hotkey is
        // held — a path that additionally suspends the pause throttle, so treating those as
        // fine-clock ticks meant a held hotkey double-ticked the frame loop at an unthrottled
        // rate. The reassertion stays unconditional because every delivery point sits just before
        // somewhere the emulator could step.
        // Before the pause reassertion: if the core has been swapped under us there is nothing left
        // worth asserting, and holding EmuHawk paused through its own ROM load would be actively
        // unhelpful. FrameTick checks the same thing — this is the earlier of the two doors.
        if ((_pausedByUs || _phase.IsActive) && !CoreStillOurs())
        {
            EndSession("the ROM was closed or changed underneath the session");
            return;
        }
        if (_pausedByUs) ReassertPause();
        if (type != ToolFormUpdateType.General) return;
        _emuLoopCallsWindow++;

        // This is the frame clock. EmuHawk calls it once per iteration of the loop that owns the UI
        // thread, so nothing we could install can fire between two iterations of the loop we are
        // running inside.
        //
        // It is NOT the ~3200/s this comment used to claim. That figure is the unthrottled loop, and
        // a session is not unthrottled — it is PAUSED, and BizHawk's Throttle.Step opens with
        // "if (signal_paused && !signal_continuousFrameAdvancing) { Thread.Sleep(15); return; }",
        // unconditionally. So the ceiling here is ~66/s, which is what the pacing line's `tick N/s`
        // has been reporting all along (50-65 in real sessions).
        //
        // That makes it comparable in RATE to the WM_TIMER fallback, not thirty times better. Its
        // advantage is placement, not frequency: it runs inside EmuHawk's loop immediately before
        // StepRunLoop_Core, rather than as a separate message subject to coalescing — which is also
        // why the pause reassertion above happens on every delivery, from the moment we take the
        // clock at the lobby (_pausedByUs is the ownership flag, not _phase.IsActive).
        if (!_phase.IsActive || _driver == null) return;
        double nowMs = _paceClock.Elapsed.TotalMilliseconds;
        if (!_schedule.ShouldWake(nowMs, FineClockWakeMarginMs)) return;
        if (nowMs - _lastFineTickMs < FineClockMinSpacingMs) return;
        _lastFineTickMs = nowMs;
        _emuLoopTicksWindow++;
        FrameTick(fromFineClock: true);
    }

    /// <summary>Stops the heartbeat timer. The fine clock needs no unhooking — it is EmuHawk's own
    /// callback, gated on <see cref="SessionPhase.IsActive"/>, so clearing that flag is what stops
    /// it. The savestate pool is NOT released here: this runs before TeardownNetwork disposes the
    /// driver, and disposing the rollback ring pushes every buffer back INTO the pool — clearing
    /// first meant the "hand the memory back for the idle" promise was inverted, with up to a
    /// ring's worth of full-size states pinned on the large object heap until the next session.
    /// The release lives at the end of teardown, after the refill.</summary>
    private void StopFramePacing()
    {
        _frameTimer.Stop();
    }

    /// <summary>
    /// Worst frame-decision cost, and how many were bad, since the pacing line last printed.
    ///
    /// These are kept here rather than in <see cref="PacingStats"/> because the pacing window and
    /// the pacing *print* run on different clocks — the window rolls over every 500ms inside
    /// UpdateSessionUi, the line prints once a second — so roughly every other window is summarized
    /// into <c>_lastPacing</c> and then overwritten before anyone reads it. A one-off spike landing
    /// in a dropped window leaves no trace at all, and a spike that does survive still cannot move
    /// <c>gate p95</c>, which is nearest-rank over ~30 samples and therefore reports the second
    /// largest by construction. That is why a session showed `gate mean 0.0 p95 0.0` in every window
    /// while individual ticks were spending 55.8ms there. These two carry across dropped windows so
    /// the worst case can never go unreported again.
    /// </summary>
    private double _worstGateSinceLogMs;
    private int _gateSpikesSinceLog;

    /// <summary>
    /// Frame-decision cost above which something unexplained happened. A repair's honest cost is
    /// one state load plus its re-simulated frames plus their snapshots; at the shallow depths a
    /// healthy link produces that is ~1-2ms on Genesis and under ~19ms on N64 even at depth 1 with
    /// every snapshot taken. Five is clear of the former and well under the latter, so on a light
    /// core this only fires for something the repair model does not account for.
    /// </summary>
    private const double GateSpikeMs = 5.0;

    /// <summary>Collections observed inside the frame decision, per pacing window.</summary>
    private int _gcGateWindow0, _gcGateWindow1, _gcGateWindow2;

    /// <summary>
    /// Record one frame-decision sample: its cost against the spike accounting, and how many
    /// collections happened while it ran. Allocation-free; called on both the stall and ready paths
    /// so neither can hide a spike from the other.
    /// </summary>
    private void NoteGate(double gateMs, int g0Before, int g1Before, int g2Before,
                          ref int gcGate0, ref int gcGate1, ref int gcGate2)
    {
        if (gateMs > _worstGateSinceLogMs) _worstGateSinceLogMs = gateMs;
        if (gateMs >= GateSpikeMs) _gateSpikesSinceLog++;

        int d0 = GC.CollectionCount(0) - g0Before;
        int d1 = GC.CollectionCount(1) - g1Before;
        int d2 = GC.CollectionCount(2) - g2Before;
        gcGate0 += d0; gcGate1 += d1; gcGate2 += d2;
        _gcGateWindow0 += d0; _gcGateWindow1 += d1; _gcGateWindow2 += d2;
    }

    /// <param name="fromFineClock">
    /// True when EmuHawk's own run loop is calling us (<see cref="UpdateValues"/>), false for the
    /// WinForms fallback timer. The two differ in one respect that matters below: only the fine
    /// clock has MainForm.Render() waiting two statements behind it.
    /// </param>
    /// <summary>
    /// Whether the core this session was built against is still the one EmuHawk is holding.
    ///
    /// A ROM load disposes the old core and only then reassigns <c>MainForm.Emulator</c>, and the
    /// hook that lets a tool tear down first — AskSaveChanges — is skipped entirely when the user
    /// has ticked Config → Customize → "Supress 'Ask Save Changes'". With it skipped, nothing stops
    /// the frame timer, and every modal EmuHawk shows on the way into the next ROM (missing
    /// firmware, the archive chooser, the platform picker) pumps messages — delivering a tick that
    /// steps a disposed core. On a waterbox core that is a native access violation, which no catch
    /// on this side survives.
    ///
    /// The window between Dispose and the reassignment contains no message pump, so comparing
    /// references closes it. Cheap enough for the top of every tick: one interface call and a
    /// reference compare.
    /// </summary>
    private bool CoreStillOurs()
    {
        try
        {
            if (MainForm is not BizHawk.Client.EmuHawk.MainForm mf) return true; // can't tell — assume yes
            return ReferenceEquals(_emulator, mf.Emulator);
        }
        catch { return true; }
    }

    private void FrameTick(bool fromFineClock)
    {
        if (!_phase.IsActive || _driver == null) return;
        if (!CoreStillOurs())
        {
            EndSession("the ROM was closed or changed underneath the session");
            return;
        }
        // Snapshot the field into a local for the rest of the tick. The null check above does not
        // survive the first call the compiler cannot see inside — a field could be reassigned by
        // anything — so every later use was a nullable dereference, which is what CS8602 was
        // pointing at. A local is the fix; suppressing it with ! would only hide the same question.
        var driver = _driver;
        if (_frameTickRunning) return;

        // Deliberately NOT stopping the timer here. Stopping on entry and restarting in the finally
        // made each period (interval + tick work + message-queue latency) instead of just the
        // interval, because Start() re-arms SetTimer from zero. With ~10ms of enforced interval and
        // a few ms of work that measured at ~26ms — about 38 callbacks a second. Since the frame is
        // presented once per callback, the picture was capped near 38fps while the core happily
        // emulated 60: the "60fps but choppy" report. Left free-running, WM_TIMER re-arms itself and
        // coalesces (never more than one queued), and _frameTickRunning below is what actually keeps
        // a nested message pump from reentering us.
        _frameTickRunning = true;
        long tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
        double coreMs = 0, gateMs = 0, renderMs = 0;
        // Everything else the tick does, so a slow one can be attributed instead of guessed at.
        // A report that itemises 0.8ms of a 16.1ms tick names nothing: the four original terms
        // are the four cheap ones, and the interesting time was all in the unmeasured remainder.
        double audioMs = 0, emuApiMs = 0, uiMs = 0;
        // Collections during this tick, and the subset of them that landed inside the frame
        // decision. Both are needed: the tick figure says a pause happened at all, the gate figure
        // says it happened in the span that has been reporting impossible costs.
        int gcGate0 = 0, gcGate1 = 0, gcGate2 = 0;
        int gcTick0Before = GC.CollectionCount(0), gcTick1Before = GC.CollectionCount(1),
            gcTick2Before = GC.CollectionCount(2);
        int packetsDrained = 0;
        int frameForTelemetry = driver.CurrentFrame;
        _lastHashMs = 0;
        // Snapshot the repair counters so a slow tick can report what this tick actually did rather
        // than a session total. "rollback/gate" covers both repair and the savestate work around it,
        // and those need opposite fixes — a 190ms gate that turns out to have run no repair at all
        // is a different bug from one that resimulated forty frames.
        var tickRollback = driver.Strategy as RollbackStrategy;
        int repairsBefore = tickRollback?.RollbackCount ?? 0;
        long resimBefore = tickRollback?.FramesResimulated ?? 0;
        try
        {
            // Keep the audio device fed every tick, independent of how many frames we step this
            // tick (or none, during a stall) — the ring buffer decouples playback from stepping.
            double audioStart = ElapsedMs(tickStart);
            _adapter?.PumpAudio();
            audioMs = ElapsedMs(tickStart) - audioStart;

            // Last tick's checksum, before this tick's frame — here its cost lands in the slack
            // ahead of the frame decision instead of between the step and the present.
            if (_checksumDue)
            {
                _checksumDue = false;
                MaybeSendChecksum();
            }

            // Say the moment EmuHawk's sound device stops or restarts, not just that it is down
            // when something else already went wrong. Sampling it on a slow tick reports the
            // aftermath: in the one log where this was caught, the device had been down for
            // ~10 seconds of perfectly healthy ticks before anything else looked wrong, so the
            // edge is the only thing that dates it. Cheap: a bool compare per tick.
            if (_adapter != null)
            {
                bool devUp = _adapter.AudioDeviceStarted;
                if (devUp != _audioDevWasUp)
                {
                    _audioDevWasUp = devUp;
                    ConnLog(devUp
                        ? $"audio device restarted at frame {driver.CurrentFrame}"
                        : $"audio device STOPPED at frame {driver.CurrentFrame} — EmuHawk shut its "
                          + "sound output down. Note what happened just now (window focus, a headset or "
                          + "monitor connecting, a device change); it is not yet known what triggers this.",
                        devUp ? Color.DarkGreen : Color.DarkOrange);
                }
            }

            // Timed together because they are the same kind of thing: calls across the ApiHawk
            // boundary into EmuHawk, made once per tick for the whole session.
            double apiStart = ElapsedMs(tickStart);

            // Also here, not only in UpdateValues: the WinForms fallback clock calls FrameTick
            // directly and never passes through UpdateValues at all.
            ReassertPause();

            // If EmuHawk's own loop slipped in extra core frames (e.g. a brief unpause), our
            // counter and the core have diverged — report it plainly rather than as a desync.
            // _emulator.Frame directly: FrameCount() is exactly that field behind two interface
            // hops, and this runs per tick — the emuapi timing column should measure real work.
            int emuDelta = _emulator!.Frame - _startEmuFrame;
            emuApiMs = ElapsedMs(tickStart) - apiStart;
            if (emuDelta != driver.CurrentFrame)
            {
                int diff = emuDelta - driver.CurrentFrame;
                string why = diff > 0
                    ? $"EmuHawk advanced {diff} extra frame(s) — did you unpause?"
                    : $"the core's frame count jumped back {-diff} — a rewind/load-state hotkey fired?";
                EndSession(why + " The tool must own the frame clock; avoid EmuHawk hotkeys during a session.");
                return;
            }

            // Frozen while a dropped peer is being waited on — don't advance until the rejoin
            // resyncs everyone. Sticky pause and drift validation above must still run here.
            if (_phase.AwaitingRejoin)
            {
                if (_phase.IsRebuilding) driver.ResendLocalInputIfDue();
                MaybeSendPing();
                CheckLinkTimeouts();
                return;
            }

            // Drain once per callback; FrameDriver caps the number of datagrams consumed. This is
            // deliberately separate from input sends, so Pump + Capture cannot duplicate a packet.
            driver.PumpNetwork();
            packetsDrained = driver.LastPacketsDrained;

            // State capture/import stays on this thread, but whole-state transfer runs on each
            // peer's writer thread. Hold the new baseline while that transfer is in flight.
            if (_phase.IsRebuilding)
            {
                // Every peer may rebuild at a different instant. Keep publishing this epoch's
                // neutral/start window so an early sender is not lost by peers still rejecting
                // new-generation UDP with their old driver.
                driver.ResendLocalInputIfDue();
                MaybeSendPing();
                CheckLinkTimeouts();
                return;
            }

            double nowMs = _paceClock.Elapsed.TotalMilliseconds;
            if (_schedule.TryRebase(nowMs))
            {
                _pacingRebases++;
                _pacing.AddRebase();
            }

            bool steppedThisTick = false;
            bool stalledThisTick = false;
            bool timeSyncThisTick = false;
            int framesThisTick = 0;
            bool committedSecondFrame = false;
            while (_schedule.MayRunFrame(nowMs, framesThisTick))
            {
                // Normally this loop runs once. A second frame compensates for an irregular ~25ms
                // WinForms callback without reviving the old eight-frame catch-up bursts. Never start
                // that second frame after the callback has already consumed its UI work budget.
                if (framesThisTick > 0)
                {
                    if (!committedSecondFrame && ElapsedMs(tickStart) >= TickBudgetMs()) break;
                    // A frame of core execution just happened, and packets that landed during it are
                    // already queued. Draining once per tick would judge this frame's readiness on
                    // network state captured before that work — turning an input that did arrive in
                    // time into a stall that costs the whole tick.
                    driver.PumpNetwork();
                    packetsDrained += driver.LastPacketsDrained;
                    // A catch-up burst is the longest this callback ever goes without returning to
                    // the message loop, so top the ring up mid-tick rather than only at tick start.
                    _adapter?.PumpAudio();
                }

                // The pre-frame half of EmuHawk's own input bookkeeping — the two clears it
                // performs before stepping (MainForm.cs:2945-2946). Before the capture, as
                // upstream does it: a one-frame override cleared after the capture would be read
                // by the very capture it was meant to have expired for.
                _adapter!.AdvanceHostInputBookkeeping(beforeFrame: true);
                driver.CaptureLocalInput(); // capture local pad (paused-safe, via IInputApi) + send
                // Collection counts either side of the frame decision. Two field reads per
                // generation, no allocation — see the gcGate/gcTick remarks in the slow-tick line
                // for why this is the one measurement that can settle a 55.8ms gate.
                int g0Before = GC.CollectionCount(0), g1Before = GC.CollectionCount(1),
                    g2Before = GC.CollectionCount(2);
                long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!driver.CurrentFrameReady())
                {
                    double stallGateMs = ElapsedMs(phaseStart);
                    gateMs += stallGateMs;
                    NoteGate(stallGateMs, g0Before, g1Before, g2Before,
                        ref gcGate0, ref gcGate1, ref gcGate2);
                    _pacing.AddGate(stallGateMs);
                    stalledThisTick = true;
                    driver.ResendLocalInputIfDue();
                    var stallReason = driver.Strategy.LastStallReason;
                    bool yielded = stallReason.IsDeliberateYield();
                    timeSyncThisTick = yielded;
                    if (yielded)
                    {
                        // Advantage debt is denominated in emulated frames, not timer callbacks.
                        // The cost-cap yield is paid the same way for the same reason — the frame
                        // is being given up on purpose, so there is nothing to retry for.
                        _schedule.SkipFrame();
                    }
                    if (Verbose && nowMs - _lastStallLogMs >= 1000)
                    {
                        _lastStallLogMs = nowMs;
                        Log($"frame {driver.CurrentFrame}: {stallReason.Describe()}");
                    }
                    break;
                }
                else
                {
                    double readyGateMs = ElapsedMs(phaseStart); // includes rollback repair
                    // Rollback hashes its checksum anchor inside the frame decision, where the core
                    // is already standing on the state. That time is real but it isn't repair, so
                    // move it into the hash column and keep the two disjoint. Nothing to drain on
                    // the stall path above: a stall returns before the anchor is ever reached.
                    double anchorHashMs = tickRollback?.TakeHashCostMs() ?? 0;
                    if (anchorHashMs > 0)
                    {
                        readyGateMs = Math.Max(0, readyGateMs - anchorHashMs);
                        _lastHashMs += anchorHashMs;
                    }
                    gateMs += readyGateMs;
                    NoteGate(readyGateMs, g0Before, g1Before, g2Before,
                        ref gcGate0, ref gcGate1, ref gcGate2);
                    _pacing.AddGate(readyGateMs);
                    phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    // When wall-clock debt already makes a second frame due, the first picture is
                    // throwaway. Skip it only when frame two is input-safe and recent core cost says
                    // both frames fit the UI budget. If one frame unexpectedly spikes after that
                    // commitment, finish the visible second frame once; the conservative rolling
                    // estimate prevents that spike from causing repeated two-frame callbacks.
                    // Lockstep can only step a frame every port has already sent, so full
                    // confirmation IS its gate. Rollback's gate is its own caps: predicting one
                    // frame further is the thing it exists to do, and requiring confirmation here
                    // meant the burst essentially never fired in rollback's steady state — on
                    // exactly the heavy-core sessions whose repairs and anchor ticks create the
                    // debt it exists to repay. See RollbackStrategy.CanRunWithoutStalling.
                    bool nextFrameRunnable = driver.Strategy is RollbackStrategy secondRollback
                        ? secondRollback.CanRunWithoutStalling(driver.CurrentFrame + 1)
                        : driver.Strategy is LockstepStrategy && driver.NextFrameFullyConfirmed;
                    bool anotherFrameDue =
                        _schedule.AnotherFrameFits(nowMs, framesThisTick, ElapsedMs(tickStart))
                        && nextFrameRunnable;
                    if (anotherFrameDue) committedSecondFrame = true;
                    _adapter!.AdvanceFrame(driver.CurrentInputs(), renderVideo: !anotherFrameDue);
                    double frameCoreMs = ElapsedMs(phaseStart);
                    coreMs += frameCoreMs;
                    _pacing.AddFrame(frameCoreMs, rendered: !anotherFrameDue);
                    driver.CompleteFrame();
                    // ...and the post-frame half: the two counters upstream advances after the step
                    // (MainForm.cs:3038-3043), autofire phase gated on the lag frame exactly as it
                    // gates it. EmuHawk ticks all four inside the block our BlockFrameAdvance
                    // suppresses, so during a session nothing advances them — sticky autofire
                    // freezes on one pattern value, a Virtual Pad click never clears, and a
                    // one-frame override never expires. Upstream of what we capture, so this
                    // restores features without changing what the wire carries.
                    _adapter!.AdvanceHostInputBookkeeping();
                    steppedThisTick = true;
                    framesThisTick++;
                    if (framesThisTick >= 2) committedSecondFrame = false;
                    _schedule.FrameCompleted(frameCoreMs);
                    // Deferred to the head of the NEXT tick rather than run here. On the fine
                    // clock the present is EmuHawk's Render(), two statements after this callback
                    // returns — so everything after the last frame step delays the picture by its
                    // own cost, and a full-memory hash is ~2ms where a raw block can be reached,
                    // 7ms on a domain that has to be sampled by word, and up to 38ms on
                    // the SHA fallback. That was one guaranteed late present every five seconds,
                    // logged as `hash` but paid for by the screen. Both peers quantise the checksum
                    // to the same interval boundary, so a one-tick deferral changes nothing about
                    // what is compared — with one exception. Lockstep hashes the LIVE state, so if
                    // a catch-up burst is about to step another frame this same tick, deferring
                    // past a boundary loses it: the next tick's head finds CurrentFrame one past
                    // the interval and this machine never reports it, leaving the ledger entry
                    // pending forever. Hash now in that one case, while the state still stands on
                    // the boundary; the burst tick was already paying double core cost.
                    if (anotherFrameDue && driver.Strategy is LockstepStrategy
                        && driver.CurrentFrame % _checksumInterval == 0)
                        MaybeSendChecksum();
                    else
                        _checksumDue = true;
                    _fpsCount++;
                }
            }
            // A commitment the loop can break, named so nobody has to rediscover it: the first
            // frame skips its render when a second is due, and the second may then never run —
            // MayRunFrame can refuse, or the gate can stall. The frame is emulated with no picture,
            // so nothing can be re-presented, and the cost is one tick showing the previous image.
            // Accepted: it needs AnotherFrameFits to say yes and MayRunFrame to say no within one
            // tick, and the alternative — only skipping the render once the second frame is
            // guaranteed — would mean never skipping at all, since the gate's answer isn't known
            // until the frame is stepped.
            if (committedSecondFrame && Verbose) Log("catch-up burst broke off after an unrendered frame");

            // Exactly one tick counted per callback, so the stall rate stays a share of ticks.
            // Ticks that returned early above (frozen for a rejoin, mid-resync) are deliberately
            // not counted: they aren't the frame loop, and folding them in would dilute the rate.
            _pacing.AddTick(stalledThisTick, timeSyncThisTick);
            if (_lastTickClockMs >= 0) _pacing.AddTickInterval(nowMs - _lastTickClockMs);
            _lastTickClockMs = nowMs;

            // Put the latest frame on screen — but only on the clock that actually needs us to.
            //
            // The reason once recorded here was that a paused EmuHawk "never presents". That is
            // false: MainForm's loop calls Render() every iteration regardless of pause. The frozen
            // picture this was added for was real, though — the clock simply moved underneath it.
            // It predates the fine clock, when frames advanced from a WM_TIMER message: that runs
            // during message dispatch, nowhere near Render, so a frame could land after Render had
            // already drawn and sit unshown.
            //
            // On the fine clock that cannot happen. MainForm.cs:1008-1011 runs
            // GeneralUpdateActiveExtTools() -> StepRunLoop_Core() -> Render(), so the picture goes
            // up two statements after the callback we are standing in — and PresentVideo() invokes
            // that very same MainForm.Render(). Calling it here draws the identical frame twice,
            // for ~0.3ms per tick, on the one thread a repair is also bidding for. Skipped
            // mid-session on N64 (2026-07-30) the picture kept moving, which is the same conclusion
            // arrived at from the other direction.
            //
            // The fallback clock keeps presenting for itself: a WM_TIMER tick gets nowhere near
            // Render, which is the case this existed for in the first place.
            //
            // A stepped tick still COUNTS as a picture either way, because on the fine clock it is
            // one — Render draws it immediately after. So `present`, `undrawn` and judder keep
            // meaning what they meant; only `present mean`, which times our own call, drops to zero,
            // and that zero is the saving. The standing caveat is unchanged: these count frames we
            // handed to the display, not refreshes the player's eye received.
            if (steppedThisTick)
            {
                bool presented = true;
                if (fromFineClock)
                {
                    renderMs = 0;
                }
                else
                {
                    long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    presented = _adapter!.PresentVideo();
                    renderMs = ElapsedMs(phaseStart);
                }

                // Stamp AFTER the picture is on screen, not from the tick's entry timestamp.
                //
                // This measured `nowMs`, read before the frame decision and the core step ran. The
                // whole point of the metric is spacing between pictures, and everything variable
                // about that spacing — the repair, the core, the render — happens between those two
                // instants. It was therefore reporting how regularly the CLOCK fired, which is very
                // regular, and was structurally blind to the case it was built to catch: a tick
                // whose frame decision cost 55.8ms presented 55.8ms late and still recorded a
                // textbook 16.7ms gap. That is a large part of why judder read 0-3% through
                // sessions the player described as hitching.
                double presentedAtMs = _paceClock.Elapsed.TotalMilliseconds;
                if (presented)
                {
                    _pacing.AddPresent(renderMs);
                    if (_lastPresentClockMs >= 0)
                        _pacing.AddPresentInterval(presentedAtMs - _lastPresentClockMs, _frameMs);
                    _lastPresentClockMs = presentedAtMs;
                }
                else if (_adapter!.PresentFailuresInARow == 1)
                {
                    // Once, on the first failure: a persistent one now shows as presented-fps
                    // falling rather than as a healthy number over a frozen window.
                    ConnLog("video present failed — the picture may be frozen while emulation " +
                            $"continues: {_adapter.LastPresentError ?? "no detail"}", Color.Firebrick);
                }
            }

            // Liveness runs every tick, independent of stepping (so a stall doesn't stop our pings
            // and a dead link is still detected while we're waiting on it).
            MaybeSendPing();
            CheckLinkTimeouts();
            CheckUdpInputProgress();
            if (!_phase.IsActive || _driver == null) return;

            // Joiner: the host clears its resync counter once checksums re-agree, but a joiner gets no
            // such signal. Decay ours after running well past the last resync without another one —
            // otherwise a run of successful recoveries would eventually trip the "persistent desync"
            // give-up limit on a perfectly healthy joiner.
            if (!_isHost && !_phase.AwaitingRejoin
                && _resyncBudget.RecordQuiet(MonotonicElapsedSeconds(_lastResyncStamp)))
                Log("back in sync — recovery confirmed");

            // One-shot audio pipeline snapshot ~2s in, so a single test shows where sound breaks.
            if (!_audioStatsLogged && _driver.CurrentFrame >= 120)
            {
                _audioStatsLogged = true;
                Log(_adapter!.AudioStats());
            }
            else if (Verbose && _driver.CurrentFrame % 300 == 0 && _driver.CurrentFrame > 0
                && _driver.CurrentFrame != _lastVerboseAudioFrame)
            {
                _lastVerboseAudioFrame = _driver.CurrentFrame;
                Log(_adapter!.AudioStats());
            }

            double uiStart = ElapsedMs(tickStart);
            UpdateSessionUi(nowMs);
            uiMs = ElapsedMs(tickStart) - uiStart;
        }
        // Already says what happened and that it is terminal; wrapping it in "session error" would
        // bury the one detail that distinguishes a broken core from a broken link.
        catch (StateRestoreFailedException ex) { EndSession(ex.Message); }
        catch (Exception ex) { EndSession("session error: " + ex.Message); }
        finally
        {
            double elapsed = ElapsedMs(tickStart);
            double clockMs = _paceClock.Elapsed.TotalMilliseconds;
            if (_phase.IsActive && elapsed >= Math.Max(12.0, _frameMs * 0.75)
                && clockMs - _lastSlowTickLogMs >= 1000)
            {
                _lastSlowTickLogMs = clockMs;
                string repairStr = "";
                if (tickRollback != null && gateMs >= 1.0)
                {
                    int repairs = tickRollback.RollbackCount - repairsBefore;
                    long resim = tickRollback.FramesResimulated - resimBefore;
                    repairStr = repairs == 0
                        ? ", no repair ran"
                        : $", {repairs} repair(s) (last d{tickRollback.LastRollbackDepth}, " +
                          $"{resim} frame(s) resimulated)";
                }
                // The remainder is what none of the named terms covered. It is reported rather
                // than left implicit because it was the whole story the one time this mattered:
                // 15.3ms of a 16.1ms tick, invisible in a line that itemised only the four
                // cheapest things the tick does.
                double other = elapsed - (coreMs + gateMs + _lastHashMs + renderMs
                    + audioMs + emuApiMs + uiMs);
                // Audio device state, because a stopped one is silent in every other column: the
                // pump early-returns, the ring it drains fills up, and the tick collapses a few
                // seconds later with core, gate and stall all reading perfectly healthy.
                string audioState = "";
                if (_adapter != null)
                {
                    double ring = _adapter.AudioRingFullness;
                    audioState = $", audiodev {(_adapter.AudioDeviceStarted ? "on" : "STOPPED")}" +
                        (ring >= 0 ? $", ring {ring:P0}" : "");
                }
                // Garbage collection, because nothing else on this line can explain a frame decision
                // costing 55.8ms. That span cannot block — it holds no transport reference and
                // contains no sleep, wait, lock or socket call — and the work it does at depth 1 is
                // one state load, one invisible frame and at most two snapshots, which is ~1-2ms on
                // a core whose frames cost 0.6ms. It is also the most allocation-hostile thing the
                // tick does: a whole-core savestate is a fresh ~787KiB array, nine times the Large
                // Object Heap threshold, and net48 neither compacts the LOH nor collects it outside
                // a blocking gen2. A pause is therefore both plausible here and, being another
                // thread's bill, invisible in every column that measures our own work.
                int gcTick0 = GC.CollectionCount(0) - gcTick0Before;
                int gcTick1 = GC.CollectionCount(1) - gcTick1Before;
                int gcTick2 = GC.CollectionCount(2) - gcTick2Before;
                string gcStr = $", gc tick {gcTick0}/{gcTick1}/{gcTick2} gate {gcGate0}/{gcGate1}/{gcGate2}";

                Log($"slow tick {elapsed:F1}ms at frame {frameForTelemetry}: core {coreMs:F1}, " +
                    $"rollback/gate {gateMs:F1}, hash {_lastHashMs:F1}, present {renderMs:F1}, " +
                    $"audio {audioMs:F1}, emuapi {emuApiMs:F1}, ui {uiMs:F1}, other {other:F1}" +
                    $"{audioState}{gcStr}, " +
                    $"UDP drained {packetsDrained}, pacing rebases {_pacingRebases}{repairStr}");
            }
            _frameTickRunning = false;
            // No Start() here on purpose: the timer never stopped, and re-arming it would restore
            // the serialization described above. EndSession/OnConnectFailed stop it explicitly.
        }
    }

    private void UpdateSessionUi(double nowMs)
    {
        if (_driver == null || nowMs - _lastUiRefreshMs < 250) return;
        _lastUiRefreshMs = nowMs;

        double ping = WorstPingMs(out bool udpMeasured);
        double effRttMs = (ping < 0 ? 0 : ping) + 2.0 * _simLatencyMs;
        int advantage = ComputeFrameAdvantage(out bool haveAdvantage, out int revision, out bool freshAdvantage);
        _driver.Strategy.OnPacingReport(new PacingInfo(effRttMs, advantage,
            haveAdvantage && freshAdvantage, revision));

        string pingStr = ping < 0 ? ""
            : $" — ping {effRttMs:F0}ms{(udpMeasured ? " udp" : "")}" +
              $"{(_simLatencyMs > 0 ? $" (incl. {2 * _simLatencyMs}ms sim)" : "")}{(_peers.Count > 1 ? " (worst)" : "")}";
        string rbStr = _driver.Strategy is RollbackStrategy rbs
            // Walk-back only appears once it has happened: it is the price sparse keyframes pays,
            // and the first thing to look at if a repair costs more than its depth accounts for.
            // d and wb are disjoint now: d is how far the correction reached, wb the extra frames
            // replayed to get to a keyframe. Their sum is what the repair actually re-simulated.
            ? $" — rollback ×{rbs.RollbackCount} (last d{rbs.LastRollbackDepth}" +
              $"{(rbs.LastRollbackWalkback > 0 ? $"+{rbs.LastRollbackWalkback}wb" : "")}" +
            // The three stall counts are shown separately, and only once each has happened, because
            // they point at three different culprits: tsync at the clock, cost at this machine, and
            // wait at the link. Reporting only tsync meant a session stalling on packet loss showed
            // a zero and nothing else.
              $", max d{rbs.MaxRollbackDepthSeen}" +
              $"{(rbs.TimeSyncStalls > 0 ? $", tsync {rbs.TimeSyncStalls}" : "")}" +
              $"{(rbs.CostStalls > 0 ? $", cost {rbs.CostStalls}" : "")}" +
              $"{(rbs.PredictionLimitStalls > 0 ? $", wait {rbs.PredictionLimitStalls}" : "")})"
            : "";

        if (_fpsClock.ElapsedMilliseconds >= 500)
        {
            _actualFps = _fpsCount * 1000.0 / _fpsClock.ElapsedMilliseconds;
            _fpsCount = 0;
            // Summarize before resetting: this is the only place the pacing window rolls over,
            // so the log line below reads the same numbers the status bar just showed.
            _lastPacing = _pacing.Summarize(_fpsClock.Elapsed.TotalMilliseconds);
            _pacing.Reset();
            _fpsClock.Restart();
        }
        double targetFps = _frameMs > 0 ? 1000.0 / _frameMs : 60.0;
        bool cpuBound = _actualFps >= 0 && _actualFps < targetFps * 0.95;
        // Only worth the width when presentation actually fell behind the core — otherwise the two
        // numbers are the same and repeating it just crowds the bar.
        string presentStr = _lastPacing.PresentedFps < _lastPacing.AdvancedFps - 1
            ? $", present {_lastPacing.PresentedFps:F0}"
            : "";
        string speedStr = _actualFps < 0 ? ""
            : $" — {_actualFps:F0}/{targetFps:F0} fps ({_actualFps / targetFps * 100:F0}%{(cpuBound ? ", CPU-bound" : "")}{presentStr})";
        // The number that separates a slow core from a stalling link: high here means waiting on
        // the network (raise input delay), low here with fps under target means CPU or pacing.
        double stallPct = _lastPacing.StallTickPct;
        string stallStr = stallPct >= 5 ? $" — stall {stallPct:F0}%" : "";
        string udpStr = _udpWarningActive ? " — UDP recovering" : "";
        Status($"in session — frame {_driver.CurrentFrame}{speedStr}{pingStr}{rbStr}{stallStr}{udpStr}",
            _udpWarningActive || cpuBound || stallPct >= 25 ? Color.DarkOrange : Color.Green);
        MaybeHintStalling(nowMs);
        MaybeHintPresentation(nowMs);
        LogPacingSummary(nowMs);
        RefreshPlayersList();
    }

    /// <summary>
    /// Say something once when lockstep is actually stalling, regardless of what the ping says.
    /// <see cref="MaybeHintDelay"/> reasons from the worst measured round-trip, but what stalls a
    /// lockstep session is the <em>late</em> packet, not the typical one — a link with a fine
    /// median and a wide swing looks healthy by ping and still waits on remote input constantly.
    /// The measured stall rate catches that case directly.
    ///
    /// It deliberately does NOT claim the delay is the cause. In lockstep, stalling is also how a
    /// fast peer waits for a slow one, so a CPU-bound machine at the other end produces exactly
    /// the same reading — and raising delay would do nothing for it. The message names both.
    /// </summary>
    /// <summary>
    /// Say once when this machine cannot repair even the minimum depth inside its frame budget.
    ///
    /// The cost cap has a floor of two frames, so below it rollback does not shrink to nothing —
    /// it carries on committing to work already measured as unaffordable, and the result reaches
    /// the player as judder with no stated cause. Rollback is the wrong choice at that point and
    /// the two things that are the right choice both belong to the host, so name them.
    /// </summary>
    private void MaybeHintRollbackUnaffordable(RollbackStrategy rb)
    {
        if (!rb.BudgetExceededByFloor) return;
        if (!_rollbackCostHint.ShouldFire(true, MonotonicNow())) return;

        ConnLog($"this machine needs {rb.FloorRepairPerFrameMs:F1}ms to re-simulate a single frame, " +
            $"which does not fit the repair budget — rollback is running below the depth it can " +
            $"afford, and the judder that causes is not the network. Lockstep at a higher input " +
            $"delay is the honest trade for this core on this CPU. " +
            $"{ApplyDelayAdvice(_sessionDelay + 2)}", Color.DarkOrange);
    }

    /// <summary>
    /// Say once when snapshot elision — rollback's largest saving on a heavy core — is not firing.
    ///
    /// A frame is only elided when every port's input for it is already confirmed, so elision
    /// fires exactly when input delay covers the link's latency. Below that, remote ports are
    /// predicted continuously and a snapshot is taken every keyframe interval forever: on N64
    /// that is a ~6ms whole-core savestate roughly thirty times a second, which is the dominant
    /// steady-state cost in the whole program and reaches the player as judder with no stated
    /// cause. The session measures both halves already — the elided share, and what the probe
    /// timed a save at — but nothing connected them to the one setting that governs it.
    ///
    /// Stated as a trade, not a recommendation: a frame of delay is a real cost too, and which
    /// one a player prefers is theirs to choose. This only makes the choice visible.
    /// </summary>
    private void MaybeHintSaveRate(double nowMs, long savesTaken, long savesElided)
    {
        if (_mode != SyncMode.Rollback || _probeSaveMs < SaveRateHintCostMs) return;
        long considered = savesTaken + savesElided;
        if (considered < 30) return; // not a window worth judging
        double elidedShare = (double)savesElided / considered;
        if (!_saveRateHint.ShouldFire(elidedShare < SaveRateHintElidedShare, nowMs)) return;

        ConnLog($"taking {savesTaken} savestates a second at {_probeSaveMs:F1}ms each " +
            $"(~{savesTaken * _probeSaveMs:F0}ms of every second), because input delay " +
            $"{_sessionDelay} is below this link's latency: rollback can only skip a snapshot for a " +
            "frame whose input has already arrived, and at this delay almost none have. Raising the " +
            "delay until they do is what makes that cost disappear — it is the single biggest one " +
            $"this core pays. {DelayRemedy(_sessionDelay + 1)}", Color.DarkOrange);
    }

    private void MaybeHintStalling(double nowMs)
    {
        if (_mode != SyncMode.Lockstep || _lastPacing.Ticks == 0) return;
        if (!_stallHint.ShouldFire(_lastPacing.StallTickPct > StallHintPct, nowMs)) return;

        ConnLog($"stalling {_lastPacing.StallTickPct:F0}% of the time waiting on remote input. " +
            $"Either input delay ({_sessionDelay}) isn't covering the link's worst moments — a ping " +
            "that looks fine on average still stalls if it swings — or the other machine can't hold " +
            "full speed and you're waiting for it. Check whether their fps reads CPU-bound: if it " +
            $"does, only faster core settings help. If it doesn't: {DelayRemedy(_sessionDelay + 1)}",
            Color.DarkOrange);
    }


    /// <summary>
    /// Say once when the core is keeping up but the picture isn't.
    ///
    /// A frame is presented once per timer callback, so when a callback emulates more than one frame
    /// only the last of them is ever shown. On a light core that costs nothing — the whole burst
    /// fits inside a single display refresh, so the skipped pictures were never going to be seen.
    /// On a heavy core the frames of a burst are ten-plus milliseconds apart and every one of them
    /// was a real, showable picture.
    ///
    /// Nothing else in the session reports this, and it isn't an oversight: fps, the CPU-bound
    /// reading and the stall rate are all computed from frames advanced, which hold at the console's
    /// rate exactly because the pacing code is succeeding. So the session reads 60/60 fps at 100%,
    /// in sync, no stalls — while the window updates half that often and the game feels like it is
    /// dropping inputs. Naming it is the whole point; there is no lever here but a cheaper frame.
    /// </summary>
    private void MaybeHintPresentation(double nowMs)
    {
        if (_lastPacing.Ticks == 0 || _lastPacing.AdvancedFps <= 0) return;
        if (!_presentHint.ShouldFire(_lastPacing.PresentedShare < PresentShareHintFloor, nowMs)) return;

        var p = _lastPacing;
        ConnLog($"emulating {p.AdvancedFps:F0} fps but only drawing the picture {p.PresentedFps:F0} " +
            $"times a second. A frame is drawn once per timer callback, and callbacks are landing " +
            $"{p.TickGapMeanMs:F0}ms apart against a {_frameMs:F0}ms frame period, so each one has to " +
            "emulate more than one frame and only the last of them is shown. The session is at full " +
            "speed and in sync — it is the display that is coarse, which feels like dropped inputs. " +
            $"Core frames cost {p.CoreMeanMs:F1}ms each here; nothing but making them cheaper helps, " +
            "so lower the render resolution or pick a lighter video plugin.",
            Color.DarkOrange);
    }

    /// <summary>
    /// The full pacing breakdown, once a second under Verbose. The status bar has room for two
    /// numbers; this has the rest — notably <c>rebases</c>, which counts how many times the pacing
    /// clock gave up on accumulated debt and discarded frames outright. That is the difference
    /// between a core that genuinely can't make budget (core mean at or above the frame period)
    /// and a schedule that threw away frames the core could have run.
    /// </summary>
    /// <summary>
    /// Rollback activity over the last pacing window, as a rate rather than a running total.
    ///
    /// Missing from this line until a player reported the game "slowing down when a lot is going
    /// on" while `adv` sat at a solid 60fps the whole time. Under rollback those are not in
    /// conflict: a contradicted prediction re-simulates frames the player has already been shown,
    /// so the advance rate stays exactly 60 while what is on screen gets rewritten. The rewrites
    /// are what a player sees, and nothing here counted them — `gate` reports what a repair COST,
    /// which on a core with a 0.4ms savestate stays near zero however many are happening.
    /// </summary>
    // Windowed against the strategy's lifetime counters. CounterWindow, not a plain baseline field,
    // because the strategy is REPLACED by a resync, a reconnect or a mid-session netcode change —
    // see its remarks for the negative figures that produced.
    private readonly CounterWindow _pacingRollbacks = new();
    private readonly CounterWindow _pacingResim = new();
    private readonly CounterWindow _pacingSavesTaken = new();
    private readonly CounterWindow _pacingSavesElided = new();

    /// <summary>
    /// Roll the once-a-second telemetry windows, whether or not anything is going to print them.
    ///
    /// These used to live inside LogPacingSummary, after its "not Verbose" return — so with the
    /// checkbox off they never reset and accumulated for the whole session. Tick ten minutes with
    /// Verbose off, turn it on, and the first line reported ten minutes of totals in a format whose
    /// every other figure is per-second: emuloop in the tens of thousands, `gate worst` the worst
    /// of the session, rollbacks a session count presented as one second's worth. The CounterWindow
    /// case was worse still — its went-backwards heuristic for a replaced strategy only works if it
    /// is Observed often enough to still be below a fresh counter, which with Verbose off it was
    /// not. Rolling unconditionally costs four subtractions a second.
    /// </summary>
    private void RollTelemetryWindows(out string clockStr, out string rbStr, out string gateStr)
    {
        clockStr = $"clock emuloop {_emuLoopTicksWindow}/{_emuLoopCallsWindow} " +
                   $"timer {_timerTicksWindow}, ";
        // The floor this session's judder cannot go below, printed beside it. A present happens at
        // most once per tick that ran a frame, so when the run loop's period and the frame period
        // disagree the difference HAS to show up as a gap — chasing judder past this is chasing
        // BizHawk's paused-loop sleep, which nothing here can reach. Ticks that ran no frame are
        // exactly (calls - ticks).
        //
        // "Nothing here can reach" is a conclusion, not a shrug — checked against the 2.11.1
        // source, so nobody has to check it again. The sleep is a hard-coded Thread.Sleep(15) in
        // Throttle.Step, gated on signal_paused && !signal_continuousFrameAdvancing. Both gates are
        // recomputed from MainForm state on EVERY loop iteration — and _runloopFrameProgress is
        // force-cleared in StepRunLoop_Core, which runs BEFORE the throttle reads it — so a
        // reflected write is erased before it can take effect. Every flag that would persistently
        // open the gate (PressFrameAdvance, HoldFrameAdvance, unpausing) also makes StepRunLoop_Core
        // step the core itself, which would double-step every frame this session emulates: a
        // guaranteed desync, traded for a tick rate that even then lands in SpeedThrottle at about
        // the core's own fps. The only lever on this side of the seam is emulating more than one
        // frame per tick, which is what the catch-up burst does.
        double judderFloorPct = _emuLoopTicksWindow > 0
            ? 100.0 * Math.Max(0, _emuLoopCallsWindow - _emuLoopTicksWindow) / _emuLoopTicksWindow
            : 0;
        clockStr += $"judder floor {judderFloorPct:F0}%, ";
        _timerTicksWindow = 0;
        _emuLoopCallsWindow = 0;
        _emuLoopTicksWindow = 0;

        rbStr = "";
        if (_driver?.Strategy is RollbackStrategy rb)
        {
            long rollbacks = _pacingRollbacks.Observe(rb.RollbackCount);
            long resim = _pacingResim.Observe(rb.FramesResimulated);
            // Snapshots actually taken versus elided. The elision rule turns rollback's steady
            // state from a savestate every frame into nearly none, so the allocation rate this
            // path is responsible for is a measured number rather than something to reason about
            // from the tuning constants.
            long taken = _pacingSavesTaken.Observe(rb.SavesTaken);
            long elided = _pacingSavesElided.Observe(rb.SavesElided);
            // These are the windowed figures the save-rate advisory reasons from, so it is asked
            // here rather than duplicating the windowing beside it.
            MaybeHintSaveRate(MonotonicNow(), taken, elided);
            // buffers=N is the acceptance test for the state pool: it should climb to the ring's
            // size in the first second or two and then stop. Still climbing means the pool is
            // being outrun and savestates are still being allocated per frame.
            rbStr = $"rollbacks {rollbacks} ({resim} frame(s) resimulated, last d{rb.LastRollbackDepth}, " +
                    $"max d{rb.MaxRollbackDepthSeen}), saves {taken} taken/{elided} elided" +
                    $"{(_adapter != null ? $" (pool {_adapter.StatePoolSize}, buffers {_adapter.StateBuffersAllocated})" : "")}, ";
        }

        // Worst frame decision since this line last printed, not since the window opened — see
        // _worstGateSinceLogMs for why the windowed figures could not see these at all.
        gateStr = $"gate worst {_worstGateSinceLogMs:F1}ms ({_gateSpikesSinceLog} over {GateSpikeMs:F0}ms), " +
                  $"gc {_gcGateWindow0}/{_gcGateWindow1}/{_gcGateWindow2} in gate, ";
        _worstGateSinceLogMs = 0;
        _gateSpikesSinceLog = 0;
        _gcGateWindow0 = _gcGateWindow1 = _gcGateWindow2 = 0;
    }

    private void LogPacingSummary(double nowMs)
    {
        if (nowMs - _lastPacingLogMs < 1000) return;
        _lastPacingLogMs = nowMs;
        // Rolled and advised on EVERY cadence, printed only under Verbose. The advisory below is
        // user-facing — it tells someone their machine cannot afford the netcode it is running —
        // and it used to sit past the Verbose return, so the only people who could see it were the
        // ones who had already ticked a debug checkbox.
        if (_driver?.Strategy is RollbackStrategy costly) MaybeHintRollbackUnaffordable(costly);
        RollTelemetryWindows(out string clockStr, out string rbStr, out string gateStr);
        if (!Verbose) return;
        var p = _lastPacing;
        if (p.Ticks == 0) return;

        Log($"pacing: adv {p.AdvancedFps:F0} fps, present {p.PresentedFps:F0}, " +
            $"tick {p.TicksPerSecond:F0}/s (gap min {p.TickGapMinMs:F1} mean {p.TickGapMeanMs:F1} " +
            $"max {p.TickGapMaxMs:F1}ms), " +
            $"core mean {p.CoreMeanMs:F1} p95 {p.CoreP95Ms:F1} max {p.CoreMaxMs:F1}ms, " +
            $"gate mean {p.GateMeanMs:F1} p95 {p.GateP95Ms:F1}ms, " +
            $"present mean {p.PresentMeanMs:F1}ms, undrawn {p.UndrawnRenders}, " +
            $"judder {p.JudderPct:F0}% (gap {p.PresentGapMeanMs:F1}ms ±{p.PresentJitterMs:F1} " +
            $"max {p.PresentGapMaxMs:F1} vs {_frameMs:F1} target), " +
            $"{gateStr}{clockStr}{rbStr}" +
            $"stall {p.StallTickPct:F0}% of {p.Ticks} ticks (tsync {p.TimeSyncTickPct:F0}%), " +
            $"rebases {p.Rebases}, budget {TickBudgetMs():F0}ms");
    }

}
