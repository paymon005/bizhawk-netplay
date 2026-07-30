using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Input;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using BizHawkNetplay.Core.Sync;
using BizHawkNetplay.Core.Tests.Fakes;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    public class InputGenerationTests
    {
        private sealed class QueueTransport : ITransport
        {
            private readonly Queue<byte[]> _inbound = new Queue<byte[]>();

            public void Enqueue(byte[] datagram) => _inbound.Enqueue(datagram);
            public void Send(byte[] datagram) { }

            public bool TryReceive(out byte[] datagram)
            {
                if (_inbound.Count > 0)
                {
                    datagram = _inbound.Dequeue();
                    return true;
                }

                datagram = null!;
                return false;
            }
        }

        private static IReadOnlyList<KeyValuePair<int, byte[]>> Window(int firstFrame, params byte[] values)
        {
            var frames = new List<KeyValuePair<int, byte[]>>(values.Length);
            for (int i = 0; i < values.Length; i++)
                frames.Add(new KeyValuePair<int, byte[]>(firstFrame + i, new[] { values[i] }));
            return frames;
        }

        [Fact]
        public void SessionGeneration_ValidatesAndAdvances()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SessionGeneration(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SessionGeneration(1, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new InputPacketCodec(new[] { 1 }, default(SessionGeneration)));

            var initial = new SessionGeneration(0x123456789abcdef0UL, 4);
            Assert.Equal(new SessionGeneration(initial.SessionId, 5), initial.Next());
            Assert.NotEqual(initial, initial.Next());
        }

        [Fact]
        public void Codec_RejectsWrongSessionAndEpoch()
        {
            var expected = new SessionGeneration(0x0102030405060708UL, 7);
            var packet = new InputPacketCodec(new[] { 1, 1 }, expected)
                .EncodeInput(1, Window(3, 0x5a));

            var matching = new InputPacketCodec(new[] { 1, 1 }, expected);
            Assert.True(matching.TryDecodeInput(packet, out var frames));
            Assert.Single(frames);
            Assert.Equal(3, frames[0].Frame);

            var wrongSession = new InputPacketCodec(
                new[] { 1, 1 }, new SessionGeneration(0x1112131415161718UL, expected.Epoch));
            Assert.False(wrongSession.TryDecodeInput(packet, out _));

            var wrongEpoch = new InputPacketCodec(new[] { 1, 1 }, expected.Next());
            Assert.False(wrongEpoch.TryDecodeInput(packet, out _));
        }

        [Fact]
        public void Codec_GapRequest_RoundTripsAndRejectsOtherGenerations()
        {
            var generation = new SessionGeneration(0x0102030405060708UL, 7);
            var codec = new InputPacketCodec(new[] { 1, 1 }, generation);
            var request = codec.EncodeRequest(1, 42);

            Assert.True(codec.TryDecodeRequest(request, out var port, out var fromFrame));
            Assert.Equal(1, port);
            Assert.Equal(42, fromFrame);

            // A request is not an input window and vice versa.
            Assert.False(codec.TryDecodeInput(request, out _));
            Assert.False(codec.TryDecodeRequest(codec.EncodeInput(1, Window(3, 0x5a)), out _, out _));

            // Stale-generation requests are dropped like stale input.
            var older = new InputPacketCodec(new[] { 1, 1 }, generation.Next());
            Assert.False(older.TryDecodeRequest(request, out _, out _));
        }

        [Theory]
        [InlineData(3)]
        [InlineData(30)]
        [InlineData(300)]
        public void RebuiltDriver_RejectsDelayedAndReorderedFramesFromPreviousEpoch(int staleFrame)
        {
            var oldGeneration = new SessionGeneration(0x8877665544332211UL, 2);
            var newGeneration = oldGeneration.Next();
            var transport = new QueueTransport();

            var newCodec = new InputPacketCodec(new[] { 1, 1 }, newGeneration);
            var oldCodec = new InputPacketCodec(new[] { 1, 1 }, oldGeneration);

            var emu = new FakeEmuAdapter(portCount: 2);
            var driver = new FrameDriver(
                emu,
                transport,
                pipeline => new LockstepStrategy(pipeline),
                localPort: 0,
                delay: 1,
                generation: newGeneration);

            Assert.Equal(newGeneration, driver.Generation);
            int staleArrivalFrame = Math.Max(0, staleFrame - 3);
            for (int frame = 0; frame < staleFrame; frame++)
            {
                if (frame == staleArrivalFrame)
                {
                    // A delayed old-timeline packet arrives out of order, ahead of the valid
                    // packet for the rebuilt timeline's current frame. Deliver it twice to model
                    // redundant UDP windows/duplicate delivery. It is close enough to CurrentFrame
                    // that the ordinary future-frame guard would accept it without the epoch check.
                    var stalePacket = oldCodec.EncodeInput(1, Window(staleFrame, 0x01));
                    transport.Enqueue(stalePacket);
                    transport.Enqueue((byte[])stalePacket.Clone());
                }

                transport.Enqueue(newCodec.EncodeInput(1, Window(frame, (byte)(frame & 1))));
                Assert.Equal(FrameStep.Ran, driver.OnPreFrame());
                emu.AdvanceAppliedFrame();
                driver.OnPostFrame();
            }

            Assert.Equal(staleFrame, driver.CurrentFrame);
            Assert.Equal(FrameStep.Stalled, driver.OnPreFrame());
            Assert.Equal(staleFrame, driver.CurrentFrame);
        }

        /// <summary>
        /// A refused input packet must leave evidence naming the port and both sizes.
        ///
        /// A 3-player NES session failed twice with "no UDP input from P3", dropping the session for a
        /// lost UDP path — while the two joiners swapped slots between attempts, so it was the third
        /// PORT failing rather than either person's network. The packets were arriving the whole time
        /// and being discarded for a payload-size disagreement, and the drop was a bare `return false`
        /// with no counter behind it, so nothing in any log could have said so.
        /// </summary>
        [Fact]
        public void Decode_ReportsThePortAndBothSizesWhenAPeerDisagreesOnPayloadSize()
        {
            var generation = new SessionGeneration(0x5151515151515151UL, 3);

            // Sender's port 2 serializes 3 bytes per frame (say, a multitap layout); the receiver
            // computes 1 for that port because its peripheral configuration differs.
            var sender = new InputPacketCodec(new[] { 1, 1, 3 }, generation);
            var receiver = new InputPacketCodec(new[] { 1, 1, 1 }, generation);

            var packet = sender.EncodeInput(2, new[]
            {
                new KeyValuePair<int, byte[]>(10, new byte[] { 1, 2, 3 }),
                new KeyValuePair<int, byte[]>(11, new byte[] { 4, 5, 6 }),
            });

            Assert.False(receiver.TryDecodeInput(packet, out var frames));
            Assert.Empty(frames);

            // The point of the test: the refusal is attributable, not silent.
            Assert.Equal(1, receiver.RejectedPayloadSize);
            Assert.Equal(1, receiver.RejectedTotal);
            Assert.Equal(2, receiver.LastSizeMismatchPort);
            Assert.Equal(1, receiver.LastSizeMismatchExpected);
            Assert.Equal(3, receiver.LastSizeMismatchObserved);

            // A matching peer is unaffected and counts nothing.
            var agreeing = new InputPacketCodec(new[] { 1, 1, 3 }, generation);
            Assert.True(agreeing.TryDecodeInput(packet, out var ok));
            Assert.Equal(2, ok.Count);
            Assert.Equal(0, agreeing.RejectedTotal);
        }

        /// <summary>
        /// A gap request is not an input packet and must not be counted as a refusal — otherwise the
        /// rejection counters would read non-zero on every healthy session and mean nothing.
        /// </summary>
        [Fact]
        public void Decode_DoesNotCountRequestsOrForeignGenerationsAsTheSameThing()
        {
            var generation = new SessionGeneration(0x2727272727272727UL, 1);
            var codec = new InputPacketCodec(new[] { 1, 1 }, generation);

            Assert.False(codec.TryDecodeInput(codec.EncodeRequest(1, 42), out _));
            Assert.Equal(0, codec.RejectedTotal);   // a request is not a refusal

            var foreign = new InputPacketCodec(new[] { 1, 1 }, generation.Next());
            var stale = foreign.EncodeInput(1, new[] { new KeyValuePair<int, byte[]>(5, new byte[] { 9 }) });
            Assert.False(codec.TryDecodeInput(stale, out _));
            Assert.Equal(1, codec.RejectedGeneration);
            Assert.Equal(0, codec.RejectedPayloadSize);
            Assert.Equal(-1, codec.LastSizeMismatchPort);  // no size disagreement to report
        }

        [Fact]
        public void DecodeWindow_DescribesTheSameFramesTheCopyingDecodeProduces()
        {
            var generation = new SessionGeneration(0x9191919191919191UL, 2);
            var codec = new InputPacketCodec(new[] { 1, 3 }, generation);
            var packet = codec.EncodeInput(1, new[]
            {
                new KeyValuePair<int, byte[]>(40, new byte[] { 1, 2, 3 }),
                new KeyValuePair<int, byte[]>(41, new byte[] { 4, 5, 6 }),
                new KeyValuePair<int, byte[]>(42, new byte[] { 7, 8, 9 }),
            });

            Assert.True(codec.TryDecodeInputWindow(packet, out var window));
            Assert.Equal(1, window.Port);
            Assert.Equal(40, window.BaseFrame);
            Assert.Equal(3, window.Count);
            Assert.Equal(3, window.PayloadSize);

            // The offsets must land exactly where the copying decode's payloads came from.
            Assert.True(codec.TryDecodeInput(packet, out var frames));
            Assert.Equal(window.Count, frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                Assert.Equal(window.BaseFrame + i, frames[i].Frame);
                Assert.Equal(window.Port, frames[i].Port);
                for (int b = 0; b < window.PayloadSize; b++)
                    Assert.Equal(frames[i].Payload[b], packet[window.OffsetOf(i) + b]);
            }
        }

        [Fact]
        public void DecodeWindow_RefusesAndCountsExactlyWhatTheCopyingDecodeDoes()
        {
            var generation = new SessionGeneration(0x3131313131313131UL, 1);
            var sender = new InputPacketCodec(new[] { 1, 1, 3 }, generation);
            var packet = sender.EncodeInput(2,
                new[] { new KeyValuePair<int, byte[]>(10, new byte[] { 1, 2, 3 }) });

            var viaWindow = new InputPacketCodec(new[] { 1, 1, 1 }, generation);
            var viaCopy = new InputPacketCodec(new[] { 1, 1, 1 }, generation);
            Assert.False(viaWindow.TryDecodeInputWindow(packet, out _));
            Assert.False(viaCopy.TryDecodeInput(packet, out _));

            // Both entry points share one set of acceptance rules and one set of counters, so a
            // diagnosis drawn from the logs means the same thing whichever the hot path used.
            Assert.Equal(viaCopy.RejectedTotal, viaWindow.RejectedTotal);
            Assert.Equal(viaCopy.RejectedPayloadSize, viaWindow.RejectedPayloadSize);
            Assert.Equal(viaCopy.LastSizeMismatchPort, viaWindow.LastSizeMismatchPort);
            Assert.Equal(viaCopy.LastSizeMismatchObserved, viaWindow.LastSizeMismatchObserved);

            var request = viaWindow.EncodeRequest(1, 7);
            Assert.False(viaWindow.TryDecodeInputWindow(request, out _));
            Assert.Equal(viaCopy.RejectedTotal, viaWindow.RejectedTotal); // still not a refusal
        }

        /// <summary>
        /// A session whose datagrams would not survive the path is refused at construction, with the
        /// delay that WOULD work in the message — rather than starting, looking healthy, and reporting
        /// "no UDP input from P2" forever because every packet that port sends is dropped for its size.
        /// </summary>
        [Fact]
        public void ADelayThatWouldOversizeTheDatagramIsRefusedWithTheDelayThatFits()
        {
            // 60 buttons and four 4-byte axes = 8 + 16 = 24 bytes per frame for this port.
            var wide = new ControllerLayout(
                Array.ConvertAll(new int[60], _ => "B"),
                new[]
                {
                    new AxisSpec("X", 0, 1_000_000, 0), new AxisSpec("Y", 0, 1_000_000, 0),
                    new AxisSpec("Z", 0, 1_000_000, 0), new AxisSpec("W", 0, 1_000_000, 0),
                });
            Assert.Equal(24, wide.PayloadByteWidth);

            int allowed = InputPacketCodec.MaxInputDelayFor(wide.PayloadByteWidth);
            Assert.InRange(allowed, 1, HandshakeCodec.MaxInputDelay);

            var emu = new FakeEmuAdapter(portCount: 2) { Layout = wide };
            // One over the limit is refused...
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new FrameDriver(
                emu, new QueueTransport(), p => new LockstepStrategy(p),
                localPort: 0, delay: allowed + 1));
            Assert.Contains($"input delay of {allowed} or less", ex.Message);

            // ...and the delay it names actually works.
            using var ok = new FrameDriver(emu, new QueueTransport(), p => new LockstepStrategy(p),
                localPort: 0, delay: allowed);
            Assert.Equal(0, ok.CurrentFrame);
        }

        [Fact]
        public void TheDatagramLimitLeavesOrdinaryControllersFarMoreDelayThanTheUiOffers()
        {
            // The cap must bite only on exotic layouts. A SNES pad is 2 bytes, an N64 pad with two
            // analog axes is 4 — both should clear the UI's maximum of 20 with room to spare.
            Assert.True(InputPacketCodec.MaxInputDelayFor(2) >= HandshakeCodec.MaxInputDelay);
            Assert.True(InputPacketCodec.MaxInputDelayFor(4) >= HandshakeCodec.MaxInputDelay);
            Assert.True(InputPacketCodec.MaxInputDelayFor(16) > 20);
            Assert.Equal(0, InputPacketCodec.MaxFramesPerDatagram(0)); // no layout, no frames
        }

        /// <summary>
        /// The reason the window decode exists. A datagram repeats the last R frames, so at delay 4
        /// nine frames arrive and eight are typically already held; copying all nine before the caller
        /// drops eight allocated ~1,600–2,300 arrays a second at four players. Describing the datagram
        /// in place costs nothing at all.
        /// </summary>
        [Fact]
        public void DecodeWindow_AllocatesNothing()
        {
            var generation = new SessionGeneration(0x7373737373737373UL, 4);
            var codec = new InputPacketCodec(new[] { 1, 2 }, generation);
            var frames = new List<KeyValuePair<int, byte[]>>();
            for (int f = 0; f < 9; f++) frames.Add(new KeyValuePair<int, byte[]>(100 + f, new byte[] { 1, 2 }));
            var packet = codec.EncodeInput(1, frames);

            Assert.True(codec.TryDecodeInputWindow(packet, out _)); // warm any first-call cost

            // Assertions stay outside the measured region — an assert that allocates would be
            // indistinguishable from a decode that does.
            int decodedFrames = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                if (codec.TryDecodeInputWindow(packet, out var window)) decodedFrames += window.Count;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(9000, decodedFrames);
            Assert.True(allocated == 0, $"decoding 1000 windows allocated {allocated} bytes; expected none");
        }
    }
}
