using System;
using System.Collections.Generic;
using System.Net;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The parsers of remote bytes that the control-message fuzz did not reach.
///
/// <c>CodecFuzzTests</c> covers <c>ControlMessageCodec</c> and <c>HandshakeCodec</c> — everything on
/// the TCP control channel. It does not cover the UDP side, and the omission is the wrong way round:
/// <see cref="InputPacketCodec"/> parses roughly sixty datagrams a second per peer, arriving on a
/// socket any host on the internet can send to, and its output indexes arrays and seeds frame
/// numbers. <see cref="InputSerializer"/> unpacks the bytes it points at. <see cref="StunClient"/>
/// parses a reply from a public server nobody in the session controls.
///
/// The contract is the one the control decoders have: <b>refuse, or return a value inside its own
/// declared domain, and never throw.</b> These run on the receive thread and inside the frame
/// callback — an escape from either is a dead session, not a dropped packet.
/// </summary>
public class WireParserFuzzTests
{
    private static readonly SessionGeneration Gen = new(0x0BADF00D, 5);

    private static ControllerLayout Pad() => new(
        new[] { "Up", "Down", "Left", "Right", "A", "B", "Start", "Select" },
        Array.Empty<AxisSpec>());

    private static ControllerLayout Stick() => new(
        new[] { "A", "B" },
        new[] { new AxisSpec("X", -128, 127, 0), new AxisSpec("Y", -128, 127, 0) });

    private static InputPacketCodec Codec(params int[] sizes) => new(sizes, Gen);

    /// <summary>A well-formed input datagram, to be mutated.</summary>
    private static byte[] Datagram(InputPacketCodec codec, byte port, int baseFrame, int count)
    {
        var window = new List<KeyValuePair<int, byte[]>>();
        int size = codec.PayloadSizeFor(port);
        for (int i = 0; i < count; i++) window.Add(new(baseFrame + i, new byte[size]));
        return codec.EncodeInput(port, window);
    }

    // ---------------------------------------------------------------- input datagrams

    /// <summary>
    /// Noise, truncation and bit flips over the input decoder.
    ///
    /// Its outputs are not merely values: <c>Port</c> indexes a per-port array, <c>BaseFrame</c>
    /// becomes a dictionary key and a rollback target, and <c>OffsetOf</c> becomes an index into
    /// the datagram itself. A decoder that said yes to a shape it had not checked would hand those
    /// straight to the pipeline.
    /// </summary>
    [Fact]
    public void TheInputDecoderNeverThrowsAndNeverAcceptsAnImpossibleWindow()
    {
        var codec = Codec(8, 8, 4, 0);   // port 3 has no serialisable layout on this machine
        var rng = new Random(0x19507);
        var corpus = new List<byte[]>
        {
            Datagram(codec, 0, 0, 1),
            Datagram(codec, 1, 12345, 8),
            Datagram(codec, 2, int.MaxValue - 9, 8),
            codec.EncodeRequest(1, 0),
            codec.EncodeRequest(2, int.MaxValue),
            Array.Empty<byte>(),
        };

        foreach (var sample in new List<byte[]>(corpus))
        {
            for (int cut = 0; cut <= sample.Length; cut++)
            {
                var truncated = new byte[cut];
                Buffer.BlockCopy(sample, 0, truncated, 0, cut);
                corpus.Add(truncated);
            }
            for (int trial = 0; trial < 60 && sample.Length > 0; trial++)
            {
                var flipped = (byte[])sample.Clone();
                flipped[rng.Next(flipped.Length)] ^= (byte)(1 << rng.Next(8));
                corpus.Add(flipped);
            }
        }
        for (int length = 0; length <= 96; length++)
            for (int trial = 0; trial < 6; trial++)
            {
                var noise = new byte[length];
                rng.NextBytes(noise);
                corpus.Add(noise);
            }

        int accepted = 0;
        foreach (var datagram in corpus)
        {
            bool ok;
            InputPacketCodec.InputWindow window;
            try { ok = codec.TryDecodeInputWindow(datagram, out window); }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException(
                    $"TryDecodeInputWindow threw {e.GetType().Name} on a {datagram.Length}-byte " +
                    $"datagram — on the receive thread that is a dead session. {e.Message}");
            }
            if (!ok) continue;
            accepted++;

            // Everything an accepted window promises, because every one of these is used as an
            // index or a frame number without being re-checked.
            Assert.InRange((int)window.Port, 0, 2);
            Assert.True(window.PayloadSize > 0, "a window promised a port with no layout");
            Assert.InRange(window.Count, 1, 255);
            Assert.True(window.BaseFrame >= 0, $"negative base frame {window.BaseFrame}");
            Assert.True(window.BaseFrame <= int.MaxValue - window.Count,
                "the window's last frame overflows int");
            // The offsets it hands out stay inside the datagram it described.
            for (int i = 0; i < window.Count; i++)
            {
                int at = window.OffsetOf(i);
                Assert.InRange(at, 0, datagram.Length - window.PayloadSize);
            }
            Assert.Equal(datagram.Length, window.OffsetOf(window.Count - 1) + window.PayloadSize);
        }

