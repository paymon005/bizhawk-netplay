using System;
using System.Drawing;
using System.Windows.Forms;

namespace BizHawkNetplay.Tool;

public sealed partial class NetplayToolForm
{
    // --- State used only by this file (everything shared stays in NetplayToolForm.cs) ---
    private CheckBox _forceDesyncCheck = null!;
    private CheckBox _freezeInputCheck = null!;
    private Label _myCodeLabel = null!;
    private Label _peerCodeLabel = null!;
    private Label _playersHint = null!;
    private ListView _playersList = null!;
    private Label _punchInstructions = null!;
    private NetplaySettings _settings = null!;     // persisted UI prefs (UPnP, port, delay, netcode, recent IPs)
    private readonly ToolTip _tips = new(); // owns a native window — disposed with the form
    private CheckBox _unpausedClockCheck = null!;
    private CheckBox _aboveNativeCheck = null!;
    private CheckBox _deferToMajorityCheck = null!;
    private CheckBox _verboseCheck = null!;

    // ------------------------------------------------------------------ persisted settings

    /// <summary>Load remembered prefs and apply them to the controls, then hook change-to-save.</summary>
    private void LoadAndApplySettings()
    {
        _settings = NetplaySettings.Load();
        _loadingSettings = true;
        try
        {
            _upnpCheck.Checked = _settings.Upnp;
            _portBox.Value = Clamp(_settings.Port, (int)_portBox.Minimum, (int)_portBox.Maximum);
            _playersBox.Value = Clamp(_settings.Players, (int)_playersBox.Minimum, (int)_playersBox.Maximum);
            _delayBox.Value = Clamp(_settings.Delay, (int)_delayBox.Minimum, (int)_delayBox.Maximum);
            _autoDelayCheck.Checked = _settings.AutoDelay;
            _autoDelayMaxBox.Value = Clamp(_settings.AutoDelayMax,
                (int)_autoDelayMaxBox.Minimum, (int)_autoDelayMaxBox.Maximum);
            if (_settings.Netcode >= 0 && _settings.Netcode < _netcodeCombo.Items.Count)
                _netcodeCombo.SelectedIndex = _settings.Netcode;
            if (_settings.InputSource >= 0 && _settings.InputSource < _inputSourceCombo.Items.Count)
                _inputSourceCombo.SelectedIndex = _settings.InputSource;
            RefreshIpDropdown();
            if (_settings.RecentIps.Count > 0) _ipBox.Text = _settings.RecentIps[0]; // last host, ready to re-join
        }
        finally { _loadingSettings = false; }

        // Persist whenever a remembered control changes, so state survives even without starting a session.
        _upnpCheck.CheckedChanged += (_, __) => SaveSettingsFromUi();
        _portBox.ValueChanged += (_, __) => SaveSettingsFromUi();
        _playersBox.ValueChanged += (_, __) => SaveSettingsFromUi();
        _delayBox.ValueChanged += (_, __) => SaveSettingsFromUi();
        _autoDelayCheck.CheckedChanged += (_, __) => { UpdateEnabled(); SaveSettingsFromUi(); };
        _autoDelayMaxBox.ValueChanged += (_, __) => SaveSettingsFromUi();
        _netcodeCombo.SelectedIndexChanged += (_, __) => SaveSettingsFromUi();
        _inputSourceCombo.SelectedIndexChanged += (_, __) => SaveSettingsFromUi();
    }

    /// <summary>
    /// Cap the Players box at the loaded core's controller-port count and show that ceiling next to
    /// it. A session can only fill ports the core actually exposes — Genesis is 2 until you enable
    /// the 4-Way Play / Team Player adapter, N64 is 4 natively — and picking 4 on a 2-port core used
    /// to be accepted, silently clamped, and only explained by a line in the Log tab.
    ///
    /// The clamp deliberately does NOT overwrite the remembered preference: someone who wants 4
    /// players and switches from Genesis to N64 gets their 4 back rather than being stuck at the
    /// lowest core they ever loaded. Called on every core/ROM change (<see cref="Restart"/>), which
    /// is also when enabling a multitap in the core's sync settings takes effect.
    /// </summary>
    private void RefreshPlayerLimit()
    {
        int max = 8;          // no core loaded yet: leave the box's own ceiling in place
        bool known = false;
        try
        {
            if (_emulator != null) { max = Math.Max(2, EmuHawkAdapter.PortCountOf(_emulator)); known = true; }
        }
        catch { /* odd core definition — fall back to the unrestricted box */ }

        _loadingSettings = true; // a programmatic clamp must not persist over the user's choice
        try
        {
            int want = _settings != null ? _settings.Players : (int)_playersBox.Value;
            _playersBox.Maximum = max;
            _playersBox.Value = Clamp(want, (int)_playersBox.Minimum, max);
            _playersHint.Text = known ? $"of {max}" : "";
        }
        finally { _loadingSettings = false; }
    }

