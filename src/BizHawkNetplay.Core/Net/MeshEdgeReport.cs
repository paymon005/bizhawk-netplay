namespace BizHawkNetplay.Core.Net;

/// <summary>
/// How one logical peer's UDP edge looked at the moment it was asked about: whether it answered,
/// what it measured, and whether the address that worked had to be learned rather than advertised.
/// </summary>
public readonly struct MeshEdgeReport
{
    public MeshEdgeReport(int remotePort, bool measured, double medianMs, double highMs, bool viaLearnedEndpoint)
    {
        RemotePort = remotePort;
        Measured = measured;
        MedianMs = medianMs;
        HighMs = highMs;
        ViaLearnedEndpoint = viaLearnedEndpoint;
    }

    /// <summary>The controller port on the far end of this edge.</summary>
    public int RemotePort { get; }

    /// <summary>True when at least one candidate for this peer produced a round-trip sample.</summary>
    public bool Measured { get; }

    /// <summary>Median round trip on this peer's best candidate; meaningless unless <see cref="Measured"/>.</summary>
    public double MedianMs { get; }

    /// <summary>High-water round trip on the same candidate — the figure jitter is sized from.</summary>
    public double HighMs { get; }

    /// <summary>
    /// True when the best candidate was an address this peer never advertised — one we could only
    /// find because it presented its token. In practice that names a symmetric NAT on its side, and
    /// it is worth saying out loud: nothing else in the session would ever have reached it.
    /// </summary>
    public bool ViaLearnedEndpoint { get; }
}
