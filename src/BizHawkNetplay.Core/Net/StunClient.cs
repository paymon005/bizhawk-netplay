using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// Minimal RFC 5389 STUN client: discovers the public (reflexive) IPv4 address a UDP socket is
/// translated to behind NAT — the address other peers must reach it at over the internet, which a
/// plain "what's my public IP" lookup can't give because it doesn't reveal the translated port.
/// This is the primitive NAT punch-through is built on. Encoding/decoding is pure and unit-tested;
/// <see cref="DiscoverPublicAddress"/> does the actual UDP round-trip to a public STUN server.
/// </summary>
public static class StunClient
{
    private const uint MagicCookie = 0x2112A442;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;

    /// <summary>Public STUN servers (Binding Request over UDP), tried in order until one answers.</summary>
    public static readonly (string host, int port)[] Servers =
    [
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    ];

    /// <summary>
    /// How long to wait on name resolution before giving up on a STUN server.
    ///
    /// Dns.GetHostAddresses has no timeout parameter and, against a blackholed resolver, blocks for
    /// the system default — on the host path that is the lobby accept loop, so joiners wait with no
    /// feedback for something that is only ever an optimisation. Three servers are tried in turn;
    /// the budget is per server.
    /// </summary>
    private const int ResolveTimeoutMs = 3000;

    /// <summary>Resolve a STUN server host to its first IPv4 endpoint, or null.</summary>
    public static IPEndPoint? ResolveV4(string host, int port)
    {
        try
        {
            // The resolve itself cannot be cancelled — the abandoned task simply finishes into
            // nothing later. What matters is that the CALLER stops waiting, since every path here
            // treats a null as "try the next server".
            var lookup = Task.Run(() => Dns.GetHostAddresses(host));
            if (!lookup.Wait(ResolveTimeoutMs)) return null;
            foreach (var a in lookup.Result)
                if (a.AddressFamily == AddressFamily.InterNetwork) return new IPEndPoint(a, port);
        }
        catch { }
        return null;
    }

    /// <summary>Build a Binding Request and hand back its 12-byte transaction id for matching.</summary>
    public static byte[] BuildRequest(out byte[] transactionId)
    {
        transactionId = new byte[12];
        Buffer.BlockCopy(Guid.NewGuid().ToByteArray(), 0, transactionId, 0, 12); // unique-enough per request
        var msg = new byte[20];
        WriteU16(msg, 0, BindingRequest);
        WriteU16(msg, 2, 0); // no attributes
        WriteU32(msg, 4, MagicCookie);
        Buffer.BlockCopy(transactionId, 0, msg, 8, 12);
        return msg;
    }

    /// <summary>
    /// Parse a Binding Success Response, returning the reflexive endpoint from XOR-MAPPED-ADDRESS
    /// (or the legacy MAPPED-ADDRESS), or null if this isn't a matching STUN response — so a shared
    /// receive loop can feed unrecognized packets straight in with no separate validation.
    /// </summary>
    public static IPEndPoint? ParseResponse(byte[] data, byte[] expectedTransactionId)
    {
        if (data == null || data.Length < 20 || expectedTransactionId == null || expectedTransactionId.Length < 12)
            return null;
        if (ReadU16(data, 0) != BindingSuccess) return null;
        if (ReadU32(data, 4) != MagicCookie) return null;
        for (int i = 0; i < 12; i++) if (data[8 + i] != expectedTransactionId[i]) return null;

        int end = Math.Min(data.Length, 20 + ReadU16(data, 2));
        int off = 20;
        while (off + 4 <= end)
        {
            int attrType = ReadU16(data, off);
            int attrLen = ReadU16(data, off + 2);
            int valOff = off + 4;
            if (valOff + attrLen > data.Length) break;

            // XOR-MAPPED-ADDRESS (0x0020): port/address XORed with the cookie so middleboxes that
            // rewrite plain addresses in transit can't mangle this one.
            if (attrType == 0x0020 && attrLen >= 8 && data[valOff + 1] == 0x01)
            {
                int port = ReadU16(data, valOff + 2) ^ (int)(MagicCookie >> 16);
                uint addr = ReadU32(data, valOff + 4) ^ MagicCookie;
                return new IPEndPoint(new IPAddress(ToNetworkBytes(addr)), port);
            }
            // MAPPED-ADDRESS (0x0001): older, unXORed equivalent some servers still send.
            if (attrType == 0x0001 && attrLen >= 8 && data[valOff + 1] == 0x01)
                return new IPEndPoint(new IPAddress(ToNetworkBytes(ReadU32(data, valOff + 4))), ReadU16(data, valOff + 2));

            off = valOff + attrLen + ((4 - (attrLen % 4)) % 4); // attributes are 4-byte padded
        }
        return null;
    }

