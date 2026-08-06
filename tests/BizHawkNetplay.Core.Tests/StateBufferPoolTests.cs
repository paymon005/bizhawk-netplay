using System.Collections.Generic;
using BizHawkNetplay.Core.Emu;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The savestate buffer pool, whose one rule is that a buffer is handed out to one owner at a time.
///
/// It was a <c>Stack</c> and two methods on the adapter, untestable because it sat behind BizHawk
/// types it did not actually use. What it permitted was the worst failure this codebase has:
/// releasing a buffer twice pushed it into the pool twice, so two later saves popped what they
/// believed were two buffers and got one — two savestates aliasing the same bytes, the second
/// silently overwriting the first, and a rollback restoring a state nobody asked for.
///
/// The test double for the adapter had always refused a double release. The shipping pool had not.
/// So every test in the suite ran against something strictly more forgiving than production, which
/// is the reason this moved rather than any path anyone could walk.
/// </summary>
public class StateBufferPoolTests
{
    [Fact]
    public void AFreshPoolHandsOutNewBuffers()
    {
        var pool = new StateBufferPool();
        var a = pool.Take();
        var b = pool.Take();
        Assert.NotSame(a, b);
        Assert.Equal(2, pool.Allocated);
        Assert.Equal(0, pool.Size);
    }

    [Fact]
    public void AReturnedBufferIsReused()
    {
        var pool = new StateBufferPool();
        var first = pool.Take();
        pool.Return(first);
        Assert.Equal(1, pool.Size);

        var again = pool.Take();
        Assert.Same(first, again);
        Assert.Equal(1, pool.Allocated);   // reuse, not a second allocation
        Assert.Equal(0, pool.Size);
    }

    /// <summary>
    /// The rule. Returning the same buffer twice must not put it in the pool twice — two owners of
    /// one buffer is two savestates that are secretly the same bytes.
    /// </summary>
    [Fact]
    public void ReturningTheSameBufferTwiceDoesNotPoolItTwice()
    {
        var pool = new StateBufferPool();
        var buffer = pool.Take();

        pool.Return(buffer);
        pool.Return(buffer);
        pool.Return(buffer);
        Assert.Equal(1, pool.Size);

        var a = pool.Take();
        var b = pool.Take();
        Assert.Same(buffer, a);
        Assert.NotSame(a, b);   // the second take must be a DIFFERENT buffer, not the same one again
    }

    /// <summary>
    /// The consequence, driven rather than asserted about: however the returns are shuffled, no two
    /// buffers held at once are ever the same object.
    /// </summary>
    [Fact]
    public void NoTwoBuffersHeldAtOnceAreEverTheSame()
    {
        var pool = new StateBufferPool();
        var held = new List<StateBuffer>();
        var rng = new System.Random(0xB0FF);

        for (int step = 0; step < 2000; step++)
        {
            if (held.Count > 0 && rng.Next(2) == 0)
            {
                int at = rng.Next(held.Count);
                var giving = held[at];
                held.RemoveAt(at);
                pool.Return(giving);
                // A caller that loses track and returns again — the mistake the flag exists for.
                if (rng.Next(4) == 0) pool.Return(giving);
            }
            else
            {
                var taken = pool.Take();
                Assert.DoesNotContain(taken, held);
                held.Add(taken);
            }
        }
    }

    [Fact]
    public void ATakenBufferIsNotMarkedRetired()
    {
        var pool = new StateBufferPool();
        var buffer = pool.Take();
        Assert.False(buffer.Retired);
        pool.Return(buffer);
        Assert.True(buffer.Retired);
        Assert.False(pool.Take().Retired);
    }

    [Fact]
    public void ATakenBufferStartsEmptyAndPositionedAtZero()
    {
        // The writer appends from Position, so a buffer handed out mid-stream would produce a state
        // with another state's bytes in front of it.
        var pool = new StateBufferPool();
        var buffer = pool.Take();
        buffer.Writer.Write(new byte[512]);
        buffer.Writer.Flush();
        pool.Return(buffer);

        var again = pool.Take();
        Assert.Same(buffer, again);
        Assert.Equal(0, again.Stream.Length);
        Assert.Equal(0, again.Stream.Position);
    }

    /// <summary>
    /// Past the cap a returned buffer is let go rather than retained, so a burst cannot pin memory.
    /// It is still marked retired: the cap governs whether the pool keeps it, not whether its owner
    /// gave it up.
    /// </summary>
    [Fact]
    public void ReturnsPastTheCapAreLetGoRatherThanRetained()
    {
        var pool = new StateBufferPool(cap: 4);
        var taken = new List<StateBuffer>();
        for (int i = 0; i < 10; i++) taken.Add(pool.Take());
        foreach (var buffer in taken) pool.Return(buffer);

        Assert.Equal(4, pool.Size);
        Assert.All(taken, b => Assert.True(b.Retired));
    }

    [Fact]
    public void TheSizeHintOnlyEverGrows()
    {
        var pool = new StateBufferPool(initialSizeHint: 1024);
        pool.NoteSize(4096);
        Assert.Equal(4096, pool.SizeHint);
        pool.NoteSize(16);
        Assert.Equal(4096, pool.SizeHint);   // a small state must not shrink the next allocation
    }

    [Fact]
    public void ClearingDropsEveryRetainedBuffer()
    {
        var pool = new StateBufferPool();
        var buffer = pool.Take();
        pool.Return(buffer);
        pool.Clear();
        Assert.Equal(0, pool.Size);
        // And the next take is a fresh one rather than the disposed stream.
        var fresh = pool.Take();
        Assert.NotSame(buffer, fresh);
        fresh.Writer.Write(1);   // usable: would throw ObjectDisposedException on the cleared one
        fresh.Writer.Flush();
    }

    [Fact]
    public void ReturningNothingIsHarmless()
    {
        var pool = new StateBufferPool();
        pool.Return(null);
        Assert.Equal(0, pool.Size);
    }
}
