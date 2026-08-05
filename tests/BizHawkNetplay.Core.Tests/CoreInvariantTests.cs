using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Properties the code relies on and states only in prose. Each one here was reasoned through
/// during a review and believed; none of them was pinned, which is the same position the FIN linger
/// and the frame cap were in before they turned out to be wrong.
/// </summary>
public class CoreInvariantTests
{
    // ---------------------------------------------------------------- pair keys

    /// <summary>
    /// Distinct seat pairs get distinct keys, across the whole range the session can use.
    ///
    /// The transport's packing had the same shape at eight bits a side and collided —
    /// <c>PairKey(5, 1000)</c> and <c>PairKey(7, 232)</c> both came out 0x7E8, with
    /// <c>PunchTargetPortBase = 1000</c> fifteen lines below it. The keyring's is provably safe at
    /// eight bits because every entry validates against MaxPlayers, and this is what "provably"
    /// should mean.
    /// </summary>
    [Fact]
    public void EveryDistinctSeatPairGetsADistinctKey()
    {
        var seen = new Dictionary<int, (int, int)>();
        for (int a = 0; a < HandshakeCodec.MaxPlayers; a++)
            for (int b = a + 1; b < HandshakeCodec.MaxPlayers; b++)
            {
                int key = MeshPairKeyring.PairKey(a, b);
                Assert.False(seen.TryGetValue(key, out var other),
                    $"pair ({a},{b}) collides with {other} at key 0x{key:X}");
                seen[key] = (a, b);
            }
        Assert.Equal(HandshakeCodec.MaxPlayers * (HandshakeCodec.MaxPlayers - 1) / 2, seen.Count);
    }

    [Fact]
    public void APairKeyDoesNotCareWhichWayRoundItIsAsked()
    {
        for (int a = 0; a < HandshakeCodec.MaxPlayers; a++)
            for (int b = 0; b < HandshakeCodec.MaxPlayers; b++)
                Assert.Equal(MeshPairKeyring.PairKey(a, b), MeshPairKeyring.PairKey(b, a));
    }

    /// <summary>
    /// The packing's domain, stated as a test rather than as the word "provably" in a comment.
    ///
    /// <see cref="MeshPairKeyring.PairKey"/> spends eight bits a side and
    /// <see cref="MeshPairKeyring.For"/> decodes with <c>&amp; 0xFF</c>; the tag prefix writes the
    /// author as <c>(byte)author</c>. All three stop being true at 256 seats. The cap moved once
    /// already (KI-22 raised it), so the thing to pin is the relationship, not the number.
    /// </summary>
    [Fact]
    public void TheSeatCapFitsThePackingThatAssumesIt()
    {
        Assert.True(HandshakeCodec.MaxPlayers <= 256,
            $"MaxPlayers is {HandshakeCodec.MaxPlayers}; PairKey packs a seat into 8 bits, For() " +
            "decodes with & 0xFF, and MeshTagger writes the author as a single byte");
    }

    /// <summary>
    /// A minted table hands each seat exactly the pairs it belongs to — the property the whole
    /// scheme rests on, since a seat holding one extra key can impersonate a player it is not.
    ///
    /// This drives the encode and the decode against each other: <c>Mint</c> packs, <c>For</c>
    /// unpacks with a mask, and only the round trip catches a width the two disagree on.
    /// </summary>
    [Fact]
    public void EachSeatIsHandedItsOwnPairsAndNoOthers()
    {
        const int players = HandshakeCodec.MaxPlayers;
        var full = MeshPairKeyring.Mint(players);
        Assert.Equal(players * (players - 1) / 2, full.Count);

        for (int me = 0; me < players; me++)
        {
            var mine = full.For(me);
            Assert.Equal(players - 1, mine.Count);
            for (int other = 0; other < players; other++)
            {
                if (other == me) continue;
                Assert.True(mine.Has(me, other), $"seat {me} was not given K{{{me},{other}}}");
                for (int third = 0; third < players; third++)
                    if (third != me && third != other && other < third)
                        Assert.False(mine.Has(other, third),
                            $"seat {me} holds K{{{other},{third}}} — a pair it is not in");
            }
        }
    }

    // ---------------------------------------------------------------- mask ranges

    /// <summary>
    /// A learned mask never describes a range that runs backwards or past the end of the domain.
    ///
    /// The ranges become byte spans the checksum skips. An inverted one would skip nothing (or, on
    /// a future reader that trusts it, something arbitrary), and one past the end would be read off
    /// the end of the buffer. Bucket spans are rounded UP to a word boundary, so the last bucket can
    /// start beyond a domain whose size is not a multiple of the span — which is exactly the case
    /// the non-power-of-two sizes below cover.
    /// </summary>
    [Theory]
    [InlineData(8 * 1024)]          // GB main RAM
    [InlineData(64 * 1024)]         // NES-ish
    [InlineData(2 * 1024 * 1024)]   // Genesis
    [InlineData(8 * 1024 * 1024)]   // N64 RDRAM
    [InlineData(4 * 1024 + 1)]      // deliberately not a multiple of anything
    [InlineData(1000)]
    [InlineData(257)]
    [InlineData(3)]                 // smaller than the bucket count
    public void AMaskNeverDescribesAnImpossibleRange(long domainSize)
    {
        int buckets = ControlMessageCodec.DivergenceBuckets;
        // Several shapes: all set, alternating, one at each end, and a random spread.
        var rng = new Random(4242);
        foreach (var mask in Shapes(buckets, rng))
        {
            foreach (var (start, endExclusive) in DivergenceLearner.MaskRanges(mask, domainSize))
            {
                Assert.True(start >= 0, $"range starts at {start} for a {domainSize}-byte domain");
                Assert.True(endExclusive > start,
                    $"range [{start},{endExclusive}) runs backwards for a {domainSize}-byte domain");
                Assert.True(endExclusive <= domainSize,
                    $"range [{start},{endExclusive}) runs past a {domainSize}-byte domain");
            }
        }
    }

