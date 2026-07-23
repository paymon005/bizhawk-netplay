using System;
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
    /// <summary>
    /// M1 — 2-player lockstep netplay. Host or join over direct IP, runs the handshake +
    /// initial-state transfer on a background thread, then drives the session by owning the frame
    /// clock: EmuHawk is paused and a UI-thread timer advances exactly one confirmed frame per tick
    /// via <c>DoFrameAdvance</c>. A stall just skips the tick — no reliance on EmuHawk's own frame
    /// loop (which pausing would silence). Desync is checked by trading main-memory hashes over the
    /// reliable control channel.
    /// </summary>
    [ExternalTool("BizHawk Netplay",
        Description = "2-player lockstep netplay over direct IP.")]
    public sealed class NetplayToolForm : ToolFormBase, IExternalToolForm
    {
        private const int Protocol = 1;
        private const int DefaultPort = 47800;
        private const int ChecksumInterval = 60; // frames between desync-detection samples

        public ApiContainer? _apiContainer { get; set; }
        private ApiContainer APIs => _apiContainer!;

        [RequiredService] public IEmulator? _emulator { get; set; }
        [OptionalService] public IStatable? _statable { get; set; }

        // --- UI ---
        private readonly RadioButton _hostRadio;
        private readonly RadioButton _joinRadio;
        private readonly TextBox _ipBox;
        private readonly NumericUpDown _portBox;
        private readonly NumericUpDown _delayBox;
        private readonly Button _goButton;
        private readonly Button _disconnectButton;
        private readonly Button _probeButton;
        private readonly Button _testInputButton;
        private readonly CheckBox _verboseCheck;
        private readonly CheckBox _freezeInputCheck;
        private readonly Label _status;

        private bool Verbose => _verboseCheck.Checked;

        private int _startEmuFrame; // emulator FrameCount at session start, for drift detection
        private readonly TextBox _log;

        // --- Session state (all touched on the UI thread except where noted) ---
        private EmuHawkAdapter? _adapter;
        private UdpTransport? _udp;
        private TcpListener? _listener;
        private TcpClient? _tcp;
        private ControlChannel? _control;
        private FrameDriver? _driver;
        private Thread? _controlReader;
        private readonly System.Windows.Forms.Timer _frameTimer;
        private volatile bool _sessionActive;
        private int _stallLog;     // throttles verbose stall messages

        private readonly object _hashLock = new object();
        private readonly Dictionary<int, uint> _localHashes = new Dictionary<int, uint>();
        private readonly Dictionary<int, uint> _remoteHashes = new Dictionary<int, uint>();

        // Saved EmuHawk config we override for the session's duration (keep running while unfocused).
        private Config? _config;
        private bool _prevRunInBackground;
        private bool _prevAcceptBackgroundInput;
        private bool _configApplied;

        protected override string WindowTitleStatic => "BizHawk Netplay";

        public NetplayToolForm()
        {
            SuspendLayout();
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(460, 320);

            _hostRadio = new RadioButton { Text = "Host", Checked = true, AutoSize = true, Location = new Point(12, 12) };
            _joinRadio = new RadioButton { Text = "Join", AutoSize = true, Location = new Point(80, 12) };
            _hostRadio.CheckedChanged += (_, __) => UpdateEnabled();

            var ipLabel = new Label { Text = "Host IP:", AutoSize = true, Location = new Point(12, 44) };
            _ipBox = new TextBox { Text = "127.0.0.1", Location = new Point(80, 41), Width = 160 };
            var portLabel = new Label { Text = "Port:", AutoSize = true, Location = new Point(260, 44) };
            _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = DefaultPort, Location = new Point(300, 41), Width = 70 };
            var delayLabel = new Label { Text = "Input delay:", AutoSize = true, Location = new Point(12, 76) };
            _delayBox = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 2, Location = new Point(90, 73), Width = 50 };

            _goButton = new Button { Text = "Start Hosting", Location = new Point(12, 108), Width = 130 };
            _goButton.Click += (_, __) => OnGo();
            _disconnectButton = new Button { Text = "Disconnect", Location = new Point(150, 108), Width = 110, Enabled = false };
            _disconnectButton.Click += (_, __) => EndSession("disconnected by user");

            _probeButton = new Button { Text = "Capability Probe", Location = new Point(268, 108), Width = 130 };
            _probeButton.Click += (_, __) => RunProbe();

            _testInputButton = new Button { Text = "Test Input", Location = new Point(12, 140), Width = 130 };
            _testInputButton.Click += (_, __) => RunInputTest();

            _verboseCheck = new CheckBox { Text = "Verbose log", AutoSize = true, Location = new Point(410, 100) };
            _freezeInputCheck = new CheckBox { Text = "Freeze input (diag)", AutoSize = true, Location = new Point(410, 122) };
            _freezeInputCheck.CheckedChanged += (_, __) =>
                EmuHawkAdapter.ForceNeutralInput = _freezeInputCheck.Checked;

            _status = new Label { Text = "Idle.", AutoSize = true, Location = new Point(150, 145), ForeColor = Color.DimGray };

            _log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Location = new Point(12, 170), Size = new Size(536, 238),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font(FontFamily.GenericMonospace, 9f),
            };

            Controls.AddRange(new Control[]
            {
                _hostRadio, _joinRadio, ipLabel, _ipBox, portLabel, _portBox,
                delayLabel, _delayBox, _goButton, _disconnectButton, _probeButton, _testInputButton,
                _verboseCheck, _freezeInputCheck, _status, _log,
            });
            ResumeLayout(false);

            _frameTimer = new System.Windows.Forms.Timer();
            _frameTimer.Tick += (_, __) => FrameTick();

            UpdateEnabled();
        }

        public override void Restart()
        {
            // ROM load / tool re-init: tear down any live session.
            if (_sessionActive) EndSession("emulator restarted");
            UpdateEnabled();
        }

        // ------------------------------------------------------------------ start

        private void OnGo()
        {
            if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
            if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }

            try
            {
                _adapter = new EmuHawkAdapter(APIs, _emulator, _statable);
                if (!_adapter.VerifyDeterministicMode())
                    Log("WARNING: core does not report deterministic emulation — desyncs are likely.");
                if (!_adapter.HasBindings)
                    Log($"WARNING: input may not register — {_adapter.BindingDiagnostic}");

                var id = BuildIdentity(_adapter);
                var prefs = new SessionPreferences((int)_delayBox.Value, wantRollback: false); // rollback is M3
                int port = (int)_portBox.Value;

                // Freeze the emulator NOW. Otherwise the host keeps free-running between exporting
                // its state and the joiner arriving, so the two sims start on different frames and
                // desync immediately. Paused here == the frame both peers resume from.
                APIs.EmuClient.Pause();

                SetBusy(true);
                if (_hostRadio.Checked)
                {
                    _udp = UdpTransport.Bind(port);
                    var state = _adapter.ExportState();
                    Log($"exported {state.Length / 1024}KiB initial state (frozen until a player joins)");
                    StartThread(() => HostThread(port, id, prefs, state, _udp.LocalPort));
                }
                else
                {
                    if (!IPAddress.TryParse(_ipBox.Text.Trim(), out var _))
                    { Log("Enter a valid host IP."); SetBusy(false); return; }
                    _udp = UdpTransport.Bind(0);
                    string ip = _ipBox.Text.Trim();
                    StartThread(() => JoinThread(ip, port, id, prefs, _udp.LocalPort));
                }
            }
            catch (Exception ex)
            {
                Log("start failed: " + ex.Message);
                SetBusy(false);
            }
        }

        private void HostThread(int port, PeerIdentity id, SessionPreferences prefs, byte[] state, int udpLocalPort)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                UiLog($"hosting on TCP+UDP {port} — waiting for a player to join…");
                var tcp = _listener.AcceptTcpClient();
                try { _listener.Stop(); } catch { }
                _listener = null;

                var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                var channel = new ControlChannel(tcp.GetStream());
                var sp = Handshake.RunHost(channel, id, prefs, state, udpLocalPort);
                _tcp = tcp; _control = channel;
                BeginInvokeUi(() => BeginSession(sp, remoteIp));
            }
            catch (Exception ex) { BeginInvokeUi(() => FailSession(ex.Message)); }
        }

        private void JoinThread(string ip, int port, PeerIdentity id, SessionPreferences prefs, int udpLocalPort)
        {
            try
            {
                UiLog($"connecting to {ip}:{port}…");
                var tcp = new TcpClient();
                tcp.Connect(ip, port);
                var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                var channel = new ControlChannel(tcp.GetStream());
                var sp = Handshake.RunClient(channel, id, prefs, udpLocalPort);
                _tcp = tcp; _control = channel;
                BeginInvokeUi(() => BeginSession(sp, remoteIp));
            }
            catch (Exception ex) { BeginInvokeUi(() => FailSession(ex.Message)); }
        }

        // ------------------------------------------------------------------ session

        private void BeginSession(SessionParams sp, IPAddress remoteIp)
        {
            try
            {
                if (sp.InitialState != null)
                {
                    _adapter!.ImportState(sp.InitialState);
                    Log($"imported {sp.InitialState.Length / 1024}KiB host state");
                }
                // Both peers should print the SAME number here; if not, the start is misaligned.
                Log($"emulator frame at start: {APIs.Emulation.FrameCount()}");

                _udp!.SetRemote(new IPEndPoint(remoteIp, sp.RemoteUdpPort));
                _driver = new FrameDriver(_adapter!, _udp, p => new LockstepStrategy(p),
                    sp.LocalPort, sp.InputDelay, redundancy: 8);

                ApplyBackgroundConfig(true); // don't let EmuHawk pause/ignore input when unfocused
                try { APIs.EmuClient.EnableRewind(false); } catch { } // rewind would jump the frame count -> desync
                APIs.EmuClient.Pause(); // we own the clock now
                _startEmuFrame = APIs.Emulation.FrameCount(); // baseline for frame-advance drift checks
                _driver.Start();
                _sessionActive = true;

                _controlReader = new Thread(ControlReaderLoop) { IsBackground = true, Name = "BizHawkNetplay-control" };
                _controlReader.Start();

                _frameTimer.Interval = Math.Max(1, (int)Math.Round(FrameMs()));
                _frameTimer.Start();

                Status($"in session — {(sp.Mode == SyncMode.Rollback ? "rollback" : "lockstep")}, " +
                       $"you are P{sp.LocalPort + 1}, delay {sp.InputDelay}", Color.Green);
                Log($"session started vs {remoteIp}:{sp.RemoteUdpPort}");
                _disconnectButton.Enabled = true;
            }
            catch (Exception ex) { FailSession(ex.Message); }
        }

        private void FrameTick()
        {
            if (!_sessionActive || _driver == null) return;
            try
            {
                // Sticky pause: we own the frame clock. If the user (or anything) unpauses EmuHawk,
                // its own loop would advance the core on top of ours and desync — snap it back.
                if (!APIs.EmuClient.IsPaused())
                {
                    APIs.EmuClient.Pause();
                    if (Verbose) Log("re-paused (the session owns the frame clock — don't unpause)");
                }

                // If EmuHawk's own loop slipped in extra core frames (e.g. a brief unpause), our
                // counter and the core have diverged — report it plainly rather than as a desync.
                int emuDelta = APIs.Emulation.FrameCount() - _startEmuFrame;
                if (emuDelta != _driver.CurrentFrame)
                {
                    int diff = emuDelta - _driver.CurrentFrame;
                    string why = diff > 0
                        ? $"EmuHawk advanced {diff} extra frame(s) — did you unpause?"
                        : $"the core's frame count jumped back {-diff} — a rewind/load-state hotkey fired?";
                    EndSession(why + " The tool must own the frame clock; avoid EmuHawk hotkeys during a session.");
                    return;
                }

                _driver.PumpNetwork();       // drain remote input + resend our redundant window
                _driver.CaptureLocalInput(); // capture local pad (paused-safe, via IInputApi) + send

                if (_driver.CurrentFrameReady())
                {
                    // Step the core with ONLY our merged inputs — deterministic, bypasses EmuHawk's
                    // input chain and hotkeys. Gated: we advance only when the frame is confirmed.
                    _adapter!.AdvanceFrame(_driver.CurrentInputs());
                    _driver.CompleteFrame();
                    MaybeSendChecksum();
                    if (_driver.CurrentFrame % 120 == 0)
                        Status($"in session — frame {_driver.CurrentFrame}", Color.Green);
                }
                else if (Verbose && (++_stallLog % 30 == 0))
                {
                    Log($"stalling at frame {_driver.CurrentFrame} — waiting for remote input");
                }
            }
            catch (Exception ex) { EndSession("session error: " + ex.Message); }
        }

        private void MaybeSendChecksum()
        {
            int frame = _driver!.CurrentFrame;
            if (frame % ChecksumInterval != 0) return;
            uint hash = _adapter!.HashMainMemory();
            lock (_hashLock) { _localHashes[frame] = hash; }
            try { _control!.Send(ControlMessageType.Checksum, EncodeChecksum(frame, hash)); }
            catch { /* control channel gone; the reader loop will end the session */ }
            if (Verbose)
            {
                int emuDelta = APIs.Emulation.FrameCount() - _startEmuFrame;
                // emuΔ should always equal frame; if not, DoFrameAdvance stepped the core an unequal
                // number of times on this side — the smoking gun for a frame-advance drift desync.
                string drift = emuDelta == frame ? "" : $"  !! emuΔ={emuDelta} (expected {frame})";
                Log($"checksum frame {frame}: local {hash:X8}{drift}");
            }
            CompareChecksum(frame);
        }

        private void ControlReaderLoop()
        {
            try
            {
                while (_sessionActive)
                {
                    var (type, body) = _control!.Receive();
                    if (type == ControlMessageType.Checksum && body.Length == 8)
                    {
                        DecodeChecksum(body, out int frame, out uint hash);
                        lock (_hashLock) { _remoteHashes[frame] = hash; }
                        CompareChecksum(frame);
                    }
                    else if (type == ControlMessageType.Bye)
                    {
                        BeginInvokeUi(() => EndSession("peer left the session"));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_sessionActive) BeginInvokeUi(() => EndSession("control channel lost: " + ex.Message));
            }
        }

        private void CompareChecksum(int frame)
        {
            uint local, remote;
            lock (_hashLock)
            {
                if (!_localHashes.TryGetValue(frame, out local)) return;
                if (!_remoteHashes.TryGetValue(frame, out remote)) return;
            }
            if (local != remote)
                BeginInvokeUi(() => EndSession($"DESYNC detected at frame {frame} " +
                                               $"(local {local:X8} != remote {remote:X8})"));
            else if (Verbose)
                BeginInvokeUi(() => Log($"checksum frame {frame}: MATCH ({local:X8})"));
        }

        private void FailSession(string reason)
        {
            Log("connection failed: " + reason);
            TeardownNetwork();
            ApplyBackgroundConfig(false);
            try { APIs.EmuClient.Unpause(); } catch { } // undo the freeze from OnGo
            SetBusy(false);
            Status("Idle.", Color.DimGray);
        }

        private void EndSession(string reason)
        {
            if (!_sessionActive && _listener == null && _tcp == null) { SetBusy(false); return; }
            _sessionActive = false;
            _frameTimer.Stop();

            try { _control?.Send(ControlMessageType.Bye, Array.Empty<byte>()); } catch { }
            TeardownNetwork();

            ApplyBackgroundConfig(false); // restore the user's focus/pause preferences
            try { APIs.EmuClient.Unpause(); } catch { }
            lock (_hashLock) { _localHashes.Clear(); _remoteHashes.Clear(); }

            Log("session ended: " + reason);
            Status("Idle.", Color.DimGray);
            SetBusy(false);
        }

        private void TeardownNetwork()
        {
            try { _listener?.Stop(); } catch { }
            _listener = null;
            try { _tcp?.Close(); } catch { }
            _tcp = null;
            _control = null;
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            _driver = null;
            var reader = _controlReader;
            _controlReader = null;
            if (reader != null && reader.IsAlive && reader != Thread.CurrentThread)
            {
                try { reader.Join(300); } catch { }
            }
        }

        // ------------------------------------------------------------------ probe

        /// <summary>
        /// M0 capability probe, folded in as a diagnostic: times save/load/frame-advance on the
        /// loaded core and reports whether it qualifies for rollback (§5). Saves and restores the
        /// current position so it doesn't disturb play. Only runs when idle.
        /// </summary>
        private void RunProbe()
        {
            if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
            if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }
            if (_sessionActive) { Log("Can't probe during a session."); return; }

            Log("=== capability probe ===");
            string? restore = null;
            try
            {
                var adapter = new EmuHawkAdapter(APIs, _emulator, _statable);
                restore = APIs.MemorySaveState.SaveCoreStateToMemory();
                double budget = FrameMs();
                var probe = new CapabilityProbe(adapter, new StopwatchClock(), samples: 100);
                var result = probe.Run(budget, budget * 0.25);
                Log(result.ToString());
            }
            catch (Exception ex) { Log("probe failed: " + ex.Message); }
            finally
            {
                if (restore != null)
                {
                    try { APIs.MemorySaveState.LoadCoreStateFromMemory(restore); APIs.MemorySaveState.DeleteState(restore); }
                    catch (Exception ex) { Log("(warning) could not restore pre-probe state: " + ex.Message); }
                }
                Log("=== done ===");
            }
        }

        /// <summary>
        /// Dumps what BizHawk sees for input: the controller/binding keys we resolve against, and
        /// the host inputs pressed right now vs how they map to P1. Hold a button and click.
        /// </summary>
        private void RunInputTest()
        {
            if (_emulator == null || _apiContainer == null) { Log("No core loaded."); return; }
            if (_statable == null) { Log("This core has no savestate support — unsupported for netplay."); return; }
            try
            {
                var adapter = _adapter ?? new EmuHawkAdapter(APIs, _emulator, _statable);
                Log("=== input test (hold a button while clicking) ===");
                Log(adapter.DescribeInputState());
            }
            catch (Exception ex) { Log("input test failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ helpers

        private PeerIdentity BuildIdentity(EmuHawkAdapter a)
        {
            var layouts = new string[a.PortCount];
            for (int p = 0; p < layouts.Length; p++)
                layouts[p] = a.GetControllerLayout(p).Digest;
            // Depth 0 for M1: only LockstepStrategy exists yet, so rollback is never negotiated.
            return new PeerIdentity(Protocol, a.RomHash, a.CoreName, a.CoreVersion,
                a.SyncSettingsDigest, layouts, a.VerifyDeterministicMode(), maxRollbackDepth: 0);
        }

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
                    _config.RunInBackground = true;
                    _config.AcceptBackgroundInput = true;
                    _configApplied = true;
                    Log("run-in-background enabled for this session (unfocused window keeps running)");
                }
                else if (_configApplied && _config != null)
                {
                    _config.RunInBackground = _prevRunInBackground;
                    _config.AcceptBackgroundInput = _prevAcceptBackgroundInput;
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

        private static byte[] EncodeChecksum(int frame, uint hash)
        {
            var b = new byte[8];
            b[0] = (byte)(frame >> 24); b[1] = (byte)(frame >> 16); b[2] = (byte)(frame >> 8); b[3] = (byte)frame;
            b[4] = (byte)(hash >> 24); b[5] = (byte)(hash >> 16); b[6] = (byte)(hash >> 8); b[7] = (byte)hash;
            return b;
        }

        private static void DecodeChecksum(byte[] b, out int frame, out uint hash)
        {
            frame = (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
            hash = ((uint)b[4] << 24) | ((uint)b[5] << 16) | ((uint)b[6] << 8) | b[7];
        }

        private void StartThread(Action body) =>
            new Thread(() => body()) { IsBackground = true, Name = "BizHawkNetplay-connect" }.Start();

        private void UpdateEnabled()
        {
            bool host = _hostRadio.Checked;
            _ipBox.Enabled = !host;
            _goButton.Text = host ? "Start Hosting" : "Join";
        }

        private void SetBusy(bool busy)
        {
            _goButton.Enabled = !busy;
            _hostRadio.Enabled = _joinRadio.Enabled = !busy;
            _ipBox.Enabled = !busy && _joinRadio.Checked;
            _portBox.Enabled = _delayBox.Enabled = !busy;
            _probeButton.Enabled = !busy;
            _disconnectButton.Enabled = busy;
            // _testInputButton stays enabled (useful to check bindings before and during a session)
        }

        private void Status(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }

        private void Log(string message)
        {
            if (_log.IsDisposed) return;
            _log.AppendText(message + Environment.NewLine);
        }

        private void UiLog(string message) => BeginInvokeUi(() => Log(message));

        private void BeginInvokeUi(Action action)
        {
            if (IsDisposed) return;
            try { BeginInvoke(action); } catch { /* form closing */ }
        }
    }
}
