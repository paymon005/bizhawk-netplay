using BizHawkNetplay.Core.Emu;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The span the desync checksum must skip, from the registers that describe it.
///
/// These pin the edges rather than the happy path, because the happy path is not where this can
/// hurt anyone: a span that comes out too small leaves GPU-produced bytes in the hash and N64 keeps
/// desyncing above native — the bug this exists to fix — while one that comes out too large blanks
/// real memory out of the checksum, which is desync detection quietly going away.
/// </summary>
public class VideoFramebufferTests
{
    private const long EightMiB = 8 * 1024 * 1024;

    /// <summary>320x240 at 16bpp from the low end of RAM: the setting every session runs today.</summary>
    private const uint Status16Bit = 2;
    private const uint Status32Bit = 3;
    // The active field as the VI expresses it: start and end in half-lines, high and low halves.
    private static uint VStart(int startHalfLine, int endHalfLine) =>
        ((uint)startHalfLine << 16) | (uint)endHalfLine;
    // 2.10 fixed point. 1024 is 1:1, so the line count is exactly half the half-line count.
    private const uint UnscaledY = 1024;

    [Fact]
    public void ResolvesTheSpanANativeResolutionFrameOccupies()
    {
        Assert.True(VideoFramebuffer.TryResolve(
            Status16Bit, origin: 0x00200000, width: 320, vStart: VStart(0, 480), yScale: UnscaledY,
            EightMiB, out long start, out long end));

        Assert.Equal(0x00200000, start);
        Assert.Equal(320 * 240 * 2, end - start);
    }

    [Fact]
    public void A32BitFramebufferIsTwiceTheBytes()
    {
        VideoFramebuffer.TryResolve(Status16Bit, 0x100000, 640, VStart(0, 480), UnscaledY,
            EightMiB, out long start16, out long end16);
        Assert.True(VideoFramebuffer.TryResolve(Status32Bit, 0x100000, 640, VStart(0, 480), UnscaledY,
            EightMiB, out long start32, out long end32));

        Assert.Equal(start16, start32);
        Assert.Equal(2 * (end16 - start16), end32 - start32);
    }

    [Fact]
    public void ABlankedVideoInterfaceScansOutNothing()
    {
        // Pixel size 0 and 1 both mean "no picture". Excluding a span here would remove real memory
        // from the checksum for no reason at all.
        foreach (uint blank in new uint[] { 0, 1 })
            Assert.False(VideoFramebuffer.TryResolve(
                blank, 0x00200000, 320, VStart(0, 480), UnscaledY, EightMiB, out _, out _));
    }

    [Fact]
    public void AVirtualOriginIsMaskedIntoRam()
    {
        // Games hand the VI a KSEG0 address as often as a physical one; the top bits are not part
        // of the offset and must not push the span off the end of the domain.
        Assert.True(VideoFramebuffer.TryResolve(
            Status16Bit, origin: 0x80200000, width: 320, vStart: VStart(0, 480), yScale: UnscaledY,
            EightMiB, out long start, out long end));

        Assert.Equal(0x00200000, start);
        Assert.True(end <= EightMiB);
    }

    [Fact]
    public void TheVerticalScaleShrinksTheSpan()
    {
        // Half scale means half the lines, which is half the bytes. Reading the fixed point wrongly
        // would leave the bottom of the picture in the hash.
        Assert.True(VideoFramebuffer.TryResolve(
            Status16Bit, 0x100000, 320, VStart(0, 480), yScale: UnscaledY / 2,
            EightMiB, out long start, out long end));

        Assert.Equal(320 * 120 * 2, end - start);
        Assert.Equal(0x100000, start);
    }

    [Fact]
    public void AnUnprogrammedRegisterBlockIsRefusedRatherThanBlankingTheChecksum()
    {
        // Width and scale at their maxima describe something no framebuffer ever was. The refusal
        // is the point: a span over half of RAM would take desync detection with it.
        Assert.False(VideoFramebuffer.TryResolve(
            Status32Bit, origin: 0, width: 0xFFF, vStart: VStart(0, 0x3FF), yScale: 0xFFF,
            EightMiB, out _, out _));
    }

    [Fact]
    public void DegenerateGeometryResolvesToNothing()
    {
        // An empty or inverted active field, a zero width, and a zero scale each mean the registers
        // are not describing a picture yet — which is the state at power-on, before the game has
        // programmed them.
        Assert.False(VideoFramebuffer.TryResolve(Status16Bit, 0x100000, 320, VStart(480, 480), UnscaledY,
            EightMiB, out _, out _));
        Assert.False(VideoFramebuffer.TryResolve(Status16Bit, 0x100000, 320, VStart(480, 0), UnscaledY,
            EightMiB, out _, out _));
        Assert.False(VideoFramebuffer.TryResolve(Status16Bit, 0x100000, 0, VStart(0, 480), UnscaledY,
            EightMiB, out _, out _));
        Assert.False(VideoFramebuffer.TryResolve(Status16Bit, 0x100000, 320, VStart(0, 480), 0,
            EightMiB, out _, out _));
    }

    [Fact]
    public void TheSpanIsWordAlignedAtBothEnds()
    {
        // The core stores RDRAM with the bytes inside each aligned word permuted, so only a
        // word-aligned range covers the same bytes whichever order a hash path walks them in.
        Assert.True(VideoFramebuffer.TryResolve(
            Status16Bit, origin: 0x100002, width: 321, vStart: VStart(0, 478), yScale: UnscaledY,
            EightMiB, out long start, out long end));

        Assert.Equal(0, start % 4);
        Assert.Equal(0, end % 4);
        Assert.True(start <= 0x100002, "the span must not begin after the picture does");
        Assert.True(end >= 0x100002 + 321 * 239 * 2, "the span must not end before the picture does");
    }

    [Fact]
    public void TheSpanNeverLeavesTheDomain()
    {
        // An origin near the top of RAM with a full-size picture behind it: the end is clamped
        // rather than allowed to index past the block being hashed.
        Assert.True(VideoFramebuffer.TryResolve(
            Status16Bit, origin: (uint)(EightMiB - 4096), width: 320, vStart: VStart(0, 480),
            yScale: UnscaledY, EightMiB, out long start, out long end));

        Assert.True(end <= EightMiB);
        Assert.True(start < end);
    }

    [Fact]
    public void ADomainWhoseSizeIsNotAPowerOfTwoIsRefused()
    {
        // The origin is masked into the domain, and a mask needs a power of two to mean anything.
        Assert.False(VideoFramebuffer.TryResolve(
            Status16Bit, 0x1000, 320, VStart(0, 480), UnscaledY, 3 * 1024 * 1024, out _, out _));
        Assert.False(VideoFramebuffer.TryResolve(
            Status16Bit, 0x1000, 320, VStart(0, 480), UnscaledY, 0, out _, out _));
    }

    [Fact]
    public void AnImplausiblyTallFieldIsCappedRatherThanTrusted()
    {
        // Capped at MaxLines, so a scale that would describe thousands of lines still produces a
        // span bounded by something a framebuffer could actually be.
        Assert.True(VideoFramebuffer.TryResolve(
            Status16Bit, origin: 0, width: 320, vStart: VStart(0, 0x3FF), yScale: 0xFFF,
            EightMiB, out long start, out long end));

        Assert.Equal(320 * VideoFramebuffer.MaxLines * 2, end - start);
        Assert.Equal(0, start);
    }
}
