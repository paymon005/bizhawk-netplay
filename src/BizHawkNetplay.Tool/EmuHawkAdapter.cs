using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BizHawk.Client.Common;
using BizHawk.Emulation.Common;
using BizHawkNetplay.Core.Emu;
using BizHawkNetplay.Core.Input;
using CoreLayout = BizHawkNetplay.Core.Input.ControllerLayout;

namespace BizHawkNetplay.Tool;

/// <summary>
/// The one class that knows BizHawk. Bridges <see cref="IEmuAdapter"/> onto the injected
/// ApiHawk container and emulator services of the running EmuHawk. Everything else in the
/// system talks only to <see cref="IEmuAdapter"/>.
///
/// Split across partials by subject, because this is also the one class no test can reach — it
/// needs net48 and a live EmuHawk — so the only defence its risky parts have is being separable
/// and readable:
///
/// <list type="bullet">
/// <item><b>this file</b> — construction, stepping the core, savestates, the desync checksum.</item>
/// <item><see cref="EmuHawkAdapter"/>.Identity — what the core IS, for the handshake.</item>
/// <item>.Input — reading the local pad and the tables that make it cheap.</item>
/// <item>.InputDiagnostics — the input test; never runs during a session.</item>
/// <item>.Output — audio and video while the session owns EmuHawk's run loop.</item>
/// </list>
/// </summary>
internal sealed partial class EmuHawkAdapter : IEmuAdapter
{
    private readonly ApiContainer _apis;
    private readonly IEmulator _emulator;
    private readonly IStatable _statable;
    private readonly CoreLayout[] _layouts;
    // EmuHawk's live config, for the master volume the audio pump has to apply itself. Read per
    // pump, not cached as a value: the slider moves while you play.
    private readonly Config? _hostConfig;

    // Raw main-memory access for the periodic desync checksum, resolved lazily (see HashMainMemory).
    private IMemoryDomains? _memoryDomains;
    private bool _memoryDomainsResolved;
    private byte[] _hashScratch = [];
    private PropertyInfo? _domainDataProp;
    private bool _domainDataResolved;

    public EmuHawkAdapter(ApiContainer apis, IEmulator emulator, IStatable statable)
    {
        _apis = apis ?? throw new ArgumentNullException(nameof(apis));
        _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
        _statable = statable ?? throw new ArgumentNullException(nameof(statable));
        _layouts = BuildLayouts(emulator.ControllerDefinition);
        _bindings = BuildBindings();
        _analogBinds = BuildAnalogBinds();
        _axisReversed = BuildAxisReversed();
        _remapCompatible = BuildRemapCompatibility();
        _padButtonKeys = new string[_layouts.Length][];
        _padAxisKeys = new string[_layouts.Length][];
        for (int p = 0; p < _layouts.Length; p++)
        {
            _padButtonKeys[p] = _layouts[p].Buttons.Select(StripPortPrefix).ToArray();
            _padAxisKeys[p] = _layouts[p].Axes.Select(a => StripPortPrefix(a.Name)).ToArray();
        }
        try { _hostConfig = (_apis.Emulation as EmulationApi)?.ForbiddenConfigReference; } catch { }
        _useCircularAnalogConstraint = ReadCircularAnalogConstraintSetting();
    }

    // --- Stepping the core --------------------------------------------------------

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
        DrainCoreAudio();
    }

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
}
