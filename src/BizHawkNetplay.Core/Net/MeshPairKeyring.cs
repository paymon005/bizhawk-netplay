using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// The per-pair secrets that make an input datagram's author provable.
///
/// Membership tokens cannot do this job, and the reason is worth stating plainly: every peer holds
/// every seat's token, because that is what lets any of them recognise a rejoining player at a new
/// address. A shared secret proves membership and nothing else — with it, P2 could put P3's port in
/// a datagram and P4 would believe it. Authorship needs a secret that P2 does not have.
///
/// So the host mints one key per unordered pair of seats and hands each peer only the keys for the
/// pairs it is in. P2 gets K{2,0}, K{2,3}, K{2,4} and no others, so the datagram it can forge is one
/// it was entitled to send anyway. The host holds the whole table, because it is the only node that
/// relays, and a relayed datagram has to be re-tagged for its destination.
///
/// The keys travel in WELCOME over the authenticated control channel, alongside the tokens.
///
/// What this does NOT do is stop replay: a datagram captured off the wire stays valid for as long as
/// the session generation does. That is deliberate rather than overlooked — input is keyed by
/// (port, frame) and first write wins, so a replayed input datagram delivers frames the receiver
/// already holds, and a replayed request costs one retransmit that the gap-serve budget already
/// meters. Binding the author is the property that was missing; sequencing it would cost a
/// per-datagram counter for no threat that survives the analysis.
/// </summary>
public sealed class MeshPairKeyring
{
    /// <summary>Bytes in one pair key. HMAC-SHA256's block-size-matched key length, so the hash
    /// never has to pre-digest it.</summary>
    public const int KeyBytes = 32;

    /// <summary>Bytes of tag carried by each datagram. Truncated from SHA-256's 32 because these
    /// ride every input packet at 60Hz: 8 bytes is 2^64 forgery attempts against a secret that
    /// lives for one session, and the thing being forged is a single frame of input.</summary>
    public const int TagBytes = 8;

    /// <summary>An empty keyring — nothing can be tagged and nothing can be verified.</summary>
    public static readonly MeshPairKeyring None = new(null);

    private readonly Dictionary<int, byte[]> _keys = new();

    public MeshPairKeyring(IEnumerable<KeyValuePair<int, byte[]>>? keys)
    {
        if (keys == null) return;
        foreach (var kv in keys)
        {
            if (kv.Value is not { Length: KeyBytes }) continue;
            _keys[kv.Key] = (byte[])kv.Value.Clone();
        }
    }

    /// <summary>
    /// Stable identity for the unordered pair {a, b}, packed a byte a side.
    ///
    /// Eight bits is provably enough HERE and only here: every way into this ring validates its
    /// ports against <c>HandshakeCodec.MaxPlayers</c>, and the packed value is decoded back into
    /// (a, b) in two places that assume the same width. <c>MeshUdpTransport</c> keys relay pairs by
    /// the same arithmetic at sixteen bits a side, because it also hands out synthetic punch-target
    /// ports in the thousands and an eight-bit packing collides there. The two are not required to
    /// agree — neither table is ever keyed from the other, and this value never reaches the wire
    /// (the handshake sends a and b as separate fields).
    /// </summary>
    public static int PairKey(int a, int b) => a < b ? (a << 8) | b : (b << 8) | a;

    /// <summary>How many pairs this ring holds. Zero means input cannot be authenticated at all,
    /// which is a wiring fault rather than a network condition — the transport counts it.</summary>
    public int Count => _keys.Count;

    public bool Has(int a, int b) => _keys.ContainsKey(PairKey(a, b));

    /// <summary>Every pair this ring holds, in the wire form the handshake encodes.</summary>
    public IEnumerable<KeyValuePair<int, byte[]>> Entries => _keys;

    /// <summary>
    /// Mint a full table for a session of <paramref name="players"/> seats — every unordered pair,
    /// independently random. The host calls this once; nobody else ever holds all of it.
    /// </summary>
    public static MeshPairKeyring Mint(int players)
    {
        if (players < 0) throw new ArgumentOutOfRangeException(nameof(players));
        var keys = new Dictionary<int, byte[]>();
        using var rng = RandomNumberGenerator.Create();
        for (int a = 0; a < players; a++)
            for (int b = a + 1; b < players; b++)
            {
                var key = new byte[KeyBytes];
                rng.GetBytes(key);
                keys[PairKey(a, b)] = key;
            }
        return new MeshPairKeyring(keys);
    }

    /// <summary>
    /// The subset the peer at <paramref name="port"/> is entitled to: the pairs it is a member of,
    /// and nothing else. This is the whole point of the scheme, so it is one method rather than a
    /// filter each caller writes — a caller that forgot the filter would hand out the ability to
    /// impersonate every other seat.
    /// </summary>
    public MeshPairKeyring For(int port)
    {
        var mine = new Dictionary<int, byte[]>();
        foreach (var kv in _keys)
        {
            int a = (kv.Key >> 8) & 0xFF, b = kv.Key & 0xFF;
            if (a == port || b == port) mine[kv.Key] = kv.Value;
        }
        return new MeshPairKeyring(mine);
    }

