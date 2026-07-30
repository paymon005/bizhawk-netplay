using System.Net;

namespace BizHawkNetplay.Core.Net
{
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
        /// What to tell the player. Phrased around what they can do, because the one thing they cannot
        /// do is make this tool traverse a symmetric NAT — the relay that would is not built.
        /// </summary>
        public string Describe() => Mapping switch
        {
            NatMapping.EndpointIndependent =>
                $"NAT check: your router keeps one public address ({First}) for every destination, " +
                "so UDP Punch can open a path.",
            NatMapping.Symmetric =>
                $"NAT check: SYMMETRIC NAT — your router gave two different public ports for the same " +
                $"socket ({First?.Port} and {Second?.Port}), so no peer can be told an address that " +
                "will still be valid when it aims there. What this breaks: UDP Punch, and the direct " +
                "leg to every OTHER joiner in a 3-4 player session. What still works: joining a host " +
                "who has forwarded a port, because you open that path yourself. So — play 2-player " +
                "against a forwarded host, or forward a UDP port and host it yourself. A relay for the " +
                "rest is not built.",
            _ => "NAT check: could not reach two STUN servers, so nothing was established about your " +
                 "router. This is not a verdict either way.",
        };
    }
}
