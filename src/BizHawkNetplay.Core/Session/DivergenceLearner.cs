using System;
using System.Collections.Generic;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Whether a session is allowed to act on a learned mask at all.
///
/// <b>Why this gate exists.</b> Excluding memory from the checksum is only safe while the excluded
/// bytes are an OUTPUT — something a machine produced for itself that nothing downstream consumes.
/// That is not guaranteed. Rice's <c>FBRead</c> fires when emulated CPU code reads framebuffer
/// memory and copies rendered data back into RDRAM, so GPU-produced bytes can become genuinely
/// game-readable causal state. A startup disagreement proves the bytes are machine-dependent; it
/// does not prove the game never reads them.
///
/// So masking is not a correctness fix, it is a trade: on the many games that never read their
/// framebuffer it turns an unplayable above-native session into a working one, and on the few that
/// do it can hide real divergence. A trade like that is the player's to make knowingly, not the
/// tool's to make silently — hence an explicit opt-in, and hence native resolution never masking
/// anything no matter what is measured.
/// </summary>
public enum MaskPolicy
{
    /// <summary>Never exclude. Divergence vectors are still collected and their ranges logged —
    /// the diagnostic is free and answers "which bytes" for every desync.</summary>
    DiagnosticOnly,

    /// <summary>Exclude what was measured, because the player opted into above-native play and
    /// accepted that a game reading its own framebuffer may desync undetected.</summary>
    ExperimentalExclude,
}

/// <summary>What the learner concluded once its rounds completed.</summary>
public enum DivergenceVerdict
{
    /// <summary>Still collecting rounds.</summary>
    Learning,
    /// <summary>Every bucket agreed in every round — nothing is machine-dependent here. The
    /// checksum keeps reading everything, exactly as it always has (the native-resolution case).</summary>
    NothingDiverges,
    /// <summary>A bounded set of buckets disagreed: machine-produced bytes (a video plugin
    /// resolving GPU output into console RAM). Exclude them and the rest of memory can be
    /// compared honestly at any resolution.</summary>
    MaskLearned,
    /// <summary>Too much of memory disagreed for a framebuffer to explain. That is a real desync,
    /// not a render artifact — refuse to learn a mask that would blind the checksum to it, and
    /// let the ordinary desync machinery do its job.</summary>
    TooBroadToMask,
}

/// <summary>
/// Learns which byte ranges of main memory are machine-dependent, by measurement instead of
/// inference — the fix KI-14/KI-15 called for.
///
/// The insight that makes this trustworthy: right after every authoritative rebuild — session
/// start, a resync, a rejoin — all peers are byte-identical by construction. Over the next few
/// checksum boundaries, any bucket that disagrees between peers can only hold bytes some machine
/// produced for itself: on N64 above native resolution, the framebuffer a video plugin resolves
/// back into RDRAM from the GPU. Reading VI registers to find that region was tried (v0.30) and is
/// structurally insufficient — VI_ORIGIN names the buffer being scanned out while the plugin
/// writes to the one just rendered, and the render target's address lives inside the plugin where
/// no register exposes it. Comparing what actually differs answers the real question for any
/// game, plugin or resolution, and self-disables at native (nothing diverges, no mask).
///
/// The union over several rounds is what catches double-buffering — the two framebuffers
/// alternate roles per boundary, so one round sees one of them. The share cap is what keeps a
/// REAL desync from being learned away: emulation divergence spreads through memory within
/// frames, so it blows past any plausible framebuffer footprint and lands in
/// <see cref="DivergenceVerdict.TooBroadToMask"/> instead of a mask.
/// </summary>
public sealed class DivergenceLearner
{
    /// <summary>Boundaries compared before concluding. Three catches both halves of a
    /// double-buffered pair with one to spare, and costs ~15 seconds of learning at the default
    /// checksum interval — during which checksum mismatches are attributed here, not to desync.</summary>
    public const int LearnRounds = 3;

    /// <summary>Largest share of buckets a mask may cover. Framebuffers measure ~2-15% of RDRAM;
    /// half of memory disagreeing is a broken core, and masking it would disable the very
    /// detection this feature exists to preserve.</summary>
    public const double MaxMaskedShare = 0.25;

    private readonly int _expectedReports;
    private readonly Dictionary<int, Dictionary<int, uint[]>> _byFrame = new();
    private readonly bool[] _mask = new bool[ControlMessageCodec.DivergenceBuckets];
    private readonly List<int> _learnedFrames = new();

    /// <param name="expectedReports">Vectors that complete one frame — the ACTIVE player count,
    /// host included. Vacated seats report nothing.</param>
    public DivergenceLearner(int expectedReports)
    {
        if (expectedReports < 2) throw new ArgumentOutOfRangeException(nameof(expectedReports));
        _expectedReports = expectedReports;
    }

    /// <summary>Frames still worth reporting for: a peer sends vectors for its first
    /// <see cref="LearnRounds"/> checksum boundaries of a generation and then stops.</summary>
    public static bool IsLearnFrame(int frame, int checksumInterval) =>
        checksumInterval > 0 && frame > 0 && frame <= LearnRounds * checksumInterval;

