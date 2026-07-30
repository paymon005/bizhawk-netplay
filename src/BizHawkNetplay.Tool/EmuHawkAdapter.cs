using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BizHawk.Client.Common;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;
using CoreLayout = BizHawkNetplay.Core.Input.ControllerLayout;
using CoreAxisSpec = BizHawkNetplay.Core.Input.AxisSpec;

namespace BizHawkNetplay.Tool;

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
    private InputSetController? _controller; // reused every frame; see Controller()
    private readonly string[][] _bindings; // [port][buttonIndex] -> host-input binding string
    private readonly AnalogBind?[][] _analogBinds; // [port][axisIndex] -> host analog binding (null if unbound)
    private readonly bool[][] _axisReversed;      // [port][axisIndex] -> core axis IsReversed flag
    // The N64 stick gate. EmuHawk applies this right after latching physical input; see
    // ApplyAxisConstraints for what its absence did to analog magnitude.
    private readonly bool _useCircularAnalogConstraint;
    private readonly Dictionary<string, int>? _constraintScratch;

    // Raw main-memory access for the periodic desync checksum, resolved lazily (see HashMainMemory).
    private IMemoryDomains? _memoryDomains;
    private bool _memoryDomainsResolved;
    private byte[] _hashScratch = Array.Empty<byte>();
    private PropertyInfo? _domainDataProp;
    private bool _domainDataResolved;

    // Audio: we drive EmuHawk's Sound output ourselves (see EnableAudio / AdvanceFrame / PumpAudio).
    private BizHawk.Client.EmuHawk.Sound? _sound;
    private BizHawk.Client.EmuHawk.MainForm? _mainForm;
    private ISoundOutput? _outputDevice;             // EmuHawk's host audio device, driven directly
    private ISoundProvider? _coreSound;
    private NetplaySoundBuffer? _soundBuffer;
    private short[] _pumpScratch = Array.Empty<short>();
    private int _soundChannels = 2;
    // Standing audio cushion in ms — a permanent video→audio offset, so keep it as small as the
    // pump jitter allows (see EnableAudio). ~2.5 frames at 60fps.
    private const int AudioPrimeMs = 40;
    private bool _audioReady;
    private bool _coreSyncSound = true;              // drain via GetSamplesSync (else GetSamplesAsync)
    private short[] _asyncScratch = Array.Empty<short>();
    // Diagnostics so a single test round shows where the audio pipeline breaks.
    private long _audioFrames, _audioPairs, _audioPumps;
    // Restart bookkeeping for a device that stopped under us — see PumpAudio's revival path.
    private readonly System.Diagnostics.Stopwatch _audioRevive = System.Diagnostics.Stopwatch.StartNew();
    private double _lastReviveAttemptMs = double.NegativeInfinity;
    private const double ReviveIntervalMs = 500; // don't hammer StartSound every tick
    public int AudioRevivals { get; private set; }
    private int _audioPeak;               // max abs sample seen (0 => core handed us silence)
    private string _audioSyncErr = "";

    public EmuHawkAdapter(ApiContainer apis, IEmulator emulator, IStatable statable)
    {
        _apis = apis ?? throw new ArgumentNullException(nameof(apis));
        _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
        _statable = statable ?? throw new ArgumentNullException(nameof(statable));
        _layouts = BuildLayouts(emulator.ControllerDefinition);
        _bindings = BuildBindings();
        _analogBinds = BuildAnalogBinds();
        _axisReversed = BuildAxisReversed();
        _useCircularAnalogConstraint = ReadCircularAnalogConstraintSetting();
        if (_useCircularAnalogConstraint)
            _constraintScratch = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>True if we found the user's controller bindings (needed to capture input).</summary>
    public bool HasBindings
    {
        get
        {
            foreach (var port in _bindings)
                foreach (var b in port)
                    if (!string.IsNullOrEmpty(b)) return true;
            return false;
        }
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

    /// <summary>BizHawk system identifier (for conservative per-system netplay defaults).</summary>
    public string SystemId => _emulator.SystemId;

    // Identity = core + version + system + the core's REAL sync-settings blob. The blob is what
    // makes two peers on the same core but different per-core settings (e.g. an N64 video plugin, a
    // region, a CPU-core choice) fail the handshake up front instead of silently desyncing later.
    // Both peers run the identical core build (the handshake already requires matching CoreVersion),
    // so the same settings serialize to the same JSON and hash equal — while any real difference
    // diverges. Best-effort: if the settings can't be read, the blob is empty and this degrades to
    // the old coarse core+version+system digest (no false mismatch).
    public string SyncSettingsDigest
    {
        get
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(
                CoreName + "|" + CoreVersion + "|" + _emulator.SystemId + "|" + SyncSettingsBlob()));
            return BitConverter.ToString(bytes, 0, 8).Replace("-", string.Empty);
        }
    }

    /// <summary>
    /// The core's live sync settings serialized to JSON, or "" if the core exposes none / it can't be
    /// read. Read straight from the core via <c>ISettable&lt;,&gt;.GetSyncSettings()</c> (the authoritative,
    /// both-peers-symmetric source) rather than the config dict, so a value the user just changed and a
    /// value loaded from disk can't serialize differently for the same logical settings.
    /// </summary>
    /// <summary>
    /// The video settings that change what ends up in main RAM without being part of the sync
    /// settings — so the handshake never compares them and a desync is the first anyone hears.
    ///
    /// On N64 the render resolution lives in <c>N64Settings.VideoSizeX/Y</c>, which is the ordinary
    /// settings object, while the plugin choice and its framebuffer options live in
    /// <c>N64SyncSettings</c> and ARE compared. Above native resolution the plugin resolves its
    /// framebuffer back into RDRAM and those bytes come from the GPU, so two machines disagree even
    /// with byte-identical settings on both — measured as a desync at every single checksum at
    /// 800x600, and none at all at native, in lockstep and rollback alike.
    ///
    /// Reported rather than enforced: this reads whatever the loaded core happens to expose, and
    /// what counts as "too high" is a property of the game and plugin, not something worth guessing
    /// at from here. Returns a bare summary like "800x600, plugin Rice (InN64Resolution=False)" so
    /// callers can quote it in their own wording. Null when the core has no such setting.
    /// </summary>
    public string? VideoSettingsDiagnostic()
    {
        try
        {
            var settable = _emulator.GetType().GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition().FullName == "BizHawk.Emulation.Common.ISettable`2");
            var settings = settable?.GetMethod("GetSettings")?.Invoke(_emulator, null);
            if (settings == null) return null;

            var x = MemberValue(settings, "VideoSizeX");
            var y = MemberValue(settings, "VideoSizeY");
            if (x == null || y == null) return null;

            var sync = settable!.GetMethod("GetSyncSettings")?.Invoke(_emulator, null);
            var plugin = sync == null ? null : MemberValue(sync, "VideoPlugin");
            if (plugin == null) return $"{x}x{y}";

            // Whether the plugin renders at the console's own resolution is the setting that decides
            // whether those framebuffer bytes came off the GPU at all, so it belongs next to the size.
            // It lives on the selected plugin's own settings object, which each plugin names for
            // itself; the two that have one disagree even on what to call it. Absent for the rest.
            var pluginSettings = MemberValue(sync!, plugin + "Plugin");
            if (pluginSettings != null)
                foreach (var flag in new[] { "InN64Resolution", "UseNativeResolutionFactor" })
                {
                    var value = MemberValue(pluginSettings, flag);
                    if (value != null) return $"{x}x{y}, plugin {plugin} ({flag}={value})";
                }
            return $"{x}x{y}, plugin {plugin}";
        }
        catch { return null; } // a settings read must never disturb starting a session
    }

    /// <summary>
    /// Read a public instance member by name, whether it is a property or a field.
    ///
    /// BizHawk's settings objects mix the two freely — N64Settings exposes UseMupenStyleLag as a
    /// property and VideoSizeX/VideoSizeY as bare fields — so a property-only lookup returns null
    /// for exactly the values worth reporting, and does it silently.
    /// </summary>
    private static object? MemberValue(object target, string name)
    {
        var type = target.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property != null) return property.GetValue(target);
        return type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
    }

    private string SyncSettingsBlob()
    {
        try
        {
            var settable = _emulator.GetType().GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition().FullName == "BizHawk.Emulation.Common.ISettable`2");
            var syncSettings = settable?.GetMethod("GetSyncSettings")?.Invoke(_emulator, null);
            if (syncSettings == null) return "";
            return Newtonsoft.Json.JsonConvert.SerializeObject(syncSettings);
        }
        catch { return ""; } // never let a settings read break the handshake — fall back to the coarse digest
    }

    public bool VerifyDeterministicMode() => _emulator.DeterministicEmulation;

    // --- Input --------------------------------------------------------------------

    /// <summary>
    /// Ports a session may actually use. This counted layout slots, including empty ones that
    /// serialize to zero bytes — see <see cref="UsablePorts"/> for what that cost a 3-player NES
    /// session. Every caller wants the usable figure: the player-count cap, the host's minimum
    /// check, and the per-port layout dump all break in the same way on a slot that carries no input.
    /// </summary>
    public int PortCount => UsablePorts(_layouts);

    /// <summary>Total layout slots the core declared, usable or not. Diagnostic only — reported at
    /// session start so a core that declares more ports than it populates is visible.</summary>
    public int DeclaredPortCount => _layouts.Length;

    public CoreLayout GetControllerLayout(int port) => _layouts[port];

    /// <summary>
    /// DIAGNOSTIC: when true, ReadLocalInput returns neutral and never touches the pad. Proved
    /// the no-input session holds sync (isolating netcode/core from input). Left as a toggle
    /// for future debugging; normal play reads the real pad.
    /// </summary>
    public static bool ForceNeutralInput = false;

    /// <summary>
    /// Which EmuHawk controller port's bindings to actually read the local pad from. -1 (default)
    /// reads the assigned port's own bindings. Set it to e.g. 0 so a player assigned P2/P3/P4 still
    /// uses their normal P1 controls with no rebinding — same-console ports share button order, so
    /// the source bindings map onto the assigned port's layout by index.
    /// </summary>
    public int InputSourcePort { get; set; } = -1;

    public PortInput ReadLocalInput(int port)
    {
        var layout = _layouts[port];
        if (ForceNeutralInput)
            return PortInput.Neutral(layout);

        // Read raw host input directly (IInputApi.Get works while the emulator is paused and does
        // NOT run EmuHawk's controller/hotkey chain), then resolve to core buttons via the user's
        // bindings. Input capture thus stays entirely out of the emulation path — the core only
        // ever sees the merged inputs we feed in AdvanceFrame, so there is no physical leak, no
        // hotkey firing, and both peers stay deterministic.
        var pressed = new HashSet<string>(_apis.Input.GetPressedButtons());
        // Optionally source the pad from a different port's bindings, but only if that port's layout
        // has the same button set (guards mixed-layout cores); otherwise fall back to the assigned port.
        var binds = _bindings[port];
        int src = InputSourcePort;
        if (src >= 0 && src < _bindings.Length && _bindings[src].Length == layout.Buttons.Count)
            binds = _bindings[src];
        var buttons = new bool[layout.Buttons.Count];
        for (int i = 0; i < buttons.Length; i++)
            buttons[i] = EvaluateBinding(binds[i], pressed);

        var axes = new int[layout.Axes.Count];
        if (axes.Length > 0)
        {
            // Read live host analog axes — paused-safe via the same host coalescer GetPressedButtons
            // uses (values are in BizHawk's ±10000 convention) — and apply BizHawk's own analog-bind
            // math so the captured value matches exactly what local play would feed the core. Same
            // source-port remap as buttons, so a player assigned P2/P3/P4 uses their P1 stick.
            int axSrc = (src >= 0 && src < _analogBinds.Length && _analogBinds[src].Length == axes.Length) ? src : port;
            IReadOnlyDictionary<string, int>? hostAxes;
            try { hostAxes = _apis.Input.GetPressedAxes(); }
            catch { hostAxes = null; } // ResolveAxis treats null as "no live axes" (neutral)
            for (int j = 0; j < axes.Length; j++)
                axes[j] = ResolveAxis(_analogBinds[axSrc][j], layout.Axes[j], _axisReversed[port][j], hostAxes, pressed);
            ApplyAxisConstraints(layout, axes);
        }
        return new PortInput(buttons, axes);
    }

    // The real session injects inputs by stepping the core with a controller built from the
    // merged InputSet (AdvanceFrame), not via Joypad.Set — so this is unused.
    /// <summary>
    /// Deliberately empty. A BizHawk core reads its input from the <c>IController</c> handed INTO
    /// <c>FrameAdvance</c>, so there is no "set them now, step later" state to hold — the merged
    /// set travels with the frame in <see cref="AdvanceFrame"/>. See <see cref="IEmuAdapter.SetInputs"/>.
    /// </summary>
    public void SetInputs(InputSet inputs) { }

    /// <summary>
    /// One frame with video rendered, for the capability probe's live-frame measurement.
    ///
    /// Deliberately not <see cref="AdvanceFrame"/>: that drains the core's samples into the session
    /// audio ring, and the probe runs outside the session's audio lifecycle entirely.
    ///
    /// Sound is still rendered, because a live frame renders it and the whole point is to time a
    /// live frame — but then the samples must go somewhere. Left in the core they pile up and reach
    /// EmuHawk's own output as a backlog, which it drains by playing faster: the music jumps in
    /// pitch after every probe and slides back down as it catches up, and stacks higher if you probe
    /// again before it has. Discarding is both the fix and the more faithful measurement, since the
    /// session empties the core after every frame too.
    /// </summary>
    public void AdvanceRenderedFrame(InputSet inputs)
    {
        _emulator.FrameAdvance(Controller(inputs), render: true, renderSound: true);
        try
        {
            _coreSound ??= _emulator.ServiceProvider.GetService<ISoundProvider>();
            _coreSound?.DiscardSamples();
        }
        catch { /* a probe must never be the thing that breaks audio */ }
    }

    /// <summary>
    /// Step the core exactly one frame using <paramref name="inputs"/> as the ONLY input source
    /// (bypasses EmuHawk's input chain and hotkeys). Identical inputs on both peers therefore
    /// produce identical state — the proven-deterministic stepping path.
    /// </summary>
    public void AdvanceFrame(InputSet inputs, bool renderVideo = true)
    {
        // renderVideo=false skips only the video render — used for the throwaway intermediate frames
        // of a catch-up burst (their picture is never shown), which lets a heavy core recover from a
        // hitch faster. Sound is always rendered so audio stays continuous, and emulation is identical.
        _emulator.FrameAdvance(Controller(inputs), render: renderVideo, renderSound: true);

        // Drain the samples the core just generated into our ring buffer. EmuHawk's audio device
        // pulls from that ring at the steady real-time rate (PumpAudio → UpdateSound), so the ring
        // absorbs the mismatch between this bursty manual stepping and smooth playback. Producing
        // here and consuming there is what keeps audio clean despite the coarse WinForms timer.
        // Drain in whichever mode the core actually reports — a sync-only drain of an async core
        // gets nothing (silence).
        if (_audioReady)
        {
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
    public void PumpAudio()
    {
        if (!_audioReady) return;
        var dev = _outputDevice;
        var snd = _sound;
        if (dev == null || snd == null || _soundBuffer == null) return;

        // EmuHawk disposes and recreates its audio device across a window minimize/restore
        // (Sound.StopSound/StartSound). While it's stopped, skip — do NOT give up permanently, or
        // audio would stay dead until a reconnect. When EmuHawk restarts the device we resume.
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
            dev.ApplyVolumeSettings(1.0); // full volume; the user's master volume is baked into the samples' source
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

            var devField = typeof(BizHawk.Client.EmuHawk.Sound)
                .GetField("_outputDevice", BindingFlags.Instance | BindingFlags.NonPublic);
            if (devField?.GetValue(snd) is ISoundOutput fresh) _outputDevice = fresh;
            AudioRevivals++;
        }
        catch { /* try again on the next interval; never let this kill the frame tick */ }
    }

    /// <summary>
    /// Blit the core's freshly-rendered frame to EmuHawk's window. We hold EmuHawk paused and drive
    /// frame-advance ourselves, so its own run loop never presents — a paused window keeps showing
    /// the last frame its swapchain holds, which is why the host's picture froze while emulation
    /// (audio, netplay) kept running. MainForm.Render() re-presents the current video provider; we
    /// call it once per tick after stepping, the video twin of <see cref="PumpAudio"/>. Best-effort.
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
               $"pairsProduced={_audioPairs} pumps={_audioPumps} ring={ring}/{cap} shorts peak={_audioPeak}" +
               $"{(AudioRevivals > 0 ? $" revivals={AudioRevivals}" : "")}{err}";
    }

    private void UpdatePeak(short[] buf, int shorts)
    {
        if (buf == null) return;
        int n = Math.Min(shorts, buf.Length);
        for (int i = 0; i < n; i++)
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
    /// from the core each frame in <see cref="AdvanceFrame"/>, drained to the device each tick in
    /// <see cref="PumpAudio"/>. <see cref="DisableAudio"/> restores EmuHawk's wiring on session end.
    /// </summary>
    public void EnableAudio(BizHawk.Client.EmuHawk.MainForm? mainForm)
    {
        _audioReady = false;
        AudioDiagnostic = "";
        _audioFrames = _audioPairs = _audioPumps = 0;
        _audioPeak = 0;
        _audioSyncErr = "";
        try
        {
            if (mainForm == null) { AudioDiagnostic = "no MainForm reference"; return; }
            _mainForm = mainForm;

            // MainForm.Sound is a public type but has a private getter, so reflect it once up front.
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
    /// True if the host-input expression <paramref name="binding"/> is satisfied by the pressed
    /// set. Handles comma-separated alternatives and '+'-separated combos (the common BizHawk
    /// forms); exotic modifiers may not resolve (M2).
    /// </summary>
    private static bool EvaluateBinding(string binding, HashSet<string> pressed)
    {
        if (string.IsNullOrEmpty(binding)) return false;
        foreach (var alt in binding.Split(','))
        {
            var one = alt.Trim();
            if (one.Length == 0) continue;
            bool all = true;
            foreach (var part in one.Split('+'))
            {
                var key = part.Trim();
                if (key.Length == 0) continue;
                if (!pressed.Contains(key)) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    /// <summary>Human-readable note on why bindings were/weren't found (for the UI log).</summary>
    public string BindingDiagnostic { get; private set; } = "";

    /// <summary>
    /// One-shot input diagnostic for the UI: what controller/bindings we found, and what host
    /// inputs are pressed right now vs how they resolve to P1 buttons.
    /// </summary>
    public string DescribeInputState()
    {
        var sb = new StringBuilder();
        sb.Append("controllerDef='").Append(_emulator.ControllerDefinition.Name)
          .Append("', system='").Append(_emulator.SystemId).Append("'\n");
        sb.Append("bindings: ").Append(HasBindings ? "FOUND" : "MISSING");
        if (!string.IsNullOrEmpty(BindingDiagnostic)) sb.Append("  ").Append(BindingDiagnostic);
        sb.Append('\n');

        if (_layouts.Length > 0)
        {
            var l0 = _layouts[0];
            for (int i = 0; i < l0.Buttons.Count && i < 8; i++)
                sb.Append("  ").Append(l0.Buttons[i]).Append(" <- '").Append(_bindings[0][i]).Append("'\n");
        }

        IReadOnlyList<string> pressed;
        try { pressed = _apis.Input.GetPressedButtons(); }
        catch (Exception ex) { return sb.Append("GetPressedButtons error: ").Append(ex.Message).ToString(); }
        sb.Append("pressed host inputs now: [").Append(string.Join(", ", pressed)).Append("]\n");

        if (_layouts.Length > 0)
        {
            var set = new HashSet<string>(pressed);
            var l0 = _layouts[0];
            var on = new List<string>();
            for (int i = 0; i < l0.Buttons.Count; i++)
                if (EvaluateBinding(_bindings[0][i], set)) on.Add(l0.Buttons[i]);
            sb.Append("resolved P1 pressed: [").Append(string.Join(", ", on)).Append("]");
        }
        return sb.ToString();
    }

    private string[][] BuildBindings()
    {
        Dictionary<string, string>? map = null;
        try
        {
            var config = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference;
            if (config != null)
            {
                // Config.AllTrollers is keyed by the controller-definition name, not the system id.
                var defName = _emulator.ControllerDefinition.Name;
                if (!config.AllTrollers.TryGetValue(defName, out map))
                    config.AllTrollers.TryGetValue(_emulator.SystemId, out map);
                if (map == null)
                    BindingDiagnostic = $"no bindings for '{defName}' or '{_emulator.SystemId}'. " +
                        $"available: [{string.Join(", ", config.AllTrollers.Keys)}]";
            }
            else BindingDiagnostic = "config reference unavailable";
        }
        catch (Exception ex) { BindingDiagnostic = "binding lookup error: " + ex.Message; }

        var result = new string[_layouts.Length][];
        for (int p = 0; p < _layouts.Length; p++)
        {
            var layout = _layouts[p];
            var arr = new string[layout.Buttons.Count];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = (map != null && map.TryGetValue(layout.Buttons[i], out var b)) ? b : "";
            result[p] = arr;
        }
        return result;
    }

    /// <summary>Per-axis host analog bindings (host axis + mult/deadzone/button binds), from the user's
    /// EmuHawk analog config. Null entries are unbound axes (captured as neutral).</summary>
    private AnalogBind?[][] BuildAnalogBinds()
    {
        Dictionary<string, AnalogBind>? map = null;
        try
        {
            var config = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference;
            config?.AllTrollersAnalog.TryGetValue(_emulator.ControllerDefinition.Name, out map);
        }
        catch { /* leave unbound -> neutral axes */ }

        var result = new AnalogBind?[_layouts.Length][];
        for (int p = 0; p < _layouts.Length; p++)
        {
            var axes = _layouts[p].Axes;
            var arr = new AnalogBind?[axes.Count];
            for (int j = 0; j < arr.Length; j++)
                arr[j] = (map != null && map.TryGetValue(axes[j].Name, out var b)) ? b : (AnalogBind?)null;
            result[p] = arr;
        }
        return result;
    }

    /// <summary>
    /// EmuHawk's <c>N64UseCircularAnalogConstraint</c>, the setting that decides whether the stick
    /// gate is applied at all. Read by name off the same config object the analog binds come from,
    /// because it is not on any interface we are handed. Defaults to TRUE when it cannot be read:
    /// that is both EmuHawk's own default and the safer direction, since applying the gate can only
    /// bring a value back inside what the hardware could produce.
    /// </summary>
    private bool ReadCircularAnalogConstraintSetting()
    {
        try
        {
            var config = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference;
            if (config == null) return true;
            var prop = config.GetType().GetProperty("N64UseCircularAnalogConstraint");
            if (prop?.GetValue(config) is bool b) return b;
        }
        catch { }
        return true;
    }

    /// <summary>The core's IsReversed flag per axis (BizHawk applies it when mapping host -> core).</summary>
    private bool[][] BuildAxisReversed()
    {
        var def = _emulator.ControllerDefinition;
        var result = new bool[_layouts.Length][];
        for (int p = 0; p < _layouts.Length; p++)
        {
            var axes = _layouts[p].Axes;
            var arr = new bool[axes.Count];
            for (int j = 0; j < arr.Length; j++) // names come from def.Axes, so the indexer is safe
                arr[j] = def.Axes[axes[j].Name].IsReversed;
            result[p] = arr;
        }
        return result;
    }

    /// <summary>
    /// The step that follows LatchFromPhysical in EmuHawk's own input path, and that this adapter
    /// was missing: the N64's stick is round, and its gate clamps the (X,Y) VECTOR to radius 127.
    ///
    /// Without it a diagonal reaches magnitude √(127² + 127²) ≈ 180 — 42% past anything the real
    /// hardware can produce. A game that reads the stick's magnitude (throw power, run speed, a
    /// charge meter) then saturates its top bucket at about 70% deflection, and with a 0.1 deadzone
    /// eating the bottom there is no travel left in between: every input reads as minimum or
    /// maximum. That is precisely the symptom this was found from, on a game whose throw distance
    /// is chosen by how far the stick is flicked.
    ///
    /// It runs on capture rather than on injection so the value that goes on the wire is the value
    /// the core will see, which keeps a peer's view of an input identical to its author's.
    ///
    /// Uses BizHawk's own <see cref="ControllerDefinition.ApplyAxisConstraints"/> rather than
    /// reimplementing the clamp. Reimplementing the analog-bind math above was already a standing
    /// risk of silent drift; there was no reason to take it twice.
    /// </summary>
    private void ApplyAxisConstraints(CoreLayout layout, int[] axes)
    {
        if (!_useCircularAnalogConstraint) return;
        var scratch = _constraintScratch;
        if (scratch == null) return;

        scratch.Clear();
        for (int j = 0; j < axes.Length; j++) scratch[layout.Axes[j].Name] = axes[j];
        try { _emulator.ControllerDefinition.ApplyAxisConstraints("Natural Circle", scratch); }
        catch { return; }   // a core without constraints must not cost anyone their input
        for (int j = 0; j < axes.Length; j++)
            if (scratch.TryGetValue(layout.Axes[j].Name, out int v)) axes[j] = v;
    }

    /// <summary>
    /// Turn a live host analog reading into a core axis value, mirroring BizHawk's own analog-bind
    /// math (Controller.cs): host value ÷10000 → [-1,1], deadzone, ×Mult, scale to the axis half-range,
    /// shift to Neutral, clamp. Kept identical so a captured value equals what local play would inject.
    /// </summary>
    private static int ResolveAxis(AnalogBind? bindN, CoreAxisSpec spec, bool reversed,
        IReadOnlyDictionary<string, int>? hostAxes, HashSet<string> pressedButtons)
    {
        if (bindN == null) return spec.Neutral;
        var bind = bindN.Value;

        float value = 0f;
        if (!string.IsNullOrEmpty(bind.ButtonBindPositive) && EvaluateBinding(bind.ButtonBindPositive, pressedButtons)) value += 1f;
        if (!string.IsNullOrEmpty(bind.ButtonBindNegative) && EvaluateBinding(bind.ButtonBindNegative, pressedButtons)) value -= 1f;
        if (value == 0f)
        {
            int host = 0;
            if (hostAxes != null && !string.IsNullOrEmpty(bind.Value)) hostAxes.TryGetValue(bind.Value, out host);
            value = host / 10000f;
        }

        float dz = bind.Deadzone;
        if (value < -dz) value += dz;
        else if (value < dz) value = 0f;
        else value -= dz;
        value = dz < 1f ? value / (1f - dz) : 0f;

        value *= bind.Mult;
        if (reversed) value = -value;
        value *= Math.Max(spec.Neutral - spec.Min, spec.Max - spec.Neutral);
        value += spec.Neutral;

        int result = (int)Math.Round(value);
        if (result < spec.Min) result = spec.Min;
        else if (result > spec.Max) result = spec.Max;
        return result;
    }

    // --- State --------------------------------------------------------------------

    // Declared nullable on the ApiContainer, but the tool refuses cores without savestate
    // support before this adapter is ever constructed — absence here would be a wiring bug,
    // so fail with a message instead of a bare NullReferenceException.
    private IMemorySaveStateApi MemorySave => _apis.MemorySaveState
        ?? throw new InvalidOperationException("MemorySaveState API unavailable (non-statable core?)");

    /// <summary>
    /// Buffers retired by <see cref="ReleaseState"/>, waiting to be written into again.
    ///
    /// Rollback used <c>IMemorySaveStateApi.SaveCoreStateToMemory()</c>, which clones the core
    /// into a brand-new array under a fresh GUID every time and offers no way to write into a
    /// buffer you already own. A session measured 33 of those a second — the elision rule only
    /// halves the rate while a peer is being predicted continuously, which is the normal case at
    /// any delay below the link's latency. At 787KiB each that is 26.5MB/s of allocation, every
    /// byte of it nine times over the Large Object Heap threshold, on a runtime (net48) that
    /// neither compacts the LOH nor reclaims it outside a blocking gen2.
    ///
    /// The bill arrived as gen2 collections landing inside the frame decision one to two times a
    /// second, and occasionally as a 52.5ms pause there with no repair running at all — a hitch
    /// no column could account for, because a collection is not work this code is doing.
    ///
    /// Reusing the buffers removes the allocation rather than tuning what collects it. The pool
    /// stabilizes at the ring's size within a second or two and then never allocates again.
    /// <see cref="IStatable"/> is the seam that makes it possible: it writes into a stream we own,
    /// and it is the same pair <see cref="ExportState"/> already round-trips for session start and
    /// resync, so the format is proven rather than newly trusted.
    /// </summary>
    private readonly Stack<MemoryStream> _statePool = new();

    /// <summary>Largest state seen, so a fresh buffer starts big enough to avoid growth copies.</summary>
    private int _stateSizeHint = 1 << 16;

    /// <summary>
    /// Cap on retained buffers. The ring keeps roughly maxRollback + margin states, so this is far
    /// above steady state and exists only so a pathological release burst cannot pin memory.
    /// </summary>
    private const int StatePoolCap = 64;

    public StateHandle SaveStateToMemory()
    {
        MemoryStream ms;
        if (_statePool.Count > 0) ms = _statePool.Pop();
        else { ms = new MemoryStream(_stateSizeHint); StateBuffersAllocated++; }
        ms.SetLength(0);
        ms.Position = 0;
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            _statable.SaveStateBinary(bw);
        if (ms.Length > _stateSizeHint) _stateSizeHint = (int)ms.Length;
        return new StateHandle(_emulator.Frame, ms);
    }

    public void LoadStateFromMemory(StateHandle handle)
    {
        var ms = (MemoryStream)handle.Token;
        ms.Position = 0;
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        _statable.LoadStateBinary(br);
    }

    public void ReleaseState(StateHandle handle)
    {
        // Retire the buffer for reuse rather than dropping it for the collector. Over the cap it
        // is simply let go, which is the old behaviour for that one buffer.
        if (!(handle.Token is MemoryStream ms)) return;
        if (_statePool.Count >= StatePoolCap) return;
        ms.SetLength(0);
        _statePool.Push(ms);
    }

    /// <summary>Drop every retired buffer. Called when a session ends so a long idle between
    /// sessions does not hold the ring's worth of memory for nothing.</summary>
    public void ClearStatePool()
    {
        while (_statePool.Count > 0) _statePool.Pop().Dispose();
    }

    /// <summary>Buffers currently retired and reusable. Reported so a session can show the pool
    /// reaching steady state — once it stops growing, the save path has stopped allocating.</summary>
    public int StatePoolSize => _statePool.Count;

    /// <summary>Buffers created since the session began. This is the number that matters: it should
    /// climb to the ring's size in the first second or two and then stop. If it keeps climbing, the
    /// pool is being outrun and the allocation this exists to remove is still happening.</summary>
    public int StateBuffersAllocated { get; private set; }

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
            _emulator.FrameAdvance(Controller(inputsFor(i)), render: false, renderSound: false);
    }

    /// <summary>
    /// The one controller this adapter presents to the core, refilled for <paramref name="inputs"/>.
    /// Rebuilt only when the core's controller definition or our layouts change identity — which is
    /// what a core reboot does, and the one case where a cached name map would be wrong.
    /// </summary>
    private InputSetController Controller(InputSet inputs)
    {
        var definition = _emulator.ControllerDefinition;
        if (_controller == null || !_controller.Matches(definition, _layouts))
            _controller = new InputSetController(definition, _layouts);
        _controller.Update(inputs);
        return _controller;
    }

    // --- Integrity ----------------------------------------------------------------

    /// <summary>
    /// 32-bit checksum over main memory for periodic desync detection.
    ///
    /// The obvious implementation — <c>IMemoryApi.HashRegion</c> — runs SHA over the whole domain,
    /// which on N64's 8MiB of RDRAM measured at ~38ms. That landed as a visible hitch every
    /// <c>ChecksumInterval</c> frames (every ~5 seconds), on the UI thread, in the middle of play.
    /// Nothing here needs a cryptographic digest: this only ever gets compared against the same
    /// number computed by a peer, so an FNV-1a over the same bytes does the same job for a
    /// fraction of the cost. The peer comparison is why the exact function is part of the wire
    /// contract — changing it requires a Protocol bump so mismatched builds refuse rather than
    /// reporting a phantom desync.
    ///
    /// Two ways in, because domains differ in what they expose: a raw block gets memcpy'd, anything
    /// else is read a word at a time (see HashByWord). They need not agree with each other — both
    /// peers run the same core, so both take the same path. Falls back to the portable API path if
    /// the domain service isn't reachable at all.
    /// </summary>
    public uint HashMainMemory(int salt = 0)
    {
        var domain = MainMemoryDomain();
        if (domain != null)
        {
            try
            {
                long size = domain.Size;
                if (size > 0 && size <= int.MaxValue)
                {
                    int length = (int)size;
                    var timer = Stopwatch.StartNew();
                    string how;
                    uint result;
                    // Waterbox-backed domains (GPGX and friends) only expose their memory while
                    // activated, and the Monitor variants guard it with a lock; Enter/Exit is a
                    // no-op for the plain native cores.
                    domain.Enter();
                    try
                    {
                        var data = DomainDataPointer(domain);
                        if (data != IntPtr.Zero)
                        {
                            // Pointer-backed domain: a straight memcpy, then hash the buffer. The
                            // domain never changes size mid-session, so this allocates once.
                            if (_hashScratch.Length != length) _hashScratch = new byte[length];
                            Marshal.Copy(data, _hashScratch, 0, length);
                            result = Fnv1a64(_hashScratch, length);
                            how = "ptr";
                        }
                        else
                        {
                            result = HashByWord(domain, size, salt, out int stride);
                            how = stride > 1 ? $"word/{stride}" : "word";
                        }
                    }
                    finally { domain.Exit(); }
                    // Report the cost the first time so a regression here is attributable rather
                    // than showing up as an unexplained slow tick.
                    if (HashDiagnostic == null)
                        HashDiagnostic = $"checksum: {how} domain '{domain.Name}' {length / 1024}KiB " +
                            $"in {timer.Elapsed.TotalMilliseconds:F1}ms";
                    return result;
                }
            }
            catch (Exception ex)
            {
                // Any surprise from the domain service — fall through to the portable path rather
                // than losing desync detection entirely.
                _memoryDomains = null;
                HashDiagnostic = "checksum: bulk domain path failed (" + ex.GetType().Name +
                    ": " + ex.Message + "), using the SHA fallback";
            }
        }

        var shaTimer = Stopwatch.StartNew();
        var name = _apis.Memory.MainMemoryName;
        var domainSize = _apis.Memory.GetMemoryDomainSize(name);
        var hex = _apis.Memory.HashRegion(0, (int)domainSize, name);
        // Fold the leading bytes of the SHA hex string into a cheap 32-bit rolling checksum.
        uint h = 2166136261;
        foreach (var c in hex) { h ^= c; h *= 16777619; }
        if (HashDiagnostic == null || HashDiagnostic.StartsWith("checksum: bulk domain path failed"))
            HashDiagnostic = (HashDiagnostic ?? "checksum: no memory-domain service") +
                $" — SHA over '{name}' {domainSize / 1024}KiB took {shaTimer.Elapsed.TotalMilliseconds:F1}ms";
        return h;
    }

    /// <summary>Which checksum path ran and what it cost, filled in on the first hash of a session.
    /// Null until then. Logged once so a regression here can't hide as a generic slow tick.</summary>
    public string? HashDiagnostic { get; private set; }

    /// <summary>
    /// The domain's backing block as a raw pointer, or Zero if it doesn't have one. Every
    /// MemoryDomainIntPtr* variant exposes it as a public <c>Data</c> property; delegate- and
    /// array-backed domains do not, and fall back to BulkPeekByte. Resolved by reflection once
    /// because the concrete domain type lives in the emulation cores, which this project has no
    /// compile-time reference to.
    ///
    /// Note the Swap16 variants hold their bytes in a different order than PeekByte reports. That
    /// is fine here and only here: this value is never interpreted, only compared against the same
    /// computation on a peer running the same build.
    /// </summary>
    private IntPtr DomainDataPointer(MemoryDomain domain)
    {
        if (!_domainDataResolved)
        {
            _domainDataResolved = true;
            try
            {
                var prop = domain.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == typeof(IntPtr) && prop.CanRead)
                    _domainDataProp = prop;
            }
            catch { _domainDataProp = null; }
        }
        if (_domainDataProp == null) return IntPtr.Zero;
        try { return (IntPtr)(_domainDataProp.GetValue(domain) ?? IntPtr.Zero); }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Main memory as a raw domain, resolved once. Null if the service isn't offered.</summary>
    private MemoryDomain? MainMemoryDomain()
    {
        if (!_memoryDomainsResolved)
        {
            _memoryDomainsResolved = true;
            try { _memoryDomains = _emulator.ServiceProvider.GetService<IMemoryDomains>(); }
            catch { _memoryDomains = null; }
        }
        try { return _memoryDomains?.MainMemory; }
        catch { return null; }
    }

    /// <summary>
    /// How many 32-bit words one checksum may read. N64's RDRAM measured ~13.5ns per word read, so
    /// this budget is roughly 7ms — under half a frame, where reading all 2M words took 28ms.
    /// Domains at or under this size (everything up to a 2MiB main RAM) are still read in full.
    /// </summary>
    private const int HashWordBudget = 512 * 1024;

    /// <summary>
    /// Hash a domain that has no raw backing block, reading a 32-bit word at a time.
    ///
    /// N64's RDRAM is a <c>MemoryDomainDelegate</c>: there is no pointer to copy, and every read —
    /// byte or word — goes through a delegate. <c>BulkPeekByte</c> is one such call PER BYTE (8
    /// million of them, 34ms for 8MiB); reading words measured 28ms, because PeekUint on this
    /// domain is itself composed from byte reads. There is no fast path, so the only remaining
    /// lever is reading less.
    ///
    /// Above <see cref="HashWordBudget"/> the domain is therefore sampled with a stride. To avoid
    /// permanently ignoring the unsampled words, the starting offset rotates with the frame the
    /// checksum describes: over <c>stride</c> consecutive checksums every word is covered. Both
    /// peers derive the offset from the same frame number, so they always read the same slice.
    ///
    /// The trade this makes, stated plainly: a divergence spanning at least <c>stride</c> words is
    /// still caught immediately, and anything narrower is caught within <c>stride</c> intervals
    /// instead of at the next one. Emulation divergence spreads across memory within a few frames,
    /// so in practice this costs detection latency rather than detection.
    /// </summary>
    private static uint HashByWord(MemoryDomain domain, long size, int salt, out int stride)
    {
        long words = size / 4;
        stride = words <= HashWordBudget
            ? 1
            : (int)Math.Min(int.MaxValue, (words + HashWordBudget - 1) / HashWordBudget);
        long offset = stride <= 1 ? 0 : (uint)salt % (uint)stride;

        const ulong prime = 1099511628211UL;
        ulong h = 14695981039346656037UL;
        // Fold the sampling parameters in, so a value can never be compared as though it described
        // a slice it didn't. A peer reading a different slice produces an obviously different hash
        // rather than a plausible one.
        h = (h ^ (((ulong)(uint)stride << 32) | (uint)offset)) * prime;

        for (long w = offset; w < words; w += stride)
            h = (h ^ domain.PeekUint(w * 4, false)) * prime;

        // Trailing bytes past the last whole word, only when reading everything anyway.
        if (stride == 1)
            for (long a = words * 4; a < size; a++)
                h = (h ^ domain.PeekByte(a)) * prime;

        return (uint)(h ^ (h >> 32));
    }

    /// <summary>
    /// FNV-1a folded to 32 bits, consuming eight bytes per step. Both peers run the same arithmetic
    /// on the same bytes, so the result is identical wherever it's computed — that, not collision
    /// resistance, is the property desync detection needs.
    /// </summary>
    private static uint Fnv1a64(byte[] data, int length)
    {
        const ulong prime = 1099511628211UL;
        ulong h = 14695981039346656037UL;
        int i = 0;
        for (int limit = length - 7; i < limit; i += 8)
            h = (h ^ BitConverter.ToUInt64(data, i)) * prime;
        for (; i < length; i++)
            h = (h ^ data[i]) * prime;
        // Fold the high half down so a divergence up there can't vanish in the truncation.
        return (uint)(h ^ (h >> 32));
    }

    // --- Layout derivation --------------------------------------------------------

    /// <summary>
    /// How many controller ports the loaded core exposes, without building a whole adapter (no
    /// binding/config lookups). The UI needs this before a session to cap the player count — a core
    /// only exposes more than its default ports once a multitap/adapter is enabled in its settings
    /// (Genesis 4-Way Play / Team Player, SNES multitap), which reboots the core and re-reads this.
    /// </summary>
    public static int PortCountOf(IEmulator emulator)
    {
        if (emulator == null) throw new ArgumentNullException(nameof(emulator));
        return UsablePorts(BuildLayouts(emulator.ControllerDefinition));
    }

    /// <summary>
    /// Leading run of ports that can actually carry input — a layout with no buttons and no axes
    /// serializes to zero bytes and is a slot, not a controller.
    ///
    /// <see cref="BuildLayouts"/> allocates one slot per player number it finds anywhere in the
    /// core's flat control list, and those slots are not guaranteed to be populated. QuickNES
    /// reports three, of which the third is empty — so a 3-player session passed the player-count
    /// cap, and then every packet for port 2 was refused on arrival because the receiving side had
    /// nothing to deserialize it into. Both ends agreed there was no input to exchange, no frame
    /// ever advanced, and the session died at frame 0 blaming the UDP path.
    ///
    /// The run is counted from port 0 and stops at the first empty slot, because ports are assigned
    /// contiguously: a usable port sitting behind an unusable one is unreachable anyway.
    /// </summary>
    private static int UsablePorts(CoreLayout[] layouts)
    {
        int usable = 0;
        while (usable < layouts.Length && layouts[usable].PayloadByteWidth > 0) usable++;
        return usable;
    }

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
