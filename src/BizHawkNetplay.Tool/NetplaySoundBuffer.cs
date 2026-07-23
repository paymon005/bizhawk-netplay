using System;
using BizHawk.Emulation.Common;

namespace BizHawkNetplay.Tool
{
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
        private readonly object _lock = new object();
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

        /// <summary>Push <paramref name="shortCount"/> interleaved shorts from <paramref name="src"/>
        /// into the ring, dropping the oldest samples on overflow so latency stays bounded.</summary>
        public void Enqueue(short[] src, int shortCount)
        {
            if (src == null || shortCount <= 0) return;
            lock (_lock)
            {
                for (int i = 0; i < shortCount; i++)
                {
                    if (_count == _capacity) { _read = (_read + 1) % _capacity; _count--; } // overflow → drop oldest
                    _ring[_write] = src[i];
                    _write = (_write + 1) % _capacity;
                    _count++;
                }
            }
        }

        /// <summary>Fill the whole array from the ring for the device; pad with silence on underrun.</summary>
        public void GetSamplesAsync(short[] samples)
        {
            if (samples == null) return;
            lock (_lock)
            {
                int give = Math.Min(samples.Length, _count);
                for (int i = 0; i < give; i++)
                {
                    samples[i] = _ring[_read];
                    _read = (_read + 1) % _capacity;
                }
                _count -= give;
                for (int i = give; i < samples.Length; i++) samples[i] = 0;
            }
        }

        public void DiscardSamples() { lock (_lock) { _read = _write = _count = 0; } }

        public void GetSamplesSync(out short[] samples, out int nsamp)
            => throw new InvalidOperationException("NetplaySoundBuffer is async-only.");
    }
}
