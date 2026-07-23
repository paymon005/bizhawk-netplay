using System.Diagnostics;
using BizHawkNetplay.Core.Probe;

namespace BizHawkNetplay.Tool
{
    /// <summary>High-resolution wall clock backing the capability probe in the real tool.</summary>
    internal sealed class StopwatchClock : IMonotonicClock
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public double NowMs => _sw.Elapsed.TotalMilliseconds;
    }
}