        Assert.True(accepted > 20,
            $"only {accepted} windows were accepted — the corpus proves nothing about what a " +
            "'true' return guarantees");
    }

    [Fact]
    public void TheRequestDecoderNeverThrowsAndNeverAcceptsAnImpossibleFrame()
    {
        var codec = Codec(8, 8, 4);
        var rng = new Random(0x5EED1);
        var corpus = new List<byte[]> { codec.EncodeRequest(1, 0), codec.EncodeRequest(2, 999_999) };
        foreach (var sample in new List<byte[]>(corpus))
            for (int trial = 0; trial < 400; trial++)
            {
                var flipped = (byte[])sample.Clone();
                flipped[rng.Next(flipped.Length)] ^= (byte)(1 << rng.Next(8));
                corpus.Add(flipped);
            }
        for (int length = 0; length <= 40; length++) corpus.Add(new byte[length]);

        foreach (var datagram in corpus)
        {
            bool ok;
            byte port;
            int fromFrame;
            try { ok = codec.TryDecodeRequest(datagram, out port, out fromFrame); }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException(
                    $"TryDecodeRequest threw {e.GetType().Name}: {e.Message}");
            }
            if (!ok) continue;
            Assert.InRange((int)port, 0, 2);
            Assert.True(fromFrame >= 0, $"a request asked for negative frame {fromFrame}");
        }
    }

    /// <summary>
    /// A datagram from a peer whose generation has moved on is refused and counted, never decoded.
    ///
    /// The counter matters as much as the refusal: a rejected input packet used to vanish silently,
    /// which is how a whole session of one-way input loss reads as "the network is bad".
    /// </summary>
    [Fact]
    public void AForeignGenerationIsRefusedAndCounted()
    {
        var mine = Codec(8);
        var theirs = new InputPacketCodec(new[] { 8 }, new SessionGeneration(Gen.SessionId, Gen.Epoch + 1));
        long before = mine.RejectedGeneration;
        Assert.False(mine.TryDecodeInputWindow(Datagram(theirs, 0, 10, 4), out _));
        Assert.True(mine.RejectedGeneration > before);
    }

    // ---------------------------------------------------------------- payload unpacking

    /// <summary>
    /// The serializer unpacks whatever bytes the window pointed at, and those are a peer's.
    ///
    /// Buttons are bits and axes are clamped ranges, so there is no byte pattern it may refuse —
    /// which makes "never throws, always in range" the whole contract. An axis outside its declared
    /// range would reach the core as an input the layout says cannot exist.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheSerializerAcceptsAnyBytesAndStaysInsideTheLayout(int which)
    {
        var layout = which == 0 ? Pad() : Stick();
        var serializer = new InputSerializer(layout);
        var rng = new Random(0xB175 + which);
        var payload = new byte[serializer.PayloadSize];

        for (int trial = 0; trial < 3000; trial++)
        {
            rng.NextBytes(payload);
            PortInput input;
            try { input = serializer.Deserialize(payload); }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Deserialize threw {e.GetType().Name} on random bytes: {e.Message}");
            }
            Assert.Equal(layout.Buttons.Count, input.Buttons.Length);
            Assert.Equal(layout.Axes.Count, input.Axes.Length);
            for (int a = 0; a < layout.Axes.Count; a++)
                Assert.InRange(input.Axes[a], layout.Axes[a].Min, layout.Axes[a].Max);
        }
    }

    [Fact]
    public void TheSerializerReadsAtAnOffsetWithoutRunningPastTheBuffer()
    {
        // The driver always calls the offset overload, with an offset the window computed.
        var serializer = new InputSerializer(Pad());
        var rng = new Random(0x0FF5E7);
        int size = serializer.PayloadSize;
        var buffer = new byte[size * 4];
        for (int trial = 0; trial < 2000; trial++)
        {
            rng.NextBytes(buffer);
            int offset = rng.Next(0, 4) * size;
            var input = serializer.Deserialize(buffer, offset);
            Assert.Equal(Pad().Buttons.Count, input.Buttons.Length);
        }
    }

    // ---------------------------------------------------------------- STUN

    /// <summary>
    /// A STUN reply comes from a public server nobody in the session runs, over UDP, to a socket
    /// anyone can send to. Its answer becomes this machine's advertised public address.
    ///
    /// Two properties: it never throws, and it never returns an endpoint for a reply whose
    /// transaction ID is not the one we sent — otherwise an off-path party who guessed the timing
    /// could tell us where we are, and the whole mesh would be aimed at an address of their
    /// choosing.
    /// </summary>
    [Fact]
    public void TheStunParserNeverThrowsAndHonoursTheTransactionId()
    {
        var request = StunClient.BuildRequest(out var txn);
        var rng = new Random(0x5701);
        var wrongTxn = new byte[txn.Length];
        rng.NextBytes(wrongTxn);

        var corpus = new List<byte[]> { Array.Empty<byte>(), request };
        for (int length = 0; length <= 80; length++)
            for (int trial = 0; trial < 20; trial++)
            {
                var noise = new byte[length];
                rng.NextBytes(noise);
                corpus.Add(noise);
                // ...and the same noise wearing OUR transaction id, which is the case that gets
                // past a length check and reaches the attribute walk.
                if (length >= 20)
                {
                    var stamped = (byte[])noise.Clone();
                    Buffer.BlockCopy(txn, 0, stamped, 8, Math.Min(txn.Length, stamped.Length - 8));
                    corpus.Add(stamped);
                }
            }

        foreach (var reply in corpus)
        {
            IPEndPoint? mine;
            try { mine = StunClient.ParseResponse(reply, txn); }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException(
                    $"ParseResponse threw {e.GetType().Name} on a {reply.Length}-byte reply: {e.Message}");
            }
            if (mine != null)
            {
                Assert.InRange(mine.Port, 1, 65535);
                Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, mine.AddressFamily);
            }

            // A reply for somebody else's transaction is never ours, whatever else it contains.
            Assert.Null(StunClient.ParseResponse(reply, wrongTxn));
        }
    }
}