    /// <summary>
    /// Discover our reflexive address via the first responding public STUN server. Binds a
    /// temporary UDP socket, so the reflected port belongs to that socket — the public IPv4 is the
    /// part a host with a forwarded port needs. Returns null when offline or every server times out.
    /// </summary>
    public static IPEndPoint? DiscoverPublicAddress(TimeSpan timeout)
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.Bind(new IPEndPoint(IPAddress.Any, 0));
        sock.ReceiveTimeout = Math.Max(250, (int)timeout.TotalMilliseconds);

        foreach (var (host, port) in Servers)
        {
            try
            {
                IPAddress? v4 = null;
                foreach (var a in Dns.GetHostAddresses(host))
                    if (a.AddressFamily == AddressFamily.InterNetwork) { v4 = a; break; }
                if (v4 == null) continue;

                var req = BuildRequest(out var txn);
                sock.SendTo(req, new IPEndPoint(v4, port));

                var buf = new byte[512];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = sock.ReceiveFrom(buf, ref from);
                var resp = new byte[n];
                Buffer.BlockCopy(buf, 0, resp, 0, n);

                var mapped = ParseResponse(resp, txn);
                if (mapped != null) return mapped;
            }
            catch { /* try the next server */ }
        }
        return null;
    }

    /// <summary>
    /// Ask two different STUN servers what this socket's public endpoint is, from the same socket,
    /// and compare. A NAT that hands out one mapping per destination gives two different answers.
    /// </summary>
    /// <remarks>
    /// Worth the two round-trips because symmetric NAT is the one failure this tool cannot work
    /// around and currently cannot name. Punching opens a path by having both sides send to the
    /// mapping the other observed; under a per-destination mapping the address a peer was told to
    /// aim at was only ever valid for the STUN server, so every candidate is dead on arrival. The
    /// session then fails the way an ordinary bad network fails — silence, then a timeout — and
    /// the player has no way to tell "your router cannot do this" from "try again".
    /// </remarks>
    public static NatReport ClassifyMapping(TimeSpan timeout)
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Bind(new IPEndPoint(IPAddress.Any, 0));
            sock.ReceiveTimeout = Math.Max(400, (int)(timeout.TotalMilliseconds / 2));

            // Distinct destination ADDRESSES, not just distinct hostnames: two names behind one
            // anycast address would probe the same mapping twice and call every NAT cone.
            var targets = new System.Collections.Generic.List<IPEndPoint>();
            foreach (var (host, port) in Servers)
            {
                var ep = ResolveV4(host, port);
                if (ep == null) continue;
                bool seen = false;
                foreach (var t in targets) if (t.Address.Equals(ep.Address)) { seen = true; break; }
                if (!seen) targets.Add(ep);
                if (targets.Count == 2) break;
            }
            if (targets.Count < 2) return default;

            var first = QueryOnce(sock, targets[0]);
            var second = QueryOnce(sock, targets[1]);
            return new NatReport(Classify(first, second), first, second);
        }
        catch { return default; }
    }

    /// <summary>
    /// The verdict, given two observations. Separate from the I/O so the reasoning is testable:
    /// one unanswered probe means unknown, never "cone", because failing open here would tell a
    /// player their router is fine on the evidence of a timeout.
    /// </summary>
    public static NatMapping Classify(IPEndPoint? first, IPEndPoint? second)
    {
        if (first == null || second == null) return NatMapping.Unknown;
        return first.Port == second.Port && first.Address.Equals(second.Address)
            ? NatMapping.EndpointIndependent
            : NatMapping.Symmetric;
    }

    private static IPEndPoint? QueryOnce(Socket sock, IPEndPoint server)
    {
        try
        {
            var req = BuildRequest(out var txn);
            sock.SendTo(req, server);
            var buf = new byte[512];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = sock.ReceiveFrom(buf, ref from);
            var resp = new byte[n];
            Buffer.BlockCopy(buf, 0, resp, 0, n);
            return ParseResponse(resp, txn);
        }
        catch { return null; }
    }

    private static void WriteU16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
    private static void WriteU32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static int ReadU16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    private static uint ReadU32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
    private static byte[] ToNetworkBytes(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}