    /// <summary>
    /// A tagger bound to this ring's keys. NOT thread-safe and deliberately so: an
    /// <see cref="HMACSHA256"/> carries hashing state across its Transform calls, so the send path
    /// and the receive path each take their own rather than sharing one under a lock on every
    /// datagram. Cached per key because minting an HMAC allocates, and this runs 60 times a second
    /// per peer.
    /// </summary>
    public MeshTagger CreateTagger() => new(_keys);
}

/// <summary>
/// Computes and checks the authorship tag on one thread. See <see cref="MeshPairKeyring"/> for why
/// the keys are per-pair.
/// </summary>
public sealed class MeshTagger : IDisposable
{
    private readonly Dictionary<int, HMACSHA256> _macs = new();

    // The author, prefixed to the payload before hashing.
    //
    // The RECIPIENT is deliberately not here, and the reason is worth stating so nobody adds it back
    // as an oversight. The pair is already bound — by which key gets used. A datagram P2 tagged for
    // P4 is tagged under K{2,4}, and P1 holds no such key, so it cannot verify it whatever the bytes
    // say. Naming the recipient a second time inside the hash would be restating the key's own
    // identity. The author is different in kind: within a pair the key is symmetric, so without this
    // byte a peer could reflect its partner's datagram back as though the partner had authored it.
    private readonly byte[] _prefix = new byte[1];

    internal MeshTagger(Dictionary<int, byte[]> keys)
    {
        foreach (var kv in keys) _macs[kv.Key] = new HMACSHA256(kv.Value);
    }

    /// <summary>True when this tagger can speak for the pair at all.</summary>
    public bool Has(int author, int recipient) => _macs.ContainsKey(MeshPairKeyring.PairKey(author, recipient));

    /// <summary>
    /// Write the tag for <paramref name="author"/> → <paramref name="recipient"/> over the codec
    /// payload. False when this node holds no key for the pair, which the caller must treat as
    /// "cannot send" rather than "send untagged" — an untagged datagram is one the receiver drops,
    /// so silently emitting one would turn a key-distribution bug into an unexplained input stall.
    ///
    /// The recipient never goes on the wire. It selects the key, and that selection is the binding:
    /// a datagram tagged for one peer cannot be verified by another, because the other holds a
    /// different key for its own pair. That is exactly why a relayed datagram has to be re-tagged
    /// rather than forwarded — the destination changed, so the key did.
    /// </summary>
    public bool TryComputeTag(int author, int recipient, byte[] payload, int offset, int count,
        byte[] into, int intoOffset)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (into == null) throw new ArgumentNullException(nameof(into));
        if (into.Length - intoOffset < MeshPairKeyring.TagBytes)
            throw new ArgumentException("No room for the tag", nameof(into));
        if (!_macs.TryGetValue(MeshPairKeyring.PairKey(author, recipient), out var mac)) return false;

        _prefix[0] = (byte)author;
        mac.Initialize();
        mac.TransformBlock(_prefix, 0, 1, null, 0);
        mac.TransformFinalBlock(payload, offset, count);
        Buffer.BlockCopy(mac.Hash, 0, into, intoOffset, MeshPairKeyring.TagBytes);
        return true;
    }

    /// <summary>
    /// True when the tag proves <paramref name="author"/> wrote this payload for us. False for a
    /// forgery, for a pair we hold no key for, and for a truncated datagram — all three are equally
    /// "do not act on this", and the caller counts them together.
    /// </summary>
    public bool Verify(int author, int recipient, byte[] payload, int offset, int count,
        byte[] tag, int tagOffset)
    {
        if (tag == null) throw new ArgumentNullException(nameof(tag));
        if (tag.Length - tagOffset < MeshPairKeyring.TagBytes) return false;
        if (!_macs.TryGetValue(MeshPairKeyring.PairKey(author, recipient), out var mac)) return false;

        _prefix[0] = (byte)author;
        mac.Initialize();
        mac.TransformBlock(_prefix, 0, 1, null, 0);
        mac.TransformFinalBlock(payload, offset, count);
        var expected = mac.Hash;

        // Constant time over the compared bytes: a tag check that returns early leaks how much of a
        // guess was right, which is the difference between 2^64 attempts and 8 × 2^8.
        int diff = 0;
        for (int i = 0; i < MeshPairKeyring.TagBytes; i++) diff |= tag[tagOffset + i] ^ expected[i];
        return diff == 0;
    }

    public void Dispose()
    {
        foreach (var mac in _macs.Values) mac.Dispose();
        _macs.Clear();
    }
}
