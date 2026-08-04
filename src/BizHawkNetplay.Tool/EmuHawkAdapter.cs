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
    // The raw block behind a delegate-wrapped domain, and the closure type it was found on. See
    // ResolveDelegateClosurePointer — this is what puts N64's RDRAM on the memcpy path.
    private FieldInfo? _domainClosurePtrField;
    private Type? _domainClosureOwner;
    private bool _closurePathRejected;        // a contents spot-check disagreed; never trust it again
    // The VI register block, resolved lazily alongside main memory (N64 only; null everywhere else).
    private MemoryDomain? _viRegisters;
    private bool _viRegistersResolved;

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
        _layouts = AppendConsoleControls(
            ReorderLayoutsToGamePlayerNumbering(BuildLayouts(emulator.ControllerDefinition)));
        _bindings = BuildBindings();
        _analogBinds = BuildAnalogBinds();
        _axisReversed = BuildAxisReversed();
        _remapCompatible = BuildRemapCompatibility();
        _padButtonKeys = new string[_layouts.Length][];
        _padAxisKeys = new string[_layouts.Length][];
        _playerButtonCount = new int[_layouts.Length];
        _playerAxisCount = new int[_layouts.Length];
        for (int p = 0; p < _layouts.Length; p++)
        {
            _padButtonKeys[p] = _layouts[p].Buttons.Select(StripPortPrefix).ToArray();
            _padAxisKeys[p] = _layouts[p].Axes.Select(a => StripPortPrefix(a.Name)).ToArray();
            _playerButtonCount[p] = PlayerControlRun(_layouts[p].Buttons);
            _playerAxisCount[p] = PlayerAxisRun(_layouts[p].Axes);
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
    /// Five ways in, because domains differ in what they expose — see
    /// <see cref="ResolveDomainAccess"/> for how one is chosen. The paths need not agree with each
    /// other about byte order or coverage: both peers run the same core, so both take the same
    /// path. Selection is a pure function of the domain's type and size, never of measured speed
    /// or of what memory contains — a fast machine and a slow machine must hash the same bytes.
    /// Falls back to the portable API path if the domain service isn't reachable at all.
    ///
    /// The fifth (<c>ptr*</c>) is the one that matters on a heavy core: N64's RDRAM is a
    /// <see cref="MemoryDomainDelegate"/> built around a pointer BizHawk already has, so reaching
    /// that pointer moves the domain from the sampling by-word path onto the same memcpy the plain
    /// native cores use — ~2ms for all 8MiB where sampling a quarter of it cost ~7ms. That is both
    /// a hitch removed every <c>ChecksumInterval</c> and four times the coverage, so a narrow
    /// divergence is caught at the next checksum instead of whenever the rotation happens to land
    /// on it.
    ///
    /// Whatever the path, the span the video hardware is scanning out is skipped — see
    /// <see cref="TryGetFramebufferSpan"/>, which is what stops N64 desyncing at every checksum
    /// above native resolution. Both the path and that span are folded into the value, so a peer
    /// that resolved either differently produces an obviously different hash rather than a
    /// plausible one.
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
                        // Bytes the video hardware wrote back into RAM, which above native
                        // resolution are produced by the GPU and therefore differ between machines.
                        // Skipped by every path below; see TryGetFramebufferSpan.
                        bool excluded = TryGetFramebufferSpan(size, out long exStart, out long exEnd);
                        if (!excluded) { exStart = 0; exEnd = 0; }

                        var data = DomainDataPointer(domain);
                        var array = DomainDataArray(domain);
                        var closure = data == IntPtr.Zero && array == null
                            ? DomainClosurePointer(domain, length)
                            : IntPtr.Zero;
                        if (data != IntPtr.Zero)
                        {
                            // Pointer-backed domain: a straight memcpy, then hash the buffer. The
                            // domain never changes size mid-session, so this allocates once.
                            if (_hashScratch.Length != length) _hashScratch = new byte[length];
                            Marshal.Copy(data, _hashScratch, 0, length);
                            result = Fnv1a64(_hashScratch, length, PathPtr, exStart, exEnd);
                            how = "ptr";
                        }
                        else if (array != null && array.Length >= length)
                        {
                            // Array-backed domain (the Hawk cores' RAM): the bytes are already
                            // managed, so hash them where they sit. No copy at all.
                            result = Fnv1a64(array, length, PathArray, exStart, exEnd);
                            how = "arr";
                        }
                        else if (closure != IntPtr.Zero)
                        {
                            // A delegate-wrapped pointer (N64's RDRAM). Same memcpy as the `ptr`
                            // path once the block has been found — see ResolveDelegateClosurePointer
                            // for how, and why the copy is spot-checked against the domain itself
                            // before its answer is believed.
                            if (_hashScratch.Length != length) _hashScratch = new byte[length];
                            Marshal.Copy(closure, _hashScratch, 0, length);
                            if (ClosureBufferDisagrees(domain, _hashScratch, length))
                            {
                                // The pointer is not this domain's memory after all. Fall back for
                                // this hash and every later one rather than reporting bytes that
                                // were never compared to anything.
                                _closurePathRejected = true;
                                result = HashByWord(domain, size, salt, exStart, exEnd, out int fallbackStride);
                                how = fallbackStride > 1 ? $"word/{fallbackStride}" : "word";
                            }
                            else
                            {
                                result = Fnv1a64(_hashScratch, length, PathClosure, exStart, exEnd);
                                how = "ptr*";
                            }
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
                            result = Fnv1a64(_hashScratch, length, PathBulk, exStart, exEnd);
                            how = "bulk";
                        }
                        else
                        {
                            result = HashByWord(domain, size, salt, exStart, exEnd, out int stride);
                            how = stride > 1 ? $"word/{stride}" : "word";
                        }
                        if (excluded)
                            how += $" -fb@{exStart / 1024}KiB+{(exEnd - exStart) / 1024}KiB";
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
        _domainClosurePtrField = null;
        _domainClosureOwner = null;
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

            if (_domainDataProp == null && _domainArrayProp == null && !_domainBulkCapable)
                ResolveDelegateClosurePointer(domain);
        }
        catch
        {
            _domainDataProp = null;
            _domainArrayProp = null;
            _domainBulkCapable = false;
            _domainClosurePtrField = null;
            _domainClosureOwner = null;
        }
    }

    /// <summary>
    /// Find the raw block behind a <see cref="MemoryDomainDelegate"/> whose peek is a closure over a
    /// pointer — which is the shape N64's RDRAM has, and the reason the checksum used to take the
    /// slowest path available to it.
    ///
    /// BizHawk's N64 builds every domain by asking mupen for a pointer
    /// (<c>api.get_memory_ptr</c>) and then wrapping it in per-byte peek/poke lambdas that do the
    /// core's <c>addr ^ 3</c> swizzle. The pointer is therefore right there, captured in the
    /// closure, but invisible to the <c>Data</c>-property probe above — so an 8MiB domain was read
    /// one delegate call per word, which is why <see cref="HashByWord"/> had to sample rather than
    /// read it all: ~7ms for a quarter of RDRAM, against ~2ms for all of it by memcpy.
    ///
    /// Matched on SHAPE, never on name: the closure is compiler-generated
    /// (<c>&lt;&gt;c__DisplayClass80_0</c>), and its name is not a contract — a recompile may
    /// renumber it. What is stable is that it captures exactly one <see cref="IntPtr"/> and an
    /// integer equal to the domain's size. Requiring exactly one pointer field is what keeps this
    /// from guessing between two of them.
    ///
    /// Acceptance is deliberately a pure function of the domain's TYPE and SIZE, never of what
    /// memory happens to contain. Both peers must take the same path or they would hash unlike
    /// byte sets, and a rule that read RAM contents could answer differently on two machines that
    /// merely reached this point at different moments. The contents ARE checked — see
    /// <see cref="ClosureBufferDisagrees"/> — but only ever to reject.
    /// </summary>
    private void ResolveDelegateClosurePointer(MemoryDomain domain)
    {
        if (domain is not MemoryDomainDelegate del) return;
        var target = del.Peek?.Target;
        if (target == null) return;
        var owner = target.GetType();

        FieldInfo? pointer = null;
        bool sizeAgrees = false;
        foreach (var field in owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.FieldType == typeof(IntPtr))
            {
                if (pointer != null) return;   // two candidates: refuse rather than pick one
                pointer = field;
            }
            else if (field.FieldType == typeof(int) || field.FieldType == typeof(long))
            {
                try
                {
                    if (Convert.ToInt64(field.GetValue(target)) == domain.Size) sizeAgrees = true;
                }
                catch { /* not a value we can compare; it simply doesn't corroborate */ }
            }
        }
        if (pointer == null || !sizeAgrees) return;
        _domainClosurePtrField = pointer;
        _domainClosureOwner = owner;
    }

    /// <summary>
    /// The block <see cref="ResolveDelegateClosurePointer"/> found, re-read per hash for the same
    /// reason <see cref="DomainDataPointer"/> is: the domain object outlives a savestate load, and
    /// caching an address across one would hash freed memory. Zero once the path has been rejected.
    /// </summary>
    private IntPtr DomainClosurePointer(MemoryDomain domain, int length)
    {
        if (_closurePathRejected || _domainClosurePtrField == null || length <= 0) return IntPtr.Zero;
        if (domain is not MemoryDomainDelegate del) return IntPtr.Zero;
        var target = del.Peek?.Target;
        if (target == null || target.GetType() != _domainClosureOwner) return IntPtr.Zero;
        try { return (IntPtr)(_domainClosurePtrField.GetValue(target) ?? IntPtr.Zero); }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Aligned words spot-checked against the domain before the copied block is believed.</summary>
    private const int ClosureProbeWords = 256;

    /// <summary>
    /// Does the copied block disagree with what the domain itself reports?
    ///
    /// The swizzle means byte order within a word is permuted, so this compares each sampled word
    /// as an unordered set of four bytes — true whatever permutation the core applies, and false as
    /// soon as the pointer is the wrong block. A word whose four bytes are all equal cannot
    /// distinguish anything, so it is skipped: on mostly-zero RAM the check would otherwise
    /// "pass" against any pointer at all.
    ///
    /// Only ever used to REJECT. A sample that discriminates nothing leaves the path in use, which
    /// is safe precisely because acceptance was decided structurally — see
    /// <see cref="ResolveDelegateClosurePointer"/>.
    /// </summary>
    private static bool ClosureBufferDisagrees(MemoryDomain domain, byte[] buffer, int length)
    {
        int words = length / 4;
        if (words == 0) return false;
        int step = Math.Max(1, words / ClosureProbeWords);
        for (long w = 0; w < words; w += step)
        {
            long at = w * 4;
            int a0 = domain.PeekByte(at), a1 = domain.PeekByte(at + 1),
                a2 = domain.PeekByte(at + 2), a3 = domain.PeekByte(at + 3);
            if (a0 == a1 && a1 == a2 && a2 == a3) continue;      // discriminates nothing
            int b0 = buffer[at], b1 = buffer[at + 1], b2 = buffer[at + 2], b3 = buffer[at + 3];
            if (a0 + a1 + a2 + a3 != b0 + b1 + b2 + b3) return true;
            if ((a0 ^ a1 ^ a2 ^ a3) != (b0 ^ b1 ^ b2 ^ b3)) return true;
            if (Math.Max(Math.Max(a0, a1), Math.Max(a2, a3))
                != Math.Max(Math.Max(b0, b1), Math.Max(b2, b3))) return true;
        }
        return false;
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

    // --- The video framebuffer, and why the checksum must skip it -------------------

    /// <summary>
    /// N64's VI register block, or null on every other core. Resolved once.
    ///
    /// Named rather than probed because the name IS the identification: only BizHawk's N64 exposes
    /// a domain called this, so finding it is what says "this core scans a framebuffer out of main
    /// memory". <see cref="IMemoryDomains"/> offers a by-name indexer and a <c>Has</c>, so nothing
    /// here reaches past the public surface.
    /// </summary>
    private MemoryDomain? ViRegisterDomain()
    {
        if (_viRegistersResolved) return _viRegisters;
        _viRegistersResolved = true;
        try
        {
            var domains = _memoryDomains;
            if (domains != null && domains.Has(ViDomainName)) _viRegisters = domains[ViDomainName];
        }
        catch { _viRegisters = null; }
        return _viRegisters;
    }

    private const string ViDomainName = "VI Register";

    // Offsets within the VI register block, in mupen64plus's own order. The domain is big-endian
    // and swizzled, which together mean PeekUint returns the register's value as the core holds it.
    private const int ViStatusOffset = 0x00;   // bits 1..0 select the pixel size
    private const int ViOriginOffset = 0x04;   // RDRAM address currently being scanned out
    private const int ViWidthOffset = 0x08;    // pixels per line in the framebuffer
    private const int ViVStartOffset = 0x28;   // active field, in half-lines
    private const int ViYScaleOffset = 0x34;   // 2.10 fixed point vertical scale
    private const int ViRegisterBytes = 0x38;  // fourteen registers

    /// <summary>
    /// The span of main memory the video hardware is scanning out, which the desync checksum must
    /// not read.
    ///
    /// <b>Why this exists.</b> N64 desyncs at every checksum above native resolution, and it is not
    /// the netcode: Rice and GLideN64 resolve their framebuffer back into RDRAM, and above native
    /// those bytes are produced by your GPU rather than by the emulated core. They differ between
    /// two machines that are otherwise in perfect agreement, they land inside the region the
    /// checksum hashes, and resyncing cannot help because the next frame reproduces them. That is
    /// what has forced every N64 session to native resolution.
    ///
    /// <b>Why it is safe to skip them.</b> Which pixels the GPU produced is not emulated state that
    /// anything downstream consumes — the core re-renders the picture from scratch every frame. The
    /// bytes excluded here are an output, not a cause.
    ///
    /// <b>Why both peers exclude the same range.</b> Every value read here is a VI register, which
    /// is written by the game's own CPU code and carried in the savestate. Two peers standing on
    /// the same state read the same registers and compute the same span, so this needs no
    /// negotiation — only a protocol bump, so a peer that computes it differently refuses rather
    /// than reporting a phantom desync. The bounds are folded into the hash as well, so a
    /// disagreement is loud rather than plausible.
    ///
    /// <b>What it does not cover.</b> One buffer, the one being scanned out. A double-buffered game
    /// is also rendering into another, and framebuffer emulation can write elsewhere again. If a
    /// session still disagrees above native, that is the reason, and the bucketed divergence map in
    /// KNOWN-ISSUES is what would say so instead of leaving it to be guessed at.
    ///
    /// The arithmetic is <see cref="VideoFramebuffer"/>; this reads the registers and hands them
    /// over. Nothing below the four peeks knows what a memory domain is, which is the only reason
    /// any of it can be tested.
    /// </summary>
    private bool TryGetFramebufferSpan(long mainMemorySize, out long start, out long endExclusive)
    {
        start = 0;
        endExclusive = 0;
        var vi = ViRegisterDomain();
        if (vi == null || vi.Size < ViRegisterBytes) return false;

        try
        {
            // The block is big-endian and swizzled, which together mean PeekUint hands back each
            // register exactly as the core holds it. Everything past these four reads is arithmetic
            // over plain integers and lives in Core, where it can be tested — see VideoFramebuffer.
            uint status, origin, width, vStart, yScale;
            vi.Enter();
            try
            {
                status = vi.PeekUint(ViStatusOffset, bigEndian: true);
                origin = vi.PeekUint(ViOriginOffset, bigEndian: true);
                width = vi.PeekUint(ViWidthOffset, bigEndian: true);
                vStart = vi.PeekUint(ViVStartOffset, bigEndian: true);
                yScale = vi.PeekUint(ViYScaleOffset, bigEndian: true);
            }
            finally { vi.Exit(); }

            return VideoFramebuffer.TryResolve(
                status, origin, width, vStart, yScale, mainMemorySize, out start, out endExclusive);
        }
        catch
        {
            start = 0;
            endExclusive = 0;
            return false;
        }
    }

    /// <summary>
    /// How many 32-bit words one checksum may read. N64's RDRAM measured ~13.5ns per word read, so
    /// this budget is roughly 7ms — under half a frame, where reading all 2M words took 28ms.
    /// Domains at or under this size (everything up to a 2MiB main RAM) are still read in full.
    /// </summary>
    private const int HashWordBudget = 512 * 1024;

    /// <summary>
    /// Hash a domain whose raw backing block cannot be reached, reading a 32-bit word at a time.
    ///
    /// This was written for N64's RDRAM, a <c>MemoryDomainDelegate</c> where every read — byte or
    /// word — goes through a delegate. <c>BulkPeekByte</c> is one such call PER BYTE (8 million of
    /// them, 34ms for 8MiB); reading words measured 28ms, because PeekUint on this domain is itself
    /// composed from byte reads. With no pointer to copy, the only remaining lever was reading less.
    ///
    /// There IS a pointer, as it turns out — captured in the peek closure, and taken by
    /// <see cref="ResolveDelegateClosurePointer"/>, which moves N64 onto the memcpy path and hashes
    /// all of RDRAM for a third of what sampling a quarter of it cost. So this is now the fallback
    /// for a delegate domain whose block could not be found or could not be trusted, rather than
    /// the path the heaviest core in the set was stuck on. It is kept because that case is real:
    /// a domain closing over something other than a single pointer still has to be hashed somehow.
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
    private static uint HashByWord(MemoryDomain domain, long size, int salt,
        long exStart, long exEnd, out int stride)
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
        h = SeedWithPath(h, PathWord, exStart, exEnd);

        for (long w = offset; w < words; w += stride)
        {
            long at = w * 4;
            // Both bounds are word-aligned, so a whole word is either in the excluded span or out
            // of it — the sample never has to split one.
            if (at >= exStart && at < exEnd) continue;
            h = (h ^ domain.PeekUint(at, false)) * prime;
        }

        // Trailing bytes past the last whole word, only when reading everything anyway.
        if (stride == 1)
            for (long a = words * 4; a < size; a++)
            {
                if (a >= exStart && a < exEnd) continue;
                h = (h ^ domain.PeekByte(a)) * prime;
            }

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
    private static uint Fnv1a64(byte[] data, int length, int pathTag, long exStart, long exEnd)
    {
        ulong h = SeedWithPath(14695981039346656037UL, pathTag, exStart, exEnd);
        if (exEnd > exStart)
        {
            h = FoldRange(h, data, 0, (int)Math.Min(length, exStart));
            h = FoldRange(h, data, (int)Math.Min(length, exEnd), length);
        }
        else h = FoldRange(h, data, 0, length);
        // Fold the high half down so a divergence up there can't vanish in the truncation.
        return (uint)(h ^ (h >> 32));
    }

    private static ulong FoldRange(ulong h, byte[] data, int from, int to)
    {
        const ulong prime = 1099511628211UL;
        int i = from;
        for (int limit = to - 7; i < limit; i += 8)
            h = (h ^ BitConverter.ToUInt64(data, i)) * prime;
        for (; i < to; i++)
            h = (h ^ data[i]) * prime;
        return h;
    }

    /// <summary>
    /// Which hash path ran, and which bytes it skipped, folded into the seed.
    ///
    /// Same argument as the stride and offset in <see cref="HashByWord"/>, extended to cover the
    /// two things that now also decide what a checksum describes. Two peers are expected to agree
    /// on both — the path is chosen from the domain's type and size, the excluded span from
    /// registers carried in the savestate — and if they ever did not, the point is that the
    /// resulting values must not be comparable. A visible disagreement resyncs and names the paths
    /// in the log; a plausible one would compare unlike byte sets forever.
    /// </summary>
    private static ulong SeedWithPath(ulong h, int pathTag, long exStart, long exEnd)
    {
        const ulong prime = 1099511628211UL;
        h = (h ^ (uint)pathTag) * prime;
        h = (h ^ (ulong)exStart) * prime;
        h = (h ^ (ulong)exEnd) * prime;
        return h;
    }

    /// <summary>Identifies the route a hash took, so two peers on different ones cannot compare
    /// their results as though they described the same bytes. See <see cref="SeedWithPath"/>.</summary>
    private const int PathPtr = 1;
    private const int PathArray = 2;
    private const int PathBulk = 3;
    private const int PathWord = 4;
    private const int PathClosure = 5;
}
