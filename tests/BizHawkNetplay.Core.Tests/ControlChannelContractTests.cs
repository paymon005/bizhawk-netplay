using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Every session message, driven through two live channels in the direction it really travels.
///
/// The defect this file is the answer to: <c>StateOffer</c> had encode/decode tests that passed and
/// had never once been SENT. Its body is a savestate, the frame cap said 256 KiB, and so the donor
/// answering a majority request threw, its writer treated the throw as a lost link, and the peer
/// whose state the session had just chosen was dropped. Nothing about that needed a session to
/// reproduce — it needed one test that put a realistic body through a channel.
///
/// Driven by <see cref="ControlMessageRouting"/> rather than a hand-written list, so a new message
/// type is covered here the moment it is registered.
/// </summary>
public class ControlChannelContractTests
{
    /// <summary>Comfortably past the small-frame ceiling — a deflated N64 state is in this range.</summary>
    private const int StateSizedBody = 2 * 1024 * 1024;

    private static byte[] BodyFor(ControlMessageType type)
    {
        int size = ControlMessageRouting.Size(type) == MessageSize.State ? StateSizedBody : 64;
        var body = new byte[size];
        for (int i = 0; i < body.Length; i += 997) body[i] = (byte)type;   // sparse, cheap, checkable
        return body;
    }

