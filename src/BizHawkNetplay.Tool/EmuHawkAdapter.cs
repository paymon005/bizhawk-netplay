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
using BizHawkNetplay.Core.Session;
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
    /// The pool itself lives in Core (<see cref="StateBufferPool"/>) because none of it needs
    /// BizHawk — it is a stack of MemoryStreams and the rule that a buffer is handed out once. That
    /// rule is why it moved: releasing one twice used to push it into the pool twice, and two later
    /// saves would then pop what they believed were two buffers and get one, so two savestates
    /// aliased the same bytes. Nothing reached it, but the test double had always refused a double
    /// release while the shipping pool did not — so the suite was exercising something strictly
    /// more forgiving than production.
    /// </summary>
    private readonly StateBufferPool _statePool = new();

    /// <summary>
    public StateHandle SaveStateToMemory()
    {
        var buffer = _statePool.Take();
        try
        {
            _statable.SaveStateBinary(buffer.Writer);
            buffer.Writer.Flush();
        }
        catch
        {
            // The buffer belongs to this method until a StateHandle names it. A core that throws
            // mid-save would otherwise drop a whole state's worth of pool on the floor — 16 MiB on
            // N64 — and the next save allocates a replacement. That is not a leak in the sense that
            // it grows without bound, but it is exactly the large-object-heap churn the pool exists
            // to remove, arriving during a failure rather than in the steady state, and net48
            // neither compacts the LOH nor reclaims it outside a blocking gen2.
            //
            // ExportState has always had this as a `finally` because it returns the buffer
            // unconditionally. This path keeps the buffer on success, so it needs the failure half
            // spelled out separately — which is how the two drifted apart.
            _statePool.Return(buffer);
            throw;
        }
        _statePool.NoteSize(buffer.Stream.Length);
        return new StateHandle(_emulator.Frame, buffer);
    }

    public void LoadStateFromMemory(StateHandle handle)
    {
        var buffer = (StateBuffer)handle.Token;
        buffer.Stream.Position = 0;
        _statable.LoadStateBinary(buffer.Reader);
    }

    /// <summary>Retire the buffer for reuse. Releasing the same one twice is a no-op — see
    /// <see cref="StateBufferPool.Return"/> for what a second entry in the pool would cost.</summary>
    public void ReleaseState(StateHandle handle) => _statePool.Return(handle.Token as StateBuffer);

    /// <summary>Drop every retired buffer. Called when a session ends so a long idle between
    /// sessions does not hold the ring's worth of memory for nothing. Must run AFTER the driver is
    /// disposed — disposing the rollback ring is what pushes its buffers back in here.</summary>
    public void ClearStatePool()
    {
        _statePool.Clear();
        _hashScratch = [];   // sized to the main-memory domain — 8MiB on N64, same argument
    }

    /// <summary>Buffers currently retired and reusable. Reported so a session can show the pool
    /// reaching steady state — once it stops growing, the save path has stopped allocating.</summary>
    public int StatePoolSize => _statePool.Size;

    /// <summary>Buffers created since the session began. This is the number that matters: it should
    /// climb to the ring's size in the first second or two and then stop. If it keeps climbing, the
    /// pool is being outrun and the allocation this exists to remove is still happening.</summary>
    public int StateBuffersAllocated => _statePool.Allocated;

    /// <summary>
    /// Time one savestate and record how the core wrote it, then replay that shape without the
    /// core to separate our cost from its own. See <see cref="SaveWritePathVerdict"/> for what the
    /// three figures mean and why the question is worth asking at all.
    ///
    /// Diagnostics only. The shipping save path is untouched: this wraps the pooled stream in a
    /// decorator for one call rather than adding a branch to a path that runs every frame.
    ///
    /// The buffer is taken from and returned to the same pool the ring uses, so the measured save
    /// pays exactly what a session's save pays — including reusing a warm buffer rather than
    /// allocating one, which is the mistake the capability probe was making until v0.37.0.
    /// </summary>
    public string MeasureSaveWritePath()
    {
        // Warm the pool so the timed save reuses a buffer, as a session in steady state does.
        _statePool.Return(_statePool.Take());

        // The shape, from one save through the counting decorator.
        var histogram = new WriteSizeHistogram();
        var counted = _statePool.Take();
        try
        {
            using var measuring = new MeasuringStream(counted.Stream, histogram);
            using var writer = new BinaryWriter(measuring, Encoding.UTF8, leaveOpen: true);
            _statable.SaveStateBinary(writer);
            writer.Flush();
            _statePool.NoteSize(counted.Stream.Length);
        }
        finally { _statePool.Return(counted); }

        // The cost, from the undecorated path, medianed on the same terms as the two figures it
        // will be compared against — otherwise the comparison is between a warm loop and a cold one.
        var timed = _statePool.Take();
        double actualMs;
        try
        {
            using var writer = new BinaryWriter(timed.Stream, Encoding.UTF8, leaveOpen: true);
            actualMs = MedianOf(() =>
            {
                timed.Stream.SetLength(0);
                timed.Stream.Position = 0;
                _statable.SaveStateBinary(writer);
                writer.Flush();
            });
        }
        finally { _statePool.Return(timed); }

        return SaveWritePathVerdict.Describe(histogram, actualMs,
            ReplayWritePattern(histogram), BlockCopyFloorMs(histogram.Bytes));
    }

    /// <summary>
    /// Median of several timed runs after a warm-up pass.
    ///
    /// <b>Both comparison figures got this wrong on their first outing and it changed the answer.</b>
    /// A freshly allocated array is committed lazily, so the first pass over sixteen megabytes pays
    /// page faults the second does not — and the real save being compared against reuses a warm
    /// pooled buffer, so the comparison charged the alternatives for something the thing they are
    /// compared to never pays. On real N64 the "replay" came out ABOVE the actual save, which is
    /// impossible for the same bytes through less work, and the verdict duly reported the core
    /// doing zero.
    ///
    /// Exactly the mistake the capability probe was making until v0.37.0, in the same shape:
    /// measuring allocation where the shipping path measures reuse. Third time this class of error
    /// has appeared in this codebase, which is why it is written down here rather than just fixed.
    /// </summary>
    private static double MedianOf(Action op, int reps = 7)
    {
        op();                                   // warm: commit the pages, JIT the loop
        var samples = new double[reps];
        for (int i = 0; i < reps; i++)
        {
            var timer = Stopwatch.StartNew();
            op();
            samples[i] = timer.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[reps / 2];
    }

    /// <summary>The same write shape into the same kind of stream, with no core involved — so the
    /// difference from the real save is the core's own work.</summary>
    private static double ReplayWritePattern(WriteSizeHistogram histogram)
    {
        var chunk = new byte[Math.Max(1, histogram.LargestWrite)];
        var plan = histogram.ReplayPlan().ToList();
        using var stream = new MemoryStream((int)Math.Min(int.MaxValue, histogram.Bytes + 4096));
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        return MedianOf(() =>
        {
            // Rewound rather than reallocated, so the buffer stays warm exactly as the pool's does.
            stream.SetLength(0);
            stream.Position = 0;
            foreach (var (size, count) in plan)
                for (long i = 0; i < count; i++) writer.Write(chunk, 0, size);
            writer.Flush();
        });
    }

    /// <summary>One block copy of the same byte count: what merely moving the bytes costs, and
    /// therefore the least any write path could spend.</summary>
    private static double BlockCopyFloorMs(long bytes)
    {
        int size = (int)Math.Min(int.MaxValue, Math.Max(1, bytes));
        var from = new byte[size];
        var to = new byte[size];
        return MedianOf(() => Buffer.BlockCopy(from, 0, to, 0, size));
    }

    public byte[] ExportState()
    {
        // Serialized through the SAME pool the rollback ring uses, then copied out. The caller owns
        // the returned array (it is kept, compressed and sent), so that copy is unavoidable — but
        // the scratch buffer underneath it is not. A fresh MemoryStream here meant ~32MiB of large
        // object heap per whole-state transfer on N64 rather than ~16MiB, and every resync is a
        // moment the session is already struggling: net48 neither compacts the LOH nor reclaims it
        // outside a blocking gen2, which is the same 52.5ms-pause bill the pool was built to stop.
        var buffer = _statePool.Take();
        try
        {
            _statable.SaveStateBinary(buffer.Writer);
            buffer.Writer.Flush();
            _statePool.NoteSize(buffer.Stream.Length);
            return buffer.Stream.ToArray();
        }
        finally { _statePool.Return(buffer); }
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
    public uint HashMainMemory(int salt = 0) => HashMainMemoryCore(salt, null, out _);

    /// <summary>See <see cref="IEmuAdapter.TryHashMainMemoryBuckets"/>. The buckets come off the
    /// same pass over the same bytes as the hash, so the pair always describes one state.</summary>
    public bool TryHashMainMemoryBuckets(int salt, uint[] buckets, out uint hash)
    {
        if (buckets == null || buckets.Length != ControlMessageCodec.DivergenceBuckets)
            throw new ArgumentException("bucket sink has the wrong shape", nameof(buckets));
        hash = HashMainMemoryCore(salt, buckets, out bool filled);
        return filled;
    }

    private uint HashMainMemoryCore(int salt, uint[]? buckets, out bool bucketsFilled)
    {
        bucketsFilled = false;
        var domain = MainMemoryDomain();
        if (domain != null)
        {
            try
            {
                long size = domain.Size;
                if (size > 0 && size <= int.MaxValue)
                {
                    var timer = Stopwatch.StartNew();
                    uint result = HashOneDomain(domain, salt, buckets, applyExclusions: true,
                        out bucketsFilled, out string how);

                    // A core that emulates several machines registers one Main RAM per machine and
                    // nominates none, so MainMemory resolves to the FIRST — machine A, with B/C/D
                    // never read. Folding the siblings in is what makes a divergence confined to
                    // another player's Game Boy visible at all; see MainMemoryCoverage for why it
                    // is the siblings rather than every registered domain.
                    //
                    // Sequence-sensitive on purpose, over a list sorted by machine letter: the fold
                    // is what makes "A diverged" distinguishable from "B diverged", and the sort is
                    // what stops two peers folding in different orders.
                    var siblings = SiblingMachineDomains();
                    for (int i = 0; i < siblings.Count; i++)
                    {
                        uint other = HashOneDomain(siblings[i], salt, null, applyExclusions: false,
                            out _, out _);
                        result = (result ^ other) * 16777619u;
                    }
                    if (siblings.Count > 0) how += $" +{siblings.Count}mach";

                    // Report the cost the first time so a regression here is attributable rather
                    // than showing up as an unexplained slow tick.
                    if (HashDiagnostic == null)
                        HashDiagnostic = $"checksum: {how} domain '{domain.Name}' {size / 1024}KiB " +
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

    /// <summary>
    /// Hash exactly one domain, by whichever of the five paths its type affords.
    ///
    /// Split out of <see cref="HashMainMemoryCore"/> so a multi-machine core can run it once per
    /// machine. Exclusions and the divergence buckets belong to the PRIMARY domain only, which is
    /// why they are parameters rather than read here: the framebuffer mask is an N64 concept and
    /// N64 has one machine, so no core has ever needed both at once — and a sibling silently
    /// inheriting the primary's mask ranges would exclude arbitrary bytes of another machine.
    /// </summary>
    private uint HashOneDomain(MemoryDomain domain, int salt, uint[]? buckets, bool applyExclusions,
        out bool bucketsFilled, out string how)
    {
        bucketsFilled = false;
        long size = domain.Size;
        int length = (int)size;
        uint result;
        ResolveDomainAccess(domain);
        // Waterbox-backed domains only expose their memory while activated, and the
        // Monitor variants guard it with a lock; Enter/Exit is a no-op for the plain
        // native cores.
        domain.Enter();
        try
        {
            // Bytes some machine produced for itself — GPU output a video plugin
            // resolved back into console RAM — which the hash must skip or two
            // perfectly synchronized peers disagree forever. The learned mask (built
            // by measurement; see DivergenceLearner) outranks the VI-register guess it
            // replaced; the guess remains the pre-learn default.
            var (exclusions, exclusionSeed, exclusionTag) = applyExclusions
                ? ResolveExclusions(size, salt)
                : (Array.Empty<long>(), 0UL, "");

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
                result = MemoryHash.Fnv1a64(_hashScratch, length, MemoryHash.PathPtr, exclusions, exclusionSeed);
                bucketsFilled = MemoryHash.FillBuckets(_hashScratch, length, buckets);
                how = "ptr";
            }
            else if (array != null && array.Length >= length)
            {
                // Array-backed domain (the Hawk cores' RAM): the bytes are already
                // managed, so hash them where they sit. No copy at all.
                result = MemoryHash.Fnv1a64(array, length, MemoryHash.PathArray, exclusions, exclusionSeed);
                bucketsFilled = MemoryHash.FillBuckets(array, length, buckets);
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
                    result = HashByWord(domain, size, salt, exclusions, out int fallbackStride);
                    how = fallbackStride > 1 ? $"word/{fallbackStride}" : "word";
                }
                else
                {
                    result = MemoryHash.Fnv1a64(_hashScratch, length, MemoryHash.PathClosure, exclusions, exclusionSeed);
                    bucketsFilled = MemoryHash.FillBuckets(_hashScratch, length, buckets);
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
                result = MemoryHash.Fnv1a64(_hashScratch, length, MemoryHash.PathBulk, exclusions, exclusionSeed);
                bucketsFilled = MemoryHash.FillBuckets(_hashScratch, length, buckets);
                how = "bulk";
            }
            else
            {
                result = HashByWord(domain, size, salt, exclusions, out int stride);
                how = stride > 1 ? $"word/{stride}" : "word";
            }
            how += exclusionTag;
        }
        finally { domain.Exit(); }
        return result;
    }

    /// <summary>
    /// Every OTHER machine's main memory on a multi-machine core, in a fixed order. Empty — and
    /// therefore free — for every ordinary core, which is all of them but the link cables.
    ///
    /// Resolved once. The domain objects are stable for a core's lifetime (BizHawk mutates them in
    /// place on a state load rather than replacing them), which is the same property the access
    /// resolution above relies on.
    /// </summary>
    private List<MemoryDomain> SiblingMachineDomains()
    {
        if (_siblingMachines != null) return _siblingMachines;
        // Through MainMemoryDomain() rather than the field, so the service is resolved first: an
        // early caller would otherwise cache an empty list permanently and the siblings would never
        // be hashed again for the rest of the session.
        var primary = MainMemoryDomain();
        _siblingMachines = new List<MemoryDomain>();
        try
        {
            var domains = _memoryDomains;
            if (domains == null || primary == null) return _siblingMachines;
            var names = new List<string>();
            foreach (var d in domains) names.Add(d.Name);
            foreach (var siblingName in MainMemoryCoverage.SiblingMachines(primary.Name, names))
            {
                var found = domains[siblingName];
                // Skip anything unhashable rather than throwing: a sibling that cannot be read is a
                // coverage gap the diagnostic reports, not a reason to lose the checksum entirely.
                if (found != null && found.Size > 0 && found.Size <= int.MaxValue)
                    _siblingMachines.Add(found);
            }
        }
        catch { _siblingMachines = new List<MemoryDomain>(); }
        return _siblingMachines;
    }

    private List<MemoryDomain>? _siblingMachines;

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

    /// <summary>
    /// Why the desync checksum cannot see this core's whole machine, or null when it can.
    ///
    /// Null now for the link cores too: <see cref="SiblingMachineDomains"/> folds every emulated
    /// machine's RAM into the checksum, so the blindness this used to refuse over is gone. It
    /// returns a refusal only if a sibling machine is registered but unreadable — the same gap for
    /// the same reason, arrived at a different way, and still not something to run a session on.
    /// </summary>
    public string? MainMemoryCoverageGap()
    {
        try
        {
            var domain = MainMemoryDomain();
            var domains = _memoryDomains;
            if (domain == null || domains == null) return null;
            var names = new List<string>();
            foreach (var d in domains) names.Add(d.Name);
            if (!MainMemoryCoverage.IsSingleMachineSlice(domain.Name, names)) return null;

            var expected = MainMemoryCoverage.SiblingMachines(domain.Name, names);
            var hashed = SiblingMachineDomains();
            if (hashed.Count == expected.Count) return null;   // every machine is covered

            var missing = new List<string>(expected);
            foreach (var d in hashed) missing.Remove(d.Name);
            return $"this core emulates more than one machine and {string.Join(", ", missing)} " +
                   "cannot be read, so a divergence confined to that machine would be invisible — " +
                   "every checksum would agree while the session was already broken. Netplay " +
                   "refuses rather than run with detection blind to part of the state it guards.";
        }
        catch { return null; } // a probe that cannot answer must not be the thing that refuses
    }

    /// <summary>How many emulated machines the checksum covers, for the session log. 1 on every
    /// ordinary core; 2-4 on a link cable.</summary>
    public int HashedMachineCount => 1 + SiblingMachineDomains().Count;

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

    // --- What the checksum must not read: learned mask, or the VI-register guess ----

    // The learned exclusion (see DivergenceLearner): buckets of main memory MEASURED to hold
    // machine-dependent bytes, published by the host with the frame it takes effect from. Both
    // sides derive identical byte ranges from the identical domain size, and the mask identity is
    // folded into the hash so peers on different masks produce obviously different values.
    private bool[]? _learnedMaskBuckets;
    private int _learnedMaskFrom;
    private ulong _learnedMaskSeed;
    private long[] _learnedRanges = [];
    private long _learnedRangesForSize = -1;

    /// <summary>Adopt a learned mask, effective for checksums describing frames at or past
    /// <paramref name="effectiveFromFrame"/> — the same switch-over point on every peer, far
    /// enough ahead that nobody has already hashed a boundary past it the old way. An all-false
    /// mask clears (equivalent to <see cref="ClearLearnedExclusion"/>).</summary>
    public void SetLearnedExclusion(bool[] maskBuckets, int effectiveFromFrame)
    {
        int set = 0;
        foreach (bool b in maskBuckets) if (b) set++;
        if (set == 0) { ClearLearnedExclusion(); return; }

        _learnedMaskBuckets = (bool[])maskBuckets.Clone();
        _learnedMaskFrom = effectiveFromFrame;
        _learnedRangesForSize = -1;
        // The mask's identity, folded into every hash it shapes: bitmap plus switch-over frame,
        // so two peers on different masks — or the same mask from different frames — can never
        // compare values as though they described the same bytes.
        const ulong prime = 1099511628211UL;
        ulong seed = 14695981039346656037UL;
        seed = (seed ^ (uint)effectiveFromFrame) * prime;
        for (int i = 0; i < maskBuckets.Length; i++)
            if (maskBuckets[i]) seed = (seed ^ (uint)i) * prime;
        _learnedMaskSeed = seed;
    }

    /// <summary>Drop the learned mask. Called on every driver rebuild: frame numbers restart at
    /// zero there, every peer clears at the same generation boundary, and the next learn round —
    /// which every rebuild begins, standing on freshly identical memory — replaces it.</summary>
    public void ClearLearnedExclusion()
    {
        _learnedMaskBuckets = null;
        _learnedMaskFrom = 0;
        _learnedMaskSeed = 0;
        _learnedRangesForSize = -1;
    }

    /// <summary>Whether a learned mask is present (for the session log).</summary>
    public bool HasLearnedExclusion => _learnedMaskBuckets != null;

    /// <summary>Size of the domain the checksum reads, so masked buckets can be named as
    /// addresses. 0 when the domain is not reachable.</summary>
    public long MainMemorySize
    {
        get { try { return MainMemoryDomain()?.Size ?? 0; } catch { return 0; } }
    }

    /// <summary>
    /// The byte ranges this hash must skip, with the seed contribution and log tag that identify
    /// them. The learned mask outranks the VI-register span: measurement beats the guess, and the
    /// guess (v0.30) is structurally incomplete — VI_ORIGIN names the buffer being scanned out
    /// while the plugin writes to the one just rendered. The span remains the pre-learn default,
    /// so the first boundaries of a session keep v0.30's behaviour until measurement replaces it.
    /// </summary>
    private (long[] ranges, ulong seed, string tag) ResolveExclusions(long size, int salt)
    {
        var mask = _learnedMaskBuckets;
        if (mask != null && salt >= _learnedMaskFrom)
        {
            if (_learnedRangesForSize != size)
            {
                var spans = DivergenceLearner.MaskRanges(mask, size);
                var flat = new long[spans.Count * 2];
                for (int i = 0; i < spans.Count; i++)
                {
                    flat[2 * i] = spans[i].Start;
                    flat[2 * i + 1] = spans[i].EndExclusive;
                }
                _learnedRanges = flat;
                _learnedRangesForSize = size;
            }
            if (_learnedRanges.Length > 0)
            {
                long masked = 0;
                for (int i = 0; i < _learnedRanges.Length; i += 2)
                    masked += _learnedRanges[i + 1] - _learnedRanges[i];
                return (_learnedRanges, _learnedMaskSeed,
                    $" -mask{_learnedRanges.Length / 2}r/{masked / 1024}KiB");
            }
        }

        if (TryGetFramebufferSpan(size, out long exStart, out long exEnd))
            return (new[] { exStart, exEnd }, 0UL,
                $" -fb@{exStart / 1024}KiB+{(exEnd - exStart) / 1024}KiB");
        return (Array.Empty<long>(), 0UL, "");
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
        long[] exRanges, out int stride)
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
        h = MemoryHash.SeedWithExclusions(h, MemoryHash.PathWord, exRanges, 0);

        // Every exclusion bound is word-aligned, so a whole word is either in an excluded range or
        // out of it — the sample never has to split one. Addresses only ever increase, so the
        // range cursor advances rather than searching.
        int range = 0;
        for (long w = offset; w < words; w += stride)
        {
            long at = w * 4;
            while (range < exRanges.Length && at >= exRanges[range + 1]) range += 2;
            if (range < exRanges.Length && at >= exRanges[range]) continue;
            h = (h ^ domain.PeekUint(at, false)) * prime;
        }

        // Trailing bytes past the last whole word, only when reading everything anyway.
        if (stride == 1)
            for (long a = words * 4; a < size; a++)
            {
                while (range < exRanges.Length && a >= exRanges[range + 1]) range += 2;
                if (range < exRanges.Length && a >= exRanges[range]) continue;
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

}
