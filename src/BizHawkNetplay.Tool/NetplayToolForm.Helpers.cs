using System;
using System.Drawing;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using BizHawk.Client.Common;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Tool
{
    public sealed partial class NetplayToolForm
    {
        // --- State used only by this file (everything shared stays in NetplayToolForm.cs) ---
        private Config? _config;
        private bool _configApplied;
        private int _logLines;      // lines currently in _log, tracked so trimming needn't split its text
        private bool _prevAcceptBackgroundInput;
        private bool _prevAcceptBackgroundInputControllerOnly;
        private bool _prevRunInBackground;
        private Label _status = null!;


        /// <summary>
        /// While a session is live, keep EmuHawk running and accepting input even when its window
        /// isn't focused — otherwise two instances on one screen pause each other (only one can be
        /// focused). Restores the user's original settings when the session ends.
        /// </summary>
        private void ApplyBackgroundConfig(bool enable)
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
                    // Controller-only: the unfocused window still reads its gamepad, but background
                    // KEYBOARD is ignored — so typing in another window can't fire an EmuHawk hotkey
                    // (rewind/load-state) that would desync the session.
                    _config.AcceptBackgroundInputControllerOnly = true;
                    _configApplied = true;
                    Log("run-in-background enabled (controller-only) for this session");
                }
                else if (_configApplied && _config != null)
                {
                    _config.RunInBackground = _prevRunInBackground;
                    _config.AcceptBackgroundInput = _prevAcceptBackgroundInput;
                    _config.AcceptBackgroundInputControllerOnly = _prevAcceptBackgroundInputControllerOnly;
                    _configApplied = false;
                }
            }
            catch (Exception ex) { Log("(note) background-config adjust failed: " + ex.Message); }
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
}
