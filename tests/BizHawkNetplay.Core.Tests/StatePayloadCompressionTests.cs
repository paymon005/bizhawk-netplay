using System;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The authoritative state on the wire. A resync, a rejoin and a live settings change all stop
/// every emulator until this lands, and on a heavy core it is 16.7 MiB of mostly-not-random RAM —
/// which over a real upstream link is the difference between a pause and something that reads as a
/// crash. Nothing here had been compressed at all.
/// </summary>
public class StatePayloadCompressionTests
{
    private static readonly SessionGeneration Gen = new(0x0123456789ABCDEFUL, 7);

    private static byte[] RoundTrip(byte[] state)
    {
        var body = ControlMessageCodec.EncodeStatePayload(Gen, state);
        Assert.True(ControlMessageCodec.TryDecodeStatePayload(body, out var gen, out var got));
        Assert.Equal(Gen, gen);
        return got;
    }

    [Fact]
    public void ASavestateSurvivesTheRoundTripByteForByte()
    {
        // Console RAM shape: long runs, repeated structures, some noise.
        var state = new byte[512 * 1024];
        var rng = new Random(1234);
        for (int i = 0; i < state.Length; i++)
            state[i] = (byte)(i % 4096 < 3000 ? 0 : rng.Next(256));

        Assert.Equal(state, RoundTrip(state));
    }

    [Fact]
    public void CompressibleStatesActuallyGetSmaller()
    {
        // The whole point. A megabyte of mostly-zero RAM is the realistic case for a big core.
        var state = new byte[1024 * 1024];
        for (int i = 0; i < state.Length; i += 64) state[i] = (byte)(i / 64);

        var body = ControlMessageCodec.EncodeStatePayload(Gen, state);
        Assert.True(body.Length < state.Length / 4,
            $"expected real compression, got {body.Length} bytes from {state.Length}");
        Assert.Equal(state, RoundTrip(state));
    }

    [Fact]
    public void IncompressibleStatesCostOnlyTheHeader()
    {
        // Deflate loses on random data, so the encoder must fall back to raw rather than ship a
        // payload bigger than the state it is carrying.
        var state = new byte[256 * 1024];
        new Random(99).NextBytes(state);

        var body = ControlMessageCodec.EncodeStatePayload(Gen, state);
        Assert.Equal(12 + 5 + state.Length, body.Length);
        Assert.Equal(state, RoundTrip(state));
    }

    [Fact]
    public void AnEmptyStateIsStillAValidPayload()
    {
        Assert.Empty(RoundTrip(Array.Empty<byte>()));
    }

    [Fact]
    public void ADecompressionBombIsRefusedBeforeAnythingIsAllocated()
    {
        // The declared length arrives on a peer's word, and a few compressed bytes can claim to
        // expand to any size at all. The cap is checked before the buffer exists.
        var body = ControlMessageCodec.EncodeStatePayload(Gen, new byte[1024]);
        // Overwrite the declared uncompressed length with something absurd.
        body[12 + 1] = 0x7F; body[12 + 2] = 0xFF; body[12 + 3] = 0xFF; body[12 + 4] = 0xFF;

        Assert.False(ControlMessageCodec.TryDecodeStatePayload(body, out _, out var state));
        Assert.Empty(state);
    }

    [Fact]
    public void AStreamThatDoesNotMatchItsDeclaredLengthIsRefused()
    {
        // Truncated into a shorter state, an import would succeed and desync instead of failing.
        var real = new byte[64 * 1024];
        for (int i = 0; i < real.Length; i++) real[i] = (byte)(i % 251);
        var body = ControlMessageCodec.EncodeStatePayload(Gen, real);

        // Claim fewer bytes than the stream will actually produce.
        body[12 + 1] = 0; body[12 + 2] = 0; body[12 + 3] = 0x10; body[12 + 4] = 0x00;
        Assert.False(ControlMessageCodec.TryDecodeStatePayload(body, out _, out _));

        // ...and more than it will produce.
        body[12 + 1] = 0; body[12 + 2] = 0xFF; body[12 + 3] = 0xFF; body[12 + 4] = 0xFF;
        Assert.False(ControlMessageCodec.TryDecodeStatePayload(body, out _, out _));
    }

    [Fact]
    public void GarbageAndTruncatedBodiesAreRefusedRatherThanThrowing()
    {
        Assert.False(ControlMessageCodec.TryDecodeStatePayload(null!, out _, out _));
        Assert.False(ControlMessageCodec.TryDecodeStatePayload(new byte[12], out _, out _));
        Assert.False(ControlMessageCodec.TryDecodeStatePayload(new byte[16], out _, out _));

        // A valid header with an unknown format byte is a peer on a build we don't understand.
        var body = ControlMessageCodec.EncodeStatePayload(Gen, new byte[64]);
        body[12] = 9;
        Assert.False(ControlMessageCodec.TryDecodeStatePayload(body, out _, out _));

        // Deflate bytes replaced with noise.
        var real = ControlMessageCodec.EncodeStatePayload(Gen, new byte[8192]);
        if (real[12] == 1)
        {
            new Random(7).NextBytes(real);
            real[12] = 1;
            Assert.False(ControlMessageCodec.TryDecodeStatePayload(real, out _, out _));
        }
    }

    [Fact]
    public void TheHandshakeUsesTheSameFramingWithNoGenerationPrefix()
    {
        // The initial join ships its state bare over the control channel, not through the resync
        // codec — so it gets the framing directly, and had to be wired separately. Nine existing
        // handshake tests caught it when it wasn't.
        var state = new byte[128 * 1024];
        for (int i = 0; i < state.Length; i++) state[i] = (byte)(i & 7);

        var packed = StateCompression.Pack(state);
        Assert.True(packed.Length < state.Length / 4);
        Assert.True(StateCompression.TryUnpack(packed, ControlMessageCodec.MaxStateBytes, out var got));
        Assert.Equal(state, got);
    }

    [Fact]
    public void UnpackingHonoursItsOwnCapAndBounds()
    {
        var packed = StateCompression.Pack(new byte[4096]);

        // The cap is the caller's to set, and a state larger than it is refused rather than read.
        Assert.False(StateCompression.TryUnpack(packed, maxBytes: 1024, out _));
        Assert.True(StateCompression.TryUnpack(packed, maxBytes: 4096, out _));

        // Offsets outside the array are refused, not indexed.
        Assert.False(StateCompression.TryUnpack(packed, -1, packed.Length, 4096, out _));
        Assert.False(StateCompression.TryUnpack(packed, 0, packed.Length + 1, 4096, out _));
        Assert.False(StateCompression.TryUnpack(packed, 0, 4, 4096, out _));  // shorter than a header
    }

    [Fact]
    public void TheAnnouncedSizeStaysTheUncompressedOne()
    {
        // Deliberate: every receiver-side length check and both transfer deadlines are written
        // against the state's real size. Leaving them there means compression can only make a
        // deadline more generous, never cause a timeout on a link that was previously fine.
        var state = new byte[200 * 1024];
        var begin = ControlMessageCodec.EncodeResyncBegin(Gen, state.Length, inputDelay: 2,
            SyncMode.Rollback);

        Assert.True(ControlMessageCodec.TryDecodeResyncBegin(begin, out _, out int announced,
            out _, out _, out _, out _));
        Assert.Equal(state.Length, announced);
        Assert.Equal(announced, RoundTrip(state).Length);
    }
}
