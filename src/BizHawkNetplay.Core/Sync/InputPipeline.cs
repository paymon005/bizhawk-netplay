using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;

namespace BizHawkNetplay.Core.Sync;

/// <summary>
/// Per-port input store shared by both strategies. Holds every known input keyed by
/// (port, frame) and tracks each port's <c>ConfirmedFrontier</c> — the highest frame for
/// which real input is known contiguously from frame 0. Local ports advance their frontier
/// as physical input is stamped for frame N + D; remote ports advance as packets arrive.
///
/// This class is deliberately free of BizHawk and transport concerns: it stores and
/// answers frontier queries. Delay-stamping policy (the value of D) lives with the caller.
/// </summary>
public sealed class InputPipeline
{
    private readonly int _portCount;
    private readonly Dictionary<int, PortInput>[] _byFrame; // per port: frame -> input
    private readonly int[] _frontier;                       // per port: contiguous-known frame, -1 if none
    private readonly bool[] _isLocal;
    // How far PruneBefore has already swept, so the ordinary call removes one frame instead of
    // reading every key of every port's map. See PruneBefore.
    private int _prunedBelow;
    private readonly List<int> _sweepScratch = new List<int>();
    private const int MaxPruneSweep = 256;

    public InputPipeline(int portCount)
    {
        if (portCount < 1) throw new ArgumentOutOfRangeException(nameof(portCount));
        _portCount = portCount;
        _byFrame = new Dictionary<int, PortInput>[portCount];
        _frontier = new int[portCount];
        _isLocal = new bool[portCount];
        for (int p = 0; p < portCount; p++)
        {
            _byFrame[p] = new Dictionary<int, PortInput>();
            _frontier[p] = -1;
        }
    }

    public int PortCount => _portCount;

    /// <summary>Mark which ports this peer owns (drives local capture; informational for frontier).</summary>
    public void SetLocal(int port, bool isLocal) => _isLocal[port] = isLocal;

    public bool IsLocal(int port) => _isLocal[port];

    /// <summary>Highest frame known contiguously for a port; -1 if nothing yet.</summary>
    public int ConfirmedFrontier(int port) => _frontier[port];

    /// <summary>Lowest confirmed frontier across all ports — the frame the sim can safely run to.</summary>
    public int MinFrontier()
    {
        int min = int.MaxValue;
        for (int p = 0; p < _portCount; p++)
            if (_frontier[p] < min) min = _frontier[p];
        return min;
    }

    /// <summary>
    /// Record an input for (port, frame). Idempotent: re-adding the same frame is a no-op
    /// (redundant packets carry overlapping ranges by design). Advances the port frontier
    /// across any newly-contiguous run.
    /// </summary>
    public void Add(int port, int frame, PortInput input)
    {
        if (port < 0 || port >= _portCount) throw new ArgumentOutOfRangeException(nameof(port));
        if (frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
        if (input == null) throw new ArgumentNullException(nameof(input));

        var map = _byFrame[port];
        if (!map.ContainsKey(frame))
            map[frame] = input;

        // Extend the contiguous frontier as far as consecutive frames are present.
        int next = _frontier[port] + 1;
        while (map.ContainsKey(next))
            next++;
        _frontier[port] = next - 1;
    }

    public bool TryGet(int port, int frame, out PortInput input) =>
        _byFrame[port].TryGetValue(frame, out input!);

    /// <summary>True when every port has confirmed input at <paramref name="frame"/> (lockstep gate).</summary>
    public bool AllConfirmed(int frame)
    {
        for (int p = 0; p < _portCount; p++)
            if (_frontier[p] < frame) return false;
        return true;
    }

    /// <summary>
    /// Assemble the full <see cref="InputSet"/> for a frame from stored per-port inputs.
    /// Throws if any port lacks input — callers gate on <see cref="AllConfirmed"/> first.
    /// </summary>
    public InputSet Merge(int frame)
    {
        var ports = new PortInput[_portCount];
        for (int p = 0; p < _portCount; p++)
        {
            if (!_byFrame[p].TryGetValue(frame, out var value))
                throw new InvalidOperationException($"No input for port {p} at frame {frame}");
            ports[p] = value;
        }
        return new InputSet(frame, ports);
    }

    /// <summary>Drop stored inputs older than <paramref name="keepFromFrame"/> to bound memory.</summary>
    /// <summary>
    /// Drop everything older than <paramref name="keepFromFrame"/>.
    ///
    /// Called once per advanced frame, which is what makes the obvious implementation the wrong
    /// one: scanning every key of every port's map to find the one entry that just aged out is
    /// O(history x ports) per frame, and the <c>List&lt;int&gt;</c> it collected them into was a
    /// fresh allocation per port per frame — 240 a second at four players, forever.
    ///
    /// Normally exactly one frame ages out per call, so walk DOWN from the boundary instead and
    /// stop at the first frame that isn't there. The loop is bounded by
    /// <paramref name="maxScan"/> rather than by the map size, so the pathological cases — a
    /// rollback that jumps the frame counter, a resync, a burst that advanced several frames —
    /// cost a bounded amount of work now and finish on subsequent calls. Nothing is retained that
    /// matters: entries below the boundary are unreachable by every reader, so a straggler simply
    /// waits one more frame to be dropped.
    /// </summary>
    public void PruneBefore(int keepFromFrame)
    {
        if (keepFromFrame <= _prunedBelow)
        {
            // The boundary went backwards: a rollback repair, or a rebuilt timeline. Nothing above
            // it needs dropping, but the watermark has to follow or the sweep below would skip the
            // frames between here and wherever it used to be.
            _prunedBelow = keepFromFrame;
            return;
        }

        int from = keepFromFrame - _prunedBelow > MaxPruneSweep ? -1 : _prunedBelow;
        for (int p = 0; p < _portCount; p++)
        {
            var map = _byFrame[p];
            if (map.Count == 0) continue;

            if (from >= 0)
            {
                // The ordinary case: the boundary moved by one, so this removes one entry.
                for (int f = from; f < keepFromFrame; f++) map.Remove(f);
            }
            else
            {
                // The boundary jumped — a resync, a rejoin, a settings change. Rare enough to pay
                // for a full sweep, and it must be a sweep: entries stranded below a hole would
                // otherwise never be visited again.
                _sweepScratch.Clear();
                foreach (var f in map.Keys)
                    if (f < keepFromFrame) _sweepScratch.Add(f);
                for (int i = 0; i < _sweepScratch.Count; i++) map.Remove(_sweepScratch[i]);
            }
        }
        _prunedBelow = keepFromFrame;
    }
}
