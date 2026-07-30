using System;
using System.Drawing;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using BizHawk.Client.Common;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    // --- State used only by this file (everything shared stays in NetplayToolForm.cs) ---
    private Config? _config;
    private bool _configApplied;
    private int _logLines;      // lines currently in _log, tracked so trimming needn't split its text
    private bool _prevAcceptBackgroundInput;
    private bool _prevAcceptBackgroundInputControllerOnly;
    private bool _prevRunInBackground;
    private bool _prevBlockFrameAdvance;
    private bool _prevPaused;   // was EmuHawk already paused when we first took the clock?
    private bool _pausedByUs;   // ...and have we actually taken it, so there is something to undo
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
                _config = (APIs.Emulation as EmulationApi)?.ForbiddenConfigReference;
                if (_config == null) { Log("(note) couldn't reach config to disable pause-on-unfocus"); return; }
                _prevRunInBackground = _config.RunInBackground;
                _prevAcceptBackgroundInput = _config.AcceptBackgroundInput;
                _prevAcceptBackgroundInputControllerOnly = _config.AcceptBackgroundInputControllerOnly;
                _config.RunInBackground = true;
                _config.AcceptBackgroundInput = true;
                _config.AcceptBackgroundInputControllerOnly = true;
                try { _prevBlockFrameAdvance = MainForm.BlockFrameAdvance; MainForm.BlockFrameAdvance = true; }
                catch (Exception ex) { Log("(note) could not block EmuHawk's frame advance: " + ex.Message); }
                _configApplied = true;
                Log("run-in-background enabled (controller-only); EmuHawk's own frame advance blocked");
            }
            else if (_configApplied && _config != null)
            {
                _config.RunInBackground = _prevRunInBackground;
                _config.AcceptBackgroundInput = _prevAcceptBackgroundInput;
                _config.AcceptBackgroundInputControllerOnly = _prevAcceptBackgroundInputControllerOnly;
                try { MainForm.BlockFrameAdvance = _prevBlockFrameAdvance; } catch { }
                // Resume rewind if the user's preference says it should be on. EnableRewind is a
                // suspend/resume, not a config change, so Config.Rewind.Enabled still holds their
                // intent — and reading it now rather than at session start honours a change made
                // while we were playing.
                try { APIs.EmuClient.EnableRewind(_config.Rewind.Enabled); } catch { }
                _configApplied = false;
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
        if (APIs.EmuClient.IsPaused()) return;
        APIs.EmuClient.Pause();
        if (Verbose) Log("re-paused (the session owns the frame clock — don't unpause)");
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
    private void PauseForSession()
    {
        if (!_pausedByUs)
        {
            try { _prevPaused = MainForm.EmulatorPaused; } catch { _prevPaused = false; }
            _pausedByUs = true;
        }
        APIs.EmuClient.Pause();
    }

    /// <summary>Undo <see cref="PauseForSession"/>, leaving a deliberately-paused emulator paused.</summary>
    private void RestorePauseState()
    {
        bool restoreToRunning = _pausedByUs && !_prevPaused;
        _pausedByUs = false;
        if (restoreToRunning) { try { APIs.EmuClient.Unpause(); } catch { } }
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
    private void Log(string message)
    {
        if (_log.IsDisposed) return;
        _log.AppendText(message + Environment.NewLine);

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
