using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BizHawk.Common;
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
    private PropertyInfo? _domainArrayProp;
    private bool _domainBulkCapable;
    private Type? _domainAccessResolvedFor;   // domain type the three fields above describe

    public EmuHawkAdapter(ApiContainer apis, IEmulator emulator, IStatable statable,
        Config? config = null, IMovieSession? movieSession = null)
    {
        _apis = apis ?? throw new ArgumentNullException(nameof(apis));
        _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
        _statable = statable ?? throw new ArgumentNullException(nameof(statable));
        // The tool passes its FormBase.Config and ToolFormBase.MovieSession — the supported route,
        // both set by ToolManager before the form is even shown. The config falls back to
        // EmulationApi.ForbiddenConfigReference (whose name is the API's own comment on being used
        // that way); the movie session falls back to MainForm when AttachMainForm runs. Getting the
        // session at construction matters for input: without it, capture silently took the
        // allocating dictionary path until the audio path happened to hand MainForm over.
        _hostConfig = config;
        _movieSession = movieSession;
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
        if (_hostConfig == null)
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
        DiscardCoreAudio();
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

    public void RunFramesInvisible(int count, Func<int, InputSet> inputsFor)
    {
        for (int i = 0; i < count; i++)
        {
            _emulator.FrameAdvance(Controller(inputsFor(i)), render: false, renderSound: false);
            // IEmulator's contract, verbatim: "(some?) cores expect you to call
            // SoundProvider.GetSamples() after each FrameAdvance() ... please do this, even when
            // renderSound = false". It is not a courtesy. The Hawk cores ignore renderSound
            // entirely and keep pushing APU deltas into blargg's native blip_buf, whose write
            // position only resets when samples are taken out — and whose only bounds check is a
            // C assert compiled out of the shipped DLL. NESHawk's buffer holds ~5.6 frames, the
            // rollback ring goes to 16, so a deep repair that skipped this wrote past the end of
            // a native heap allocation. Discard, not drain: a repaired frame's audio was already
            // produced by its original (predicted) run.
            DiscardCoreAudio();
        }
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
    private readonly Stack<PooledState> _statePool = new();

    /// <summary>
    /// One pooled savestate buffer and the reader/writer bound to it.
    ///
    /// The writer is part of the pooling for the same reason the buffer is: a new BinaryWriter
    /// (and its UTF8 encoder) per savestate is ~60 small objects a second under continuous
    /// prediction on N64 — trivial against the 5.9ms save itself, but pure churn inside the most
    /// timing-critical span in the program, and avoidable for free once something owns the pair.
    /// </summary>
    private sealed class PooledState
    {
        public PooledState(int capacity)
        {
            Stream = new MemoryStream(capacity);
            Writer = new BinaryWriter(Stream, Encoding.UTF8, leaveOpen: true);
            Reader = new BinaryReader(Stream, Encoding.UTF8, leaveOpen: true);
        }
        public MemoryStream Stream { get; }
        public BinaryWriter Writer { get; }
        public BinaryReader Reader { get; }
    }

    /// <summary>Largest state seen, so a fresh buffer starts big enough to avoid growth copies.</summary>
    private int _stateSizeHint = 1 << 16;

    /// <summary>
    /// Cap on retained buffers. The ring keeps roughly maxRollback + margin states, so this is far
    /// above steady state and exists only so a pathological release burst cannot pin memory.
    /// </summary>
    private const int StatePoolCap = 64;

    public StateHandle SaveStateToMemory()
    {
        PooledState pooled;
        if (_statePool.Count > 0) pooled = _statePool.Pop();
        else { pooled = new PooledState(_stateSizeHint); StateBuffersAllocated++; }
        var ms = pooled.Stream;
        ms.SetLength(0);
        ms.Position = 0;
        _statable.SaveStateBinary(pooled.Writer);
        pooled.Writer.Flush();
        if (ms.Length > _stateSizeHint) _stateSizeHint = (int)ms.Length;
        return new StateHandle(_emulator.Frame, pooled);
    }

    public void LoadStateFromMemory(StateHandle handle)
    {
        var pooled = (PooledState)handle.Token;
        pooled.Stream.Position = 0;
        _statable.LoadStateBinary(pooled.Reader);
    }

    public void ReleaseState(StateHandle handle)
    {
        // Retire the buffer for reuse rather than dropping it for the collector. Over the cap it
        // is simply let go, which is the old behaviour for that one buffer.
        if (!(handle.Token is PooledState pooled)) return;
        if (_statePool.Count >= StatePoolCap) return;
        pooled.Stream.SetLength(0);
        _statePool.Push(pooled);
    }

    /// <summary>Drop every retired buffer. Called when a session ends so a long idle between
    /// sessions does not hold the ring's worth of memory for nothing. Must run AFTER the driver is
    /// disposed — disposing the rollback ring is what pushes its buffers back in here.</summary>
    public void ClearStatePool()
    {
        while (_statePool.Count > 0) _statePool.Pop().Stream.Dispose();
        _hashScratch = [];   // sized to the main-memory domain — 8MiB on N64, same argument
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
        // Serialized through the SAME pool the rollback ring uses, then copied out. The caller owns
        // the returned array (it is kept, compressed and sent), so that copy is unavoidable — but
        // the scratch buffer underneath it is not. A fresh MemoryStream here meant ~32MiB of large
        // object heap per whole-state transfer on N64 rather than ~16MiB, and every resync is a
        // moment the session is already struggling: net48 neither compacts the LOH nor reclaims it
        // outside a blocking gen2, which is the same 52.5ms-pause bill the pool was built to stop.
        var pooled = _statePool.Count > 0 ? _statePool.Pop() : new PooledState(_stateSizeHint);
        try
        {
            var ms = pooled.Stream;
            ms.SetLength(0);
            ms.Position = 0;
            _statable.SaveStateBinary(pooled.Writer);
            pooled.Writer.Flush();
            if (ms.Length > _stateSizeHint) _stateSizeHint = (int)ms.Length;
            return ms.ToArray();
        }
        finally
        {
            pooled.Stream.SetLength(0);
            if (_statePool.Count < StatePoolCap) _statePool.Push(pooled);
        }
    }

    public void ImportState(byte[] state)
    {
        using var ms = new MemoryStream(state);
        // Same encoding as ExportState's writer, for symmetry's sake. Byte-compatible either way —
        // BinaryReader/Writer never emit or consume a preamble; encoding only affects Write(string),
        // which no core uses — but matching removes the question.
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
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
    /// reporting a phantom desync (v15 did exactly that: the path selection below changed which
    /// bytes some cores hash).
    ///
    /// Four ways in, because domains differ in what they expose — see
    /// <see cref="ResolveDomainAccess"/> for how one is chosen. The paths need not agree with each
    /// other about byte order or coverage: both peers run the same core, so both take the same
    /// path. Selection is a pure function of the domain's type, never of measured speed — a fast
    /// machine and a slow machine must hash the same bytes. Falls back to the portable API path if
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
                    ResolveDomainAccess(domain);
                    // Waterbox-backed domains only expose their memory while activated, and the
                    // Monitor variants guard it with a lock; Enter/Exit is a no-op for the plain
                    // native cores.
                    domain.Enter();
                    try
                    {
                        var data = DomainDataPointer(domain);
                        var array = DomainDataArray(domain);
                        if (data != IntPtr.Zero)
                        {
                            // Pointer-backed domain: a straight memcpy, then hash the buffer. The
                            // domain never changes size mid-session, so this allocates once.
                            if (_hashScratch.Length != length) _hashScratch = new byte[length];
                            Marshal.Copy(data, _hashScratch, 0, length);
                            result = Fnv1a64(_hashScratch, length);
                            how = "ptr";
                        }
                        else if (array != null && array.Length >= length)
                        {
                            // Array-backed domain (the Hawk cores' RAM): the bytes are already
                            // managed, so hash them where they sit. No copy at all.
                            result = Fnv1a64(array, length);
                            how = "arr";
                        }
                        else if (_domainBulkCapable)
                        {
                            // A real BulkPeekByte override — one memcpy or one native call for the
                            // whole domain. This is what rescues the waterbox function-backed
                            // domains (the Nyma cores), whose per-byte reads each take a monitor
                            // round-trip: the full domain now costs about what a 1/16 stride
                            // sample used to, and a divergence narrower than the stride is caught
                            // at the next checksum instead of up to stride intervals late.
                            if (_hashScratch.Length != length) _hashScratch = new byte[length];
                            domain.BulkPeekByte(0L.RangeToExclusive(size), _hashScratch);
                            result = Fnv1a64(_hashScratch, length);
                            how = "bulk";
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
    /// Work out, once per domain type, which of the four hash paths this domain supports.
    ///
    /// - A public <c>IntPtr Data</c> property is the MemoryDomainIntPtr* family (GPGX, mGBA,
    ///   BSNES, QuickNES): raw pointer, memcpy.
    /// - A public <c>byte[] Data</c> property is MemoryDomainByteArray (the Hawk cores): the
    ///   managed array itself, hashed in place.
    /// - A genuine <c>BulkPeekByte</c> override is the waterbox family (Snes9x, Ares64, melonDS,
    ///   the Nyma cores): one memcpy or one native call under one monitor round-trip.
    ///   MemoryDomainDelegate is explicitly EXCLUDED even though it declares an override, because
    ///   its override silently falls back to the per-byte base loop when the core supplied no bulk
    ///   delegate — which mupen N64 does not — and that per-byte loop through a delegate is slower
    ///   than the strided word path this would replace.
    /// - Everything else reads by word (see HashByWord).
    ///
    /// Reflection because the concrete domain types live in the emulation cores, which this
    /// project has no compile-time reference to. Keyed on the domain's type: the domain OBJECT is
    /// stable for a core's lifetime (BizHawk mutates domains in place on savestate load rather
    /// than replacing them), but its backing pointer/array is re-read every hash for exactly that
    /// reason — MergeList repoints Data on some cores' state loads, so caching the pointer would
    /// hash a freed buffer.
    /// </summary>
    private void ResolveDomainAccess(MemoryDomain domain)
    {
        var type = domain.GetType();
        if (_domainAccessResolvedFor == type) return;
        _domainAccessResolvedFor = type;
        _domainDataProp = null;
        _domainArrayProp = null;
        _domainBulkCapable = false;
        try
        {
            var prop = type.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
            {
                if (prop.PropertyType == typeof(IntPtr)) _domainDataProp = prop;
                else if (prop.PropertyType == typeof(byte[])) _domainArrayProp = prop;
            }

            var bulk = type.GetMethod(nameof(MemoryDomain.BulkPeekByte),
                new[] { typeof(BizHawk.Common.Range<long>), typeof(byte[]) });
            _domainBulkCapable = bulk != null
                && bulk.DeclaringType != typeof(MemoryDomain)
                && bulk.DeclaringType != typeof(MemoryDomainDelegate);
        }
        catch
        {
            _domainDataProp = null;
            _domainArrayProp = null;
            _domainBulkCapable = false;
        }
    }

    /// <summary>
    /// The domain's backing block as a raw pointer, or Zero if it doesn't have one.
    ///
    /// Note the Swap16 variants hold their bytes in a different order than PeekByte reports. That
    /// is fine here and only here: this value is never interpreted, only compared against the same
    /// computation on a peer running the same build.
    /// </summary>
    private IntPtr DomainDataPointer(MemoryDomain domain)
    {
        if (_domainDataProp == null) return IntPtr.Zero;
        try { return (IntPtr)(_domainDataProp.GetValue(domain) ?? IntPtr.Zero); }
        catch { return IntPtr.Zero; }
    }

    /// <summary>The domain's backing managed array, or null. Re-read per hash — see
    /// <see cref="ResolveDomainAccess"/> for why caching the array itself would be a bug.</summary>
    private byte[]? DomainDataArray(MemoryDomain domain)
    {
        if (_domainArrayProp == null) return null;
        try { return _domainArrayProp.GetValue(domain) as byte[]; }
        catch { return null; }
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
    /// checksum describes. Both peers derive the offset from the same frame number, so they always
    /// read the same slice.
    ///
    /// The salt is bit-mixed before the modulo, and the reason is a bug this code shipped with for
    /// its whole life: the salt is a checksum frame, checksum frames are multiples of the interval
    /// (300), and 300 shares every factor of the strides this budget actually produces — so
    /// <c>salt % stride</c> was 0 on every boundary and the "rotation" never moved. Only words at
    /// offset 0 mod stride were ever hashed; on 8MiB RDRAM that is a fixed 25% of memory, forever.
    /// Mixing first makes the offset depend on all of the salt's bits, so consecutive boundaries
    /// land on different residues regardless of what the interval and stride happen to divide.
    ///
    /// The trade this makes, stated plainly: a divergence spanning at least <c>stride</c> words is
    /// still caught immediately. Anything narrower is caught when the rotation lands on it — the
    /// offsets are a deterministic pseudo-random sequence rather than a strict round-robin, so
    /// expected coverage of all residues takes a few times <c>stride</c> intervals rather than
    /// exactly <c>stride</c>. Emulation divergence spreads across memory within a few frames, so
    /// in practice this costs detection latency rather than detection.
    /// </summary>
    private static uint HashByWord(MemoryDomain domain, long size, int salt, out int stride)
    {
        long words = size / 4;
        stride = words <= HashWordBudget
            ? 1
            : (int)Math.Min(int.MaxValue, (words + HashWordBudget - 1) / HashWordBudget);
        long offset = stride <= 1 ? 0 : MixSalt((uint)salt) % (uint)stride;

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
    /// murmur3's 32-bit finalizer. Every output bit depends on every input bit, which is the one
    /// property <see cref="HashByWord"/>'s offset needs: a salt divisible by the stride must not
    /// produce an offset of zero every time. Both peers run it on the same salt, so they still
    /// read the same slice.
    /// </summary>
    private static uint MixSalt(uint x)
    {
        x ^= x >> 16;
        x *= 0x85EBCA6Bu;
        x ^= x >> 13;
        x *= 0xC2B2AE35u;
        x ^= x >> 16;
        return x;
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
