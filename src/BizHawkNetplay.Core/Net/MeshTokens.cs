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
///
/// <see cref="Pairs"/> rides along for the opposite reason. Tokens are shared — every peer holds
/// every seat's, which is what makes a rejoin recognisable and also what makes a token useless for
/// proving who WROTE something. The pair keys are the unshared half, and they travel here because
/// they are distributed on the same authenticated channel, at the same moments, to the same peers:
/// splitting them into their own message would have meant two things to keep in step and one of
/// them eventually forgotten.
/// </summary>
public sealed class MeshTokens
{
    public static readonly MeshTokens None = new(null, null);

    public MeshTokens(byte[]? local, IEnumerable<KeyValuePair<int, byte[]>>? peers,
        MeshPairKeyring? pairs = null)
    {
        Pairs = pairs ?? MeshPairKeyring.None;
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

    /// <summary>The per-pair keys this peer may hold — its own pairs, and for the host the whole
    /// table because only the host re-tags a relayed datagram.</summary>
    public MeshPairKeyring Pairs { get; }

    /// <summary>True when there is nothing to distribute — no token of our own, none to accept, and
    /// no key to authenticate input with.</summary>
    public bool IsEmpty => Local == null && Peers.Count == 0 && Pairs.Count == 0;
}
