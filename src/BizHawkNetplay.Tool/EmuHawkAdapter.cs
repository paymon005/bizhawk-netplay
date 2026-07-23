using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BizHawk.Client.Common;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;
using CoreLayout = BizHawkNetplay.Core.Input.ControllerLayout;
using CoreAxisSpec = BizHawkNetplay.Core.Input.AxisSpec;

namespace BizHawkNetplay.Tool
{
    /// <summary>
    /// The one class that knows BizHawk. Bridges <see cref="IEmuAdapter"/> onto the injected
    /// ApiHawk container and emulator services of the running EmuHawk. Everything else in the
    /// system talks only to <see cref="IEmuAdapter"/>.
    /// </summary>
    internal sealed class EmuHawkAdapter : IEmuAdapter
    {
        private readonly ApiContainer _apis;
        private readonly IEmulator _emulator;
        private readonly IStatable _statable;
        private readonly CoreLayout[] _layouts;
        private readonly string[][] _bindings; // [port][buttonIndex] -> host-input binding string

        // Audio: we drive EmuHawk's Sound output ourselves (see EnableAudio / AdvanceFrame / PumpAudio).
        private BizHawk.Client.EmuHawk.Sound? _sound;
        private BizHawk.Client.EmuHawk.MainForm? _mainForm;
        private ISoundProvider? _coreSound;
        private NetplaySoundBuffer? _soundBuffer;
        private int _soundChannels = 2;
        private bool _audioReady;

        public EmuHawkAdapter(ApiContainer apis, IEmulator emulator, IStatable statable)
        {
            _apis = apis ?? throw new ArgumentNullException(nameof(apis));
            _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
            _statable = statable ?? throw new ArgumentNullException(nameof(statable));
            _layouts = BuildLayouts(emulator.ControllerDefinition);
            _bindings = BuildBindings();
        }

        /// <summary>True if we found the user's controller bindings (needed to capture input).</summary>
        public bool HasBindings
        {
            get
            {
                foreach (var port in _bindings)
                    foreach (var b in port)
                        if (!string.IsNullOrEmpty(b)) return true;
                return false;
            }
        }

        // --- Identity & determinism ---------------------------------------------------

        public string RomHash => _apis.Emulation.GetGameInfo()?.Hash ?? "unknown";

        public string CoreName
        {
            get
            {
                // Prefer the core's [Core] attribute name; fall back to the emulator type name.
                var attr = _emulator.GetType()
                    .GetCustomAttributes(true)
                    .FirstOrDefault(a => a.GetType().Name == "CoreAttribute");
                if (attr != null)
                {
                    var nameProp = attr.GetType().GetProperty("Name") ?? attr.GetType().GetProperty("CoreName");
                    if (nameProp?.GetValue(attr) is string s && s.Length > 0) return s;
                }
                return _emulator.GetType().Name;
            }
        }

        public string CoreVersion =>
            _emulator.GetType().Assembly.GetName().Version?.ToString() ?? "0";