    private static IEnumerable<bool[]> Shapes(int buckets, Random rng)
    {
        var all = new bool[buckets];
        for (int i = 0; i < buckets; i++) all[i] = true;
        yield return all;

        var alternating = new bool[buckets];
        for (int i = 0; i < buckets; i++) alternating[i] = i % 2 == 0;
        yield return alternating;

        var ends = new bool[buckets];
        ends[0] = true;
        ends[buckets - 1] = true;
        yield return ends;

        var scattered = new bool[buckets];
        for (int i = 0; i < buckets; i++) scattered[i] = rng.Next(4) == 0;
        yield return scattered;

        yield return new bool[buckets];   // none set: no ranges at all
    }

    [Fact]
    public void AnEmptyMaskExcludesNothing()
    {
        Assert.Empty(DivergenceLearner.MaskRanges(
            new bool[ControlMessageCodec.DivergenceBuckets], 8 * 1024 * 1024));
    }

    // ---------------------------------------------------------------- input pipeline

    /// <summary>
    /// Nothing is ever stored below the prune watermark.
    ///
    /// <c>PruneBefore</c> walks DOWN from the boundary and stops at the first frame that is not
    /// there, which is fast because normally one frame ages out per call. That is only safe while
    /// nothing adds an entry BELOW the watermark afterwards — such an entry would never be visited
    /// again and would sit there for the rest of the session. The driver's accept window is what
    /// makes it safe, and this pins the two together rather than leaving it to a reader to
    /// re-derive.
    /// </summary>
    [Fact]
    public void NothingSurvivesBelowThePruneWatermark()
    {
        const int ports = 4;
        var pipeline = new InputPipeline(ports);
        var layout = new ControllerLayout(new[] { "A" }, Array.Empty<AxisSpec>());
        var input = PortInput.Neutral(layout);

        // Advance a long way, pruning as the driver does, while input arrives raggedly — including
        // attempts to add frames that have already aged out, which the driver would refuse but the
        // pipeline must survive being handed.
        var rng = new Random(99);
        int watermark = 0;
        for (int frame = 0; frame < 2000; frame++)
        {
            for (int p = 0; p < ports; p++)
                if (rng.Next(4) != 0) pipeline.Add(p, frame, input);

            // The driver prunes to CurrentFrame - historyKeep; model a keep of 32.
            int keepFrom = frame - 32;
            if (keepFrom > 0) { pipeline.PruneBefore(keepFrom); watermark = keepFrom; }
        }

        // Every frame below the watermark must be gone from every port. TryGet is the only reader,
        // so asking it directly is asking the question the invariant is about.
        for (int p = 0; p < ports; p++)
            for (int frame = 0; frame < watermark; frame++)
                Assert.False(pipeline.TryGet(p, frame, out _),
                    $"port {p} still holds frame {frame}, below the {watermark} watermark");
    }

    /// <summary>
    /// A prune boundary that goes BACKWARDS — a rollback repair, or a rebuilt timeline — leaves the
    /// watermark following it down, so the frames between the new boundary and the old one are
    /// still swept when the boundary next moves forward past them.
    ///
    /// That is the whole reason the backwards branch assigns rather than returning: leave the
    /// watermark at 150 and a later <c>PruneBefore(120)</c> reads as another backwards move and
    /// drops nothing, so everything re-added in 10..119 stays for the rest of the session.
    ///
    /// Note what is NOT claimed: frames re-added BELOW the new boundary are not swept by the
    /// incremental walk, and nothing needs them to be — the driver's accept floor is
    /// <c>CurrentFrame - rollbackWindow - 1</c> while it prunes to
    /// <c>CurrentFrame - (delay + redundancy + 2 + rollbackWindow)</c>, so an accepted frame is
    /// always at least <c>delay + redundancy + 1</c> above the watermark.
    /// <see cref="NothingSurvivesBelowThePruneWatermark"/> is what pins that.
    /// </summary>
    [Fact]
    public void AWatermarkThatGoesBackwardsStillSweepsWhatItPassed()
    {
        var pipeline = new InputPipeline(2);
        var layout = new ControllerLayout(new[] { "A" }, Array.Empty<AxisSpec>());
        var input = PortInput.Neutral(layout);

        for (int frame = 0; frame < 200; frame++) pipeline.Add(0, frame, input);
        pipeline.PruneBefore(150);
        pipeline.PruneBefore(10);      // the timeline was rebuilt beneath us

        for (int frame = 10; frame < 200; frame++) pipeline.Add(0, frame, input);
        pipeline.PruneBefore(120);

        for (int frame = 10; frame < 120; frame++)
            Assert.False(pipeline.TryGet(0, frame, out _),
                $"frame {frame} survived a boundary that moved back and then forward past it");
        Assert.True(pipeline.TryGet(0, 120, out _), "the sweep took a frame it was told to keep");
    }
}
