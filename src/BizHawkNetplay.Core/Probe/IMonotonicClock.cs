namespace BizHawkNetplay.Core.Probe
{
    /// <summary>
    /// High-resolution elapsed-time source. Abstracted so the capability probe can be driven
    /// by a scripted clock in tests and by <c>Stopwatch</c> in the real tool.
    /// </summary>
    public interface IMonotonicClock
    {
        /// <summary>Current time in fractional milliseconds from an arbitrary origin.</summary>
        double NowMs { get; }
    }
}
