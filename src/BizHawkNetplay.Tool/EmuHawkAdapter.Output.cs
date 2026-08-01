using System;
using System.Reflection;
using BizHawk.Client.Common;
using BizHawk.Emulation.Common;

namespace BizHawkNetplay.Tool;

/// <summary>
/// Audio and video output while the session owns the run loop.
///
/// One subject, not two: because we hold EmuHawk paused and step the core ourselves, its own loop
/// keeps calling <c>Sound.UpdateSound</c> and <c>Render</c> as though nothing had changed. Audio has
/// to be taken away from it — a paused <c>UpdateSound</c> runs at atten 0 and floods the device with
/// silence — while video has to be left to it wherever its <c>Render</c> still lands in time. Both
/// halves are therefore about the same question: which of EmuHawk's output paths still works while
/// we drive, and which we have to drive ourselves.
///
/// Every reflection hop here reaches something BizHawk exposes only privately, so all of it is
/// best-effort and none of it may throw into the frame tick.
/// </summary>
internal sealed partial class EmuHawkAdapter
{
    // Audio: we drive EmuHawk's Sound output ourselves (see EnableAudio / DrainCoreAudio / PumpAudio).
    private BizHawk.Client.EmuHawk.Sound? _sound;
    private BizHawk.Client.EmuHawk.MainForm? _mainForm;
    private ISoundOutput? _outputDevice;             // EmuHawk's host audio device, driven directly
    private ISoundProvider? _coreSound;
    private NetplaySoundBuffer? _soundBuffer;
    private short[] _pumpScratch = [];
    private int _soundChannels = 2;
    // Standing audio cushion in ms — a permanent video→audio offset, so keep it as small as the
    // pump jitter allows (see EnableAudio). ~2.5 frames at 60fps.
    private const int AudioPrimeMs = 40;
    private bool _audioReady;
    private bool _coreSyncSound = true;              // drain via GetSamplesSync (else GetSamplesAsync)
    private short[] _asyncScratch = [];
    // Diagnostics so a single test round shows where the audio pipeline breaks.
    private long _audioFrames, _audioPairs, _audioPumps;
    // Restart bookkeeping for a device that stopped under us — see PumpAudio's revival path.
    private readonly System.Diagnostics.Stopwatch _audioRevive = System.Diagnostics.Stopwatch.StartNew();
    private double _lastReviveAttemptMs = double.NegativeInfinity;
    private const double ReviveIntervalMs = 500; // don't hammer StartSound every tick
    public int AudioRevivals { get; private set; }
    private int _audioPeak;               // max abs sample seen (0 => core handed us silence)
    private string _audioSyncErr = "";

    /// <summary>
    /// Empty the core's sound provider without keeping anything. Resolves the provider itself so it
    /// works whether or not <see cref="EnableAudio"/> ever ran — the whole point is the sessions
    /// where it didn't. DiscardSamples is determinism-safe: the sample clocks it resets are pure
    /// output timestamps, serialized nowhere and read by nothing but the audio path.
    /// </summary>
    private void DiscardCoreAudio()
    {
        try
        {
            _coreSound ??= _emulator.ServiceProvider.GetService<ISoundProvider>();
            _coreSound?.DiscardSamples();
        }
        catch { /* audio must never break emulation */ }
    }

    /// <summary>
    /// Move the samples the core just produced into our ring buffer.
    ///
    /// EmuHawk's audio device pulls from that ring at the steady real-time rate
    /// (<see cref="PumpAudio"/>), so the ring absorbs the mismatch between this bursty manual
    /// stepping and smooth playback. Producing here and consuming there is what keeps audio clean
    /// despite a coarse frame clock.
    ///
    /// Drained in whichever mode the core actually reports: a sync-only drain of an async core gets
    /// nothing, which is audible as silence rather than as an error.
    /// </summary>
    private void DrainCoreAudio()
    {
        // A session without working audio still has to EMPTY the core. Skipping the drain entirely
        // left the Hawk cores' blip_buf accumulating forever — sound merely muted in EmuHawk's
        // config was enough to reach a native out-of-bounds write within seconds of session start.
        // See RunFramesInvisible for the chapter and verse.
        if (!_audioReady) { DiscardCoreAudio(); return; }
        try
        {
            if (_coreSyncSound)
            {
                _coreSound!.GetSamplesSync(out var samples, out var nSampPairs);
                int shorts = nSampPairs * _soundChannels;
                UpdatePeak(samples, shorts);
                _soundBuffer!.Enqueue(samples, shorts);
                _audioFrames++; _audioPairs += nSampPairs;
            }
            else
            {
                _coreSound!.GetSamplesAsync(_asyncScratch);
                UpdatePeak(_asyncScratch, _asyncScratch.Length);
                _soundBuffer!.Enqueue(_asyncScratch, _asyncScratch.Length);
                _audioFrames++; _audioPairs += _asyncScratch.Length / Math.Max(1, _soundChannels);
            }
        }
        catch (Exception ex) { _audioSyncErr = ex.Message; } // never break emulation over audio
    }

