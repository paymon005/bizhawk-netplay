using System;
using System.IO;
using System.Text;

namespace BizHawkNetplay.Core.Emu;

/// <summary>
/// One reusable savestate buffer, and whether anyone currently owns it.
///
/// The stream and its reader/writer are kept together because they wrap it and must not be
/// recreated per save — that pairing is the whole reason a buffer is worth pooling.
/// </summary>
public sealed class StateBuffer
{
    internal StateBuffer(int capacity)
    {
        Stream = new MemoryStream(capacity);
        Writer = new BinaryWriter(Stream, Encoding.UTF8, leaveOpen: true);
        Reader = new BinaryReader(Stream, Encoding.UTF8, leaveOpen: true);
    }

    public MemoryStream Stream { get; }
    public BinaryWriter Writer { get; }
    public BinaryReader Reader { get; }

    /// <summary>
    /// True while this buffer is sitting in the pool with nobody holding it.
    ///
    /// The flag exists to make a second release a no-op. Without it the same buffer can be pushed
    /// twice, and then two saves pop what they believe are two buffers and get one — so two
    /// savestates alias the same bytes and the second silently overwrites the first.
    /// </summary>
    public bool Retired { get; internal set; }
}

/// <summary>
/// The savestate buffer pool: hand a buffer out, take it back, and never hand the same one out
/// twice.
///
/// <b>Why pooled at all.</b> Allocating a whole-core state per snapshot put megabytes a second onto
/// the large object heap — on a heavy core the resulting gen2 collections landed inside the frame
/// decision as multi-tens-of-millisecond hitches. Reuse is what makes a per-frame savestate ring
/// affordable.
///
/// <b>Why a type rather than a Stack.</b> It was a <c>Stack</c> and two methods on the adapter, and
/// the release path had no guard against being called twice on the same buffer. Nothing reached it,
/// but the failure it permits is the worst kind this codebase has: two ring entries for different
/// frames pointing at one buffer, so a rollback restores a state nobody asked for, with nothing
/// raised. The test double for the adapter has always refused a double release; the shipping pool
/// did not — meaning every test in the suite ran against something strictly more forgiving than
/// production. That asymmetry is the argument, more than any path anyone can currently walk.
///
/// Single-threaded by construction: every caller is the emulator thread, which is the only thread
/// allowed to touch a core.
/// </summary>
public sealed class StateBufferPool
{
    /// <summary>
    /// Cap on retained buffers. The rollback ring keeps roughly maxRollback + margin states, so
    /// this is far above steady state and exists only so a pathological release burst cannot pin
    /// memory. Over the cap a returned buffer is simply let go for the collector.
    /// </summary>
    public const int DefaultCap = 64;

    private readonly System.Collections.Generic.Stack<StateBuffer> _free = new();
    private readonly int _cap;
    private int _sizeHint;

    public StateBufferPool(int cap = DefaultCap, int initialSizeHint = 1 << 16)
    {
        _cap = cap < 1 ? 1 : cap;
        _sizeHint = initialSizeHint < 1 ? 1 : initialSizeHint;
    }

    /// <summary>Buffers currently retired and reusable. Reported so a session can show the pool
    /// reaching steady state — once it stops growing, the save path has stopped allocating.</summary>
    public int Size => _free.Count;

    /// <summary>Buffers this pool had to create. Below the save count exactly to the extent reuse
    /// is working, which is the only way to tell a pool from a wrapper around <c>new</c>.</summary>
    public int Allocated { get; private set; }

    /// <summary>Largest state seen, so a fresh buffer starts big enough to avoid growth copies.</summary>
    public int SizeHint => _sizeHint;

    /// <summary>Take a buffer to write a state into. Reused when one is free, fresh otherwise.</summary>
    public StateBuffer Take()
    {
        StateBuffer buffer;
        if (_free.Count > 0) buffer = _free.Pop();
        else { buffer = new StateBuffer(_sizeHint); Allocated++; }
        buffer.Retired = false;
        buffer.Stream.SetLength(0);
        buffer.Stream.Position = 0;
        return buffer;
    }

    /// <summary>Note how big a state actually turned out, so the next fresh buffer starts there.</summary>
    public void NoteSize(long bytes)
    {
        if (bytes > _sizeHint && bytes <= int.MaxValue) _sizeHint = (int)bytes;
    }

    /// <summary>
    /// Give a buffer back. Releasing one twice is a no-op rather than a second entry in the pool —
    /// see <see cref="StateBuffer.Retired"/> for what the second entry would cost.
    /// </summary>
    public void Return(StateBuffer? buffer)
    {
        if (buffer == null || buffer.Retired) return;
        buffer.Retired = true;
        if (_free.Count >= _cap) return;   // over the cap: let the collector have it
        buffer.Stream.SetLength(0);
        _free.Push(buffer);
    }

    /// <summary>
    /// Drop every retired buffer. Called when a session ends so a long idle between sessions does
    /// not hold the ring's worth of memory for nothing.
    ///
    /// Must run AFTER whatever holds states is disposed — disposing the rollback ring is what
    /// returns its buffers here. Running it early would dispose streams the ring still names, and
    /// the next release of one would throw from inside a teardown path.
    /// </summary>
    public void Clear()
    {
        while (_free.Count > 0) _free.Pop().Stream.Dispose();
    }
}
