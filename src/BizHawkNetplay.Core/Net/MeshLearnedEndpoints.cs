using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;

namespace BizHawkNetplay.Core.Net;

/// <summary>
/// Where each peer ACTUALLY reaches us from, learned by watching, and the tokens that make
/// believing it safe.
///
/// <b>The problem this solves.</b> A symmetric NAT assigns a fresh public port per DESTINATION, so
/// the address a peer discovers by asking a STUN server is not the address it reaches us from.
/// Pinning on advertised endpoints alone meant dropping its packets unread, forever, on a path that
/// was physically working. The peer cannot know its own destination-specific port either — only we
/// can see it — which is why it has to be learned here rather than announced.
///
/// <b>Why a token.</b> Accepting an address nobody advertised is accepting a claim. The token is 16
/// random bytes handed out over the authenticated control channel, so an off-path attacker guessing
/// one is the same problem as guessing the session password. It is compared in constant time.
///
/// <b>Why the rules around it are not obvious.</b> Every peer holds every seat's token — that is
/// what makes a rejoin at a new address recognisable — so the token alone does not say WHICH seat
/// is speaking honestly. Two rules close the gap, and both are here rather than at the call site
/// because getting either wrong is a way one session member can take another off the mesh:
/// a binding that is currently answering is never replaced, and a binding that has never answered
/// is retired.
/// </summary>
public sealed class MeshLearnedEndpoints
{
    /// <summary>Bytes in a membership token.</summary>
    public const int TokenBytes = 16;

    /// <summary>
    /// How long a learned endpoint may go without answering a probe before it is forgotten.
    ///
    /// Learning is a claim rather than a conclusion, so something has to retire the claims that
    /// were never true. Without this, one spoofed announcement would have this node probing the
    /// named address for the rest of the session — a slow trickle rather than the input stream it
    /// used to turn on, but still traffic aimed at a stranger by a stranger. A genuine peer answers
    /// within a round trip of the next probe, and one dropped here is re-learned by its next
    /// announcement.
    /// </summary>
    public const int UnprovenExpiryMs = 10_000;

    private readonly MeshLinkQuality _quality;
    private volatile byte[]? _localToken;
    private readonly ConcurrentDictionary<int, byte[]> _peerTokens = new();          // seat -> token
    private readonly ConcurrentDictionary<int, IPEndPoint> _byPort = new();
    private readonly ConcurrentDictionary<IPEndPoint, int> _byEndpoint = new();
    // When each learned endpoint was first recorded, so one that never answers a probe can be
    // dropped instead of being probed for the rest of the session.
    private readonly ConcurrentDictionary<IPEndPoint, long> _learnedAt = new();

    /// <param name="quality">Liveness is what distinguishes a binding that has proved itself from a
    /// claim that has not, so learning cannot decide anything without it.</param>
    public MeshLearnedEndpoints(MeshLinkQuality quality) =>
        _quality = quality ?? throw new ArgumentNullException(nameof(quality));

    /// <summary>Our own token, announced so peers can recognise us at whatever address their side of
    /// the network actually sees. Null until the control channel hands one over.</summary>
    public byte[]? LocalToken => _localToken;

    public void SetLocalToken(byte[]? token) =>
        _localToken = token is { Length: TokenBytes } ? (byte[])token.Clone() : null;

    /// <summary>
    /// The tokens we will accept, by the seat that owns each.
    ///
    /// Replacing a seat's token retires anything learned under the old one. A learned binding was
    /// earned by presenting the token that seat carried at the time; if the host has since rotated
    /// them — which it does when a lobby casualty renumbers seats — the claim behind the binding no
    /// longer stands. Keeping it is not the conservative choice, it is the failure: the old occupant
    /// is usually still in the session on another seat, still answering from that endpoint, so the
    /// stale binding stays alive indefinitely and the seat's next genuine occupant is refused
    /// forever.
    /// </summary>
    public void SetPeerTokens(IEnumerable<KeyValuePair<int, byte[]>>? tokens)
    {
        var previous = new List<KeyValuePair<int, byte[]>>(_peerTokens);
        _peerTokens.Clear();
        if (tokens != null)
            foreach (var kv in tokens)
                if (kv.Value is { Length: TokenBytes }) _peerTokens[kv.Key] = (byte[])kv.Value.Clone();

        foreach (var old in previous)
        {
            if (_peerTokens.TryGetValue(old.Key, out var current) && TokensEqual(old.Value, current))
                continue;
            Retire(old.Key);
        }
    }

