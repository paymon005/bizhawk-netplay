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

        public EmuHawkAdapter(ApiContainer apis, IEmulator emulator, IStatable statable)
        {
            _apis = apis ?? throw new ArgumentNullException(nameof(apis));
            _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
            _statable = statable ?? throw new ArgumentNullException(nameof(statable));
            _layouts = BuildLayouts(emulator.ControllerDefinition);
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

        public PortInput ReadLocalInput(int port)
        {
            var layout = _layouts[port];
            var current = _apis.Joypad.GetImmediate(port + 1); // BizHawk controllers are 1-based
            var buttons = new bool[layout.Buttons.Count];
            for (int i = 0; i < buttons.Length; i++)
                buttons[i] = current.TryGetValue(layout.Buttons[i], out var v) && v is bool b && b;
            var axes = new int[layout.Axes.Count];
            for (int j = 0; j < axes.Length; j++)
                axes[j] = current.TryGetValue(layout.Axes[j].Name, out var v) && v is int iv
                    ? iv
                    : layout.Axes[j].Neutral;
            return new PortInput(buttons, axes);
        }

        public void SetInputs(InputSet inputs)
        {
            for (int p = 0; p < _layouts.Length && p < inputs.Ports.Length; p++)
            {
                var layout = _layouts[p];
                var port = inputs.Ports[p];
                var boolDict = new Dictionary<string, bool>(layout.Buttons.Count);
                for (int i = 0; i < layout.Buttons.Count; i++)
                    boolDict[layout.Buttons[i]] = port.Buttons[i];
                _apis.Joypad.Set(boolDict, p + 1);

                if (layout.Axes.Count > 0)
                {
                    var axisDict = new Dictionary<string, int?>(layout.Axes.Count);
                    for (int j = 0; j < layout.Axes.Count; j++)
                        axisDict[layout.Axes[j].Name] = port.Axes[j];
                    _apis.Joypad.SetAnalog(axisDict, p + 1);
                }
            }
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