    private void SaveSettingsFromUi()
    {
        if (_loadingSettings || _settings == null) return;
        _settings.Upnp = _upnpCheck.Checked;
        _settings.Port = (int)_portBox.Value;
        _settings.Players = (int)_playersBox.Value;
        _settings.Delay = (int)_delayBox.Value;
        _settings.AutoDelay = _autoDelayCheck.Checked;
        _settings.AutoDelayMax = (int)_autoDelayMaxBox.Value;
        _settings.Netcode = _netcodeCombo.SelectedIndex;
        _settings.InputSource = _inputSourceCombo.SelectedIndex;
        _settings.Save();
    }

    /// <summary>Record a successfully-joined host IP into the recent list and refresh the dropdown.</summary>
    private void RecordJoinIp(string ip)
    {
        if (_settings == null) return;
        _settings.RecordIp(ip);
        SaveSettingsFromUi(); // also persists the current control values alongside the new IP
        RefreshIpDropdown();
    }

    /// <summary>Repopulate the IP dropdown from the recent list, preserving the typed text.</summary>
    private void RefreshIpDropdown()
    {
        string current = _ipBox.Text;
        _ipBox.BeginUpdate();
        _ipBox.Items.Clear();
        foreach (var ip in _settings.RecentIps) _ipBox.Items.Add(ip);
        _ipBox.EndUpdate();
        _ipBox.Text = current;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>The Connection tab: role, host address/port, delay, rollback, and the start/stop buttons.</summary>
    private TabPage BuildConnectionTab()
    {
        var page = new TabPage("Connection") { Padding = new Padding(8) };

        _hostRadio = new RadioButton { Text = "Host", Checked = true, AutoSize = true, Location = new Point(12, 12) };
        _joinRadio = new RadioButton { Text = "Join", AutoSize = true, Location = new Point(80, 12) };
        _hostRadio.CheckedChanged += (_, __) => UpdateEnabled();

        var ipLabel = new Label { Text = "Host IP:", AutoSize = true, Location = new Point(12, 46) };
        _ipBox = new ComboBox
        {
            Text = "127.0.0.1", Location = new Point(80, 43), Width = 160,
            DropDownStyle = ComboBoxStyle.DropDown, // editable, with a dropdown of recently-used IPs
        };
        _tips.SetToolTip(_ipBox,
            "The host's address: 1.2.3.4 or 1.2.3.4:47800.\r\n" +
            "A port typed here overrides the Port box.");
        var portLabel = new Label { Text = "Port:", AutoSize = true, Location = new Point(260, 46) };
        _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = DefaultPort, Location = new Point(300, 43), Width = 70 };
        var playersLabel = new Label { Text = "Players:", AutoSize = true, Location = new Point(388, 46) };
        _playersBox = new NumericUpDown { Minimum = 2, Maximum = 8, Value = 2, Location = new Point(444, 43), Width = 46 };
        // The ceiling is the core's controller-port count, so say what it is instead of letting
        // someone pick 4 and only find out at start time that the core exposes 2 (see RefreshPlayerLimit).
        _playersHint = new Label { Text = "", AutoSize = true, Location = new Point(494, 46), ForeColor = Color.DimGray };

        var passwordLabel = new Label { Text = "Password:", AutoSize = true, Location = new Point(12, 78) };
        _passwordBox = new TextBox { Location = new Point(80, 75), Width = 160, UseSystemPasswordChar = true };
        var passwordHint = new Label { Text = "(optional; must match on both ends)", AutoSize = true, Location = new Point(248, 78), ForeColor = Color.DimGray };

        var delayLabel = new Label { Text = "Input delay:", AutoSize = true, Location = new Point(12, 110) };
        // This is always honored as a manual floor. Auto may raise it before WELCOME, but never
        // changes the running timeline or lowers a value explicitly requested by either player.
        _delayBox = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 1, Location = new Point(90, 107), Width = 50 };
        _autoDelayCheck = new CheckBox
        {
            Text = "Auto from ping", AutoSize = true, Checked = true, Location = new Point(150, 109),
        };
        var autoDelayMaxLabel = new Label { Text = "Max:", AutoSize = true, Location = new Point(270, 110) };
        _autoDelayMaxBox = new NumericUpDown
        {
            Minimum = 1, Maximum = 20, Value = 8, Location = new Point(306, 107), Width = 45,
        };
        _tips.SetToolTip(_delayBox,
            "Fixed input delay, or the minimum when Auto is enabled.\r\n" +
            "Each frame reduces typical rollback correction but adds one frame of local response time.");
        _tips.SetToolTip(_autoDelayCheck,
            "Host only: measure every direct UDP path and choose delay before play starts.\r\n" +
            "It picks once, at the start. To change delay later, edit the box and press\r\n" +
            "Apply changes — the host can do that without ending the session.");
        _tips.SetToolTip(_autoDelayMaxBox,
            "Largest delay Auto may choose. Explicit player delay requests are still honored.");

