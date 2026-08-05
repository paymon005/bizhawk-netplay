using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// What the mesh knows about each candidate address: whether anything has come back from it
/// lately, how long the round trip takes, and — the part that matters per frame — which of a
/// peer's addresses input should be sent to right now.
///
/// <b>Why this is its own type.</b> It was six concurrent dictionaries and a burst deadline inside
/// <see cref="MeshUdpTransport"/>, and the ranking rule that consumes them is the single most
/// consequential decision in the transport: pick a dead address and that peer's input stops, with
/// no error anywhere — the session just starts stalling and rolling back. Reaching it needed real
/// sockets, real NAT behaviour and real timing, so it was never tested. Here it is state and
/// arithmetic over a caller-supplied clock, which is all it ever was.
///
/// <b>Threading.</b> The tables are concurrent because they genuinely are: the socket thread
/// records what arrives, the punch thread records what it probed, and the frame thread selects.
/// The selection reads several tables without a lock, which is correct for the same reason it was
/// before — every value it reads is monotonic in the direction that matters, and a selection made
/// against a reading one tick stale is a selection the next frame revisits.
/// </summary>
public sealed class MeshLinkQuality
{
    /// <summary>No traffic for this long and the path is considered down again.</summary>
    public const int AliveWindowMs = 8000;

    /// <summary>
    /// Stricter than plain liveness, for choosing where to send.
    ///
    /// Keepalive acks arrive at least every ~1.25s on a healthy path, and input at frame rate on
    /// the active one, so a candidate not heard from in this long has very likely died. Failing
    /// over to a sibling that is still answering beats waiting out the full alive window on a black
    /// hole — a wait that races the UDP-lost session watchdog.
    /// </summary>
    public const int FreshWindowMs = 2500;

    /// <summary>Samples per candidate: about 1.4s of pre-GO burst at the burst cadence.</summary>
    public const int RttWindowSamples = 24;

    private readonly ConcurrentDictionary<IPEndPoint, long> _alive = new();
    private readonly ConcurrentDictionary<IPEndPoint, long> _lastPunch = new();
    private readonly ConcurrentDictionary<IPEndPoint, double> _rtt = new();
    // Raw sample window per candidate, kept alongside the EMA above. The EMA is what send-path
    // selection wants (one smooth number); a delay decision wants the distribution, because what
    // stalls a session is the worst packet rather than the typical one.
    private readonly ConcurrentDictionary<IPEndPoint, RttWindow> _rttWindows = new();
    // Last candidate input was actually sent through, per logical peer — the failover anchor while
    // a repunch has the liveness table cleared.
    private readonly ConcurrentDictionary<int, IPEndPoint> _lastSelected = new();
    private long _burstUntilMs = long.MinValue;

    /// <summary>
    /// A bounded ring of raw round-trip samples for one candidate, summarized the same way the
    /// control-channel lobby probe summarizes its own: median for the settled cost, nearest-rank
    /// 85th percentile for the high-water mark. Using the same statistic on both transports is what
    /// makes a UDP reading and a TCP reading comparable enough to take the worst of.
    /// </summary>
    private sealed class RttWindow
    {
        private readonly double[] _samples = new double[RttWindowSamples];
        private int _count;
        private int _next;

        public void Add(double sample)
        {
            lock (_samples)
            {
                _samples[_next] = sample;
                _next = (_next + 1) % RttWindowSamples;
                if (_count < RttWindowSamples) _count++;
            }
        }