    /// <summary>Session-phase types only. The handshake ones are driven by HandshakeTests through
    /// the real sequence, where their ordering is the thing under test.</summary>
    public static IEnumerable<object[]> SessionTypes =>
        ControlMessageRouting.All
            .Where(ControlMessageRouting.ReaderDispatched)
            .OrderBy(t => t.ToString(), StringComparer.Ordinal)
            .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(SessionTypes))]
    public void EverySessionMessageSurvivesTheChannelInItsOwnDirection(ControlMessageType type)
    {
        // Run both roles for an Either type; a directional one runs only the way it travels.
        foreach (bool senderIsHost in new[] { true, false })
        {
            if (!ControlMessageRouting.MaySend(type, senderIsHost)) continue;

            using var pair = ChannelPair.Create();
            var body = BodyFor(type);

            var (gotType, gotBody) = pair.RoundTrip(senderIsHost, type, body);

            Assert.Equal(type, gotType);
            Assert.Equal(body.Length, gotBody.Length);
            Assert.Equal(body, gotBody);
        }
    }

    /// <summary>
    /// The three messages that carry a savestate can each actually carry one, end to end.
    ///
    /// Named here rather than read from <see cref="ControlMessageRouting"/>, deliberately. The
    /// theories above take their body size FROM the table, so they prove the channel honours the
    /// table and would happily pass with a state-bearing type mis-registered as small — which is
    /// precisely the defect that shipped. This one asserts the fact independently: these three
    /// carry two megabytes in the direction their handlers read them, or the test fails.
    /// </summary>
    [Theory]
    [InlineData(ControlMessageType.State, true)]        // host -> joiner: the initial baseline
    [InlineData(ControlMessageType.Resync, true)]       // host -> joiner: a recovery baseline
    [InlineData(ControlMessageType.StateOffer, false)]  // joiner -> host: the majority's state
    public void TheStateBearingMessagesReallyCarryAState(ControlMessageType type, bool senderIsHost)
    {
        using var pair = ChannelPair.Create();
        var body = new byte[StateSizedBody];
        for (int i = 0; i < body.Length; i += 4093) body[i] = (byte)(i / 4093);

        var (gotType, gotBody) = pair.RoundTrip(senderIsHost, type, body);

        Assert.Equal(type, gotType);
        Assert.Equal(body, gotBody);
    }

    /// <summary>
    /// A state-sized body in the WRONG direction is refused, and refused at the sender rather than
    /// after the receiver has allocated it.
    ///
    /// This is the half that turns the ceiling from "authenticated peers may be large" into
    /// "the end that legitimately sends this may be large" — without it, an admitted peer can make
    /// its counterpart allocate 64 MiB for a message that end does not even handle.
    /// </summary>
    [Fact]
    public void AStateSizedBodySentTheWrongWayIsRefusedBySender()
    {
        foreach (var type in ControlMessageRouting.All
                     .Where(t => ControlMessageRouting.Size(t) == MessageSize.State))
        {
            foreach (bool senderIsHost in new[] { true, false })
            {
                if (ControlMessageRouting.MaySend(type, senderIsHost)) continue;   // the legal way

                using var pair = ChannelPair.Create();
                var ex = Assert.Throws<ArgumentException>(
                    () => pair.For(senderIsHost).Send(type, new byte[StateSizedBody]));
                Assert.Contains(type.ToString(), ex.Message);
            }
        }
    }

    /// <summary>
    /// Small messages are capped small in BOTH directions, so a type that is not supposed to carry
    /// a state cannot be used to buy the large ceiling.
    /// </summary>
    [Theory]
    [MemberData(nameof(SessionTypes))]
    public void ASmallMessageCannotCarryAStateSizedBody(ControlMessageType type)
    {
        if (ControlMessageRouting.Size(type) == MessageSize.State) return;

        foreach (bool senderIsHost in new[] { true, false })
        {
            if (!ControlMessageRouting.MaySend(type, senderIsHost)) continue;
            using var pair = ChannelPair.Create();
            Assert.Throws<ArgumentException>(
                () => pair.For(senderIsHost).Send(type, new byte[StateSizedBody]));
        }
    }

    /// <summary>
    /// An unauthenticated connection gets the small ceiling for everything, including the types
    /// that may legitimately be enormous once the password is proved. A state is the one thing on
    /// this channel that may be huge, and it is also the one thing nobody may send before the
    /// handshake reaches that point.
    /// </summary>
    [Fact]
    public void NothingIsLargeBeforeThePasswordIsProved()
    {
        foreach (var type in ControlMessageRouting.All
                     .Where(t => ControlMessageRouting.Size(t) == MessageSize.State))
        {
            using var pair = ChannelPair.Create(authenticated: false);
            foreach (bool senderIsHost in new[] { true, false })
                Assert.Throws<ArgumentException>(
                    () => pair.For(senderIsHost).Send(type, new byte[StateSizedBody]));
        }
    }

    /// <summary>
    /// Several frames in a row keep their order and their integrity sequence.
    ///
    /// The MAC is bound to a per-direction counter, so a channel that miscounts — by consuming a
    /// tag it should not have, or skipping one — fails on the frame AFTER the mistake rather than
    /// the frame that made it. One send is not enough to catch that; this drives a run in both
    /// directions on one pair.
    /// </summary>
    [Fact]
    public void ARunOfFramesKeepsItsOrderAndItsSequence()
    {
        using var pair = ChannelPair.Create();
        var sent = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var body = new byte[] { (byte)i, 0xAB, 0xCD };
            sent.Add(body);
            pair.Joiner.Send(ControlMessageType.Checksum, body);
            pair.Host.Send(ControlMessageType.PeerList, body);   // the other direction, interleaved
        }
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(sent[i], pair.Host.Receive().body);
            Assert.Equal(sent[i], pair.Joiner.Receive().body);
        }
    }

    /// <summary>
    /// A frame declaring a length the sender never had standing to ask for is refused at the header,
    /// before the bytes are allocated. Hand-built, because a well-behaved <c>Send</c> cannot produce
    /// it — which is the point: this is what a hostile or corrupt peer sends.
    /// </summary>
    [Fact]
    public void AnOversizedDeclarationIsRefusedAtTheHeader()
    {
        var header = new byte[5];
        header[0] = (byte)ControlMessageType.Checksum;
        int declared = 2 * 1024 * 1024;
        header[1] = (byte)(declared >> 24); header[2] = (byte)(declared >> 16);
        header[3] = (byte)(declared >> 8); header[4] = (byte)declared;

        var channel = new ControlChannel(new MemoryStream(header)) { Authenticated = true };
        Assert.Throws<InvalidDataException>(() => channel.Receive());
    }
}