        var netcodeSelLabel = new Label { Text = "Netcode:", AutoSize = true, Location = new Point(366, 110) };
        _netcodeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(426, 107), Width = 120 };
        _netcodeCombo.Items.AddRange(["Automatic", "Rollback", "Lockstep"]);
        _netcodeCombo.SelectedIndex = 0; // Automatic: rollback if the core qualifies, else lockstep

        var inputSrcLabel = new Label { Text = "My controls:", AutoSize = true, Location = new Point(12, 142) };
        _inputSourceCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(90, 139), Width = 130 };
        _inputSourceCombo.Items.AddRange(["Use P1 pad", "Use P2 pad", "Use P3 pad", "Use P4 pad", "Assigned port"]);
        _inputSourceCombo.SelectedIndex = 0; // default: read your normal P1 controls, whatever port you're assigned

        _upnpCheck = new CheckBox { Text = "Auto-forward host port (UPnP)", AutoSize = true, Checked = true, Location = new Point(240, 141) };

        _goButton = new Button { Text = "Start Hosting", Location = new Point(12, 172), Width = 150 };
        _goButton.Click += (_, __) => OnGo();
        _disconnectButton = new Button { Text = "Disconnect", Location = new Point(172, 172), Width = 110, Enabled = false };
        _disconnectButton.Click += (_, __) => EndSession("disconnected by user");
        _pubAddrButton = new Button { Text = "My public address", Location = new Point(292, 172), Width = 150 };
        _pubAddrButton.Click += (_, __) => ShowPublicAddress();
        _applyLiveButton = new Button
        {
            Text = "Apply changes", Location = new Point(452, 172), Width = 104, Enabled = false,
        };
        _applyLiveButton.Click += (_, __) => ApplyLiveSettingsAsHost();
        _tips.SetToolTip(_applyLiveButton,
            "Host, during a session: push the Netcode and Input delay above to everyone.\r\n" +
            "Nobody disconnects — the session pauses briefly while one savestate is shared,\r\n" +
            "the same way a desync recovery does. Costs more on a heavy core with a big state.");

        // Connection log: the did-I-get-in answer, on the tab you're already looking at. The Log tab
        // carries the full diagnostic firehose, which is the wrong place to learn that your password
        // was wrong — only connection-lifecycle events land here, color-coded (red = refused/failed,
        // green = connected). See ConnLog.
        // Lobby status sits ABOVE the connection log, because the two answer different questions:
        // this one is "what is happening right now", the log is "what happened". Full width rather
        // than the old 300px, since the states it has to carry ("waiting for the host to fill the
        // lobby", "holding the seat for a rejoin") are sentences and not labels.
        //
        // Two lines in one box: the state on top, and beneath it the netcode and delay the session
        // actually settled on — which is the follow-up question every time the top line changes.
        var lobbyLabel = new Label { Text = "Lobby status:", AutoSize = true, Location = new Point(12, 348) };
        _lobbyPanel = new Panel
        {
            Location = new Point(12, 366), Size = new Size(544, 48),
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White,
        };
        _lobbyStateLabel = new Label
        {
            Text = "Not connected.", Location = new Point(6, 5), Size = new Size(530, 18),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };
        _netcodeLabel = new Label
        {
            Text = "Netcode in use: —", Location = new Point(6, 25), Size = new Size(530, 18),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray,
        };
        _lobbyPanel.Controls.AddRange([_lobbyStateLabel, _netcodeLabel]);

        var connLogLabel = new Label { Text = "Connection status:", AutoSize = true, Location = new Point(12, 424) };
        _connLog = new RichTextBox
        {
            Location = new Point(12, 442), Size = new Size(544, 92),
            ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            ScrollBars = RichTextBoxScrollBars.Vertical, TabStop = false, DetectUrls = false,
        };

        page.Controls.AddRange(
        [
            _hostRadio, _joinRadio, ipLabel, _ipBox, portLabel, _portBox, playersLabel, _playersBox, _playersHint,
            passwordLabel, _passwordBox, passwordHint, delayLabel, _delayBox, _autoDelayCheck,
            autoDelayMaxLabel, _autoDelayMaxBox,
            netcodeSelLabel, _netcodeCombo, inputSrcLabel, _inputSourceCombo, _upnpCheck,
            _goButton, _disconnectButton, _pubAddrButton, _applyLiveButton,
            lobbyLabel, _lobbyPanel, connLogLabel, _connLog, BuildPunchGroup(),
        ]);
        return page;
    }

    /// <summary>
    /// The "UDP Punch" group: the no-port-forwarding path (2-player). Distinct buttons for a regular
    /// connection (above) versus punch, exactly as requested — punch isn't a silent fallback, it's an
    /// explicit choice that surfaces a connect code you swap with your friend out of band.
    /// </summary>
    private GroupBox BuildPunchGroup()
    {
        _punchGroup = new GroupBox
        {
            Text = "UDP Punch — play without port-forwarding",
            Location = new Point(12, 200), Size = new Size(544, 140),
        };

        // The group shows only what YOUR role needs (see UpdatePunchUiForRole): a joiner
        // punches and sends the code; a host pastes codes into its waiting lobby.
        _punchInstructions = new Label
        {
            Text = "", AutoSize = true, Location = new Point(12, 22), ForeColor = Color.DimGray,
        };

        // Joiner row.
        _punchButton = new Button { Text = "UDP Punch", Location = new Point(12, 66), Width = 110 };
        _punchButton.Click += (_, __) => OnPunchStart();
        _myCodeLabel = new Label { Text = "Your code:", AutoSize = true, Location = new Point(136, 71) };
        _myCodeBox = new TextBox
        {
            ReadOnly = true, Location = new Point(206, 68), Width = 200,
            Font = new Font(FontFamily.GenericMonospace, 11f), Text = "",
        };
        _copyCodeButton = new Button { Text = "Copy", Location = new Point(414, 67), Width = 60, Enabled = false };
        _copyCodeButton.Click += (_, __) => CopyMyCode();

        // Host row (same vertical slot — only one row is ever visible).
        _peerCodeLabel = new Label { Text = "Joiner's code:", AutoSize = true, Location = new Point(12, 71) };
        _peerCodeBox = new TextBox { Location = new Point(102, 68), Width = 240 };
        _connectButton = new Button { Text = "Admit", Location = new Point(350, 67), Width = 80 };
        _connectButton.Click += (_, __) => OnPunchAdmit();

        _punchStatus = new Label
        {
            Text = "", AutoSize = true, Location = new Point(12, 104), ForeColor = Color.DimGray,
        };

        _punchGroup.Controls.AddRange(
        [
            _punchInstructions, _punchButton, _myCodeLabel, _myCodeBox, _copyCodeButton,
            _peerCodeLabel, _peerCodeBox, _connectButton, _punchStatus,
        ]);
        UpdatePunchUiForRole();
        return _punchGroup;
    }

    /// <summary>Show only the punch controls the selected role uses — a joiner punches and
    /// sends a code; a host pastes codes into its waiting lobby.</summary>
    private void UpdatePunchUiForRole()
    {
        if (_punchGroup == null || _punchGroup.IsDisposed) return;
        bool host = _hostRadio.Checked;
        _punchInstructions.Text = host
            ? "A player who can't reach you: they pick Join, enter your IP, and click UDP Punch.\nWhile your lobby is waiting, paste the code they send you:"
            : "Can't reach the host? Enter the host's IP above as usual, then click UDP Punch.\nSend the code it shows to the host, and stay put — it connects when they paste it.";
        _punchButton.Visible = !host;
        _myCodeLabel.Visible = !host;
        _myCodeBox.Visible = !host;
        _copyCodeButton.Visible = !host;
        _peerCodeLabel.Visible = host;
        _peerCodeBox.Visible = host;
        _connectButton.Visible = host;
    }

    /// <summary>The Diagnostics tab: the capability probe, input test, and the fault-injection toggles.</summary>
    private TabPage BuildDiagnosticsTab()
    {
        var page = new TabPage("Diagnostics") { Padding = new Padding(8) };

        _probeButton = new Button { Text = "Capability Probe", Location = new Point(12, 12), Width = 130 };
        _probeButton.Click += (_, __) => RunProbe();
        _testInputButton = new Button { Text = "Test Input", Location = new Point(152, 12), Width = 130 };
        _testInputButton.Click += (_, __) => RunInputTest();
        _analogWatchButton = new Button { Text = "Watch Analog (5s)", Location = new Point(292, 12), Width = 130 };
        _analogWatchButton.Click += (_, __) => StartAnalogWatch();
        _tips.SetToolTip(_analogWatchButton,
            "Click, then move the stick through its FULL travel for five seconds.\r\n" +
            "Reports every distinct value that actually reached the core, so a stick\r\n" +
            "that jumps from small values straight to full deflection shows the gap.");

        _saveWritePathButton = new Button
        { Text = "Savestate Cost", Location = new Point(432, 12), Width = 130 };
        _saveWritePathButton.Click += (_, __) => RunSaveWritePathTest();
        _tips.SetToolTip(_saveWritePathButton,
            "Times one savestate and records how the core wrote it, then replays that\r\n" +
            "shape without the core. On a heavy core the snapshot is the biggest term in\r\n" +
            "the rollback budget, and this says how much of it is the core gathering\r\n" +
            "state versus our write path — i.e. whether any of it can be won back.");

        _verboseCheck = new CheckBox { Text = "Verbose log", AutoSize = true, Location = new Point(12, 54) };
        _freezeInputCheck = new CheckBox { Text = "Freeze input (diag)", AutoSize = true, Location = new Point(12, 78) };
        _freezeInputCheck.CheckedChanged += (_, __) =>
            EmuHawkAdapter.ForceNeutralInput = _freezeInputCheck.Checked;
        _forceDesyncCheck = new CheckBox { Text = "Force desync (diag)", AutoSize = true, Location = new Point(12, 102) };
        _forceDesyncCheck.CheckedChanged += (_, __) =>
        {
            if (!_forceDesyncCheck.Checked) return;
            _forceDesyncOnce = true;
            _forceDesyncCheck.Checked = false;
            Log(_phase.IsActive ? "will inject a fake desync at the next checksum (tests resync)"
                               : "arm this during a session to test resync");
        };

        // The "skip our video present" checkbox lived here. It asked whether our own present
        // duplicated the Render() EmuHawk runs every loop iteration; it does, the answer was
        // measured on N64 on 2026-07-30, and the fine clock has skipped presenting unconditionally
        // ever since. What the box still controlled was the WM_TIMER fallback path — the one case
        // where the present is genuinely load-bearing — so ticking it had stopped being a diagnostic
        // and become a way to freeze the picture.

        // Experimental: while a session runs, keep EmuHawk UNPAUSED with only BlockFrameAdvance
        // standing between its loop and the core. On the other side of the pause branch in
        // Throttle.Step — which is an unconditional Thread.Sleep(15), the ~66Hz ceiling on the
        // session's frame clock — sits SpeedThrottle: a phase-locked, drift-corrected wait derived
        // from the core's own vsync rate. If it works, judder collapses; if a frame is ever stolen,
        // the drift check ends the session by name. Off by default until measured in real play.
        _unpausedClockCheck = new CheckBox
        {
            Text = "Unpaused clock (experimental)", AutoSize = true, Location = new Point(200, 160),
        };
        _tips.SetToolTip(_unpausedClockCheck,
            "Experimental. Runs the session with EmuHawk unpaused (frame stepping still belongs to\r\n" +
            "netplay via BlockFrameAdvance), which swaps the paused loop's coarse 15ms sleep for\r\n" +
            "EmuHawk's own phase-locked frame clock. Watch 'judder' and 'gap' in the pacing line.\r\n" +
            "Read at session start; toggling mid-session does nothing until the next session.");

        // The N64 above-native opt-in. Deliberately a checkbox with a blunt tooltip rather than a
        // default: excluding memory from the desync checksum trades detection for playability, and
        // it is only sound while the excluded bytes are never read back by the game. Rice copies
        // rendered data into RDRAM when emulated code reads framebuffer memory, so on a game that
        // does read it, a real divergence there would go unseen. That is the player's trade to
        // make knowingly. Does nothing at native resolution, whatever the box says.
        _aboveNativeCheck = new CheckBox
        {
            Text = "Allow above-native N64 (experimental)", AutoSize = true, Location = new Point(200, 186),
        };
        _tips.SetToolTip(_aboveNativeCheck,
            "Experimental, N64 only, and only above native resolution.\r\n\r\n" +
            "Above native the video plugin resolves its framebuffer back into console RAM, and those\r\n" +
            "bytes come from your GPU — so two players disagree even when both are perfectly in sync.\r\n" +
            "With this on, the session measures which memory that is and stops checksumming it.\r\n\r\n" +
            "The trade: a game that READS its own framebuffer can then diverge for real without being\r\n" +
            "detected. Most games never do. Leave this off and play at native if you want the\r\n" +
            "strongest desync detection; the measurement is logged either way.");

        // Host-side, and ON by default as of v0.36.0. Recovery distributes the host's state, so a
        // lone-diverged host overwrites everyone who was right — an accident that happens for
        // ordinary reasons (a Lua script, a cheat, a stray savestate load) and ruins the session for
        // the players who were correct. Deferring means the host adopts a peer's savestate, which is
        // a trusted-input format all the way into the core and an exposure that used to be the
        // joiners' alone (KI-13). That trade is now taken by default: abusing it needs a colluding
        // MAJORITY — two of three players, or three of four — where the accident it prevents needs
        // nobody at all. Unticking restores the old host-always-wins behaviour. See MajorityRecovery.
        _deferToMajorityCheck = new CheckBox
        {
            Text = "Defer to the majority on a desync", AutoSize = true, Location = new Point(200, 210),
            Checked = true,
        };
        _tips.SetToolTip(_deferToMajorityCheck,
            "Host only. On by default.\r\n\r\n" +
            "A resync makes everyone adopt the HOST's state. If the host is the machine that\r\n" +
            "diverged — a Lua script, a cheat, a stray savestate load — that overwrites the players\r\n" +
            "who were right. With this on, the host asks a machine from the majority for its state\r\n" +
            "and everyone adopts that instead.\r\n\r\n" +
            "The trade: the host then loads a peer's savestate, and a savestate can set memory, page\r\n" +
            "permissions and the stack pointer of the emulated machine. It takes a colluding\r\n" +
            "majority to abuse — two of three players, or three of four — not one bad peer, and the\r\n" +
            "mistake it prevents takes nobody at all. Untick it if you are hosting for strangers;\r\n" +
            "the log still names the case either way.");

        var simLatencyLabel = new Label { Text = "Sim latency ms:", AutoSize = true, Location = new Point(12, 132) };
        _simLatencyBox = new NumericUpDown { Minimum = 0, Maximum = 500, Increment = 10, Value = 0, Location = new Point(110, 130), Width = 60 };
        _simUnresponsiveCheck = new CheckBox { Text = "Simulate unresponsive (diag)", AutoSize = true, Location = new Point(12, 160) };
        _simUnresponsiveCheck.CheckedChanged += (_, __) =>
        {
            _simUnresponsive = _simUnresponsiveCheck.Checked;
            if (_phase.IsActive)
                Log(_simUnresponsive
                    ? "simulating an unresponsive peer — we've stopped answering pings; the other side should drop us in ~3s"
                    : "resumed responding to pings");
        };

        page.Controls.AddRange(
        [
            _probeButton, _testInputButton, _analogWatchButton, _saveWritePathButton,
            _verboseCheck, _freezeInputCheck, _forceDesyncCheck,
            simLatencyLabel, _simLatencyBox, _simUnresponsiveCheck, _unpausedClockCheck,
            _aboveNativeCheck,
        ]);
        return page;
    }

    /// <summary>The Players tab: a live list of everyone in the session with their address and ping.</summary>
    private TabPage BuildPlayersTab()
    {
        var page = new TabPage("Players");
        _playersList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _playersList.Columns.Add("Player", 80);
        _playersList.Columns.Add("Address", 220);
        _playersList.Columns.Add("Ping", 70);
        _playersList.Columns.Add("Link", 120);
        page.Controls.Add(_playersList);
        return page;
    }

    /// <summary>Whether a direct UDP path to this peer is currently confirmed open (mesh punch/keepalive).</summary>
    private bool MeshLinkAlive(PeerLink link)
    {
        var mesh = _mesh;
        if (mesh == null) return false;
        return (link.UdpEndpoint != null && mesh.IsEndpointAlive(link.UdpEndpoint))
            || (link.ReflexiveEndpoint != null && mesh.IsEndpointAlive(link.ReflexiveEndpoint));
    }

    /// <summary>Human-readable direct-link state for the Players list.</summary>
    private string MeshLinkStatus(PeerLink link)
    {
        var mesh = _mesh;
        if (mesh == null) return "—";
        if (link.UdpEndpoint != null && mesh.IsEndpointAlive(link.UdpEndpoint)) return "direct";
        if (link.ReflexiveEndpoint != null && mesh.IsEndpointAlive(link.ReflexiveEndpoint)) return "direct (punched)";
        return "connecting…";
    }

    /// <summary>Rebuild the players list from the current peers (self first). Cheap for 2–4 players.</summary>
    private void RefreshPlayersList()
    {
        if (_playersList.IsDisposed) return;
        _playersList.BeginUpdate();
        _playersList.Items.Clear();
        if (_phase.IsActive)
        {
            var me = new ListViewItem($"P{_localPort + 1} (you)");
            me.SubItems.Add(_isHost ? "this machine (host)" : "this machine");
            me.SubItems.Add("—");
            me.SubItems.Add("—");
            _playersList.Items.Add(me);

            // _pingLock guards PingMs only, and the control-reader threads take it for every Pong and
            // every Pacing frame. Building ListView items under it made those threads queue behind
            // WinForms work — and on a log-trim tick, behind a full rewrite of the log buffer, which
            // is where the pacing samples that feed the time-sync valve were being delayed. Take the
            // numbers, let go, then build.
            if (_pingSnapshot.Length < _peers.Count) _pingSnapshot = new double[_peers.Count];
            lock (_pingLock)
                for (int i = 0; i < _peers.Count; i++) _pingSnapshot[i] = _peers[i].PingMs;

            for (int i = 0; i < _peers.Count; i++)
            {
                var link = _peers[i];
                double ping = _pingSnapshot[i];
                var item = new ListViewItem($"P{link.RemotePort + 1}");
                item.SubItems.Add(link.UdpEndpoint?.ToString() ?? link.Label);
                item.SubItems.Add(ping < 0 ? "…" : $"{ping + 2 * _simLatencyMs:F0} ms");
                item.SubItems.Add(MeshLinkStatus(link));
                _playersList.Items.Add(item);

                // One-time log when a peer's direct UDP path first confirms (host-as-rendezvous punch).
                if (MeshLinkAlive(link) && !link.DirectLogged)
                {
                    link.DirectLogged = true;
                    Log($"{link.Label}: direct UDP path open");
                }
            }
        }
        _playersList.EndUpdate();
    }

    /// <summary>The Log tab: the scrolling monospace session log, filling the page.</summary>
    private TabPage BuildLogTab()
    {
        var page = new TabPage("Log");
        _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        // Everything above is also being written to a file, and this is the row that says so. Put
        // here rather than under Diagnostics because this is the tab someone is already looking at
        // when they are asked for their log — a folder they cannot find is the same as no file.
        var bar = new Panel { Dock = DockStyle.Top, Height = 30 };
        var openFolder = new Button { Text = "Open log folder", Location = new Point(0, 3), Width = 120 };
        openFolder.Click += (_, __) => OpenLogFolder();
        _logFileLabel = new Label
        {
            AutoSize = true, Location = new Point(128, 8), ForeColor = Color.DimGray,
            Text = "a file is written once you host or join",
        };
        _tips.SetToolTip(openFolder,
            "Once you host or join, this log is written to a file as well as shown here.\r\n" +
            "Send that file to whoever is helping you — it keeps the whole session,\r\n" +
            "including the part this box has already scrolled past.\r\n" +
            "Opening and closing the tool without connecting writes nothing.");
        bar.Controls.AddRange([openFolder, _logFileLabel]);

        page.Controls.Add(_log);   // added first so Fill takes what the docked bar leaves
        page.Controls.Add(bar);
        return page;
    }

    /// <summary>Show the logs folder in Explorer, selecting this launch's file if there is one.</summary>
    private void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SessionLog.Folder);
            // Explorer only understands /select, with the file quoted; without a file, open the
            // folder itself rather than passing a switch with nothing after it.
            var path = _logFile?.Path;
            if (path != null && System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", $"\"{SessionLog.Folder}\"");
        }
        catch (Exception ex)
        {
            // Not fatal, and the path is the actually useful half of the answer — someone can paste
            // it into a File Explorer address bar even if launching one from here is blocked.
            Log($"couldn't open the log folder ({ex.Message}). It is: {SessionLog.Folder}");
        }
    }

    /// <summary>
    /// EmuHawk asks every tool this BEFORE it closes the current game — on every ROM load, on
    /// switching to no ROM, and on File → Exit — and a false return aborts the operation.
    ///
    /// This is the hook <see cref="Restart"/> cannot be: Restart runs AFTER the load completed,
    /// when the old core is already disposed, so a teardown from there is feeling around a corpse
    /// (see the pre-join note below). Here the old core is still alive, the session can be ended
    /// against the emulator it belongs to, and — the part no other hook offers — the user gets to
    /// cancel a ROM load they didn't realise would disconnect every other player.
    ///
    /// Config → "Supress 'Ask Save Changes'" bypasses the prompt entirely (EmuHawk answers yes for
    /// the user), so Restart stays as the backstop and must survive running second.
    /// </summary>
    public override bool AskSaveChanges()
    {
        if (SessionMachineryIdle) return true;
        var choice = MessageBox.Show(this,
            _phase.IsActive
                ? "A netplay session is running. Loading a ROM or closing EmuHawk disconnects every " +
                  "other player.\n\nDisconnect and continue?"
                : "A netplay connection is being set up. Loading a ROM or closing EmuHawk cancels " +
                  "it.\n\nCancel it and continue?",
            "BizHawk Netplay", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes) return false;
        EndSession("ROM change or shutdown requested");
        return true;
    }

    public override void Restart()
    {
        // ROM load / tool re-init: also tear down a lobby, join, or state transfer. Those phases
        // have already paused the emulator and captured an adapter for the old core even though
        // _phase.IsActive is still false.
        //
        // By the time this runs the OLD core is disposed and the injected services point at the
        // NEW one — AskSaveChanges above is where the graceful teardown happened. Anything that
        // would touch the old core from here is dropped first: the pre-join state belongs to a
        // dead emulator, and restoring it into the new one would be wrong even where it didn't
        // crash (on a waterbox core, LoadStateBinary into a disposed host is a native access
        // violation, which no catch on this side survives).
        _preJoinRestoreState = null;
        EndSession("emulator restarted");
        // The adapter holds the OLD core's IEmulator and IStatable in readonly fields, so it is
        // scrap the moment the ROM changes. It used to survive here, and the Diagnostics buttons
        // reuse it when present (`_adapter ?? new EmuHawkAdapter(...)`) — so a session, a
        // disconnect, a different ROM and then "Test Input" read a disposed core.
        _adapter = null;
        // Invalidate the cached probe depth — the core/ROM may have changed, and a stale (deeper)
        // measurement from a lighter core could wrongly grant rollback to a heavier one.
        _probeDepth = -1;
        RefreshPlayerLimit(); // the new core may expose a different number of controller ports
        UpdateEnabled();
    }

}
