using BizHawkNetplay.Core.Emu;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// KI-18: the handshake used to be told a flat <c>true</c> for determinism, whatever the core said.
///
/// These tests pin the shape of the replacement, and the shape is the interesting part: the flag
/// decides, and there is exactly one named exception rather than a general tolerance for false.
/// </summary>
public class DeterminismPolicyTests
{
    [Fact]
    public void ACoreThatReportsDeterministicQualifies()
    {
        Assert.True(DeterminismPolicy.Qualifies(true, "Gambatte"));
        Assert.True(DeterminismPolicy.Qualifies(true, "Ares64"));
        Assert.Null(DeterminismPolicy.Refusal(true, "Ares64"));
    }

    [Fact]
    public void MupenQualifiesEvenReportingFalse()
    {
        // Mupen64Plus declares `DeterministicEmulation => false` and reads it back nowhere in the
        // whole N64 core — verified against 2.11.1. Nothing is seeded from it, so it is inert.
        Assert.True(DeterminismPolicy.Qualifies(false, "Mupen64Plus"));
        Assert.Null(DeterminismPolicy.Refusal(false, "Mupen64Plus"));
    }

    [Theory]
    [InlineData("Ares64")]        // DeterministicEmulation = requested || !UseRealTime, then GetRtcTime
    [InlineData("BSNES")]         // snes_time() falls back to DateTime.Now
    [InlineData("melonDS")]
    [InlineData("MAME")]          // also registers a DIFFERENT set of memory domains when false
    [InlineData("SameBoy")]
    [InlineData("Libretro")]      // hardcodes false, but the core behind it is arbitrary
    public void EveryOtherCoreReportingFalseIsRefused(string coreName)
    {
        Assert.False(DeterminismPolicy.Qualifies(false, coreName));
        var refusal = DeterminismPolicy.Refusal(false, coreName);
        Assert.NotNull(refusal);
        Assert.Contains(coreName, refusal!);
        // The refusal has to name the remedy. "This core is not deterministic" is true and useless.
        Assert.Contains("Use Real Time", refusal!);
    }

    [Fact]
    public void AnUnknownCoreReportingFalseIsRefusedRatherThanAssumedFine()
    {
        Assert.False(DeterminismPolicy.Qualifies(false, "SomeCoreAddedNextYear"));
        Assert.False(DeterminismPolicy.Qualifies(false, null));
        Assert.NotNull(DeterminismPolicy.Refusal(false, null));
    }

    [Fact]
    public void TheExceptionIsMatchedExactlyRatherThanLoosely()
    {
        // A near-miss must not inherit the exception — the point of naming it is that it is narrow.
        Assert.False(DeterminismPolicy.Qualifies(false, "mupen64plus"));
        Assert.False(DeterminismPolicy.Qualifies(false, "Mupen64Plus (Next)"));
        Assert.True(DeterminismPolicy.IsInertWhenFalse(DeterminismPolicy.MupenCoreName));
    }
}
