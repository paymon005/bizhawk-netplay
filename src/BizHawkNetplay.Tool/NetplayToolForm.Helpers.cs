using System;
using System.Drawing;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using BizHawk.Client.Common;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Diag;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    // --- State used only by this file (everything shared stays in NetplayToolForm.cs) ---
    private Config? _config;
    private bool _hostOwnershipHeld;

    /// <summary>Why ownership could not be taken, or null. Set by a failed acquisition, which has
    /// already undone its own partial work; the caller refuses the session and reports this.</summary>
    private string? OwnershipRefusal { get; set; }
    private int _logLines;      // lines currently in _log, tracked so trimming needn't split its text
    private bool _prevAcceptBackgroundInput;
    private bool _prevAcceptBackgroundInputControllerOnly;
    private bool _prevRunInBackground;
    private bool _prevBlockFrameAdvance;
    private bool _prevPaused;        // was EmuHawk already paused when we first took the clock?
    private bool _pausedByUs;        // ...and have we actually taken it, so there is something to undo
    private bool _prevRewindEnabled; // the user's rewind preference, to restore on teardown
    // Experimental unpaused clock (see EngageUnpausedClock). Captured at session start; the
    // checkbox is only read there, so toggling mid-session does nothing until the next session.
    private bool _clockUnpaused;
    private bool _prevClockThrottle;
    private bool _prevUnthrottled;
    private int _prevSpeedPercent;
    private Label _status = null!;


    /// <summary>
    /// Take, and later hand back, the host state a live session owns. One lifecycle, because these
    /// have to be undone together or not at all — a session that ends leaving any of them applied
    /// has changed the emulator out from under the user.
    ///
    /// <list type="bullet">
    /// <item>Run-in-background, so two instances on one screen don't pause each other (only one can
    /// be focused). Controller-only: the unfocused window still reads its gamepad, but background
    /// KEYBOARD is ignored, so typing elsewhere can't fire a rewind/load-state hotkey.</item>
    /// <item><c>BlockFrameAdvance</c>, so EmuHawk's own run loop cannot step the core. This is the
    /// real guard, not the sticky pause: <c>GeneralUpdateActiveExtTools</c> is followed immediately
    /// by <c>StepRunLoop_Core</c>, so anything that unpauses between the two gets a frame in through
    /// EmuHawk's ordinary controller chain — a frame we neither fed nor sent. It gates only
    /// <c>StepRunLoop_Core</c>; our own <c>IEmulator.FrameAdvance</c> calls are untouched.</item>
    /// <item>Rewind, suspended for the session because rewinding rewrites the frame counter the
    /// whole timeline is indexed by. It was suspended and then never resumed — for the rest of the
    /// EmuHawk run, not just the session.</item>
    /// </list>
    ///
    /// Pause is snapshotted rather than assumed. Teardown used to unpause unconditionally, which
    /// started the emulator for someone who had deliberately paused it before connecting.
    /// </summary>
    private void ApplySessionHostOwnership(bool enable)
    {
        try
        {
            if (enable)
            {
                // Idempotent: acquisition happens at the FIRST pause and a session pauses several
                // times on the way in. Re-running would snapshot the values we ourselves just wrote,
                // and teardown would then "restore" the user's settings to ours.
                if (_hostOwnershipHeld) return;

                // Ownership is claimed only once the two guards that DEFINE it are proven, and the
                // flag is set last. It used to be set first, with each guard logging and carrying
                // on if it failed — so a session could run believing it owned a timeline whose
                // frame advance was never actually blocked (EmuHawk's own loop free to step an
                // unsynchronized frame through its ordinary controller chain) or whose state-load
                // hooks were never installed (a Quick Load neither refused nor observed). Both are
                // silent desyncs wearing the costume of a working session. Failing closed here
                // costs a refused session; failing open cost a session that looked fine.
                bool blockedFrameAdvance = false;
                try
                {
                    _prevBlockFrameAdvance = MainForm.BlockFrameAdvance;
                    MainForm.BlockFrameAdvance = true;
                    blockedFrameAdvance = MainForm.BlockFrameAdvance;   // read back, never assumed
                }
                catch (Exception ex) { Log("(note) could not block EmuHawk's frame advance: " + ex.Message); }
                if (!blockedFrameAdvance)
                {
                    try { MainForm.BlockFrameAdvance = _prevBlockFrameAdvance; } catch { }
                    OwnershipRefusal = "EmuHawk's own frame advance could not be blocked, so its run " +
                        "loop could step a frame this session never sent to anyone — a silent desync. " +
                        "Netplay refuses rather than run without that guard.";
                    return;
                }

                if (!SubscribeHostCommandEvents()) // Quick Load refused, any other load ends the session
                {
                    try { MainForm.BlockFrameAdvance = _prevBlockFrameAdvance; } catch { }
                    OwnershipRefusal = "the savestate hooks could not be installed, so a state load " +
                        "would be neither refused nor noticed — every peer would carry on from a " +
                        "state this machine no longer has. Netplay refuses rather than run blind to it.";
                    return;
                }
                _hostOwnershipHeld = true;

                // FormBase.Config is the supported route (ToolManager sets it before the form is
                // shown); the forbidden reference stays as a fallback that should never be needed.
                _config = Config ?? (APIs.Emulation as EmulationApi)?.ForbiddenConfigReference;
                // Rewind rewrites the frame counter the whole timeline is indexed by, and a lobby
                // baseline is every bit as rewindable as a running session. Snapshot what teardown
                // should restore — the RUNTIME state, not the config flag: EnableRewind suspends
                // and resumes the live rewinder and never touches Config.Rewind.Enabled, so a user
                // who had rewind config-enabled but suspended it by hand before connecting used to
                // get it resumed on disconnect. The config flag stays as the fallback when the
                // concrete MainForm isn't reachable.
                try
                {
                    _prevRewindEnabled = MainForm is BizHawk.Client.EmuHawk.MainForm mf
                        ? mf.Rewinder is { Active: true }
                        : _config?.Rewind.Enabled ?? true;
                }
                catch { _prevRewindEnabled = _config?.Rewind.Enabled ?? true; }
                try { APIs.EmuClient.EnableRewind(false); } catch { }

                if (_config == null) { Log("(note) couldn't reach config to disable pause-on-unfocus"); return; }
                _prevRunInBackground = _config.RunInBackground;
                _prevAcceptBackgroundInput = _config.AcceptBackgroundInput;
                _prevAcceptBackgroundInputControllerOnly = _config.AcceptBackgroundInputControllerOnly;
                _config.RunInBackground = true;
                _config.AcceptBackgroundInput = true;
                _config.AcceptBackgroundInputControllerOnly = true;
                Log("EmuHawk's own frame advance blocked and rewind suspended; run-in-background " +
                    "enabled (controller-only)");
            }
            else if (_hostOwnershipHeld)
            {
                // Clearing the flag FIRST is load-bearing, not tidiness. EnableRewind calls
                // CreateRewinder when the rewinder is null, and CreateRewinder opens with
                // "if (ToolControllingRewind is not null) return;" — so restoring rewind while we
                // still claim it silently fails to recreate anything, and rewind stays dead for the
                // rest of the EmuHawk run. Separate try blocks for the same reason: one failed
                // restoration must not skip the others.
                _hostOwnershipHeld = false;
                UnsubscribeHostCommandEvents();
                try { MainForm.BlockFrameAdvance = _prevBlockFrameAdvance; } catch { }
                try { APIs.EmuClient.EnableRewind(_prevRewindEnabled); } catch { }
                if (_config != null)
                {
                    _config.RunInBackground = _prevRunInBackground;
                    _config.AcceptBackgroundInput = _prevAcceptBackgroundInput;
                    _config.AcceptBackgroundInputControllerOnly = _prevAcceptBackgroundInputControllerOnly;
                }
            }
        }
        catch (Exception ex) { Log("(note) host-ownership adjust failed: " + ex.Message); }
    }

    /// <summary>
    /// Snap EmuHawk back to paused if anything unpaused it. <see cref="ApplySessionHostOwnership"/>'s
    /// BlockFrameAdvance is what actually prevents a stolen frame; this keeps the emulator's own
    /// state consistent with the fact that we own the clock, and gives the user a reason in the log.
    /// Called from both clocks — the fine one and the WinForms fallback, which does not pass through
    /// <c>UpdateValues</c> at all.
    /// </summary>
    private void ReassertPause()
    {
        // The experimental clock inverts the assertion: the session RUNS unpaused, with
        // BlockFrameAdvance alone keeping EmuHawk's loop off the core, because the paused branch of
        // Throttle.Step is an unconditional Thread.Sleep(15) — the ~66Hz ceiling on the frame
        // clock — while the unpaused branch is a phase-locked wait on the core's own vsync rate.
        // Only once the session is live; the lobby and state transfer stay paused either way, since
        // nothing is stepping frames there and paused is the honest state to show the user.
        if (_clockUnpaused && _phase.IsActive)
        {
            ApplyUnpausedClockThrottle();   // the speed hotkeys would otherwise retune our clock
            if (!APIs.EmuClient.IsPaused()) return;
            APIs.EmuClient.Unpause();
            if (Verbose) Log("re-unpaused (experimental clock — the session owns frame stepping)");
            return;
        }
        if (APIs.EmuClient.IsPaused()) return;
        APIs.EmuClient.Pause();
        if (Verbose) Log("re-paused (the session owns the frame clock — don't unpause)");
    }

    /// <summary>
    /// Enter the experimental unpaused-clock mode for this session, if the box is ticked.
    ///
    /// Two things have to hold for this to be safe, both verified against 2.11.1's source:
    /// BlockFrameAdvance gates the whole of MainForm's core step (input latch aside, nothing in the
    /// skipped block runs — no CaptureRewind, no MovieSession handling), and Sound.UpdateSound's
    /// attenuation is only ever assigned INSIDE that block, so it stays 0 and the audio takeover
    /// holds exactly as it does paused. ClockThrottle is forced on for the session because
    /// SpeedThrottle — the phase-locked wait this exists to reach — only runs under it; with a
    /// vsync or sound throttle configured the unpaused loop would instead spin flat out.
    /// </summary>
    private void EngageUnpausedClock()
    {
        if (_clockUnpaused) return;                       // idempotent: never snapshot our own values
        if (!_unpausedClockCheck.Checked) return;
        // Without the config we cannot force the throttle, and unpausing anyway is the one outcome
        // worse than not trying: with a vsync or sound throttle configured, Throttle.Step's
        // "paused || ClockThrottle || overrideSecondary" test is all false, SpeedThrottle never
        // runs, and the loop spins a core flat out for the whole session. Refuse instead.
        if (_config == null)
        {
            Log("unpaused clock unavailable — EmuHawk's config is not reachable, and without it the " +
                "run loop cannot be kept throttled. Staying paused.");
            return;
        }
        _clockUnpaused = true;
        _prevClockThrottle = _config.ClockThrottle;
        _prevUnthrottled = _config.Unthrottled;
        _prevSpeedPercent = _config.SpeedPercent;
        ApplyUnpausedClockThrottle();
        try { APIs.EmuClient.Unpause(); } catch { }
        Log("EXPERIMENTAL unpaused clock: EmuHawk's loop is running throttled at the core's own " +
            "rate; BlockFrameAdvance alone prevents stolen frames (the drift check names any that " +
            "slip through). Speed and Unthrottle are held at 100% for the session, since under this " +
            "mode they would scale the netplay frame clock itself. Compare judder/gap in the pacing " +
            "line against a normal session.");
    }

    /// <summary>
    /// Hold the three throttle settings the frame clock now depends on.
    ///
    /// Paused, none of this mattered: Throttle.Step took the sleep-15 branch and every speed control
    /// was inert. Unpaused, the run loop IS the frame clock, so Unthrottle/Turbo makes it spin flat
    /// out and Speed% scales it directly — at 50% the clock delivers half the wakes the frame rate
    /// needs, which is exactly the judder this mode exists to remove. Re-applied from ReassertPause
    /// rather than only at engage, because these are ordinary hotkeys the user can hit mid-session.
    /// </summary>
    private void ApplyUnpausedClockThrottle()
    {
        if (_config == null) return;
        _config.ClockThrottle = true;
        _config.Unthrottled = false;
        if (_config.SpeedPercent != 100) _config.SpeedPercent = 100;
    }

    /// <summary>Leave the experimental clock mode. Runs inside RestorePauseState so every teardown
    /// path — EndSession, FailSession — passes through it exactly once.</summary>
    private void DisengageUnpausedClock()
    {
        if (!_clockUnpaused) return;
        _clockUnpaused = false;
        if (_config == null) return;
        _config.ClockThrottle = _prevClockThrottle;
        _config.Unthrottled = _prevUnthrottled;
        _config.SpeedPercent = _prevSpeedPercent;
    }

    /// <summary>
    /// Pause for the session, remembering whether the user already had it paused so teardown can put
    /// it back exactly as it was.
    ///
    /// The snapshot has to happen at the FIRST pause, which is entering the lobby — not when the
    /// driver takes the clock. Reading it later always reports "paused", because by then we are the
    /// reason it is: that would make every session end leave the emulator frozen. Idempotent for the
    /// same reason, since a session pauses more than once on the way in.
    /// </summary>
    /// <returns>False when ownership could not be taken — see <see cref="OwnershipRefusal"/>. The
    /// caller must abandon the session; nothing has been paused and nothing acquired.</returns>
    private bool PauseForSession()
    {
        if (!_pausedByUs)
        {
            try { _prevPaused = MainForm.EmulatorPaused; } catch { _prevPaused = false; }
            _pausedByUs = true;
            // Ownership starts HERE, not at GO. Between this pause and the session starting, the
            // host probes, exports the authoritative state and then waits — possibly for minutes —
            // and a joiner imports that state and waits for GO. Anything that moves the core in that
            // window (the Frame Advance hotkey, a window activation that unpauses) silently changes
            // the baseline the peers agreed on, and the post-GO drift check cannot see it because
            // _startEmuFrame is only sampled once the session begins. The host never re-imports what
            // it exported, so on its side that drift is a desync at frame 0.
            OwnershipRefusal = null;
            ApplySessionHostOwnership(true);
            if (OwnershipRefusal != null)
            {
                // Acquisition already undid its own partial work; give back the pause flag too, so
                // the refused attempt leaves the emulator exactly as it found it.
                _pausedByUs = false;
                return false;
            }
        }
        APIs.EmuClient.Pause();
        return true;
    }

    /// <summary>Undo <see cref="PauseForSession"/>, leaving a deliberately-paused emulator paused.</summary>
    private void RestorePauseState()
    {
        // Under the experimental clock the emulator is RUNNING at teardown, so the restore points
        // the other way: a user who had it paused before the session gets their pause back.
        bool wasUnpausedClock = _clockUnpaused;
        DisengageUnpausedClock();
        bool restoreToRunning = _pausedByUs && !_prevPaused;
        bool restoreToPaused = _pausedByUs && _prevPaused && wasUnpausedClock;
        _pausedByUs = false;
        if (restoreToRunning) { try { APIs.EmuClient.Unpause(); } catch { } }
        else if (restoreToPaused) { try { APIs.EmuClient.Pause(); } catch { } }
    }

    private double FrameMs()
    {
        var vp = _emulator!.ServiceProvider.GetService<IVideoProvider>();
        if (vp != null && vp.VsyncNumerator > 0 && vp.VsyncDenominator > 0)
            return 1000.0 * vp.VsyncDenominator / vp.VsyncNumerator;
        return 1000.0 / 60.0;
    }

    private void StartThread(Action body) =>
        new Thread(() => body()) { IsBackground = true, Name = "BizHawkNetplay-connect" }.Start();

    private int BeginConnectionAttempt() => _lifecycle.Begin();
    private int CurrentConnectionAttempt => _lifecycle.Current;
    private bool IsConnectionAttemptCurrent(int attempt) => _lifecycle.IsCurrent(attempt);
    private void InvalidateConnectionAttempt() => _lifecycle.Invalidate();
    private void AllowHandshakeClients() => _lifecycle.AcceptNew();
    private bool TrackHandshakeClient(TcpClient? tcp, int attempt) => _lifecycle.Track(tcp, attempt);
    private void UntrackHandshakeClient(TcpClient? tcp) => _lifecycle.Untrack(tcp);
    private bool HasHandshakeClients() => _lifecycle.HasTracked;

    private static long MonotonicNow() => System.Diagnostics.Stopwatch.GetTimestamp();

    private static long MonotonicTicks(double seconds) =>
        (long)Math.Ceiling(Math.Max(0, seconds) * System.Diagnostics.Stopwatch.Frequency);

    private static long MonotonicDeadline(double seconds) =>
        MonotonicNow() + MonotonicTicks(seconds);

    private static double MonotonicElapsedSeconds(long startedAt) => startedAt == 0
        ? double.PositiveInfinity
        : (MonotonicNow() - startedAt) / (double)System.Diagnostics.Stopwatch.Frequency;

    private static int StateTransferTimeoutMs(int stateBytes) =>
        StateTransferBudget.SocketTimeoutMs(stateBytes, HandshakeReceiveTimeoutMs);

    private static void ConfigureStateTransferTimeouts(TcpClient? tcp, int stateBytes)
    {
        if (tcp == null) return;
        int timeout = StateTransferTimeoutMs(stateBytes);
        try { tcp.ReceiveTimeout = timeout; tcp.SendTimeout = timeout; } catch { }
    }

    private static T WithAbsoluteSocketDeadline<T>(TcpClient tcp, int timeoutMs, Func<T> action)
    {
        using (var deadline = new AbsoluteSocketDeadline(tcp, timeoutMs))
        {
            try
            {
                T result = action();
                if (!deadline.TryComplete())
                    throw new TimeoutException("peer authentication deadline expired");
                return result;
            }
            catch (Exception ex) when (deadline.Expired && !(ex is TimeoutException))
            {
                throw new TimeoutException("peer authentication deadline expired", ex);
            }
        }
    }

    private void UpdateEnabled()
    {
        bool host = _hostRadio.Checked;
        _ipBox.Enabled = !host;
        _playersBox.Enabled = host; // only the host chooses the player count
        _autoDelayCheck.Enabled = host;
        _autoDelayMaxBox.Enabled = host && _autoDelayCheck.Checked;
        // The host settles netcode and delay for the whole session, and UPnP forwards the host's
        // own port. Greyed out rather than hidden so a joiner can still read what they mean —
        // and see the value they'll be joining under is not theirs to set. LocalPreferences is
        // what makes that true rather than merely implied.
        _netcodeCombo.Enabled = host;
        _delayBox.Enabled = host;
        _upnpCheck.Enabled = host;
        _goButton.Text = host ? "Start Hosting" : "Join";
        UpdatePunchUiForRole();
    }

    /// <summary>
    /// What this peer asks the session for.
    ///
    /// A host asks for what its own controls say. A joiner asks for nothing it could impose:
    /// rollback is opted into unconditionally so a stale local dropdown cannot veto the host's
    /// choice, and the delay ask is the floor so a stale local number cannot raise the session's
    /// — the negotiator honours the LARGEST ask, so anything else would let a disabled control go
    /// on quietly deciding things.
    ///
    /// What a joiner still decides is nothing to do with preference: the rollback depth it
    /// advertises is measured on its own machine, and the host refuses rollback outright if any
    /// joiner's is too shallow. Capability is not up for negotiation; taste is the host's.
    /// </summary>
    private SessionPreferences LocalPreferences(bool isHost) =>
        isHost
            ? new SessionPreferences((int)_delayBox.Value,
                _netcodeChoice != NetcodeChoice.Lockstep, _passwordBox.Text)
            : new SessionPreferences(1, true, _passwordBox.Text);

    private void SetBusy(bool busy)
    {
        _goButton.Enabled = !busy;
        _hostRadio.Enabled = _joinRadio.Enabled = !busy;
        _ipBox.Enabled = !busy && _joinRadio.Checked;
        _playersBox.Enabled = !busy && _hostRadio.Checked;
        _portBox.Enabled = _delayBox.Enabled = !busy;
        _autoDelayCheck.Enabled = !busy && _hostRadio.Checked;
        _autoDelayMaxBox.Enabled = !busy && _hostRadio.Checked && _autoDelayCheck.Checked;
        _netcodeCombo.Enabled = _passwordBox.Enabled = _upnpCheck.Enabled = !busy;
        _inputSourceCombo.Enabled = !busy;
        _probeButton.Enabled = !busy;
        _punchButton.Enabled = !busy;
        _disconnectButton.Enabled = busy;
        // _testInputButton stays enabled (useful to check bindings before and during a session)
        RefreshLiveSettingsUi(); // re-opens netcode/delay for a host whose session is already running
    }

    private void Status(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    /// <summary>
    /// Append to the Log tab, keeping only the most recent <see cref="LogMaxLines"/> lines.
    ///
    /// The cap is not cosmetic. This is a diagnostic firehose — verbose mode logs checksums, audio
    /// stats and stall notices for as long as you play — and the backing Win32 EDIT control gets
    /// slower to append to as its buffer grows, so an unbounded log quietly taxes the UI thread that
    /// also owns the frame clock. Trimming rewrites the whole control, so it's amortized: it happens
    /// once every (LogMaxLines - LogKeepLines) appends, not on every line.
    /// </summary>
    /// <summary>
    /// Create this launch's log file, now that something has happened worth keeping.
    ///
    /// Called when a connection is ATTEMPTED, not when one succeeds: a refused password, a
    /// handshake mismatch and a join that timed out are the logs most often asked for, and none of
    /// them ever reaches a session. Everything logged before this point was buffered and goes into
    /// the file too, so the run-up is not lost. Idempotent.
    /// </summary>
    private void StartLogFile()
    {
        if (_logFile == null || _logFile.IsOpen) return;
        if (!_logFile.Activate())
        {
            Log("(note) couldn't write a log file — the session is unaffected, but there will be " +
                $"nothing to send afterwards. Tried: {SessionLog.Folder}");
            return;
        }
        Log($"logging this session to {_logFile.Path}");
        if (!_logFileLabel.IsDisposed)
            _logFileLabel.Text = "saving to " + System.IO.Path.GetFileName(_logFile.Path!);
    }

    private void Log(string message)
    {
        // Stamped like the connection box, and for the same reason: almost every question worth
        // asking of a log is about ORDER and GAPS — how long the state transfer took, whether the
        // stall came before or after the drop — and an unstamped line can answer neither. Only the
        // first line of a multi-line message is prefixed; the continuation lines are one event.
        string stamped = RotatingLogFile.Stamp() + "  " + message;

        _logFile?.Write(stamped);
        if (_log.IsDisposed) return;
        _log.AppendText(stamped + Environment.NewLine);

        _logLines += 1 + CountNewlines(message); // a single message can carry several lines (e.g. AudioStats)
        if (_logLines <= LogMaxLines) return;

        int cut = IndexAfterNewline(_log.Text, _logLines - LogKeepLines);
        if (cut <= 0) { _logLines = LogKeepLines; return; }
        _log.Text = _log.Text.Substring(cut);
        _logLines = LogKeepLines;
        _log.SelectionStart = _log.TextLength; // setting Text resets the caret; stay pinned to the newest line
        _log.ScrollToCaret();
    }

    private static int CountNewlines(string s)
    {
        int n = 0;
        foreach (char c in s) if (c == '\n') n++;
        return n;
    }

    /// <summary>Index just past the <paramref name="count"/>-th newline, or -1 if there aren't that many.</summary>
    private static int IndexAfterNewline(string s, int count)
    {
        if (count <= 0) return 0;
        int seen = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\n') continue;
            if (++seen == count) return i + 1;
        }
        return -1;
    }

    private void UiLog(string message) => BeginInvokeUi(() => Log(message));

    /// <summary>
    /// Report a connection-lifecycle event — hosting/joining/refused/connected/dropped/ended. It goes
    /// to the Connection tab's box, where someone who just failed to join can actually see it, and to
    /// the full log. Colors carry the verdict: red refused/failed, green connected, orange interrupted.
    /// Per-frame and diagnostic chatter stays on <see cref="Log"/> so this box stays readable.
    /// </summary>
    private void ConnLog(string message, Color color)
    {
        Log(message);
        // These are the lines a log is read FOR, and they are rare — a handful per session. Getting
        // them onto disk immediately is what makes the file useful after EmuHawk is killed or hangs,
        // which is exactly the situation someone is being asked for their log about.
        _logFile?.Flush();
        if (_connLog.IsDisposed) return;

        // Bound the history — a long session can rack up drops, rejoins and resyncs, and an unbounded
        // RichTextBox is a slow leak. Delete the oldest lines by selection so the kept tail retains
        // its coloring (re-appending them as text would flatten it).
        if (_connLog.Lines.Length > ConnLogMaxLines)
        {
            int cut = _connLog.GetFirstCharIndexFromLine(_connLog.Lines.Length - ConnLogKeepLines);
            if (cut > 0) { _connLog.Select(0, cut); _connLog.SelectedText = string.Empty; }
        }

        _connLog.SelectionStart = _connLog.TextLength;
        _connLog.SelectionLength = 0;
        _connLog.SelectionColor = color;
        _connLog.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        _connLog.SelectionColor = _connLog.ForeColor;
        _connLog.ScrollToCaret(); // newest line stays visible in the small box
    }

    /// <summary>Thread-safe <see cref="ConnLog"/> for the accept/join/reconnect background threads.</summary>
    private void UiConnLog(string message, Color color) => BeginInvokeUi(() => ConnLog(message, color));

    /// <summary>Set the lobby status line from the lobby/join threads. Same marshalling as
    /// <see cref="UiConnLog"/> — these call sites are almost always the ones already logging.</summary>
    private void UiLobbyPhase(string text, Color color) => BeginInvokeUi(() => SetLobbyPhase(text, color));

    private void InvokeUiBlocking(Action action)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(NetplayToolForm));
        if (!InvokeRequired) { action(); return; }
        Invoke(action);
    }

    private void BeginInvokeUi(Action action)
    {
        if (IsDisposed) return;
        try { BeginInvoke(action); } catch { /* form closing */ }
    }
}
