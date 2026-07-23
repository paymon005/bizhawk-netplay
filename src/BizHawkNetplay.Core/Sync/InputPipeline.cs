using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;

namespace BizHawkNetplay.Core.Sync
{
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
        public void PruneBefore(int keepFromFrame)
        {
            for (int p = 0; p < _portCount; p++)
            {
                var map = _byFrame[p];
                if (map.Count == 0) continue;
                var stale = new List<int>();
                foreach (var f in map.Keys)
                    if (f < keepFromFrame) stale.Add(f);
                foreach (var f in stale) map.Remove(f);
            }
        }
    }
}
