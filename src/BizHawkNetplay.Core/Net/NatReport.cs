using System.Net;

namespace BizHawkNetplay.Core.Net;

/// <summary>How this machine's NAT assigns the public port a peer would have to aim at.</summary>
public enum NatMapping
{
    /// <summary>Not established — offline, or fewer than two STUN servers answered.</summary>
    Unknown = 0,

    /// <summary>One mapping for all destinations. Hole punching works.</summary>
    EndpointIndependent,

    /// <summary>A fresh mapping per destination, so the address a peer is told to aim at was only
    /// ever valid for the STUN server. Punching cannot open a path.</summary>
    Symmetric,
}

/// <summary>The classification and the two observations it came from, so a log can show its working.</summary>
public readonly struct NatReport
{
    public NatReport(NatMapping mapping, IPEndPoint? first, IPEndPoint? second)
    {
        Mapping = mapping;
        First = first;
        Second = second;
    }

    public NatMapping Mapping { get; }
    public IPEndPoint? First { get; }
    public IPEndPoint? Second { get; }

    public bool IsSymmetric => Mapping == NatMapping.Symmetric;

    /// <summary>
    /// What to tell the player, phrased around what they can do about it.
    ///
    /// A symmetric NAT still cannot be punched — that is not solvable from this side — but it is no
    /// longer a dead end at 3-4 players: the host relays input over the legs that never opened, so
    /// such a peer plays, one extra hop behind. What it still cannot do is HOST without forwarding.
    /// </summary>
    public string Describe() => Mapping switch
    {
        NatMapping.EndpointIndependent =>
            $"NAT check: your router keeps one public address ({First}) for every destination, " +
            "so UDP Punch can open a path.",
        NatMapping.Symmetric =>
            $"NAT check: SYMMETRIC NAT — your router gave two different public ports for the same " +
            $"socket ({First?.Port} and {Second?.Port}), so no peer can be told an address that " +
            "will still be valid when it aims there. What this breaks: UDP Punch, and hosting " +
            "without a forwarded port. What still works: joining a host who has forwarded one, " +
            "because you open that path yourself — and at 3-4 players the host now relays input " +
            "over the direct legs to the other joiners that cannot open, so you play normally with " +
            "one extra hop of delay on those legs. So: join a forwarded host, or forward a UDP " +
            "port and host it yourself.",
        _ => "NAT check: could not reach two STUN servers, so nothing was established about your " +
             "router. This is not a verdict either way.",
    };
}
