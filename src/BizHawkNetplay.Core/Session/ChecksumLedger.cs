using System.Collections.Generic;
using BizHawkNetplay.Core.Net;

namespace BizHawkNetplay.Core.Session;

/// <summary>What a recorded checksum report concluded for its frame.</summary>
public enum ChecksumOutcome
{
    /// <summary>Not every player has reported this frame yet.</summary>
    Pending,
    /// <summary>Every player reported and all hashes match.</summary>
    Agreement,
    /// <summary>Every player reported and at least one hash differs — a desync.</summary>
    Mismatch,
}

/// <summary>
/// The host's desync-detection ledger: checksum reports keyed
/// <c>generation → frame → sourcePort</c>, resolved to a verdict once every player has reported
/// a frame. Extracted from the tool form so the aggregation rules are unit-testable. The keying
/// is what stops a stale reader or a duplicated packet from fabricating agreement or desync:
/// a re-report from the same port overwrites (never double-counts toward the player total), a
/// report from a retired generation can never meet a current one at the same frame, and a
/// resolved frame is removed so late duplicates start a fresh (incompletable) entry.
///
/// NOT thread-safe — the caller serializes access (the tool records under its hash lock from
/// both the UI thread and control-reader threads).
/// </summary>
public sealed class ChecksumLedger
{
    // Bound memory when a peer never reports a frame: once more than MaxOpenFrames entries are
    // open for a generation, entries older than StaleFrameAge frames behind the newest report
    // are dropped.
    private const int MaxOpenFrames = 32;
    private const int StaleFrameAge = 600;

    private readonly Dictionary<SessionGeneration, Dictionary<int, Dictionary<int, uint>>> _byGeneration =
        new();

    /// <summary>
    /// Record one player's checksum for a frame and resolve the frame if this completed it.
    /// Reports for any generation other than <paramref name="generation"/> are retired — the
    /// caller only ever records for the live generation, so anything else is a dead timeline.
    /// </summary>
    public ChecksumOutcome Record(SessionGeneration generation, int sourcePort, int frame, uint hash, int playerCount)
        => Record(generation, sourcePort, frame, hash, playerCount, playerCount);

    /// <summary>
    /// As above, with the port range and the report count that resolves a frame stated apart.
    /// They diverge the moment a seat is vacated: the session still spans
    /// <paramref name="portRange"/> controller ports — reports arrive tagged with their original
    /// port numbers — but only <paramref name="expectedReports"/> players exist to report, and
    /// waiting for the full range would leave every frame pending forever.
    /// </summary>
    public ChecksumOutcome Record(SessionGeneration generation, int sourcePort, int frame, uint hash,
        int portRange, int expectedReports)
    {
        // Defensive, never throwing: Record runs on control-reader threads under the caller's
        // lock — a nonsense player count must degrade to "nothing resolves", not an exception.
        if (portRange < 1 || expectedReports < 1) return ChecksumOutcome.Pending;
        if (sourcePort < 0 || sourcePort >= portRange) return ChecksumOutcome.Pending;

        if (!_byGeneration.TryGetValue(generation, out var frames))
        {
            frames = new Dictionary<int, Dictionary<int, uint>>();
            _byGeneration[generation] = frames;
        }
        if (!frames.TryGetValue(frame, out var reports))
        {
            reports = new Dictionary<int, uint>();
            frames[frame] = reports;
        }
        reports[sourcePort] = hash;

        var outcome = ChecksumOutcome.Pending;
        if (reports.Count >= expectedReports)
        {
            outcome = ChecksumOutcome.Agreement;
            bool haveFirst = false;
            uint first = 0;
            foreach (uint report in reports.Values)
            {
                if (!haveFirst) { first = report; haveFirst = true; }
                else if (report != first) { outcome = ChecksumOutcome.Mismatch; break; }
            }
            frames.Remove(frame);
        }

        if (frames.Count > MaxOpenFrames)
        {
            var stale = new List<int>();
            foreach (var k in frames.Keys) if (k < frame - StaleFrameAge) stale.Add(k);
            foreach (var k in stale) frames.Remove(k);
        }

        if (_byGeneration.Count > 1)
        {
            var retired = new List<SessionGeneration>();
            foreach (var key in _byGeneration.Keys) if (key != generation) retired.Add(key);
            foreach (var key in retired) _byGeneration.Remove(key);
        }

        return outcome;
    }

    /// <summary>Frames still awaiting reports for a generation (diagnostics/tests).</summary>
    public int OpenFrames(SessionGeneration generation) =>
        _byGeneration.TryGetValue(generation, out var frames) ? frames.Count : 0;

    /// <summary>Forget everything (session start, resync rebuild, teardown).</summary>
    public void Clear() => _byGeneration.Clear();
}