        // NOTE (M1): replace with a hash of the core's real sync-settings blob obtained via the
        // settable-service interface. For M0 identity is core+version+system, enough to catch
        // gross mismatches at handshake.
        public string SyncSettingsDigest
        {
            get
            {
                using var sha = SHA256.Create();
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    CoreName + "|" + CoreVersion + "|" + _emulator.SystemId));
                return BitConverter.ToString(bytes, 0, 8).Replace("-", string.Empty);
            }
        }

        public bool VerifyDeterministicMode() => _emulator.DeterministicEmulation;

        // --- Input --------------------------------------------------------------------

        public int PortCount => _layouts.Length;

        public CoreLayout GetControllerLayout(int port) => _layouts[port];

        /// <summary>
        /// DIAGNOSTIC: when true, ReadLocalInput returns neutral and never touches the pad. Proved
        /// the no-input session holds sync (isolating netcode/core from input). Left as a toggle
        /// for future debugging; normal play reads the real pad.
        /// </summary>
        public static bool ForceNeutralInput = false;

        public PortInput ReadLocalInput(int port)
        {
            var layout = _layouts[port];
            if (ForceNeutralInput)
                return PortInput.Neutral(layout);

            // Read raw host input directly (IInputApi.Get works while the emulator is paused and does
            // NOT run EmuHawk's controller/hotkey chain), then resolve to core buttons via the user's
            // bindings. Input capture thus stays entirely out of the emulation path — the core only
            // ever sees the merged inputs we feed in AdvanceFrame, so there is no physical leak, no
            // hotkey firing, and both peers stay deterministic.
            var pressed = new HashSet<string>(_apis.Input.GetPressedButtons());
            var binds = _bindings[port];
            var buttons = new bool[layout.Buttons.Count];
            for (int i = 0; i < buttons.Length; i++)
                buttons[i] = EvaluateBinding(binds[i], pressed);
            var axes = new int[layout.Axes.Count];
            for (int j = 0; j < axes.Length; j++)
                axes[j] = layout.Axes[j].Neutral; // analog capture is M2
            return new PortInput(buttons, axes);
        }

        // The real session injects inputs by stepping the core with a controller built from the
        // merged InputSet (AdvanceFrame), not via Joypad.Set — so this is unused.
        public void SetInputs(InputSet inputs) { }

        /// <summary>
        /// Step the core exactly one frame using <paramref name="inputs"/> as the ONLY input source
        /// (bypasses EmuHawk's input chain and hotkeys). Identical inputs on both peers therefore
        /// produce identical state — the proven-deterministic stepping path.
        /// </summary>
        public void AdvanceFrame(InputSet inputs)
        {
            var controller = new InputSetController(_emulator.ControllerDefinition, _layouts, inputs);
            _emulator.FrameAdvance(controller, render: true, renderSound: true);

            // Drain the samples the core just generated into our ring buffer. EmuHawk's audio device
            // pulls from that ring at the steady real-time rate (PumpAudio → UpdateSound), so the ring
            // absorbs the mismatch between this bursty manual stepping and smooth playback. Producing
            // here and consuming there is what keeps audio clean despite the coarse WinForms timer.
            if (_audioReady)
            {
                try
                {
                    _coreSound!.GetSamplesSync(out var samples, out var nSampPairs);
                    _soundBuffer!.Enqueue(samples, nSampPairs * _soundChannels);
                }
                catch { /* a sample-pull hiccup must never break emulation */ }
            }
        }

        /// <summary>
        /// Top up EmuHawk's audio device from our ring buffer. Call every frame-timer tick — whether
        /// or not a frame advanced — so the device stays fed at the real playback rate, decoupled from
        /// our (bursty, occasionally stalled) frame stepping. The async path in <c>Sound.UpdateSound</c>
        /// just moves buffered samples to the device: no resampling, no blocking.
        /// </summary>
        public void PumpAudio()
        {
            if (!_audioReady) return;
            // atten = 1 → full volume (EmuHawk applies the user's master volume at the device).
            try { _sound!.UpdateSound(1.0f, isSecondaryThrottlingDisabled: false); }
            catch { _audioReady = false; }
        }

        /// <summary>True once <see cref="EnableAudio"/> has wired up the sound output for the session.</summary>
        public bool AudioReady => _audioReady;

        /// <summary>Human-readable note on why audio was/wasn't wired up (for the UI log).</summary>
        public string AudioDiagnostic { get; private set; } = "";

        /// <summary>
        /// Wire up audio for a driven session. Because we keep EmuHawk paused and step the core
        /// ourselves, EmuHawk's main loop never pumps its sound output. We grab its Sound device (the
        /// private <c>MainForm.Sound</c>) and re-point its input pin at a ring buffer we own
        /// (<see cref="NetplaySoundBuffer"/>, in async mode → the jitter-tolerant buffered path), then
        /// feed that ring from the core each frame in <see cref="AdvanceFrame"/> and drain it to the
        /// device each tick in <see cref="PumpAudio"/>. <see cref="DisableAudio"/> restores EmuHawk's
        /// own wiring when the session ends.
        /// </summary>
        public void EnableAudio(BizHawk.Client.EmuHawk.MainForm? mainForm)
        {
            _audioReady = false;
            AudioDiagnostic = "";
            try
            {
                if (mainForm == null) { AudioDiagnostic = "no MainForm reference"; return; }
                _mainForm = mainForm;

                // MainForm.Sound is a public type but has a private getter, so reflect it once up front.
                var prop = typeof(BizHawk.Client.EmuHawk.MainForm)
                    .GetProperty("Sound", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _sound = prop?.GetValue(mainForm) as BizHawk.Client.EmuHawk.Sound;
                if (_sound == null) { AudioDiagnostic = "couldn't reach MainForm.Sound"; return; }

                _coreSound = _emulator.ServiceProvider.GetService<ISoundProvider>();
                if (_coreSound == null) { AudioDiagnostic = "core exposes no ISoundProvider"; return; }

                // We pull the core's samples synchronously right after each FrameAdvance.
                if (_coreSound.SyncMode != SyncSoundMode.Sync)
                {
                    try { _coreSound.SetSyncMode(SyncSoundMode.Sync); }
                    catch { AudioDiagnostic = "core sound is async-only (unsupported for driven audio)"; return; }
                }

                // Make sure the device is running before we touch its input pin (so a bail leaves
                // EmuHawk's wiring untouched). StartSound no-ops if the user disabled sound.
                if (!_sound.IsStarted) _sound.StartSound();
                if (!_sound.IsStarted) { AudioDiagnostic = "EmuHawk sound output is off (enable Config → Sound)"; return; }

                _soundChannels = _sound.ChannelCount;
                _soundBuffer = new NetplaySoundBuffer(_sound.SampleRate, _soundChannels, capacityMs: 200);
                // Prime a small standing cushion of silence so pump jitter doesn't underrun the ring.
                int prime = _sound.SampleRate * _soundChannels * 50 / 1000;
                _soundBuffer.Enqueue(new short[prime], prime);

                _sound.SetInputPin(_soundBuffer); // route the device to pull from our async ring
                _audioReady = true;
            }
            catch (Exception ex) { _sound = null; AudioDiagnostic = "audio init failed: " + ex.Message; }
        }

        /// <summary>
        /// Restore EmuHawk's own audio wiring so normal sound resumes after the session. Prefers
        /// EmuHawk's <c>RewireSound</c> (re-establishes the correct pin per core/config); falls back to
        /// re-pinning the core provider directly.
        /// </summary>
        public void DisableAudio()
        {
            _audioReady = false;
            if (_sound == null) return;
            try
            {
                _soundBuffer?.DiscardSamples();
                var rewire = typeof(BizHawk.Client.EmuHawk.MainForm)
                    .GetMethod("RewireSound", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (_mainForm != null && rewire != null) rewire.Invoke(_mainForm, null);
                else if (_coreSound != null) _sound.SetInputPin(_coreSound);
            }
            catch { try { if (_coreSound != null) _sound.SetInputPin(_coreSound); } catch { } }
        }

        /// <summary>
        /// True if the host-input expression <paramref name="binding"/> is satisfied by the pressed
        /// set. Handles comma-separated alternatives and '+'-separated combos (the common BizHawk
        /// forms); exotic modifiers may not resolve (M2).
        /// </summary>
        private static bool EvaluateBinding(string binding, HashSet<string> pressed)
        {
            if (string.IsNullOrEmpty(binding)) return false;
            foreach (var alt in binding.Split(','))
            {
                var one = alt.Trim();
                if (one.Length == 0) continue;
                bool all = true;
                foreach (var part in one.Split('+'))
                {
                    var key = part.Trim();
                    if (key.Length == 0) continue;
                    if (!pressed.Contains(key)) { all = false; break; }
                }
                if (all) return true;
            }
            return false;
        }

        /// <summary>Human-readable note on why bindings were/weren't found (for the UI log).</summary>
        public string BindingDiagnostic { get; private set; } = "";

        /// <summary>
        /// One-shot input diagnostic for the UI: what controller/bindings we found, and what host
        /// inputs are pressed right now vs how they resolve to P1 buttons.
        /// </summary>
        public string DescribeInputState()
        {
            var sb = new StringBuilder();
            sb.Append("controllerDef='").Append(_emulator.ControllerDefinition.Name)
              .Append("', system='").Append(_emulator.SystemId).Append("'\n");
            sb.Append("bindings: ").Append(HasBindings ? "FOUND" : "MISSING");
            if (!string.IsNullOrEmpty(BindingDiagnostic)) sb.Append("  ").Append(BindingDiagnostic);
            sb.Append('\n');

            if (_layouts.Length > 0)
            {
                var l0 = _layouts[0];
                for (int i = 0; i < l0.Buttons.Count && i < 8; i++)
                    sb.Append("  ").Append(l0.Buttons[i]).Append(" <- '").Append(_bindings[0][i]).Append("'\n");
            }

            IReadOnlyList<string> pressed;
            try { pressed = _apis.Input.GetPressedButtons(); }
            catch (Exception ex) { return sb.Append("GetPressedButtons error: ").Append(ex.Message).ToString(); }
            sb.Append("pressed host inputs now: [").Append(string.Join(", ", pressed)).Append("]\n");

            if (_layouts.Length > 0)
            {
                var set = new HashSet<string>(pressed);
                var l0 = _layouts[0];
                var on = new List<string>();
                for (int i = 0; i < l0.Buttons.Count; i++)
                    if (EvaluateBinding(_bindings[0][i], set)) on.Add(l0.Buttons[i]);
                sb.Append("resolved P1 pressed: [").Append(string.Join(", ", on)).Append("]");
            }
            return sb.ToString();
        }

        private string[][] BuildBindings()
        {
            Dictionary<string, string>? map = null;
            try
            {
                var config = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference;
                if (config != null)
                {
                    // Config.AllTrollers is keyed by the controller-definition name, not the system id.
                    var defName = _emulator.ControllerDefinition.Name;
                    if (!config.AllTrollers.TryGetValue(defName, out map))
                        config.AllTrollers.TryGetValue(_emulator.SystemId, out map);
                    if (map == null)
                        BindingDiagnostic = $"no bindings for '{defName}' or '{_emulator.SystemId}'. " +
                            $"available: [{string.Join(", ", config.AllTrollers.Keys)}]";
                }
                else BindingDiagnostic = "config reference unavailable";
            }
            catch (Exception ex) { BindingDiagnostic = "binding lookup error: " + ex.Message; }

            var result = new string[_layouts.Length][];
            for (int p = 0; p < _layouts.Length; p++)
            {
                var layout = _layouts[p];
                var arr = new string[layout.Buttons.Count];
                for (int i = 0; i < arr.Length; i++)
                    arr[i] = (map != null && map.TryGetValue(layout.Buttons[i], out var b)) ? b : "";
                result[p] = arr;
            }
            return result;
        }

        // --- State --------------------------------------------------------------------

        public StateHandle SaveStateToMemory() =>
            new StateHandle(_emulator.Frame, _apis.MemorySaveState.SaveCoreStateToMemory());

        public void LoadStateFromMemory(StateHandle handle) =>
            _apis.MemorySaveState.LoadCoreStateFromMemory((string)handle.Token);

        public byte[] ExportState()
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                _statable.SaveStateBinary(bw);
            return ms.ToArray();
        }

        public void ImportState(byte[] state)
        {
            using var ms = new MemoryStream(state);
            using var br = new BinaryReader(ms);
            _statable.LoadStateBinary(br);
        }

        // --- Frame control ------------------------------------------------------------

        public void SetPaused(bool paused)
        {
            if (paused) _apis.EmuClient.Pause();
            else _apis.EmuClient.Unpause();
        }

        public void SetAudioMuted(bool muted) => _apis.EmuClient.SetSoundOn(!muted);

        public void RunFramesInvisible(int count, Func<int, InputSet> inputsFor)
        {
            for (int i = 0; i < count; i++)
            {
                var controller = new InputSetController(_emulator.ControllerDefinition, _layouts, inputsFor(i));
                _emulator.FrameAdvance(controller, render: false, renderSound: false);
            }
        }

        // --- Integrity ----------------------------------------------------------------

        public uint HashMainMemory()
        {
            var name = _apis.Memory.MainMemoryName;
            var size = _apis.Memory.GetMemoryDomainSize(name);
            var hex = _apis.Memory.HashRegion(0, (int)size, name);
            // Fold the leading bytes of the SHA hex string into a cheap 32-bit rolling checksum.
            uint h = 2166136261;
            foreach (var c in hex) { h ^= c; h *= 16777619; }
            return h;
        }

        // --- Layout derivation --------------------------------------------------------

        private static CoreLayout[] BuildLayouts(ControllerDefinition def)
        {
            // Group the core's flat button/axis lists by player number into per-port layouts.
            int maxPlayer = 0;
            foreach (var b in def.BoolButtons)
                maxPlayer = Math.Max(maxPlayer, SafePlayerNumber(def, b));
            foreach (var axisName in def.Axes.Keys)
                maxPlayer = Math.Max(maxPlayer, SafePlayerNumber(def, axisName));
            if (maxPlayer < 1) maxPlayer = 1; // system-only cores still expose one nominal port

            var layouts = new CoreLayout[maxPlayer];
            for (int player = 1; player <= maxPlayer; player++)
            {
                var buttons = def.BoolButtons
                    .Where(b => SafePlayerNumber(def, b) == player)
                    .ToList();
                var axes = def.Axes.Keys
                    .Where(a => SafePlayerNumber(def, a) == player)
                    .Select(a =>
                    {
                        var spec = def.Axes[a];
                        return new CoreAxisSpec(a, spec.Min, spec.Max, spec.Neutral);
                    })
                    .ToList();
                layouts[player - 1] = new CoreLayout(buttons, axes);
            }
            return layouts;
        }

        private static int SafePlayerNumber(ControllerDefinition def, string control)
        {
            // PlayerNumber is static — it derives the player from the control-name prefix.
            try { return ControllerDefinition.PlayerNumber(control); }
            catch { return 0; }
        }
    }
}
