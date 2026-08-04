using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// KI-21: an input datagram has to prove who wrote it.
///
/// The property under test is not "outsiders are kept out" — the membership token already did that,
/// and every admitted peer holds every seat's token, which is exactly why it could not do this job.
/// The property is that an ADMITTED peer cannot write another seat's input.
/// </summary>
public class MeshInputAuthorshipTests
{
    private static IPEndPoint Loop(int port) => new(IPAddress.Loopback, port);

    /// <summary>Wire one mesh into a session of <paramref name="seats"/> seats, giving it exactly
    /// the keys production would: its own pairs, or the whole table if it is the host.</summary>
    private static void Wire(MeshUdpTransport mesh, int seat, MeshPairKeyring ring,
        IReadOnlyDictionary<int, int> udpPortBySeat, bool host = false)
    {
        var routes = udpPortBySeat.Where(kv => kv.Key != seat)
            .Select(kv => new PeerRoute(kv.Key, new[] { Loop(kv.Value) })).ToList();
        mesh.SetPeerRoutes(routes);
        mesh.ApplyTokens(new MeshTokens(null, null, host ? ring : ring.For(seat)), seat);
    }

    // ---------------------------------------------------------------- the keyring itself

    [Fact]
    public void ANarrowedRingHoldsOnlyThePairsItsOwnerIsIn()
    {
        var ring = MeshPairKeyring.Mint(4);
        Assert.Equal(6, ring.Count);                        // 4 seats = 6 unordered pairs

        var p2 = ring.For(2);
        Assert.Equal(3, p2.Count);
        Assert.True(p2.Has(2, 0));
        Assert.True(p2.Has(2, 1));
        Assert.True(p2.Has(2, 3));
        // The one that matters: P2 holds nothing it could sign as P3 to P1 with.
        Assert.False(p2.Has(1, 3));
        Assert.False(p2.Has(0, 1));
        Assert.False(p2.Has(0, 3));
    }

