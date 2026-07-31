using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// One peer's mesh identity: the token it announces as itself, and the tokens it will accept from
/// others, keyed by the controller port that owns each.
///
/// These exist because an advertised address is a guess. A symmetric NAT hands out a different
/// public port per destination, so the endpoint a peer learned from STUN is valid only for the
/// STUN server — everyone else sees it arriving from somewhere nobody was told about. A token
/// travels on the authenticated control channel, so a packet carrying one is proof of session
/// membership and the receiver can bind that peer to wherever it really came from.
///
/// Travelling together is the point: <see cref="Local"/> alone would let a peer announce itself to
/// people who cannot place it, and <see cref="Peers"/> alone would let it recognise peers it never
/// introduced itself to.
/// </summary>
public sealed class MeshTokens
{
    public static readonly MeshTokens None = new(null, null);

    public MeshTokens(byte[]? local, IEnumerable<KeyValuePair<int, byte[]>>? peers)
    {
        Local = local == null ? null : (byte[])local.Clone();
        var map = new Dictionary<int, byte[]>();
        if (peers != null)
            foreach (var kv in peers)
                if (kv.Value != null)
                    map[kv.Key] = (byte[])kv.Value.Clone();
        Peers = new ReadOnlyDictionary<int, byte[]>(map);
    }

    /// <summary>The token this peer announces as itself; null when the session has none.</summary>
    public byte[]? Local { get; }

    /// <summary>Tokens this peer will accept, by the controller port that owns each.</summary>
    public IReadOnlyDictionary<int, byte[]> Peers { get; }

    /// <summary>True when there is nothing to distribute — no token of our own and none to accept.</summary>
    public bool IsEmpty => Local == null && Peers.Count == 0;
}
