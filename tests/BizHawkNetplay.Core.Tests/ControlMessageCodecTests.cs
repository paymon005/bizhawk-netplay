using System;
using BizHawkNetplay.Core.Net;
using BizHawkNetplay.Core.Session;
using Xunit;
using static BizHawkNetplay.Core.Tests.Net48Compat;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The in-session control-message wire format. Round trips prove encode/decode symmetry; the
/// byte-level pins lock the exact big-endian layout so an accidental endianness or offset
/// change breaks a test instead of desyncing two builds of the tool against each other.
/// </summary>
public class ControlMessageCodecTests
{
    private static readonly SessionGeneration Gen = new(0x0102030405060708UL, 9);

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
        var body = ControlMessageCodec.EncodeResyncBegin(Gen, 4096, inputDelay: 3,
            SyncMode.Rollback, waitSeconds: 60, isSettingsChange: true);
        Assert.True(ControlMessageCodec.TryDecodeResyncBegin(body, out var generation,
            out int stateBytes, out int waitSeconds, out int inputDelay, out var mode,
            out bool isSettingsChange));
        Assert.Equal(Gen, generation);
        Assert.Equal(4096, stateBytes);
        Assert.Equal(60, waitSeconds);
        Assert.Equal(3, inputDelay);
        Assert.Equal(SyncMode.Rollback, mode);
        Assert.True(isSettingsChange);

        // A plain recovery says so, and says lockstep is lockstep.
        var recovery = ControlMessageCodec.EncodeResyncBegin(Gen, 8, 1, SyncMode.Lockstep);
        Assert.True(ControlMessageCodec.TryDecodeResyncBegin(recovery, out _, out _, out _,
            out int recoveryDelay, out var recoveryMode, out bool recoveryIsChange));
        Assert.Equal(1, recoveryDelay);
        Assert.Equal(SyncMode.Lockstep, recoveryMode);
        Assert.False(recoveryIsChange);

