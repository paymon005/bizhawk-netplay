using System;
using BizHawk.Emulation.Common;

namespace BizHawkNetplay.Tool;

/// <summary>
/// The ring buffer between our bursty manual frame stepping and the audio device's steady
/// real-time pull. Producer: <c>DrainCoreAudio</c> enqueues right after each manual FrameAdvance
/// with whatever the core just generated. Consumer: <c>PumpAudio</c> reads at the device's own
/// rate and writes to it directly. The ring absorbs the mismatch; a small standing prime of
/// silence covers pump jitter without much added latency.
///
/// Historical note, because the shape is otherwise puzzling: this used to be attached as
/// EmuHawk's <c>Sound</c> input pin (hence <see cref="ISoundProvider"/> and
/// <see cref="GetSamplesAsync"/>, which the interface requires). That cannot work while the tool
/// owns stepping — EmuHawk's own <c>UpdateSound</c> runs at attenuation 0 the whole time and
/// would discard the ring and flood the device with silence — so the pin is detached and
/// <c>PumpAudio</c> drives the device itself through <see cref="Read"/>.
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
            // Block clear, same reason the copies above stopped being per-sample loops: this runs
            // under the lock the frame thread's Enqueue also wants, and it runs exactly when audio
            // is already in trouble (an underrun) — the worst moment to hold the lock longer.
            int padEnd = Math.Min(count, dest.Length);
            if (padEnd > give) Array.Clear(dest, give, padEnd - give);
        }
    }

    public void DiscardSamples() { lock (_lock) { _read = _write = _count = 0; } }

    public void GetSamplesSync(out short[] samples, out int nsamp)
        => throw new InvalidOperationException("NetplaySoundBuffer is async-only.");
}
