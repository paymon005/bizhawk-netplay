using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;

namespace BizHawkNetplay.Core.Tests.Fakes
{
    /// <summary>
    /// A deterministic in-memory stand-in for a real core. Lets the probe and (later) the
    /// strategies run with zero BizHawk dependency. "Memory" is a small array mutated by each
    /// frame from the applied inputs, so save/load/hash behave meaningfully.
    /// </summary>
    public sealed class FakeEmuAdapter : IEmuAdapter
    {
        private readonly ControllerLayout _layout;
        private byte[] _memory;
        private int _frame;

        public FakeEmuAdapter(int portCount = 2, int memoryBytes = 4096)
        {
            PortCount = portCount;
            _layout = new ControllerLayout(
                new[] { "Up", "Down", "Left", "Right", "A", "B", "Start", "Select" },
                Array.Empty<AxisSpec>());
            _memory = new byte[memoryBytes];
        }

        // Instrumentation for assertions.
        public int SaveCount { get; private set; }
        public int LoadCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public int InvisibleFrameCount { get; private set; }
        public List<InputSet> AppliedInputs { get; } = new List<InputSet>();

        /// <summary>Live in-memory states not yet released — models BizHawk's GUID-keyed store so
        /// tests can prove the rollback ring stays bounded (no per-frame leak).</summary>
        public HashSet<StateHandle> LiveStates { get; } = new HashSet<StateHandle>();

        /// <summary>The inputs last applied to advance each frame, keyed by frame. A rollback re-runs
        /// frames through <see cref="Step"/>, overwriting the earlier prediction — so once every input
        /// is confirmed this holds the corrected (real) input per frame, the rollback correctness oracle.</summary>
        public Dictionary<int, InputSet> LastInputByFrame { get; } = new Dictionary<int, InputSet>();

        public string RomHash => "fakerom";
        public string CoreName => "FakeCore";
        public string CoreVersion => "1.0";
        public string SyncSettingsDigest => "fakesync";
        public bool VerifyDeterministicMode() => true;

        public int PortCount { get; }
        public ControllerLayout GetControllerLayout(int port) => _layout;

        /// <summary>Scripted local controller, keyed by the current sim frame; neutral if unset.</summary>
        public Func<int, PortInput>? LocalInputScript { get; set; }

        public PortInput ReadLocalInput(int port) =>
            LocalInputScript?.Invoke(_frame) ?? PortInput.Neutral(_layout);

        public void SetInputs(InputSet inputs) => AppliedInputs.Add(inputs);

        /// <summary>
        /// Model EmuHawk running one frame with the inputs the driver just injected: fold the
        /// last applied <see cref="InputSet"/> into memory and advance. Two instances that apply
        /// identical merged inputs evolve identical memory — so hash equality proves lockstep.
        /// </summary>
        public void AdvanceAppliedFrame()
        {
            if (AppliedInputs.Count == 0) throw new InvalidOperationException("No inputs applied yet");
            Step(AppliedInputs[AppliedInputs.Count - 1]);
        }

        public StateHandle SaveStateToMemory()
        {
            SaveCount++;
            var copy = (byte[])_memory.Clone();
            var handle = new StateHandle(_frame, copy);
            LiveStates.Add(handle);
            return handle;
        }

        public void LoadStateFromMemory(StateHandle handle)
        {
            LoadCount++;
            _memory = (byte[])((byte[])handle.Token).Clone();
            _frame = handle.Frame;
        }

        public void ReleaseState(StateHandle handle)
        {
            if (LiveStates.Remove(handle)) ReleaseCount++;
        }

        public byte[] ExportState()
        {
            var buf = new byte[_memory.Length + 4];
            Buffer.BlockCopy(_memory, 0, buf, 4, _memory.Length);
            BitConverter.GetBytes(_frame).CopyTo(buf, 0);
            return buf;
        }

        public void ImportState(byte[] state)
        {
            _frame = BitConverter.ToInt32(state, 0);
            _memory = new byte[state.Length - 4];
            Buffer.BlockCopy(state, 4, _memory, 0, _memory.Length);
        }

        public void SetPaused(bool paused) { }
        public void SetAudioMuted(bool muted) { }

        public void RunFramesInvisible(int count, Func<int, InputSet> inputsFor)
        {
            for (int i = 0; i < count; i++)
            {
                var inputs = inputsFor(i);
                Step(inputs);
                InvisibleFrameCount++;
            }
        }

        private void Step(InputSet inputs)
        {
            // Record the inputs used to advance this frame (resim overwrites any earlier prediction).
            LastInputByFrame[_frame] = inputs;
            // Fold inputs into memory so state genuinely evolves and hashes diverge.
            int acc = _frame;
            foreach (var port in inputs.Ports)
                foreach (var b in port.Buttons)
                    acc = acc * 31 + (b ? 1 : 0);
            _memory[_frame % _memory.Length] ^= (byte)acc;
            _frame++;
        }

        public uint HashMainMemory()
        {
            uint h = 2166136261;
            foreach (var b in _memory) { h ^= b; h *= 16777619; }
            return h;
        }
    }
}
