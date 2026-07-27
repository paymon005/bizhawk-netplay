using System;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// The in-session control-message wire format. Round trips prove encode/decode symmetry; the
    /// byte-level pins lock the exact big-endian layout so an accidental endianness or offset
    /// change breaks a test instead of desyncing two builds of the tool against each other.
    /// </summary>
    public class ControlMessageCodecTests
    {
        private static readonly SessionGeneration Gen = new SessionGeneration(0x0102030405060708UL, 9);

        [Fact]
        public void Pacing_RoundTrips_AndRejectsWrongShapes()
        {
            var body = ControlMessageCodec.EncodePacing(Gen, 17, 16, 1234, -3);
            Assert.Equal(ControlMessageCodec.PacingSize, body.Length);

            Assert.True(ControlMessageCodec.TryDecodePacing(body, out var generation,
                out int sequence, out int acknowledges, out int frame, out int advantage));
            Assert.Equal(Gen, generation);
            Assert.Equal(17, sequence);
            Assert.Equal(16, acknowledges);
            Assert.Equal(1234, frame);
            Assert.Equal(-3, advantage); // sign must survive the trip — advantage is signed

            Assert.False(ControlMessageCodec.TryDecodePacing(null!, out _, out _, out _, out _, out _));
            Assert.False(ControlMessageCodec.TryDecodePacing(new byte[27], out _, out _, out _, out _, out _));
            var zeroSession = (byte[])body.Clone();
            for (int i = 0; i < 8; i++) zeroSession[i] = 0; // session ID 0 is never valid
            Assert.False(ControlMessageCodec.TryDecodePacing(zeroSession, out _, out _, out _, out _, out _));
        }

        [Fact]
        public void Checksum_RoundTrips_AndOnlyForTheExpectedGeneration()
        {
            var body = ControlMessageCodec.EncodeChecksum(Gen, 300, 0xDEADBEEF);
            Assert.True(ControlMessageCodec.TryDecodeChecksum(body, Gen, out int frame, out uint hash));
            Assert.Equal(300, frame);
            Assert.Equal(0xDEADBEEFu, hash);

            // A dead timeline's checksum must never decode against the live generation.
            Assert.False(ControlMessageCodec.TryDecodeChecksum(body, Gen.Next(), out _, out _));
            Assert.False(ControlMessageCodec.TryDecodeChecksum(new byte[19], Gen, out _, out _));
        }

        [Fact]
        public void ResyncBegin_RoundTrips_AndBoundsItsFields()
        {
            var body = ControlMessageCodec.EncodeResyncBegin(Gen, 4096, 60);
            Assert.True(ControlMessageCodec.TryDecodeResyncBegin(body, out var generation,
                out int stateBytes, out int waitSeconds));
            Assert.Equal(Gen, generation);
            Assert.Equal(4096, stateBytes);
            Assert.Equal(60, waitSeconds);

            // Encode refuses out-of-range fields outright…
            Assert.Throws<ArgumentOutOfRangeException>(() => ControlMessageCodec.EncodeResyncBegin(Gen, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ControlMessageCodec.EncodeResyncBegin(Gen, ControlMessageCodec.MaxStateBytes + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ControlMessageCodec.EncodeResyncBegin(Gen, 0, ControlMessageCodec.MaxResyncWaitSeconds + 1));

            // …and decode treats a hostile out-of-range frame as garbage, not a huge allocation cue.
            var bogus = ControlMessageCodec.EncodeResyncBegin(Gen, 4096, 60);
            bogus[12] = 0x7F; // stateBytes ≈ int.MaxValue, far past MaxStateBytes
            Assert.False(ControlMessageCodec.TryDecodeResyncBegin(bogus, out _, out _, out _));
        }

        [Fact]
        public void StatePayload_RoundTrips_IncludingEmptyStates()
        {
            var state = new byte[] { 1, 2, 3, 4, 5 };
            Assert.True(ControlMessageCodec.TryDecodeStatePayload(
                ControlMessageCodec.EncodeStatePayload(Gen, state), out var generation, out var decoded));
            Assert.Equal(Gen, generation);
            Assert.Equal(state, decoded);

            Assert.True(ControlMessageCodec.TryDecodeStatePayload(
                ControlMessageCodec.EncodeStatePayload(Gen, Array.Empty<byte>()), out _, out var empty));
            Assert.Empty(empty);

            Assert.False(ControlMessageCodec.TryDecodeStatePayload(new byte[11], out _, out _)); // shorter than a generation
        }

        [Fact]
        public void GenerationBody_RoundTrips_AndMatchesHandshakeCodec()
        {
            // The bare-generation body (READY/GO, resync ack/resume) must stay byte-compatible with
            // HandshakeCodec's encoder — both travel on the same channel and are decoded by either.
            var body = HandshakeCodec.EncodeGeneration(Gen);
            Assert.True(ControlMessageCodec.TryDecodeGeneration(body, out var generation));
            Assert.Equal(Gen, generation);

            Assert.False(ControlMessageCodec.TryDecodeGeneration(new byte[11], out _));
            Assert.False(ControlMessageCodec.TryDecodeGeneration(new byte[13], out _));
            Assert.False(ControlMessageCodec.TryDecodeGeneration(new byte[12], out _)); // session ID 0
        }

        [Fact]
        public void WireLayout_IsBigEndian_AtTheDocumentedOffsets()
        {
            // Freeze the exact bytes. If this test breaks, the wire format changed and the protocol
            // version must be bumped — two builds disagreeing here desync silently otherwise.
            var body = ControlMessageCodec.EncodePacing(
                new SessionGeneration(0x1122334455667788UL, 0x0A0B0C0D), 0x01020304, 0x11121314, 0x21222324, 0x31323334);
            Assert.Equal(new byte[]
            {
                0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, // session ID, big-endian
                0x0A, 0x0B, 0x0C, 0x0D,                         // epoch
                0x01, 0x02, 0x03, 0x04,                         // sequence
                0x11, 0x12, 0x13, 0x14,                         // acknowledges
                0x21, 0x22, 0x23, 0x24,                         // frame
                0x31, 0x32, 0x33, 0x34,                         // local advantage
            }, body);

            var checksum = ControlMessageCodec.EncodeChecksum(
                new SessionGeneration(0x1122334455667788UL, 1), 2, 0xAABBCCDD);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x02 }, checksum[12..16]); // frame
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, checksum[16..20]); // hash
        }
    }
}
