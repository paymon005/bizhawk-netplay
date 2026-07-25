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
        private readonly AnalogBind?[][] _analogBinds; // [port][axisIndex] -> host analog binding (null if unbound)
        private readonly bool[][] _axisReversed;      // [port][axisIndex] -> core axis IsReversed flag

        // Audio: we drive EmuHawk's Sound output ourselves (see EnableAudio / AdvanceFrame / PumpAudio).
        private BizHawk.Client.EmuHawk.Sound? _sound;
        private BizHawk.Client.EmuHawk.MainForm? _mainForm;
        private ISoundOutput? _outputDevice;             // EmuHawk's host audio device, driven directly
        private ISoundProvider? _coreSound;
        private NetplaySoundBuffer? _soundBuffer;
        private short[] _pumpScratch = Array.Empty<short>();
        private int _soundChannels = 2;
        private bool _audioReady;
        private bool _coreSyncSound = true;              // drain via GetSamplesSync (else GetSamplesAsync)
        private short[] _asyncScratch = Array.Empty<short>();
        // Diagnostics so a single test round shows where the audio pipeline breaks.
        private long _audioFrames, _audioPairs, _audioPumps;
        private int _audioPeak;               // max abs sample seen (0 => core handed us silence)
        private string _audioSyncErr = "";

        public EmuHawkAdapter(ApiContainer apis, IEmulator emulator, IStatable statable)
        {
            _apis = apis ?? throw new ArgumentNullException(nameof(apis));
            _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
            _statable = statable ?? throw new ArgumentNullException(nameof(statable));
            _layouts = BuildLayouts(emulator.ControllerDefinition);
            _bindings = BuildBindings();
            _analogBinds = BuildAnalogBinds();
            _axisReversed = BuildAxisReversed();
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

        /// <summary>BizHawk system identifier (for conservative per-system netplay defaults).</summary>
        public string SystemId => _emulator.SystemId;

        // Identity = core + version + system + the core's REAL sync-settings blob. The blob is what
        // makes two peers on the same core but different per-core settings (e.g. an N64 video plugin, a
        // region, a CPU-core choice) fail the handshake up front instead of silently desyncing later.
        // Both peers run the identical core build (the handshake already requires matching CoreVersion),
        // so the same settings serialize to the same JSON and hash equal — while any real difference
        // diverges. Best-effort: if the settings can't be read, the blob is empty and this degrades to
        // the old coarse core+version+system digest (no false mismatch).
        public string SyncSettingsDigest
        {
            get
            {
                using var sha = SHA256.Create();
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    CoreName + "|" + CoreVersion + "|" + _emulator.SystemId + "|" + SyncSettingsBlob()));
                return BitConverter.ToString(bytes, 0, 8).Replace("-", string.Empty);
            }
        }

        /// <summary>
        /// The core's live sync settings serialized to JSON, or "" if the core exposes none / it can't be
        /// read. Read straight from the core via <c>ISettable&lt;,&gt;.GetSyncSettings()</c> (the authoritative,
        /// both-peers-symmetric source) rather than the config dict, so a value the user just changed and a
        /// value loaded from disk can't serialize differently for the same logical settings.
        /// </summary>
        private string SyncSettingsBlob()
        {
            try
            {
                var settable = _emulator.GetType().GetInterfaces().FirstOrDefault(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition().FullName == "BizHawk.Emulation.Common.ISettable`2");
                var syncSettings = settable?.GetMethod("GetSyncSettings")?.Invoke(_emulator, null);
                if (syncSettings == null) return "";
                return Newtonsoft.Json.JsonConvert.SerializeObject(syncSettings);
            }
            catch { return ""; } // never let a settings read break the handshake — fall back to the coarse digest
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

        /// <summary>
        /// Which EmuHawk controller port's bindings to actually read the local pad from. -1 (default)
        /// reads the assigned port's own bindings. Set it to e.g. 0 so a player assigned P2/P3/P4 still
        /// uses their normal P1 controls with no rebinding — same-console ports share button order, so
        /// the source bindings map onto the assigned port's layout by index.
        /// </summary>
        public int InputSourcePort { get; set; } = -1;

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
            // Optionally source the pad from a different port's bindings, but only if that port's layout
            // has the same button set (guards mixed-layout cores); otherwise fall back to the assigned port.
            var binds = _bindings[port];
            int src = InputSourcePort;
            if (src >= 0 && src < _bindings.Length && _bindings[src].Length == layout.Buttons.Count)
                binds = _bindings[src];
            var buttons = new bool[layout.Buttons.Count];
            for (int i = 0; i < buttons.Length; i++)
                buttons[i] = EvaluateBinding(binds[i], pressed);

            var axes = new int[layout.Axes.Count];
            if (axes.Length > 0)
            {
                // Read live host analog axes — paused-safe via the same host coalescer GetPressedButtons
                // uses (values are in BizHawk's ±10000 convention) — and apply BizHawk's own analog-bind
                // math so the captured value matches exactly what local play would feed the core. Same
                // source-port remap as buttons, so a player assigned P2/P3/P4 uses their P1 stick.
                int axSrc = (src >= 0 && src < _analogBinds.Length && _analogBinds[src].Length == axes.Length) ? src : port;
                IReadOnlyDictionary<string, int> hostAxes;
                try { hostAxes = _apis.Input.GetPressedAxes(); }
                catch { hostAxes = null; }
                for (int j = 0; j < axes.Length; j++)
                    axes[j] = ResolveAxis(_analogBinds[axSrc][j], layout.Axes[j], _axisReversed[port][j], hostAxes, pressed);
            }
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
        public void AdvanceFrame(InputSet inputs, bool renderVideo = true)
        {
            // renderVideo=false skips only the video render — used for the throwaway intermediate frames
            // of a catch-up burst (their picture is never shown), which lets a heavy core recover from a
            // hitch faster. Sound is always rendered so audio stays continuous, and emulation is identical.
            var controller = new InputSetController(_emulator.ControllerDefinition, _layouts, inputs);
            _emulator.FrameAdvance(controller, render: renderVideo, renderSound: true);

            // Drain the samples the core just generated into our ring buffer. EmuHawk's audio device
            // pulls from that ring at the steady real-time rate (PumpAudio → UpdateSound), so the ring
            // absorbs the mismatch between this bursty manual stepping and smooth playback. Producing
            // here and consuming there is what keeps audio clean despite the coarse WinForms timer.
            // Drain in whichever mode the core actually reports — a sync-only drain of an async core
            // gets nothing (silence).
            if (_audioReady)
            {
                try
                {
                    if (_coreSyncSound)
                    {
                        _coreSound!.GetSamplesSync(out var samples, out var nSampPairs);
                        int shorts = nSampPairs * _soundChannels;
                        UpdatePeak(samples, shorts);
                        _soundBuffer!.Enqueue(samples, shorts);
                        _audioFrames++; _audioPairs += nSampPairs;
                    }
                    else
                    {
                        _coreSound!.GetSamplesAsync(_asyncScratch);
                        UpdatePeak(_asyncScratch, _asyncScratch.Length);
                        _soundBuffer!.Enqueue(_asyncScratch, _asyncScratch.Length);
                        _audioFrames++; _audioPairs += _asyncScratch.Length / Math.Max(1, _soundChannels);
                    }
                }
                catch (Exception ex) { _audioSyncErr = ex.Message; } // never break emulation over audio
            }
        }

        /// <summary>
        /// Top up the host audio device from our ring buffer. Call every frame-timer tick — whether or
        /// not a frame advanced — so the device stays fed at the real playback rate, decoupled from our
        /// (bursty, occasionally stalled) frame stepping.
        ///
        /// We write to the device DIRECTLY rather than via <c>Sound.UpdateSound</c>, because EmuHawk's
        /// own run loop also calls <c>Sound.UpdateSound</c> every iteration; while we hold it paused
        /// that call runs with atten=0, which discards our buffered samples and floods the device with
        /// silence far faster than we can pump. Nulling EmuHawk's input pin (see EnableAudio) makes its
        /// call early-return, leaving the device ours to feed here.
        /// </summary>
        public void PumpAudio()
        {
            if (!_audioReady) return;
            var dev = _outputDevice;
            var snd = _sound;
            if (dev == null || snd == null || _soundBuffer == null) return;

            // EmuHawk disposes and recreates its audio device across a window minimize/restore
            // (Sound.StopSound/StartSound). While it's stopped, skip — do NOT give up permanently, or
            // audio would stay dead until a reconnect. When EmuHawk restarts the device we resume.
            if (!snd.IsStarted) return;

            _audioPumps++;
            try
            {
                dev.ApplyVolumeSettings(1.0); // full volume; the user's master volume is baked into the samples' source
                int needed = dev.CalculateSamplesNeeded(); // sample-pairs the device can accept right now
                if (needed <= 0) return;
                int shorts = needed * _soundChannels;
                if (_pumpScratch.Length < shorts) _pumpScratch = new short[shorts];
                _soundBuffer.Read(_pumpScratch, shorts); // dequeue from our ring, silence-pad underruns
                dev.WriteSamples(_pumpScratch, 0, needed);
            }
            catch { /* transient hiccup while the voice is being recreated — skip this tick, stay armed */ }
        }

        /// <summary>
        /// Blit the core's freshly-rendered frame to EmuHawk's window. We hold EmuHawk paused and drive
        /// frame-advance ourselves, so its own run loop never presents — a paused window keeps showing
        /// the last frame its swapchain holds, which is why the host's picture froze while emulation
        /// (audio, netplay) kept running. MainForm.Render() re-presents the current video provider; we
        /// call it once per tick after stepping, the video twin of <see cref="PumpAudio"/>. Best-effort.
        /// </summary>
        public void PresentVideo()
        {
            var form = _mainForm;
            if (form == null || form.IsDisposed) return;
            try { form.Render(); } catch { /* transient present hiccup — next tick tries again */ }
        }

        /// <summary>True once <see cref="EnableAudio"/> has wired up the sound output for the session.</summary>
        public bool AudioReady => _audioReady;

        /// <summary>Human-readable note on why audio was/wasn't wired up (for the UI log).</summary>
        public string AudioDiagnostic { get; private set; } = "";

        /// <summary>Pipeline counters for diagnosing silence: samples produced vs pumped vs buffered.</summary>
        public string AudioStats()
        {
            int ring = _soundBuffer?.Count ?? -1;
            int cap = _soundBuffer?.Capacity ?? 0;
            string err = string.IsNullOrEmpty(_audioSyncErr) ? "" : $" drainErr='{_audioSyncErr}'";
            return $"audio stats: coreMode={(_coreSyncSound ? "Sync" : "Async")} frames={_audioFrames} " +
                   $"pairsProduced={_audioPairs} pumps={_audioPumps} ring={ring}/{cap} shorts peak={_audioPeak}{err}";
        }

        private void UpdatePeak(short[] buf, int shorts)
        {
            if (buf == null) return;
            int n = Math.Min(shorts, buf.Length);
            for (int i = 0; i < n; i++)
            {
                int a = buf[i]; if (a < 0) a = -a;
                if (a > _audioPeak) _audioPeak = a;
            }
        }

        private int SamplesPerFrame()
        {
            double fps = 60.0;
            try
            {
                var vp = _emulator.ServiceProvider.GetService<IVideoProvider>();
                if (vp != null && vp.VsyncNumerator > 0 && vp.VsyncDenominator > 0)
                    fps = (double)vp.VsyncNumerator / vp.VsyncDenominator;
            }
            catch { /* fall back to 60 */ }
            int rate = _sound?.SampleRate ?? 44100;
            return Math.Max(1, (int)Math.Round(rate / fps));
        }

        /// <summary>
        /// Wire up audio for a driven session. We keep EmuHawk paused and step the core ourselves, but
        /// EmuHawk's run loop keeps calling <c>Sound.UpdateSound</c> every iteration — with atten=0
        /// while paused, which discards buffered samples and writes silence to the device. So we (1)
        /// grab EmuHawk's Sound and its host audio device (both via reflection), (2) NULL EmuHawk's
        /// input pin so its paused <c>UpdateSound</c> early-returns and never touches the device, and
        /// (3) feed the device directly from a ring we own (<see cref="NetplaySoundBuffer"/>): filled
        /// from the core each frame in <see cref="AdvanceFrame"/>, drained to the device each tick in
        /// <see cref="PumpAudio"/>. <see cref="DisableAudio"/> restores EmuHawk's wiring on session end.
        /// </summary>
        public void EnableAudio(BizHawk.Client.EmuHawk.MainForm? mainForm)
        {
            _audioReady = false;
            AudioDiagnostic = "";
            _audioFrames = _audioPairs = _audioPumps = 0;
            _audioPeak = 0;
            _audioSyncErr = "";
            try
            {
                if (mainForm == null) { AudioDiagnostic = "no MainForm reference"; return; }
                _mainForm = mainForm;

                // MainForm.Sound is a public type but has a private getter, so reflect it once up front.
                var prop = typeof(BizHawk.Client.EmuHawk.MainForm)
                    .GetProperty("Sound", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _sound = prop?.GetValue(mainForm) as BizHawk.Client.EmuHawk.Sound;
                if (_sound == null) { AudioDiagnostic = "couldn't reach MainForm.Sound"; return; }

                // The host audio device (XAudio2/OpenAL). Sound._outputDevice is private; we drive it directly.
                var devField = typeof(BizHawk.Client.EmuHawk.Sound)
                    .GetField("_outputDevice", BindingFlags.Instance | BindingFlags.NonPublic);
                _outputDevice = devField?.GetValue(_sound) as ISoundOutput;
                if (_outputDevice == null) { AudioDiagnostic = "couldn't reach Sound._outputDevice"; return; }

                _coreSound = _emulator.ServiceProvider.GetService<ISoundProvider>();
                if (_coreSound == null) { AudioDiagnostic = "core exposes no ISoundProvider"; return; }

                // Prefer draining the core synchronously (one frame's samples per FrameAdvance). If the
                // core won't switch to sync, drain it in async mode instead — either way we feed our
                // ring. Forcing sync on an async-only core and then GetSamplesSync'ing it yields silence,
                // which is the bug this replaces.
                if (_coreSound.SyncMode != SyncSoundMode.Sync)
                {
                    try { _coreSound.SetSyncMode(SyncSoundMode.Sync); } catch { /* async core */ }
                }
                _coreSyncSound = _coreSound.SyncMode == SyncSoundMode.Sync;

                // Make sure the device is running before we touch its input pin (so a bail leaves
                // EmuHawk's wiring untouched). StartSound no-ops if the user disabled sound.
                if (!_sound.IsStarted) _sound.StartSound();
                if (!_sound.IsStarted) { AudioDiagnostic = "EmuHawk sound output is off (enable Config → Sound)"; return; }

                _soundChannels = _sound.ChannelCount;
                _asyncScratch = new short[Math.Max(_soundChannels, SamplesPerFrame() * _soundChannels)];
                _soundBuffer = new NetplaySoundBuffer(_sound.SampleRate, _soundChannels, capacityMs: 400);
                // Prime a standing cushion of silence so pump jitter / brief network hitches don't
                // underrun the ring (which would inject audible silence). ~80ms trades a little latency
                // for clean playback.
                int prime = _sound.SampleRate * _soundChannels * 80 / 1000;
                _soundBuffer.Enqueue(new short[prime], prime);

                // Detach EmuHawk's input pin so its run-loop UpdateSound(atten=0, while we hold it paused)
                // early-returns instead of discarding our audio and writing silence to the device. From
                // here PumpAudio owns the device.
                _sound.SetInputPin(null);
                _audioReady = true;

                string cfg = "";
                try
                {
                    var config = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference;
                    if (config != null)
                        cfg = $" out={config.SoundOutputMethod} vol={config.SoundVolume} throttle={config.SoundThrottle} enabled={config.SoundEnabled} bufMs={config.SoundBufferSizeMs}";
                }
                catch { }
                AudioDiagnostic = $"core={_coreSound.GetType().Name} mode={(_coreSyncSound ? "Sync" : "Async")} " +
                                  $"rate={_sound.SampleRate} ch={_soundChannels} started={_sound.IsStarted}{cfg}";
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

        /// <summary>Per-axis host analog bindings (host axis + mult/deadzone/button binds), from the user's
        /// EmuHawk analog config. Null entries are unbound axes (captured as neutral).</summary>
        private AnalogBind?[][] BuildAnalogBinds()
        {
            Dictionary<string, AnalogBind> map = null;
            try
            {
                var config = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference;
                config?.AllTrollersAnalog.TryGetValue(_emulator.ControllerDefinition.Name, out map);
            }
            catch { /* leave unbound -> neutral axes */ }

            var result = new AnalogBind?[_layouts.Length][];
            for (int p = 0; p < _layouts.Length; p++)
            {
                var axes = _layouts[p].Axes;
                var arr = new AnalogBind?[axes.Count];
                for (int j = 0; j < arr.Length; j++)
                    arr[j] = (map != null && map.TryGetValue(axes[j].Name, out var b)) ? b : (AnalogBind?)null;
                result[p] = arr;
            }
            return result;
        }

        /// <summary>The core's IsReversed flag per axis (BizHawk applies it when mapping host -> core).</summary>
        private bool[][] BuildAxisReversed()
        {
            var def = _emulator.ControllerDefinition;
            var result = new bool[_layouts.Length][];
            for (int p = 0; p < _layouts.Length; p++)
            {
                var axes = _layouts[p].Axes;
                var arr = new bool[axes.Count];
                for (int j = 0; j < arr.Length; j++) // names come from def.Axes, so the indexer is safe
                    arr[j] = def.Axes[axes[j].Name].IsReversed;
                result[p] = arr;
            }
            return result;
        }

        /// <summary>
        /// Turn a live host analog reading into a core axis value, mirroring BizHawk's own analog-bind
        /// math (Controller.cs): host value ÷10000 → [-1,1], deadzone, ×Mult, scale to the axis half-range,
        /// shift to Neutral, clamp. Kept identical so a captured value equals what local play would inject.
        /// </summary>
        private static int ResolveAxis(AnalogBind? bindN, CoreAxisSpec spec, bool reversed,
            IReadOnlyDictionary<string, int> hostAxes, HashSet<string> pressedButtons)
        {
            if (bindN == null) return spec.Neutral;
            var bind = bindN.Value;

            float value = 0f;
            if (!string.IsNullOrEmpty(bind.ButtonBindPositive) && EvaluateBinding(bind.ButtonBindPositive, pressedButtons)) value += 1f;
            if (!string.IsNullOrEmpty(bind.ButtonBindNegative) && EvaluateBinding(bind.ButtonBindNegative, pressedButtons)) value -= 1f;
            if (value == 0f)
            {
                int host = 0;
                if (hostAxes != null && !string.IsNullOrEmpty(bind.Value)) hostAxes.TryGetValue(bind.Value, out host);
                value = host / 10000f;
            }

            float dz = bind.Deadzone;
            if (value < -dz) value += dz;
            else if (value < dz) value = 0f;
            else value -= dz;
            value = dz < 1f ? value / (1f - dz) : 0f;

            value *= bind.Mult;
            if (reversed) value = -value;
            value *= Math.Max(spec.Neutral - spec.Min, spec.Max - spec.Neutral);
            value += spec.Neutral;

            int result = (int)Math.Round(value);
            if (result < spec.Min) result = spec.Min;
            else if (result > spec.Max) result = spec.Max;
            return result;
        }

        // --- State --------------------------------------------------------------------

        public StateHandle SaveStateToMemory() =>
            new StateHandle(_emulator.Frame, _apis.MemorySaveState.SaveCoreStateToMemory());

        public void LoadStateFromMemory(StateHandle handle) =>
            _apis.MemorySaveState.LoadCoreStateFromMemory((string)handle.Token);

        public void ReleaseState(StateHandle handle)
        {
            // The rollback ring evicts a state every frame; free the underlying GUID blob so it
            // doesn't accumulate. Tolerate an already-deleted/unknown id (DeleteState of a stale
            // GUID is a no-op or throws depending on build — swallow either way).
            try { _apis.MemorySaveState.DeleteState((string)handle.Token); }
            catch { /* already gone / unknown token — nothing to free */ }
        }

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

        /// <summary>
        /// How many controller ports the loaded core exposes, without building a whole adapter (no
        /// binding/config lookups). The UI needs this before a session to cap the player count — a core
        /// only exposes more than its default ports once a multitap/adapter is enabled in its settings
        /// (Genesis 4-Way Play / Team Player, SNES multitap), which reboots the core and re-reads this.
        /// </summary>
        public static int PortCountOf(IEmulator emulator)
        {
            if (emulator == null) throw new ArgumentNullException(nameof(emulator));
            return BuildLayouts(emulator.ControllerDefinition).Length;
        }

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
