using BizHawkNetplay.Core.Emu;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// KI-18: the handshake used to be told a flat <c>true</c> for determinism, whatever the core said.
///
/// These tests pin the shape of the replacement, and the shape is the interesting part: the flag
/// decides, and the exceptions are named and read rather than tolerated as a class.
///
/// The list grew once, in the way worth guarding against: A7800Hawk reports false because it forgot
/// to assign the property, and the gate answered a player with "turn off Use Real Time" for a core
/// that has no such setting and no clock — a refusal with no way out of it.
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
    [InlineData("A7800Hawk")]
    [InlineData("O2Hawk")]
    [InlineData("VectrexHawk")]
    [InlineData("GBHawkLink")]
    [InlineData("GBHawkLink3x")]
    [InlineData("GBHawkLink4x")]
    [InlineData("GGHawkLink")]
    public void TheNeverAssignedAutoPropertyCoresQualifyToo(string coreName)
    {
        // Each of these declares `DeterministicEmulation { get; set; }` and never assigns it, so the
        // flag is false by C# default while nothing reads it back. None contains a DateTime, an RTC
        // option, or a "Use Real Time" setting — so refusing them offered the player no way forward.
        Assert.True(DeterminismPolicy.Qualifies(false, coreName));
        Assert.Null(DeterminismPolicy.Refusal(false, coreName));
    }

    [Fact]
    public void GBHawkIsNotExemptBecauseItDoesNotNeedToBe()
    {
        // GBHawk sets the flag true in its constructor; it is the Link variants that drop that line.
        // If GBHawk ever reports false it means something changed, and that should be read, not
        // waved through on a family resemblance to the cores next to it.
        Assert.False(DeterminismPolicy.Qualifies(false, "GBHawk"));
    }

    [Theory]
    [InlineData("CPCHawk")]
    [InlineData("ZXHawk")]
    public void TheTwoCoresWithTheirOwnSettingAreNamedTheSettingTheyHave(string coreName)
    {
        // These two do derive the flag from sync settings — from one called "Deterministic
        // Emulation" that defaults to on. So they are still refused, but pointing them at "Use Real
        // Time" would send them hunting for a setting their core does not have.
        Assert.False(DeterminismPolicy.Qualifies(false, coreName));
        string refusal = DeterminismPolicy.Refusal(false, coreName)!;
        Assert.Contains("Deterministic Emulation", refusal);
        Assert.DoesNotContain("Use Real Time", refusal);
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

        // Same for the group added alongside it: the list is names, not a "Hawk" pattern, so a core
        // that merely looks like one of them still has to be read before it is trusted.
        Assert.False(DeterminismPolicy.Qualifies(false, "a7800hawk"));
        Assert.False(DeterminismPolicy.Qualifies(false, "A7800Hawk2"));
        Assert.False(DeterminismPolicy.Qualifies(false, "SubGBHawk"));
    }
}
