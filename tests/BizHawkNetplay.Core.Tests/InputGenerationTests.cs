using System;
using System.Collections.Generic;
using BizHawkNetplay.Core.Net;
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
    }
}