    /// <summary>
    /// Top up the host audio device from our ring buffer. Call every frame-timer tick — whether or
    /// not a frame advanced — so the device stays fed at the real playback rate, decoupled from our
    /// (bursty, occasionally stalled) frame stepping.
    ///
    /// We write to the device DIRECTLY rather than via <c>Sound.UpdateSound</c>, because EmuHawk's
    /// own run loop also calls <c>Sound.UpdateSound</c> every iteration; while we hold it paused
    /// that call runs with atten=0, which discards our buffered samples and floods the device with
    /// silence far faster than we can pump. Nulling EmuHawk's input pin (see EnableAudio) makes its
    /// call early-return, leaving the device ours to feed here.
    /// </summary>
    // EmuHawk's per-frame host-input bookkeeping, which lives inside the run loop's frame-advance
    // block and therefore never runs while a session owns stepping. Resolved once, best-effort.
    private static readonly FieldInfo? InputManagerField =
        typeof(BizHawk.Client.EmuHawk.MainForm)
            .GetField("InputManager", BindingFlags.Instance | BindingFlags.NonPublic);
    private object? _inputManager;
    private MethodInfo? _stickyIncrementLoops;
    private object? _stickyAutofire;
    private MethodInfo? _clickyFrameTick;
    private object? _clickyVirtualPad;
    private bool _hostInputBookkeepingResolved;

    /// <summary>Whether EmuHawk's own per-frame input bookkeeping could be reached. False means
    /// <see cref="AdvanceHostInputBookkeeping"/> is doing nothing and the caller should say so.</summary>
    public bool HostInputBookkeepingAvailable { get; private set; }

    /// <summary>
    /// Advance the two pieces of host-side input state that MainForm normally ticks once per frame.
    ///
    /// Both live inside the <c>BlockFrameAdvance</c> gate, so during a session neither runs — and
    /// both are stateful. <c>StickyAutofireController.IncrementLoops</c> is the only thing that
    /// advances an autofire pattern, and <c>IsPressed</c> merely peeks, so a sticky-autofire button
    /// reads whatever value it held when the session began, forever. <c>ClickyVirtualPadController
    /// .FrameTick</c> is a Clear(), so a Virtual Pad click sticks for the rest of the session —
    /// which looks exactly like a desync from the chair.
    ///
    /// Determinism is unaffected either way: both sit upstream of the controller we read, so their
    /// resolved value is captured and shipped to peers like any other input. This restores two
    /// features, it does not change what the wire carries.
    /// </summary>
    public void AdvanceHostInputBookkeeping()
    {
        if (!_hostInputBookkeepingResolved) ResolveHostInputBookkeeping();
        if (!HostInputBookkeepingAvailable) return;
        try
        {
            bool lagged = false;
            try { lagged = _emulator.CanPollInput() && _emulator.AsInputPollable().IsLagFrame; }
            catch { /* a core without lag polling simply never reports one */ }
            _stickyIncrementLoops!.Invoke(_stickyAutofire, new object[] { lagged });
            _clickyFrameTick!.Invoke(_clickyVirtualPad, null);
        }
        catch
        {
            // One failure retires it: this runs per frame and a throwing reflection call every
            // frame would cost more than the features are worth.
            HostInputBookkeepingAvailable = false;
        }
    }