    [Fact]
    public void EveryPairGetsAnIndependentKey()
    {
        var ring = MeshPairKeyring.Mint(4);
        var keys = ring.Entries.Select(e => Convert.ToBase64String(e.Value)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(ring.Entries, e => Assert.Equal(MeshPairKeyring.KeyBytes, e.Value.Length));
    }

    [Fact]
    public void ATagIsBoundToBothEndsOfThePairItWasMadeFor()
    {
        var ring = MeshPairKeyring.Mint(4);
        using var tagger = ring.CreateTagger();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var tag = new byte[MeshPairKeyring.TagBytes];

        Assert.True(tagger.TryComputeTag(2, 3, payload, 0, payload.Length, tag, 0));
        Assert.True(tagger.Verify(2, 3, payload, 0, payload.Length, tag, 0));

        // Same author, same bytes, different destination: the tag does not carry over, because the
        // destination chose a different key. This is what makes a relayed copy something the host
        // has to re-tag rather than merely forward.
        Assert.False(tagger.Verify(2, 1, payload, 0, payload.Length, tag, 0));

        // Author and recipient swapped. The KEY is the same one — a pair is unordered — so this case
        // is caught by the author byte inside the hash and by nothing else. Without it, P3 could
        // bounce P2's own datagram back at it and have it accepted as P2's input.
        Assert.False(tagger.Verify(3, 2, payload, 0, payload.Length, tag, 0));
    }

    [Fact]
    public void AnAlteredPayloadFailsTheTag()
    {
        var ring = MeshPairKeyring.Mint(3);
        using var tagger = ring.CreateTagger();
        var payload = new byte[] { 1, 0, 9, 9 };
        var tag = new byte[MeshPairKeyring.TagBytes];
        Assert.True(tagger.TryComputeTag(0, 1, payload, 0, payload.Length, tag, 0));

        payload[3] ^= 0x01;   // one bit anywhere in the frame window
        Assert.False(tagger.Verify(0, 1, payload, 0, payload.Length, tag, 0));
    }

    [Fact]
    public void ATaggerWithoutThePairsKeyRefusesToSignRatherThanSigningBadly()
    {
        var ring = MeshPairKeyring.Mint(4).For(2);
        using var tagger = ring.CreateTagger();
        var payload = new byte[] { 1, 3, 0, 0 };
        var tag = new byte[MeshPairKeyring.TagBytes];

        Assert.True(tagger.TryComputeTag(2, 1, payload, 0, payload.Length, tag, 0));
        // P2 asked to sign as P3. There is no key, so there is no tag — not a weak one.
        Assert.False(tagger.TryComputeTag(3, 1, payload, 0, payload.Length, tag, 0));
        Assert.False(tagger.Verify(0, 1, payload, 0, payload.Length, tag, 0));
    }

    [Fact]
    public void AFreshMintSharesNothingWithThePreviousOne()
    {
        // What a seat renumbering and the end of a session both rely on: the departing occupant of
        // seat 2 keeps whatever bytes it was given, so the seat's next occupant must not inherit
        // them. Reminting has to actually produce different keys, table-wide.
        var first = MeshPairKeyring.Mint(4);
        var second = MeshPairKeyring.Mint(4);

        var before = first.Entries.ToDictionary(e => e.Key, e => Convert.ToBase64String(e.Value));
        foreach (var entry in second.Entries)
            Assert.NotEqual(before[entry.Key], Convert.ToBase64String(entry.Value));
    }

    [Fact]
    public void PairKeyIsOrderIndependent()
    {
        Assert.Equal(MeshPairKeyring.PairKey(1, 3), MeshPairKeyring.PairKey(3, 1));
        Assert.NotEqual(MeshPairKeyring.PairKey(1, 3), MeshPairKeyring.PairKey(1, 2));
    }

    [Fact]
    public void PairKeysSurviveTheHandshakeRoundTripNarrowed()
    {
        var ring = MeshPairKeyring.Mint(4);
        var forP2 = ring.For(2);
        var welcome = HandshakeCodec.EncodeWelcome(
            2, 4, 1, SyncMode.Rollback, new SessionGeneration(7, 0),
            new[] { new PeerRoute(0, new[] { Loop(47800) }) },
            new MeshTokens(SessionAuth.NewMeshToken(),
                new Dictionary<int, byte[]> { [0] = SessionAuth.NewMeshToken() }, forP2));

        var decoded = HandshakeCodec.DecodeTokens(welcome);
        Assert.Equal(3, decoded.Pairs.Count);
        Assert.True(decoded.Pairs.Has(2, 0));
        Assert.False(decoded.Pairs.Has(0, 1));

        // The bytes have to be the same bytes, or both ends compute different tags and every
        // datagram fails for a reason that looks exactly like packet loss.
        using var original = forP2.CreateTagger();
        using var round = decoded.Pairs.CreateTagger();
        var payload = new byte[] { 1, 2, 3 };
        var a = new byte[MeshPairKeyring.TagBytes];
        var b = new byte[MeshPairKeyring.TagBytes];
        Assert.True(original.TryComputeTag(2, 3, payload, 0, payload.Length, a, 0));
        Assert.True(round.TryComputeTag(2, 3, payload, 0, payload.Length, b, 0));
        Assert.Equal(a, b);
    }

    // ---------------------------------------------------------------- over a real socket

    [Fact]
    public void AProperlyKeyedPeersInputArrives()
    {
        var ring = MeshPairKeyring.Mint(2);
        var host = MeshUdpTransport.Bind(0);
        var joiner = MeshUdpTransport.Bind(0);
        try
        {
            var ports = new Dictionary<int, int> { [0] = host.LocalPort, [1] = joiner.LocalPort };
            Wire(host, 0, ring, ports, host: true);
            Wire(joiner, 1, ring, ports);

            joiner.Send(InputFrom(1));
            Assert.Equal(InputFrom(1), WaitRecv(host));
            Assert.Equal(0, host.InputUnauthenticated);
        }
        finally { host.Dispose(); joiner.Dispose(); }
    }

    [Fact]
    public void APeerCannotSubmitAnotherSeatsInput()
    {
        // Three seats, all legitimately admitted. P2 wants P1's input to land at the host.
        var ring = MeshPairKeyring.Mint(3);
        var host = MeshUdpTransport.Bind(0);
        var p1 = MeshUdpTransport.Bind(0);
        var p2 = MeshUdpTransport.Bind(0);
        try
        {
            var ports = new Dictionary<int, int>
                { [0] = host.LocalPort, [1] = p1.LocalPort, [2] = p2.LocalPort };
            Wire(host, 0, ring, ports, host: true);
            Wire(p1, 1, ring, ports);
            Wire(p2, 2, ring, ports);

            // P2 writes seat 1 into the codec payload — the byte that used to decide authorship
            // outright — and sends it under its own (legitimate) seat-2 keys.
            p2.Send(InputFrom(1));

            AssertNoRecv(host);
            WaitUntil(() => host.InputUnauthenticated > 0,
                "the host accepted a datagram whose payload named a seat its author did not hold");

            // The same host still takes P2's own input, so this is a rejection of the forgery and
            // not of the peer.
            p2.Send(InputFrom(2));
            Assert.Equal(InputFrom(2), WaitRecv(host));
        }
        finally { host.Dispose(); p1.Dispose(); p2.Dispose(); }
    }

    [Fact]
    public void APeerCannotSignAsAnotherSeatBecauseItHasNoKeyToDoItWith()
    {
        // The other half of the same attack: rather than lying in the payload, P2 tries to author
        // the datagram as seat 1 outright. It has no K{1,0}, so nothing goes out at all.
        var ring = MeshPairKeyring.Mint(3);
        var host = MeshUdpTransport.Bind(0);
        var p2 = MeshUdpTransport.Bind(0);
        try
        {
            var ports = new Dictionary<int, int> { [0] = host.LocalPort, [2] = p2.LocalPort };
            Wire(host, 0, ring, ports, host: true);
            p2.SetPeerRoutes(new[] { new PeerRoute(0, new[] { Loop(host.LocalPort) }) });
            // P2 holds its own narrow ring but claims to be seat 1.
            p2.ApplyTokens(new MeshTokens(null, null, ring.For(2)), 1);

            p2.Send(InputFrom(1));

            AssertNoRecv(host);
            WaitUntil(() => p2.InputUnkeyed > 0,
                "a peer signing as a seat it holds no key for should send nothing, and say so");
            Assert.Equal(0, host.InputUnauthenticated); // nothing ever left the sender
        }
        finally { host.Dispose(); p2.Dispose(); }
    }

    [Fact]
    public void AnOffPathForgeryIsDropped()
    {
        // A machine that knows the addresses and the wire format but holds no key at all — the
        // off-path injector. Its datagram is well-formed in every respect except the tag.
        var ring = MeshPairKeyring.Mint(2);
        var host = MeshUdpTransport.Bind(0);
        var attacker = MeshUdpTransport.Bind(0);
        try
        {
            var ports = new Dictionary<int, int> { [0] = host.LocalPort, [1] = attacker.LocalPort };
            Wire(host, 0, ring, ports, host: true);
            attacker.SetPeerRoutes(new[] { new PeerRoute(0, new[] { Loop(host.LocalPort) }) });
            attacker.ApplyTokens(new MeshTokens(null, null, MeshPairKeyring.Mint(2)), 1); // wrong keys

            attacker.Send(InputFrom(1));

            AssertNoRecv(host);
            WaitUntil(() => host.InputUnauthenticated > 0, "a forged tag was accepted");
        }
        finally { host.Dispose(); attacker.Dispose(); }
    }

    [Fact]
    public void TheHostRetagsARelayedDatagramSoItsDestinationCanVerifyIt()
    {
        // P1 and P2 cannot reach each other — the case the relay exists for. P1's input has to
        // arrive at P2 still attributable to P1, having been tagged for the host.
        var ring = MeshPairKeyring.Mint(3);
        var host = MeshUdpTransport.Bind(0);
        var p1 = MeshUdpTransport.Bind(0);
        var p2 = MeshUdpTransport.Bind(0);
        try
        {
            var ports = new Dictionary<int, int>
                { [0] = host.LocalPort, [1] = p1.LocalPort, [2] = p2.LocalPort };
            Wire(host, 0, ring, ports, host: true);
            Wire(p2, 2, ring, ports);

            // P1 knows only the host: its leg to P2 never opened.
            p1.SetPeerRoutes(new[] { new PeerRoute(0, new[] { Loop(host.LocalPort) }) });
            p1.ApplyTokens(new MeshTokens(null, null, ring.For(1)), 1);

            // The host can only relay over a candidate it has heard from, so both joiners have to
            // have spoken first — same precondition as the plain relay test.
            p1.Send(InputFrom(1));
            Assert.Equal(InputFrom(1), WaitRecv(host));
            p2.Send(InputFrom(2));
            Assert.Equal(InputFrom(2), WaitRecv(host));

            host.SetRelayRoutes(new[]
            {
                new PeerRoute(1, new[] { Loop(p1.LocalPort) }),
                new PeerRoute(2, new[] { Loop(p2.LocalPort) }),
            });
            host.SetRelayPairs(new[] { (1, 2) });

            p1.Send(InputFrom(1));

            Assert.Equal(InputFrom(1), WaitRecv(host));
            // The bytes P2 gets are P1's, and P2 verified them under K{1,2} — a key the host holds
            // only because it holds the whole table.
            Assert.Equal(InputFrom(1), WaitRecv(p2));
            Assert.Equal(0, p2.InputUnauthenticated);
        }
        finally { host.Dispose(); p1.Dispose(); p2.Dispose(); }
    }

    [Fact]
    public void AMeshWithNoKeysSendsNothingAndSaysSo()
    {
        // The wiring-fault case. Sending untagged would have looked like a working link right up
        // until the far side silently dropped everything, so it is counted at the source instead.
        var a = MeshUdpTransport.Bind(0);
        var b = MeshUdpTransport.Bind(0);
        try
        {
            a.SetPeerRoutes(new[] { new PeerRoute(1, new[] { Loop(b.LocalPort) }) });
            a.ApplyTokens(MeshTokens.None, 0);
            b.SetPeerRoutes(new[] { new PeerRoute(0, new[] { Loop(a.LocalPort) }) });
            b.ApplyTokens(MeshTokens.None, 1);

            a.Send(InputFrom(0));

            AssertNoRecv(b);
            Assert.True(a.InputUnkeyed > 0, "an unkeyed send should be counted, not silent");
        }
        finally { a.Dispose(); b.Dispose(); }
    }

    /// <summary>A minimally shaped input datagram: codec type 1, then the author's seat — the two
    /// bytes the transport reads without decoding the rest.</summary>
    private static byte[] InputFrom(byte seat) => [1, seat, 0xAA, 0xBB];

    private static byte[] WaitRecv(MeshUdpTransport t)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            if (t.TryReceive(out var got)) return got;
            Thread.Sleep(1);
        }
        throw new Xunit.Sdk.XunitException("no datagram received within 2s");
    }

    private static void AssertNoRecv(MeshUdpTransport transport, int durationMs = 300)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            Assert.False(transport.TryReceive(out _), "a datagram that should have been refused arrived");
            Thread.Sleep(2);
        }
    }

    private static void WaitUntil(Func<bool> condition, string message, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new Xunit.Sdk.XunitException(message);
    }
}