    /// <summary>Endpoints learned from a token that no advertised candidate matched — i.e. peers
    /// whose real address only became knowable by being told. Zero on a well-behaved network.</summary>
    public int Count => _byPort.Count;

    /// <summary>The address a seat was learned at.</summary>
    public bool TryGet(int remotePort, out IPEndPoint endpoint) =>
        _byPort.TryGetValue(remotePort, out endpoint!);

    /// <summary>Whether this endpoint is one we learned, and whose seat it belongs to. The receive
    /// path uses it to accept a datagram from an address no route lists.</summary>
    public bool TryGetSeat(IPEndPoint endpoint, out int remotePort) =>
        _byEndpoint.TryGetValue(endpoint, out remotePort);

    public bool IsLearned(IPEndPoint endpoint) => _byEndpoint.ContainsKey(endpoint);

    /// <summary>Every learned address, for the probe loop.</summary>
    public ICollection<IPEndPoint> Endpoints => _byPort.Values;

    /// <summary>Every learned seat, so a route refresh can decide which bindings outlive it.</summary>
    public ICollection<int> Seats => _byPort.Keys;

    /// <summary>
    /// Accept an endpoint nobody advertised, on the strength of a token presented from it.
    ///
    /// True when the token matched a seat we accept — including the case where the binding was
    /// already known, which is the steady state. False when the token matched nothing, or when the
    /// seat's existing binding is currently answering: a peer that is demonstrably still there is
    /// not moved on somebody else's say-so. A NAT rebinding, the case this whole path exists for,
    /// arrives at a seat that has just gone quiet, which still rebinds.
    /// </summary>
    public bool TryLearn(byte[] buffer, int tokenOffset, int available, IPEndPoint source, long nowMs)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (available < tokenOffset + TokenBytes) return false;

        foreach (var kv in _peerTokens)
        {
            if (!ConstantTimeEquals(kv.Value, buffer, tokenOffset)) continue;

            int port = kv.Key;
            if (_byPort.TryGetValue(port, out var previous))
            {
                if (previous.Equals(source)) return true;   // already known, nothing to record
                if (_quality.IsFresh(previous, nowMs)) return false;
                Drop(previous);
            }
            _byPort[port] = source;
            _byEndpoint[source] = port;
            _learnedAt[source] = nowMs;
            return true;
        }
        return false;
    }

    /// <summary>Forget learned endpoints that have never answered a probe. See
    /// <see cref="UnprovenExpiryMs"/> for why a claim has to expire.</summary>
    public void PruneUnproven(long nowMs)
    {
        if (_byPort.IsEmpty) return;
        foreach (var kv in _byPort)
        {
            var endpoint = kv.Value;
            if (_quality.HasBeenHeard(endpoint)) continue;                 // it proved itself
            if (!_learnedAt.TryGetValue(endpoint, out long at)) continue;
            if (nowMs - at < UnprovenExpiryMs) continue;
            // Racing a fresh learn for the same seat costs at most one re-learn on the next
            // announcement, so this stays a plain remove rather than a compare-and-swap.
            _byPort.TryRemove(kv.Key, out _);
            _byEndpoint.TryRemove(endpoint, out _);
            _learnedAt.TryRemove(endpoint, out _);
        }
    }

    /// <summary>
    /// Drop the binding for a seat that is no longer in the session.
    ///
    /// A learned endpoint belongs to a SEAT, not to that seat's advertised candidates, so it
    /// deliberately survives a route refresh — otherwise every reflexive candidate that trickled in
    /// would un-learn the symmetric-NAT peers, which is the one thing they cannot recover from on
    /// their own. It does not survive its seat leaving.
    /// </summary>
    public void ForgetSeat(int remotePort)
    {
        if (!_byPort.TryRemove(remotePort, out var endpoint)) return;
        _byEndpoint.TryRemove(endpoint, out _);
        _learnedAt.TryRemove(endpoint, out _);
    }

    private void Retire(int remotePort)
    {
        if (!_byPort.TryRemove(remotePort, out var endpoint)) return;
        Drop(endpoint);
    }

    private void Drop(IPEndPoint endpoint)
    {
        _byEndpoint.TryRemove(endpoint, out _);
        _learnedAt.TryRemove(endpoint, out _);
        _quality.Forget(endpoint);
    }

    private static bool TokensEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Constant time over the compared bytes: an early return leaks how much of a guess was
    /// right, which is the difference between 2^128 attempts and 16 × 2^8.</summary>
    private static bool ConstantTimeEquals(byte[] expected, byte[] buffer, int offset)
    {
        int diff = 0;
        for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ buffer[offset + i];
        return diff == 0;
    }
}