    public DivergenceVerdict Verdict { get; private set; } = DivergenceVerdict.Learning;

    /// <summary>The learned mask. Meaningful only after <see cref="DivergenceVerdict.MaskLearned"/>.</summary>
    public bool[] MaskBuckets => (bool[])_mask.Clone();

    /// <summary>Share of buckets the mask covers so far.</summary>
    public double MaskedShare
    {
        get
        {
            int set = 0;
            foreach (bool b in _mask) if (b) set++;
            return (double)set / _mask.Length;
        }
    }

    /// <summary>The boundaries that completed, for the log.</summary>
    public IReadOnlyList<int> LearnedFrames => _learnedFrames;

    /// <summary>
    /// Record one peer's vector for one boundary. Never throws — this runs beside the checksum
    /// ledger on reader threads, and a malformed report must cost its sender the report, not the
    /// session. Returns the verdict as of this record; once terminal it never changes.
    /// </summary>
    public DivergenceVerdict Record(int frame, int port, uint[] buckets)
    {
        if (Verdict != DivergenceVerdict.Learning) return Verdict;
        if (frame < 0 || port < 0 || buckets == null
            || buckets.Length != ControlMessageCodec.DivergenceBuckets) return Verdict;

        if (!_byFrame.TryGetValue(frame, out var reports))
            _byFrame[frame] = reports = new Dictionary<int, uint[]>();
        reports[port] = buckets;
        if (reports.Count < _expectedReports) return Verdict;

        // A boundary is complete: any bucket where any two peers disagree is machine-dependent.
        _byFrame.Remove(frame);
        _learnedFrames.Add(frame);
        uint[]? first = null;
        foreach (var vector in reports.Values)
        {
            if (first == null) { first = vector; continue; }
            for (int i = 0; i < _mask.Length; i++)
                if (vector[i] != first[i]) _mask[i] = true;
        }

        if (_learnedFrames.Count < LearnRounds) return Verdict;

        double share = MaskedShare;
        Verdict = share == 0 ? DivergenceVerdict.NothingDiverges
            : share > MaxMaskedShare ? DivergenceVerdict.TooBroadToMask
            : DivergenceVerdict.MaskLearned;
        return Verdict;
    }

    /// <summary>
    /// Name the masked buckets as byte ranges of a domain, for the log — the answer KI-15 wanted
    /// every desync report to carry: WHICH bytes, not just "some byte". The ranges are also what
    /// the checksum skips, so every one must be a real span of the domain.
    ///
    /// Both ends are clamped and empty runs dropped, because <see cref="BucketSpan"/> rounds UP to
    /// a word: whenever <c>buckets * span</c> overshoots the domain — any size that is not a whole
    /// number of word-aligned buckets — the trailing buckets START past the end. Clamping only the
    /// end (as this did) turns such a bucket into an inverted range whose length is negative.
    /// The bucket vectors never set those buckets, so no session has produced one; the mask
    /// arrives over the wire, and a range that runs backwards is not something to hand a hasher on
    /// the strength of the sender having behaved.
    /// </summary>
    public static List<(long Start, long EndExclusive)> MaskRanges(bool[] maskBuckets, long domainSize)
    {
        var ranges = new List<(long, long)>();
        if (maskBuckets == null || domainSize <= 0) return ranges;
        long span = BucketSpan(domainSize, maskBuckets.Length);
        long runStart = -1;
        for (int i = 0; i <= maskBuckets.Length; i++)
        {
            bool set = i < maskBuckets.Length && maskBuckets[i];
            if (set && runStart < 0) runStart = Math.Min(domainSize, i * span);
            else if (!set && runStart >= 0)
            {
                long end = Math.Min(domainSize, (long)i * span);
                if (end > runStart) ranges.Add((runStart, end));
                runStart = -1;
            }
        }
        return ranges;
    }

    /// <summary>Bytes per bucket for a domain, word-aligned so a bucket boundary never splits an
    /// aligned word — every peer derives the identical slicing from the identical domain size.</summary>
    public static long BucketSpan(long domainSize, int buckets) =>
        ((domainSize + buckets - 1) / buckets + 3) & ~3L;

    /// <summary>
    /// Whether a learned mask may actually be applied, given what the session is doing.
    ///
    /// Two gates, and both must open. The session must be rendering ABOVE native — at native the
    /// write-back bytes already agree, so a mask there would exclude memory for no benefit and
    /// could only ever hide something. And the player must have opted in, because excluding memory
    /// trades detection for playability on a game that might read its own framebuffer (see
    /// <see cref="MaskPolicy"/>).
    ///
    /// <paramref name="aboveNativeResolution"/> is null when the core exposes no resolution to
    /// read, which is every core but N64 — those get DiagnosticOnly, since the phenomenon the mask
    /// exists for is a render-resolution artifact and a mask elsewhere would be masking something
    /// this reasoning does not cover.
    /// </summary>
    public static MaskPolicy ChoosePolicy(bool optedIn, bool? aboveNativeResolution) =>
        optedIn && aboveNativeResolution == true
            ? MaskPolicy.ExperimentalExclude
            : MaskPolicy.DiagnosticOnly;
}
