using System.Net;
using System.Net.Sockets;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// Whether a peer's claim about its own public UDP endpoint is worth believing.
///
/// A peer discovers this address by asking a STUN server and announces it in its HELLO (and again,
/// later, in a Candidate frame). The host does not merely store it: it hands the address to every
/// other player, and the punch loop probes it four times a second while the no-confirmed-path
/// fallback broadcasts input to it. So an unchecked claim is an instruction — pointed wherever the
/// sender likes — to aim this machine, and everyone else in the session, at a third party.
///
/// The check is that the address matches the one the peer's own control connection arrives from.
/// Both are that peer's public address; they are observed from different vantage points (a STUN
/// server's and ours), which is why the PORT may legitimately differ — a symmetric NAT assigns a
/// fresh one per destination, and that difference is the entire reason the field exists. The
/// address itself differing is not something a working NAT does.
///
/// Refusing is cheap. A refused candidate leaves the route holding the endpoint we observed
/// directly, which is what every session had before reflexive discovery existed and is still the
/// only candidate on a LAN. The cases that refuse honestly — a joiner on our own LAN, whose public
/// address is nothing like the one we see it at, or a peer behind a carrier NAT that answers from a
/// pool of addresses — are cases where the announced address would not have helped anyway.
/// </summary>
public static class ReflexiveCandidate
{
    /// <param name="claimed">The endpoint the peer announced, or null if it discovered none.</param>
    /// <param name="observed">The address this peer's control connection actually arrives from.</param>
    public static bool IsCredible(IPEndPoint? claimed, IPAddress? observed)
    {
        if (claimed == null || observed == null) return false;

        // The mesh socket is bound to IPv4 and can send nowhere else, so a v6 candidate is never a
        // path to a player regardless of who announced it.
        if (claimed.AddressFamily != AddressFamily.InterNetwork) return false;
        if (claimed.Port < 1 || claimed.Port > 65535) return false;

        // An IPv4 control connection tunnelled over a v6 socket is reported mapped; compare on the
        // v4 address it stands for rather than refusing the peer for its transport.
        var source = observed.IsIPv4MappedToIPv6 ? observed.MapToIPv4() : observed;
        if (source.AddressFamily != AddressFamily.InterNetwork) return false;

        return claimed.Address.Equals(source);
    }
}