        // Encode refuses out-of-range fields outright…
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ControlMessageCodec.EncodeResyncBegin(Gen, -1, 1, SyncMode.Lockstep));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ControlMessageCodec.EncodeResyncBegin(Gen, ControlMessageCodec.MaxStateBytes + 1, 1, SyncMode.Lockstep));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ControlMessageCodec.EncodeResyncBegin(Gen, 0, 1, SyncMode.Lockstep,
                ControlMessageCodec.MaxResyncWaitSeconds + 1));
        // A delay of zero would build a driver that cannot exist; it must never reach the wire.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ControlMessageCodec.EncodeResyncBegin(Gen, 0, 0, SyncMode.Lockstep));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ControlMessageCodec.EncodeResyncBegin(Gen, 0, HandshakeCodec.MaxInputDelay + 1, SyncMode.Lockstep));

        // …and decode treats a hostile out-of-range frame as garbage, not a huge allocation cue.
        var bogus = ControlMessageCodec.EncodeResyncBegin(Gen, 4096, 2, SyncMode.Lockstep, 60);
        bogus[12] = 0x7F; // stateBytes ≈ int.MaxValue, far past MaxStateBytes
        Assert.False(ControlMessageCodec.TryDecodeResyncBegin(bogus, out _, out _, out _, out _, out _, out _));

        // A peer that invents a delay or a mode code cannot make us build a driver for it: this
        // frame decides what every peer rebuilds as, so an unrecognised value is a refusal.
        var badDelay = ControlMessageCodec.EncodeResyncBegin(Gen, 16, 2, SyncMode.Lockstep);
        WriteBigEndian(badDelay, 20, 0);
        Assert.False(ControlMessageCodec.TryDecodeResyncBegin(badDelay, out _, out _, out _, out _, out _, out _));
        var badMode = ControlMessageCodec.EncodeResyncBegin(Gen, 16, 2, SyncMode.Lockstep);
        WriteBigEndian(badMode, 24, 2);
        Assert.False(ControlMessageCodec.TryDecodeResyncBegin(badMode, out _, out _, out _, out _, out _, out _));
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
            ControlMessageCodec.EncodeStatePayload(Gen, []), out _, out var empty));
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
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x02 }, Slice(checksum, 12, 4)); // frame
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, Slice(checksum, 16, 4)); // hash
    }

    [Fact]
    public void MeshRtt_RoundTripsWithSubMillisecondResolution()
    {
        // A LAN mesh edge measures well under a millisecond. Rounding the report to whole
        // milliseconds would turn every local edge into either 0 or 1 and make the delay
        // decision blind precisely where it should be cheapest, so the wire carries microseconds.
        var body = ControlMessageCodec.EncodeMeshRtt(Gen, medianMs: 0.375, highMs: 1.5,
            measuredEdges: 2, totalEdges: 3);
        Assert.Equal(ControlMessageCodec.MeshRttSize, body.Length);

        Assert.True(ControlMessageCodec.TryDecodeMeshRtt(body, out var generation,
            out double medianMs, out double highMs, out int measured, out int total));
        Assert.Equal(Gen, generation);
        Assert.Equal(0.375, medianMs, 3);
        Assert.Equal(1.5, highMs, 3);
        Assert.Equal(2, measured);
        Assert.Equal(3, total);
    }

    [Fact]
    public void MeshRtt_RejectsShapesThatWouldCorruptADelayDecision()
    {
        var body = ControlMessageCodec.EncodeMeshRtt(Gen, 12.0, 20.0, 1, 1);

        Assert.False(ControlMessageCodec.TryDecodeMeshRtt(null!, out _, out _, out _, out _, out _));
        Assert.False(ControlMessageCodec.TryDecodeMeshRtt(new byte[27], out _, out _, out _, out _, out _));
        Assert.False(ControlMessageCodec.TryDecodeMeshRtt(new byte[29], out _, out _, out _, out _, out _));

        // Negative microseconds would decode as a negative RTT and could only lower the delay.
        var negative = (byte[])body.Clone();
        negative[12] = 0xFF;
        Assert.False(ControlMessageCodec.TryDecodeMeshRtt(negative, out _, out _, out _, out _, out _));

        // More edges measured than exist is a peer overstating its own coverage.
        var overCounted = ControlMessageCodec.EncodeMeshRtt(Gen, 1, 1, 1, 1);
        overCounted[23] = 5;
        Assert.False(ControlMessageCodec.TryDecodeMeshRtt(overCounted, out _, out _, out _, out _, out _));

        // A high-water below the median is incoherent; clamp rather than report negative jitter.
        var inverted = ControlMessageCodec.EncodeMeshRtt(Gen, 40.0, 40.0, 1, 1);
        WriteBigEndian(inverted, 16, 1000); // high = 1ms, median stays 40ms
        Assert.True(ControlMessageCodec.TryDecodeMeshRtt(inverted, out _, out double median,
            out double high, out _, out _));
        Assert.Equal(median, high, 3);
    }

    [Fact]
    public void InputDelay_RoundTripsAndRefusesOutOfRangeValues()
    {
        var body = ControlMessageCodec.EncodeInputDelay(Gen, 7);
        Assert.Equal(ControlMessageCodec.InputDelaySize, body.Length);
        Assert.True(ControlMessageCodec.TryDecodeInputDelay(body, out var generation, out int delay));
        Assert.Equal(Gen, generation);
        Assert.Equal(7, delay);

        Assert.Throws<ArgumentOutOfRangeException>(() => ControlMessageCodec.EncodeInputDelay(Gen, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ControlMessageCodec.EncodeInputDelay(Gen, HandshakeCodec.MaxInputDelay + 1));

        // A peer-supplied delay outside the wire bound is refused, not clamped: the host and the
        // joiner have to build their drivers on the SAME number, and a silently clamped one is a
        // different number on each end.
        var tooLarge = ControlMessageCodec.EncodeInputDelay(Gen, 1);
        WriteBigEndian(tooLarge, 12, HandshakeCodec.MaxInputDelay + 1);
        Assert.False(ControlMessageCodec.TryDecodeInputDelay(tooLarge, out _, out _));
        Assert.False(ControlMessageCodec.TryDecodeInputDelay(new byte[15], out _, out _));
    }

    private static void WriteBigEndian(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }
}
