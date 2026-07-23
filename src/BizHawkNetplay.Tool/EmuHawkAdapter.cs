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