        public bool TryDescribe(out double medianMs, out double highMs)
        {
            double[] sorted;
            lock (_samples)
            {
                if (_count == 0) { medianMs = 0; highMs = 0; return false; }
                sorted = new double[_count];
                Array.Copy(_samples, sorted, _count);
            }
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            medianMs = sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) / 2.0
                : sorted[middle];
            int rank = (int)Math.Ceiling(0.85 * sorted.Length);
            if (rank < 1) rank = 1;
            if (rank > sorted.Length) rank = sorted.Length;
            highMs = sorted[rank - 1];
            if (highMs < medianMs) highMs = medianMs;
            return true;
        }
    }

    // ---------------------------------------------------------------- liveness

    /// <summary>Something arrived from this endpoint. The one signal every judgement below rests on.</summary>
    public void MarkHeard(IPEndPoint endpoint, long nowMs) => _alive[endpoint] = nowMs;

    /// <summary>A direct path to this candidate is currently open — it answered a probe or sent
    /// input inside <see cref="AliveWindowMs"/>.</summary>
    public bool IsAlive(IPEndPoint? endpoint, long nowMs) =>
        endpoint != null && _alive.TryGetValue(endpoint, out long heard) && nowMs - heard < AliveWindowMs;

    /// <summary>Heard from inside the stricter <see cref="FreshWindowMs"/> — see why that is a
    /// separate question from being alive.</summary>
    public bool IsFresh(IPEndPoint? endpoint, long nowMs) =>
        endpoint != null && _alive.TryGetValue(endpoint, out long heard) && nowMs - heard < FreshWindowMs;

    /// <summary>Whether anything has ever been heard from this endpoint at all.</summary>
    public bool HasBeenHeard(IPEndPoint endpoint) => _alive.ContainsKey(endpoint);

    /// <summary>Drop one endpoint's liveness and probe schedule — a learned binding was replaced,
    /// or its seat left the session.</summary>
    public void Forget(IPEndPoint endpoint)
    {
        _alive.TryRemove(endpoint, out _);
        _lastPunch.TryRemove(endpoint, out _);
    }

    /// <summary>Force every path to re-prove itself: a repunch, or a rebind after a NAT change.
    /// Measurements are deliberately kept — a path that comes back is the same path.</summary>
    public void ForgetAllLiveness()
    {
        _alive.Clear();
        _lastPunch.Clear();
    }

    // ---------------------------------------------------------------- round trip

    /// <summary>Fold one measured sample into both the smoothed figure and the sample window.</summary>
    public void RecordRtt(IPEndPoint endpoint, double sampleMs)
    {
        // Same EMA shape as the control-channel ping, so the two readings are comparable.
        _rtt.AddOrUpdate(endpoint, sampleMs, (_, prev) => 0.8 * prev + 0.2 * sampleMs);
        _rttWindows.GetOrAdd(endpoint, _ => new RttWindow()).Add(sampleMs);
    }

    /// <summary>Smoothed round trip on this exact path. False until something has answered.</summary>
    public bool TryGetRtt(IPEndPoint? endpoint, out double rttMs)
    {
        rttMs = 0;
        return endpoint != null && _rtt.TryGetValue(endpoint, out rttMs);
    }

    /// <summary>The sample window's settled cost and high-water mark. False until a probe has been
    /// answered.</summary>
    public bool TryGetStats(IPEndPoint? endpoint, out double medianMs, out double highMs)
    {
        medianMs = 0;
        highMs = 0;
        return endpoint != null
            && _rttWindows.TryGetValue(endpoint, out var window)
            && window.TryDescribe(out medianMs, out highMs);
    }

    // ---------------------------------------------------------------- probe schedule

    /// <summary>When this endpoint was last probed; false if never.</summary>
    public bool TryGetLastPunch(IPEndPoint endpoint, out long atMs) =>
        _lastPunch.TryGetValue(endpoint, out atMs);

    public void MarkPunched(IPEndPoint endpoint, long nowMs) => _lastPunch[endpoint] = nowMs;

    /// <summary>
    /// Probe hard for a while and start every sample window over.
    ///
    /// Called once per peer in the lobby, before GO. The steady-state cadences exist to hold NAT
    /// mappings open rather than to characterize a link — four samples a second per edge is too few
    /// to separate a link's settled cost from its jitter — so a short burst buys a proper sample set
    /// on the path input will actually ride. The probe schedule is cleared too, so the first burst
    /// tick fires immediately instead of waiting out a keepalive.
    /// </summary>
    public void BeginBurst(long nowMs, int durationMs)
    {
        if (durationMs < 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
        _rttWindows.Clear();
        _lastPunch.Clear();
        Interlocked.Exchange(ref _burstUntilMs, nowMs + durationMs);
    }

    public bool InBurst(long nowMs) => nowMs < Interlocked.Read(ref _burstUntilMs);

    // ---------------------------------------------------------------- selection

    /// <summary>The candidate input was last actually sent through for a peer.</summary>
    public bool TryGetLastSelected(int remotePort, out IPEndPoint endpoint) =>
        _lastSelected.TryGetValue(remotePort, out endpoint!);

    /// <summary>
    /// Which address to send this peer's input to right now, or null to broadcast to all of them.
    ///
    /// The ranking, in order, and every step of it is load-bearing:
    ///
    /// <list type="number">
    /// <item>A LEARNED endpoint that is alive outranks everything. It is the only address we have
    /// OBSERVED this peer arriving from, and for a symmetric-NAT peer none of the advertised
    /// candidates can ever work — without this the learning would be recorded and never used.</item>
    /// <item>Among advertised candidates, prefer ones heard from RECENTLY over ones merely alive. A
    /// path that dies mid-session keeps its stale, low RTT and stays inside the alive window for a
    /// while; if a sibling is still answering keepalives, input has to move rather than stay pinned
    /// to a black hole.</item>
    /// <item>Within each of those tiers, lowest RTT; failing any measurement, first listed.</item>
    /// <item>Nothing confirmed — start-up, or a repunch just cleared liveness — falls back to the
    /// last path that actually worked. For an internet peer the first advertised candidate is
    /// typically the pre-NAT address, which is exactly the one that does NOT work when the
    /// reflexive path was carrying the session. A learned endpoint counts as valid here even though
    /// it is never among the candidates: for a symmetric-NAT peer it is the only address that
    /// works, and rejecting it stopped input to that peer entirely the moment liveness lapsed.</item>
    /// <item>Nothing has ever worked, so no single address is a safe guess: null, and the caller
    /// broadcasts until a punch confirms one.</item>
    /// </list>
    /// </summary>
    public IPEndPoint? Select(int remotePort, IReadOnlyList<IPEndPoint> candidates,
        IPEndPoint? learned, long nowMs)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));

        if (learned != null && IsAlive(learned, nowMs))
        {
            _lastSelected[remotePort] = learned;
            return learned;
        }

        IPEndPoint? firstFresh = null, bestFresh = null;
        IPEndPoint? firstLive = null, bestLive = null;
        double bestFreshRtt = double.MaxValue, bestLiveRtt = double.MaxValue;
        for (int i = 0; i < candidates.Count; i++)  // indexed: per-frame path, no enumerator box
        {
            var endpoint = candidates[i];
            if (!_alive.TryGetValue(endpoint, out long heard) || nowMs - heard >= AliveWindowMs) continue;
            bool fresh = nowMs - heard < FreshWindowMs;
            if (firstLive == null) firstLive = endpoint;
            if (fresh && firstFresh == null) firstFresh = endpoint;
            if (_rtt.TryGetValue(endpoint, out double rtt) && rtt >= 0)
            {
                if (rtt < bestLiveRtt) { bestLiveRtt = rtt; bestLive = endpoint; }
                if (fresh && rtt < bestFreshRtt) { bestFreshRtt = rtt; bestFresh = endpoint; }
            }
        }

        var chosen = bestFresh ?? firstFresh ?? bestLive ?? firstLive;
        if (chosen != null)
        {
            _lastSelected[remotePort] = chosen;
            return chosen;
        }

        if (_lastSelected.TryGetValue(remotePort, out var last)
            && (Contains(candidates, last) || (learned != null && learned.Equals(last))))
            return last;

        return null;
    }

    /// <summary>Membership over the advertised candidates, indexed rather than LINQ — this sits on
    /// the per-frame send path.</summary>
    public static bool Contains(IReadOnlyList<IPEndPoint> candidates, IPEndPoint endpoint)
    {
        for (int i = 0; i < candidates.Count; i++)
            if (candidates[i].Equals(endpoint)) return true;
        return false;
    }

    // ---------------------------------------------------------------- housekeeping

    /// <summary>
    /// Forget everything about endpoints outside <paramref name="keep"/> — a route refresh, where a
    /// rejoin can change addresses. Measurements for an address nobody routes to any more are not
    /// merely useless; left in place they would be offered to an aggregate as though they described
    /// a live edge.
    /// </summary>
    public void RetainOnly(ICollection<IPEndPoint> keep)
    {
        if (keep == null) throw new ArgumentNullException(nameof(keep));
        foreach (var k in Snapshot(_alive.Keys)) if (!keep.Contains(k)) _alive.TryRemove(k, out _);
        foreach (var k in Snapshot(_lastPunch.Keys)) if (!keep.Contains(k)) _lastPunch.TryRemove(k, out _);
        foreach (var k in Snapshot(_rtt.Keys)) if (!keep.Contains(k)) _rtt.TryRemove(k, out _);
        foreach (var k in Snapshot(_rttWindows.Keys)) if (!keep.Contains(k)) _rttWindows.TryRemove(k, out _);
        foreach (var kv in _lastSelected)
            if (!keep.Contains(kv.Value)) _lastSelected.TryRemove(kv.Key, out _);
    }

    private static List<IPEndPoint> Snapshot(ICollection<IPEndPoint> keys) => new(keys);
}
