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

    /// <summary>What this buffer counted toward the pool's retained-byte total when it was taken
    /// back. Stored rather than re-measured, so the subtraction on the way out is exactly the
    /// addition on the way in even if the stream has since grown.</summary>
    internal long RetainedSize { get; set; }
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

    /// <summary>
    /// Ceiling on RETAINED bytes, which is the cap that actually means something.
    ///
    /// Sixty-four buffers is a wildly different commitment per core: about 32 MiB of SNES states,
    /// and better than a gigabyte of N64 ones. A count alone therefore bounds nothing on the core
    /// where bounding it matters, and the pool would happily sit on hundreds of megabytes it will
    /// never hand out again — which is felt hardest when three or four EmuHawk instances share a
    /// machine, exactly the four-player case this tool exists for.
    ///
    /// 256 MiB is comfortably more than a rollback ring needs on any core the probe will grant
    /// depth to (about fifteen N64 states, against a realistic N64 ring of three to six), and never
    /// binds at all on lighter cores, where the count cap governs as before. Whichever binds first
    /// wins.
    /// </summary>
    public const long DefaultMaxRetainedBytes = 256L * 1024 * 1024;

    private readonly System.Collections.Generic.Stack<StateBuffer> _free = new();
    private readonly int _cap;
    private readonly long _maxRetainedBytes;
    private long _retainedBytes;
    private int _sizeHint;

    public StateBufferPool(int cap = DefaultCap, int initialSizeHint = 1 << 16,
        long maxRetainedBytes = DefaultMaxRetainedBytes)
    {
        _cap = cap < 1 ? 1 : cap;
        _sizeHint = initialSizeHint < 1 ? 1 : initialSizeHint;
        _maxRetainedBytes = maxRetainedBytes < 1 ? 1 : maxRetainedBytes;
    }

    /// <summary>Bytes currently held in retired buffers. The figure the ceiling is against, and
    /// the one worth putting in a diagnostics line.</summary>
    public long RetainedBytes => _retainedBytes;

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
        if (_free.Count > 0)
        {
            buffer = _free.Pop();
            _retainedBytes -= buffer.RetainedSize;   // exactly what Return added, not a re-measure
            buffer.RetainedSize = 0;
        }
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

        // Whichever ceiling binds first. Over either, the buffer is let go for the collector — it
        // is still marked retired, because the cap governs whether the POOL keeps it, not whether
        // its owner gave it up.
        long size = buffer.Stream.Capacity;
        if (_free.Count >= _cap || _retainedBytes + size > _maxRetainedBytes) return;
        buffer.Stream.SetLength(0);
        buffer.RetainedSize = size;
        _retainedBytes += size;
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
        _retainedBytes = 0;
    }
}
