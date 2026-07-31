using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;

namespace BizHawkNetplay.Core.Tests.Fakes;

/// <summary>
/// A deterministic in-memory stand-in for a real core. Lets the probe and (later) the
/// strategies run with zero BizHawk dependency. "Memory" is a small array mutated by each
/// frame from the applied inputs, so save/load/hash behave meaningfully.
/// </summary>
public sealed class FakeEmuAdapter : IEmuAdapter
{
    private ControllerLayout _layout;
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
    public int HashCount { get; private set; }
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

    public IReadOnlyList<KeyValuePair<string, string>> SyncSettingsFields { get; set; } =
        new List<KeyValuePair<string, string>>();

    public bool VerifyDeterministicMode() => true;

    public int PortCount { get; }

    /// <summary>Override the default 8-button pad — for the cases where the SIZE of a layout is
    /// the thing under test, such as the datagram limit.</summary>
    public ControllerLayout Layout
    {
        get => _layout;
        set => _layout = value ?? throw new ArgumentNullException(nameof(value));
    }

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

    /// <summary>
    /// The frame each save captured, and (from, to) for each load. Costs on a real core depend on
    /// whether the operation is against state that actually changed — a snapshot of untouched
    /// memory and a load of the state the core already stands on are both measurably cheaper than
    /// the real thing — so a test needs to be able to prove the probe times the real thing.
    /// </summary>
    public List<int> SavedAtFrames { get; } = new List<int>();
    public List<(int From, int To)> LoadJumps { get; } = new List<(int, int)>();

    /// <summary>
    /// Buffers handed back by <see cref="ReleaseState"/>, reused by the next save — and
    /// deliberately scribbled over first.
    ///
    /// The real adapter pools these, because allocating a fresh whole-core state per snapshot was
    /// putting 26.5MB/s onto the Large Object Heap and the resulting gen2 collections were landing
    /// inside the frame decision as multi-tens-of-millisecond hitches. Pooling admits exactly one
    /// new failure mode: reading a state after releasing it. Mirroring it here means every rollback
    /// test in the suite — loss, latency, sparse keyframes, checksums, ring bounds — doubles as a
    /// use-after-release detector, and the poison below turns a silent wrong answer into a loud one.
    /// </summary>
    private readonly Stack<byte[]> _statePool = new();

    /// <summary>Buffers this adapter had to create. Below <see cref="SaveCount"/> exactly to the
    /// extent the pool is doing its job — and the guard against the reuse coverage above being
    /// vacuous, since a pool that never hands a buffer back tests nothing.</summary>
    public int StateBuffersAllocated { get; private set; }

    public StateHandle SaveStateToMemory()
    {
        SaveCount++;
        SavedAtFrames.Add(_frame);
        byte[] buffer;
        if (_statePool.Count > 0 && _statePool.Peek().Length == _memory.Length) buffer = _statePool.Pop();
        else { buffer = new byte[_memory.Length]; StateBuffersAllocated++; }
        Buffer.BlockCopy(_memory, 0, buffer, 0, _memory.Length);
        var handle = new StateHandle(_frame, buffer);
        LiveStates.Add(handle);
        return handle;
    }

    public void LoadStateFromMemory(StateHandle handle)
    {
        LoadCount++;
        LoadJumps.Add((_frame, handle.Frame));
        _memory = (byte[])((byte[])handle.Token).Clone();
        _frame = handle.Frame;
    }

    public void ReleaseState(StateHandle handle)
    {
        if (!LiveStates.Remove(handle)) return;
        ReleaseCount++;
        if (!(handle.Token is byte[] buffer)) return;
        // Poison before retiring. A released buffer holds no meaningful state, so anything that
        // reads one is already wrong; filling it makes that wrongness fail a checksum immediately
        // instead of surviving as whatever the bytes happened to still be.
        for (int i = 0; i < buffer.Length; i++) buffer[i] = 0xDD;
        _statePool.Push(buffer);
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

    /// <summary>Counted separately so a test can prove the probe times a rendered frame at all;
    /// there is nothing to render here, so it costs whatever the scripted clock says.</summary>
    public int RenderedFrameCount { get; private set; }

    public void AdvanceRenderedFrame(InputSet inputs)
    {
        RenderedFrameCount++;
        Step(inputs);
    }

    public void RunFramesInvisible(int count, Func<int, InputSet> inputsFor)
    {
        for (int i = 0; i < count; i++)
        {
            var inputs = inputsFor(i);
            Step(inputs);
            InvisibleFrameCount++;
        }
    }

    /// <summary>
    /// Set to model a core that does not reproduce from a savestate — the property N64 appeared to
    /// violate in real play. Each advance folds in a counter that savestates do NOT capture, so
    /// replaying the same inputs from the same state lands somewhere else, exactly as a core with
    /// hidden state outside its savestate would.
    /// </summary>
    public bool DriftsOnReplay { get; set; }

    private int _hiddenDrift;

    private void Step(InputSet inputs)
    {
        // Record the inputs used to advance this frame (resim overwrites any earlier prediction).
        LastInputByFrame[_frame] = inputs;
        // Fold inputs into memory so state genuinely evolves and hashes diverge.
        int acc = _frame;
        foreach (var port in inputs.Ports)
            foreach (var b in port.Buttons)
                acc = acc * 31 + (b ? 1 : 0);
        if (DriftsOnReplay) acc += ++_hiddenDrift;
        _memory[_frame % _memory.Length] ^= (byte)acc;
        _frame++;
    }

    // Reads the whole (tiny) buffer, so the sampling salt is irrelevant here.
    public uint HashMainMemory(int salt = 0)
    {
        HashCount++;
        uint h = 2166136261;
        foreach (var b in _memory) { h ^= b; h *= 16777619; }
        return h;
    }
}