    private void ResolveHostInputBookkeeping()
    {
        _hostInputBookkeepingResolved = true;
        try
        {
            _inputManager = _mainForm == null ? null : InputManagerField?.GetValue(_mainForm);
            if (_inputManager == null) return;
            var type = _inputManager.GetType();
            _stickyAutofire = type.GetProperty("StickyAutofireController")?.GetValue(_inputManager);
            _clickyVirtualPad = type.GetProperty("ClickyVirtualPadController")?.GetValue(_inputManager);
            _stickyIncrementLoops = _stickyAutofire?.GetType()
                .GetMethod("IncrementLoops", new[] { typeof(bool) });
            _clickyFrameTick = _clickyVirtualPad?.GetType()
                .GetMethod("FrameTick", Type.EmptyTypes);
            HostInputBookkeepingAvailable =
                _stickyIncrementLoops != null && _clickyFrameTick != null;
        }
        catch { HostInputBookkeepingAvailable = false; }
    }

    public void PumpAudio()
    {
        if (!_audioReady) return;
        if (!EnsureAudioOwnership()) return;
        var dev = _outputDevice;
        var snd = _sound;
        if (dev == null || snd == null || _soundBuffer == null) return;

        // EmuHawk stops (and sometimes recreates) its audio device on several UI paths — the
        // window RESIZE/MOVE loop (ResizeBegin/ResizeEnd), the mute hotkey, Config → Sound OK —
        // not, as this comment used to claim, minimize/restore. While it's stopped, skip — do NOT
        // give up permanently, or audio would stay dead until a reconnect. When EmuHawk restarts
        // the device we resume. Known interplay, accepted: during a title-bar drag the modal
        // move/size loop still dispatches WM_TIMER, so the revival below re-arms the device the
        // resize mute just stopped; the drag also freezes EmuHawk's input latch, so frames advance
        // on stale input for its duration either way.
        if (!snd.IsStarted)
        {
            // Keep draining anyway. This pump is the ring's only consumer, so standing still
            // lets the core's continued output pile up: observed in play as 300 frames with the
            // ring pegged at capacity, which is ~0.8s of stale audio waiting to be played the
            // moment the device comes back. Those samples belong to a moment that has passed —
            // dropping them costs nothing and keeps the resume clean.
            if (_soundBuffer.Count > _soundBuffer.Capacity / 4) _soundBuffer.DiscardSamples();
            ReviveSound(snd);
            return;
        }

        _audioPumps++;
        try
        {
            dev.ApplyVolumeSettings(HostAttenuation());
            int needed = dev.CalculateSamplesNeeded(); // sample-pairs the device can accept right now
            if (needed <= 0) return;
            int shorts = needed * _soundChannels;
            if (_pumpScratch.Length < shorts) _pumpScratch = new short[shorts];
            _soundBuffer.Read(_pumpScratch, shorts); // dequeue from our ring, silence-pad underruns
            dev.WriteSamples(_pumpScratch, 0, needed);
        }
        catch { /* transient hiccup while the voice is being recreated — skip this tick, stay armed */ }
    }

