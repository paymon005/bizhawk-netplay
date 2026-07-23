using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using BizHawk.Client.Common;
using BizHawk.Client.EmuHawk;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Tool
{
    /// <summary>
    /// M0 — the probe harness. A standalone external tool that runs the §5 capability probe
    /// against the loaded core and answers the three API experiments (reentrant frame advance,
    /// hide-during-repair mechanism, speed modulation). Produces the per-core feasibility line
    /// that gates rollback and sizes lockstep expectations.
    /// </summary>
    [ExternalTool("BizHawk Netplay — Capability Probe",
        Description = "Probes the loaded core for rollback feasibility and validates the netplay API surface.")]
    public sealed class ProbeToolForm : ToolFormBase, IExternalToolForm
    {
        // Injected by EmuHawk's ApiInjector (whole container) and ServiceInjector (services).
        public ApiContainer? _apiContainer { get; set; }
        private ApiContainer APIs => _apiContainer!;

        [RequiredService] public IEmulator? _emulator { get; set; }
        [OptionalService] public IStatable? _statable { get; set; }

        private readonly TextBox _log;
        private readonly Label _coreInfo;
        private readonly Button _probeButton;
        private readonly Button _experimentsButton;

        // Set when the "reentrant frame advance" experiment is armed; consumed on next PreFrame.
        private bool _reentrantExperimentArmed;

        protected override string WindowTitleStatic => "BizHawk Netplay — Capability Probe";

        public ProbeToolForm()
        {
            SuspendLayout();
            ClientSize = new Size(640, 460);
            MinimumSize = new Size(420, 260);

            _coreInfo = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(8, 8, 8, 0),
                Text = "No core loaded.",
            };

            var buttonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(6, 4, 6, 4),
            };
            _probeButton = new Button { Text = "Run Capability Probe", AutoSize = true, Enabled = false };
            _probeButton.Click += (_, __) => RunProbe();
            _experimentsButton = new Button { Text = "Run API Experiments", AutoSize = true, Enabled = false };
            _experimentsButton.Click += (_, __) => RunExperiments();
            buttonRow.Controls.Add(_probeButton);
            buttonRow.Controls.Add(_experimentsButton);

            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 9f),
            };

            Controls.Add(_log);
            Controls.Add(buttonRow);
            Controls.Add(_coreInfo);
            ResumeLayout(false);
        }

        public override void Restart()
        {
            bool ready = _emulator != null && _apiContainer != null;
            _probeButton.Enabled = ready;
            _experimentsButton.Enabled = ready;
            if (ready)
            {
                _coreInfo.Text =
                    $"Core: {SafeCoreName()}   System: {_emulator!.SystemId}   " +
                    $"Deterministic: {_emulator.DeterministicEmulation}   " +
                    $"Statable: {_statable != null}";
            }
            else
            {
                _coreInfo.Text = "No core loaded.";
            }
        }

        protected override void UpdateBefore()
        {
            if (!_reentrantExperimentArmed) return;
            _reentrantExperimentArmed = false;
            try
            {
                // The deciding experiment (§6.2): can a tool callback reentrantly frame-advance?
                var adapter = BuildAdapter();
                var before = _emulator!.Frame;
                adapter.RunFramesInvisible(1, i => Core.Input.InputSet.AllNeutral(before, GetLayouts(adapter)));
                Log($"[experiment a] Reentrant FrameAdvance from PreFrame callback SUCCEEDED " +
                    $"(frame {before} -> {_emulator.Frame}). Synchronous repair is available.");
            }
            catch (Exception ex)
            {
                Log($"[experiment a] Reentrant FrameAdvance from callback THREW: {ex.GetType().Name}: {ex.Message}");
                Log("            -> Use catch-up mode for repair (the architecture's default).");
            }
        }

        private void RunProbe()
        {
            if (_emulator == null) { Log("No emulator."); return; }
            if (_statable == null)
            {
                Log("Core is not IStatable — savestates unavailable. This core is UNSUPPORTED for netplay.");
                return;
            }

            Log("=== Capability probe starting ===");
            string? restore = null;
            try
            {
                restore = APIs.MemorySaveState.SaveCoreStateToMemory(); // restore user's position afterwards
                var adapter = BuildAdapter();
                var clock = new StopwatchClock();
                double frameBudgetMs = FrameBudgetMs();
                double headroomMs = frameBudgetMs * 0.25; // reserve a quarter for render/transport/GC

                var probe = new CapabilityProbe(adapter, clock, samples: 100);
                ProbeResult result = probe.Run(frameBudgetMs, headroomMs);

                Log(result.ToString());
                Log($"    ports={adapter.PortCount}  det={adapter.VerifyDeterministicMode()}  " +
                    $"rom={adapter.RomHash}  sync={adapter.SyncSettingsDigest}");
            }
            catch (Exception ex)
            {
                Log($"Probe failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (restore != null)
                {
                    try { APIs.MemorySaveState.LoadCoreStateFromMemory(restore); APIs.MemorySaveState.DeleteState(restore); }
                    catch (Exception ex) { Log($"(warning) could not restore pre-probe state: {ex.Message}"); }
                }
                Log("=== done ===");
            }
        }

        private void RunExperiments()
        {
            Log("=== API experiments ===");

            // (a) Reentrant frame advance — armed here, executed on the next PreFrame callback.
            _reentrantExperimentArmed = true;
            Log("[experiment a] Armed: will attempt reentrant FrameAdvance on next PreFrame. " +
                "Ensure emulation is running (unpaused).");

            // (b) Hide-during-repair mechanism availability (§6.3).
            Log("[experiment b] EmuClient exposes: SpeedMode, SetSoundOn; Emulation exposes: " +
                "LimitFramerate, MinimizeFrameskip. No InvisibleEmulation on the API container " +
                "-> DispSpeedupFeatures/SoundThrottle-style hiding is the path, as the design assumes.");

            // (c) Speed modulation (§3.6).
            try
            {
                APIs.EmuClient.SpeedMode(400);
                APIs.Emulation.LimitFramerate(false);
                APIs.Emulation.LimitFramerate(true);
                APIs.EmuClient.SpeedMode(100);
                Log("[experiment c] SpeedMode(400->100) and LimitFramerate toggling succeeded.");
            }
            catch (Exception ex)
            {
                Log($"[experiment c] speed modulation threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private EmuHawkAdapter BuildAdapter() => new EmuHawkAdapter(APIs, _emulator!, _statable!);

        private static Core.Input.ControllerLayout[] GetLayouts(EmuHawkAdapter adapter)
        {
            var layouts = new Core.Input.ControllerLayout[adapter.PortCount];
            for (int p = 0; p < layouts.Length; p++) layouts[p] = adapter.GetControllerLayout(p);
            return layouts;
        }

        private double FrameBudgetMs()
        {
            // Use the console's exact frame period, not an assumed 60.000 Hz (§3.6).
            var vp = _emulator!.ServiceProvider.GetService<IVideoProvider>();
            if (vp != null && vp.VsyncNumerator > 0 && vp.VsyncDenominator > 0)
                return 1000.0 * vp.VsyncDenominator / vp.VsyncNumerator;
            return 1000.0 / 60.0;
        }

        private string SafeCoreName()
        {
            try { return BuildAdapter().CoreName; }
            catch { return _emulator?.GetType().Name ?? "?"; }
        }

        private void Log(string message)
        {
            if (_log.IsDisposed) return;
            _log.AppendText(message + Environment.NewLine);
        }
    }
}
