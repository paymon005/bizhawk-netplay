using System;
using BizHawk.Emulation.Common;

namespace BizHawkNetplay.Tool;

/// <summary>
/// An async <see cref="ISoundProvider"/> backed by a ring buffer we fill ourselves. It exists to
/// smooth audio while we step the core manually.
///
/// We hold EmuHawk paused and advance frames from a WinForms timer, which fires coarsely and
/// irregularly (WM_TIMER is ~15 ms and coalesced). EmuHawk's normal *sync* sound path resamples
/// the core's audio on the assumption of steady once-per-frame pumping, so irregular pumping makes
/// it warble/discard samples (quiet, crackly). Reporting <see cref="SyncSoundMode.Async"/> instead
/// routes <c>Sound.SetInputPin</c> to the buffered-async path, which simply tops up the audio
/// device from a queue — no resampling, no <c>Thread.Sleep</c>, tolerant of jitter.
///
/// Producer: <see cref="Enqueue"/> is called right after each manual FrameAdvance with the samples
/// the core just generated (bursty, tied to our stepping). Consumer: EmuHawk's device pulls via
/// <see cref="GetSamplesAsync"/> at the steady real-time playback rate. The ring absorbs the
/// difference; a small standing prime of silence covers pump jitter without much added latency.
/// </summary>
internal sealed class NetplaySoundBuffer : ISoundProvider
{
    private readonly object _lock = new();
    private readonly short[] _ring; // interleaved (L,R,…) shorts
    private readonly int _capacity;
    private int _read, _write, _count;

    public NetplaySoundBuffer(int sampleRate, int channels, int capacityMs)
    {
        _capacity = Math.Max(channels, sampleRate * channels * Math.Max(1, capacityMs) / 1000);
        _ring = new short[_capacity];
    }

    public bool CanProvideAsync => true;
    public SyncSoundMode SyncMode => SyncSoundMode.Async;
    public void SetSyncMode(SyncSoundMode mode) { /* async only; nothing to switch */ }

    /// <summary>Current fill level in shorts (diagnostic).</summary>
    public int Count { get { lock (_lock) { return _count; } } }

    /// <summary>Ring capacity in shorts (diagnostic).</summary>
    public int Capacity => _capacity;

    /// <summary>Push <paramref name="shortCount"/> interleaved shorts from <paramref name="src"/>
    /// into the ring, dropping the oldest samples on overflow so latency stays bounded.</summary>
    public void Enqueue(short[] src, int shortCount)
    {
        if (src == null || shortCount <= 0) return;
        if (shortCount > src.Length) shortCount = src.Length;
        lock (_lock)
        {
            // A frame of 44.1kHz stereo is ~1,470 shorts. Copying them one at a time with two
            // integer divisions each, under the lock the audio device thread also wants, cost about
            // fifty times what two Array.Copy calls do. Nothing about the ring's behaviour changes —
            // including dropping the oldest on overflow — only how many instructions it takes.
            int from = 0;
            if (shortCount >= _capacity)
            {
                // Everything currently held is about to be overwritten anyway; keep only the newest
                // capacity-worth and start from empty.
                from = shortCount - _capacity;
                shortCount = _capacity;
                _read = _write = _count = 0;
            }
            int overflow = _count + shortCount - _capacity;
            if (overflow > 0) { _read = Advance(_read, overflow); _count -= overflow; }

            int firstRun = Math.Min(shortCount, _capacity - _write);
            Array.Copy(src, from, _ring, _write, firstRun);
            if (firstRun < shortCount)
                Array.Copy(src, from + firstRun, _ring, 0, shortCount - firstRun);
            _write = Advance(_write, shortCount);
            _count += shortCount;
        }
    }

    /// <summary>Move a ring index forward, wrapping. Compare-and-subtract rather than a modulo:
    /// the step never exceeds the capacity, so one branch replaces an integer division.</summary>
    private int Advance(int index, int by)
    {
        index += by;
        return index >= _capacity ? index - _capacity : index;
    }

    /// <summary>Fill the whole array from the ring for the device; pad with silence on underrun.</summary>
    public void GetSamplesAsync(short[] samples)
    {
        if (samples == null) return;
        Read(samples, samples.Length);
    }

    /// <summary>Dequeue <paramref name="count"/> shorts into <paramref name="dest"/>, padding with
    /// silence on underrun. Used to drive the host audio device directly.</summary>
    public void Read(short[] dest, int count)
    {
        if (dest == null) return;
        lock (_lock)
        {
            int give = Math.Min(Math.Min(count, dest.Length), _count);
            int firstRun = Math.Min(give, _capacity - _read);
            Array.Copy(_ring, _read, dest, 0, firstRun);
            if (firstRun < give) Array.Copy(_ring, 0, dest, firstRun, give - firstRun);
            _read = Advance(_read, give);
            _count -= give;
            for (int i = give; i < count && i < dest.Length; i++) dest[i] = 0;
        }
    }

    public void DiscardSamples() { lock (_lock) { _read = _write = _count = 0; } }

    public void GetSamplesSync(out short[] samples, out int nsamp)
        => throw new InvalidOperationException("NetplaySoundBuffer is async-only.");
}
