using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Core.Tests.Fakes
{
    /// <summary>
    /// A clock whose reads follow a scripted sequence of increments, so probe timings are
    /// deterministic. Each pair of reads (start/stop around an op) consumes one increment.
    /// </summary>
    public sealed class ManualClock : IMonotonicClock
    {
        private readonly Queue<double> _increments;
        private double _now;
        private bool _pendingStop;
        private double _nextDelta;

        public ManualClock(IEnumerable<double> perOpDurationsMs)
        {
            _increments = new Queue<double>(perOpDurationsMs);
        }

        public double NowMs
        {
            get
            {
                if (!_pendingStop)
                {
                    // Start read: capture the duration this op will take.
                    _nextDelta = _increments.Count > 0 ? _increments.Dequeue() : 0.0;
                    _pendingStop = true;
                    return _now;
                }
                // Stop read: advance by the captured duration.
                _now += _nextDelta;
                _pendingStop = false;
                return _now;
            }
        }
    }
}
