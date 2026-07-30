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
            _netcodeCombo.Items.AddRange(new object[] { "Automatic", "Rollback", "Lockstep" });
            _netcodeCombo.SelectedIndex = 0; // Automatic: rollback if the core qualifies, else lockstep

            var inputSrcLabel = new Label { Text = "My controls:", AutoSize = true, Location = new Point(12, 142) };
            _inputSourceCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(90, 139), Width = 130 };
            _inputSourceCombo.Items.AddRange(new object[] { "Use P1 pad", "Use P2 pad", "Use P3 pad", "Use P4 pad", "Assigned port" });
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
            var connLogLabel = new Label { Text = "Connection status:", AutoSize = true, Location = new Point(12, 348) };
            _connLog = new RichTextBox
            {
                Location = new Point(12, 366), Size = new Size(544, 92),
                ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Vertical, TabStop = false, DetectUrls = false,
            };

            _netcodeLabel = new Label
            {
                Text = "Netcode in use: —", Location = new Point(12, 466), Width = 300, Height = 24,
                BorderStyle = BorderStyle.FixedSingle, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0), ForeColor = Color.DimGray,
            };

            page.Controls.AddRange(new Control[]
            {
                _hostRadio, _joinRadio, ipLabel, _ipBox, portLabel, _portBox, playersLabel, _playersBox, _playersHint,
                passwordLabel, _passwordBox, passwordHint, delayLabel, _delayBox, _autoDelayCheck,
                autoDelayMaxLabel, _autoDelayMaxBox,
                netcodeSelLabel, _netcodeCombo, inputSrcLabel, _inputSourceCombo, _upnpCheck,
                _goButton, _disconnectButton, _pubAddrButton, _applyLiveButton,
                connLogLabel, _connLog, _netcodeLabel, BuildPunchGroup(),
            });
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

            _punchGroup.Controls.AddRange(new Control[]
            {
                _punchInstructions, _punchButton, _myCodeLabel, _myCodeBox, _copyCodeButton,
                _peerCodeLabel, _peerCodeBox, _connectButton, _punchStatus,
            });
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
                Log(_sessionActive ? "will inject a fake desync at the next checksum (tests resync)"
                                   : "arm this during a session to test resync");
            };

            var simLatencyLabel = new Label { Text = "Sim latency ms:", AutoSize = true, Location = new Point(12, 132) };
            _simLatencyBox = new NumericUpDown { Minimum = 0, Maximum = 500, Increment = 10, Value = 0, Location = new Point(110, 130), Width = 60 };
            _simUnresponsiveCheck = new CheckBox { Text = "Simulate unresponsive (diag)", AutoSize = true, Location = new Point(12, 160) };
            _simUnresponsiveCheck.CheckedChanged += (_, __) =>
            {
                _simUnresponsive = _simUnresponsiveCheck.Checked;
                if (_sessionActive)
                    Log(_simUnresponsive
                        ? "simulating an unresponsive peer — we've stopped answering pings; the other side should drop us in ~3s"
                        : "resumed responding to pings");
            };

            page.Controls.AddRange(new Control[]
            {
                _probeButton, _testInputButton, _verboseCheck, _freezeInputCheck, _forceDesyncCheck,
                simLatencyLabel, _simLatencyBox, _simUnresponsiveCheck,
            });
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
            if (_sessionActive)
            {
                var me = new ListViewItem($"P{_localPort + 1} (you)");
                me.SubItems.Add(_isHost ? "this machine (host)" : "this machine");
                me.SubItems.Add("—");
                me.SubItems.Add("—");
                _playersList.Items.Add(me);

                lock (_pingLock)
                {
                    foreach (var link in _peers)
                    {
                        var item = new ListViewItem($"P{link.RemotePort + 1}");
                        item.SubItems.Add(link.UdpEndpoint?.ToString() ?? link.Label);
                        item.SubItems.Add(link.PingMs < 0 ? "…" : $"{link.PingMs + 2 * _simLatencyMs:F0} ms");
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
            page.Controls.Add(_log);
            return page;
        }

        public override void Restart()
        {
            // ROM load / tool re-init: also tear down a lobby, join, or state transfer. Those phases
            // have already paused the emulator and captured an adapter for the old core even though
            // _sessionActive is still false.
            EndSession("emulator restarted");
            // Invalidate the cached probe depth — the core/ROM may have changed, and a stale (deeper)
            // measurement from a lighter core could wrongly grant rollback to a heavier one.
            _probeDepth = -1;
            RefreshPlayerLimit(); // the new core may expose a different number of controller ports
            UpdateEnabled();
        }

    }
}