    /// <summary>
    /// Re-take the audio device if EmuHawk has taken it back, before every pump.
    ///
    /// Opening Config → Sound during a session used to break audio for the rest of it, and both
    /// halves of why are in MainForm.Events.cs: on OK it either calls Sound.StopSound(), or — when
    /// the output method or device changed — Sound.Dispose() and builds a WHOLE NEW Sound. Then,
    /// either way, it calls RewireSound(), which does SetInputPin(_currentSoundProvider).
    ///
    /// So two things break at once. Our Sound and device references can point at a disposed object;
    /// and even when they survive, EmuHawk has re-attached its own provider, so its UpdateSound stops
    /// early-returning and starts fighting us for the device — at atten 0, because BlockFrameAdvance
    /// means the run loop never reaches the line that computes volume. The audible result is exactly
    /// what was reported: sound dies and does not come back.
    ///
    /// Reasserting is free. SetInputPin(null) with nothing attached is two null checks and a return;
    /// with something attached it detaches AND discards the samples EmuHawk buffered, which is what
    /// we want anyway. So it is simply done every pump rather than tracked.
    /// </summary>
    /// <summary>
    /// The two by-name lookups the audio path needs, resolved once.
    ///
    /// <see cref="EnsureAudioOwnership"/> runs on every pump — sixty to a hundred and twenty times
    /// a second — and Type.GetProperty with BindingFlags is a name search over the whole of MainForm,
    /// which is a very large type, plus a PropertyInfo allocation each time. The docstring below is
    /// right that reasserting the input PIN is free; the reflection above it was not.
    /// </summary>
    private static readonly PropertyInfo? SoundProperty =
        typeof(BizHawk.Client.EmuHawk.MainForm)
            .GetProperty("Sound", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? OutputDeviceField =
        typeof(BizHawk.Client.EmuHawk.Sound)
            .GetField("_outputDevice", BindingFlags.Instance | BindingFlags.NonPublic);

    private bool EnsureAudioOwnership()
    {
        var mainForm = _mainForm;
        if (mainForm == null) return _sound != null;
        try
        {
            var current = SoundProperty?.GetValue(mainForm) as BizHawk.Client.EmuHawk.Sound;
            if (current == null) return false;

            if (!ReferenceEquals(current, _sound))
            {
                // A new Sound object: the old device is disposed and every reference we hold is dead.
                _sound = current;
                _outputDevice = OutputDeviceField?.GetValue(current) as ISoundOutput;
                _soundBuffer?.DiscardSamples(); // buffered for a device that no longer exists
                AudioRebinds++;
            }
            _sound?.SetInputPin(null);
            return _outputDevice != null;
        }
        catch { return false; }
    }

    /// <summary>Times EmuHawk handed us a different Sound object mid-session — a sound-settings
    /// change. Reported by AudioStats so "I opened Config → Sound" is visible in a log.</summary>
    public int AudioRebinds { get; private set; }

    /// <summary>
    /// Bring EmuHawk's sound device back up after it stopped under us.
    ///
    /// Waiting for EmuHawk to do it is what the old code did, and the reasoning was wrong in a way
    /// this takeover created. <see cref="EnableAudio"/> detaches the input pin precisely so
    /// EmuHawk's <c>UpdateSound</c> early-returns and leaves the device to us — which also
    /// short-circuits whatever would have restarted it. So a device that stops mid-session stays
    /// stopped: observed in play as the pump counter frozen for hundreds of frames with audio
    /// dead for the rest of the session, recovering only on reconnect, because reconnecting runs
    /// EnableAudio and its StartSound again.
    ///
    /// Having taken the device, we own reviving it. Rate-limited because StartSound is not free
    /// and the tick runs ~60 times a second; the device reference is re-read on success because a
    /// stop/start cycle can hand back a different object, and the stale one would then throw on
    /// every pump forever after.
    /// </summary>
    private void ReviveSound(BizHawk.Client.EmuHawk.Sound snd)
    {
        double now = _audioRevive.Elapsed.TotalMilliseconds;
        if (now - _lastReviveAttemptMs < ReviveIntervalMs) return;
        _lastReviveAttemptMs = now;
        try
        {
            snd.StartSound();
            if (!snd.IsStarted) return; // sound disabled in config, or the device is genuinely gone

            if (OutputDeviceField?.GetValue(snd) is ISoundOutput fresh) _outputDevice = fresh;
            AudioRevivals++;
        }
        catch { /* try again on the next interval; never let this kill the frame tick */ }
    }

    /// <summary>
    /// Blit the core's freshly-rendered frame to EmuHawk's window, by calling the same
    /// MainForm.Render() its own run loop calls.
    ///
    /// The claim this once carried — that a paused EmuHawk "never presents" — is wrong: Render()
    /// runs every loop iteration regardless of pause. What is true is that it runs at a fixed point
    /// in that iteration (MainForm.cs:1011), so a frame landing outside the loop, from the WinForms
    /// fallback timer, can miss it and sit unshown. That is the caller this exists for; the caller
    /// driven by the loop itself skips it, because Render is already two statements away.
    /// Best-effort.
    /// </summary>
    /// <returns>
    /// True if a picture actually reached the window. This returned void and swallowed every
    /// failure, while the caller counted a present regardless — so a window that had stopped
    /// rendering entirely still reported a healthy presented-fps, which is precisely the symptom
    /// this method exists to detect. Consecutive failures are counted so a persistent one is
    /// distinguishable from the transient hiccup the catch was written for.
    /// </returns>
    public bool PresentVideo()
    {
        var form = _mainForm;
        if (form == null || form.IsDisposed) { PresentFailuresInARow++; return false; }
        try
        {
            form.Render();
            PresentFailuresInARow = 0;
            return true;
        }
        catch (Exception ex)
        {
            PresentFailuresInARow++;
            if (LastPresentError == null) LastPresentError = ex.Message;
            return false;
        }
    }

    /// <summary>Presents that failed back to back; zero once one succeeds.</summary>
    public int PresentFailuresInARow { get; private set; }

    /// <summary>The first present failure of the session, kept so it can be reported once.</summary>
    public string? LastPresentError { get; private set; }

    /// <summary>True once <see cref="EnableAudio"/> has wired up the sound output for the session.</summary>
    public bool AudioReady => _audioReady;

    /// <summary>Human-readable note on why audio was/wasn't wired up (for the UI log).</summary>
    public string AudioDiagnostic { get; private set; } = "";

    /// <summary>
    /// Whether EmuHawk's sound device is currently running, and how full our ring is.
    ///
    /// Cheap enough to read on a slow tick, and between them they identify a failure the
    /// per-second audio line only shows in hindsight: EmuHawk stops its device across a window
    /// minimize/restore, <see cref="PumpAudio"/> early-returns while it is stopped, and since the
    /// pump is what drains the ring, the ring climbs to full and stays there. Observed once in
    /// real play — 300 frames with the pump counter frozen and the ring pegged at capacity,
    /// immediately followed by the frame tick collapsing to 9/s.
    /// </summary>
    public bool AudioDeviceStarted
    {
        get { try { return _audioReady && _sound != null && _sound.IsStarted; } catch { return false; } }
    }

    /// <summary>Ring occupancy as a fraction, or -1 if there is no ring to report on.</summary>
    public double AudioRingFullness
    {
        get
        {
            try
            {
                if (_soundBuffer == null) return -1;
                int cap = _soundBuffer.Capacity;
                return cap <= 0 ? -1 : (double)_soundBuffer.Count / cap;
            }
            catch { return -1; }
        }
    }

    /// <summary>Pipeline counters for diagnosing silence: samples produced vs pumped vs buffered.</summary>
    public string AudioStats()
    {
        int ring = _soundBuffer?.Count ?? -1;
        int cap = _soundBuffer?.Capacity ?? 0;
        string err = string.IsNullOrEmpty(_audioSyncErr) ? "" : $" drainErr='{_audioSyncErr}'";
        return $"audio stats: coreMode={(_coreSyncSound ? "Sync" : "Async")} frames={_audioFrames} " +
               $"pairsProduced={_audioPairs} pumps={_audioPumps} ring={ring}/{cap} " +
               // "shorts peak=N" read as short READS — underruns — when it is the loudest PCM sample
               // seen, the check for "is the core producing sound at all". Named against full scale
               // so it cannot be mistaken for a count of anything.
               // Clamped: |short.MinValue| is 32768, one past short.MaxValue, so a sample at the
               // negative rail printed as "32768/32767" — a fraction over its own maximum.
               $"peakSample={Math.Min(_audioPeak, short.MaxValue)}/{short.MaxValue}" +
               $"{(AudioRevivals > 0 ? $" revivals={AudioRevivals}" : "")}" +
               $"{(AudioRebinds > 0 ? $" rebinds={AudioRebinds}" : "")}{err}";
    }

    private void UpdatePeak(short[] buf, int shorts)
    {
        if (buf == null) return;
        // Every 16th sample, not every sample. This answers exactly one question — "is the core
        // producing sound at all" — for a diagnostic printed once at frame 120 and then only under
        // Verbose. Scanning all ~1,470 shorts of every frame for that was ~88,000 compares a
        // second inside the frame step, billed to coreMs; a 16-stride still cannot miss real audio
        // (any tone spans hundreds of consecutive samples) and only mis-reports the exact peak,
        // which nothing consumes as a number.
        int n = Math.Min(shorts, buf.Length);
        for (int i = 0; i < n; i += 16)
        {
            int a = buf[i]; if (a < 0) a = -a;
            if (a > _audioPeak) _audioPeak = a;
        }
    }

    private int SamplesPerFrame()
    {
        double fps = 60.0;
        try
        {
            var vp = _emulator.ServiceProvider.GetService<IVideoProvider>();
            if (vp != null && vp.VsyncNumerator > 0 && vp.VsyncDenominator > 0)
                fps = (double)vp.VsyncNumerator / vp.VsyncDenominator;
        }
        catch { /* fall back to 60 */ }
        int rate = _sound?.SampleRate ?? 44100;
        return Math.Max(1, (int)Math.Round(rate / fps));
    }

    /// <summary>
    /// Wire up audio for a driven session. We keep EmuHawk paused and step the core ourselves, but
    /// EmuHawk's run loop keeps calling <c>Sound.UpdateSound</c> every iteration — with atten=0
    /// while paused, which discards buffered samples and writes silence to the device. So we (1)
    /// grab EmuHawk's Sound and its host audio device (both via reflection), (2) NULL EmuHawk's
    /// input pin so its paused <c>UpdateSound</c> early-returns and never touches the device, and
    /// (3) feed the device directly from a ring we own (<see cref="NetplaySoundBuffer"/>): filled
    /// from the core each frame in <see cref="DrainCoreAudio"/>, drained to the device each tick in
    /// <see cref="PumpAudio"/>. <see cref="DisableAudio"/> restores EmuHawk's wiring on session end.
    /// </summary>
    /// <summary>
    /// Hand the adapter EmuHawk's MainForm.
    ///
    /// Separate from <see cref="EnableAudio"/> because the input capture path wants it too, and it
    /// used to arrive only through there — so a session whose audio was unavailable also silently
    /// lost the fast capture path, for no related reason.
    /// </summary>
    public void AttachMainForm(BizHawk.Client.EmuHawk.MainForm? mainForm)
    {
        if (mainForm != null) _mainForm = mainForm;
    }

    public void EnableAudio(BizHawk.Client.EmuHawk.MainForm? mainForm)
    {
        _audioReady = false;
        AudioDiagnostic = "";
        _audioFrames = _audioPairs = _audioPumps = 0;
        _audioPeak = 0;
        AudioRebinds = 0;
        _audioSyncErr = "";
        try
        {
            if (mainForm == null) { AudioDiagnostic = "no MainForm reference"; return; }
            _mainForm = mainForm;

            // MainForm.Sound is a PRIVATE property (the type Sound is public), so reflect it once.
            var prop = typeof(BizHawk.Client.EmuHawk.MainForm)
                .GetProperty("Sound", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _sound = prop?.GetValue(mainForm) as BizHawk.Client.EmuHawk.Sound;
            if (_sound == null) { AudioDiagnostic = "couldn't reach MainForm.Sound"; return; }

            // The host audio device (XAudio2/OpenAL). Sound._outputDevice is private; we drive it directly.
            var devField = typeof(BizHawk.Client.EmuHawk.Sound)
                .GetField("_outputDevice", BindingFlags.Instance | BindingFlags.NonPublic);
            _outputDevice = devField?.GetValue(_sound) as ISoundOutput;
            if (_outputDevice == null) { AudioDiagnostic = "couldn't reach Sound._outputDevice"; return; }

            _coreSound = _emulator.ServiceProvider.GetService<ISoundProvider>();
            if (_coreSound == null) { AudioDiagnostic = "core exposes no ISoundProvider"; return; }

            // Prefer draining the core synchronously (one frame's samples per FrameAdvance). If the
            // core won't switch to sync, drain it in async mode instead — either way we feed our
            // ring. Forcing sync on an async-only core and then GetSamplesSync'ing it yields silence,
            // which is the bug this replaces.
            if (_coreSound.SyncMode != SyncSoundMode.Sync)
            {
                try { _coreSound.SetSyncMode(SyncSoundMode.Sync); } catch { /* async core */ }
            }
            _coreSyncSound = _coreSound.SyncMode == SyncSoundMode.Sync;

            // Make sure the device is running before we touch its input pin (so a bail leaves
            // EmuHawk's wiring untouched). StartSound no-ops if the user disabled sound.
            if (!_sound.IsStarted) _sound.StartSound();
            if (!_sound.IsStarted) { AudioDiagnostic = "EmuHawk sound output is off (enable Config → Sound)"; return; }

            _soundChannels = _sound.ChannelCount;
            _asyncScratch = new short[Math.Max(_soundChannels, SamplesPerFrame() * _soundChannels)];
            _soundBuffer = new NetplaySoundBuffer(_sound.SampleRate, _soundChannels, capacityMs: 400);
            // Prime a standing cushion of silence so pump jitter / brief network hitches don't
            // underrun the ring (which would inject audible silence).
            //
            // This cushion is a PERMANENT offset, not a startup cost: sound plays this far behind the
            // frame that produced it for the whole session, and players feel that as input lag even
            // though the simulation is unaffected. It was 80ms — more than the entire input delay of a
            // tuned rollback session — sized when audio was pumped from a coarse, coalesced WM_TIMER.
            // The tick now runs at 2ms with timeBeginPeriod(1) and pumps every tick, so the jitter it
            // covers is far smaller. AudioStats() reports the ring's fill level: if it never
            // approaches empty during play, this can come down further.
            int prime = _sound.SampleRate * _soundChannels * AudioPrimeMs / 1000;
            _soundBuffer.Enqueue(new short[prime], prime);

            // Detach EmuHawk's input pin so its run-loop UpdateSound(atten=0, while we hold it paused)
            // early-returns instead of discarding our audio and writing silence to the device. From
            // here PumpAudio owns the device.
            _sound.SetInputPin(null);
            _audioReady = true;

            string cfg = "";
            try
            {
                var config = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference;
                if (config != null)
                    cfg = $" out={config.SoundOutputMethod} vol={config.SoundVolume} throttle={config.SoundThrottle} enabled={config.SoundEnabled} bufMs={config.SoundBufferSizeMs}";
            }
            catch { }
            AudioDiagnostic = $"core={_coreSound.GetType().Name} mode={(_coreSyncSound ? "Sync" : "Async")} " +
                              $"rate={_sound.SampleRate} ch={_soundChannels} started={_sound.IsStarted}{cfg}";
        }
        catch (Exception ex) { _sound = null; AudioDiagnostic = "audio init failed: " + ex.Message; }
    }

    /// <summary>
    /// Restore EmuHawk's own audio wiring so normal sound resumes after the session. Prefers
    /// EmuHawk's <c>RewireSound</c> (re-establishes the correct pin per core/config); falls back to
    /// re-pinning the core provider directly.
    /// </summary>
    public void DisableAudio()
    {
        _audioReady = false;
        if (_sound == null) return;
        try
        {
            _soundBuffer?.DiscardSamples();
            var rewire = typeof(BizHawk.Client.EmuHawk.MainForm)
                .GetMethod("RewireSound", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_mainForm != null && rewire != null) rewire.Invoke(_mainForm, null);
            else if (_coreSound != null) _sound.SetInputPin(_coreSound);
        }
        catch { try { if (_coreSound != null) _sound.SetInputPin(_coreSound); } catch { } }
    }

    /// <summary>
    /// The master volume, as EmuHawk would apply it.
    ///
    /// Volume is NOT baked into the samples a core produces, whatever the comment here used to say:
    /// EmuHawk computes an attenuation and hands it to the output device
    /// (<c>MainForm</c> line ~2960, <c>Sound.UpdateSound</c> -> <c>ApplyVolumeSettings</c>). We drive
    /// that device ourselves during a session, so passing 1.0 meant a session ignored the volume
    /// slider and the mute checkbox entirely and always played at full.
    ///
    /// Only the normal-playback branch of EmuHawk's formula applies: rewind/fast-forward and
    /// frame-advance muting are frontend modes a session never enters. Falls back to full volume if
    /// the config is unreachable, which is what it did before and is the audible-rather-than-silent
    /// direction to fail in.
    /// </summary>
    private double HostAttenuation()
    {
        var cfg = _hostConfig;
        if (cfg == null) return 1.0;
        try
        {
            // Both switches, not just the normal-play one. SoundEnabled is the master mute, which
            // EmuHawk honours in UpdateSound rather than in the attenuation it passes — so a session
            // reading only SoundEnabledNormal kept playing through a master mute, since we bypass
            // UpdateSound entirely.
            if (!cfg.SoundEnabled || !cfg.SoundEnabledNormal) return 0.0;
            double atten = cfg.SoundVolume / 100.0;
            return atten < 0 ? 0.0 : atten > 1 ? 1.0 : atten;
        }
        catch { return 1.0; }
    }
}
