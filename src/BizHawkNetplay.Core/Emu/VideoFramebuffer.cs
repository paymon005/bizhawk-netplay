using System;

namespace BizHawkNetplay.Core.Emu;

/// <summary>
/// Works out which span of main memory the video hardware is scanning out, from the registers that
/// describe it.
///
/// <b>What this is for.</b> N64 desyncs at every checksum above native resolution, and it is not the
/// netcode: the video plugins resolve their framebuffer back into RDRAM, and above native those
/// bytes are produced by the GPU rather than by the emulated core. They differ between two machines
/// that are otherwise in perfect agreement, and they land inside the region the desync checksum
/// hashes. Skipping them is what lets a session run above native; see the adapter's
/// <c>HashMainMemory</c> for where that happens.
///
/// <b>Why the arithmetic lives here.</b> The adapter that reads these registers needs a live EmuHawk
/// and so cannot be tested at all. The register semantics are the part with edges worth pinning —
/// a fixed-point scale, a half-line field, alignment and two refusals — so they are a pure function
/// over plain integers, and the adapter is left with nothing but four reads and a call.
///
/// <b>Determinism.</b> Every input is a VI register, written by the game's own CPU code and carried
/// in the savestate. Two peers standing on the same state pass the same numbers in and get the same
/// span out, so nothing here needs negotiating between them.
/// </summary>
public static class VideoFramebuffer
{
    /// <summary>Vertical size past which these numbers are not describing a framebuffer any more.
    /// The tallest thing an N64 scans out is 480 lines; the margin is for interlaced modes.</summary>
    public const int MaxLines = 640;

    /// <summary>
    /// The span being scanned out, as a half-open byte range into main memory, or false when the
    /// registers do not describe one.
    /// </summary>
    /// <param name="status">VI_STATUS. Bits 1..0 select the pixel size: 2 is 16-bit, 3 is 32-bit,
    /// and 0 or 1 mean the video interface is blanked and nothing is being scanned out.</param>
    /// <param name="origin">VI_ORIGIN — where in RAM the picture starts. Masked into the domain
    /// rather than trusted, so a KSEG-flavoured address lands in the right place.</param>
    /// <param name="width">VI_WIDTH, in pixels per line.</param>
    /// <param name="vStart">VI_V_START: the active field as two 10-bit half-line marks, end in the
    /// low half and start in the high half.</param>
    /// <param name="yScale">VI_Y_SCALE, 2.10 fixed point in its low 12 bits.</param>
    /// <param name="mainMemorySize">Size of the domain the span must fall inside. Must be a power
    /// of two, which both N64 configurations (4MiB, or 8MiB with the expansion pak) are.</param>
    public static bool TryResolve(
        uint status, uint origin, uint width, uint vStart, uint yScale, long mainMemorySize,
        out long start, out long endExclusive)
    {
        start = 0;
        endExclusive = 0;
        if (mainMemorySize <= 0 || (mainMemorySize & (mainMemorySize - 1)) != 0) return false;

        int bytesPerPixel = (status & 3) switch { 2 => 2, 3 => 4, _ => 0 };
        if (bytesPerPixel == 0) return false;           // blanked: there is no picture in RAM

        long pixels = width & 0xFFF;
        if (pixels == 0) return false;

        long halfLines = (long)(vStart & 0x3FF) - ((vStart >> 16) & 0x3FF);
        long scale = yScale & 0xFFF;
        if (halfLines <= 0 || scale == 0) return false;
        long lines = halfLines * scale / 2048;          // half-lines to lines, and 2.10 to integer
        if (lines <= 0) return false;
        if (lines > MaxLines) lines = MaxLines;

        long from = (origin & (mainMemorySize - 1));
        long bytes = pixels * lines * bytesPerPixel;

        // Aligned outward to a word. N64 stores RDRAM with the bytes inside each aligned word
        // permuted (the core's `addr ^ 3`), so a word-aligned range covers the same bytes whichever
        // order a reader walks them in — which is what lets one span serve a memcpy of the raw
        // block and a walk through the domain's own byte-order alike.
        start = from & ~3L;
        endExclusive = Math.Min(mainMemorySize, (from + bytes + 3) & ~3L);
        if (endExclusive <= start) return false;

        // A register block that has not been programmed yet can name a span far larger than any
        // framebuffer. Refuse rather than blank most of the checksum: losing desync detection is
        // the one failure this whole mechanism must not be able to cause.
        if (endExclusive - start > mainMemorySize / 2) return false;
        return true;
    }
}
